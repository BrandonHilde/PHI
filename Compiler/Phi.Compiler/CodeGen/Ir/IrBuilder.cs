using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    /// <summary>
    /// A method's checked syntax tree as the intermediate form, or null with why not. It takes
    /// only what it knows exactly: whole numbers of every size, bln, chr and enums, in a
    /// method's parameters and variables and in a class's variables; if, while, break,
    /// continue and return; and calls of methods that take and give those. Anything else (text,
    /// arrays, objects, pointers, floating point, catching, the library's built-ins) and the
    /// method is the generator's, as it was. What's taken is grown one kind of thing at a time,
    /// each checked by the whole test suite and phi fuzz run through it (PHI_IR=1).
    /// </summary>
    public sealed partial class IrBuilder
    {
        /// <summary>Why a method can't be built: said by PHI_IR_TRACE, and the generator compiles it.</summary>
        sealed class NotYet : Exception
        {
            public NotYet(string why) : base(why) { }
        }

        readonly MethodSymbol method;
        readonly IrFunction fn;
        readonly Dictionary<VariableSymbol, VReg> variables = new();
        readonly List<(Block Continue, Block Break)> loops = new();
        Block current;

        /// <summary>Whether a class's variable may be kept in a register by a loop (the generator says: see EmitLoopHolding).</summary>
        readonly Func<VariableSymbol, bool> holdable;

        /// <summary>The method's first block (what's zeroed when it starts goes at its top).</summary>
        readonly Block entry;

        /// <summary>A method that gives str (a text, not an array of them): its caller says where it goes.</summary>
        static bool GivesText(MethodSymbol m) => m.ReturnType.IsString && !m.ReturnType.IsArray;

        /// <summary>The class variables the loop being built keeps in registers, each with its value.</summary>
        readonly Dictionary<VariableSymbol, VReg> held = new();

        /// <summary>The method's own objects (new T x), each where it's kept in the frame: never in a register.</summary>
        readonly Dictionary<VariableSymbol, string> owned = new();

        /// <summary>The program catches errors: a throw going past frees the method's own objects.</summary>
        readonly bool catches;

        // What's known of the method's own objects, from one instruction to the next in a block
        // (forgotten where blocks meet). Nothing but this method reaches its homes, so only what
        // it stores there changes them; an object's fields, anything that may write memory.

        /// <summary>What a home holds, when it was stored by this block (0 after takes or empty).</summary>
        readonly Dictionary<string, Opnd> homeHolds = new();

        /// <summary>Homes whose object was just made (renew zeroes it): its object fields are empty.</summary>
        readonly HashSet<string> freshObjects = new();

        /// <summary>Values read from a home, with which: the object that home still holds, until it's stored to.</summary>
        readonly Dictionary<VReg, string> readFrom = new();

        /// <summary>A program's method (exit is the program's end).</summary>
        readonly bool program;

        IrBuilder(MethodSymbol m, Func<VariableSymbol, bool> holdable, bool catches, bool growResults, bool program)
        {
            this.program = program;
            this.growResults = growResults;
            this.holdable = holdable;
            this.catches = catches;
            method = m;
            fn = new IrFunction { Name = m.Name };
            current = fn.NewBlock();
            entry = current;
            layout.Add(current);
            Need(m.ReturnType.Kind == TypeKind.Void || Supported(m.ReturnType) || GivesText(m), $"it gives {m.ReturnType}");
            Need(!m.ReturnType.IsObject || m.ReturnType.Struct != null && !m.ReturnType.Struct.IsInterface, "it gives an interface's object");
        }

        /// <summary>The method as the intermediate form, or null and why it isn't.</summary>
        public static IrFunction? Build(MethodSymbol m, Func<VariableSymbol, bool> holdable, bool catches, bool growResults, bool program, out string why)
        {
            why = "";
            try
            {
                var builder = new IrBuilder(m, holdable, catches, growResults, program);
                builder.BuildBody();
                return builder.fn;
            }
            catch (NotYet e)
            {
                why = e.Message;
                return null;
            }
        }

        /// <summary>PHI_VECTORIZE=0 leaves loops as they're written (to compare, or find a difference).</summary>
        static readonly bool VectorizeLoops = Environment.GetEnvironmentVariable("PHI_VECTORIZE") is not ("0" or "off");

        static void Need(bool ok, string why)
        {
            if (!ok) throw new NotYet(why);
        }

        /// <summary>The kinds of value this form keeps: a whole number of any size, bln, chr, an enum, a pointer.</summary>
        public static bool Supported(PhiType t) =>
            !t.IsArray && t.Kind is TypeKind.I8 or TypeKind.U8 or TypeKind.I16 or TypeKind.U16 or TypeKind.I32 or TypeKind.U32
                or TypeKind.I64 or TypeKind.U64 or TypeKind.Bool or TypeKind.Chr or TypeKind.Enum or TypeKind.Pointer or TypeKind.Object
                or TypeKind.F64 or TypeKind.F32 or TypeKind.Vec or TypeKind.Method;

        /// <summary>
        /// An f64 or f32, which this form keeps in an xmm register as an f64 (as the generator's
        /// x87 has an f32 while it works on it): one in memory is read as one and rounded as
        /// it's written, and an f32 variable is rounded as it's set. (f16 is the generator's.)
        /// </summary>
        static bool IsF(PhiType t) => !t.IsArray && t.Kind is TypeKind.F64 or TypeKind.F32;

        /// <summary>
        /// A value of t takes 64 bits: an i64, a u64, a pointer, or an object, which this form
        /// keeps only where it's borrowed (a struct parameter): its address, as a pointer is.
        /// </summary>
        /// <summary>
        /// A value of type t takes 64 bits: an i64 or u64; and in 64-bit code an address (a pointer,
        /// an object, a method value, a str's text), which is 32 bits in 32-bit code.
        /// </summary>
        public static bool Is64(PhiType t) => t.IsWide || PhiType.AddressSize == 8 && (t.IsPointer || t.IsObject || t.IsMethod || t.IsString);

        /// <summary>The type an index is made before it's added to an address: as wide as one (64 bits in 64-bit code), with its own sign.</summary>
        static PhiType IndexType(bool signed) => PhiType.AddressSize == 8 ? (signed ? PhiType.I64 : PhiType.U64) : (signed ? PhiType.I32 : PhiType.U32);

        /// <summary>A value that's an address: a pointer, an object, a method value, a str's text.</summary>
        static bool IsAddress(PhiType t) => t.IsPointer || t.IsObject || t.IsMethod || t.IsString;

        /// <summary>A value as wide as an address (for InReg): 64 bits in 64-bit code.</summary>
        static bool Addr => PhiType.AddressSize == 8;

        /// <summary>How an address is kept in memory: all 8 bytes of one in 64-bit code, 4 in 32-bit.</summary>
        static MemKind Ptr => PhiType.AddressSize == 8 ? MemKind.Q64 : MemKind.D32;

        /// <summary>How a value of type t is kept in memory.</summary>
        public static MemKind KindOf(PhiType t) => t.Kind switch
        {
            TypeKind.I8 => MemKind.S8,
            TypeKind.U8 or TypeKind.Bool or TypeKind.Chr => MemKind.U8,
            TypeKind.I16 => MemKind.S16,
            TypeKind.U16 => MemKind.U16,
            TypeKind.I64 or TypeKind.U64 => MemKind.Q64,
            TypeKind.Pointer or TypeKind.Object or TypeKind.Method => Ptr,
            TypeKind.F64 => MemKind.F64,
            TypeKind.F32 => MemKind.F32,
            TypeKind.Vec => MemKind.V128,
            _ => MemKind.D32,
        };

        /// <summary>
        /// The type e's value really has. p + n and p - n on a pointer are a pointer, all 64 bits
        /// of it, though the binder gives them the type of the number math they'd be on 32 bits
        /// (it leaves keeping the whole pointer to the generator, as here).
        /// </summary>
        static PhiType Real(Expr e) => PointerMath(e) ? PointerOf((BinaryExpr)e) : e.Type;

        /// <summary>p + n or p - n: a pointer moved (as the binder has it). p - q is a number.</summary>
        static bool PointerMath(Expr e) => e is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } b
            && (b.Left.Type.IsPointer || PointerMath(b.Left)) != (b.Right.Type.IsPointer || PointerMath(b.Right));

        static PhiType PointerOf(BinaryExpr b) => b.Left.Type.IsPointer || PointerMath(b.Left) ? Real(b.Left) : Real(b.Right);

        // ---------------------------------------------------------------- the method

        void BuildBody()
        {
            MethodSymbol m = method;
            Need(!m.IsInterruptHandler && !m.IsHook, "an event or interrupt handler");
            VariableSymbol? badParameter = m.Parameters.FirstOrDefault(p => !FrameText(p) && !CallersText(p) && !GrowingText(p) && !ArrayParameter(p) && !(Supported(p.Type) && !p.Grows && !p.TextRef && p.Ownership is Ownership.None or Ownership.Borrowed));
            Need(badParameter == null, $"a parameter it doesn't take ('{badParameter?.Name}', {Describe(badParameter)})");

            VariableSymbol? badLocal = m.Locals.FirstOrDefault(v => !(OwnObject(v) || FrameStruct(v) || FrameText(v) || GrowingText(v) || LocalView(v) || LocalTable(v)
                                   || Supported(v.Type) && !v.Type.IsObject && v.Ownership == Ownership.None && !v.Grows && !v.GrowingArray && !v.GrowingElements
                                   // (one that borrows an object: its address, nothing to give back)
                                   || v.Type.IsObject && !v.Type.IsArray && v.Ownership == Ownership.Borrowed && !v.Grows));
            Need(badLocal == null, $"a variable it doesn't take ('{badLocal?.Name}', {Describe(badLocal)})");

            // its own objects, each in a home of its own, empty to start with (as the generator's
            // frame starts zeroed), and freed where it ends
            foreach (VariableSymbol v in m.Locals.Where(OwnObject))
            {
                string home = fn.NewHome();
                owned[v] = home;
                fn.Owned.Add((home, v.Type.Struct!));
                StoreObject((home, null, 0), Opnd.Const(0));
            }
            PlaceGrowingTexts();
            PlaceLocalTables();
            foreach (VariableSymbol v in m.Locals.Where(LocalView))
            {
                fn.NewHome();
                fn.NewHome();
                string home = $"rbp - {8 * fn.Homes}";
                localViews[v] = home;
                // (its elements freed as a text is, Heap.Free of what's at its start: after its
                // objects or texts, when it has some)
                if (OwnsElements(v)) fn.OwnedViews.Add((home, v.Type.TextTable ? null : v.Type.Element.Struct));
                else fn.OwnedTexts.Add(home);
                Emit(new ZeroI { Place = home, Size = 16 });
            }
            addressed = BodyNodes().Select(n => n switch
                {
                    AddrExpr { OfObject: false, CodeLabel: null, Operand: NameExpr { Symbol: VariableSymbol v } } => v,
                    AtomicStmt { Target: NameExpr { Symbol: VariableSymbol v } } => v,     // (an atomic step is on memory: its own slot)
                    AtomicExpr { Target: NameExpr { Symbol: VariableSymbol v } } => v,
                    _ => null,
                })
                .Where(v => v != null && Ours(v) && Supported(v.Type) && !v.Type.IsVec && !owned.ContainsKey(v) && !frameStructs.ContainsKey(v)
                            && !FrameText(v) && !GrowingText(v) && !CallersText(v) && !ArrayParameter(v))
                .Select(v => v!).ToHashSet();
            if (catches && (owned.Count > 0 || fn.OwnedTexts.Count > 0 || fn.OwnedViews.Count > 0 || fn.OwnedTableTexts.Count > 0 || m.Locals.Any(OwnObjectArray)
                            || growResults && BodyNodes().Any(n => n is CallExpr { Resolved: MethodSymbol cm } && GivesText(cm) || n is CallStmt { Resolved: MethodSymbol sm } && GivesText(sm))
                            || BodyNodes().Any(n => n is CallExpr { Resolved: PointerCall { Signature.Result: { IsString: true, IsArray: false } } }
                                                    || n is CallStmt { Resolved: PointerCall { Signature.Result: { IsString: true, IsArray: false } } })))
            {
                // the record a throw looks at, three homes: the lowest is where it starts
                fn.NewHome();
                fn.NewHome();
                fn.NewHome();
                fn.UnwindRecord = 8 * fn.Homes;
                Emit(new UnwindI { On = true });
            }

            PlaceFrameTexts();

            // a struct of the method's own (an err, say), in homes of its own
            foreach (VariableSymbol v in m.Locals.Where(FrameStruct))
            {
                int size = FrameBytes(v);
                for (int k = 0; k < (size + 7) / 8; k++) fn.NewHome();
                frameStructs[v] = 8 * fn.Homes;
                if (OwnObjectArray(v))
                {
                    // empty to start with; each element's object freed where the method ends
                    Emit(new ZeroI { Place = FramePlace(v, 0), Size = (size + 7) / 8 * 8 });
                    for (int k = 0; k < v.Type.Length; k++) fn.Owned.Add((FramePlace(v, PhiType.AddressSize * k), v.Type.Element.Struct!));
                }
            }

            // the parameters, from their places in the frame: the last at rbp + 16, each 8 bytes
            // (a vec's 16)
            // (a str result: where it goes is nearest; each argument a word, as the generator pushes them)
            int word = PhiType.AddressSize;
            int above = 2 * word + (GivesText(m) ? word : 0);
            for (int i = m.Parameters.Count - 1; i >= 0; i--)
            {
                VariableSymbol p = m.Parameters[i];
                if (growingTexts.ContainsKey(p))
                {
                    // a str parameter without a size: the text it's given, copied into its own slot
                    VReg given = fn.NewAddress();
                    Emit(new LoadI { Dst = given, Place = $"rbp + {above}", Kind = Ptr });
                    VReg slot = fn.NewAddress();
                    Emit(new AddrI { Dst = slot, Label = growingTexts[p] });
                    Emit(new GrowStoreI { Slot = slot, Value = given, SourceIsString = true });
                    above += word;
                    continue;
                }
                if (ArrayParameter(p))
                {
                    // an array: a view of it where the caller pushed it (its elements, how many, the most)
                    paramViews[p] = $"rbp + {above}";
                    above += word == 8 ? 16 : 12;     // (its elements' address, how many, the most)
                    continue;
                }
                if (CallersText(p))
                {
                    VReg slot = fn.NewAddress(p.Name);
                    Emit(new LoadI { Dst = slot, Place = $"rbp + {above}", Kind = Ptr });
                    callersTexts[p] = slot;
                    above += word;
                    continue;
                }
                if (frameTexts.ContainsKey(p))
                {
                    // a str parameter: the text it's given, copied into its own buffer (too long an error)
                    VReg given = fn.NewAddress();
                    Emit(new LoadI { Dst = given, Place = $"rbp + {above}", Kind = Ptr });
                    Emit(new TextI { Op = TextOp.Copy, A = given, Place = TextPlace(p), Room = p.Capacity });
                    above += word;
                    continue;
                }
                VReg v = Variable(p);
                Emit(new LoadI { Dst = v, Place = $"rbp + {above}", Kind = KindOf(p.Type) });
                // (a vec takes 16; in 32-bit code an i64 or f64 takes 8, the rest 4)
                above += p.Type.IsVec ? 16 : word == 8 ? 8 : p.Type.IsWide || p.Type.Kind == TypeKind.F64 ? 8 : 4;
            }
            // (a variable is set where it's declared; one that could be read before that is
            // given 0 at the start, by IrPeephole, which knows which)
            foreach (VariableSymbol v in m.Locals.Where(v => !owned.ContainsKey(v) && !frameStructs.ContainsKey(v) && !frameTexts.ContainsKey(v) && !growingTexts.ContainsKey(v) && !localViews.ContainsKey(v) && !localTables.ContainsKey(v))) Variable(v);

            Statements(m.Decl.Body);
            if (m.Decl.Result is Expr value)
            {
                Mark(value);
                Return(value);
            }
            else
            {
                Need(m.ReturnType.Kind == TypeKind.Void, "a method that can end without its result");
                if (current.End == null)
                {
                    TidyUp();
                    current.End = new RetT();
                }
            }
            // laid out in the order they were written, which keeps a loop's body together
            var order = layout.Concat(fn.Blocks.Where(b => !layout.Contains(b))).ToList();
            fn.Blocks.Clear();
            fn.Blocks.AddRange(order);
            RemoveUnreachable();
        }

        /// <summary>What a variable is, for saying why it isn't taken.</summary>
        static string Describe(VariableSymbol? v) => v == null ? "" :
            v.Grows ? "a str that grows" : v.GrowingArray ? "an array that grows" : v.Type.IsArray ? $"an array of {v.Type.Element}" : A(v.Type);

        /// <summary>Blocks no jump reaches (after a return or a break) are left out.</summary>
        void RemoveUnreachable()
        {
            var seen = new HashSet<Block>();
            var work = new Stack<Block>();
            work.Push(fn.Blocks[0]);
            while (work.Count > 0)
            {
                Block b = work.Pop();
                if (!seen.Add(b)) continue;
                foreach (Block next in Successors(b)) work.Push(next);
            }
            fn.Blocks.RemoveAll(b => !seen.Contains(b));
        }

        public static IEnumerable<Block> Successors(Block b) => b.End switch
        {
            JumpT j => new[] { j.Target },
            BranchT br => new[] { br.Then, br.Else },
            SwitchT sw => sw.Cases.Select(c => c.Target).Append(sw.Default).Distinct().ToArray(),
            TryT t => new[] { t.Body, t.Caught },
            _ => Array.Empty<Block>(),
        };

        /// <summary>A new value for one of type t: an f64's, or a whole number's of its width.</summary>
        VReg New(PhiType t, string name = "") => t.IsVec ? fn.NewVec(name) : IsF(t) ? fn.NewFloat(name) : fn.NewValue(Is64(t), name);

        /// <summary>A struct the method keeps in its frame (Error err, say): how far below rbp it starts.</summary>
        readonly Dictionary<VariableSymbol, int> frameStructs = new();

        /// <summary>A struct variable of the method's own, with no objects in it: kept in the frame, its fields read and written there.</summary>
        static bool FrameStruct(VariableSymbol v) =>
            !v.IsParameter && v.Ownership == Ownership.None && !v.Grows && !v.GrowingArray && !v.GrowingElements
            && (v.Type.IsStruct && !v.Type.IsArray && v.Type.Struct!.Size > 0 && !v.Type.Struct.OwnsObjects || FixedArray(v.Type))
            || OwnObjectArray(v);

        /// <summary>
        /// A method's own array of objects of a size (new Node list[3]), not an interface's: kept in
        /// its frame, each element's object its own (freed at the end and by a throw going past).
        /// </summary>
        static bool OwnObjectArray(VariableSymbol v) =>
            !v.IsParameter && v.Ownership == Ownership.Owned && v.Type.IsArray && !v.Type.Open && !v.GrowingArray && v.Type.Length is > 0 and <= 64
            && v.Type.Element.IsObject && v.Type.Element.Struct != null;     // (an interface's too: its routines go to each object's own)

        /// <summary>An array of a size, of numbers (or vecs), kept where it is: what this form reads and writes an element of in place.</summary>
        static bool FixedArray(PhiType t) =>
            t.IsArray && !t.Open && !t.TextTable && t.Length > 0
            && (t.Kind != TypeKind.Str && t.Element.IsObject && t.Element.Struct != null     // (objects: their addresses; kept by the object statements)
                || t.Kind != TypeKind.Str && Supported(t.Element) && !t.Element.IsObject && t.Element.ElementSize is 1 or 2 or 4 or 8 or 16
                || t.Kind != TypeKind.Str && InPlaceStruct(t.Element) && !t.Element.Struct!.OwnsObjects
                || t.Element.IsInlineText && t.Element.Room > 0);     // (texts kept in place, each its room's bytes)     // (an [atmost N] one too: its count after them)

        /// <summary>The bytes a variable kept in the frame takes.</summary>
        static int FrameBytes(VariableSymbol v) => v.Type.IsArray ? v.Type.ByteSize : v.Type.Struct!.Size;

        /// <summary>Where a field of a struct in the frame is, as a place an instruction names.</summary>
        string FramePlace(VariableSymbol v, int offset) => $"rbp - {frameStructs[v] - offset}";

        /// <summary>A method's own object, new T x: kept in a home of the frame (see <see cref="owned"/>).</summary>
        static bool OwnObject(VariableSymbol v) =>
            v.Ownership == Ownership.Owned && v.Type.IsObject && !v.Type.IsArray && v.Type.Struct != null && !v.IsParameter;

        /// <summary>
        /// Before each return: off the list a throw looks at, and the method's own objects freed
        /// (the generator's EmitTidyUp: the result is worked out already, and stays).
        /// </summary>
        void TidyUp()
        {
            if (fn.UnwindRecord != 0) Emit(new UnwindI { On = false });
            foreach (VariableSymbol v in localViews.Keys.Where(OwnsElements)) FreeView(v);
            foreach (var (slot, room) in fn.OwnedTableTexts)
            {
                // an element that moved to the heap (one still in its own buffer stays)
                VReg roomWord = fn.NewValue(false);
                Emit(new LoadI { Dst = roomWord, Place = room, Kind = MemKind.D32 });
                Block free = fn.NewBlock(), kept = fn.NewBlock();
                if (current.End != null) Enter(fn.NewBlock());
                current.End = new BranchT { Cond = Cond.Eq, A = roomWord, B = Opnd.Const(0), TestMask = 0x80000000, Then = free, Else = kept };
                Enter(free);
                Emit(new FreeTextI { Place = slot });
                current.End = new JumpT { Target = kept };
                Enter(kept);
            }
            foreach (string slot in fn.OwnedTexts) Emit(new FreeTextI { Place = slot });
            foreach (var (home, s) in fn.Owned)
            {
                VReg o = fn.NewAddress();
                Emit(new LoadI { Dst = o, Place = home, Kind = Ptr });
                Emit(new ObjCallI { Kind = "free", Struct = s, Arg = o });
            }
        }

        /// <summary>Where an object is kept: a method's own one's home, or a field's base and offset.</summary>
        (string? Home, VReg? Base, long Offset) ObjectPlace(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol v } && owned.TryGetValue(v, out string? home)) return (home, null, 0);
            // a class's object, or an element of an array of them: by its address, read each time
            // (a call may change a class's: it isn't a home only this method reaches)
            if (e is NameExpr { Symbol: VariableSymbol g } && !Ours(g) && g.Type.IsObject && !g.Type.IsArray && Global(g) is string label)
            {
                Need(!held.ContainsKey(g), "an object of a class's kept while a loop holds it");
                VReg at = fn.NewAddress();
                Emit(new AddrI { Dst = at, Label = label });
                return (null, at, 0);
            }
            if (e is IndexExpr { Type.IsObject: true } element && CanAddress(element)) return (null, AddressOf(element), 0);
            var (b, offset, t) = Field(e);
            Need(b != null && t.IsObject, $"an object kept in a {e.GetType().Name} it doesn't take");
            return (null, b, offset);
        }

        /// <summary>Where an object place is: its home's address, or its base and offset added.</summary>
        VReg PlaceAddress((string? Home, VReg? Base, long Offset) place)
        {
            VReg d = fn.NewAddress();
            if (place.Home != null) Emit(new AddrI { Dst = d, Label = place.Home });
            else Emit(new BinI { Op = BinOp.Add, Dst = d, A = place.Base!, B = Small(Opnd.Const(place.Offset), true) });
            return d;
        }

        /// <summary>The object kept there: what's known of it (empty, mostly), or read.</summary>
        Opnd LoadObject((string? Home, VReg? Base, long Offset) place)
        {
            if (place.Home != null && homeHolds.TryGetValue(place.Home, out Opnd known)) return known;
            // (a field of an object just made is empty)
            if (place.Base != null && readFrom.TryGetValue(place.Base, out string? of) && freshObjects.Contains(of)) return Opnd.Const(0);
            VReg d = fn.NewAddress();
            if (place.Home != null)
            {
                Emit(new LoadI { Dst = d, Place = place.Home, Kind = Ptr });
                readFrom[d] = place.Home;
            }
            else Emit(new LoadIdxI { Dst = d, Base = place.Base!, Index = Opnd.Const(place.Offset), Scale = 1, Kind = Ptr });
            return d;
        }

        void StoreObject((string? Home, VReg? Base, long Offset) place, Opnd value)
        {
            if (place.Home != null)
            {
                Emit(new StoreI { Place = place.Home, Src = value, Kind = Ptr });
                // another object there now: what was known of the one before goes
                foreach (VReg was in readFrom.Where(r => r.Value == place.Home).Select(r => r.Key).ToList()) readFrom.Remove(was);
                freshObjects.Remove(place.Home);
                homeHolds[place.Home] = value;
                return;
            }
            Emit(new StoreIdxI { Base = place.Base!, Index = Opnd.Const(place.Offset), Scale = 1, Src = value, Kind = Ptr });
            // (one of its object fields isn't empty now)
            if (readFrom.TryGetValue(place.Base!, out string? of)) freshObjects.Remove(of);
        }

        /// <summary>What an instruction makes no longer known (see homeHolds).</summary>
        void Forget(Instr i)
        {
            switch (i)
            {
                case ObjCallI { Kind: "renew", Home: string made }:
                    // the object there is new, or the old one emptied: all of it zero
                    freshObjects.Clear();
                    homeHolds.Remove(made);
                    foreach (VReg was in readFrom.Where(r => r.Value == made).Select(r => r.Key).ToList()) readFrom.Remove(was);
                    freshObjects.Add(made);
                    break;
                case ObjCallI { Kind: "set" or "assign" or "clone" }:
                    // (what any home holds, or any object's fields, may be other now)
                    freshObjects.Clear();
                    homeHolds.Clear();
                    readFrom.Clear();
                    break;
                case CallI or ObjCallI or RawCallI or LockI or UnlockI:
                    // (a method may write any object's fields; none reaches the homes)
                    freshObjects.Clear();
                    break;
                case StoreIdxI st when !readFrom.ContainsKey(st.Base):
                    // through a pointer that might be to one of them
                    freshObjects.Clear();
                    break;
            }
        }

        VReg Variable(VariableSymbol v)
        {
            // (a method's own object is read from its home each time: a call may have changed it)
            if (owned.TryGetValue(v, out string? home))
            {
                VReg loaded = fn.NewAddress(v.Name);
                Emit(new LoadI { Dst = loaded, Place = home, Kind = Ptr });
                readFrom[loaded] = home;
                return loaded;
            }
            if (!variables.TryGetValue(v, out VReg? r))
            {
                Need(Supported(v.Type), $"'{v.Name}', {A(v.Type)}");
                r = New(v.Type, v.Name);
                r.Pinned = addressed.Contains(v);
                variables[v] = r;
            }
            return r;
        }

        /// <summary>The method's own variables whose address it takes (addr n): pinned, each in its own slot.</summary>
        HashSet<VariableSymbol> addressed = new();

        void Emit(Instr i)
        {
            // (code after a return or a break, which nothing reaches, goes in a block of its own)
            if (current.End != null) Enter(fn.NewBlock());
            current.Instrs.Add(i);
            Forget(i);
        }

        void Mark(Node n) => Emit(new LineMark { Node = n });

        void StartBlock(Block b)
        {
            if (current.End == null) current.End = new JumpT { Target = b };
            Enter(b);
        }

        /// <summary>The order blocks are first written in: the source's, which is how they're laid out.</summary>
        readonly List<Block> layout = new();

        void Enter(Block b)
        {
            // (where blocks meet, nothing is known of what came before)
            homeHolds.Clear();
            freshObjects.Clear();
            readFrom.Clear();
            current = b;
            if (!layout.Contains(b)) layout.Add(b);
        }

        // ---------------------------------------------------------------- statements

        void Statements(List<Stmt> body)
        {
            foreach (Stmt s in body) Statement(s);
        }

        /// <summary>The locked blocks the statement being built is in: each lock, what catches errors outside it, and how many loops there were.</summary>
        readonly List<(string Label, VReg Saved, int Loops)> locks = new();

        /// <summary>
        /// The locks taken since there were this many loops given back, innermost first: before
        /// a break or continue (in the loop it's about), or a return (all of them).
        /// </summary>
        void Unlock(int loopsBefore)
        {
            for (int k = locks.Count - 1; k >= 0 && locks[k].Loops >= loopsBefore; k--)
                Emit(new UnlockI { Label = locks[k].Label, Saved = locks[k].Saved });
        }

        /// <summary>True while a statement that needs the held class variables in memory is built: they're there.</summary>
        bool heldInMemory;

        void Statement(Stmt s)
        {
            // a call, a return or a division that may stop, in a loop holding class variables in
            // registers: they're written back first and read again after (the generator's fence)
            if (held.Count > 0 && !heldInMemory && X86Generator.NeedsMemory(s))
            {
                foreach (var (g, v) in held) Emit(new StoreI { Place = Global(g)!, Src = v, Kind = KindOf(g.Type) });
                heldInMemory = true;
                try { Statement(s); }
                finally { heldInMemory = false; }
                if (s is not ReturnStmt)
                    foreach (var (g, v) in held) Emit(new LoadI { Dst = v, Place = Global(g)!, Kind = KindOf(g.Type) });
                return;
            }
            switch (s)
            {
                case VarDecl { IsNew: true, Symbol: VariableSymbol made } d when owned.ContainsKey(made):
                {
                    Need(d.Narrows == null && d.AtAddress == null && d.BufferSize == null && d.MoreSizes.Count == 0, "a new object it doesn't take");
                    if (d.Source != null)
                    {
                        // new T x: empty, or from another object
                        Statement(d.Source);
                        return;
                    }
                    // a zeroed object (the first time; after that the old one emptied and used
                    // again), then its fields from the values, in order
                    Mark(d);
                    Emit(new ObjectLineI { At = d });
                    Emit(new ObjCallI { Kind = "renew", Struct = made.Type.Struct!, Home = owned[made] });
                    Statements(d.FieldValues);
                    return;
                }

                case ObjectAssignStmt { Kind: ObjectAssignKind.Copy or ObjectAssignKind.Move } given:
                {
                    // the generator's EmitObjectAssign: a copy of the value's object (set into an owner's
                    // place, assign into a borrowed one), or a call's new object taken over
                    Mark(given);
                    Emit(new ObjectLineI { At = given });
                    VReg source = given.Kind == ObjectAssignKind.Copy ? ObjectAddress(given.Value) : InReg(Value(given.Value), Addr);
                    TakeObject(given.Target, given.Struct, source, given.Kind == ObjectAssignKind.Move);
                    return;
                }

                case ObjectAssignStmt { Kind: ObjectAssignKind.Clear } clear:
                {
                    // x is empty: what it had is freed
                    Mark(clear);
                    var place = ObjectPlace(clear.Target);
                    Opnd had = LoadObject(place);
                    StoreObject(place, Opnd.Const(0));
                    if (!(had.IsImm && had.Imm == 0)) Emit(new ObjCallI { Kind = "free", Struct = clear.Struct, Arg = InReg(had, Addr) });
                    return;
                }

                case TakesStmt t:
                {
                    // the generator's EmitTakes: the source's place, then the target's; the object
                    // moves, the source is left empty, and what the target had is freed (unless it
                    // was nothing, or the same object)
                    Need(t.Struct != null, "a takes it doesn't take");
                    Mark(t);
                    var from = ObjectPlace(t.Source);
                    if (t.Inside != null)
                    {
                        // nodes:a.left takes nodes:b; when a is b, it would own itself: an error then
                        VReg source = PlaceAddress(from), inside = PlaceAddress(ObjectPlace(t.Inside));
                        Block stop = fn.NewBlock(), apart = fn.NewBlock();
                        if (current.End != null) Enter(fn.NewBlock());
                        current.End = new BranchT { Cond = Cond.Eq, A = inside, B = source, Then = stop, Else = apart };
                        Enter(stop);
                        Emit(new StopI { Text = "an object can't take the object it's inside of (it would own itself)", Kind = X86Generator.ErrorHierarchyKind, At = t, Held = HeldStores() });
                        current.End = new RetT();
                        Enter(apart);
                    }
                    var to = ObjectPlace(t.Target);
                    Opnd moved = LoadObject(from);
                    StoreObject(from, Opnd.Const(0));
                    Opnd had = LoadObject(to);
                    StoreObject(to, moved);
                    // (a target known to be empty, as a list's new node's next one is, frees nothing)
                    if (had.IsImm && had.Imm == 0) return;
                    VReg hadReg = InReg(had, Addr);
                    Block check = fn.NewBlock(), free = fn.NewBlock(), after = fn.NewBlock();
                    current.End = new BranchT { A = hadReg, B = Opnd.Const(0), Cond = Cond.Eq, Then = after, Else = check };
                    Enter(check);
                    current.End = new BranchT { A = hadReg, B = moved, Cond = Cond.Eq, Then = after, Else = free };
                    Enter(free);
                    Emit(new ObjCallI { Kind = "free", Struct = t.Struct!, Arg = hadReg });
                    StartBlock(after);
                    return;
                }

                case VarDecl { Symbol: VariableSymbol tabled } tdecl when localTables.ContainsKey(tabled):
                {
                    // its values put in, one an element (as list:i is v); none: each text empty, where its slot says
                    Mark(tdecl);
                    var tableName = new NameExpr { Name = tabled.Name, Symbol = tabled, Type = tabled.Type };
                    if (tdecl.Values.Count == 0)
                    {
                        for (int k = 0; k < tabled.Type.Length; k++)
                        {
                            VReg text = fn.NewAddress();
                            Emit(new LoadI { Dst = text, Place = $"{localTables[tabled]} + {PhiType.AddressSize * k}", Kind = Ptr });
                            Emit(new StoreIdxI { Base = text, Index = Opnd.Const(0), Scale = 1, Src = Opnd.Const(0), Kind = MemKind.U8 });
                        }
                        return;
                    }
                    for (int k = 0; k < tdecl.Values.Count; k++)
                    {
                        var element = new IndexExpr { Target = tableName, Index = new NumberExpr { Value = k, Type = PhiType.Int }, Type = tabled.Type.Element, Span = tdecl.Span };
                        Need(SetText(element, tdecl.Values[k]), "a str table given what isn't text");
                    }
                    return;
                }

                case VarDecl { Symbol: VariableSymbol viewed } vd when localViews.TryGetValue(viewed, out string? viewHome):
                    // empty again each time it runs: its elements let go, its view zero
                    Need(vd.Values.Count == 0, "an array that grows given values where it's declared");
                    Mark(vd);
                    if (OwnsElements(viewed)) FreeView(viewed);
                    else Emit(new FreeTextI { Place = viewHome });
                    Emit(new ZeroI { Place = viewHome, Size = 16 });
                    return;

                case VarDecl { Symbol: VariableSymbol grown } gd when growingTexts.ContainsKey(grown):
                {
                    // its value (or no text): each time the declaration runs; the room it has stays
                    Need(gd.Values.Count <= 1 && gd.TextSize == null, "a str given more than one value where it's declared");
                    Mark(gd);
                    PutText(new NameExpr { Name = grown.Name, Symbol = grown, Type = grown.Type },
                            gd.Values.Count == 1 ? gd.Values[0] : new StringExpr { Value = "", Type = PhiType.Str, Span = gd.Span }, append: false);
                    return;
                }

                case VarDecl { Symbol: VariableSymbol text } td when frameTexts.ContainsKey(text):
                {
                    Need(td.Values.Count <= 1 && td.TextSize == null, "a str given more than one value where it's declared");
                    Mark(td);
                    if (td.Values.Count == 1)
                    {
                        Need(SetText(new NameExpr { Name = text.Name, Symbol = text, Type = text.Type }, td.Values[0]), "a str given what isn't text");
                        return;
                    }
                    // (empty: its first byte the zero that ends it)
                    Emit(new StoreI { Place = TextPlace(text), Src = Opnd.Const(0), Kind = MemKind.U8 });
                    return;
                }

                case VarDecl { Symbol: VariableSymbol objects } od when frameStructs.ContainsKey(objects) && OwnObjectArray(objects):
                {
                    // new T list[n]: a zeroed object in each (renew: one already there is made zero);
                    // an [atmost n] one: what it held let go, and none in it (the generator's EmitNewArray)
                    Need(od.Values.Count == 0 && od.FieldValues.Count == 0 && od.Source == null, "an array of objects given values where it's declared");
                    Mark(od);
                    Emit(new ObjectLineI { At = od });
                    var list = new NameExpr { Name = objects.Name, Symbol = objects, Type = objects.Type };
                    if (objects.Type.Filled)
                    {
                        VReg none = fn.NewValue(false);
                        Emit(new ConstI { Dst = none, Value = 0 });
                        FreeFrom(list, none);
                        Emit(new StoreI { Place = FramePlace(objects, objects.Type.CountOffset), Src = Opnd.Const(0), Kind = MemKind.D32 });
                        return;
                    }
                    for (int k = 0; k < objects.Type.Length; k++)
                    {
                        VReg slot = fn.NewAddress();
                        Emit(new AddrI { Dst = slot, Label = FramePlace(objects, PhiType.AddressSize * k) });
                        Emit(new ObjCallI { Kind = "renew", Struct = objects.Type.Element.Struct!, Arg = slot });
                    }
                    return;
                }

                case VarDecl { Symbol: VariableSymbol framed } fd when frameStructs.ContainsKey(framed):
                {
                    Need(fd.FieldValues.Count == 0 && fd.Source == null
                         && (fd.Values.Count == 0 || framed.Type.IsArray && !framed.Type.Filled && Supported(framed.Type.Element) && fd.Values.Count <= framed.Type.Length),
                         "a struct of a method's own given its values where it's declared");
                    Mark(fd);
                    // zeros, then an array's values in order (the values first, as they're worked out)
                    PhiType element = framed.Type.Element;
                    var values = fd.Values.Select(x => Small(Value(x, element), Is64(element))).ToList();
                    Emit(new ZeroI { Place = FramePlace(framed, 0), Size = FrameBytes(framed) });
                    for (int k = 0; k < values.Count; k++)
                        Emit(new StoreI { Place = FramePlace(framed, k * element.ElementSize), Src = values[k], Kind = KindOf(element) });
                    return;
                }

                case VarDecl { Symbol: VariableSymbol classVar } cd when !Ours(classVar) && Global(classVar) is string kept:
                {
                    // (a class's own statement, made a method: its variable is the class's, set where it's kept)
                    Need(cd.Values.Count <= 1 && !cd.IsNew && cd.AtAddress == null && cd.EachOf == null, "a declaration it doesn't take");
                    Mark(cd);
                    Opnd value = cd.Values.Count == 1 ? Value(cd.Values[0], classVar.Type)
                        : classVar.Type.IsVec ? ZeroVec() : IsF(classVar.Type) ? FloatConst(0.0) : Opnd.Const(0);
                    Emit(new StoreI { Place = kept, Src = Small(value, Is64(classVar.Type)), Kind = KindOf(classVar.Type) });
                    return;
                }

                case VarDecl d:
                {
                    Need(d.Symbol is VariableSymbol && d.Values.Count <= 1 && !d.IsNew && d.AtAddress == null && d.BufferSize == null
                         && d.MoreSizes.Count == 0 && !d.Unsized && d.TextSize == null && (d.EachOf == null || d.Values.Count == 0) && d.Narrows == null,
                         "a declaration it doesn't take");
                    VariableSymbol v = (VariableSymbol)d.Symbol!;
                    Mark(d);
                    Assign(v, d.Values.Count == 1 ? d.Values[0] : null);
                    return;
                }

                case AssignStmt { Op: AssignOp.Set, Values: null, Target.Type.IsString: true } ta when !ta.Target.Type.IsArray:
                    Mark(ta);
                    Need(SetText(ta.Target, ta.Value), $"text put in {Unknown(ta.Target)}");
                    return;

                case AssignStmt a:
                {
                    Need(a.Op == AssignOp.Set && a.Values == null && !a.Target.Type.IsObject, "an assignment it doesn't take");
                    Mark(a);
                    switch (a.Target)
                    {
                        case NameExpr { Symbol: VariableSymbol v } when variables.ContainsKey(v) || method.Locals.Contains(v) || method.Parameters.Contains(v):
                            Assign(v, a.Value);
                            return;
                        case IndexExpr { Target.Type.IsVec: true } lane:
                            SetLane(lane, a.Value);
                            return;
                        case NameExpr { Symbol: VariableSymbol kept } when !heldInMemory && held.TryGetValue(kept, out VReg? inRegister):
                        {
                            Opnd v = Value(a.Value, kept.Type);
                            if (v.IsImm) Emit(new ConstI { Dst = inRegister, Value = v.Imm });
                            else Emit(new CopyI { Dst = inRegister, Src = v });
                            return;
                        }
                        case NameExpr { Symbol: VariableSymbol g } when Global(g) is string place:
                            Emit(new StoreI { Place = place, Src = Value(a.Value, g.Type), Kind = KindOf(g.Type) });
                            return;
                        case Expr field when IsField(field):
                        {
                            // (the value first, then where it goes: the field's pointer is read after it)
                            Opnd value = Value(a.Value, field.Type);
                            var (fb, fo, ft) = Field(field);
                            Emit(new StoreIdxI { Base = fb, Index = Opnd.Const(fo), Scale = 1, Src = Small(value, Is64(ft)), Kind = KindOf(ft) });
                            return;
                        }
                        case NameExpr { Symbol: FieldPathSymbol gf } when GlobalStruct(gf.Root) is string label && Supported(gf.Type) && !gf.Type.IsObject:
                            Emit(new StoreI { Place = $"{label} + {gf.Offset}", Src = Value(a.Value, gf.Type), Kind = KindOf(gf.Type) });
                            return;
                        case NameExpr { Symbol: FieldPathSymbol ff } when frameStructs.ContainsKey(ff.Root) && Supported(ff.Type) && !ff.Type.IsObject:
                            Emit(new StoreI { Place = FramePlace(ff.Root, ff.Offset), Src = Value(a.Value, ff.Type), Kind = KindOf(ff.Type) });
                            return;
                        case IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } inArray when GlobalArray(arr) != null:
                        {
                            Opnd value = Value(a.Value, inArray.Type);
                            var (bas, index, scale) = ArrayElement(inArray, arr);
                            Emit(new StoreIdxI { Base = bas, Index = index, Scale = scale, Src = Small(value, Is64(inArray.Type)), Kind = KindOf(inArray.Type) });
                            return;
                        }
                        case IndexExpr { Target.Type.IsString: true, Target.Type.IsArray: false } character when IsCharacter(character) && Supported(character.Type):
                        {
                            // a character of a str: the value, then where (checked), as the generator
                            Opnd value = Value(a.Value, character.Type);
                            var (text, index) = CharacterElement(character);
                            Emit(new StoreIdxI { Base = text, Index = index, Scale = 1, Src = Small(value, false), Kind = MemKind.U8 });
                            return;
                        }
                        case IndexExpr viewed when InView(viewed) && !viewed.Type.IsObject:
                        {
                            Opnd value = Value(a.Value, viewed.Type);
                            var (bas, index, scale) = ViewElement(viewed);
                            Emit(new StoreIdxI { Base = bas, Index = index, Scale = scale, Src = Small(value, Is64(viewed.Type)), Kind = KindOf(viewed.Type) });
                            return;
                        }
                        case IndexExpr placed when InPlace(placed) && !placed.Type.IsObject:
                        {
                            Opnd value = Value(a.Value, placed.Type);
                            var (bas, index, scale) = PlacedElement(placed);
                            Emit(new StoreIdxI { Base = bas, Index = index, Scale = scale, Src = Small(value, Is64(placed.Type)), Kind = KindOf(placed.Type) });
                            return;
                        }
                        case IndexExpr { Target.Type.IsPointer: true } element:
                        {
                            // (the value first, then where it goes, as the generator works them out)
                            Opnd value = Value(a.Value, element.Type);
                            var (bas, index, scale) = Element(element);
                            Emit(new StoreIdxI { Base = bas, Index = index, Scale = scale, Src = Small(value, Is64(element.Type)), Kind = KindOf(element.Type) });
                            return;
                        }
                    }
                    throw new NotYet($"an assignment to {a.Target.GetType().Name}");
                }

                case IfStmt { Condition: ThrowsExpr throws } tried:
                {
                    // the generator's EmitIfThrows: a catch record, the call, the record off again;
                    // a throw comes to Caught instead, where err is given the error
                    Need(tried.Then.Count > 0 && tried.Then[0] is VarDecl { Symbol: VariableSymbol err } && frameStructs.ContainsKey(err)
                         && err.Type.Struct!.Size == X86Generator.ErrorBytes, "a catch whose err isn't an Error");
                    var errVar = (VariableSymbol)((VarDecl)tried.Then[0]).Symbol!;
                    Need(locks.Count == 0, "a catch inside a locked block");
                    Mark(tried);
                    for (int k = 0; k < 6; k++) fn.NewHome();
                    string record = $"rbp - {8 * fn.Homes}";
                    Block body = fn.NewBlock(), caught = fn.NewBlock(), after = fn.NewBlock();
                    Block otherwise = tried.Else != null ? fn.NewBlock() : after;
                    Emit(new CatchI { Record = record, Caught = caught });
                    current.End = new TryT { Body = body, Caught = caught };
                    Enter(body);
                    Statement(throws.Call);
                    Emit(new UncatchI { Record = record });
                    if (current.End == null) current.End = new JumpT { Target = otherwise };
                    Enter(caught);
                    Emit(new CaughtI { Place = FramePlace(errVar, 0) });
                    Statements(tried.Then.Skip(1).ToList());
                    if (current.End == null) current.End = new JumpT { Target = after };
                    if (tried.Else != null)
                    {
                        Enter(otherwise);
                        Statements(tried.Else);
                        if (current.End == null) current.End = new JumpT { Target = after };
                    }
                    Enter(after);
                    return;
                }

                case IfStmt ifs:
                {
                    Need(ifs.Condition is not (ThrowsExpr or IsTypeExpr or HasWhenExpr), "an if it doesn't take");
                    Mark(ifs);
                    Block then = fn.NewBlock(), after = fn.NewBlock();
                    Block otherwise = ifs.Else != null ? fn.NewBlock() : after;
                    Condition(ifs.Condition, then, otherwise);
                    // laid out then, else, after: the else runs on into what follows
                    Enter(then);
                    Statements(ifs.Then);
                    if (current.End == null) current.End = new JumpT { Target = after };
                    if (ifs.Else != null)
                    {
                        Enter(otherwise);
                        Statements(ifs.Else);
                        if (current.End == null) current.End = new JumpT { Target = after };
                    }
                    Enter(after);
                    return;
                }

                case WhileStmt loop:
                {
                    // tested before the first time round, then at the bottom: one jump each time
                    // round, back to the top while it holds (the generator's loops are the same)
                    if (loop.Init != null) Statement(loop.Init);
                    // (a simple loop done a vec at a time first, where it can be)
                    if (VectorizeLoops) Vectorize(loop);
                    // the class variables the loop changes, in registers through it, as the
                    // generator keeps them (read before it, written back after it and before an
                    // error it stops with), when it calls nothing and nothing else reaches them
                    // (a statement in it that needs them in memory, a call or a return, has them
                    // written back before it and read again after, as the generator does: unless it
                    // has a break or continue, which would leave with them in neither; then none is held)
                    // (nor in a loop with a locked block: what's under a lock is shared with other
                    // threads, and a value kept in a register would be written back outside the
                    // lock, over theirs)
                    bool fenceable = !X86Generator.NeedsMemory(loop)
                        && !SyntaxWalker.Nodes(loop).Any(n => n is FreeStmt or LockedStmt or AtomicStmt or AtomicExpr or StringBuildStmt
                            || n is AssignStmt { Target.Type.IsString: true }
                            || n is Stmt inner && inner != loop && X86Generator.NeedsMemory(inner) && SyntaxWalker.Nodes(inner).Any(j => j is BreakStmt or ContinueStmt));
                    var holding = held.Count == 0 && fenceable ? HoldableIn(loop) : new List<VariableSymbol>();
                    foreach (VariableSymbol g in holding)
                    {
                        VReg v = New(g.Type, g.Name);
                        Emit(new LoadI { Dst = v, Place = Global(g)!, Kind = KindOf(g.Type) });
                        held[g] = v;
                    }
                    Block body = fn.NewBlock(), step = fn.NewBlock(), after = fn.NewBlock();
                    Mark(loop);
                    Condition(loop.Condition, body, after);
                    Enter(body);
                    loops.Add((step, after));
                    Statements(loop.Body);
                    loops.RemoveAt(loops.Count - 1);
                    StartBlock(step);
                    if (loop.Step != null) Statement(loop.Step);
                    Mark(loop);
                    Condition(loop.Condition, body, after);
                    Enter(after);
                    // (only the loop that holds them writes them back and lets them go: a loop
                    // inside it holds nothing of its own)
                    if (holding.Count > 0)
                    {
                        foreach (VariableSymbol g in holding)
                            Emit(new StoreI { Place = Global(g)!, Src = held[g], Kind = KindOf(g.Type) });
                        held.Clear();
                    }
                    return;
                }

                case WhileEachStmt { Current: not null } each:
                {
                    // the generator's: the index from 0, while it's under how many the array has
                    // now (read each time: the body may add to it), the element fetched each time round
                    Need(each.Fetch != null || each.Current.Type.IsObject || InPlaceStruct(each.Current.Type) && CanAddress(each.Current), "a while each over structs where they are");
                    Need(CanCount(each.Array), $"a while each over {Unknown(each.Array)}");
                    Mark(each);
                    VariableSymbol counter = each.Index.Symbol!;
                    Need(Supported(counter.Type) && !Is64(counter.Type), "a while each with a wide index");
                    VReg i = Variable(counter);
                    SetVariable(i, Opnd.Const(0), counter.Type);
                    Block body = fn.NewBlock(), step = fn.NewBlock(), after = fn.NewBlock();
                    EachTest(each.Array, i, body, after);
                    Enter(body);
                    if (each.Fetch != null) Statement(each.Fetch);
                    else
                    {
                        VariableSymbol element = each.Element.Symbol!;
                        Need(Supported(element.Type) && !owned.ContainsKey(element), "a while each's element it doesn't take");
                        // an object's address, or where a struct is in the array (the element itself)
                        SetVariable(Variable(element), each.Current.Type.IsObject ? Value(each.Current) : AddressOf(each.Current), element.Type);
                    }
                    loops.Add((step, after));
                    Statements(each.Body);
                    loops.RemoveAt(loops.Count - 1);
                    StartBlock(step);
                    Emit(new BinI { Op = BinOp.Add, Dst = i, A = i, B = Opnd.Const(1) });
                    Mark(each);
                    EachTest(each.Array, i, body, after);
                    Enter(after);
                    return;
                }

                case WhenStmt textChoice when (Real(textChoice.Subject) is { IsString: true, IsArray: false } || Real(textChoice.Subject).IsTextPointer)
                                              && textChoice.Branches.All(br => br.TypeName == null):
                {
                    // the subject's text once, then each value compared with it in turn (phi_strcmp), the first that's the same taken
                    Mark(textChoice);
                    VReg subject = Real(textChoice.Subject).IsTextPointer ? InReg(Value(textChoice.Subject), Addr) : (VReg)TextValue(textChoice.Subject).Reg!;
                    Block after = fn.NewBlock();
                    Block otherwise = textChoice.Else != null ? fn.NewBlock() : after;
                    var bodies = new List<(Block Block, List<Stmt> Body)>();
                    foreach (WhenBranch branch in textChoice.Branches) bodies.Add((fn.NewBlock(), branch.Body));
                    for (int k = 0; k < textChoice.Branches.Count; k++)
                        foreach (Expr value in textChoice.Branches[k].Values)
                        {
                            VReg other = Real(value).IsTextPointer ? InReg(Value(value), Addr) : (VReg)TextValue(value).Reg!;
                            VReg differs = fn.NewValue(false);
                            Emit(new TextI { Op = TextOp.Equal, Dst = differs, A = subject, B = other });
                            Block next = fn.NewBlock();
                            if (current.End != null) Enter(fn.NewBlock());
                            current.End = new BranchT { Cond = Cond.Eq, A = differs, B = Opnd.Const(0), Then = bodies[k].Block, Else = next };
                            Enter(next);
                        }
                    if (current.End == null) current.End = new JumpT { Target = otherwise };
                    foreach (var (block, body) in bodies)
                    {
                        Enter(block);
                        Statements(body);
                        if (current.End == null) current.End = new JumpT { Target = after };
                    }
                    if (textChoice.Else != null)
                    {
                        Enter(otherwise);
                        Statements(textChoice.Else);
                        if (current.End == null) current.End = new JumpT { Target = after };
                    }
                    Enter(after);
                    return;
                }

                case WhenStmt choice:
                {
                    PhiType st = Real(choice.Subject);
                    Need(Supported(st) && !IsF(st) && !st.IsPointer && !st.IsObject && choice.Branches.All(br => br.TypeName == null), "a when it doesn't take");
                    Mark(choice);
                    VReg subject = InReg(Value(choice.Subject), Is64(st));
                    Block after = fn.NewBlock();
                    Block otherwise = choice.Else != null ? fn.NewBlock() : after;
                    var sw = new SwitchT { Value = subject, Default = otherwise };
                    var bodies = new List<(Block Block, List<Stmt> Body)>();
                    foreach (WhenBranch branch in choice.Branches)
                    {
                        Block body = fn.NewBlock();
                        foreach (Expr value in branch.Values)
                        {
                            long? k = value switch
                            {
                                NumberExpr n => n.Value,
                                NameExpr { Symbol: ConstantSymbol c } => c.Value,
                                StringExpr { Character: int ch } => ch,
                                _ => Semantics.ConstantFolder.Evaluate(value),
                            };
                            Need(k != null, "a when whose values aren't numbers known when it's built");
                            sw.Cases.Add((k!.Value, body));
                        }
                        bodies.Add((body, branch.Body));
                    }
                    if (current.End != null) Enter(fn.NewBlock());
                    current.End = sw;
                    foreach (var (block, body) in bodies)
                    {
                        Enter(block);
                        Statements(body);
                        if (current.End == null) current.End = new JumpT { Target = after };
                    }
                    if (choice.Else != null)
                    {
                        Enter(otherwise);
                        Statements(choice.Else);
                        if (current.End == null) current.End = new JumpT { Target = after };
                    }
                    Enter(after);
                    return;
                }

                case ConstDecl:
                    return;     // (a constant is its value where it's used)

                case BreakStmt:
                    Need(loops.Count > 0, "a break outside a loop");
                    Mark(s);
                    Unlock(loops.Count);
                    if (current.End == null) current.End = new JumpT { Target = loops[^1].Break };
                    return;

                case ContinueStmt:
                    Need(loops.Count > 0, "a continue outside a loop");
                    Mark(s);
                    Unlock(loops.Count);
                    if (current.End == null) current.End = new JumpT { Target = loops[^1].Continue };
                    return;

                case ReturnStmt ret:
                    Mark(ret);
                    Need(ret.Value == null || locks.Count == 0 || !SyntaxWalker.Nodes(ret.Value).Any(n => n is CallExpr or NameExpr { Call: not null } or MemberExpr { Call: not null }),
                         "a return from inside a lock whose value calls a method");
                    if (ret.Value != null) Return(ret.Value);
                    else
                    {
                        Unlock(0);
                        if (current.End == null)
                        {
                            TidyUp();
                            current.End = new RetT();
                        }
                    }
                    return;

                case UnsafeStmt u:
                    // (unsafe leaves out index checks, which the binder has already left out of
                    // what's inside, and those known only when it runs, here)
                    unsafeDepth++;
                    try { Statements(u.Body); }
                    finally { unsafeDepth--; }
                    return;

                case LockedStmt lk:
                {
                    Need(lk.Lock is NameExpr { Symbol: VariableSymbol { Type.Kind: TypeKind.Lock } lv } && AnyGlobal(lv) != null, "a lock that isn't a class's variable");
                    string label = AnyGlobal((VariableSymbol)((NameExpr)lk.Lock).Symbol!)!;
                    Mark(lk);
                    VReg saved = fn.NewAddress("catch");
                    Emit(new LockI { Label = label, Saved = saved });
                    locks.Add((label, saved, loops.Count));
                    Statements(lk.Body);
                    locks.RemoveAt(locks.Count - 1);
                    Emit(new UnlockI { Label = label, Saved = saved });
                    return;
                }

                case LogStmt log:
                    Log(log);
                    return;

                case AppendStmt append:
                    Add(append);
                    return;

                case StringBuildStmt build:
                    Mark(build);
                    BuildText(build);
                    return;

                case AppendArrayStmt addArray:
                    AddArray(addArray);
                    return;

                case ArrayAssignStmt arrays:
                    AssignArrays(arrays);
                    return;

                case ExitStmt exit:
                {
                    Need(program, "an exit outside a program");
                    Mark(exit);
                    Opnd code = exit.Code != null ? Value(exit.Code, PhiType.Int) : Opnd.Const(0);
                    Emit(new ExitI { Code = code, Held = HeldStores() });
                    current.End = new RetT();
                    return;
                }

                case ThrowStmt thrown:
                {
                    Need(thrown.Text != null, "a throw without its text");
                    Mark(thrown);
                    textMayCut = true;
                    VReg text = (VReg)TextValue(thrown.Text!).Reg!;
                    textMayCut = false;
                    Emit(new ThrowI { Text = text, At = thrown, Held = HeldStores() });
                    // (it never comes back: what follows is reached by nothing)
                    current.End = new RetT();
                    return;
                }

                case AtomicStmt atomic:
                {
                    // the value, then where (the generator's order), then the one step
                    Mark(atomic);
                    PhiType t = atomic.Target.Type;
                    Need(CanAddress(atomic.Target) && t.ElementSize is 1 or 2 or 4 or 8, $"atomic on {Unknown(atomic.Target)}");
                    if (atomic.Op == AtomicOp.CompareSwap)
                    {
                        Opnd value = Small(Value(atomic.Value, t), Is64(t)), expected = Small(Value(atomic.Expected!, t), Is64(t));
                        Emit(new AtomicI { Op = AtomicOp.CompareSwap, Address = AddressOf(atomic.Target), Value = value, Expected = expected, Bytes = t.ElementSize, Dst = fn.NewValue(false) });
                        return;
                    }
                    Opnd amount = Small(Value(atomic.Value, atomic.Op == AtomicOp.Swap ? t : Is64(t) ? PhiType.I64 : PhiType.Int), Is64(t));
                    Emit(new AtomicI { Op = atomic.Op, Address = AddressOf(atomic.Target), Value = amount, Bytes = t.ElementSize });
                    return;
                }

                case SetLengthStmt shrink:
                    SetLength(shrink);
                    return;

                case FreeStmt { Owner: null, Releaser: MethodSymbol releaser } free:
                    Mark(free);
                    Call(releaser, new List<Expr> { free.Pointer }, wantResult: false);
                    return;

                case CallStmt { Resolved: RawBlockDecl raw } rc:
                    Need(rc.Arguments.Count == 0 && rc.CalleeTarget == null, "an assembly block given arguments");
                    Mark(rc);
                    if (rc.ResultTarget == null)
                    {
                        Emit(new RawCallI { Label = raw.Name });
                        return;
                    }
                    // what it leaves in eax, an int, kept as a call's result is
                    Need(rc.ResultTarget is NameExpr { Symbol: VariableSymbol { Type.IsObject: false } rawKept } && Supported(rawKept.Type) && (Ours(rawKept) || Global(rawKept) != null),
                         "an assembly block's result kept somewhere it doesn't take");
                    var into = (VariableSymbol)((NameExpr)rc.ResultTarget).Symbol!;
                    VReg rawGave = fn.NewValue(false);
                    Emit(new RawCallI { Label = raw.Name, Dst = rawGave });
                    Opnd rawValue = Convert(rawGave, PhiType.Int, into.Type);
                    if (!Ours(into)) Emit(new StoreI { Place = Global(into)!, Src = rawValue, Kind = KindOf(into.Type) });
                    else SetVariable(Variable(into), rawValue, into.Type);
                    return;

                case CallStmt { Resolved: SysCallDecl sys } scall:
                {
                    Mark(scall);
                    if (scall.ResultTarget == null)
                    {
                        SystemCall(sys, scall.Arguments, wantResult: false);
                        return;
                    }
                    Need(scall.ResultTarget is NameExpr { Symbol: VariableSymbol { Type.IsObject: false } } && Ours((VariableSymbol)((NameExpr)scall.ResultTarget).Symbol!),
                         "a call's result kept somewhere it doesn't take");
                    var keeps = (VariableSymbol)((NameExpr)scall.ResultTarget).Symbol!;
                    VReg came = SystemCall(sys, scall.Arguments, wantResult: true)!;
                    SetVariable(Variable(keeps), Convert(came, sys.ResultType ?? PhiType.Int, keeps.Type), keeps.Type);
                    return;
                }

                case CallStmt { Resolved: BuiltinFunction builtin, CalleeTarget: null } bcall:
                {
                    Mark(bcall);
                    if (bcall.ResultTarget == null)
                    {
                        CallBuiltin(builtin, bcall.Arguments, wantResult: false);
                        return;
                    }
                    Need(bcall.ResultTarget is NameExpr { Symbol: VariableSymbol { Type.IsObject: false } } && Ours((VariableSymbol)((NameExpr)bcall.ResultTarget).Symbol!),
                         "a call's result kept somewhere it doesn't take");
                    var keeps = (VariableSymbol)((NameExpr)bcall.ResultTarget).Symbol!;
                    VReg gave = CallBuiltin(builtin, bcall.Arguments, wantResult: true)!;
                    SetVariable(Variable(keeps), Convert(gave, builtin.ResultType, keeps.Type), keeps.Type);
                    return;
                }

                case CallStmt { Resolved: PointerCall through } pcall:
                {
                    Mark(pcall);
                    if (pcall.ResultTarget == null)
                    {
                        CallThrough(through, pcall.Arguments, wantResult: false);
                        return;
                    }
                    Need(pcall.ResultTarget is NameExpr { Symbol: VariableSymbol { Type.IsObject: false } } && Ours((VariableSymbol)((NameExpr)pcall.ResultTarget).Symbol!),
                         "a call's result kept somewhere it doesn't take");
                    var kept = (VariableSymbol)((NameExpr)pcall.ResultTarget).Symbol!;
                    VReg came = CallThrough(through, pcall.Arguments, wantResult: true)!;
                    SetVariable(Variable(kept), Convert(came, through.Signature.Result, kept.Type), kept.Type);
                    return;
                }

                case CallStmt call:
                {
                    Need(call.Resolved is MethodSymbol cm && (call.CalleeTarget == null || cm.Decl.Owner.ForStruct != null), "a call of a built-in or a method value");
                    Mark(call);
                    var callee = (MethodSymbol)call.Resolved!;
                    if (call.ResultTarget == null)
                    {
                        Call(callee, call.Arguments, wantResult: false);
                        return;
                    }
                    if (callee.ReturnType.IsObject)
                    {
                        // a call's new object: the target takes it over (the generator's EmitTakeOver)
                        Emit(new ObjectLineI { At = call });
                        VReg made = Call(callee, call.Arguments, wantResult: true)!;
                        TakeObject(call.ResultTarget, callee.ReturnType.Struct!, made, move: true);
                        return;
                    }
                    if (GivesText(callee))
                    {
                        // its text put in the target, as an assignment of the call would
                        var asValue = new CallExpr { Callee = call.Callee, CalleeSpan = call.CalleeSpan, CalleeTarget = call.CalleeTarget, Resolved = callee, Type = callee.ReturnType, Span = call.Span };
                        asValue.Arguments.AddRange(call.Arguments);
                        Need(call.ResultTarget.Type.IsString && !call.ResultTarget.Type.IsArray && SetText(call.ResultTarget, asValue), $"a call's text kept in {Unknown(call.ResultTarget)}");
                        return;
                    }
                    if (call.ResultTarget is not NameExpr { Symbol: VariableSymbol { Type.IsObject: false } } && !call.ResultTarget.Type.IsObject)
                    {
                        // kept somewhere else (an element, a field): as an assignment of the call, which
                        // works out its value (the call) before where it goes, as the generator does
                        var made = new CallExpr { Callee = call.Callee, CalleeSpan = call.CalleeSpan, CalleeTarget = call.CalleeTarget, Resolved = callee, Type = callee.ReturnType, Span = call.Span };
                        made.Arguments.AddRange(call.Arguments);
                        Statement(new AssignStmt { Target = call.ResultTarget, Op = AssignOp.Set, Value = made, Span = call.Span });
                        return;
                    }
                    Need(call.ResultTarget is NameExpr { Symbol: VariableSymbol { Type.IsObject: false } }, "a call's result kept somewhere it doesn't take");
                    var target = (VariableSymbol)((NameExpr)call.ResultTarget).Symbol!;
                    VReg got = Call(callee, call.Arguments, wantResult: true)!;
                    Opnd converted = Convert(got, callee.ReturnType, target.Type);
                    if (Global(target) is string place && !variables.ContainsKey(target) && !method.Locals.Contains(target) && !method.Parameters.Contains(target))
                        Emit(new StoreI { Place = place, Src = converted, Kind = KindOf(target.Type) });
                    else SetVariable(Variable(target), converted, target.Type);
                    return;
                }
            }
            throw new NotYet($"a {s.GetType().Name}");
        }

        void Assign(VariableSymbol v, Expr? value)
        {
            Need(!owned.ContainsKey(v), "an object given a value as a number is");
            VReg dst = Variable(v);
            Opnd src = value != null ? Value(value, v.Type) : v.Type.IsVec ? ZeroVec() : IsF(v.Type) ? FloatConst(0.0) : Opnd.Const(0);
            SetVariable(dst, src, v.Type);
        }

        /// <summary>A variable of type t (in its register) given a value: an f32 is rounded to one.</summary>
        void SetVariable(VReg dst, Opnd src, PhiType t)
        {
            if (t.Kind == TypeKind.F32) Emit(new RoundF32I { Dst = dst, Src = src.Reg! });
            else if (src.IsImm) Emit(new ConstI { Dst = dst, Value = src.Imm });
            else Emit(new CopyI { Dst = dst, Src = src });
        }

        /// <summary>Each return gives its value back where it is (no value carried to one exit, which would live across every call).</summary>
        void Return(Expr value)
        {
            Need(method.ReturnType.Kind != TypeKind.Void, "a return with a value from a method that gives none");
            Opnd v = method.ReturnType.IsObject ? GiveObject(value) : Value(value, method.ReturnType);
            if (GivesText(method))
            {
                // copied where the caller said before what the method owns goes back
                VReg there = fn.NewAddress();
                Emit(new ResultTextI { Dst = there, Value = InReg(v, Addr), Grow = growResults, Room = method.ResultCapacity, Held = HeldStores() });
                v = there;
            }
            Unlock(0);
            if (current.End != null) Enter(fn.NewBlock());
            TidyUp();
            current.End = new RetT { Value = v, Wide = method.ReturnType.IsWide, Pointer = method.ReturnType.IsPointer || method.ReturnType.IsObject || GivesText(method),
                                     Float = IsF(method.ReturnType), Vec = method.ReturnType.IsVec };
        }

        /// <summary>True when <see cref="Field"/> takes the expression (asking only, with nothing built).</summary>
        bool IsField(Expr e) => e switch
        {
            NameExpr { Symbol: SelfFieldSymbol f } => Ours(f.Self) && Supported(f.Type),
            MemberExpr { Field: FieldSymbol f, Call: null, Target: IndexExpr { Target.Type.IsPointer: true } element } when Supported(f.Type)
                && element.Type.IsStruct && !element.Type.IsArray && element.Type.Struct!.Size > 0 => true,
            MemberExpr { Field: FieldSymbol f, Call: null } m when Supported(f.Type) && IsAddress(Real(m.Target)) && Supported(Real(m.Target)) => true,
            MemberExpr { Field: FieldSymbol f, Call: null } placed => Supported(f.Type) && InPlaceStruct(placed.Target.Type) && CanAddress(placed.Target),
            NameExpr { Symbol: PathSymbol p } => PathRoot(p) && p.Hops.Count > 0 && Supported(p.Type),
            _ => false,
        };

        /// <summary>
        /// A field this form reads and writes where it is: its base (an address, in a value),
        /// its offset from there and its type. self's field by name; p.field through a pointer
        /// or an object; and a path through objects (a.next.value), each address on the way
        /// read from the one before. A field that's an object is its address, read as a pointer
        /// is (what's put there is the object statements' business: an assignment of one isn't
        /// taken). (null, 0, Error) for anything else.
        /// </summary>
        (VReg? Base, long Offset, PhiType Type) Field(Expr e)
        {
            switch (e)
            {
                case NameExpr { Symbol: SelfFieldSymbol f } when Ours(f.Self) && Supported(f.Type):
                    return (Variable(f.Self), f.Offset, f.Type);
                case MemberExpr { Field: FieldSymbol f, Call: null } m when Supported(f.Type) && IsAddress(Real(m.Target)) && Supported(Real(m.Target)):
                    return (InReg(Value(m.Target), Addr), f.Offset, f.Type);
                case MemberExpr { Field: FieldSymbol f, Call: null, Target: IndexExpr { Target.Type.IsPointer: true } element } when Supported(f.Type)
                                                                                && element.Type.IsStruct && !element.Type.IsArray && element.Type.Struct!.Size > 0:
                {
                    // p:i.field: the struct at p + i * its size, and the field in it (the index first, as an element's)
                    long size = element.Type.Struct!.Size;
                    PhiType it = Real(element.Index);
                    Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
                    Opnd index = Convert(Value(element.Index), it, IndexType(it.IsSigned));
                    VReg p = InReg(Value(element.Target), Addr);
                    if (index.IsImm)
                    {
                        long offset = index.Imm * size + f.Offset;
                        if (offset >= int.MinValue && offset <= int.MaxValue) return (p, offset, f.Type);
                        index = InReg(index, Addr);
                    }
                    VReg scaled = fn.NewAddress();
                    Emit(new BinI { Op = BinOp.Mul, Dst = scaled, A = index.Reg!, B = Opnd.Const(size) });
                    VReg at = fn.NewAddress();
                    Emit(new BinI { Op = BinOp.Add, Dst = at, A = p, B = scaled });
                    return (at, f.Offset, f.Type);
                }
                case MemberExpr { Field: FieldSymbol f, Call: null } placed when Supported(f.Type) && InPlaceStruct(placed.Target.Type) && CanAddress(placed.Target):
                    // list:i.field (an element in place, a struct's struct): where the struct is, and the field in it
                    return (AddressOf(placed.Target), f.Offset, f.Type);
                case NameExpr { Symbol: PathSymbol p } when PathRoot(p) && p.Hops.Count > 0
                                                             && Supported(p.Type):
                {
                    VReg at = PathStart(p);
                    for (int k = 0; k + 1 < p.Hops.Count; k++)
                    {
                        VReg next = fn.NewAddress();
                        Emit(new LoadIdxI { Dst = next, Base = at, Index = Opnd.Const(p.Hops[k]), Scale = 1, Kind = Ptr });
                        at = next;
                    }
                    return (at, p.Hops[^1], p.Type);
                }
            }
            return (null, 0, PhiType.Error);
        }

        /// <summary>A class's array of a size, kept where its label is, of numbers or f64s: its label, or null.</summary>
        /// <summary>A struct kept where it is (in an array, a frame, through a pointer): reached by its address, not a value.</summary>
        static bool InPlaceStruct(PhiType t) => t.IsStruct && !t.IsArray && !t.IsObject && t.Struct != null && t.Struct.Size > 0;

        /// <summary>An element this form reads as a value or reaches by its address: a number (a vec, an object's address), or a struct in place.</summary>
        static bool Reachable(PhiType t) => Supported(t) && t.ElementSize is 1 or 2 or 4 or 8 or 16 || InPlaceStruct(t) || t.IsInlineText && t.Room > 0;

        /// <summary>A class's array whose elements are read and reached (objects' addresses and structs in place too), not only those written as numbers.</summary>
        static string? ClassArray(VariableSymbol v) =>
            !v.IsLocal && !v.IsParameter && v.Type.IsArray && !v.Type.Open && !v.Type.TextTable && v.Type.Length > 0
            && !v.GrowingArray && !v.GrowingElements && Reachable(v.Type.Element) && SimpleLabel(v.Label) ? v.Label : null;

        static string? GlobalArray(VariableSymbol v) =>
            !v.IsLocal && !v.IsParameter && v.Type.IsArray && !v.Type.Open && !v.Type.TextTable && v.Type.Length > 0     // (an [atmost N] one too: its N in place, its count after them)
            && !v.GrowingArray && !v.GrowingElements && Supported(v.Type.Element) && !v.Type.Element.IsObject && SimpleLabel(v.Label) ? v.Label : null;

        /// <summary>
        /// list:i of a class's array: its address, the index made 64 bits, the element's size,
        /// with the index checked against the array's size as the generator checks it (an index
        /// written in is checked when it's built; one that's a mask smaller than the size, as
        /// (x &amp; 0xFF) of 256, can't be out of range).
        /// </summary>
        (VReg Base, Opnd Index, int Scale) ArrayElement(IndexExpr e, VariableSymbol array)
        {
            PhiType element = e.Type;
            Need(Reachable(element), $"an array of {element}");
            int scale = element.ElementSize;
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, IndexType(it.IsSigned));
            if (e.CheckLimit is int limit && !(index.IsImm && index.Imm >= 0 && index.Imm < limit) && !MaskedBelow(e.Index, limit))
            {
                VReg checkedIndex = InReg(index, Addr);
                Emit(new CheckI { Index = checkedIndex, Limit = limit, At = e, Held = HeldStores() });
                index = checkedIndex;
            }
            VReg bas = fn.NewAddress();
            Emit(new AddrI { Dst = bas, Label = ClassArray(array)! });
            if (index.IsImm && (index.Imm * scale < int.MinValue || index.Imm * scale > int.MaxValue)) index = InReg(index, Addr);
            return (bas, index, scale);
        }

        /// <summary>x &amp; mask, with mask a number less than limit: never out of an array of limit.</summary>
        static bool MaskedBelow(Expr index, int limit) =>
            index is BinaryExpr { Op: BinaryOp.BitAnd } and
            && (Semantics.ConstantFolder.Evaluate(and.Right) ?? Semantics.ConstantFolder.Evaluate(and.Left)) is long mask
            && mask >= 0 && mask < limit;

        /// <summary>The class variables a loop assigns, which may be kept in registers through it.</summary>
        List<VariableSymbol> HoldableIn(WhileStmt loop)
        {
            var changed = new HashSet<VariableSymbol>();
            foreach (Node n in SyntaxWalker.Nodes(loop))
                if (n is AssignStmt { Target: NameExpr { Symbol: VariableSymbol set } } && !Ours(set) && Global(set) != null && holdable(set))
                    changed.Add(set);
            return changed.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>The held class variables written back, for an error that leaves the loop.</summary>
        List<StoreI> HeldStores() => heldInMemory ? new List<StoreI>() : held.Select(h => new StoreI { Place = Global(h.Key)!, Src = h.Value, Kind = KindOf(h.Key.Type) }).ToList();

        /// <summary>A parameter or variable of this method (not a class's).</summary>
        bool Ours(VariableSymbol v) => variables.ContainsKey(v) || method.Locals.Contains(v) || method.Parameters.Contains(v);

        /// <summary>A class's struct variable, whose fields are at its label plus their offsets, or null.</summary>
        static string? GlobalStruct(VariableSymbol v) =>
            !v.IsLocal && !v.IsParameter && v.Type.IsStruct && !v.Type.IsArray && SimpleLabel(v.Label) ? v.Label : null;

        /// <summary>A class's variable of any kind, for addr: its label, or null.</summary>
        static string? AnyGlobal(VariableSymbol v) => !v.IsLocal && !v.IsParameter && SimpleLabel(v.Label) ? v.Label : null;

        static bool SimpleLabel(string label) => label.Length > 0 && label.All(c => char.IsLetterOrDigit(c) || c == '_');

        /// <summary>A class's variable this form can read and write where it is, or null.</summary>
        static string? Global(VariableSymbol v) =>
            !v.IsLocal && !v.IsParameter && Supported(v.Type) && SimpleLabel(v.Label) ? v.Label : null;

        // ---------------------------------------------------------------- conditions

        /// <summary>Jumps to yes when e holds (a number not 0), and to no when it doesn't.</summary>
        void Condition(Expr e, Block yes, Block no)
        {
            switch (e)
            {
                case BinaryExpr { Op: BinaryOp.And } and:
                {
                    Block right = fn.NewBlock();
                    Condition(and.Left, right, no);
                    Enter(right);
                    Condition(and.Right, yes, no);
                    return;
                }
                case BinaryExpr { Op: BinaryOp.Or } or:
                {
                    Block right = fn.NewBlock();
                    Condition(or.Left, yes, right);
                    Enter(right);
                    Condition(or.Right, yes, no);
                    return;
                }
                case UnaryExpr { Op: UnaryOp.Not } not:
                    Condition(not.Operand, no, yes);
                    return;
                case BinaryExpr texts when TextCompare(texts):
                {
                    var (result, cond) = CompareTexts(texts);
                    if (current.End != null) Enter(fn.NewBlock());
                    current.End = new BranchT { Cond = cond, A = result, B = Opnd.Const(0), Then = yes, Else = no };
                    return;
                }
                case BinaryExpr cmp when IsComparison(cmp.Op):
                {
                    var (a, b, cond, isFloat) = Compare(cmp);
                    if (current.End != null) Enter(fn.NewBlock());
                    current.End = new BranchT { Float = isFloat, Cond = cond, A = a, B = b, Then = yes, Else = no };
                    return;
                }
            }
            PhiType et = Real(e);
            Need(Supported(et), $"a condition that's a {et}");
            if (IsF(et))
            {
                VReg fv = (VReg)Value(e, et).Reg!;
                VReg zero = FloatConst(0.0);
                if (current.End != null) Enter(fn.NewBlock());
                current.End = new BranchT { Float = true, Cond = Cond.Ne, A = fv, B = zero, Then = yes, Else = no };
                return;
            }
            VReg v = InReg(Value(e, et), Is64(et));
            if (current.End != null) Enter(fn.NewBlock());
            current.End = new BranchT { Cond = Cond.Ne, A = v, B = Opnd.Const(0), Then = yes, Else = no };
        }

        static bool IsComparison(BinaryOp op) => op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual;

        /// <summary>A comparison's two sides, made the same width, and how they compare (as the generator does).</summary>
        (VReg A, Opnd B, Cond Cond, bool Float) Compare(BinaryExpr cmp)
        {
            if (cmp.ArrayCompare && cmp.Op is BinaryOp.Equal or BinaryOp.NotEqual)
            {
                VReg same = ArraysEqual(cmp);
                return (same, Opnd.Const(0), cmp.Op == BinaryOp.Equal ? Cond.Ne : Cond.Eq, false);
            }
            if (cmp.ObjectCompare is StructSymbol compared && cmp.Op is BinaryOp.Equal or BinaryOp.NotEqual)
            {
                if (cmp.Left is EmptyExpr || cmp.Right is EmptyExpr)
                {
                    // is (or isn't) empty: its address 0
                    VReg one = InReg(Value(cmp.Left is EmptyExpr ? cmp.Right : cmp.Left), Addr);
                    return (one, Opnd.Const(0), cmp.Op == BinaryOp.Equal ? Cond.Eq : Cond.Ne, false);
                }
                // their values compared by the struct's routine (1 when they match); the left's
                // address first only when it calls something (the generator's LeftAddressFirst)
                VReg left, right;
                if (EvaluationOrder.LeftFirst(cmp.Left, cmp.Right, method) && EvaluationOrder.HasEffect(cmp.Left))
                {
                    left = ObjectAddress(cmp.Left);
                    right = ObjectAddress(cmp.Right);
                }
                else
                {
                    right = ObjectAddress(cmp.Right);
                    left = ObjectAddress(cmp.Left);
                }
                VReg match = fn.NewValue(false);
                Emit(new ObjCallI { Kind = "equal", Struct = compared, Arg = left, Second = right, Dst = match });
                return (match, Opnd.Const(0), cmp.Op == BinaryOp.Equal ? Cond.Ne : Cond.Eq, false);
            }
            PhiType l = Real(cmp.Left), r = Real(cmp.Right);
            Need(Supported(l) && Supported(r), $"a comparison of {l} and {r}");
            Need(cmp.ObjectCompare == null && !l.IsObject && !r.IsObject, "a comparison of objects");
            if (IsF(l) || IsF(r))
            {
                // both made f64s, the left first; the condition is the signed one's, read as IEEE
                Need(!l.IsPointer && !r.IsPointer, "a pointer compared with a float");
                VReg fa = (VReg)Convert(Value(cmp.Left), l, PhiType.F64).Reg!;
                VReg fb = (VReg)Convert(Value(cmp.Right), r, PhiType.F64).Reg!;
                Cond fc = cmp.Op switch
                {
                    BinaryOp.Equal => Cond.Eq,
                    BinaryOp.NotEqual => Cond.Ne,
                    BinaryOp.Less => Cond.LtS,
                    BinaryOp.LessEqual => Cond.LeS,
                    BinaryOp.Greater => Cond.GtS,
                    _ => Cond.GeS,
                };
                return (fa, fb, fc, true);
            }
            // (a pointer, or a method value, is compared as an unsigned 64-bit number, with 0 or another)
            bool pointers = l.IsPointer || r.IsPointer || l.IsMethod || r.IsMethod;
            bool wide = l.IsWide || r.IsWide || pointers;
            bool unsigned = pointers || (wide ? l.Kind == TypeKind.U64 || r.Kind == TypeKind.U64 : l.IsUnsigned32 || r.IsUnsigned32);
            PhiType at = pointers ? (l.IsPointer || l.IsMethod ? l : r) : wide ? (unsigned ? PhiType.U64 : PhiType.I64) : (unsigned ? PhiType.U32 : PhiType.I32);
            // (the left side is worked out first: see Arithmetic)
            VReg a = InReg(Convert(Value(cmp.Left, l), l, at), wide);
            Opnd b = Small(Convert(Value(cmp.Right, r), r, at), wide);
            Cond c = cmp.Op switch
            {
                BinaryOp.Equal => Cond.Eq,
                BinaryOp.NotEqual => Cond.Ne,
                BinaryOp.Less => unsigned ? Cond.LtU : Cond.LtS,
                BinaryOp.LessEqual => unsigned ? Cond.LeU : Cond.LeS,
                BinaryOp.Greater => unsigned ? Cond.GtU : Cond.GtS,
                _ => unsigned ? Cond.GeU : Cond.GeS,
            };
            return (a, b, c, false);
        }

        // ---------------------------------------------------------------- values

        /// <summary>e's value made the type it goes into (a variable's, a parameter's, a result's).</summary>
        Opnd Value(Expr e, PhiType into)
        {
            if (into.IsString && !into.IsArray) return TextValue(e);
            Need(Supported(into), $"a value going into a {into}");
            return Convert(Value(e), Real(e), into);
        }

        /// <summary>e's value as its own type has it.</summary>
        Opnd Value(Expr e)
        {
            if (e.Type.IsString && !e.Type.IsArray && e is not (CallExpr or NameExpr { Call: not null } or MemberExpr { Call: not null })) return TextValue(e);
            switch (e)
            {
                case BinaryExpr texts when TextCompare(texts):
                {
                    var (result, cond) = CompareTexts(texts);
                    VReg holds = fn.NewValue(false);
                    Emit(new SetCcI { Cond = cond, Dst = holds, A = result, B = Opnd.Const(0) });
                    return holds;
                }
                case NameExpr { Symbol: LengthSymbol { TextRef: VariableSymbol caller } } when callersTexts.TryGetValue(caller, out VReg? given):
                {
                    // .max of the caller's str: its room less the zero when it has a size (the
                    // Fixed bit), or the most a str grows to (the generator's)
                    VReg room = LoadAt(given, PhiType.AddressSize, PhiType.U32);
                    VReg most = fn.NewValue(false);
                    Emit(new ConstI { Dst = most, Value = Binder.StrMax });
                    Block sized = fn.NewBlock(), known = fn.NewBlock();
                    if (current.End != null) Enter(fn.NewBlock());
                    current.End = new BranchT { Cond = Cond.Ne, A = room, B = Opnd.Const(0), TestMask = 0x40000000, Then = sized, Else = known };
                    Enter(sized);
                    VReg masked = fn.NewValue(false);
                    Emit(new BinI { Op = BinOp.And, Dst = masked, A = room, B = Opnd.Const(0x3FFFFFFF) });
                    Emit(new BinI { Op = BinOp.Sub, Dst = most, A = masked, B = Opnd.Const(1) });
                    current.End = new JumpT { Target = known };
                    Enter(known);
                    return most;
                }
                case NameExpr { Symbol: LengthSymbol { Str: VariableSymbol measured, Max: false, Count: null } }:
                    return TextLength(new NameExpr { Name = measured.Name, Symbol = measured, Type = measured.Type });
                case NameExpr { Symbol: LengthSymbol { Text: Expr text } }:
                    return TextLength(text);
                case MemberExpr { TextLength: true, Call: null } measuredField:
                    return TextLength(measuredField.Target);
                case IndexExpr { Target.Type.IsString: true, Target.Type.IsArray: false } character when IsCharacter(character):
                {
                    var (text, index) = CharacterElement(character);
                    VReg d = New(character.Type);
                    Emit(new LoadIdxI { Dst = d, Base = text, Index = index, Scale = 1, Kind = MemKind.U8 });
                    return d;
                }
            }
            if (e.Type.IsString && !e.Type.IsArray)
                switch (e)
                {
                    // a call that gives text: the address of what it gave
                    case CallExpr c: return CallValue(c);
                    case NameExpr { Call: CallExpr nc }: return CallValue(nc);
                    case MemberExpr { Call: CallExpr mc }: return CallValue(mc);
                }
            Need(Supported(Real(e)), $"a value that's a {Real(e)}");
            if (PointerMath(e)) return MovePointer((BinaryExpr)e);
            if (VecValue(e) is Opnd vector) return vector;
            if (e is AtomicExpr swap)
            {
                // atomic x is v when old: 1 if it held old (and holds v now)
                PhiType t = swap.Target.Type;
                Need(CanAddress(swap.Target) && t.ElementSize is 1 or 2 or 4 or 8, $"atomic on {Unknown(swap.Target)}");
                Opnd value = Small(Value(swap.Value, t), Is64(t)), expected = Small(Value(swap.Expected, t), Is64(t));
                VReg done = fn.NewValue(false);
                Emit(new AtomicI { Op = AtomicOp.CompareSwap, Address = AddressOf(swap.Target), Value = value, Expected = expected, Bytes = t.ElementSize, Dst = done });
                return done;
            }
            switch (e)
            {
                case NumberExpr n:
                    return Opnd.Const(n.Value);
                case NameExpr { Symbol: LengthSymbol { Count: int known } }:
                    return Opnd.Const(known);
                case MemberExpr { Count: int counted, Call: null }:
                    return Opnd.Const(counted);
                // how many an array has, read where it keeps it: an array field without a size
                // (its count and room after its address), an [atmost N] one (after its elements)
                case NameExpr { Symbol: LengthSymbol { View: Expr view } length } when CanAddress(view):
                    return LoadAt(AddressOf(view), length.Max ? ViewMax : ViewCount, PhiType.Int);
                case NameExpr { Symbol: LengthSymbol { Filled: Expr filled } } when CanAddress(filled):
                    return LoadAt(AddressOf(filled), filled.Type.CountOffset, PhiType.Int);
                case NameExpr { Symbol: LengthSymbol { Open: VariableSymbol viewed } viewLength } when paramViews.TryGetValue(viewed, out string? viewPlace):
                {
                    VReg view = fn.NewAddress();
                    Emit(new AddrI { Dst = view, Label = viewPlace });
                    return LoadAt(view, viewLength.Max ? ViewMax : ViewCount, PhiType.Int);
                }
                case NameExpr { Symbol: LengthSymbol { Open: VariableSymbol held } length } when GrowingView(held):
                {
                    VReg view = fn.NewAddress();
                    Emit(new AddrI { Dst = view, Label = ViewHome(held) });
                    return LoadAt(view, length.Max ? ViewMax : ViewCount, PhiType.Int);
                }
                case MemberExpr { FilledLength: true, Call: null, Target: NameExpr { Symbol: VariableSymbol grown } growing }
                    when GrowingView(grown):
                    return LoadAt(ViewOf(growing), ViewCount, PhiType.Int);
                case MemberExpr { FilledLength: true, Call: null, Target: var counted } when CanAddress(counted):
                    return LoadAt(AddressOf(counted), counted.Type.Open ? ViewCount : counted.Type.CountOffset, PhiType.Int);
                case BoolExpr b:
                    return Opnd.Const(b.Value ? 1 : 0);
                case StringExpr { Character: int c } when e.Type.IsChar || e.Type.IsNumeric:
                    return Opnd.Const(c);
                case NameExpr { Call: CallExpr call }:
                    return CallValue(call);
                case MemberExpr { Call: CallExpr call }:
                    return CallValue(call);
                case CallExpr call:
                    return CallValue(call);
                case HasExpr has:
                    return Has(has);
                case InterfaceMethodExpr method:
                {
                    VReg d = fn.NewAddress();
                    Emit(new InterfaceMethodI { Dst = d, Receiver = InReg(Value(method.Receiver), Addr), Interface = method.Interface, Index = method.Index });
                    return d;
                }
                case NameExpr { Symbol: ConstantSymbol k }:
                    return Opnd.Const(k.Value);
                case SizeofExpr size:
                    return Opnd.Const(size.Value);
                case DecimalExpr dec when IsF(e.Type):
                    return FloatConst(dec.Value);
                case NameExpr { Symbol: FloatConstantSymbol fk } when IsF(e.Type):
                    return FloatConst(fk.Value);
                case NameExpr { Symbol: VariableSymbol v } when variables.ContainsKey(v) || method.Locals.Contains(v) || method.Parameters.Contains(v):
                    return Variable(v);
                case NameExpr { Symbol: VariableSymbol kept } when !heldInMemory && held.TryGetValue(kept, out VReg? inRegister):
                    return inRegister;
                case NameExpr { Symbol: VariableSymbol g } when Global(g) is string place:
                {
                    VReg loaded = New(g.Type, g.Name);
                    Emit(new LoadI { Dst = loaded, Place = place, Kind = KindOf(g.Type) });
                    return loaded;
                }
                case CastExpr cast:
                    Need(Supported(Real(cast.Operand)), $"a cast from {Real(cast.Operand)}");
                    return Cast(Value(cast.Operand), Real(cast.Operand), cast.Type);
                case Expr field when Field(field) is var (fb, fo, ft) && fb != null:
                {
                    VReg d = New(ft);
                    Emit(new LoadIdxI { Dst = d, Base = fb, Index = Opnd.Const(fo), Scale = 1, Kind = KindOf(ft) });
                    return d;
                }
                case NameExpr { Symbol: FieldPathSymbol ff } when frameStructs.ContainsKey(ff.Root) && ff.Type.IsInlineText:
                {
                    // text kept in place: its value is where it is
                    VReg d = fn.NewAddress();
                    Emit(new AddrI { Dst = d, Label = FramePlace(ff.Root, ff.Offset) });
                    return d;
                }
                case NameExpr { Symbol: FieldPathSymbol ff } when frameStructs.ContainsKey(ff.Root) && Supported(ff.Type) && !ff.Type.IsObject:
                {
                    VReg d = New(ff.Type);
                    Emit(new LoadI { Dst = d, Place = FramePlace(ff.Root, ff.Offset), Kind = KindOf(ff.Type) });
                    return d;
                }
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: NameExpr { Symbol: VariableSymbol { Type.IsString: true, Type.IsArray: false } } text } addrText when CanText(text):
                    // addr s: where its text is (now, for one that grows: '' while it has none)
                    return TextAddress(text);
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: var viewed } when viewed.Type.Open
                        && (viewed is NameExpr { Symbol: VariableSymbol vv } && (paramViews.ContainsKey(vv) || GrowingView(vv)) || Binder.IsFieldView(viewed) && CanAddress(viewed)):
                    // addr list: where its elements are
                    return LoadAt(ViewOf(viewed), 0, PhiType.PointerTo(viewed.Type.Element));
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: NameExpr { Symbol: VariableSymbol mine } } when addressed.Contains(mine):
                {
                    // where the variable's slot is: it lives there the whole method
                    VReg d = fn.NewAddress();
                    Emit(new AddrOfI { Dst = d, Of = Variable(mine) });
                    return d;
                }
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: NameExpr { Symbol: VariableSymbol whole } } when frameStructs.ContainsKey(whole):
                {
                    VReg d = fn.NewAddress();
                    Emit(new AddrI { Dst = d, Label = FramePlace(whole, 0) });
                    return d;
                }
                case NameExpr { Symbol: FieldPathSymbol gf } when GlobalStruct(gf.Root) is string label && Supported(gf.Type) && !gf.Type.IsObject:
                {
                    VReg d = New(gf.Type);
                    Emit(new LoadI { Dst = d, Place = $"{label} + {gf.Offset}", Kind = KindOf(gf.Type) });
                    return d;
                }
                case AddrExpr { CodeLabel: string code, Method: MethodSymbol, Foreign: false } when e.Type.IsMethod:
                {
                    // addr Method: where its code is, a method value
                    VReg d = fn.NewAddress();
                    Emit(new AddrI { Dst = d, Label = code });
                    return d;
                }
                case AddrExpr { OfObject: true, CodeLabel: null, Operand: var obj } when IsAddress(Real(obj)) && Supported(Real(obj)):
                    // (an object is at the address it holds: a struct method's self, called on one)
                    return Value(obj);
                case AddrExpr { OfObject: false, CodeLabel: null, Foreign: false, Operand: var place } anywhere
                    when anywhere.Type.IsPointer && place is not NameExpr { Symbol: VariableSymbol } && CanAddress(place):
                    // addr of a field (through objects, a pointer, self, a class's struct) or an element
                    return AddressOf(place);
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: NameExpr { Symbol: VariableSymbol g } } addr
                    when addr.Type.IsPointer && !g.Grows && !g.TextRef && !g.Type.Open && AnyGlobal(g) is string label:
                {
                    VReg d = fn.NewAddress();
                    Emit(new AddrI { Dst = d, Label = label });
                    return d;
                }
                case IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } inArray when ClassArray(arr) != null && Supported(inArray.Type):
                {
                    var (bas, index, scale) = ArrayElement(inArray, arr);
                    VReg d = New(inArray.Type);
                    Emit(new LoadIdxI { Dst = d, Base = bas, Index = index, Scale = scale, Kind = KindOf(inArray.Type) });
                    return d;
                }
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } inArray }
                    when ClassArray(arr) != null:
                {
                    // addr list:i: where the element is (checked as reading it would be)
                    var (bas, index, scale) = ArrayElement(inArray, arr);
                    VReg d = fn.NewAddress();
                    if (index.IsImm) Emit(new BinI { Op = BinOp.Add, Dst = d, A = bas, B = Opnd.Const(index.Imm * scale) });
                    else
                    {
                        VReg moved = fn.NewAddress();
                        Emit(new BinI { Op = BinOp.Mul, Dst = moved, A = index.Reg!, B = Opnd.Const(scale) });
                        Emit(new BinI { Op = BinOp.Add, Dst = d, A = bas, B = moved });
                    }
                    return d;
                }
                case IndexExpr viewed when InView(viewed):
                {
                    var (bas, index, scale) = ViewElement(viewed);
                    VReg d = New(viewed.Type);
                    Emit(new LoadIdxI { Dst = d, Base = bas, Index = index, Scale = scale, Kind = KindOf(viewed.Type) });
                    return d;
                }
                case IndexExpr placed when InPlace(placed):
                {
                    var (bas, index, scale) = PlacedElement(placed);
                    VReg d = New(placed.Type);
                    Emit(new LoadIdxI { Dst = d, Base = bas, Index = index, Scale = scale, Kind = KindOf(placed.Type) });
                    return d;
                }
                case IndexExpr { Target.Type.IsPointer: true } element:
                {
                    var (bas, index, scale) = Element(element);
                    VReg d = New(element.Type);
                    Emit(new LoadIdxI { Dst = d, Base = bas, Index = index, Scale = scale, Kind = KindOf(element.Type) });
                    return d;
                }
                case NewExpr n:
                {
                    // the heap's Alloc, given the bytes: what it gives is the address (0 if there's no room)
                    Need(n.Allocator != null, "a new with no allocator");
                    return Call(n.Allocator!, new List<Expr> { n.Bytes }, wantResult: true)!;
                }
                case CheckedNumberExpr check:
                {
                    // int to u32 or u32 to int: the top bit set is a number that would change
                    Need(check.Type == PhiType.U32 || check.Type == PhiType.Int, $"a check to {check.Type}");
                    VReg v = InReg(Convert(Value(check.Operand), Real(check.Operand), check.Type), false);
                    Emit(new SignCheckI
                    {
                        Value = v,
                        At = check,
                        Held = HeldStores(),
                        Text = check.Type == PhiType.U32 ? "a negative number can't be a u32" : "a u32 of 2 billion or more can't be an int",
                    });
                    return v;
                }
                case UnaryExpr { Op: UnaryOp.Not } fnot when IsF(Real(fnot.Operand)):
                {
                    VReg fd = fn.NewValue(false);
                    VReg fv = (VReg)Value(fnot.Operand).Reg!;
                    Emit(new SetCcI { Float = true, Cond = Cond.Eq, Dst = fd, A = fv, B = FloatConst(0.0) });
                    return fd;
                }
                case UnaryExpr { Op: UnaryOp.Negate } fneg when IsF(fneg.Type):
                {
                    VReg fa = (VReg)Convert(Value(fneg.Operand), Real(fneg.Operand), PhiType.F64).Reg!;
                    VReg fd = fn.NewFloat();
                    Emit(new FNegI { Dst = fd, A = fa });
                    return fd;
                }
                case UnaryExpr { Op: UnaryOp.Not } not:
                {
                    PhiType t = Real(not.Operand);
                    VReg a = InReg(Value(not.Operand), Is64(t));
                    VReg d = fn.NewValue(false);
                    Emit(new SetCcI { Cond = Cond.Eq, Dst = d, A = a, B = Opnd.Const(0) });
                    return d;
                }
                case UnaryExpr u:
                {
                    Need(!u.Type.IsPointer, "a pointer negated");
                    // (the operand worked out once; a number is negated or turned over now, as the
                    // instruction would at the width of the type)
                    Opnd operand = Convert(Value(u.Operand), Real(u.Operand), Arith(u.Type));
                    if (operand.IsImm)
                    {
                        long r = u.Op == UnaryOp.Negate ? unchecked(-operand.Imm) : ~operand.Imm;
                        return Convert(Opnd.Const(u.Type.IsWide ? r : Fit(r, Arith(u.Type))), Arith(u.Type), u.Type);
                    }
                    VReg a = InReg(operand, u.Type.IsWide);
                    VReg d = fn.NewValue(u.Type.IsWide);
                    Emit(new UnI { Op = u.Op == UnaryOp.Negate ? UnOp.Neg : UnOp.Not, Dst = d, A = a });
                    return Convert(d, Arith(u.Type), u.Type);
                }
                case BinaryExpr cmp when IsComparison(cmp.Op):
                {
                    var (a, b, cond, isFloat) = Compare(cmp);
                    VReg d = fn.NewValue(false);
                    Emit(new SetCcI { Float = isFloat, Cond = cond, Dst = d, A = a, B = b });
                    return d;
                }
                case BinaryExpr { Op: BinaryOp.And or BinaryOp.Or } logic:
                {
                    // 1 or 0, by the jumps a condition makes
                    VReg d = fn.NewValue(false);
                    Block yes = fn.NewBlock(), no = fn.NewBlock(), after = fn.NewBlock();
                    Condition(logic, yes, no);
                    Enter(yes);
                    Emit(new ConstI { Dst = d, Value = 1 });
                    StartBlock(after);
                    Enter(no);
                    Emit(new ConstI { Dst = d, Value = 0 });
                    StartBlock(after);
                    Enter(after);
                    return d;
                }
                case BinaryExpr b:
                    return Arithmetic(b);
            }
            throw new NotYet(Unknown(e));
        }

        /// <summary>Where an array field without a size keeps its count, and its room, after its elements' address.</summary>
        static int ViewCount => PhiType.AddressSize;
        static int ViewMax => PhiType.AddressSize + 4;

        /// <summary>A number of type t at an address plus an offset.</summary>
        VReg LoadAt(VReg address, int offset, PhiType t)
        {
            VReg d = New(t);
            Emit(new LoadIdxI { Dst = d, Base = address, Index = Opnd.Const(offset), Scale = 1, Kind = KindOf(t) });
            return d;
        }

        /// <summary>True when <see cref="AddressOf"/> takes the place (asking only, with nothing built).</summary>
        bool CanAddress(Expr e) => e switch
        {
            NameExpr { Symbol: VariableSymbol pv } when paramViews.ContainsKey(pv) => true,
            NameExpr { Symbol: VariableSymbol lv } when localViews.ContainsKey(lv) => true,
            NameExpr { Symbol: VariableSymbol pinned } when addressed.Contains(pinned) => true,
            NameExpr { Symbol: VariableSymbol v } => frameStructs.ContainsKey(v) || !Ours(v) && !v.Grows && !v.TextRef && !v.Type.Open && AnyGlobal(v) != null,
            NameExpr { Symbol: FieldPathSymbol f } => frameStructs.ContainsKey(f.Root) || GlobalStruct(f.Root) != null
                || !Ours(f.Root) && !f.Root.Grows && !f.Root.Type.Open && AnyGlobal(f.Root) != null,
            NameExpr { Symbol: PathSymbol p } => PathRoot(p) && p.Hops.Count > 0,
            NameExpr { Symbol: SelfFieldSymbol f } => Ours(f.Self),
            MemberExpr { Field: FieldSymbol, Call: null } m => IsAddress(Real(m.Target)) && Supported(Real(m.Target)) || InPlaceStruct(m.Target.Type) && CanAddress(m.Target),
            IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } when ClassArray(arr) != null => true,
            IndexExpr placed when InPlace(placed) => true,
            IndexExpr viewed when InView(viewed) => true,
            IndexExpr { Target.Type.IsString: true, Target.Type.IsArray: false } character => IsCharacter(character),
            IndexExpr { Target.Type.IsPointer: true } element => Reachable(element.Type),
            _ => false,
        };

        /// <summary>
        /// Where a place is, all 64 bits of it: a class's variable, a struct in the frame or a
        /// field of one, a field reached through objects, self or a pointer, an element of a
        /// class's array or through a pointer. Only for what <see cref="CanAddress"/> takes.
        /// </summary>
        VReg AddressOf(Expr e)
        {
            VReg d = fn.NewAddress();
            switch (e)
            {
                case NameExpr { Symbol: VariableSymbol v } when frameStructs.ContainsKey(v):
                    Emit(new AddrI { Dst = d, Label = FramePlace(v, 0) });
                    return d;
                case NameExpr { Symbol: VariableSymbol pinned } when addressed.Contains(pinned):
                    Emit(new AddrOfI { Dst = d, Of = Variable(pinned) });
                    return d;
                case NameExpr { Symbol: VariableSymbol lv } when localViews.TryGetValue(lv, out string? localAt):
                    Emit(new AddrI { Dst = d, Label = localAt });
                    return d;
                case NameExpr { Symbol: VariableSymbol pv } when paramViews.TryGetValue(pv, out string? viewAt):
                    Emit(new AddrI { Dst = d, Label = viewAt });
                    return d;
                case NameExpr { Symbol: VariableSymbol g }:
                    Emit(new AddrI { Dst = d, Label = AnyGlobal(g)! });
                    return d;
                case NameExpr { Symbol: FieldPathSymbol f } when frameStructs.ContainsKey(f.Root):
                    Emit(new AddrI { Dst = d, Label = FramePlace(f.Root, f.Offset) });
                    return d;
                case NameExpr { Symbol: FieldPathSymbol f }:
                    Emit(new AddrI { Dst = d, Label = $"{GlobalStruct(f.Root) ?? AnyGlobal(f.Root)} + {f.Offset}" });
                    return d;
                case NameExpr { Symbol: PathSymbol p }:
                {
                    VReg at = PathStart(p);
                    for (int k = 0; k + 1 < p.Hops.Count; k++)
                    {
                        VReg next = fn.NewAddress();
                        Emit(new LoadIdxI { Dst = next, Base = at, Index = Opnd.Const(p.Hops[k]), Scale = 1, Kind = Ptr });
                        at = next;
                    }
                    Emit(new BinI { Op = BinOp.Add, Dst = d, A = at, B = Opnd.Const(p.Hops[^1]) });
                    return d;
                }
                case NameExpr { Symbol: SelfFieldSymbol f }:
                    Emit(new BinI { Op = BinOp.Add, Dst = d, A = Variable(f.Self), B = Opnd.Const(f.Offset) });
                    return d;
                case MemberExpr { Field: FieldSymbol f } m:
                {
                    // through the object or pointer it is, or where the struct it's in is
                    VReg holder = IsAddress(Real(m.Target)) && Supported(Real(m.Target)) ? InReg(Value(m.Target), Addr) : AddressOf(m.Target);
                    Emit(new BinI { Op = BinOp.Add, Dst = d, A = holder, B = Opnd.Const(f.Offset) });
                    return d;
                }
                case IndexExpr { Target.Type.IsString: true, Target.Type.IsArray: false } character:
                {
                    var (text, at) = CharacterElement(character);
                    return Offset(text, at, 1, d);
                }
                case IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } inArray when ClassArray(arr) != null:
                {
                    var (bas, index, scale) = ArrayElement(inArray, arr);
                    return Offset(bas, index, scale, d);
                }
                case IndexExpr placed when InPlace(placed):
                {
                    var (bas, index, scale) = PlacedElement(placed);
                    return Offset(bas, index, scale, d);
                }
                case IndexExpr viewed when InView(viewed):
                {
                    var (bas, index, scale) = ViewElement(viewed);
                    return Offset(bas, index, scale, d);
                }
                case IndexExpr element:
                {
                    var (bas, index, scale) = Element(element);
                    return Offset(bas, index, scale, d);
                }
            }
            throw new NotYet($"the address of {Unknown(e)}");
        }

        /// <summary>
        /// list:i of an array of a size kept somewhere this form reaches (the frame, a field
        /// through an object, self or a pointer, a class's struct), not a class's own array
        /// (ArrayElement's): its index checked against its length as any element's is.
        /// </summary>
        /// <summary>Inside unsafe: no index checked.</summary>
        int unsafeDepth;

        /// <summary>
        /// items:i of a struct's array without a size (a container's elements): its index checked
        /// against how many it has, when it runs, and the element where its elements' address says.
        /// </summary>
        bool InView(IndexExpr e) =>
            e.Target.Type is { Open: true, TextTable: false } && e.Target.Type.Kind != TypeKind.Str
            && (Binder.IsFieldView(e.Target) && CanAddress(e.Target)
                || e.Target is NameExpr { Symbol: VariableSymbol g } && GrowingView(g)
                || e.Target is NameExpr { Symbol: VariableSymbol pv } && paramViews.ContainsKey(pv))
            && Reachable(e.Type);

        (VReg Base, Opnd Index, int Scale) ViewElement(IndexExpr e)
        {
            int scale = e.Type.ElementSize;
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, IndexType(it.IsSigned));
            VReg view = ViewOf(e.Target);
            if (unsafeDepth == 0)
            {
                VReg count = LoadAt(view, ViewCount, PhiType.U32);
                VReg limit = fn.NewAddress();
                Emit(new ExtendI { Dst = limit, Src = count, Signed = false });
                VReg checkedIndex = InReg(index, Addr);
                Emit(new CheckI { Index = checkedIndex, LimitValue = limit, At = e, Held = HeldStores() });
                index = checkedIndex;
            }
            VReg elements = LoadAt(view, 0, PhiType.PointerTo(e.Type));
            if (index.IsImm && (index.Imm * scale < int.MinValue || index.Imm * scale > int.MaxValue)) index = InReg(index, Addr);
            return (elements, index, scale);
        }

        bool InPlace(IndexExpr e) =>
            FixedArray(e.Target.Type) && e.Target is not NameExpr { Symbol: VariableSymbol { IsLocal: false, IsParameter: false } } && CanAddress(e.Target);

        (VReg Base, Opnd Index, int Scale) PlacedElement(IndexExpr e)
        {
            int scale = e.Type.ElementSize;
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, IndexType(it.IsSigned));
            if (e.CheckLimit is int limit && !(index.IsImm && index.Imm >= 0 && index.Imm < limit) && !MaskedBelow(e.Index, limit))
            {
                VReg checkedIndex = InReg(index, Addr);
                Emit(new CheckI { Index = checkedIndex, Limit = limit, At = e, Held = HeldStores() });
                index = checkedIndex;
            }
            VReg bas = AddressOf(e.Target);
            if (index.IsImm && (index.Imm * scale < int.MinValue || index.Imm * scale > int.MaxValue)) index = InReg(index, Addr);
            return (bas, index, scale);
        }

        /// <summary>d = base + index * scale.</summary>
        /// <summary>How many an array has, as the generator's EmitArrayCount: its size, or the count it keeps.</summary>
        bool CanCount(Expr array) => !array.Type.Open && !array.Type.Filled || CanAddress(array)
            || array.Type.Open && array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g);

        Opnd ArrayCount(Expr array)
        {
            if (!array.Type.Open && !array.Type.Filled) return Opnd.Const(array.Type.Length);
            if (array.Type.Open) return LoadAt(ViewOf(array), ViewCount, PhiType.Int);
            return LoadAt(AddressOf(array), array.Type.CountOffset, PhiType.Int);
        }

        /// <summary>while each's test: on to body while i is under the count, else after.</summary>
        void EachTest(Expr array, VReg i, Block body, Block after)
        {
            Opnd count = ArrayCount(array);
            if (current.End != null) Enter(fn.NewBlock());
            current.End = new BranchT { Cond = Cond.LtU, A = i, B = count, Then = body, Else = after };
        }

        /// <summary>A path's start (a.b.c's a): the method's own, or a class's variable (read where it is, or where a loop holds it).</summary>
        bool RootReachable(VariableSymbol root) => Ours(root) || Global(root) != null;

        /// <summary>
        /// A path (a.b.c) this form follows: from a pointer or object (the method's own, or a class's),
        /// or from a struct kept in memory (a class's, or in the frame) whose field at BaseOffset is the first address.
        /// </summary>
        bool PathRoot(PathSymbol p) => p.BaseOffset == 0 && RootReachable(p.Root) && IsAddress(p.Root.Type)
            || p.BaseOffset != 0 && (frameStructs.ContainsKey(p.Root) || !Ours(p.Root) && AnyGlobal(p.Root) != null);

        /// <summary>The path's first address: the root's value, or what's at the root's place + BaseOffset (the generator's).</summary>
        VReg PathStart(PathSymbol p)
        {
            if (p.BaseOffset == 0 && RootReachable(p.Root) && IsAddress(p.Root.Type)) return RootValue(p.Root);
            VReg place = fn.NewAddress();
            Emit(new AddrI { Dst = place, Label = frameStructs.ContainsKey(p.Root) ? FramePlace(p.Root, 0) : AnyGlobal(p.Root)! });
            return LoadAt(place, p.BaseOffset, PhiType.PointerTo(PhiType.U8));
        }

        VReg RootValue(VariableSymbol root) => Ours(root) ? Variable(root) : InReg(Value(new NameExpr { Name = root.Name, Symbol = root, Type = root.Type }), Addr);

        /// <summary>A character of a str this form reads (and whose address it works out): one that grows, or with a room it's checked against.</summary>
        bool IsCharacter(IndexExpr c) => c.Target.Type.IsString && !c.Target.Type.IsArray
            && (CanSlot(c.Target)
                || (c.CheckLimit != null || unsafeDepth > 0
                    || ConstantFolder.Evaluate(c.Index) != null && !c.Target.Type.IsHeapText && c.Target is not NameExpr { Symbol: VariableSymbol { Grows: true } })
                   && CanText(c.Target));

        /// <summary>
        /// A character's text and index, the index checked: against the room of a str that grows
        /// (which changes, so read from its slot), or the room a str with a size has.
        /// </summary>
        (VReg Text, Opnd Index) CharacterElement(IndexExpr c)
        {
            PhiType it = Real(c.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            if (CanSlot(c.Target))
            {
                Opnd grownIndex = InReg(Convert(Value(c.Index), it, IndexType(it.IsSigned)), Addr);
                VReg slot = SlotOf(c.Target);
                if (unsafeDepth == 0)
                {
                    VReg room = LoadAt(slot, PhiType.AddressSize, PhiType.U32);
                    VReg masked = fn.NewValue(false), limit = fn.NewAddress();
                    Emit(new BinI { Op = BinOp.And, Dst = masked, A = room, B = Opnd.Const(0x3FFFFFFF) });
                    Emit(new ExtendI { Dst = limit, Src = masked, Signed = false });
                    Emit(new CheckI { Index = grownIndex.Reg!, LimitValue = limit, At = c, Held = HeldStores() });
                }
                return (LoadAt(slot, 0, PhiType.PointerTo(PhiType.U8)), grownIndex);
            }
            Opnd index = Convert(Value(c.Index), it, IndexType(it.IsSigned));
            if (c.CheckLimit is int limitBytes && !(index.IsImm && index.Imm >= 0 && index.Imm < limitBytes))
            {
                VReg checkedIndex = InReg(index, Addr);
                Emit(new CheckI { Index = checkedIndex, Limit = limitBytes, At = c, Held = HeldStores() });
                index = checkedIndex;
            }
            return (TextAddress(c.Target), index);
        }

        VReg Offset(VReg bas, Opnd index, int scale, VReg d)
        {
            if (index.IsImm)
            {
                Emit(new BinI { Op = BinOp.Add, Dst = d, A = bas, B = Small(Opnd.Const(index.Imm * scale), true) });
                return d;
            }
            VReg moved = fn.NewAddress();
            Emit(new BinI { Op = BinOp.Mul, Dst = moved, A = index.Reg!, B = Opnd.Const(scale) });
            Emit(new BinI { Op = BinOp.Add, Dst = d, A = bas, B = moved });
            return d;
        }

        /// <summary>What a value is, for PHI_IR_TRACE when it isn't taken: its kind, and what it names or reaches.</summary>
        static string Unknown(Expr e) => e switch
        {
            AddrExpr a => $"addr of {Unknown(a.Operand)}",
            NameExpr { Symbol: Symbol s } n => $"a NameExpr ({s.GetType().Name.Replace("Symbol", "")}{(s is VariableSymbol v ? $", {(v.IsLocal || v.IsParameter ? "the method's" : "a class's")} {v.Type}" : s is LengthSymbol l ? (l.Filled != null ? ", an atmost array's count" : l.Text != null ? ", text's length" : l.Str != null ? ", a str's length" : l.Open != null ? ", an array parameter's" : "") : "")} '{n.Name}')",
            MemberExpr m => $"a MemberExpr (.{m.Member} of {m.Target.Type}{(m.FilledLength ? ", an atmost array's count" : m.TextLength ? ", text's length" : "")})",
            IndexExpr i => $"an IndexExpr ({i.Target.Type}:{i.Index.Type}, of a {i.Target.GetType().Name})",
            _ => $"a {e.GetType().Name}",
        };

        /// <summary>The type arithmetic on t is done in: 32 bits (an int) for what's smaller.</summary>
        static PhiType Arith(PhiType t) => t.IsWide ? t : t.IsUnsigned32 ? PhiType.U32 : t.Kind is TypeKind.U32 ? PhiType.U32 : PhiType.I32;

        Opnd Arithmetic(BinaryExpr b)
        {
            Need(b.Op != BinaryOp.Power, "a power");
            if (IsF(b.Type))
            {
                FOp fop = b.Op switch
                {
                    BinaryOp.Add => FOp.Add,
                    BinaryOp.Subtract => FOp.Sub,
                    BinaryOp.Multiply => FOp.Mul,
                    BinaryOp.Divide => FOp.Div,
                    BinaryOp.Modulo => FOp.Add,     // (worked out below, by the FPU)
                    _ => throw new NotYet($"the operator {b.Op} on floats"),
                };
                if (b.Op == BinaryOp.Modulo)
                {
                    // what's left over, with the left's sign (like C's fmod), as the generator: fprem
                    VReg dividend = (VReg)Convert(Value(b.Left), Real(b.Left), PhiType.F64).Reg!;
                    VReg divisor = (VReg)Convert(Value(b.Right), Real(b.Right), PhiType.F64).Reg!;
                    VReg rem = fn.NewFloat();
                    Emit(new X87I { Dst = rem, A = dividend, B = divisor });
                    return rem;
                }
                // (the left side first, as everywhere)
                VReg fa = (VReg)Convert(Value(b.Left), Real(b.Left), PhiType.F64).Reg!;
                VReg fb = (VReg)Convert(Value(b.Right), Real(b.Right), PhiType.F64).Reg!;
                VReg fd = fn.NewFloat();
                Emit(new FBinI { Op = fop, Dst = fd, A = fa, B = fb });
                return fd;
            }
            if (b.Op == BinaryOp.Subtract && Real(b.Left).IsPointer && Real(b.Right).IsPointer)
            {
                // p - q: how far apart they are, a number (the binder's type), from all 64 bits of each
                VReg pd = fn.NewAddress();
                VReg pl = InReg(Value(b.Left), Addr);
                Opnd q = Value(b.Right);
                Emit(new BinI { Op = BinOp.Sub, Dst = pd, A = pl, B = q });
                return Convert(pd, PhiType.I64, b.Type);
            }
            Need(!Real(b.Left).IsPointer && !Real(b.Right).IsPointer, "arithmetic on a pointer that isn't moving it");
            PhiType t = b.Type;
            bool wide = t.IsWide;
            bool unsigned = wide ? t.Kind == TypeKind.U64 : t.IsUnsigned32;
            BinOp op = b.Op switch
            {
                BinaryOp.Add => BinOp.Add,
                BinaryOp.Subtract => BinOp.Sub,
                BinaryOp.Multiply => BinOp.Mul,
                BinaryOp.BitAnd => BinOp.And,
                BinaryOp.BitOr => BinOp.Or,
                BinaryOp.BitXor => BinOp.Xor,
                BinaryOp.ShiftLeft => BinOp.Shl,
                BinaryOp.ShiftRight => unsigned ? BinOp.Shr : BinOp.Sar,
                BinaryOp.Divide => unsigned ? BinOp.DivU : BinOp.DivS,
                BinaryOp.Modulo => unsigned ? BinOp.RemU : BinOp.RemS,
                _ => throw new NotYet($"the operator {b.Op}"),
            };
            // the left side is worked out first, then the right, as PHI says (which a program
            // sees when a side calls a method that changes what the other reads; see
            // EvaluationOrder). A shift's count is a number of places, at whatever width it's written.
            Opnd left = Convert(Value(b.Left), Real(b.Left), t);
            Opnd right = op is BinOp.Shl or BinOp.Shr or BinOp.Sar
                ? Convert(Value(b.Right), Real(b.Right), b.Right.Type.IsWide ? b.Right.Type : PhiType.I32)
                : Convert(Value(b.Right), Real(b.Right), t);
            if (left.IsImm && right.IsImm && Fold(op, left.Imm, right.Imm, wide, unsigned) is long folded)
                return Opnd.Const(folded);
            if (op is BinOp.Shl or BinOp.Shr or BinOp.Sar && right.Reg is { Wide: true } wideCount)
            {
                VReg low = fn.NewValue(false);
                Emit(new NarrowI { Dst = low, Src = wideCount, Kind = MemKind.D32 });
                right = low;
            }
            VReg d = fn.NewValue(wide);
            Emit(new BinI { Op = op, Dst = d, A = InReg(left, wide), B = Small(right, wide) });
            return d;
        }

        /// <summary>
        /// a op b with both known when it's built: worked out as the instruction would work it
        /// out (32 bits wrap, a shift's count is cut to 5 or 6 bits), or null to leave it to the
        /// program (a division by zero is the CPU's error when it runs, as the generator's is).
        /// </summary>
        static long? Fold(BinOp op, long a, long b, bool wide, bool unsigned)
        {
            if (wide)
            {
                ulong ua = unchecked((ulong)a), ub = unchecked((ulong)b);
                return op switch
                {
                    BinOp.Add => unchecked(a + b),
                    BinOp.Sub => unchecked(a - b),
                    BinOp.Mul => unchecked(a * b),
                    BinOp.And => a & b,
                    BinOp.Or => a | b,
                    BinOp.Xor => a ^ b,
                    BinOp.Shl => a << (int)(b & 63),
                    BinOp.Shr => unchecked((long)(ua >> (int)(b & 63))),
                    BinOp.Sar => a >> (int)(b & 63),
                    BinOp.DivS when b != 0 && !(a == long.MinValue && b == -1) => a / b,
                    BinOp.RemS when b != 0 && !(a == long.MinValue && b == -1) => a % b,
                    BinOp.DivU when b != 0 => unchecked((long)(ua / ub)),
                    BinOp.RemU when b != 0 => unchecked((long)(ua % ub)),
                    _ => null,
                };
            }
            int x = unchecked((int)a), y = unchecked((int)b);
            uint ux = unchecked((uint)a), uy = unchecked((uint)b);
            long? r = op switch
            {
                BinOp.Add => unchecked(x + y),
                BinOp.Sub => unchecked(x - y),
                BinOp.Mul => unchecked(x * y),
                BinOp.And => x & y,
                BinOp.Or => x | y,
                BinOp.Xor => x ^ y,
                BinOp.Shl => x << (y & 31),
                BinOp.Shr => unchecked((int)(ux >> (y & 31))),
                BinOp.Sar => x >> (y & 31),
                BinOp.DivS when y != 0 && !(x == int.MinValue && y == -1) => x / y,
                BinOp.RemS when y != 0 && !(x == int.MinValue && y == -1) => x % y,
                BinOp.DivU when uy != 0 => unchecked((int)(ux / uy)),
                BinOp.RemU when uy != 0 => unchecked((int)(ux % uy)),
                _ => null,
            };
            // (kept as the type keeps it: an unsigned 32-bit number is never negative)
            return r == null ? null : unsigned ? (long)unchecked((uint)r.Value) : r;
        }

        /// <summary>p + n or p - n: the pointer moved by n bytes (n with its own sign), all 64 bits.</summary>
        Opnd MovePointer(BinaryExpr b)
        {
            bool leftIsPointer = Real(b.Left).IsPointer;
            Expr pointer = leftIsPointer ? b.Left : b.Right, number = leftIsPointer ? b.Right : b.Left;
            Need(Supported(Real(number)), $"a pointer moved by a {Real(number)}");
            Need(leftIsPointer || b.Op == BinaryOp.Add, "a number minus a pointer");
            // (the left side first, as everywhere)
            Opnd n = default;
            VReg p = null!;
            if (leftIsPointer)
            {
                p = InReg(Value(pointer), Addr);
                n = Small(Convert(Value(number), Real(number), IndexType(Real(number).IsSigned)), true);
            }
            else
            {
                n = Small(Convert(Value(number), Real(number), IndexType(Real(number).IsSigned)), true);
                p = InReg(Value(pointer), Addr);
            }
            VReg d = fn.NewAddress();
            Emit(new BinI { Op = b.Op == BinaryOp.Add ? BinOp.Add : BinOp.Sub, Dst = d, A = p, B = n });
            return d;
        }

        /// <summary>p:i on a pointer: its address, the index made 64 bits with its own sign, and the element's size.</summary>
        (VReg Base, Opnd Index, int Scale) Element(IndexExpr e)
        {
            PhiType element = e.Type;
            Need(Reachable(element), $"a pointer to a {element}");
            int scale = element.ElementSize;
            VReg bas = InReg(Value(e.Target), Addr);
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer, $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, IndexType(it.IsSigned));
            // (a number goes in the address itself: it must fit 32 bits there, times the size)
            if (index.IsImm && (index.Imm * scale < int.MinValue || index.Imm * scale > int.MaxValue)) index = InReg(index, Addr);
            return (bas, index, scale);
        }

        /// <summary>x as T: made wider with its own sign, or cut to T's bits.</summary>
        Opnd Cast(Opnd v, PhiType from, PhiType to)
        {
            Need(Supported(to), $"a cast to {to}");
            if (IsF(from) && !IsF(to))
            {
                // toward zero, all 64 bits (as the generator's fistp qword), then what fits
                Need(!to.IsPointer && !to.IsObject && to.Kind != TypeKind.Bool, $"a float made a {to}");
                VReg whole = fn.NewAddress();
                Emit(new ToWholeI { Dst = whole, Src = v.Reg! });
                return to.IsWide ? whole : Convert(Cast(whole, PhiType.I64, PhiType.I32), PhiType.I32, to);
            }
            if (IsF(to)) return Convert(v, from, to);
            Need(to.Kind != TypeKind.Bool || from.Kind == TypeKind.Bool, "a cast to bln");
            if (Is64(from) && !Is64(to))
            {
                if (v.IsImm) return Opnd.Const(Fit(v.Imm, to));
                VReg low = fn.NewValue(false);
                Emit(new NarrowI { Dst = low, Src = v.Reg!, Kind = MemKind.D32 });
                return Convert(low, PhiType.U32, to);
            }
            return Convert(v, from, to);
        }

        /// <summary>
        /// A value of type from as one of type to: made 64 bits with its sign (or zeros), cut to
        /// 32, or to a small type's bits, as storing it in a variable of that type would.
        /// </summary>
        Opnd Convert(Opnd v, PhiType from, PhiType to)
        {
            if (IsF(to))
            {
                if (IsF(from)) return v;
                Need(!from.IsPointer && !from.IsObject && Supported(from), $"a {from} made a float");
                if (v.IsImm) return FloatConst(from.Kind == TypeKind.U64 ? (double)unchecked((ulong)v.Imm) : (double)v.Imm);
                // (a small number is an int already; a u32 is made 64 bits with zeros first)
                VReg src = v.Reg!;
                bool signed = from.Kind != TypeKind.U64;
                if (!from.IsWide && from.IsUnsigned32)
                {
                    VReg wide = fn.NewAddress();
                    Emit(new ExtendI { Dst = wide, Src = src, Signed = false });
                    src = wide;
                }
                VReg fd = fn.NewFloat();
                Emit(new ToFloatI { Dst = fd, Src = src, Signed = signed });
                return fd;
            }
            Need(!IsF(from), $"an f64 made a {to} without `as`");
            if (Is64(to))
            {
                if (Is64(from) || v.IsImm) return v;
                // (a number put in a pointer keeps its 32 bits as they are, as the generator's
                // does; one made 64 bits keeps its sign)
                VReg d = fn.NewAddress();
                Emit(new ExtendI { Dst = d, Src = v.Reg!, Signed = from.IsSigned && !to.IsPointer });
                return d;
            }
            if (Is64(from))
            {
                if (v.IsImm) return Opnd.Const(Fit(v.Imm, to));
                VReg low = fn.NewValue(false);
                Emit(new NarrowI { Dst = low, Src = v.Reg!, Kind = MemKind.D32 });
                v = low;
                from = PhiType.U32;
            }
            MemKind kind = KindOf(to);
            if (kind == MemKind.D32 || KindOf(from) == kind || Fits(from, kind)) return v.IsImm ? Opnd.Const(Fit(v.Imm, to)) : v;
            if (v.IsImm) return Opnd.Const(Fit(v.Imm, to));
            VReg n = fn.NewValue(false);
            Emit(new NarrowI { Dst = n, Src = v.Reg!, Kind = kind });
            return n;
        }

        /// <summary>Is every value of from already as kind keeps it (a u8 is a u16 as it is)?</summary>
        static bool Fits(PhiType from, MemKind kind) => (KindOf(from), kind) switch
        {
            (MemKind.U8, MemKind.U16 or MemKind.S16) => true,
            (MemKind.S8, MemKind.S16) => true,
            _ => false,
        };

        /// <summary>A number as a value of type t would hold it.</summary>
        static long Fit(long value, PhiType t) => KindOf(t) switch
        {
            MemKind.U8 => (byte)value,
            MemKind.S8 => (sbyte)value,
            MemKind.U16 => (ushort)value,
            MemKind.S16 => (short)value,
            MemKind.D32 => t.IsSigned ? (int)value : (uint)value,
            _ => value,
        };

        /// <summary>
        /// An f64 written in the program: made once, where the method starts (so a loop doesn't
        /// make it each time round), and the same value for each place it's written.
        /// </summary>
        VReg FloatConst(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            if (!floatConstants.TryGetValue(bits, out VReg? d))
            {
                d = fn.NewFloat($"{value}");
                fn.Blocks[0].Instrs.Insert(0, new FConstI { Dst = d, Value = value });
                floatConstants[bits] = d;
            }
            return d;
        }

        readonly Dictionary<long, VReg> floatConstants = new();

        /// <summary>v in a register: a number written into the instruction is put in one first.</summary>
        VReg InReg(Opnd v, bool wide)
        {
            if (!v.IsImm) return v.Reg!;
            VReg d = fn.NewValue(wide);
            Emit(new ConstI { Dst = d, Value = v.Imm });
            return d;
        }

        /// <summary>v as an operand an instruction can have: a 64-bit number only if it fits 32 bits with its sign.</summary>
        Opnd Small(Opnd v, bool wide) => v.IsImm && wide && (v.Imm < int.MinValue || v.Imm > int.MaxValue) ? InReg(v, Addr) : v;

        // ---------------------------------------------------------------- calls

        /// <summary>sys.Name: a system call, its arguments worked out first to last.</summary>
        VReg? SystemCall(SysCallDecl sys, List<Expr> arguments, bool wantResult)
        {
            Need(arguments.Count <= 6, "a system call with more than six arguments");
            var call = new SysCallI
            {
                Number = sys.Number,
                Name = sys.Name,
                Dst = wantResult ? New(sys.ResultType ?? PhiType.Int) : null,
            };
            for (int k = 0; k < arguments.Count; k++)
            {
                PhiType t = k < sys.ParameterTypes.Count ? sys.ParameterTypes[k] : Real(arguments[k]);
                Need(Supported(Real(arguments[k])) && !IsF(t) && !t.IsVec && !t.IsObject || arguments[k].Type.IsString, $"a system call given {A(Real(arguments[k]))}");
                call.Args.Add(Value(arguments[k], t.IsString ? t : Supported(t) ? t : Real(arguments[k])));
            }
            Emit(call);
            return call.Dst;
        }

        Opnd CallValue(CallExpr call)
        {
            if (call.Resolved is SysCallDecl sys) return SystemCall(sys, call.Arguments, wantResult: true)!;
            if (call.Resolved is VecMethod vm) return VecCall(call, vm);
            if (call.Resolved is BuiltinFunction builtin) return CallBuiltin(builtin, call.Arguments, wantResult: true)!;
            if (call.Resolved is PointerCall through) return CallThrough(through, call.Arguments, wantResult: true)!;
            // (a struct's method has the struct it's called on among its arguments already: self)
            Need(call.Resolved is MethodSymbol m && (call.CalleeTarget == null || m.Decl.Owner.ForStruct != null), "a call of a built-in or a method value");
            var callee = (MethodSymbol)call.Resolved!;
            return Call(callee, call.Arguments, wantResult: true)!;
        }

        /// <summary>
        /// A call through a method value (handler: x;): the arguments pushed as for a call by name,
        /// then the address the value holds worked out, and called (the generator's order).
        /// </summary>
        VReg? CallThrough(PointerCall through, List<Expr> arguments, bool wantResult)
        {
            MethodSignature s = through.Signature;
            // (an object is given as its address, as the generator pushes it)
            Need(s.Parameters.All(p => Supported(p) || p.IsString && !p.IsArray), "a call through a method value that takes what this form doesn't");
            bool givesText = s.Result.IsString && !s.Result.IsArray;
            Need(s.Result.Kind == TypeKind.Void || Supported(s.Result) && !s.Result.IsObject || givesText, "a call through a method value that gives what this form doesn't");
            Need(arguments.Count == s.Parameters.Count, "a call through a method value that leaves out arguments");
            var values = new List<Opnd>();
            // (a str to write, str x;: where the caller's str is, as a call by name gives it)
            for (int i = 0; i < arguments.Count; i++) values.Add(s.Into[i] ? TextRefArg(arguments[i]) : Value(arguments[i], s.Parameters[i]));
            VReg target = InReg(Value(through.Target), Addr);
            var call = new CallI
            {
                Target = target,
                Dst = wantResult && s.Result.Kind != TypeKind.Void ? New(s.Result) : null,
                Result = s.Result.Kind == TypeKind.Void ? CallResult.None : s.Result.IsWide ? CallResult.EdxEax
                    : s.Result.IsVec ? CallResult.Xmm0
                    : IsF(s.Result) ? CallResult.St0 : Is64(s.Result) ? CallResult.Rax : CallResult.Eax,
            };
            call.Args.AddRange(values);
            call.ArgKinds.AddRange(s.Parameters.Select(p => p.IsString ? Ptr : KindOf(p)));
            if (givesText)
            {
                // where its str goes, pushed last: a slot of this method's that grows, as the generator's
                fn.NewHome();
                fn.NewHome();
                string place = $"rbp - {8 * fn.Homes}";
                fn.OwnedTexts.Add(place);
                entry.Instrs.Insert(0, new ZeroI { Place = place, Size = 16 });
                VReg where = fn.NewAddress();
                Emit(new AddrI { Dst = where, Label = place });
                call.Args.Add(where);
                call.ArgKinds.Add(Ptr);
            }
            Need(!wantResult || call.Dst != null, "a method value that gives nothing, used for a value");
            Emit(call);
            return call.Dst;
        }

        /// <summary>
        /// A built-in that's one of the library's routines (OS.Grow): as the generator calls one,
        /// its arguments worked out and pushed from the last to the first, 8 bytes each, and
        /// what it gives in eax (rax for an address).
        /// </summary>
        VReg? CallBuiltin(BuiltinFunction b, List<Expr> arguments, bool wantResult)
        {
            if (b.FloatInstruction != null)
            {
                // Float.Sqrt and the rest: the FPU's own instruction, as the generator has it
                Need(arguments.Count == 1, $"a call of {b.Name} without its value");
                VReg x = (VReg)Convert(Value(arguments[0]), Real(arguments[0]), PhiType.F64).Reg!;
                VReg r = fn.NewFloat();
                Emit(new X87I { Dst = r, A = x, Instruction = b.FloatInstruction, Rounding = b.FloatRounding });
                return wantResult ? r : null;
            }
            var values = new Opnd[arguments.Count];
            for (int k = arguments.Count - 1; k >= 0; k--)
            {
                PhiType t = Real(arguments[k]);
                Need(Supported(t) && !IsF(t) && !t.IsVec && !t.IsObject, $"a call of {b.Name} given {A(t)}");
                values[k] = Small(Value(arguments[k]), true);
            }
            var call = new CallI
            {
                Label = b.Label,
                Routine = true,
                Dst = wantResult && b.Returns ? New(b.ResultType) : null,
                Result = !b.Returns ? CallResult.None : Is64(b.ResultType) ? CallResult.Rax : CallResult.Eax,
            };
            for (int k = arguments.Count - 1; k >= 0; k--)
            {
                call.Args.Add(values[k]);
                call.ArgKinds.Add(Ptr);
            }
            Need(!wantResult || call.Dst != null, $"{b.Name} gives nothing to use");
            Emit(call);
            return call.Dst;
        }

        VReg? Call(MethodSymbol callee, List<Expr> arguments, bool wantResult)
        {
            Need(!callee.IsHook && !callee.IsInterruptHandler, "a call of an event");
            Need(callee.Parameters.All(p => p.Type.IsString && !p.Type.IsArray || ArrayParameter(p)
                                           || Supported(p.Type) && !p.Grows && !p.TextRef && !(p.Type.IsObject && p.Ownership == Ownership.Owned)),
                 $"a call of {callee.Name}, which takes what this form doesn't");
            Need(callee.ReturnType.Kind == TypeKind.Void || Supported(callee.ReturnType) || GivesText(callee), $"a call of {callee.Name}, which gives a {callee.ReturnType}");
            Need(arguments.Count <= callee.Parameters.Count, $"a call of {callee.Name} with more arguments than it takes");
            var call = new CallI
            {
                Label = callee.Label,
                Dst = wantResult && callee.ReturnType.Kind != TypeKind.Void ? New(callee.ReturnType) : null,
                Result = callee.ReturnType.Kind == TypeKind.Void ? CallResult.None : callee.ReturnType.IsWide ? CallResult.EdxEax
                    : callee.ReturnType.IsVec ? CallResult.Xmm0
                    : IsF(callee.ReturnType) ? CallResult.St0 : Is64(callee.ReturnType) ? CallResult.Rax : CallResult.Eax,
            };
            // (one left out is the parameter's default, worked out here, or zero, as the generator does)
            for (int i = 0; i < callee.Parameters.Count; i++)
            {
                PhiType pt = callee.Parameters[i].Type;
                Expr? given = i < arguments.Count ? arguments[i] : callee.Parameters[i].Decl.Values.FirstOrDefault();
                if (ArrayParameter(callee.Parameters[i]))
                {
                    // a view of the array: how many and the most together (pushed first), then its elements
                    var (both, elements) = ArrayArg(given);
                    call.Args.Add(both);
                    call.ArgKinds.Add(Ptr);
                    call.Args.Add(elements);
                    call.ArgKinds.Add(Ptr);
                    continue;
                }
                if (callee.Parameters[i].TextRef)
                {
                    // where the caller's str is (it may be written)
                    Need(given != null, $"a call of {callee.Name} without its str");
                    call.Args.Add(TextRefArg(given!));
                    call.ArgKinds.Add(Ptr);
                    continue;
                }
                call.Args.Add(given != null ? Value(given, pt) : pt.IsString ? TextValue(new StringExpr { Value = "", Type = PhiType.Str })
                              : pt.IsVec ? ZeroVec() : IsF(pt) ? FloatConst(0.0) : Opnd.Const(0));
                call.ArgKinds.Add(pt.IsString ? Ptr : KindOf(pt));
            }
            if (GivesText(callee))
            {
                // where it puts its result, pushed last: a slot of this method's that grows (the
                // same one each time this call runs), or a buffer as big as its result can be
                string place;
                if (growResults)
                {
                    fn.NewHome();
                    fn.NewHome();
                    place = $"rbp - {8 * fn.Homes}";
                    fn.OwnedTexts.Add(place);
                    entry.Instrs.Insert(0, new ZeroI { Place = place, Size = 16 });
                }
                else
                {
                    for (int k = 0; k < (callee.ResultCapacity + 7) / 8; k++) fn.NewHome();
                    place = $"rbp - {8 * fn.Homes}";
                }
                VReg where = fn.NewAddress();
                Emit(new AddrI { Dst = where, Label = place });
                call.Args.Add(where);
                call.ArgKinds.Add(Ptr);
            }
            Need(!wantResult || call.Dst != null, $"{callee.Name} gives nothing to use");
            if (!wantResult && callee.ReturnType.IsObject)
            {
                // a new object nothing takes: it goes back at once
                VReg made = fn.NewAddress();
                var keep = new CallI { Label = call.Label, Dst = made, Result = CallResult.Rax };
                keep.Args.AddRange(call.Args);
                keep.ArgKinds.AddRange(call.ArgKinds);
                Emit(keep);
                Emit(new ObjCallI { Kind = "free", Struct = callee.ReturnType.Struct!, Arg = made });
                return null;
            }
            Emit(call);
            return call.Dst;
        }
    }
}

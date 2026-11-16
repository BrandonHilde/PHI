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

        IrBuilder(MethodSymbol m, Func<VariableSymbol, bool> holdable, bool catches)
        {
            this.holdable = holdable;
            this.catches = catches;
            method = m;
            fn = new IrFunction { Name = m.Name };
            current = fn.NewBlock();
            layout.Add(current);
            Need(m.ReturnType.Kind == TypeKind.Void || Supported(m.ReturnType), $"it gives {m.ReturnType}");
        }

        /// <summary>The method as the intermediate form, or null and why it isn't.</summary>
        public static IrFunction? Build(MethodSymbol m, Func<VariableSymbol, bool> holdable, bool catches, out string why)
        {
            why = "";
            try
            {
                var builder = new IrBuilder(m, holdable, catches);
                builder.BuildBody();
                return builder.fn;
            }
            catch (NotYet e)
            {
                why = e.Message;
                return null;
            }
        }

        static void Need(bool ok, string why)
        {
            if (!ok) throw new NotYet(why);
        }

        /// <summary>The kinds of value this form keeps: a whole number of any size, bln, chr, an enum, a pointer.</summary>
        public static bool Supported(PhiType t) =>
            !t.IsArray && t.Kind is TypeKind.I8 or TypeKind.U8 or TypeKind.I16 or TypeKind.U16 or TypeKind.I32 or TypeKind.U32
                or TypeKind.I64 or TypeKind.U64 or TypeKind.Bool or TypeKind.Chr or TypeKind.Enum or TypeKind.Pointer or TypeKind.Object
                or TypeKind.F64 or TypeKind.F32 or TypeKind.Vec;

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
        public static bool Is64(PhiType t) => t.IsWide || t.IsPointer || t.IsObject;

        /// <summary>How a value of type t is kept in memory.</summary>
        public static MemKind KindOf(PhiType t) => t.Kind switch
        {
            TypeKind.I8 => MemKind.S8,
            TypeKind.U8 or TypeKind.Bool or TypeKind.Chr => MemKind.U8,
            TypeKind.I16 => MemKind.S16,
            TypeKind.U16 => MemKind.U16,
            TypeKind.I64 or TypeKind.U64 or TypeKind.Pointer or TypeKind.Object => MemKind.Q64,
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
            VariableSymbol? badParameter = m.Parameters.FirstOrDefault(p => !(Supported(p.Type) && !p.Grows && !p.TextRef && p.Ownership is Ownership.None or Ownership.Borrowed));
            Need(badParameter == null, $"a parameter it doesn't take ('{badParameter?.Name}', {Describe(badParameter)})");
            Need(!m.ReturnType.IsObject, "it gives an object");
            VariableSymbol? badLocal = m.Locals.FirstOrDefault(v => !(OwnObject(v)
                                   || Supported(v.Type) && !v.Type.IsObject && v.Ownership == Ownership.None && !v.Grows && !v.GrowingArray && !v.GrowingElements));
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
            if (catches && owned.Count > 0)
            {
                // the record a throw looks at, three homes: the lowest is where it starts
                fn.NewHome();
                fn.NewHome();
                fn.NewHome();
                fn.UnwindRecord = 8 * fn.Homes;
                Emit(new UnwindI { On = true });
            }

            // the parameters, from their places in the frame: the last at rbp + 16, each 8 bytes
            // (a vec's 16)
            int above = 16;
            for (int i = m.Parameters.Count - 1; i >= 0; i--)
            {
                VariableSymbol p = m.Parameters[i];
                VReg v = Variable(p);
                Emit(new LoadI { Dst = v, Place = $"rbp + {above}", Kind = KindOf(p.Type) });
                above += p.Type.IsVec ? 16 : 8;
            }
            // (a variable is set where it's declared; one that could be read before that is
            // given 0 at the start, by IrPeephole, which knows which)
            foreach (VariableSymbol v in m.Locals.Where(v => !owned.ContainsKey(v))) Variable(v);

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
            _ => Array.Empty<Block>(),
        };

        /// <summary>A new value for one of type t: an f64's, or a whole number's of its width.</summary>
        VReg New(PhiType t, string name = "") => t.IsVec ? fn.NewVec(name) : IsF(t) ? fn.NewFloat(name) : fn.NewValue(Is64(t), name);

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
            foreach (var (home, s) in fn.Owned)
            {
                VReg o = fn.NewValue(true);
                Emit(new LoadI { Dst = o, Place = home, Kind = MemKind.Q64 });
                Emit(new ObjCallI { Kind = "free", Struct = s, Arg = o });
            }
        }

        /// <summary>Where an object is kept: a method's own one's home, or a field's base and offset.</summary>
        (string? Home, VReg? Base, long Offset) ObjectPlace(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol v } && owned.TryGetValue(v, out string? home)) return (home, null, 0);
            var (b, offset, t) = Field(e);
            Need(b != null && t.IsObject, $"an object kept in a {e.GetType().Name} it doesn't take");
            return (null, b, offset);
        }

        /// <summary>The object kept there: what's known of it (empty, mostly), or read.</summary>
        Opnd LoadObject((string? Home, VReg? Base, long Offset) place)
        {
            if (place.Home != null && homeHolds.TryGetValue(place.Home, out Opnd known)) return known;
            // (a field of an object just made is empty)
            if (place.Base != null && readFrom.TryGetValue(place.Base, out string? of) && freshObjects.Contains(of)) return Opnd.Const(0);
            VReg d = fn.NewValue(true);
            if (place.Home != null)
            {
                Emit(new LoadI { Dst = d, Place = place.Home, Kind = MemKind.Q64 });
                readFrom[d] = place.Home;
            }
            else Emit(new LoadIdxI { Dst = d, Base = place.Base!, Index = Opnd.Const(place.Offset), Scale = 1, Kind = MemKind.Q64 });
            return d;
        }

        void StoreObject((string? Home, VReg? Base, long Offset) place, Opnd value)
        {
            if (place.Home != null)
            {
                Emit(new StoreI { Place = place.Home, Src = value, Kind = MemKind.Q64 });
                // another object there now: what was known of the one before goes
                foreach (VReg was in readFrom.Where(r => r.Value == place.Home).Select(r => r.Key).ToList()) readFrom.Remove(was);
                freshObjects.Remove(place.Home);
                homeHolds[place.Home] = value;
                return;
            }
            Emit(new StoreIdxI { Base = place.Base!, Index = Opnd.Const(place.Offset), Scale = 1, Src = value, Kind = MemKind.Q64 });
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
                VReg loaded = fn.NewValue(true, v.Name);
                Emit(new LoadI { Dst = loaded, Place = home, Kind = MemKind.Q64 });
                readFrom[loaded] = home;
                return loaded;
            }
            if (!variables.TryGetValue(v, out VReg? r))
            {
                Need(Supported(v.Type), $"'{v.Name}' is a {v.Type}");
                r = New(v.Type, v.Name);
                variables[v] = r;
            }
            return r;
        }

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

                case ObjectAssignStmt { Kind: ObjectAssignKind.Clear } clear:
                {
                    // x is empty: what it had is freed
                    Mark(clear);
                    var place = ObjectPlace(clear.Target);
                    Opnd had = LoadObject(place);
                    StoreObject(place, Opnd.Const(0));
                    if (!(had.IsImm && had.Imm == 0)) Emit(new ObjCallI { Kind = "free", Struct = clear.Struct, Arg = InReg(had, true) });
                    return;
                }

                case TakesStmt t:
                {
                    // the generator's EmitTakes: the source's place, then the target's; the object
                    // moves, the source is left empty, and what the target had is freed (unless it
                    // was nothing, or the same object)
                    Need(t.Struct != null && t.Inside == null, "a takes it doesn't take");
                    Mark(t);
                    var from = ObjectPlace(t.Source);
                    var to = ObjectPlace(t.Target);
                    Opnd moved = LoadObject(from);
                    StoreObject(from, Opnd.Const(0));
                    Opnd had = LoadObject(to);
                    StoreObject(to, moved);
                    // (a target known to be empty, as a list's new node's next one is, frees nothing)
                    if (had.IsImm && had.Imm == 0) return;
                    VReg hadReg = InReg(had, true);
                    Block check = fn.NewBlock(), free = fn.NewBlock(), after = fn.NewBlock();
                    current.End = new BranchT { A = hadReg, B = Opnd.Const(0), Cond = Cond.Eq, Then = after, Else = check };
                    Enter(check);
                    current.End = new BranchT { A = hadReg, B = moved, Cond = Cond.Eq, Then = after, Else = free };
                    Enter(free);
                    Emit(new ObjCallI { Kind = "free", Struct = t.Struct!, Arg = hadReg });
                    StartBlock(after);
                    return;
                }

                case VarDecl d:
                {
                    Need(d.Symbol is VariableSymbol && d.Values.Count <= 1 && !d.IsNew && d.AtAddress == null && d.BufferSize == null
                         && d.MoreSizes.Count == 0 && !d.Unsized && d.TextSize == null && d.EachOf == null && d.Narrows == null,
                         "a declaration it doesn't take");
                    VariableSymbol v = (VariableSymbol)d.Symbol!;
                    Mark(d);
                    Assign(v, d.Values.Count == 1 ? d.Values[0] : null);
                    return;
                }

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
                        case IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } inArray when GlobalArray(arr) != null:
                        {
                            Opnd value = Value(a.Value, inArray.Type);
                            var (bas, index, scale) = ArrayElement(inArray, arr);
                            Emit(new StoreIdxI { Base = bas, Index = index, Scale = scale, Src = Small(value, Is64(inArray.Type)), Kind = KindOf(inArray.Type) });
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
                        && !SyntaxWalker.Nodes(loop).Any(n => n is FreeStmt or LockedStmt or AtomicStmt or AtomicExpr
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
                    // (unsafe leaves out index checks, which the binder has already left out of what's inside)
                    Statements(u.Body);
                    return;

                case LockedStmt lk:
                {
                    Need(lk.Lock is NameExpr { Symbol: VariableSymbol { Type.Kind: TypeKind.Lock } lv } && AnyGlobal(lv) != null, "a lock that isn't a class's variable");
                    string label = AnyGlobal((VariableSymbol)((NameExpr)lk.Lock).Symbol!)!;
                    Mark(lk);
                    VReg saved = fn.NewValue(true, "catch");
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

                case FreeStmt { Owner: null, Releaser: MethodSymbol releaser } free:
                    Mark(free);
                    Call(releaser, new List<Expr> { free.Pointer }, wantResult: false);
                    return;

                case CallStmt { Resolved: RawBlockDecl raw } rc:
                    Need(rc.Arguments.Count == 0 && rc.ResultTarget == null && rc.CalleeTarget == null, "an assembly block's result kept");
                    Mark(rc);
                    Emit(new RawCallI { Label = raw.Name });
                    return;

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
            Opnd v = Value(value, method.ReturnType);
            Unlock(0);
            if (current.End != null) Enter(fn.NewBlock());
            TidyUp();
            current.End = new RetT { Value = v, Wide = method.ReturnType.IsWide, Pointer = method.ReturnType.IsPointer, Float = IsF(method.ReturnType), Vec = method.ReturnType.IsVec };
        }

        /// <summary>True when <see cref="Field"/> takes the expression (asking only, with nothing built).</summary>
        bool IsField(Expr e) => e switch
        {
            NameExpr { Symbol: SelfFieldSymbol f } => Ours(f.Self) && Supported(f.Type),
            MemberExpr { Field: FieldSymbol f, Call: null, Target: IndexExpr { Target.Type.IsPointer: true } element } when Supported(f.Type)
                && element.Type.IsStruct && !element.Type.IsArray && element.Type.Struct!.Size > 0 => true,
            MemberExpr { Field: FieldSymbol f, Call: null } m => Supported(f.Type) && Is64(Real(m.Target)) && Supported(Real(m.Target)),
            NameExpr { Symbol: PathSymbol p } => p.BaseOffset == 0 && Ours(p.Root) && Is64(p.Root.Type) && p.Hops.Count > 0 && Supported(p.Type),
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
                case MemberExpr { Field: FieldSymbol f, Call: null } m when Supported(f.Type) && Is64(Real(m.Target)) && Supported(Real(m.Target)):
                    return (InReg(Value(m.Target), true), f.Offset, f.Type);
                case MemberExpr { Field: FieldSymbol f, Call: null, Target: IndexExpr { Target.Type.IsPointer: true } element } when Supported(f.Type)
                                                                                && element.Type.IsStruct && !element.Type.IsArray && element.Type.Struct!.Size > 0:
                {
                    // p:i.field: the struct at p + i * its size, and the field in it (the index first, as an element's)
                    long size = element.Type.Struct!.Size;
                    PhiType it = Real(element.Index);
                    Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
                    Opnd index = Convert(Value(element.Index), it, it.IsSigned ? PhiType.I64 : PhiType.U64);
                    VReg p = InReg(Value(element.Target), true);
                    if (index.IsImm)
                    {
                        long offset = index.Imm * size + f.Offset;
                        if (offset >= int.MinValue && offset <= int.MaxValue) return (p, offset, f.Type);
                        index = InReg(index, true);
                    }
                    VReg scaled = fn.NewValue(true);
                    Emit(new BinI { Op = BinOp.Mul, Dst = scaled, A = index.Reg!, B = Opnd.Const(size) });
                    VReg at = fn.NewValue(true);
                    Emit(new BinI { Op = BinOp.Add, Dst = at, A = p, B = scaled });
                    return (at, f.Offset, f.Type);
                }
                case NameExpr { Symbol: PathSymbol p } when p.BaseOffset == 0 && Ours(p.Root) && Is64(p.Root.Type) && p.Hops.Count > 0
                                                             && Supported(p.Type):
                {
                    VReg at = Variable(p.Root);
                    for (int k = 0; k + 1 < p.Hops.Count; k++)
                    {
                        VReg next = fn.NewValue(true);
                        Emit(new LoadIdxI { Dst = next, Base = at, Index = Opnd.Const(p.Hops[k]), Scale = 1, Kind = MemKind.Q64 });
                        at = next;
                    }
                    return (at, p.Hops[^1], p.Type);
                }
            }
            return (null, 0, PhiType.Error);
        }

        /// <summary>A class's array of a size, kept where its label is, of numbers or f64s: its label, or null.</summary>
        static string? GlobalArray(VariableSymbol v) =>
            !v.IsLocal && !v.IsParameter && v.Type.IsArray && !v.Type.Open && !v.Type.Filled && !v.Type.TextTable && v.Type.Length > 0
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
            Need(Supported(element), $"an array of {element}");
            int scale = element.ElementSize;
            Need(scale is 1 or 2 or 4 or 8 or 16, $"an element of {scale} bytes");
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, it.IsSigned ? PhiType.I64 : PhiType.U64);
            if (e.CheckLimit is int limit && !(index.IsImm && index.Imm >= 0 && index.Imm < limit) && !MaskedBelow(e.Index, limit))
            {
                VReg checkedIndex = InReg(index, true);
                Emit(new CheckI { Index = checkedIndex, Limit = limit, At = e, Held = HeldStores() });
                index = checkedIndex;
            }
            VReg bas = fn.NewValue(true);
            Emit(new AddrI { Dst = bas, Label = GlobalArray(array)! });
            if (index.IsImm && (index.Imm * scale < int.MinValue || index.Imm * scale > int.MaxValue)) index = InReg(index, true);
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
            // (a pointer is compared as an unsigned 64-bit number, with 0 or another pointer)
            bool pointers = l.IsPointer || r.IsPointer;
            bool wide = l.IsWide || r.IsWide || pointers;
            bool unsigned = pointers || (wide ? l.Kind == TypeKind.U64 || r.Kind == TypeKind.U64 : l.IsUnsigned32 || r.IsUnsigned32);
            PhiType at = pointers ? (l.IsPointer ? l : r) : wide ? (unsigned ? PhiType.U64 : PhiType.I64) : (unsigned ? PhiType.U32 : PhiType.I32);
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
            Need(Supported(into), $"a value going into a {into}");
            return Convert(Value(e), Real(e), into);
        }

        /// <summary>e's value as its own type has it.</summary>
        Opnd Value(Expr e)
        {
            Need(Supported(Real(e)), $"a value that's a {Real(e)}");
            if (PointerMath(e)) return MovePointer((BinaryExpr)e);
            if (VecValue(e) is Opnd vector) return vector;
            switch (e)
            {
                case NumberExpr n:
                    return Opnd.Const(n.Value);
                case NameExpr { Symbol: LengthSymbol { Count: int known } }:
                    return Opnd.Const(known);
                case MemberExpr { Count: int counted, Call: null }:
                    return Opnd.Const(counted);
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
                case NameExpr { Symbol: FieldPathSymbol gf } when GlobalStruct(gf.Root) is string label && Supported(gf.Type) && !gf.Type.IsObject:
                {
                    VReg d = New(gf.Type);
                    Emit(new LoadI { Dst = d, Place = $"{label} + {gf.Offset}", Kind = KindOf(gf.Type) });
                    return d;
                }
                case AddrExpr { OfObject: true, CodeLabel: null, Operand: var obj } when Is64(Real(obj)) && Supported(Real(obj)):
                    // (an object is at the address it holds: a struct method's self, called on one)
                    return Value(obj);
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: NameExpr { Symbol: VariableSymbol g } } addr
                    when addr.Type.IsPointer && !g.Grows && !g.TextRef && !g.Type.Open && AnyGlobal(g) is string label:
                {
                    VReg d = fn.NewValue(true);
                    Emit(new AddrI { Dst = d, Label = label });
                    return d;
                }
                case IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } inArray when GlobalArray(arr) != null:
                {
                    var (bas, index, scale) = ArrayElement(inArray, arr);
                    VReg d = New(inArray.Type);
                    Emit(new LoadIdxI { Dst = d, Base = bas, Index = index, Scale = scale, Kind = KindOf(inArray.Type) });
                    return d;
                }
                case AddrExpr { OfObject: false, CodeLabel: null, Operand: IndexExpr { Target: NameExpr { Symbol: VariableSymbol arr } } inArray }
                    when GlobalArray(arr) != null:
                {
                    // addr list:i: where the element is (checked as reading it would be)
                    var (bas, index, scale) = ArrayElement(inArray, arr);
                    VReg d = fn.NewValue(true);
                    if (index.IsImm) Emit(new BinI { Op = BinOp.Add, Dst = d, A = bas, B = Opnd.Const(index.Imm * scale) });
                    else
                    {
                        VReg moved = fn.NewValue(true);
                        Emit(new BinI { Op = BinOp.Mul, Dst = moved, A = index.Reg!, B = Opnd.Const(scale) });
                        Emit(new BinI { Op = BinOp.Add, Dst = d, A = bas, B = moved });
                    }
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
            throw new NotYet($"a {e.GetType().Name}");
        }

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
                    _ => throw new NotYet($"the operator {b.Op} on floats"),
                };
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
                VReg pd = fn.NewValue(true);
                VReg pl = InReg(Value(b.Left), true);
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
                p = InReg(Value(pointer), true);
                n = Small(Convert(Value(number), Real(number), Real(number).IsSigned ? PhiType.I64 : PhiType.U64), true);
            }
            else
            {
                n = Small(Convert(Value(number), Real(number), Real(number).IsSigned ? PhiType.I64 : PhiType.U64), true);
                p = InReg(Value(pointer), true);
            }
            VReg d = fn.NewValue(true);
            Emit(new BinI { Op = b.Op == BinaryOp.Add ? BinOp.Add : BinOp.Sub, Dst = d, A = p, B = n });
            return d;
        }

        /// <summary>p:i on a pointer: its address, the index made 64 bits with its own sign, and the element's size.</summary>
        (VReg Base, Opnd Index, int Scale) Element(IndexExpr e)
        {
            PhiType element = e.Type;
            Need(Supported(element), $"a pointer to a {element}");
            int scale = element.ElementSize;
            Need(scale is 1 or 2 or 4 or 8 or 16, $"an element of {scale} bytes");
            VReg bas = InReg(Value(e.Target), true);
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer, $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, it.IsSigned ? PhiType.I64 : PhiType.U64);
            // (a number goes in the address itself: it must fit 32 bits there, times the size)
            if (index.IsImm && (index.Imm * scale < int.MinValue || index.Imm * scale > int.MaxValue)) index = InReg(index, true);
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
                VReg whole = fn.NewValue(true);
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
                    VReg wide = fn.NewValue(true);
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
                VReg d = fn.NewValue(true);
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
        Opnd Small(Opnd v, bool wide) => v.IsImm && wide && (v.Imm < int.MinValue || v.Imm > int.MaxValue) ? InReg(v, true) : v;

        // ---------------------------------------------------------------- calls

        Opnd CallValue(CallExpr call)
        {
            if (call.Resolved is VecMethod vm) return VecCall(call, vm);
            // (a struct's method has the struct it's called on among its arguments already: self)
            Need(call.Resolved is MethodSymbol m && (call.CalleeTarget == null || m.Decl.Owner.ForStruct != null), "a call of a built-in or a method value");
            var callee = (MethodSymbol)call.Resolved!;
            return Call(callee, call.Arguments, wantResult: true)!;
        }

        VReg? Call(MethodSymbol callee, List<Expr> arguments, bool wantResult)
        {
            Need(!callee.IsHook && !callee.IsInterruptHandler, "a call of an event");
            Need(callee.Parameters.All(p => Supported(p.Type) && !p.Grows && !p.TextRef && !(p.Type.IsObject && p.Ownership == Ownership.Owned)),
                 $"a call of {callee.Name}, which takes what this form doesn't");
            Need(callee.ReturnType.Kind == TypeKind.Void || Supported(callee.ReturnType) && !callee.ReturnType.IsObject, $"a call of {callee.Name}, which gives a {callee.ReturnType}");
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
                call.Args.Add(given != null ? Value(given, pt) : pt.IsVec ? ZeroVec() : IsF(pt) ? FloatConst(0.0) : Opnd.Const(0));
                call.ArgKinds.Add(KindOf(pt));
            }
            Need(!wantResult || call.Dst != null, $"{callee.Name} gives nothing to use");
            Emit(call);
            return call.Dst;
        }
    }
}

using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // Text in the intermediate form, as the generator has it: a str's value is where its text
    // is (zero-terminated bytes). A method's own str with a size (a parameter, which the method
    // copies what it's given into when it starts, or a variable) is a buffer in its frame; a
    // class's is at its label; a struct's is where the field is (or, for one that grows, where
    // its slot says). Text that grows on the heap, and building text, are still the generator's.
    public sealed partial class IrBuilder
    {
        /// <summary>A method's own str with a size, kept in its frame: how far below rbp its bytes start.</summary>
        readonly Dictionary<VariableSymbol, int> frameTexts = new();

        /// <summary>A str this form keeps in the method's frame: a parameter (not the caller's own, str into) or a variable, with a size.</summary>
        static bool FrameText(VariableSymbol v) =>
            v.Type.IsString && !v.Type.IsArray && !v.Grows && !v.TextRef && v.Capacity > 0 && !v.Type.IsInlineText;

        string TextPlace(VariableSymbol v) => $"rbp - {frameTexts[v]}";

        /// <summary>
        /// A str parameter with no default (str target;) that the method only reads: the caller's
        /// own text, through the address of its slot (where the text is, then its room).
        /// </summary>
        static bool CallersText(VariableSymbol p) => p.IsParameter && p.TextRef && p.Type.IsString && !p.Type.IsArray;

        /// <summary>A method's own str that grows (a variable, or a parameter without a size): its slot in the frame.</summary>
        static bool GrowingText(VariableSymbol v) => v.Grows && !v.TextRef && v.Type.IsString && !v.Type.IsArray;

        /// <summary>Each such str's slot: where its text's address is (0 while it has none), then its room.</summary>
        readonly Dictionary<VariableSymbol, string> growingTexts = new();

        /// <summary>The slots, two homes each, empty to start with (as the generator's frame starts zeroed), freed at its end.</summary>
        /// <summary>
        /// The program has a heap: text built where it's used goes in a slot that grows (to a str's
        /// max), as the generator's GrowResults; without one, 256 bytes of the frame.
        /// </summary>
        readonly bool growResults;

        /// <summary>Every node of the method's body (and its [end: result]).</summary>
        IEnumerable<Node> BodyNodes() => method.Decl.Body.SelectMany(SyntaxWalker.Nodes)
            .Concat(method.Decl.Result is Expr r ? SyntaxWalker.Nodes(r) : Enumerable.Empty<Node>());

        /// <summary>With a heap, each text built where it's used: its slot (empty to start with, freed at the end).</summary>
        readonly Dictionary<TextExpr, string> inlineSlots = new();

        void PlaceGrowingTexts()
        {
            // (text built from its own elements is built aside first when they're the same: that text too)
            foreach (StringBuildStmt build in BodyNodes().OfType<StringBuildStmt>().Where(b => b.AsideWhen.Count > 0))
            {
                var whole = new TextExpr { Span = build.Span, Type = PhiType.Str };
                whole.Parts.AddRange(build.Parts);
                asides[build] = whole;
            }
            if (growResults)
                foreach (TextExpr built in BodyNodes().OfType<TextExpr>().Concat(asides.Values).Distinct())
                {
                    fn.NewHome();
                    fn.NewHome();
                    string slot = $"rbp - {8 * fn.Homes}";
                    inlineSlots[built] = slot;
                    fn.OwnedTexts.Add(slot);
                    Emit(new ZeroI { Place = slot, Size = 16 });
                }
            foreach (VariableSymbol v in method.Parameters.Concat(method.Locals).Where(GrowingText))
            {
                fn.NewHome();
                fn.NewHome();
                string slot = $"rbp - {8 * fn.Homes}";
                growingTexts[v] = slot;
                fn.OwnedTexts.Add(slot);
                Emit(new ZeroI { Place = slot, Size = 16 });
            }
        }

        /// <summary>Where a str that grows keeps its slot (its text's address, then its room).</summary>
        VReg SlotOf(Expr target)
        {
            switch (target)
            {
                case NameExpr { Symbol: VariableSymbol v } when growingTexts.TryGetValue(v, out string? home):
                {
                    VReg d = fn.NewValue(true);
                    Emit(new AddrI { Dst = d, Label = home });
                    return d;
                }
                case NameExpr { Symbol: VariableSymbol p } when callersTexts.TryGetValue(p, out VReg? given):
                    return given;
                case NameExpr { Symbol: VariableSymbol { Grows: true } g } when !Ours(g) && SimpleLabel(g.Label):
                {
                    VReg d = fn.NewValue(true);
                    Emit(new AddrI { Dst = d, Label = g.Label });
                    return d;
                }
            }
            return AddressOf(target);
        }

        bool CanSlot(Expr target) => target switch
        {
            NameExpr { Symbol: VariableSymbol v } when growingTexts.ContainsKey(v) || callersTexts.ContainsKey(v) => true,
            NameExpr { Symbol: VariableSymbol { Grows: true } g } when !Ours(g) && SimpleLabel(g.Label) => true,
            _ => target.Type.IsHeapText && CanAddress(target),
        };

        /// <summary>
        /// s is a b c; (or s++ ...): each value in turn, a text or a number written out, into a
        /// str that grows, the first in place of what it had unless it's added to.
        /// </summary>
        /// <summary>Each build from its own elements: all of its values as one text, built aside when they're the same element.</summary>
        readonly Dictionary<StringBuildStmt, TextExpr> asides = new();

        /// <summary>Where a text is kept, as the generator's EmitPlaceAddress has it: an element's slot, or where it is.</summary>
        VReg TextPlaceAddress(Expr e) => e is IndexExpr ix && TextTableElement(ix) ? TextSlotOf(ix) : AddressOf(e);

        bool CanTextPlace(Expr e) => e is IndexExpr ix && TextTableElement(ix) || CanAddress(e);

        void BuildText(StringBuildStmt build)
        {
            if (build.AsideWhen.Count > 0)
            {
                // texts:i++ texts:j; when i is j as it runs, the values are built into a text of
                // their own first, so each is read as it was; otherwise straight into it (the generator's)
                Need(CanTextPlace(build.Target) && build.AsideWhen.All(CanTextPlace), "text built from its own elements");
                Block aside = fn.NewBlock(), done = fn.NewBlock();
                foreach (Expr place in build.AsideWhen)
                {
                    VReg one = TextPlaceAddress(place), into = TextPlaceAddress(build.Target);
                    Block next = fn.NewBlock();
                    if (current.End != null) Enter(fn.NewBlock());
                    current.End = new BranchT { Cond = Cond.Eq, A = one, B = into, Then = aside, Else = next };
                    Enter(next);
                }
                var straight = new StringBuildStmt { Target = build.Target, Append = build.Append, Span = build.Span };
                straight.Parts.AddRange(build.Parts);
                BuildText(straight);
                if (current.End == null) current.End = new JumpT { Target = done };
                Enter(aside);
                var whole = new StringBuildStmt { Target = build.Target, Append = build.Append, Span = build.Span };
                whole.Parts.Add(asides[build]);
                BuildText(whole);
                if (current.End == null) current.End = new JumpT { Target = done };
                Enter(done);
                return;
            }
            Need(CanSlot(build.Target) || TableSlot(build.Target) || CanFix(build.Target), $"text built in {Unknown(build.Target)}");
            for (int k = 0; k < build.Parts.Count; k++) PutText(build.Target, build.Parts[k], append: build.Append || k > 0);
        }

        /// <summary>One value put in a str that grows (its slot found after the value, as the generator does).</summary>
        void PutText(Expr target, Expr part, bool append)
        {
            if (!CanSlot(target) && !TableSlot(target))
            {
                PutPart(part, append, null, target);
                return;
            }
            PutPart(part, append, null, null, target);
        }

        /// <summary>
        /// One value into a str: one with a size at fixedAt (or found from fixedTarget, after the
        /// value), or else the slot of slotTarget, a str that grows.
        /// </summary>
        void PutPart(Expr part, bool append, (VReg At, int Room)? fixedAt, Expr? fixedTarget, Expr? slotTarget = null, string? slotHome = null)
        {
            Expr? target = slotTarget;
            var (value, isText, unsigned, known) = PartValue(part);
            if (fixedAt == null && fixedTarget != null) fixedAt = FixedPlace(fixedTarget) ?? throw new InvalidOperationException("no place for the text");
            if (fixedAt is (VReg at, int room))
            {
                Emit(new FixedStoreI { Dst = at, Room = room, Value = value, SourceIsString = isText, Append = append, Unsigned = unsigned, MayCut = textMayCut, Held = HeldStores() });
                return;
            }
            VReg slot;
            int? roomOffset = null;
            string? rooms = null;
            if (slotHome != null)
            {
                slot = fn.NewValue(true);
                Emit(new AddrI { Dst = slot, Label = slotHome });
            }
            else if (TableSlot(target!)) (slot, roomOffset, rooms) = TableSlotOf((IndexExpr)target!);
            else slot = SlotOf(target!);
            Emit(new GrowStoreI { Slot = slot, Value = value, SourceIsString = isText, Append = append, Unsigned = unsigned, Known = known, MayCut = textMayCut,
                                  RoomOffset = roomOffset, Rooms = rooms });
        }

        /// <summary>A value put in text, worked out: a text's address, or a number (written out as text, or a whole one the store writes).</summary>
        (Opnd Value, bool IsText, bool Unsigned, int? Known) PartValue(Expr part)
        {
            PhiType t = Real(part);
            Opnd value;
            bool isText = true;
            bool unsigned = false;
            if (t.IsString && !t.IsArray) value = TextValue(part);
            else if (t.IsTextPointer) value = InReg(Value(part), true);     // (a ptr<u8>: the text it points at)
            else if (t.IsWide || IsF(t) || t.IsChar)
            {
                Need(Supported(t), $"{A(t)} written out as text");
                VReg written = fn.NewValue(true);
                fn.NeedScratch();
                PrintKind kind = t.IsChar ? PrintKind.Char : IsF(t) ? PrintKind.Float : t.Kind == TypeKind.U64 ? PrintKind.Uint64 : PrintKind.Int64;
                Emit(new NumberTextI { Dst = written, Value = Value(part), Kind = kind });
                value = written;
            }
            else
            {
                Need(t.IsNumeric && !t.IsPointer, $"{A(t)} written out as text");
                value = Value(part, t.IsUnsigned32 ? PhiType.U32 : PhiType.Int);
                isText = false;
                unsigned = t.IsUnsigned32;
            }
            int? known = part is StringExpr s && s.Type.IsString ? (s.Value.IndexOf('\0') is int zero and >= 0 ? zero : s.Value.Length) : null;
            return (Small(value, true), isText, unsigned, isText ? known : null);
        }

        /// <summary>Each such parameter's value: the address of the caller's slot.</summary>
        readonly Dictionary<VariableSymbol, VReg> callersTexts = new();

        /// <summary>Its buffer in the frame (the parameters' and variables' together, before anything else is built).</summary>
        void PlaceFrameTexts()
        {
            foreach (VariableSymbol v in method.Parameters.Concat(method.Locals).Where(FrameText))
            {
                for (int k = 0; k < (v.Capacity + 7) / 8; k++) fn.NewHome();
                frameTexts[v] = 8 * fn.Homes;
            }
        }

        /// <summary>True when <see cref="TextAddress"/> takes the text (asking only, with nothing built).</summary>
        bool CanText(Expr e) => e switch
        {
            StringExpr => true,
            NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v) || callersTexts.ContainsKey(v) || growingTexts.ContainsKey(v) => true,
            NameExpr { Symbol: VariableSymbol { TextRef: false } g } when !Ours(g) && !g.Type.IsArray && SimpleLabel(g.Label) => true,
            _ when e.Type.IsInlineText || e.Type.IsHeapText => CanAddress(e),
            TextExpr t => t.Parts.All(CanPart),
            IndexExpr element when TextTableElement(element) => true,
            CallExpr or NameExpr { Call: not null } or MemberExpr { Call: not null } => !e.Type.IsArray,
            _ => false,
        };

        /// <summary>A value that can be put in text: a text, or a number written out.</summary>
        bool CanPart(Expr part)
        {
            PhiType t = Real(part);
            if (t.IsString && !t.IsArray) return CanText(part);
            if (t.IsTextPointer) return true;
            return (t.IsNumeric || t.IsWide || IsF(t) || t.IsChar) && !t.IsPointer;
        }

        /// <summary>
        /// For a str parameter with no default (str into;): the address of a slot, its text and
        /// room, as the generator's PushTextRef. A str that grows gives its own (the method can
        /// make it bigger); anything else a slot of this call's, with its room and the Fixed bit:
        /// a str with a size, its size; text only read (a literal, a ptr&lt;u8&gt;, a number written
        /// out), its length and 1.
        /// </summary>
        VReg TextRefArg(Expr value)
        {
            if (CanSlot(value)) return SlotOf(value);
            Need(value is not IndexExpr { Type.IsString: true }, "an element of a str array given for a str to write");
            int room = value.Type.IsInlineText ? value.Type.Room
                : value is NameExpr { Symbol: VariableSymbol { Type.IsString: true, Type.IsArray: false } sized } ? sized.Capacity : 0;
            fn.NewHome();
            fn.NewHome();
            string slot = $"rbp - {8 * fn.Homes}";
            VReg text;
            PhiType t = Real(value);
            if (t.IsString && !t.IsArray) text = (VReg)TextValue(value).Reg!;
            else if (t.IsTextPointer) text = InReg(Value(value), true);
            else
            {
                // a number, written out as text first (in 16 bytes of its own)
                Need(CanPart(value), $"{A(t)} given for a str");
                fn.NewHome();
                fn.NewHome();
                string written = $"rbp - {8 * fn.Homes}";
                VReg at = fn.NewValue(true);
                Emit(new AddrI { Dst = at, Label = written });
                PutPart(value, false, (at, 12), null);
                text = fn.NewValue(true);
                Emit(new AddrI { Dst = text, Label = written });
            }
            Emit(new StoreI { Place = slot, Src = text, Kind = MemKind.Q64 });
            if (room > 0) Emit(new StoreI { Place = $"{slot} + 8", Src = Opnd.Const(room | 0x40000000), Kind = MemKind.D32 });
            else
            {
                // read only: its room is what it has, so .max is its length
                VReg length = fn.NewValue(false), roomed = fn.NewValue(false), marked = fn.NewValue(false);
                Emit(new TextI { Op = TextOp.Length, Dst = length, A = text });
                Emit(new BinI { Op = BinOp.Add, Dst = roomed, A = length, B = Opnd.Const(1) });
                Emit(new BinI { Op = BinOp.Or, Dst = marked, A = roomed, B = Opnd.Const(0x40000000) });
                Emit(new StoreI { Place = $"{slot} + 8", Src = marked, Kind = MemKind.D32 });
            }
            VReg d = fn.NewValue(true);
            Emit(new AddrI { Dst = d, Label = slot });
            return d;
        }

        /// <summary>
        /// An element of an array of str without a size (a table of where each text is, 0 for
        /// none): of a view (a struct's, a class's that grows, a parameter), or a class's table.
        /// </summary>
        bool TextTableElement(IndexExpr e) => e.Target.Type.TextTable && e.Type.IsString && !e.Type.IsArray
            && (e.Target.Type.Open
                    ? Binder.IsFieldView(e.Target) && CanAddress(e.Target)
                      || e.Target is NameExpr { Symbol: VariableSymbol v } && (paramViews.ContainsKey(v) || GrowingView(v))
                    : e.Target is NameExpr { Symbol: VariableSymbol t } && !t.GrowingArray && (!Ours(t) && SimpleLabel(t.Label) || localTables.ContainsKey(t))
                      && (e.CheckLimit != null || unsafeDepth > 0 || ConstantFolder.Evaluate(e.Index) != null));

        /// <summary>An element of a str array written (its text grows): a struct's or a class's that grows, or a class's table of texts that grow (not a parameter's).</summary>
        bool TableSlot(Expr target) => target is IndexExpr ix && TextTableElement(ix)
            && (ix.Target.Type.Open || ix.Target is NameExpr { Symbol: VariableSymbol { GrowingElements: true } });

        /// <summary>Its slot, and where its room is: .max slots on (kept in a home for the store), or the table's size on.</summary>
        (VReg Slot, int RoomOffset, string? Rooms) TableSlotOf(IndexExpr ix)
        {
            VReg slot = TextSlotOf(ix);
            if (!ix.Target.Type.Open) return (slot, PhiType.AddressSize * ((VariableSymbol)((NameExpr)ix.Target).Symbol!).Count, null);
            VReg most = LoadAt(ViewOf(ix.Target), ViewMax, PhiType.U32);
            fn.NewHome();
            string rooms = $"rbp - {8 * fn.Homes}";
            Emit(new StoreI { Place = rooms, Src = most, Kind = MemKind.D32 });
            return (slot, 0, rooms);
        }

        /// <summary>Where an element's text pointer is, the index checked: against how many a view has, or the table's size.</summary>
        VReg TextSlotOf(IndexExpr e)
        {
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, it.IsSigned ? PhiType.I64 : PhiType.U64);
            VReg elements;
            if (e.Target.Type.Open)
            {
                VReg view = ViewOf(e.Target);
                if (unsafeDepth == 0)
                {
                    VReg count = LoadAt(view, ViewCount, PhiType.U32);
                    VReg limit = fn.NewValue(true);
                    Emit(new ExtendI { Dst = limit, Src = count, Signed = false });
                    VReg checkedIndex = InReg(index, true);
                    Emit(new CheckI { Index = checkedIndex, LimitValue = limit, At = e, Held = HeldStores() });
                    index = checkedIndex;
                }
                elements = LoadAt(view, 0, PhiType.PointerTo(PhiType.U8));
            }
            else
            {
                if (e.CheckLimit is int limit && unsafeDepth == 0 && !(index.IsImm && index.Imm >= 0 && index.Imm < limit))
                {
                    VReg checkedIndex = InReg(index, true);
                    Emit(new CheckI { Index = checkedIndex, Limit = limit, At = e, Held = HeldStores() });
                    index = checkedIndex;
                }
                elements = fn.NewValue(true);
                var table = (VariableSymbol)((NameExpr)e.Target).Symbol!;
                Emit(new AddrI { Dst = elements, Label = localTables.TryGetValue(table, out string? mine) ? mine : table.Label });
            }
            return Offset(elements, InReg(index, true), PhiType.AddressSize, fn.NewValue(true));
        }

        /// <summary>A method's own table of texts (str names: 'a' 'b';, str list[3];): each element its own buffer in the frame, its text growing out of it when it must.</summary>
        static bool LocalTable(VariableSymbol v) => v.IsLocal && v.Type.TextTable && !v.Type.Open && !v.GrowingArray && v.Type.Length is > 0 and <= 64 && v.Capacity > 0;

        /// <summary>Each one's table: where its slots are (then its rooms, when its texts grow).</summary>
        readonly Dictionary<VariableSymbol, string> localTables = new();

        /// <summary>The frame for each: the table, then each element's buffer; each slot set to its buffer (its room, its size, in data), as the generator's frame start.</summary>
        void PlaceLocalTables()
        {
            foreach (VariableSymbol v in method.Locals.Where(LocalTable))
            {
                int count = v.Type.Length, room = (v.Capacity + 7) / 8 * 8;
                int tableBytes = PhiType.AddressSize * count * (v.GrowingElements ? 2 : 1);
                for (int k = 0; k < (tableBytes + count * room) / 8; k++) fn.NewHome();
                int top = 8 * fn.Homes;
                string table = $"rbp - {top}";
                localTables[v] = table;
                Emit(new ZeroI { Place = table, Size = tableBytes + count * room });
                for (int k = 0; k < count; k++)
                {
                    string slot = $"rbp - {top - PhiType.AddressSize * k}", buffer = $"rbp - {top - tableBytes - room * k}";
                    VReg at = fn.NewValue(true);
                    Emit(new AddrI { Dst = at, Label = buffer });
                    Emit(new StoreI { Place = slot, Src = at, Kind = MemKind.Q64 });
                    if (v.GrowingElements)
                    {
                        string roomPlace = $"rbp - {top - PhiType.AddressSize * (count + k)}";
                        VReg mark = fn.NewValue(false);
                        Emit(new ConstI { Dst = mark, Value = unchecked((int)(0x80000000u | (uint)v.Capacity)) });
                        Emit(new StoreI { Place = roomPlace, Src = mark, Kind = MemKind.D32 });
                        fn.OwnedTableTexts.Add((slot, roomPlace));
                    }
                }
            }
        }

        /// <summary>A throw's text is being built: cut short, it's kept as far as it goes (as the generator's textMayCut).</summary>
        bool textMayCut;

        /// <summary>A str with a size to build in: its address and its room, or null.</summary>
        (VReg At, int Room)? FixedPlace(Expr target)
        {
            switch (target)
            {
                case NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v):
                {
                    VReg d = fn.NewValue(true);
                    Emit(new AddrI { Dst = d, Label = TextPlace(v) });
                    return (d, v.Capacity);
                }
                case NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false } g } when !Ours(g) && !g.Type.IsArray && g.Capacity > 0 && SimpleLabel(g.Label):
                {
                    VReg d = fn.NewValue(true);
                    Emit(new AddrI { Dst = d, Label = g.Label });
                    return (d, g.Capacity);
                }
            }
            if (target.Type.IsInlineText && target.Type.Room > 0 && CanAddress(target)) return (AddressOf(target), target.Type.Room);
            if (target is IndexExpr fixedText && TextTableElement(fixedText) && fixedText.Target is NameExpr { Symbol: VariableSymbol { GrowingElements: false, Capacity: > 0 } sized })
                return (LoadAt(TextSlotOf(fixedText), 0, PhiType.PointerTo(PhiType.U8)), sized.Capacity);
            return null;
        }

        bool CanFix(Expr target) => target switch
        {
            NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v) => true,
            NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false } g } when !Ours(g) && !g.Type.IsArray && g.Capacity > 0 && SimpleLabel(g.Label) => true,
            IndexExpr fixedText when TextTableElement(fixedText) && fixedText.Target is NameExpr { Symbol: VariableSymbol { GrowingElements: false, Capacity: > 0 } } => true,
            _ => target.Type.IsInlineText && target.Type.Room > 0 && CanAddress(target),
        };

        /// <summary>Text built where it's used ('a' n 'b'): in 256 bytes of the frame of its own, as the generator's 64-bit code does.</summary>
        VReg BuildInline(TextExpr text)
        {
            if (inlineSlots.TryGetValue(text, out string? slot))
            {
                // into its own slot, which grows; its value is the text there ('' for none)
                for (int k = 0; k < text.Parts.Count; k++) PutPart(text.Parts[k], k > 0, null, null, slotTarget: null, slotHome: slot);
                if (text.Parts.Count == 0)
                {
                    VReg home = fn.NewValue(true);
                    Emit(new AddrI { Dst = home, Label = slot });
                    Emit(new GrowStoreI { Slot = home, Value = TextAddress(new StringExpr { Value = "", Type = PhiType.Str }), SourceIsString = true, MayCut = textMayCut });
                }
                VReg at = fn.NewValue(true), value = fn.NewValue(true);
                Emit(new LoadI { Dst = at, Place = slot, Kind = MemKind.Q64 });
                Emit(new TextI { Op = TextOp.OrEmpty, Dst = value, A = at });
                return value;
            }
            for (int k = 0; k < 32; k++) fn.NewHome();
            string place = $"rbp - {8 * fn.Homes}";
            for (int k = 0; k < text.Parts.Count; k++)
            {
                VReg at = fn.NewValue(true);
                Emit(new AddrI { Dst = at, Label = place });
                PutPart(text.Parts[k], k > 0, (at, 256), null);
            }
            if (text.Parts.Count == 0)
            {
                Emit(new StoreI { Place = place, Src = Opnd.Const(0), Kind = MemKind.U8 });
            }
            VReg d = fn.NewValue(true);
            Emit(new AddrI { Dst = d, Label = place });
            return d;
        }

        /// <summary>Where a text is: written in the program, a method's own, a class's, a struct's field ('' for one that grows and has none yet).</summary>
        VReg TextAddress(Expr e)
        {
            VReg d = fn.NewValue(true);
            switch (e)
            {
                case StringExpr s:
                    Emit(new TextI { Op = TextOp.Literal, Dst = d, Literal = s.Value });
                    return d;
                case NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v):
                    Emit(new AddrI { Dst = d, Label = TextPlace(v) });
                    return d;
                case TextExpr built:
                    return BuildInline(built);
                case IndexExpr element when TextTableElement(element):
                {
                    VReg at = TextSlotOf(element);
                    VReg text = LoadAt(at, 0, PhiType.PointerTo(PhiType.U8));
                    Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = text });
                    return d;
                }
                case CallExpr or NameExpr { Call: not null } or MemberExpr { Call: not null }:
                    return InReg(Value(e), true);
                case NameExpr { Symbol: VariableSymbol mine } when growingTexts.TryGetValue(mine, out string? home):
                {
                    VReg at = fn.NewValue(true);
                    Emit(new LoadI { Dst = at, Place = home, Kind = MemKind.Q64 });
                    Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = at });
                    return d;
                }
                case NameExpr { Symbol: VariableSymbol caller } when callersTexts.TryGetValue(caller, out VReg? slot):
                {
                    // (the caller's text: where its slot says, '' while it has none)
                    VReg at = LoadAt(slot, 0, PhiType.PointerTo(PhiType.U8));
                    Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = at });
                    return d;
                }
                case NameExpr { Symbol: VariableSymbol g } when g.Grows:
                {
                    VReg at = fn.NewValue(true);
                    Emit(new LoadI { Dst = at, Place = g.Label, Kind = MemKind.Q64 });
                    Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = at });
                    return d;
                }
                case NameExpr { Symbol: VariableSymbol g }:
                    Emit(new AddrI { Dst = d, Label = g.Label });
                    return d;
            }
            if (e.Type.IsInlineText) return AddressOf(e);
            if (e.Type.IsHeapText)
            {
                VReg slot = LoadAt(AddressOf(e), 0, PhiType.PointerTo(PhiType.U8));
                Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = slot });
                return d;
            }
            throw new NotYet($"text that's {Unknown(e)}");
        }

        /// <summary>A text's value, for where text goes: its address (a number written out as text is the generator's).</summary>
        Opnd TextValue(Expr e)
        {
            Need(e.Type.IsString, $"a {e.Type} written out as text");
            Need(CanText(e), $"text that's {Unknown(e)}");
            return TextAddress(e);
        }

        /// <summary>
        /// Two texts compared: the right side's address first, unless the left calls something
        /// (as the generator's EmitTextPair); 0 when they're the same text (is, is not), or -1, 0
        /// or 1 for their order.
        /// </summary>
        (VReg Result, Cond Cond) CompareTexts(BinaryExpr cmp)
        {
            VReg left, right;
            if (EvaluationOrder.HasEffect(cmp.Left))
            {
                left = (VReg)TextValue(cmp.Left).Reg!;
                right = (VReg)TextValue(cmp.Right).Reg!;
            }
            else
            {
                right = (VReg)TextValue(cmp.Right).Reg!;
                left = (VReg)TextValue(cmp.Left).Reg!;
            }
            bool order = cmp.Op is not (BinaryOp.Equal or BinaryOp.NotEqual);
            VReg d = fn.NewValue(false);
            Emit(new TextI { Op = order ? TextOp.Order : TextOp.Equal, Dst = d, A = left, B = right });
            Cond c = cmp.Op switch
            {
                BinaryOp.Equal => Cond.Eq,
                BinaryOp.NotEqual => Cond.Ne,
                BinaryOp.Less => Cond.LtS,
                BinaryOp.LessEqual => Cond.LeS,
                BinaryOp.Greater => Cond.GtS,
                _ => Cond.GeS,
            };
            return (d, c);
        }

        static bool TextCompare(BinaryExpr b) => IsComparison(b.Op) && b.Left.Type.IsString && b.Right.Type.IsString && !b.Left.Type.IsArray && !b.Right.Type.IsArray;

        /// <summary>How long a text is, measured.</summary>
        VReg TextLength(Expr text)
        {
            VReg at = (VReg)TextValue(text).Reg!;
            VReg d = fn.NewValue(false);
            Emit(new TextI { Op = TextOp.Length, Dst = d, A = at });
            return d;
        }

        /// <summary>A str of the method's own, or a class's with a size, given a text: copied in, too long an error.</summary>
        bool SetText(Expr target, Expr value)
        {
            if (TableSlot(target))
            {
                if (!(value.Type.IsString && CanText(value) || CanPart(value))) return false;
                PutText(target, value, append: false);
                return true;
            }
            if ((CanSlot(target) || TableSlot(target) || CanFix(target)) && Real(value).IsTextPointer)
            {
                PutText(target, value, append: false);
                return true;
            }
            if (CanSlot(target))
            {
                if (!(value.Type.IsString && CanText(value) || value.Type.IsNumeric || value.Type.IsWide || value.Type.IsFloat)) return false;
                PutText(target, value, append: false);
                return true;
            }
            if (CanFix(target) && !value.Type.IsString && CanPart(value))
            {
                PutText(target, value, append: false);
                return true;
            }
            string? place = target switch
            {
                NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v) => TextPlace(v),
                NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false } g } when !Ours(g) && !g.Type.IsArray && g.Capacity > 0 && SimpleLabel(g.Label) => g.Label,
                _ => null,
            };
            if (place == null && CanFix(target) && CanPart(value))
            {
                // a str field with a size (through an object, a pointer, self): the fixed store
                PutText(target, value, append: false);
                return true;
            }
            if (place == null || !value.Type.IsString || !CanText(value)) return false;
            int room = target is NameExpr { Symbol: VariableSymbol t } ? t.Capacity : 0;
            VReg from = TextAddress(value);
            Emit(new TextI { Op = TextOp.Copy, A = from, Place = place, Room = room, Held = HeldStores() });
            return true;
        }
    }
}

using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // Arrays that are added to and cut short, as the generator has them: an [atmost N] one
    // keeps how many it has after its elements; one without a size (a struct's, or a class's
    // that grows) keeps its elements' address, how many and its room, and is given more room
    // by phi_array_grow. Arrays of numbers (and vecs); those of text and objects, which have
    // something to free, are still the generator's.
    public sealed partial class IrBuilder
    {
        /// <summary>An array this form adds to and cuts short: of numbers, an [atmost N] one or one that grows.</summary>
        bool Resizable(Expr array) =>
            array.Type.Element is var e && Supported(e) && !e.IsObject && !e.IsString && e.ElementSize is 1 or 2 or 4 or 8 or 16
            && (array.Type.Filled && !array.Type.Open && CanAddress(array)
                || array.Type.Open && Binder.IsFieldView(array) && CanAddress(array)
                || array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g));

        /// <summary>
        /// list has x: 1 when an element is x, compared as the generator's EmitHas does: x worked out
        /// first, then each element in turn while the index is under how many it has now (numbers
        /// in 32 bits, each widened by its own sign, or all 64 of an 8-byte one; texts as is does;
        /// objects and structs by their struct's equal); text has x or a character, by the library.
        /// </summary>
        VReg Has(HasExpr has)
        {
            if (has.Kind is HasKind.Text or HasKind.Character)
            {
                Opnd sought = has.Kind == HasKind.Text ? TextValue(has.Value) : InReg(Value(has.Value), false);
                VReg within = (VReg)TextValue(has.Collection).Reg!;
                VReg found = fn.NewValue(false);
                Emit(new HasTextI { Dst = found, Text = within, Value = sought, Character = has.Kind == HasKind.Character });
                return found;
            }
            Expr list = has.Collection;
            PhiType element = list.Type.Element;
            int size = element.ElementSize;
            Need(!(has.Kind == HasKind.Numbers && (IsF(element) || element.Kind == TypeKind.F16)), "has on floats");
            Need(CanCount(list) && CanElements(list), $"has on {Unknown(list)}");
            Opnd value = has.Kind switch
            {
                HasKind.Texts => TextValue(has.Value),
                HasKind.Objects or HasKind.Structs => ObjectAddress(has.Value),
                _ => size == 8 ? InReg(Value(has.Value, PhiType.I64), Addr) : InReg(Value(has.Value, PhiType.Int), false),
            };
            VReg d = fn.NewValue(false);
            Emit(new ConstI { Dst = d, Value = 0 });
            VReg i = fn.NewAddress();
            Emit(new ConstI { Dst = i, Value = 0 });
            Block test = fn.NewBlock(), body = fn.NewBlock(), next = fn.NewBlock(), yes = fn.NewBlock(), done = fn.NewBlock();
            StartBlock(test);
            VReg count = InReg(ArrayCount(list), false), wideCount = fn.NewAddress();
            Emit(new ExtendI { Dst = wideCount, Src = count, Signed = false });
            current.End = new BranchT { Cond = Cond.LtU, A = i, B = wideCount, Then = body, Else = done };
            Enter(body);
            VReg at = Offset(ElementsOf(list), i, size, fn.NewAddress());
            VReg same;
            switch (has.Kind)
            {
                case HasKind.Numbers:
                {
                    MemKind kind = size switch { 1 => element.IsSigned ? MemKind.S8 : MemKind.U8, 2 => element.IsSigned ? MemKind.S16 : MemKind.U16, 8 => MemKind.Q64, _ => MemKind.D32 };
                    VReg e = fn.NewValue(size == 8);
                    Emit(new LoadIdxI { Dst = e, Base = at, Index = Opnd.Const(0), Scale = 1, Kind = kind });
                    current.End = new BranchT { Cond = Cond.Eq, A = e, B = value, Then = yes, Else = next };
                    break;
                }
                case HasKind.Texts:
                {
                    VReg text;
                    if (element.IsInlineText) text = at;
                    else
                    {
                        VReg slot = LoadAt(at, 0, PhiType.PointerTo(PhiType.U8));
                        text = fn.NewAddress();
                        Emit(new TextI { Op = TextOp.OrEmpty, Dst = text, A = slot });
                    }
                    same = fn.NewValue(false);
                    Emit(new TextI { Op = TextOp.Equal, Dst = same, A = text, B = (VReg)value.Reg! });
                    current.End = new BranchT { Cond = Cond.Eq, A = same, B = Opnd.Const(0), Then = yes, Else = next };
                    break;
                }
                default:
                {
                    VReg obj = has.Kind == HasKind.Objects ? LoadAt(at, 0, element) : at;
                    same = fn.NewValue(false);
                    Emit(new ObjCallI { Kind = "equal", Struct = element.Struct!, Arg = obj, Second = (VReg)value.Reg!, Dst = same });
                    current.End = new BranchT { Cond = Cond.Ne, A = same, B = Opnd.Const(0), Then = yes, Else = next };
                    break;
                }
            }
            Enter(next);
            Emit(new BinI { Op = BinOp.Add, Dst = i, A = i, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(yes);
            Emit(new ConstI { Dst = d, Value = 1 });
            current.End = new JumpT { Target = done };
            Enter(done);
            return d;
        }

        /// <summary>
        /// b is a; for arrays: of numbers or structs that own nothing, its bytes; of objects (one not
        /// inside the other), each element set from the source's. The target fits the source's count
        /// first: one that grows grows to it (letting go of objects past it), an atmost one takes it
        /// (more than it holds an index error), any other must have that many (the generator's).
        /// </summary>
        void AssignArrays(ArrayAssignStmt copy)
        {
            Expr target = copy.Target, source = copy.Source;
            PhiType t = target.Type, element = t.Element;
            Mark(copy);
            if (target is NameExpr { Symbol: VariableSymbol into } && source is NameExpr { Symbol: VariableSymbol from } && into == from) return;
            bool objects = element.IsObject;
            if (copy.Overlaps)
            {
                CopyOverlapping(copy);
                return;
            }
            if (t.TextTable && !copy.Overlaps)
            {
                AssignTexts(copy);
                return;
            }
            Need(t.Kind != TypeKind.Str, "an array of texts in place copied");
            Need(objects ? element.Struct != null : Reachable(element) && !(InPlaceStruct(element) && element.Struct!.OwnsObjects),
                 $"an array of {element} copied");
            Need(CanCount(source) && CanElements(source) && CanElements(target), $"{Unknown(target)} given {Unknown(source)}");
            bool grows = target.Type.Open && (target is NameExpr { Symbol: VariableSymbol g } && GrowingView(g) || Binder.IsFieldView(target) && CanAddress(target));
            Need(grows || !t.Open || target is NameExpr { Symbol: VariableSymbol pv } && paramViews.ContainsKey(pv), $"{Unknown(target)} given an array");
            int size = element.ElementSize;
            VReg count = InReg(ArrayCount(source), false);
            VReg wide = fn.NewAddress();
            Emit(new ExtendI { Dst = wide, Src = count, Signed = false });
            if (grows)
            {
                Emit(new ObjectLineI { At = copy });
                VReg view = ViewOf(target);
                Emit(new ArrayGrowI { View = view, Count = count, Size = size });
                if (objects) FreeFrom(target, count);
                Emit(new StoreIdxI { Base = ViewOf(target), Index = Opnd.Const(ViewCount), Scale = 1, Src = count, Kind = MemKind.D32 });
            }
            else if (t.Filled && !t.Open)
            {
                Emit(new CheckI { Index = wide, Limit = t.Length + 1, At = copy, Held = HeldStores() });
                if (objects) FreeFrom(target, count);
                Emit(new StoreIdxI { Base = AddressOf(target), Index = Opnd.Const(t.CountOffset), Scale = 1, Src = count, Kind = MemKind.D32 });
            }
            else if (t.Open || source.Type.Open)
            {
                // the same number of elements: no more (count below its own + 1) and no fewer
                VReg have = fn.NewAddress();
                if (t.Open)
                {
                    VReg its = LoadAt(ViewOf(target), ViewCount, PhiType.U32);
                    Emit(new ExtendI { Dst = have, Src = its, Signed = false });
                }
                else Emit(new ConstI { Dst = have, Value = t.Length });
                VReg haveMore = fn.NewAddress(), countMore = fn.NewAddress();
                Emit(new BinI { Op = BinOp.Add, Dst = haveMore, A = have, B = Opnd.Const(1) });
                Emit(new BinI { Op = BinOp.Add, Dst = countMore, A = wide, B = Opnd.Const(1) });
                Emit(new CheckI { Index = wide, LimitValue = haveMore, At = copy, Held = HeldStores() });
                Emit(new CheckI { Index = have, LimitValue = countMore, At = copy, Held = HeldStores() });
            }
            if (!objects)
            {
                VReg fromElements = ElementsOf(source), toElements = ElementsOf(target), bytes = fn.NewAddress();
                Emit(new BinI { Op = BinOp.Mul, Dst = bytes, A = wide, B = Opnd.Const(size) });
                Emit(new CopyBytesI { Dst = toElements, Src = fromElements, Count = bytes });
                return;
            }
            // each one: the target's object set from the source's (or emptied when that's empty)
            VReg k = fn.NewAddress();
            Emit(new ConstI { Dst = k, Value = 0 });
            Block test = fn.NewBlock(), body = fn.NewBlock(), after = fn.NewBlock();
            StartBlock(test);
            current.End = new BranchT { Cond = Cond.LtU, A = k, B = wide, Then = body, Else = after };
            Enter(body);
            VReg theirs = LoadAt(Offset(ElementsOf(source), k, size, fn.NewAddress()), 0, element);
            VReg slot = Offset(ElementsOf(target), k, size, fn.NewAddress());
            Emit(new ObjCallI { Kind = "set", Struct = element.Struct!, Arg = slot, Second = theirs });
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(after);
        }

        /// <summary>
        /// b is a; when one may be inside the other (tree.kids:0.kids is tree.kids), as the
        /// generator: both arrays that grow, the whole copy made first (phi_array_clone into a view
        /// of its own), the target's let go, and the copy's its view; a target of objects of a
        /// size or atmost, a copy of each of the source's objects made first, the last first, then
        /// each slot given its copy and what it had freed.
        /// </summary>
        void CopyOverlapping(ArrayAssignStmt copy)
        {
            Expr target = copy.Target, source = copy.Source;
            PhiType t = target.Type, element = t.Element;
            // (a struct's str array too, as the generator's GrowingView: the routines copy and free its texts)
            bool GrowsView(Expr e) => e.Type.Open
                && (Binder.IsFieldView(e) && CanAddress(e) || e is NameExpr { Symbol: VariableSymbol g } && GrowingView(g) && !e.Type.TextTable);
            if (GrowsView(target) && GrowsView(source))
            {
                Emit(new ObjectLineI { At = copy });
                fn.NewHome();
                fn.NewHome();
                string made = $"rbp - {8 * fn.Homes}";
                VReg madeAt = fn.NewAddress();
                Emit(new AddrI { Dst = madeAt, Label = made });
                Emit(new ArrayRoutineI { Kind = "clone", Element = element, View = ViewOf(source), Made = madeAt });
                VReg into = ViewOf(target);
                Emit(new ArrayRoutineI { Kind = "free", Element = element, View = into });
                // (the copy's view is the target's now: where its elements are, how many, its room)
                if (PhiType.AddressSize == 8)
                {
                    VReg first = fn.NewAddress(), second = fn.NewAddress();
                    Emit(new LoadI { Dst = first, Place = made, Kind = MemKind.Q64 });
                    Emit(new LoadI { Dst = second, Place = $"{made} + 8", Kind = MemKind.Q64 });
                    VReg again = ViewOf(target);
                    Emit(new StoreIdxI { Base = again, Index = Opnd.Const(0), Scale = 1, Src = first, Kind = MemKind.Q64 });
                    Emit(new StoreIdxI { Base = again, Index = Opnd.Const(8), Scale = 1, Src = second, Kind = MemKind.Q64 });
                }
                else
                    foreach (int at in new[] { 0, ViewCount, ViewMax })
                    {
                        VReg part = fn.NewValue(false);
                        Emit(new LoadI { Dst = part, Place = $"{made} + {at}", Kind = MemKind.D32 });
                        Emit(new StoreIdxI { Base = ViewOf(target), Index = Opnd.Const(at), Scale = 1, Src = part, Kind = MemKind.D32 });
                    }
                return;
            }
            // (one that grows grows to fit after the copies are made; otherwise how many there can be is the target's size, or the source's)
            bool grows = GrowsView(target);
            int most = grows ? source.Type.Length : t.Length;
            Need(element.IsObject && element.Struct != null && (grows ? !source.Type.Open : !t.Open) && most is > 0 and <= 64,
                 $"{Unknown(target)} given {Unknown(source)}, which may be inside it");
            Need(CanCount(source) && CanElements(source) && CanElements(target), $"{Unknown(target)} given {Unknown(source)}");
            Emit(new ObjectLineI { At = copy });
            VReg count = InReg(ArrayCount(source), false), wide = fn.NewAddress();
            Emit(new ExtendI { Dst = wide, Src = count, Signed = false });
            // (only a check: nothing is copied if they don't fit)
            if (grows) { }
            else if (t.Filled) Emit(new CheckI { Index = wide, Limit = t.Length + 1, At = copy, Held = HeldStores() });
            else
            {
                VReg have = fn.NewAddress(), haveMore = fn.NewAddress(), countMore = fn.NewAddress();
                Emit(new ConstI { Dst = have, Value = t.Length });
                Emit(new BinI { Op = BinOp.Add, Dst = haveMore, A = have, B = Opnd.Const(1) });
                Emit(new BinI { Op = BinOp.Add, Dst = countMore, A = wide, B = Opnd.Const(1) });
                Emit(new CheckI { Index = wide, LimitValue = haveMore, At = copy, Held = HeldStores() });
                Emit(new CheckI { Index = have, LimitValue = countMore, At = copy, Held = HeldStores() });
            }
            // the copies, the last first, into homes of their own
            for (int h = 0; h < most; h++) fn.NewHome();
            string copies = $"rbp - {8 * fn.Homes}";
            VReg k = fn.NewAddress();
            Emit(new CopyI { Dst = k, Src = wide });
            Block test = fn.NewBlock(), body = fn.NewBlock(), made2 = fn.NewBlock();
            StartBlock(test);
            current.End = new BranchT { Cond = Cond.Ne, A = k, B = Opnd.Const(0), Then = body, Else = made2 };
            Enter(body);
            Emit(new BinI { Op = BinOp.Sub, Dst = k, A = k, B = Opnd.Const(1) });
            VReg theirs = LoadAt(Offset(ElementsOf(source), k, PhiType.AddressSize, fn.NewAddress()), 0, element);
            VReg cloned = fn.NewAddress();
            Emit(new ObjCallI { Kind = "clone", Struct = element.Struct!, Arg = theirs, Dst = cloned });
            VReg buffer = fn.NewAddress();
            Emit(new AddrI { Dst = buffer, Label = copies });
            Emit(new StoreIdxI { Base = buffer, Index = k, Scale = PhiType.AddressSize, Src = cloned, Kind = Ptr });
            current.End = new JumpT { Target = test };
            Enter(made2);
            if (grows)
            {
                // it grows to fit, and has that many (letting go of objects past them)
                Emit(new ArrayGrowI { View = ViewOf(target), Count = count, Size = PhiType.AddressSize });
                FreeFrom(target, count);
                Emit(new StoreIdxI { Base = ViewOf(target), Index = Opnd.Const(ViewCount), Scale = 1, Src = count, Kind = MemKind.D32 });
            }
            else if (t.Filled)
            {
                FreeFrom(target, count);
                Emit(new StoreIdxI { Base = AddressOf(target), Index = Opnd.Const(t.CountOffset), Scale = 1, Src = count, Kind = MemKind.D32 });
            }
            // each slot given its copy, and what it had freed
            VReg j = fn.NewAddress();
            Emit(new ConstI { Dst = j, Value = 0 });
            Block put = fn.NewBlock(), putBody = fn.NewBlock(), done = fn.NewBlock();
            StartBlock(put);
            current.End = new BranchT { Cond = Cond.LtU, A = j, B = wide, Then = putBody, Else = done };
            Enter(putBody);
            VReg slot = Offset(ElementsOf(target), j, PhiType.AddressSize, fn.NewAddress());
            VReg had = LoadAt(slot, 0, element);
            VReg buffer2 = fn.NewAddress(), mine = fn.NewAddress();
            Emit(new AddrI { Dst = buffer2, Label = copies });
            Emit(new LoadIdxI { Dst = mine, Base = buffer2, Index = j, Scale = PhiType.AddressSize, Kind = Ptr });
            Emit(new StoreIdxI { Base = slot, Index = Opnd.Const(0), Scale = 1, Src = mine, Kind = Ptr });
            Emit(new ObjCallI { Kind = "free", Struct = element.Struct!, Arg = had });
            Emit(new BinI { Op = BinOp.Add, Dst = j, A = j, B = Opnd.Const(1) });
            current.End = new JumpT { Target = put };
            Enter(done);
        }

        /// <summary>
        /// b is a; for str arrays without a size: the target fits the source's count (one that grows
        /// gives back the texts past it, then grows to it; an atmost one takes it; any other must have
        /// as many), then each text is put in its element, growing it (the generator's).
        /// </summary>
        void AssignTexts(ArrayAssignStmt copy)
        {
            Expr target = copy.Target, source = copy.Source;
            PhiType t = target.Type;
            Need(!copy.Overlaps && source.Type.TextTable, "an array of text copied from texts in place");
            Need(CanCount(source) && CanElements(source), $"{Unknown(target)} given {Unknown(source)}");
            bool grows = t.Open && (target is NameExpr { Symbol: VariableSymbol g } && GrowingView(g) || Binder.IsFieldView(target) && CanAddress(target));
            bool table = !t.Open && target is NameExpr { Symbol: VariableSymbol { GrowingElements: true } tv } && !Ours(tv) && SimpleLabel(tv.Label);
            Need(grows || table, $"{Unknown(target)} given an array of text");
            VReg count = InReg(ArrayCount(source), false);
            VReg wide = fn.NewAddress();
            Emit(new ExtendI { Dst = wide, Src = count, Signed = false });
            if (grows)
            {
                Emit(new ObjectLineI { At = copy });
                FreeTextsFrom(target, wide);
                Emit(new ArrayGrowI { View = ViewOf(target), Count = count, Size = PhiType.AddressSize, Texts = true });
                Emit(new StoreIdxI { Base = ViewOf(target), Index = Opnd.Const(ViewCount), Scale = 1, Src = count, Kind = MemKind.D32 });
            }
            else if (t.Filled)
            {
                Emit(new CheckI { Index = wide, Limit = t.Length + 1, At = copy, Held = HeldStores() });
                Emit(new StoreIdxI { Base = AddressOf(target), Index = Opnd.Const(t.CountOffset), Scale = 1, Src = count, Kind = MemKind.D32 });
            }
            else if (source.Type.Open)
            {
                VReg have = fn.NewAddress(), haveMore = fn.NewAddress(), countMore = fn.NewAddress();
                Emit(new ConstI { Dst = have, Value = t.Length });
                Emit(new BinI { Op = BinOp.Add, Dst = haveMore, A = have, B = Opnd.Const(1) });
                Emit(new BinI { Op = BinOp.Add, Dst = countMore, A = wide, B = Opnd.Const(1) });
                Emit(new CheckI { Index = wide, LimitValue = haveMore, At = copy, Held = HeldStores() });
                Emit(new CheckI { Index = have, LimitValue = countMore, At = copy, Held = HeldStores() });
            }
            VReg k = fn.NewAddress();
            Emit(new ConstI { Dst = k, Value = 0 });
            Block test = fn.NewBlock(), body = fn.NewBlock(), after = fn.NewBlock();
            StartBlock(test);
            current.End = new BranchT { Cond = Cond.LtU, A = k, B = wide, Then = body, Else = after };
            Enter(body);
            VReg raw = LoadAt(Offset(ElementsOf(source), k, PhiType.AddressSize, fn.NewAddress()), 0, PhiType.PointerTo(PhiType.U8));
            VReg text = fn.NewAddress();
            Emit(new TextI { Op = TextOp.OrEmpty, Dst = text, A = raw });
            VReg slot = Offset(grows ? LoadAt(ViewOf(target), 0, PhiType.PointerTo(PhiType.U8)) : AddressOf(target), k, PhiType.AddressSize, fn.NewAddress());
            if (grows)
            {
                VReg most = LoadAt(ViewOf(target), ViewMax, PhiType.U32);
                fn.NewHome();
                string rooms = $"rbp - {8 * fn.Homes}";
                Emit(new StoreI { Place = rooms, Src = most, Kind = MemKind.D32 });
                Emit(new GrowStoreI { Slot = slot, Value = text, SourceIsString = true, RoomOffset = 0, Rooms = rooms });
            }
            else Emit(new GrowStoreI { Slot = slot, Value = text, SourceIsString = true, RoomOffset = PhiType.AddressSize * ((VariableSymbol)((NameExpr)target).Symbol!).Count });
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(after);
        }

        /// <summary>
        /// a is b for arrays: 1 when they have as many and each element is the same (objects by
        /// their equal, texts as is compares them, anything else its bytes), as the generator's.
        /// </summary>
        VReg ArraysEqual(BinaryExpr cmp)
        {
            Expr left = cmp.Left, right = cmp.Right;
            PhiType element = left.Type.Element;
            Need(CanCount(left) && CanCount(right) && CanElements(left) && CanElements(right), $"{Unknown(left)} compared with {Unknown(right)}");
            Need(element.Kind != TypeKind.Str || left.Type.TextTable && right.Type.TextTable, "arrays of texts in place compared");
            VReg d = fn.NewValue(false);
            Emit(new ConstI { Dst = d, Value = 0 });
            VReg n = InReg(ArrayCount(left), false), m = InReg(ArrayCount(right), false);
            Block same = fn.NewBlock(), done = fn.NewBlock();
            if (current.End != null) Enter(fn.NewBlock());
            current.End = new BranchT { Cond = Cond.Eq, A = n, B = m, Then = same, Else = done };
            Enter(same);
            VReg count = fn.NewAddress();
            Emit(new ExtendI { Dst = count, Src = n, Signed = false });
            if (!element.IsObject && element.Kind != TypeKind.Str)
            {
                VReg bytes = fn.NewAddress();
                Emit(new BinI { Op = BinOp.Mul, Dst = bytes, A = count, B = Opnd.Const(element.ElementSize) });
                VReg rightAt = ElementsOf(right), leftAt = ElementsOf(left);
                Emit(new CompareBytesI { Dst = d, A = leftAt, B = rightAt, Count = bytes });
                current.End = new JumpT { Target = done };
                Enter(done);
                return d;
            }
            VReg k = fn.NewAddress();
            Emit(new ConstI { Dst = k, Value = 0 });
            Block test = fn.NewBlock(), body = fn.NewBlock(), next = fn.NewBlock(), all = fn.NewBlock();
            StartBlock(test);
            current.End = new BranchT { Cond = Cond.LtU, A = k, B = count, Then = body, Else = all };
            Enter(body);
            VReg theirs = LoadAt(Offset(ElementsOf(right), k, PhiType.AddressSize, fn.NewAddress()), 0, PhiType.PointerTo(PhiType.U8));
            VReg ours = LoadAt(Offset(ElementsOf(left), k, PhiType.AddressSize, fn.NewAddress()), 0, PhiType.PointerTo(PhiType.U8));
            VReg r = fn.NewValue(false);
            if (element.IsObject)
            {
                Emit(new ObjCallI { Kind = "equal", Struct = element.Struct!, Arg = ours, Second = theirs, Dst = r });
                current.End = new BranchT { Cond = Cond.Ne, A = r, B = Opnd.Const(0), Then = next, Else = done };
            }
            else
            {
                VReg a = fn.NewAddress(), b = fn.NewAddress();
                Emit(new TextI { Op = TextOp.OrEmpty, Dst = a, A = ours });
                Emit(new TextI { Op = TextOp.OrEmpty, Dst = b, A = theirs });
                Emit(new TextI { Op = TextOp.Equal, Dst = r, A = a, B = b });
                current.End = new BranchT { Cond = Cond.Eq, A = r, B = Opnd.Const(0), Then = next, Else = done };
            }
            Enter(next);
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(all);
            Emit(new ConstI { Dst = d, Value = 1 });
            current.End = new JumpT { Target = done };
            Enter(done);
            return d;
        }

        /// <summary>An array that grows kept by name: a class's, or the method's own (of numbers or structs, in its frame).</summary>
        bool GrowingView(VariableSymbol g) => g.GrowingArray && (!Ours(g) && AnyGlobal(g) != null || localViews.ContainsKey(g));

        /// <summary>Where its view is (its elements' address, how many, its room).</summary>
        string ViewHome(VariableSymbol g) => localViews.TryGetValue(g, out string? home) ? home : AnyGlobal(g)!;

        /// <summary>The method's own arrays that grow: each one's view in its frame (empty to start; its elements freed at its end and by a throw going past).</summary>
        readonly Dictionary<VariableSymbol, string> localViews = new();

        static bool LocalView(VariableSymbol v) => v.GrowingArray && v.IsLocal
            && (v.Type.TextTable
                || v.Type.Kind != TypeKind.Str && (Supported(v.Type.Element) && v.Type.Element.ElementSize is 1 or 2 or 4 or 8 or 16 && !v.Type.Element.IsObject
                                                   || InPlaceStruct(v.Type.Element)
                                                   || v.Type.Element.IsObject && v.Type.Element.Struct != null));

        /// <summary>A method's own array that grows whose elements have something to give back: objects, or texts.</summary>
        static bool OwnsElements(VariableSymbol v) => v.Type.TextTable || v.Type.Element.IsObject;

        /// <summary>Its objects or texts given back, then its elements' room (what the generator's EmitGrowingArrayFree does).</summary>
        void FreeView(VariableSymbol v)
        {
            var list = new NameExpr { Name = v.Name, Symbol = v, Type = v.Type };
            VReg zero = fn.NewAddress();
            Emit(new ConstI { Dst = zero, Value = 0 });
            if (v.Type.TextTable) FreeTextsFrom(list, zero);
            else if (v.Type.Element.IsObject) FreeFrom(list, zero);
            Emit(new FreeTextI { Place = localViews[v] });
        }

        /// <summary>An array parameter (int list[]) of numbers, structs or objects: a view the caller pushes (text arrays are the generator's).</summary>
        static bool ArrayParameter(VariableSymbol p) => p.Type.Open && (p.Type.TextTable || p.Type.Kind != TypeKind.Str && Reachable(p.Type.Element));

        /// <summary>Each array parameter's view: where it is in the frame (above rbp).</summary>
        readonly Dictionary<VariableSymbol, string> paramViews = new();

        /// <summary>
        /// An array given for an array parameter, as the generator's PushArrayView64: how many it
        /// has and the most it holds as one 64-bit value (the most above), and its elements' address.
        /// </summary>
        (Opnd Both, Opnd Elements) ArrayArg(Expr? array)
        {
            if (array == null) return (Opnd.Const(0), Opnd.Const(0));
            if (array is PointerViewExpr pv)
            {
                VReg n = InReg(Value(pv.Count, PhiType.U32), false);
                VReg wide = fn.NewAddress(), high = fn.NewAddress(), both = fn.NewAddress();
                Emit(new ExtendI { Dst = wide, Src = n, Signed = false });
                Emit(new BinI { Op = BinOp.Shl, Dst = high, A = wide, B = Opnd.Const(32) });
                Emit(new BinI { Op = BinOp.Or, Dst = both, A = wide, B = high });
                return (both, InReg(Value(pv.Pointer), Addr));
            }
            if (array.Type.Open)
            {
                Need(array.Type.Open && (Binder.IsFieldView(array) && CanAddress(array) || CanAddress(array) && array is NameExpr { Symbol: VariableSymbol p } && paramViews.ContainsKey(p)
                     || array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g)), $"{Unknown(array)} given as an array");
                VReg view = ViewOf(array);
                return (LoadAt(view, ViewCount, PhiType.U64), LoadAt(view, 0, PhiType.PointerTo(array.Type.Element)));
            }
            Need(!array.Type.TextTable || array is NameExpr { Symbol: VariableSymbol { GrowingArray: false } t } && (!Ours(t) && SimpleLabel(t.Label) || localTables.ContainsKey(t)),
                 $"{Unknown(array)} given as an array of text");
            if (array.Type.TextTable)
            {
                // a class's str array, or the method's own: its table of where each text is
                var table = (VariableSymbol)((NameExpr)array).Symbol!;
                VReg at = fn.NewAddress(), full = fn.NewAddress();
                Emit(new AddrI { Dst = at, Label = localTables.TryGetValue(table, out string? mine) ? mine : table.Label });
                Emit(new ConstI { Dst = full, Value = ((long)array.Type.Length << 32) | (uint)array.Type.Length });
                return (full, at);
            }
            Need(CanCount(array) && CanAddress(array), $"{Unknown(array)} given as an array");
            VReg count = InReg(ArrayCount(array), false);
            VReg low = fn.NewAddress(), size = fn.NewAddress(), most = fn.NewAddress();
            Emit(new ExtendI { Dst = low, Src = count, Signed = false });
            Emit(new ConstI { Dst = size, Value = (long)array.Type.Length << 32 });
            Emit(new BinI { Op = BinOp.Or, Dst = most, A = low, B = size });
            return (most, AddressOf(array));
        }

        /// <summary>An array given for an array parameter in 32-bit code (PushArrayView's): the most it holds, how many it has, its elements.</summary>
        (Opnd Most, Opnd Count, Opnd Elements) ArrayArg32(Expr? array)
        {
            if (array == null) return (Opnd.Const(0), Opnd.Const(0), Opnd.Const(0));
            if (array is PointerViewExpr pv)
            {
                VReg n = InReg(Value(pv.Count, PhiType.U32), false);
                return (n, n, InReg(Value(pv.Pointer), Addr));
            }
            if (array.Type.Open)
            {
                Need(Binder.IsFieldView(array) && CanAddress(array) || CanAddress(array) && array is NameExpr { Symbol: VariableSymbol p } && paramViews.ContainsKey(p)
                     || array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g), $"{Unknown(array)} given as an array");
                VReg view = ViewOf(array);
                return (LoadAt(view, ViewMax, PhiType.U32), LoadAt(view, ViewCount, PhiType.U32), LoadAt(view, 0, PhiType.PointerTo(array.Type.Element)));
            }
            Need(!array.Type.TextTable || array is NameExpr { Symbol: VariableSymbol { GrowingArray: false } t } && (!Ours(t) && SimpleLabel(t.Label) || localTables.ContainsKey(t)),
                 $"{Unknown(array)} given as an array of text");
            if (array.Type.TextTable)
            {
                var table = (VariableSymbol)((NameExpr)array).Symbol!;
                VReg at = fn.NewAddress();
                Emit(new AddrI { Dst = at, Label = localTables.TryGetValue(table, out string? mine) ? mine : table.Label });
                return (Opnd.Const(array.Type.Length), Opnd.Const(array.Type.Length), at);
            }
            Need(CanCount(array) && CanAddress(array), $"{Unknown(array)} given as an array");
            Opnd count = ArrayCount(array);
            return (Opnd.Const(array.Type.Length), count, AddressOf(array));
        }

        /// <summary>Where an array that grows keeps its elements' address, count and room.</summary>
        VReg ViewOf(Expr array)
        {
            if (array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g))
            {
                VReg d = fn.NewAddress();
                Emit(new AddrI { Dst = d, Label = ViewHome(g) });
                return d;
            }
            return AddressOf(array);
        }

        /// <summary>An array this form adds to: one Resizable, or of structs kept in place or objects (not an interface's).</summary>
        /// <summary>A str array without a most that grows (a struct's, or a class's): texts added to its end.</summary>
        bool TextsAddable(Expr array) => array.Type.TextTable && array.Type.Open
            && (Binder.IsFieldView(array) && CanAddress(array) || array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g));

        /// <summary>
        /// list++ text; on a str array that grows: the value worked out, room made for one more
        /// (phi_text_array_grow: a new element's text is the empty one), then the value put in
        /// it, its room .max slots on (or, given none, its text emptied), as the generator's.
        /// </summary>
        void AddTexts(AppendStmt append)
        {
            Expr array = append.Target;
            Mark(append);
            var values = append.Values.Count == 0 ? new List<Expr?> { null } : append.Values.Cast<Expr?>().ToList();
            foreach (Expr? value in values)
            {
                var part = value != null ? PartValue(value) : default;
                Emit(new ObjectLineI { At = append });
                VReg view = ViewOf(array);
                VReg had = LoadAt(view, ViewCount, PhiType.U32);
                VReg need = fn.NewValue(false);
                Emit(new BinI { Op = BinOp.Add, Dst = need, A = had, B = Opnd.Const(1) });
                Emit(new ArrayGrowI { View = view, Count = need, Size = PhiType.AddressSize, Texts = true });
                VReg now = LoadAt(view, ViewCount, PhiType.U32);
                VReg more = fn.NewValue(false);
                Emit(new BinI { Op = BinOp.Add, Dst = more, A = now, B = Opnd.Const(1) });
                Emit(new StoreIdxI { Base = view, Index = Opnd.Const(ViewCount), Scale = 1, Src = more, Kind = MemKind.D32 });
                VReg elements = LoadAt(view, 0, PhiType.PointerTo(PhiType.U8));
                VReg at = fn.NewAddress();
                Emit(new ExtendI { Dst = at, Src = now, Signed = false });
                VReg slot = Offset(elements, at, PhiType.AddressSize, fn.NewAddress());
                if (value == null)
                {
                    VReg text = LoadAt(slot, 0, PhiType.PointerTo(PhiType.U8));
                    Emit(new StoreIdxI { Base = text, Index = Opnd.Const(0), Scale = 1, Src = Opnd.Const(0), Kind = MemKind.U8 });
                    continue;
                }
                VReg most = LoadAt(view, ViewMax, PhiType.U32);
                fn.NewHome();
                string rooms = $"rbp - {8 * fn.Homes}";
                Emit(new StoreI { Place = rooms, Src = most, Kind = MemKind.D32 });
                Emit(new GrowStoreI { Slot = slot, Value = part.Value, SourceIsString = part.IsText, Unsigned = part.Unsigned, Known = part.Known,
                                      RoomOffset = 0, Rooms = rooms });
            }
        }

        bool Addable(Expr array) =>
            array.Type.Element is var e && (InPlaceStruct(e) || e.IsObject && e.Struct != null)
            && (array.Type.Filled && !array.Type.Open && CanAddress(array)
                || array.Type.Open && Binder.IsFieldView(array) && CanAddress(array)
                || array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g))
            || Resizable(array);

        /// <summary>
        /// list++ x; (or list++; for a zero): at its end, with room made (or full an error), as the
        /// generator's EmitAppend. A struct is copied from where it is (from inside the array too,
        /// list++ list:0, found again after the array has moved); an object is cloned (a call's
        /// new one is taken), before the array changes, and none given is a zeroed one.
        /// </summary>
        void Add(AppendStmt append)
        {
            Expr array = append.Target;
            if (TextsAddable(array))
            {
                AddTexts(append);
                return;
            }
            Need(Addable(array), $"++ on {Unknown(array)}");
            PhiType element = array.Type.Element;
            int size = element.ElementSize;
            bool grows = !array.Type.Filled || array.Type.Open;
            var values = append.Values.Count == 0 ? new List<Expr?> { null } : append.Values.Cast<Expr?>().ToList();
            Mark(append);
            foreach (Expr? value in values)
            {
                // the value first, as the generator works it out
                Opnd v = default;
                VReg? from = null;      // a struct's address, or the object that goes in
                if (element.IsObject)
                {
                    Emit(new ObjectLineI { At = append });
                    if (value != null && Binder.IsObjectCall(value)) from = InReg(Value(value), Addr);
                    else if (value != null)
                    {
                        if (!grows)
                        {
                            // full: stop before anything is copied
                            VReg had = LoadAt(AddressOf(array), array.Type.CountOffset, PhiType.U32);
                            VReg wideHad = fn.NewAddress();
                            Emit(new ExtendI { Dst = wideHad, Src = had, Signed = false });
                            Emit(new CheckI { Index = wideHad, Limit = array.Type.Length, At = append, Held = HeldStores() });
                        }
                        VReg original = ObjectAddress(value);
                        from = fn.NewAddress();
                        Emit(new ObjCallI { Kind = "clone", Struct = element.Struct!, Arg = original, Dst = from });
                    }
                }
                else if (InPlaceStruct(element))
                {
                    if (value != null) from = ObjectAddress(value);
                }
                else
                {
                    v = value == null ? (element.IsVec ? ZeroVec() : IsF(element) ? FloatConst(0.0) : Opnd.Const(0)) : Value(value, element);
                    v = Small(v, Is64(element));
                }
                VReg elements;
                VReg count;
                VReg? before = null;    // where the elements were, before room was made
                if (!grows)
                {
                    VReg place = AddressOf(array);
                    count = LoadAt(place, array.Type.CountOffset, PhiType.U32);
                    VReg wide = fn.NewAddress();
                    Emit(new ExtendI { Dst = wide, Src = count, Signed = false });
                    // full: no room for another (the index error, as the generator's)
                    Emit(new CheckI { Index = wide, Limit = array.Type.Length, At = append, Held = HeldStores() });
                    VReg more = fn.NewValue(false);
                    Emit(new BinI { Op = BinOp.Add, Dst = more, A = count, B = Opnd.Const(1) });
                    Emit(new StoreIdxI { Base = place, Index = Opnd.Const(array.Type.CountOffset), Scale = 1, Src = more, Kind = MemKind.D32 });
                    elements = place;
                    count = wide;
                }
                else
                {
                    // room for one more (its elements moved when there isn't), then at its end
                    Emit(new ObjectLineI { At = append });
                    VReg view = ViewOf(array);
                    if (InPlaceStruct(element)) before = LoadAt(view, 0, PhiType.PointerTo(PhiType.U8));
                    VReg had = LoadAt(view, ViewCount, PhiType.U32);
                    VReg need = fn.NewValue(false);
                    Emit(new BinI { Op = BinOp.Add, Dst = need, A = had, B = Opnd.Const(1) });
                    Emit(new ArrayGrowI { View = view, Count = need, Size = size });
                    VReg now = LoadAt(view, ViewCount, PhiType.U32);
                    VReg more = fn.NewValue(false);
                    Emit(new BinI { Op = BinOp.Add, Dst = more, A = now, B = Opnd.Const(1) });
                    Emit(new StoreIdxI { Base = view, Index = Opnd.Const(ViewCount), Scale = 1, Src = more, Kind = MemKind.D32 });
                    elements = LoadAt(view, 0, PhiType.PointerTo(element));
                    count = fn.NewAddress();
                    Emit(new ExtendI { Dst = count, Src = now, Signed = false });
                }
                if (element.IsObject)
                {
                    // the slot is empty: the copy (or the call's new object), or a zeroed object
                    if (from != null) Emit(new StoreIdxI { Base = elements, Index = count, Scale = size, Src = from, Kind = Ptr });
                    else
                    {
                        VReg slot = Offset(elements, count, size, fn.NewAddress());
                        Emit(new ObjCallI { Kind = "renew", Struct = element.Struct!, Arg = slot });
                    }
                }
                else if (InPlaceStruct(element) && from == null)
                {
                    // none given: every byte of it zero (one dropped earlier may have been here)
                    VReg slot = Offset(elements, count, size, fn.NewAddress());
                    int k = 0;
                    for (; PhiType.AddressSize == 8 && k + 8 <= size; k += 8) Emit(new StoreIdxI { Base = slot, Index = Opnd.Const(k), Scale = 1, Src = Opnd.Const(0), Kind = MemKind.Q64 });
                    for (; k + 4 <= size; k += 4) Emit(new StoreIdxI { Base = slot, Index = Opnd.Const(k), Scale = 1, Src = Opnd.Const(0), Kind = MemKind.D32 });
                    for (; k < size; k++) Emit(new StoreIdxI { Base = slot, Index = Opnd.Const(k), Scale = 1, Src = Opnd.Const(0), Kind = MemKind.U8 });
                }
                else if (InPlaceStruct(element))
                {
                    VReg slot = Offset(elements, count, size, fn.NewAddress());
                    VReg source = from!;
                    if (before != null)
                    {
                        // one that was among the elements is that far into where they are now
                        VReg moved = fn.NewAddress(), apart = fn.NewAddress(), bytesBefore = fn.NewAddress();
                        Emit(new CopyI { Dst = moved, Src = from! });
                        Emit(new BinI { Op = BinOp.Sub, Dst = apart, A = from!, B = before });
                        Emit(new BinI { Op = BinOp.Mul, Dst = bytesBefore, A = count, B = Opnd.Const(size) });
                        Block inside = fn.NewBlock(), on = fn.NewBlock();
                        if (current.End != null) Enter(fn.NewBlock());
                        current.End = new BranchT { Cond = Cond.LtU, A = apart, B = bytesBefore, Then = inside, Else = on };
                        Enter(inside);
                        Emit(new BinI { Op = BinOp.Add, Dst = moved, A = elements, B = apart });
                        current.End = new JumpT { Target = on };
                        Enter(on);
                        source = moved;
                    }
                    VReg bytes = fn.NewAddress();
                    Emit(new ConstI { Dst = bytes, Value = size });
                    Emit(new CopyBytesI { Dst = slot, Src = source, Count = bytes });
                }
                else Emit(new StoreIdxI { Base = elements, Index = count, Scale = size, Src = v, Kind = KindOf(element) });
            }
        }

        /// <summary>Where an array's first element is: in place, or where one that grows keeps them.</summary>
        bool CanElements(Expr array) => !array.Type.Open ? CanAddress(array)
            : Binder.IsFieldView(array) && CanAddress(array) || array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g);

        VReg ElementsOf(Expr array) => !array.Type.Open ? AddressOf(array) : LoadAt(ViewOf(array), 0, PhiType.PointerTo(array.Type.Element));

        /// <summary>
        /// list++ other; of numbers: room made once for what list has and Count more (an atmost
        /// list: an index error unless they fit), then Count of other's elements copied to list's
        /// end, and list has that many more. Other's address is taken after the room is made,
        /// which may move list's elements (list++ list; too). The generator's EmitAppendArray.
        /// </summary>
        void AddArray(AppendArrayStmt add)
        {
            Expr target = add.Target;
            Need((Resizable(target) || Addable(target) && InPlaceStruct(target.Type.Element) && !target.Type.Element.Struct!.OwnsObjects)
                 && target.Type.Element.ElementSize == add.Source.Type.Element.ElementSize, $"{Unknown(target)} added to");
            Need(CanElements(add.Source), $"{Unknown(add.Source)} added");
            Mark(add);
            int size = target.Type.Element.ElementSize;
            VReg count = InReg(Value(add.Count, PhiType.U32), false);
            bool atmost = target.Type.Filled && !target.Type.Open;
            if (atmost)
            {
                VReg place = AddressOf(target);
                VReg had = LoadAt(place, target.Type.CountOffset, PhiType.U32);
                VReg total = fn.NewValue(false), wide = fn.NewAddress();
                Emit(new BinI { Op = BinOp.Add, Dst = total, A = had, B = count });
                Emit(new ExtendI { Dst = wide, Src = total, Signed = false });
                Emit(new CheckI { Index = wide, Limit = target.Type.Length + 1, At = add, Held = HeldStores() });
            }
            else
            {
                Emit(new ObjectLineI { At = add });
                VReg view = ViewOf(target);
                VReg had = LoadAt(view, ViewCount, PhiType.U32);
                VReg need = fn.NewValue(false);
                Emit(new BinI { Op = BinOp.Add, Dst = need, A = had, B = count });
                Emit(new ArrayGrowI { View = view, Count = need, Size = size });
            }
            // after the elements it has: the source's copied there
            VReg has = LoadAt(atmost ? AddressOf(target) : ViewOf(target), atmost ? target.Type.CountOffset : ViewCount, PhiType.U32);
            VReg from = ElementsOf(add.Source);
            VReg to = ElementsOf(target);
            VReg skip = fn.NewAddress(), bytes = fn.NewAddress(), end = fn.NewAddress();
            Emit(new ExtendI { Dst = skip, Src = has, Signed = false });
            if (size != 1) Emit(new BinI { Op = BinOp.Mul, Dst = skip, A = skip, B = Opnd.Const(size) });
            Emit(new BinI { Op = BinOp.Add, Dst = end, A = to, B = skip });
            Emit(new ExtendI { Dst = bytes, Src = count, Signed = false });
            if (size != 1) Emit(new BinI { Op = BinOp.Mul, Dst = bytes, A = bytes, B = Opnd.Const(size) });
            Emit(new CopyBytesI { Dst = end, Src = from, Count = bytes });
            // and it has that many more
            VReg at = atmost ? AddressOf(target) : ViewOf(target);
            int countAt = atmost ? target.Type.CountOffset : ViewCount;
            VReg now = LoadAt(at, countAt, PhiType.U32);
            VReg more = fn.NewValue(false);
            Emit(new BinI { Op = BinOp.Add, Dst = more, A = now, B = count });
            Emit(new StoreIdxI { Base = at, Index = Opnd.Const(countAt), Scale = 1, Src = more, Kind = MemKind.D32 });
        }

        /// <summary>list.len is n; for an array that fills or grows: it keeps its first n (no more than it has: an index error).</summary>
        void SetLength(SetLengthStmt shrink)
        {
            Expr array = shrink.Array;
            if (array.Type.TextTable)
            {
                Need(TextsAddable(array), $"the length of {Unknown(array)} set");
                ShortenTexts(shrink);
                return;
            }
            Need(Addable(array) && !array.Type.TextTable, $"the length of {Unknown(array)} set");
            Mark(shrink);
            bool grows = !array.Type.Filled || array.Type.Open;
            VReg n = InReg(Value(shrink.Value, PhiType.U32), false);
            VReg place = grows ? ViewOf(array) : AddressOf(array);
            int at = grows ? ViewCount : array.Type.CountOffset;
            VReg had = LoadAt(place, at, PhiType.U32);
            // n no more than it has: n below had + 1
            VReg limit = fn.NewAddress(), wideN = fn.NewAddress();
            Emit(new ExtendI { Dst = limit, Src = had, Signed = false });
            Emit(new BinI { Op = BinOp.Add, Dst = limit, A = limit, B = Opnd.Const(1) });
            Emit(new ExtendI { Dst = wideN, Src = n, Signed = false });
            Emit(new CheckI { Index = wideN, LimitValue = limit, At = shrink, Held = HeldStores() });
            Emit(new StoreIdxI { Base = place, Index = Opnd.Const(at), Scale = 1, Src = n, Kind = MemKind.D32 });
            if (array.Type.Element.IsObject) FreeFrom(array, n);
        }

        /// <summary>
        /// list.len is n; for a str array that grows: no more than it has (an index error), the
        /// texts of the ones dropped given back (one in the program's data stays), each slot
        /// empty again ('' and its room marked as in data), then the count (the generator's EmitFreeTexts).
        /// </summary>
        void ShortenTexts(SetLengthStmt shrink)
        {
            Expr array = shrink.Array;
            Mark(shrink);
            VReg n = InReg(Value(shrink.Value, PhiType.U32), false);
            VReg view = ViewOf(array);
            VReg had = LoadAt(view, ViewCount, PhiType.U32);
            VReg limit = fn.NewAddress(), wideN = fn.NewAddress();
            Emit(new ExtendI { Dst = limit, Src = had, Signed = false });
            Emit(new BinI { Op = BinOp.Add, Dst = limit, A = limit, B = Opnd.Const(1) });
            Emit(new ExtendI { Dst = wideN, Src = n, Signed = false });
            Emit(new CheckI { Index = wideN, LimitValue = limit, At = shrink, Held = HeldStores() });
            FreeTextsFrom(array, wideN);
            VReg viewLast = ViewOf(array);
            Emit(new StoreIdxI { Base = viewLast, Index = Opnd.Const(ViewCount), Scale = 1, Src = n, Kind = MemKind.D32 });
        }

        /// <summary>The texts of a str array that grows from element from on given back (one in the program's data stays), each slot empty again.</summary>
        void FreeTextsFrom(Expr array, VReg wideN)
        {
            VReg k = fn.NewAddress();
            Emit(new CopyI { Dst = k, Src = wideN });
            Block test = fn.NewBlock(), body = fn.NewBlock(), free = fn.NewBlock(), next = fn.NewBlock(), after = fn.NewBlock();
            StartBlock(test);
            VReg viewNow = ViewOf(array);
            VReg count = LoadAt(viewNow, ViewCount, PhiType.U32);
            VReg wideCount = fn.NewAddress();
            Emit(new ExtendI { Dst = wideCount, Src = count, Signed = false });
            current.End = new BranchT { Cond = Cond.LtU, A = k, B = wideCount, Then = body, Else = after };
            Enter(body);
            VReg view2 = ViewOf(array);
            VReg texts = LoadAt(view2, 0, PhiType.PointerTo(PhiType.U8));
            VReg most = LoadAt(view2, ViewMax, PhiType.U32);
            VReg wideMost = fn.NewAddress(), roomsAt = fn.NewAddress();
            Emit(new ExtendI { Dst = wideMost, Src = most, Signed = false });
            Emit(new BinI { Op = BinOp.Mul, Dst = roomsAt, A = wideMost, B = Opnd.Const(PhiType.AddressSize) });
            Emit(new BinI { Op = BinOp.Add, Dst = roomsAt, A = roomsAt, B = texts });
            VReg slot = Offset(texts, k, PhiType.AddressSize, fn.NewAddress());
            VReg room = Offset(roomsAt, k, PhiType.AddressSize, fn.NewAddress());
            VReg roomWord = LoadAt(room, 0, PhiType.U32);
            current.End = new BranchT { Cond = Cond.Eq, A = roomWord, B = Opnd.Const(0), TestMask = 0x80000000, Then = free, Else = next };
            Enter(free);
            VReg text = LoadAt(slot, 0, PhiType.PointerTo(PhiType.U8));
            Emit(new FreeTextI { Value = text });
            current.End = new JumpT { Target = next };
            Enter(next);
            // (found again: the free was a call)
            VReg view3 = ViewOf(array);
            VReg texts3 = LoadAt(view3, 0, PhiType.PointerTo(PhiType.U8));
            VReg most3 = LoadAt(view3, ViewMax, PhiType.U32);
            VReg wideMost3 = fn.NewAddress(), roomsAt3 = fn.NewAddress();
            Emit(new ExtendI { Dst = wideMost3, Src = most3, Signed = false });
            Emit(new BinI { Op = BinOp.Mul, Dst = roomsAt3, A = wideMost3, B = Opnd.Const(PhiType.AddressSize) });
            Emit(new BinI { Op = BinOp.Add, Dst = roomsAt3, A = roomsAt3, B = texts3 });
            VReg empty = TextAddress(new StringExpr { Value = "", Type = PhiType.Str });
            Emit(new StoreIdxI { Base = texts3, Index = k, Scale = PhiType.AddressSize, Src = empty, Kind = Ptr });
            VReg inData = fn.NewValue(false);
            Emit(new ConstI { Dst = inData, Value = unchecked((int)0x80000000) });
            Emit(new StoreIdxI { Base = roomsAt3, Index = k, Scale = PhiType.AddressSize, Src = inData, Kind = MemKind.D32 });
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(after);
        }

        /// <summary>
        /// The objects of an array of them from element n on given back, each slot made empty: up to
        /// its room (past how many it has they're empty already), or its size (the generator's EmitFreeElements).
        /// </summary>
        void FreeFrom(Expr array, VReg n)
        {
            StructSymbol s = array.Type.Element.Struct!;
            VReg k = fn.NewAddress();
            Emit(new ExtendI { Dst = k, Src = n, Signed = false });
            Block test = fn.NewBlock(), body = fn.NewBlock(), after = fn.NewBlock();
            StartBlock(test);
            Opnd limit;
            if (array.Type.Open)
            {
                VReg room = LoadAt(ViewOf(array), ViewMax, PhiType.U32);
                VReg wide = fn.NewAddress();
                Emit(new ExtendI { Dst = wide, Src = room, Signed = false });
                limit = wide;
            }
            else limit = Opnd.Const(array.Type.Length);
            current.End = new BranchT { Cond = Cond.LtU, A = k, B = limit, Then = body, Else = after };
            Enter(body);
            VReg elements = array.Type.Open ? LoadAt(ViewOf(array), 0, PhiType.PointerTo(array.Type.Element)) : AddressOf(array);
            VReg slot = Offset(elements, k, PhiType.AddressSize, fn.NewAddress());
            VReg held = LoadAt(slot, 0, array.Type.Element);
            Emit(new StoreIdxI { Base = slot, Index = Opnd.Const(0), Scale = 1, Src = Opnd.Const(0), Kind = Ptr });
            Emit(new ObjCallI { Kind = "free", Struct = s, Arg = held });
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(after);
        }
    }
}

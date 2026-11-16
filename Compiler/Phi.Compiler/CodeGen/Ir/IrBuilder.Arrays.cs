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

        /// <summary>An array that grows kept by name: a class's, or the method's own (of numbers or structs, in its frame).</summary>
        bool GrowingView(VariableSymbol g) => g.GrowingArray && (!Ours(g) && AnyGlobal(g) != null || localViews.ContainsKey(g));

        /// <summary>Where its view is (its elements' address, how many, its room).</summary>
        string ViewHome(VariableSymbol g) => localViews.TryGetValue(g, out string? home) ? home : AnyGlobal(g)!;

        /// <summary>The method's own arrays that grow: each one's view in its frame (empty to start; its elements freed at its end and by a throw going past).</summary>
        readonly Dictionary<VariableSymbol, string> localViews = new();

        static bool LocalView(VariableSymbol v) => v.GrowingArray && v.IsLocal && !v.Type.TextTable && v.Type.Kind != TypeKind.Str
            && (Supported(v.Type.Element) && v.Type.Element.ElementSize is 1 or 2 or 4 or 8 or 16 && !v.Type.Element.IsObject || InPlaceStruct(v.Type.Element));

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
                VReg wide = fn.NewValue(true), high = fn.NewValue(true), both = fn.NewValue(true);
                Emit(new ExtendI { Dst = wide, Src = n, Signed = false });
                Emit(new BinI { Op = BinOp.Shl, Dst = high, A = wide, B = Opnd.Const(32) });
                Emit(new BinI { Op = BinOp.Or, Dst = both, A = wide, B = high });
                return (both, InReg(Value(pv.Pointer), true));
            }
            if (array.Type.Open)
            {
                Need(array.Type.Open && (Binder.IsFieldView(array) && CanAddress(array) || CanAddress(array) && array is NameExpr { Symbol: VariableSymbol p } && paramViews.ContainsKey(p)
                     || array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g)), $"{Unknown(array)} given as an array");
                VReg view = ViewOf(array);
                return (LoadAt(view, ViewCount, PhiType.U64), LoadAt(view, 0, PhiType.PointerTo(array.Type.Element)));
            }
            Need(!array.Type.TextTable || array is NameExpr { Symbol: VariableSymbol { GrowingArray: false } t } && !Ours(t) && SimpleLabel(t.Label),
                 $"{Unknown(array)} given as an array of text");
            if (array.Type.TextTable)
            {
                // a class's str array: its table of where each text is
                var table = (VariableSymbol)((NameExpr)array).Symbol!;
                VReg at = fn.NewValue(true), full = fn.NewValue(true);
                Emit(new AddrI { Dst = at, Label = table.Label });
                Emit(new ConstI { Dst = full, Value = ((long)array.Type.Length << 32) | (uint)array.Type.Length });
                return (full, at);
            }
            Need(CanCount(array) && CanAddress(array), $"{Unknown(array)} given as an array");
            VReg count = InReg(ArrayCount(array), false);
            VReg low = fn.NewValue(true), size = fn.NewValue(true), most = fn.NewValue(true);
            Emit(new ExtendI { Dst = low, Src = count, Signed = false });
            Emit(new ConstI { Dst = size, Value = (long)array.Type.Length << 32 });
            Emit(new BinI { Op = BinOp.Or, Dst = most, A = low, B = size });
            return (most, AddressOf(array));
        }

        /// <summary>Where an array that grows keeps its elements' address, count and room.</summary>
        VReg ViewOf(Expr array)
        {
            if (array is NameExpr { Symbol: VariableSymbol g } && GrowingView(g))
            {
                VReg d = fn.NewValue(true);
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
                VReg at = fn.NewValue(true);
                Emit(new ExtendI { Dst = at, Src = now, Signed = false });
                VReg slot = Offset(elements, at, PhiType.AddressSize, fn.NewValue(true));
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
            array.Type.Element is var e && (InPlaceStruct(e) || e.IsObject && e.Struct != null && !e.Struct.IsInterface)
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
                    if (value != null && Binder.IsObjectCall(value)) from = InReg(Value(value), true);
                    else if (value != null)
                    {
                        if (!grows)
                        {
                            // full: stop before anything is copied
                            VReg had = LoadAt(AddressOf(array), array.Type.CountOffset, PhiType.U32);
                            VReg wideHad = fn.NewValue(true);
                            Emit(new ExtendI { Dst = wideHad, Src = had, Signed = false });
                            Emit(new CheckI { Index = wideHad, Limit = array.Type.Length, At = append, Held = HeldStores() });
                        }
                        VReg original = ObjectAddress(value);
                        from = fn.NewValue(true);
                        Emit(new ObjCallI { Kind = "clone", Struct = element.Struct!, Arg = original, Dst = from });
                    }
                }
                else if (InPlaceStruct(element))
                {
                    Need(value != null, "a struct added with no value");
                    from = ObjectAddress(value!);
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
                    VReg wide = fn.NewValue(true);
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
                    count = fn.NewValue(true);
                    Emit(new ExtendI { Dst = count, Src = now, Signed = false });
                }
                if (element.IsObject)
                {
                    // the slot is empty: the copy (or the call's new object), or a zeroed object
                    if (from != null) Emit(new StoreIdxI { Base = elements, Index = count, Scale = size, Src = from, Kind = MemKind.Q64 });
                    else
                    {
                        VReg slot = Offset(elements, count, size, fn.NewValue(true));
                        Emit(new ObjCallI { Kind = "renew", Struct = element.Struct!, Arg = slot });
                    }
                }
                else if (InPlaceStruct(element))
                {
                    VReg slot = Offset(elements, count, size, fn.NewValue(true));
                    VReg source = from!;
                    if (before != null)
                    {
                        // one that was among the elements is that far into where they are now
                        VReg moved = fn.NewValue(true), apart = fn.NewValue(true), bytesBefore = fn.NewValue(true);
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
                    VReg bytes = fn.NewValue(true);
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
            Need(Resizable(target) && target.Type.Element.ElementSize == add.Source.Type.Element.ElementSize, $"{Unknown(target)} added to");
            Need(CanElements(add.Source), $"{Unknown(add.Source)} added");
            Mark(add);
            int size = target.Type.Element.ElementSize;
            VReg count = InReg(Value(add.Count, PhiType.U32), false);
            bool atmost = target.Type.Filled && !target.Type.Open;
            if (atmost)
            {
                VReg place = AddressOf(target);
                VReg had = LoadAt(place, target.Type.CountOffset, PhiType.U32);
                VReg total = fn.NewValue(false), wide = fn.NewValue(true);
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
            VReg skip = fn.NewValue(true), bytes = fn.NewValue(true), end = fn.NewValue(true);
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
            VReg limit = fn.NewValue(true), wideN = fn.NewValue(true);
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
            VReg limit = fn.NewValue(true), wideN = fn.NewValue(true);
            Emit(new ExtendI { Dst = limit, Src = had, Signed = false });
            Emit(new BinI { Op = BinOp.Add, Dst = limit, A = limit, B = Opnd.Const(1) });
            Emit(new ExtendI { Dst = wideN, Src = n, Signed = false });
            Emit(new CheckI { Index = wideN, LimitValue = limit, At = shrink, Held = HeldStores() });
            VReg k = fn.NewValue(true);
            Emit(new CopyI { Dst = k, Src = wideN });
            Block test = fn.NewBlock(), body = fn.NewBlock(), free = fn.NewBlock(), next = fn.NewBlock(), after = fn.NewBlock();
            StartBlock(test);
            VReg viewNow = ViewOf(array);
            VReg count = LoadAt(viewNow, ViewCount, PhiType.U32);
            VReg wideCount = fn.NewValue(true);
            Emit(new ExtendI { Dst = wideCount, Src = count, Signed = false });
            current.End = new BranchT { Cond = Cond.LtU, A = k, B = wideCount, Then = body, Else = after };
            Enter(body);
            VReg view2 = ViewOf(array);
            VReg texts = LoadAt(view2, 0, PhiType.PointerTo(PhiType.U8));
            VReg most = LoadAt(view2, ViewMax, PhiType.U32);
            VReg wideMost = fn.NewValue(true), roomsAt = fn.NewValue(true);
            Emit(new ExtendI { Dst = wideMost, Src = most, Signed = false });
            Emit(new BinI { Op = BinOp.Mul, Dst = roomsAt, A = wideMost, B = Opnd.Const(PhiType.AddressSize) });
            Emit(new BinI { Op = BinOp.Add, Dst = roomsAt, A = roomsAt, B = texts });
            VReg slot = Offset(texts, k, PhiType.AddressSize, fn.NewValue(true));
            VReg room = Offset(roomsAt, k, PhiType.AddressSize, fn.NewValue(true));
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
            VReg wideMost3 = fn.NewValue(true), roomsAt3 = fn.NewValue(true);
            Emit(new ExtendI { Dst = wideMost3, Src = most3, Signed = false });
            Emit(new BinI { Op = BinOp.Mul, Dst = roomsAt3, A = wideMost3, B = Opnd.Const(PhiType.AddressSize) });
            Emit(new BinI { Op = BinOp.Add, Dst = roomsAt3, A = roomsAt3, B = texts3 });
            VReg empty = TextAddress(new StringExpr { Value = "", Type = PhiType.Str });
            Emit(new StoreIdxI { Base = texts3, Index = k, Scale = PhiType.AddressSize, Src = empty, Kind = MemKind.Q64 });
            VReg inData = fn.NewValue(false);
            Emit(new ConstI { Dst = inData, Value = unchecked((int)0x80000000) });
            Emit(new StoreIdxI { Base = roomsAt3, Index = k, Scale = PhiType.AddressSize, Src = inData, Kind = MemKind.D32 });
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(after);
            VReg viewLast = ViewOf(array);
            Emit(new StoreIdxI { Base = viewLast, Index = Opnd.Const(ViewCount), Scale = 1, Src = n, Kind = MemKind.D32 });
        }

        /// <summary>
        /// The objects of an array of them from element n on given back, each slot made empty: up to
        /// its room (past how many it has they're empty already), or its size (the generator's EmitFreeElements).
        /// </summary>
        void FreeFrom(Expr array, VReg n)
        {
            StructSymbol s = array.Type.Element.Struct!;
            VReg k = fn.NewValue(true);
            Emit(new ExtendI { Dst = k, Src = n, Signed = false });
            Block test = fn.NewBlock(), body = fn.NewBlock(), after = fn.NewBlock();
            StartBlock(test);
            Opnd limit;
            if (array.Type.Open)
            {
                VReg room = LoadAt(ViewOf(array), ViewMax, PhiType.U32);
                VReg wide = fn.NewValue(true);
                Emit(new ExtendI { Dst = wide, Src = room, Signed = false });
                limit = wide;
            }
            else limit = Opnd.Const(array.Type.Length);
            current.End = new BranchT { Cond = Cond.LtU, A = k, B = limit, Then = body, Else = after };
            Enter(body);
            VReg elements = array.Type.Open ? LoadAt(ViewOf(array), 0, PhiType.PointerTo(array.Type.Element)) : AddressOf(array);
            VReg slot = Offset(elements, k, PhiType.AddressSize, fn.NewValue(true));
            VReg held = LoadAt(slot, 0, array.Type.Element);
            Emit(new StoreIdxI { Base = slot, Index = Opnd.Const(0), Scale = 1, Src = Opnd.Const(0), Kind = MemKind.Q64 });
            Emit(new ObjCallI { Kind = "free", Struct = s, Arg = held });
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            current.End = new JumpT { Target = test };
            Enter(after);
        }
    }
}

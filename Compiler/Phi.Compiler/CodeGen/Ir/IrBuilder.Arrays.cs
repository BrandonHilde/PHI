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
                || array is NameExpr { Symbol: VariableSymbol { GrowingArray: true } g } && !Ours(g) && AnyGlobal(g) != null);

        /// <summary>Where an array that grows keeps its elements' address, count and room.</summary>
        VReg ViewOf(Expr array)
        {
            if (array is NameExpr { Symbol: VariableSymbol { GrowingArray: true } g } && !Ours(g))
            {
                VReg d = fn.NewValue(true);
                Emit(new AddrI { Dst = d, Label = AnyGlobal(g)! });
                return d;
            }
            return AddressOf(array);
        }

        /// <summary>list++ x; (or list++; for a zero): at its end, with room made (or full an error).</summary>
        void Add(AppendStmt append)
        {
            Expr array = append.Target;
            Need(Resizable(array), $"++ on {Unknown(array)}");
            PhiType element = array.Type.Element;
            var values = append.Values.Count == 0 ? new List<Expr?> { null } : append.Values.Cast<Expr?>().ToList();
            Mark(append);
            foreach (Expr? value in values)
            {
                // the value first, as the generator works it out
                Opnd v = value == null ? (element.IsVec ? ZeroVec() : IsF(element) ? FloatConst(0.0) : Opnd.Const(0)) : Value(value, element);
                v = Small(v, Is64(element));
                VReg elements;
                VReg count;
                if (array.Type.Filled)
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
                    VReg had = LoadAt(view, ViewCount, PhiType.U32);
                    VReg need = fn.NewValue(false);
                    Emit(new BinI { Op = BinOp.Add, Dst = need, A = had, B = Opnd.Const(1) });
                    Emit(new ArrayGrowI { View = view, Count = need, Size = element.ElementSize });
                    VReg now = LoadAt(view, ViewCount, PhiType.U32);
                    VReg more = fn.NewValue(false);
                    Emit(new BinI { Op = BinOp.Add, Dst = more, A = now, B = Opnd.Const(1) });
                    Emit(new StoreIdxI { Base = view, Index = Opnd.Const(ViewCount), Scale = 1, Src = more, Kind = MemKind.D32 });
                    elements = LoadAt(view, 0, PhiType.PointerTo(element));
                    count = fn.NewValue(true);
                    Emit(new ExtendI { Dst = count, Src = now, Signed = false });
                }
                Emit(new StoreIdxI { Base = elements, Index = count, Scale = element.ElementSize, Src = v, Kind = KindOf(element) });
            }
        }

        /// <summary>list.len is n; for an array that fills or grows: it keeps its first n (no more than it has: an index error).</summary>
        void SetLength(SetLengthStmt shrink)
        {
            Expr array = shrink.Array;
            Need(Resizable(array), $"the length of {Unknown(array)} set");
            Mark(shrink);
            VReg n = InReg(Value(shrink.Value, PhiType.U32), false);
            VReg place = array.Type.Filled ? AddressOf(array) : ViewOf(array);
            int at = array.Type.Filled ? array.Type.CountOffset : ViewCount;
            VReg had = LoadAt(place, at, PhiType.U32);
            // n no more than it has: n below had + 1
            VReg limit = fn.NewValue(true), wideN = fn.NewValue(true);
            Emit(new ExtendI { Dst = limit, Src = had, Signed = false });
            Emit(new BinI { Op = BinOp.Add, Dst = limit, A = limit, B = Opnd.Const(1) });
            Emit(new ExtendI { Dst = wideN, Src = n, Signed = false });
            Emit(new CheckI { Index = wideN, LimitValue = limit, At = shrink, Held = HeldStores() });
            Emit(new StoreIdxI { Base = place, Index = Opnd.Const(at), Scale = 1, Src = n, Kind = MemKind.D32 });
        }
    }
}

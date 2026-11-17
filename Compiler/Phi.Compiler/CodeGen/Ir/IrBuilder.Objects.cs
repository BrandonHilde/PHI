using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // Objects copied, handed over and given back, as the generator does it: with the routines it
    // makes for each struct (set, assign, clone, free).
    public sealed partial class IrBuilder
    {
        /// <summary>Where an object is: what an object variable or field holds, or where a struct kept in place is.</summary>
        VReg ObjectAddress(Expr e)
        {
            if (e.Type.IsObject) return InReg(Value(e), true);
            Need(e.Type.IsStruct && CanAddress(e), $"the object {Unknown(e)}");
            return AddressOf(e);
        }

        /// <summary>Where an owner keeps its object's address: a method's own object's home, or an owning field's place.</summary>
        VReg OwnerPlace(Expr target)
        {
            if (target is NameExpr { Symbol: VariableSymbol v } && owned.TryGetValue(v, out string? home))
            {
                VReg d = fn.NewAddress();
                Emit(new AddrI { Dst = d, Label = home });
                return d;
            }
            Need(CanAddress(target), $"an object kept in {Unknown(target)}");
            return AddressOf(target);
        }

        /// <summary>
        /// The object at source given to target: an owner (a method's own object, a new field)
        /// gets a copy (set), or takes a call's new object over and frees what it had; anything
        /// else is given its values (assign), and a new object is freed after.
        /// </summary>
        void TakeObject(Expr target, StructSymbol s, VReg source, bool move)
        {
            if (move && target is NameExpr { Symbol: VariableSymbol mine } && owned.TryGetValue(mine, out string? home))
            {
                // (a method's own object: through what's known of its home)
                Opnd was = LoadObject((home, null, 0));
                StoreObject((home, null, 0), source);
                if (!(was.IsImm && was.Imm == 0)) Emit(new ObjCallI { Kind = "free", Struct = s, Arg = InReg(was, true) });
                return;
            }
            if (Binder.IsOwnedPlace(target))
            {
                VReg place = OwnerPlace(target);
                if (!move)
                {
                    Emit(new ObjCallI { Kind = "set", Struct = s, Arg = place, Second = source });
                    return;
                }
                VReg had = LoadAt(place, 0, PhiType.ObjectOf(s));
                Emit(new StoreIdxI { Base = place, Index = Opnd.Const(0), Scale = 1, Src = source, Kind = Ptr });
                Emit(new ObjCallI { Kind = "free", Struct = s, Arg = had });
                return;
            }
            VReg into = ObjectAddress(target);
            Emit(new ObjCallI { Kind = "assign", Struct = s, Arg = into, Second = source });
            if (move) Emit(new ObjCallI { Kind = "free", Struct = s, Arg = source });
        }

        /// <summary>
        /// What a method that gives an object gives: its own object handed over (its home emptied,
        /// so its end doesn't free it), or a copy of any other (the caller's to free).
        /// </summary>
        Opnd GiveObject(Expr value)
        {
            if (value is NameExpr { Symbol: VariableSymbol v } && owned.TryGetValue(v, out string? home))
            {
                Opnd mine = LoadObject((home, null, 0));
                StoreObject((home, null, 0), Opnd.Const(0));
                return mine;
            }
            if (value is EmptyExpr || ConstantFolder.Evaluate(value) == 0) return Opnd.Const(0);
            // a call's new object is the caller's as it is
            if (Binder.IsObjectCall(value)) return InReg(Value(value), true);
            VReg from = ObjectAddress(value);
            VReg copy = fn.NewAddress();
            Emit(new ObjectLineI { At = value });
            Emit(new ObjCallI { Kind = "clone", Struct = method.ReturnType.Struct!, Arg = from, Dst = copy });
            return copy;
        }
    }
}

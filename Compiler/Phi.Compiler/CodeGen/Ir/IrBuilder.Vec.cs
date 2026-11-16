using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // vec<T> in the intermediate form (the binder's Binder.Vec.cs says what each use means),
    // and log, which a method that uses a vec needs as much as any.
    public sealed partial class IrBuilder
    {
        /// <summary>e's value when it's a vec, or a comparison of two (a bln); null for anything else.</summary>
        Opnd? VecValue(Expr e)
        {
            switch (e)
            {
                case VecExpr made:
                    return MakeVec(made);
                case BinaryExpr cmp when IsComparison(cmp.Op) && cmp.Left.Type.IsVec:
                {
                    // a is b: every lane the same (as the binder says, only is and is not)
                    VReg a = Vec(cmp.Left), b = Vec(cmp.Right);
                    VReg same = fn.NewValue(false);
                    Emit(new VecReduceI { Op = VReduce.Same, Dst = same, A = a, B = b, Lane = cmp.Left.Type.Lane!.Kind });
                    if (cmp.Op == BinaryOp.Equal) return same;
                    VReg differ = fn.NewValue(false);
                    Emit(new BinI { Op = BinOp.Xor, Dst = differ, A = same, B = Opnd.Const(1) });
                    return differ;
                }
                case BinaryExpr b when b.Type.IsVec:
                {
                    TypeKind lane = b.Type.Lane!.Kind;
                    VReg left = Vec(b.Left);
                    if (b.Op is BinaryOp.ShiftLeft or BinaryOp.ShiftRight)
                    {
                        Opnd count = Small(Value(b.Right, PhiType.Int), false);
                        VReg shifted = fn.NewVec();
                        Emit(new VecI { Op = b.Op == BinaryOp.ShiftLeft ? VOp.Shl : VOp.Shr, Dst = shifted, A = left, Count = count, Lane = lane });
                        return shifted;
                    }
                    VReg right = Vec(b.Right);
                    VOp op = b.Op switch
                    {
                        BinaryOp.Add => VOp.Add,
                        BinaryOp.Subtract => VOp.Sub,
                        BinaryOp.Multiply => VOp.Mul,
                        BinaryOp.Divide => VOp.Div,
                        BinaryOp.BitAnd => VOp.And,
                        BinaryOp.BitOr => VOp.Or,
                        BinaryOp.BitXor => VOp.Xor,
                        _ => throw new NotYet($"the operator {b.Op} on a vec"),
                    };
                    VReg d = fn.NewVec();
                    Emit(new VecI { Op = op, Dst = d, A = left, B = right, Lane = lane });
                    return d;
                }
                case UnaryExpr u when u.Type.IsVec:
                {
                    VReg a = Vec(u.Operand);
                    VReg d = fn.NewVec();
                    Emit(new VecI { Op = u.Op == UnaryOp.Negate ? VOp.Neg : VOp.Not, Dst = d, A = a, Lane = u.Type.Lane!.Kind });
                    return d;
                }
                case IndexExpr { Target.Type.IsVec: true } lane:
                {
                    VReg a = Vec(lane.Target);
                    Opnd index = LaneIndex(lane);
                    VReg d = New(lane.Type);
                    Emit(new VecGetI { Dst = d, A = a, Index = index, Lane = lane.Target.Type.Lane!.Kind });
                    return d;
                }
                case CastExpr cast when cast.Type.IsVec:
                {
                    VReg a = Vec(cast.Operand);
                    PhiType from = cast.Operand.Type.Lane!, to = cast.Type.Lane!;
                    // whole numbers to whole numbers are the same bytes; floats and whole numbers each lane's value
                    if (from.IsFloat == to.IsFloat) return a;
                    VReg d = fn.NewVec();
                    Emit(new VecI { Op = VOp.Convert, Dst = d, A = a, Lane = from.Kind, To = to.Kind });
                    return d;
                }
            }
            return null;
        }

        /// <summary>A vec's value, in a value of its own.</summary>
        VReg Vec(Expr e)
        {
            Need(e.Type.IsVec, $"a {e.Type} where a vec goes");
            return Value(e).Reg!;
        }

        /// <summary>A vec of zeros.</summary>
        VReg ZeroVec()
        {
            fn.NeedScratch();
            VReg d = fn.NewVec();
            Emit(new VecMakeI { Dst = d, Lane = TypeKind.U8 });
            return d;
        }

        /// <summary>
        /// A vec from its lanes: zeros, one value in every lane, or one for each; when every
        /// lane is a number written in, its 16 bytes are made when the program is built.
        /// </summary>
        VReg MakeVec(VecExpr made)
        {
            fn.NeedScratch();
            PhiType lane = made.Type.Lane!;
            int lanes = made.Type.Lanes, size = lane.ElementSize;
            VReg d = fn.NewVec();
            var known = made.Lanes.Select(l => Known(l, lane)).ToList();
            if (made.Lanes.Count > 0 && known.All(k => k != null))
            {
                var bytes = new byte[PhiType.VecBytes];
                for (int k = 0; k < lanes; k++)
                {
                    byte[] one = known[made.Lanes.Count == 1 ? 0 : k]!;
                    Array.Copy(one, 0, bytes, k * size, size);
                }
                Emit(new VecMakeI { Dst = d, Bytes = bytes, Lane = lane.Kind });
                return d;
            }
            var make = new VecMakeI { Dst = d, Lane = lane.Kind };
            foreach (Expr l in made.Lanes) make.Lanes.Add(IsF(lane) ? Value(l, PhiType.F64) : Value(l, lane));
            Emit(make);
            return d;
        }

        /// <summary>A lane's bytes, when it's a number written in (or a constant); null otherwise.</summary>
        static byte[]? Known(Expr e, PhiType lane)
        {
            if (lane.IsFloat)
            {
                double? f = KnownFloat(e);
                if (f == null) return null;
                return lane.Kind == TypeKind.F32 ? BitConverter.GetBytes((float)f.Value) : BitConverter.GetBytes(f.Value);
            }
            if (ConstantFolder.Evaluate(e) is not long n) return null;
            return BitConverter.GetBytes(n).Take(lane.ElementSize).ToArray();
        }

        static double? KnownFloat(Expr e) => e switch
        {
            DecimalExpr d => d.Value,
            NameExpr { Symbol: FloatConstantSymbol k } => k.Value,
            UnaryExpr { Op: UnaryOp.Negate } neg => -KnownFloat(neg.Operand),
            _ when !e.Type.IsFloat && ConstantFolder.Evaluate(e) is long n => n,
            _ => null,
        };

        /// <summary>v:i's index: checked against the lanes (as an array's is), made 64 bits.</summary>
        Opnd LaneIndex(IndexExpr e)
        {
            fn.NeedScratch();
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, it.IsSigned ? PhiType.I64 : PhiType.U64);
            if (e.CheckLimit is int limit && !(index.IsImm && index.Imm >= 0 && index.Imm < limit) && !MaskedBelow(e.Index, limit))
            {
                VReg checkedIndex = InReg(index, true);
                Emit(new CheckI { Index = checkedIndex, Limit = limit, At = e, Held = HeldStores() });
                index = checkedIndex;
            }
            return index;
        }

        /// <summary>v:i is x: the lane given a value (in a method's own vec, or a class's).</summary>
        void SetLane(IndexExpr lane, Expr value)
        {
            Need(lane.Target is NameExpr { Symbol: VariableSymbol }, "a lane of a vec that isn't a variable");
            var v = (VariableSymbol)((NameExpr)lane.Target).Symbol!;
            PhiType lt = lane.Target.Type.Lane!;
            // (the value first, then where it goes, as an element's)
            Opnd x = IsF(lt) ? Value(value, PhiType.F64) : Value(value, lt);
            Opnd index = LaneIndex(lane);
            if (Ours(v))
            {
                VReg own = Variable(v);
                Emit(new VecSetI { Dst = own, A = own, Index = index, Value = x, Lane = lt.Kind });
                return;
            }
            Need(Global(v) != null, $"a lane of '{v.Name}'");
            VReg was = fn.NewVec();
            Emit(new LoadI { Dst = was, Place = Global(v)!, Kind = MemKind.V128 });
            VReg now = fn.NewVec();
            Emit(new VecSetI { Dst = now, A = was, Index = index, Value = x, Lane = lt.Kind });
            Emit(new StoreI { Place = Global(v)!, Src = now, Kind = MemKind.V128 });
        }

        /// <summary>(a.Min: b), (v.Sum), (mask.Pick: x y): a vec's own methods.</summary>
        Opnd VecCall(CallExpr call, VecMethod vm)
        {
            fn.NeedScratch();
            TypeKind lane = vm.Vec.Lane!.Kind;
            VReg a = Vec(call.Arguments[0]);
            switch (vm.Name)
            {
                case "Sum" or "Smallest" or "Largest" or "Any" or "All":
                {
                    VReg d = New(vm.Result);
                    VReduce op = vm.Name switch
                    {
                        "Sum" => VReduce.Sum,
                        "Smallest" => VReduce.Smallest,
                        "Largest" => VReduce.Largest,
                        "Any" => VReduce.Any,
                        _ => VReduce.All,
                    };
                    Emit(new VecReduceI { Op = op, Dst = d, A = a, Lane = lane });
                    return d;
                }
                case "Pick":
                {
                    VReg then = Vec(call.Arguments[1]), otherwise = Vec(call.Arguments[2]);
                    VReg d = fn.NewVec();
                    Emit(new VecI { Op = VOp.Pick, Dst = d, A = a, B = then, C = otherwise, Lane = vm.Result.Lane!.Kind });
                    return d;
                }
            }
            VReg? b = call.Arguments.Count > 1 ? Vec(call.Arguments[1]) : null;
            VOp op2 = vm.Name switch
            {
                "Min" => VOp.Min,
                "Max" => VOp.Max,
                "Abs" => VOp.Abs,
                "Sqrt" => VOp.Sqrt,
                "AddSat" => VOp.AddSat,
                "SubSat" => VOp.SubSat,
                "Average" => VOp.Average,
                "Less" => VOp.Less,
                "Equal" => VOp.Equal,
                "Greater" => VOp.Greater,
                "Widen" => VOp.Widen,
                "WidenHigh" => VOp.WidenHigh,
                "Narrow" => VOp.Narrow,
                _ => throw new NotYet($"a vec's {vm.Name}"),
            };
            VReg r = fn.NewVec();
            Emit(new VecI { Op = op2, Dst = r, A = a, B = b, Lane = lane, To = vm.Result.IsVec ? vm.Result.Lane!.Kind : lane });
            return r;
        }

        // ---------------------------------------------------------------- log

        /// <summary>
        /// log a b c; (and debug): as the generator's EmitLog, a whole log at a time across the
        /// program's threads, each value written out as text, a vec's lanes with a space between.
        /// </summary>
        void Log(LogStmt log)
        {
            bool serial = log.Target == LogTarget.SerialOnly;
            Mark(log);
            Emit(new RoutineI { Label = "phi_log_take" });
            foreach (Expr value in log.Values) Print(value, serial);
            // (a whole log reaches the screen at once, even with other programs running)
            if (!serial) Emit(new RoutineI { Label = "phi_flush" });
            Emit(new RoutineI { Label = "phi_log_give" });
        }

        void Print(Expr value, bool serial)
        {
            PhiType t = Real(value);
            switch (value)
            {
                case StringExpr s when t.IsString:
                    Emit(new PrintI { Kind = PrintKind.Text, Literal = s.Value, Serial = serial });
                    return;
                case NameExpr { Symbol: VariableSymbol { Type.IsString: true, Grows: false, TextRef: false } text } when !text.IsLocal && !text.IsParameter
                                                                                                                         && SimpleLabel(text.Label) && !text.Type.IsArray:
                    Emit(new PrintI { Kind = PrintKind.Text, Label = text.Label, Serial = serial });
                    return;
                case NameExpr { Symbol: FieldPathSymbol inPlace } when inPlace.Type.IsInlineText && frameStructs.ContainsKey(inPlace.Root):
                    // (err.message: text kept in a struct in the frame)
                {
                    VReg where = fn.NewValue(true);
                    Emit(new AddrI { Dst = where, Label = FramePlace(inPlace.Root, inPlace.Offset) });
                    Emit(new PrintI { Kind = PrintKind.Text, Value = where, Serial = serial });
                    return;
                }
            }
            Need(Supported(t) && !t.IsObject, $"a log of {A(t)}");
            if (t.IsVec)
            {
                fn.NeedScratch();
                VReg v = Vec(value);
                PhiType lane = t.Lane!;
                for (int k = 0; k < t.Lanes; k++)
                {
                    if (k > 0) Emit(new PrintI { Kind = PrintKind.Text, Literal = " ", Serial = serial });
                    VReg one = New(lane);
                    Emit(new VecGetI { Dst = one, A = v, Index = Opnd.Const(k), Lane = lane.Kind });
                    PrintNumber(one, lane, serial);
                }
                return;
            }
            Need(!t.IsPointer || t.IsTextPointer, "a log of a pointer that isn't text");
            if (t.IsTextPointer)
            {
                Emit(new PrintI { Kind = PrintKind.Text, Value = InReg(Value(value), true), Serial = serial });
                return;
            }
            PrintNumber(Value(value), t, serial);
        }

        void PrintNumber(Opnd v, PhiType t, bool serial)
        {
            fn.NeedScratch();
            PrintKind kind = t.IsChar ? PrintKind.Char
                : IsF(t) ? PrintKind.Float
                : t.IsWide ? (t.Kind == TypeKind.U64 ? PrintKind.Uint64 : PrintKind.Int64)
                : t.IsUnsigned32 ? PrintKind.Uint : PrintKind.Int;
            Emit(new PrintI { Kind = kind, Value = v, Serial = serial });
        }

        static string A(PhiType t)
        {
            string name = t.ToString();
            return ("aeio".Contains(name[0]) ? "an " : "a ") + name;
        }
    }
}

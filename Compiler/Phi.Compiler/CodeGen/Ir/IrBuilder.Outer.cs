using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // A loop made vector code a lane for each of its steps when its body sums in a loop of its
    // own (IrBuilder.Loops.cs has the simple ones). A matrix's product:
    //
    //     while int j: 0; j < n; j++;
    //         f64 cell: 0.0;
    //         while int k: 0; k < n; k++;
    //             cell++ a:(i * n + k) * b:(k * n + j);
    //         ;;
    //         c:(i * n + j) is cell;
    //     ;;
    //
    // is worked out for j, j + 1 ... at once: each lane's cell its own, added up in the order
    // it's written (so floats give the same answer, as gcc does it), b:(k * n + j) the lanes'
    // neighbouring elements, and a:(i * n + k), which j doesn't change, in every lane. What's
    // written has to be what the method made with new and doesn't change, and what's read
    // anything else: memory made then is nothing that was there before, so nothing else overlaps
    // it (no check is needed when it runs).
    public sealed partial class IrBuilder
    {
        sealed class OuterLoop
        {
            public VariableSymbol Counter = null!;
            public Expr Bound = null!;
            public PhiType Lane = PhiType.Error;
            public VariableSymbol Sum = null!;
            public Expr? Start;
            public WhileStmt Inner = null!;
            public VariableSymbol InnerCounter = null!;
            public BinaryOp Op;
            public Expr Term = null!;
            public IndexExpr Target = null!;
            public Expr Value = null!;
        }

        string? PlanOuter(WhileStmt loop, out OuterLoop? plan)
        {
            plan = null;
            if (loop.Init is not VarDecl { Symbol: VariableSymbol j, Values.Count: <= 1 } || !Ours(j) || j.Type.Kind is not (TypeKind.I32 or TypeKind.U32))
                return "it doesn't count with an int or u32 of its own";
            if (loop.Condition is not BinaryExpr { Op: BinaryOp.Less, Left: NameExpr { Symbol: VariableSymbol c } } test || c != j
                || loop.Step is not AssignStmt step || !CountsUpByOne(step, j))
                return $"it isn't while {j.Name} < n; {j.Name}++";
            if (!Unchanging(test.Right, loop, j) || Real(test.Right).IsWide || Real(test.Right).IsFloat || Real(test.Right).IsPointer
                || Real(test.Right).IsSigned != j.Type.IsSigned && !(ConstantFolder.Evaluate(test.Right) is long k0 && k0 >= 0))
                return $"what {j.Name} goes up to isn't a number that stays the same, of its kind";
            // a sum's start, the loop that adds to it, and where it goes
            if (loop.Body.Count != 3 || loop.Body[0] is not VarDecl { Symbol: VariableSymbol sum, Values.Count: <= 1 } start
                || loop.Body[1] is not WhileStmt inner || loop.Body[2] is not AssignStmt { Op: AssignOp.Set, Values: null, Target: IndexExpr target } set)
                return "its body isn't a sum's start, a loop that adds to it, and an element given it";
            if (!Ours(sum) || !PhiType.CanBeLane(sum.Type) || owned.ContainsKey(sum) || frameStructs.ContainsKey(sum))
                return $"its sum is {A(sum.Type)}";
            var v = new OuterLoop { Counter = j, Bound = test.Right, Lane = sum.Type, Sum = sum, Start = start.Values.FirstOrDefault(), Inner = inner, Target = target, Value = set.Value };
            if (v.Start != null && !Uniform(v.Start, loop, v)) return "its sum starts as something each step works out differently";
            // the inner loop: while int k: from; k < to; k++; sum is sum + term;
            if (inner.Init is not VarDecl { Symbol: VariableSymbol k, Values.Count: <= 1 } innerStart || !Ours(k) || k.Type.Kind is not (TypeKind.I32 or TypeKind.U32))
                return "its inner loop doesn't count with an int or u32 of its own";
            v.InnerCounter = k;
            if (inner.Condition is not BinaryExpr { Op: BinaryOp.Less, Left: NameExpr { Symbol: VariableSymbol ck } } innerTest || ck != k
                || inner.Step is not AssignStmt innerStep || !CountsUpByOne(innerStep, k))
                return $"its inner loop isn't while {k.Name} < n; {k.Name}++";
            if (innerStart.Values.Count == 1 && !Uniform(innerStart.Values[0], loop, v) || !Uniform(innerTest.Right, loop, v) || Mentions(innerTest.Right, k))
                return "its inner loop's range isn't the same for every step";
            if (inner.Body.Count != 1 || inner.Body[0] is not AssignStmt { Op: AssignOp.Set, Values: null, Target: NameExpr { Symbol: VariableSymbol into },
                                                                           Value: BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract, Left: NameExpr { Symbol: VariableSymbol same } } added }
                || into != sum || same != sum)
                return $"its inner loop does more than add to '{sum.Name}'";
            v.Op = added.Op;
            v.Term = added.Right;
            if (Mentions(v.Term, sum)) return $"what's added to '{sum.Name}' reads it";
            // f32s are added in f64s, as PHI works them out: f32 lanes give the same only when nothing else is worked out
            if (sum.Type.Kind == TypeKind.F32) return "its sum is of f32s, which PHI works out in f64s (a lane of f32s rounds where it doesn't)";
            if (OuterLanes(v.Term, loop, v) is string notTerm) return notTerm;
            // the element it's given: at j's own place, of memory the method made
            if (target.Type != sum.Type) return $"it puts {A(sum.Type)} into {A(target.Type)}";
            if (!Affine(target.Index, loop, v)) return $"what it sets isn't at an index of {j.Name} itself (x + {j.Name})";
            if (target.Target is not NameExpr { Symbol: VariableSymbol written } || !Fresh(written))
                return "what it sets isn't memory the method made with new and doesn't change (so nothing it reads could be it)";
            if (SyntaxWalker.Nodes(loop).OfType<IndexExpr>().Any(x => x != target && x.Target is NameExpr { Symbol: VariableSymbol r } && r == written))
                return $"it reads '{written.Name}' as well as setting it";
            if (OuterLanes(v.Value, loop, v) is string notValue) return notValue;
            plan = v;
            return null;
        }

        /// <summary>A pointer the method made with new where it's declared, and never sets, or takes the address of, again.</summary>
        bool Fresh(VariableSymbol p) =>
            p.Type.IsPointer && Ours(p) && !p.IsParameter && p.Decl.Values is [NewExpr]
            && !method.Decl.Body.SelectMany(SyntaxWalker.Nodes).Any(n =>
                n is AssignStmt { Target: NameExpr { Symbol: VariableSymbol set } } && set == p
                || n is AddrExpr { Operand: NameExpr { Symbol: VariableSymbol taken } } && taken == p
                || n is VarDecl { Symbol: VariableSymbol again } d && again == p && d != p.Decl);

        /// <summary>
        /// The same for every lane (what j doesn't change): numbers, the inner loop's counter, a
        /// variable the loop doesn't set, an element at such an index of a pointer it doesn't change,
        /// and what they work out (worked out once a step, as written, and put in every lane).
        /// </summary>
        bool Uniform(Expr e, WhileStmt loop, OuterLoop v) =>
            !Mentions(e, v.Counter) && !Mentions(e, v.Sum) && !e.Type.IsVec
            && SyntaxWalker.Nodes(e).All(n => n switch
            {
                NumberExpr or DecimalExpr or BoolExpr => true,
                NameExpr { Symbol: ConstantSymbol or FloatConstantSymbol or LengthSymbol { Count: not null } } => true,
                NameExpr { Symbol: VariableSymbol x } name => x == v.InnerCounter || Unchanging(name, loop, v.Counter),
                IndexExpr { Target: NameExpr { Symbol: VariableSymbol p } } => p.Type.IsPointer,
                BinaryExpr { Op: not (BinaryOp.And or BinaryOp.Or) } or UnaryExpr { Op: not UnaryOp.Not } or CastExpr => true,
                _ => false,
            });

        /// <summary>x + j (or j, or j + x, or x + j - y), with x and y the same for every lane: the lanes' neighbouring indexes.</summary>
        bool Affine(Expr index, WhileStmt loop, OuterLoop v) => index switch
        {
            NameExpr { Symbol: VariableSymbol c } => c == v.Counter,
            BinaryExpr { Op: BinaryOp.Add } b => Affine(b.Left, loop, v) && Uniform(b.Right, loop, v) || Uniform(b.Left, loop, v) && Affine(b.Right, loop, v),
            BinaryExpr { Op: BinaryOp.Subtract } b => Affine(b.Left, loop, v) && Uniform(b.Right, loop, v),
            _ => false,
        };

        /// <summary>Whether e is lanes: the sum, an element at a lane's own index, what's the same for every lane, and + - * / and the rest as the simple loops have them; why not, or null.</summary>
        string? OuterLanes(Expr e, WhileStmt loop, OuterLoop v)
        {
            if (e is NameExpr { Symbol: VariableSymbol s } && s == v.Sum) return null;
            if (Uniform(e, loop, v)) return null;
            bool isFloat = v.Lane.IsFloat;
            int bits = 8 * v.Lane.ElementSize;
            switch (e)
            {
                case IndexExpr element:
                    if (element.Type != v.Lane) return $"it reads {element.Type}s into {v.Lane}s";
                    if (element.Target is not NameExpr { Symbol: VariableSymbol p } || !p.Type.IsPointer || !Unchanging(element.Target, loop, v.Counter))
                        return "an element isn't of a pointer the loop leaves as it is";
                    if (!(p.IsParameter || !Ours(p) || Fresh(p))) return $"'{p.Name}' may be what's written (a pointer of the method's own it didn't make with new)";
                    return Affine(element.Index, loop, v) ? null : $"an element is at neither {v.Counter.Name}'s own place nor one the same for every lane";
                case CastExpr cast when cast.Type == v.Lane && !isFloat && !cast.Operand.Type.IsFloat:
                    return OuterLanes(cast.Operand, loop, v);
                case UnaryExpr { Op: UnaryOp.Negate } neg:
                    return OuterLanes(neg.Operand, loop, v);
                case UnaryExpr { Op: UnaryOp.BitNot } not when !isFloat:
                    return OuterLanes(not.Operand, loop, v);
                case BinaryExpr b when b.Type.IsFloat == isFloat && !b.Type.IsPointer:
                    switch (b.Op)
                    {
                        case BinaryOp.Add or BinaryOp.Subtract:
                        case BinaryOp.Multiply when isFloat || bits < 64:
                        case BinaryOp.Divide when isFloat:
                        case BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor when !isFloat:
                            return OuterLanes(b.Left, loop, v) ?? OuterLanes(b.Right, loop, v);
                    }
                    return $"it has {b.Op} on {v.Lane}s, which lanes don't do the same";
            }
            return $"it has a {e.GetType().Name.Replace("Expr", "").ToLowerInvariant()} in what it works out";
        }

        void EmitOuterLoop(OuterLoop v, WhileStmt loop)
        {
            fn.NeedScratch();
            VariableSymbol j = v.Counter;
            PhiType lane = v.Lane;
            int size = lane.ElementSize, step = PhiType.VecBytes / size;
            Block scalar = fn.NewBlock();
            VReg counter = Variable(j);
            VReg Wide(VReg x, bool signed)
            {
                VReg w = fn.NewValue(true);
                Emit(new ExtendI { Dst = w, Src = x, Signed = signed });
                return w;
            }
            VReg end = Wide(InReg(Value(v.Bound, j.Type), false), j.Type.IsSigned);
            VReg last = fn.NewValue(true);
            Emit(new BinI { Op = BinOp.Sub, Dst = last, A = end, B = Opnd.Const(step) });

            // while j + step <= end
            Block head = fn.NewBlock(), body = fn.NewBlock();
            StartBlock(head);
            VReg at = Wide(counter, j.Type.IsSigned);
            current.End = new BranchT { Cond = Cond.LeS, A = at, B = last, Then = body, Else = scalar };
            Enter(body);

            // the sum's lanes, from its start
            VReg sum = v.Start != null ? Every(v.Start) : ZeroVec();
            // the inner loop, as written but with the sum in lanes
            VReg k = Variable(v.InnerCounter);
            if (v.Inner.Init!.Values.Count == 1) SetVariable(k, Value(v.Inner.Init.Values[0], v.InnerCounter.Type), v.InnerCounter.Type);
            else Emit(new ConstI { Dst = k, Value = 0 });
            Block innerBody = fn.NewBlock(), innerStep = fn.NewBlock(), innerAfter = fn.NewBlock();
            Condition(v.Inner.Condition, innerBody, innerAfter);
            Enter(innerBody);
            VReg term = Lanes(v.Term);
            VReg added = fn.NewVec();
            Emit(new VecI { Op = v.Op == BinaryOp.Add ? VOp.Add : VOp.Sub, Dst = added, A = sum, B = term, Lane = lane.Kind });
            Emit(new CopyI { Dst = sum, Src = added });
            StartBlock(innerStep);
            Emit(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            Condition(v.Inner.Condition, innerBody, innerAfter);
            Enter(innerAfter);

            // the element each lane's sum goes to
            VReg result = Lanes(v.Value);
            var (bas, index) = LaneZero(v.Target);
            Emit(new StoreIdxI { Base = bas, Index = index, Scale = size, Src = result, Kind = MemKind.V128 });
            Emit(new BinI { Op = BinOp.Add, Dst = counter, A = counter, B = Opnd.Const(step) });
            current.End = new JumpT { Target = head };
            Enter(scalar);

            // what's the same for every lane, in every lane
            VReg Every(Expr e)
            {
                var made = new VecExpr { Type = PhiType.VecOf(lane), Span = e.Span };
                made.Lanes.Add(e);
                return MakeVec(made);
            }

            // an element's base, and the index lane 0's is at (the lanes' are the ones after it)
            (VReg Base, VReg Index) LaneZero(IndexExpr element)
            {
                VReg b = InReg(Value(element.Target), true);
                PhiType it = Real(element.Index);
                VReg index64 = InReg(Convert(Value(element.Index), it, it.IsSigned ? PhiType.I64 : PhiType.U64), true);
                return (b, index64);
            }

            VReg Lanes(Expr e)
            {
                if (e is NameExpr { Symbol: VariableSymbol s } && s == v.Sum) return sum;
                if (Uniform(e, loop, v)) return Every(e);
                switch (e)
                {
                    case IndexExpr element:
                    {
                        var (b, index) = LaneZero(element);
                        VReg loaded = fn.NewVec();
                        Emit(new LoadIdxI { Dst = loaded, Base = b, Index = index, Scale = size, Kind = MemKind.V128 });
                        return loaded;
                    }
                    case CastExpr cast:
                        return Lanes(cast.Operand);
                    case UnaryExpr u:
                        return One(u.Op == UnaryOp.Negate ? VOp.Neg : VOp.Not, Lanes(u.Operand), null);
                    case BinaryExpr b:
                    {
                        VReg left = Lanes(b.Left), right = Lanes(b.Right);
                        VOp op = b.Op switch
                        {
                            BinaryOp.Add => VOp.Add,
                            BinaryOp.Subtract => VOp.Sub,
                            BinaryOp.Multiply => VOp.Mul,
                            BinaryOp.Divide => VOp.Div,
                            BinaryOp.BitAnd => VOp.And,
                            BinaryOp.BitOr => VOp.Or,
                            _ => VOp.Xor,
                        };
                        return One(op, left, right);
                    }
                }
                throw new NotYet($"a {e.GetType().Name} in a loop made vector code");
            }

            VReg One(VOp op, VReg a, VReg? b)
            {
                VReg d = fn.NewVec();
                Emit(new VecI { Op = op, Dst = d, A = a, B = b, Lane = lane.Kind });
                return d;
            }
        }
    }
}

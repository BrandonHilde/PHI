using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // Simple loops made vector code by the optimiser itself (Plan.md: "vec<T>: the design", the
    // user's idea: the optimiser for the loops it can see through, vec<T> for the rest). A loop
    //
    //     while int i: start; i < n; i++;
    //         p:i is q:i * 3 + r:i;
    //     ;;
    //
    // whose body only sets elements at i from elements at i, numbers that don't change in it and
    // arithmetic that gives the same on lanes as on whole numbers, is done a vec at a time first,
    // then the loop as written goes on from where that stopped (the elements left over, or all of
    // them when the vector part can't be sure). Nothing it writes may be anything it reads unless
    // they're the very same elements, and a class's arrays are checked first, so the loop as
    // written runs into their ends with the error it always gave. PHI_IR_TRACE says which loops
    // it took, and why not the others.
    public sealed partial class IrBuilder
    {
        sealed class VectorLoop
        {
            public VariableSymbol Counter = null!;
            public Expr Bound = null!;
            public PhiType Lane = PhiType.Error;
            public List<(IndexExpr Target, Expr Value)> Stores = new();

            /// <summary>Each array or pointer the loop reaches, by its variable: written (true) or only read.</summary>
            public Dictionary<VariableSymbol, bool> Bases = new();
        }

        /// <summary>
        /// The loop done a vec at a time when it's one of the simple ones, before the loop as
        /// written, whose counter it leaves where it got to (its Init has run).
        /// </summary>
        void Vectorize(WhileStmt loop)
        {
            string? why = PlanVector(loop, out VectorLoop? plan);
            if (plan == null)
            {
                fn.Notes.Add((loop, $"isn't vector code: {why}"));
                return;
            }
            fn.Notes.Add((loop, $"is vector code too, {PhiType.VecBytes / plan.Lane.ElementSize} {plan.Lane}s at a time"));
            EmitVectorLoop(plan, loop);
        }

        // ---------------------------------------------------------------- is it one?

        string? PlanVector(WhileStmt loop, out VectorLoop? plan)
        {
            plan = null;
            if (loop.Init is not VarDecl { Symbol: VariableSymbol i, Values.Count: <= 1 } || !Ours(i) || i.Type.Kind is not (TypeKind.I32 or TypeKind.U32))
                return "it doesn't count with an int or u32 of its own (while int i: 0; ...)";
            if (loop.Condition is not BinaryExpr { Op: BinaryOp.Less, Left: NameExpr { Symbol: VariableSymbol c } } test || c != i)
                return $"it doesn't go while {i.Name} < something";
            if (loop.Step is not AssignStmt step || !CountsUpByOne(step, i))
                return $"its step isn't {i.Name}++";
            var v = new VectorLoop { Counter = i, Bound = test.Right };
            if (!Unchanging(test.Right, loop, i) || Real(test.Right).IsWide || Real(test.Right).IsFloat || Real(test.Right).IsPointer)
                return $"what {i.Name} goes up to isn't a number that stays the same (a constant, or a variable of the method's own the loop doesn't set)";
            bool counterSigned = i.Type.IsSigned;
            if (Real(test.Right).IsSigned != counterSigned && !(ConstantFolder.Evaluate(test.Right) is long k && k >= 0))
                return "it compares a signed number with an unsigned one";
            if (loop.Body.Count == 0) return "it does nothing";
            foreach (Stmt s in loop.Body)
            {
                if (s is not AssignStmt { Op: AssignOp.Set, Values: null, Target: IndexExpr target } set)
                    return $"its body does more than set elements at {i.Name} (a {StatementKind(s)})";
                if (Element(target, loop, i, v, written: true) is string notElement) return notElement;
                PhiType t = target.Type;
                if (v.Lane.IsError) v.Lane = t;
                else if (v.Lane != t) return $"it works on {v.Lane}s and {t}s together";
                if (Lanes(set.Value, loop, v) is string notLanes) return notLanes;
                if (t.Kind == TypeKind.F32 && F32Exactly(set.Value, loop, i) is string notExact) return notExact;
                v.Stores.Add((target, set.Value));
            }
            if (!PhiType.CanBeLane(v.Lane)) return $"its elements are {v.Lane}s";
            plan = v;
            return null;
        }

        static string StatementKind(Stmt s) => s switch
        {
            AssignStmt => "different assignment",
            IfStmt => "if",
            WhileStmt => "loop",
            CallStmt => "call",
            LogStmt => "log",
            _ => s.GetType().Name.Replace("Stmt", "").ToLowerInvariant(),
        };

        static bool CountsUpByOne(AssignStmt step, VariableSymbol i) => step switch
        {
            { Op: AssignOp.Set, Target: NameExpr { Symbol: VariableSymbol t }, Value: BinaryExpr { Op: BinaryOp.Add, Left: NameExpr { Symbol: VariableSymbol l }, Right: var one } }
                => t == i && l == i && ConstantFolder.Evaluate(one) == 1,
            { Op: AssignOp.Add, Target: NameExpr { Symbol: VariableSymbol t }, Value: var by } => t == i && ConstantFolder.Evaluate(by) == 1,
            _ => false,
        };

        /// <summary>A variable the loop never sets (its own, or a parameter): what it has before, it has all through.</summary>
        bool Unchanging(Expr e, WhileStmt loop, VariableSymbol counter)
        {
            if (ConstantFolder.Evaluate(e) != null) return true;
            if (e is NameExpr { Symbol: ConstantSymbol or FloatConstantSymbol } or DecimalExpr) return true;
            if (e is not NameExpr { Symbol: VariableSymbol v } || v == counter || !Ours(v) || owned.ContainsKey(v) || frameStructs.ContainsKey(v)) return false;
            return !SyntaxWalker.Nodes(loop).Any(n => n is AssignStmt { Target: NameExpr { Symbol: VariableSymbol set } } && set == v
                                                      || n is VarDecl { Symbol: VariableSymbol declared } && declared == v
                                                      || n is AddrExpr { Operand: NameExpr { Symbol: VariableSymbol taken } } && taken == v);
        }

        /// <summary>p:i or list:i: an element at the counter, of a pointer the loop doesn't change or a class's array; why not, or null.</summary>
        string? Element(IndexExpr e, WhileStmt loop, VariableSymbol i, VectorLoop v, bool written)
        {
            if (e.Index is not NameExpr { Symbol: VariableSymbol at } || at != i)
                return $"an element is reached at something other than {i.Name} itself";
            if (e.Target is not NameExpr { Symbol: VariableSymbol b })
                return "an element isn't of a variable";
            bool pointer = b.Type.IsPointer && Unchanging(e.Target, loop, i);
            if (!pointer && GlobalArray(b) == null)
                return $"'{b.Name}' isn't a pointer the loop leaves as it is, or a class's array";
            v.Bases[b] = v.Bases.GetValueOrDefault(b) || written;
            return null;
        }

        /// <summary>
        /// Whether value works out lane by lane to what it is a number at a time (stored as the
        /// lane's type): why not, or null. A part that doesn't change in the loop is worked out
        /// once, as it is; the rest are elements, + - * &amp; | ^ ~ and -, &lt;&lt; by a number,
        /// &gt;&gt; of an element by a number (after + its bits above the lane's would come in), /
        /// of floats, and `as` the lane's own type.
        /// </summary>
        string? Lanes(Expr e, WhileStmt loop, VectorLoop v)
        {
            if (Invariant(e, loop, v.Counter)) return null;
            PhiType lane = v.Lane;
            bool isFloat = lane.IsFloat;
            int bits = 8 * lane.ElementSize;
            switch (e)
            {
                case IndexExpr element:
                    if (element.Type != lane) return $"it reads {element.Type}s into {lane}s";
                    return Element(element, loop, v.Counter, v, written: false);
                case CastExpr cast when cast.Type == lane && !isFloat && !cast.Operand.Type.IsFloat:
                    return Lanes(cast.Operand, loop, v);
                case UnaryExpr { Op: UnaryOp.Negate } neg:
                    return Lanes(neg.Operand, loop, v);
                case UnaryExpr { Op: UnaryOp.BitNot } not when !isFloat:
                    return Lanes(not.Operand, loop, v);
                case BinaryExpr b when (b.Type.IsFloat == isFloat) && !b.Type.IsPointer:
                {
                    switch (b.Op)
                    {
                        case BinaryOp.Add or BinaryOp.Subtract:
                            break;
                        case BinaryOp.Multiply when isFloat || bits < 64:
                            break;
                        case BinaryOp.Divide when isFloat:
                            break;
                        case BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor when !isFloat:
                            break;
                        case BinaryOp.ShiftLeft or BinaryOp.ShiftRight when !isFloat:
                        {
                            long most = bits == 64 ? 64 : 32;
                            if (ConstantFolder.Evaluate(b.Right) is not long by || by < 0 || by >= most)
                                return "a shift isn't by a number written in";
                            if (b.Op == BinaryOp.ShiftRight && b.Left is not IndexExpr)
                                return ">> is of something worked out (its bits above a lane's would come in), not of an element";
                            return Lanes(b.Left, loop, v);
                        }
                        default:
                            return $"it has {b.Op} on {lane}s, which lanes don't do the same";
                    }
                    return Lanes(b.Left, loop, v) ?? Lanes(b.Right, loop, v);
                }
            }
            return $"it has {(e is NameExpr { Symbol: VariableSymbol n } && n == v.Counter ? $"{n.Name} as a number" : $"a {e.GetType().Name.Replace("Expr", "").ToLowerInvariant()}")} in what it works out";
        }

        /// <summary>
        /// f32s are worked out in f64s, as PHI does them, and rounded where they're kept: f32
        /// lanes give the same for one operation at a time (an f64 has room for its exact
        /// answer's rounding) of f32s, but not for more, so a loop of more is left as written
        /// (in f64s, two lanes at a time, it's no faster). Why not, or null.
        /// </summary>
        string? F32Exactly(Expr value, WhileStmt loop, VariableSymbol i)
        {
            int operations = 0;
            string? why = null;
            void Count(Expr e)
            {
                if (Invariant(e, loop, i))
                {
                    // (worked out once, in f64s: it has to be an f32 already)
                    bool exact = e.Type.Kind == TypeKind.F32 && e is NameExpr
                                 || KnownFloat(e) is double d && (double)(float)d == d;
                    if (!exact) why = "a number it works with isn't an f32 (it would be one in f64s, as PHI works them out)";
                    return;
                }
                if (e is BinaryExpr) operations++;      // (- is exact, in any width)
                foreach (Node n in Children(e)) if (n is Expr child) Count(child);
            }
            Count(value);
            if (why != null) return why;
            return operations > 1 ? "its f32s are worked out in f64s, as PHI does them, which f32 lanes do the same only one operation at a time" : null;
        }

        /// <summary>A part of a value the loop doesn't change: worked out once, before it.</summary>
        bool Invariant(Expr e, WhileStmt loop, VariableSymbol counter) =>
            !e.Type.IsPointer && !e.Type.IsVec && (e.Type.IsNumeric || e.Type.IsFloat || e.Type.IsWide)
            && SyntaxWalker.Nodes(e).All(n => n switch
            {
                NumberExpr or DecimalExpr or BoolExpr => true,
                NameExpr { Symbol: ConstantSymbol or FloatConstantSymbol or LengthSymbol { Count: not null } } => true,
                NameExpr name => Unchanging(name, loop, counter),
                BinaryExpr { Op: not (BinaryOp.Divide or BinaryOp.Modulo or BinaryOp.And or BinaryOp.Or) } => true,
                UnaryExpr { Op: not UnaryOp.Not } or CastExpr => true,
                _ => false,
            });

        // ---------------------------------------------------------------- the code

        void EmitVectorLoop(VectorLoop v, WhileStmt loop)
        {
            fn.NeedScratch();
            VariableSymbol i = v.Counter;
            PhiType lane = v.Lane;
            int size = lane.ElementSize, step = PhiType.VecBytes / size;
            Block scalar = fn.NewBlock();

            // what it goes up to, and where it starts, as 64-bit numbers
            VReg counter = Variable(i);
            VReg Wide(VReg x, bool signed)
            {
                VReg w = fn.NewValue(true);
                Emit(new ExtendI { Dst = w, Src = x, Signed = signed });
                return w;
            }
            VReg end = Wide(InReg(Value(v.Bound, i.Type), false), i.Type.IsSigned);
            VReg start = Wide(counter, i.Type.IsSigned);

            void Unless(Cond cond, VReg a, Opnd b)
            {
                // on to the next check when a cond b holds; to the loop as written when it doesn't
                Block next = fn.NewBlock();
                if (current.End != null) Enter(fn.NewBlock());
                current.End = new BranchT { Cond = cond, A = a, B = Small(b, true), Then = next, Else = scalar };
                Enter(next);
            }

            // nothing to do a vec at a time
            VReg firstEnd = fn.NewValue(true);
            Emit(new BinI { Op = BinOp.Add, Dst = firstEnd, A = start, B = Opnd.Const(step) });
            Unless(Cond.LeS, firstEnd, end);

            // the bases: a pointer's value, a class array's address (that array's ends checked)
            var bases = new Dictionary<VariableSymbol, VReg>();
            foreach (var (b, _) in v.Bases)
            {
                if (b.Type.IsPointer)
                {
                    bases[b] = InReg(Value(new NameExpr { Name = b.Name, Symbol = b, Type = b.Type }), true);
                    continue;
                }
                VReg at = fn.NewValue(true);
                Emit(new AddrI { Dst = at, Label = GlobalArray(b)! });
                bases[b] = at;
                Unless(Cond.GeS, start, Opnd.Const(0));
                Unless(Cond.LeS, end, Opnd.Const(b.Type.Length));
            }

            // what's written is nothing else that's reached, unless it's the very same elements
            VReg bytes = fn.NewValue(true);
            Emit(new BinI { Op = BinOp.Sub, Dst = bytes, A = end, B = start });
            Emit(new BinI { Op = BinOp.Mul, Dst = bytes, A = bytes, B = Opnd.Const(size) });
            var written = v.Bases.Where(b => b.Value).Select(b => b.Key).ToList();
            foreach (VariableSymbol w in written)
                foreach (VariableSymbol other in v.Bases.Keys.Where(o => o != w))
                {
                    if (!w.Type.IsPointer && !other.Type.IsPointer) continue;   // (two class arrays never overlap)
                    if (written.Contains(other) && written.IndexOf(other) < written.IndexOf(w)) continue;   // (each pair once)
                    Block apart = fn.NewBlock(), check = fn.NewBlock(), second = fn.NewBlock();
                    VReg a = bases[w], o = bases[other];
                    if (current.End != null) Enter(fn.NewBlock());
                    current.End = new BranchT { Cond = Cond.Eq, A = a, B = o, Then = apart, Else = check };
                    Enter(check);
                    VReg aEnd = fn.NewValue(true), oEnd = fn.NewValue(true);
                    Emit(new BinI { Op = BinOp.Add, Dst = aEnd, A = a, B = bytes });
                    current.End = new BranchT { Cond = Cond.LeU, A = aEnd, B = o, Then = apart, Else = second };
                    Enter(second);
                    Emit(new BinI { Op = BinOp.Add, Dst = oEnd, A = o, B = bytes });
                    current.End = new BranchT { Cond = Cond.LeU, A = oEnd, B = a, Then = apart, Else = scalar };
                    Enter(apart);
                }

            // what doesn't change, once: each part in every lane
            var splats = new Dictionary<Expr, VReg>();
            PhiType work = lane;
            foreach (var (_, value) in v.Stores) Hoist(value);
            void Hoist(Expr e)
            {
                if (Invariant(e, loop, i))
                {
                    var made = new VecExpr { Type = PhiType.VecOf(work), Span = e.Span };
                    made.Lanes.Add(e);
                    splats[e] = MakeVec(made);
                    return;
                }
                foreach (Node n in Children(e)) if (n is Expr child) Hoist(child);
            }

            // the loop: while i + step <= end
            VReg last = fn.NewValue(true);
            Emit(new BinI { Op = BinOp.Sub, Dst = last, A = end, B = Opnd.Const(step) });
            Block head = fn.NewBlock(), body = fn.NewBlock();
            StartBlock(head);
            VReg at64 = Wide(counter, i.Type.IsSigned);
            current.End = new BranchT { Cond = Cond.LeS, A = at64, B = last, Then = body, Else = scalar };
            Enter(body);
            foreach (var (target, value) in v.Stores)
            {
                VReg result = Lanes(value);
                Emit(new StoreIdxI { Base = bases[(VariableSymbol)((NameExpr)target.Target).Symbol!], Index = at64, Scale = size, Src = result, Kind = MemKind.V128 });
            }
            Emit(new BinI { Op = BinOp.Add, Dst = counter, A = counter, B = Opnd.Const(step) });
            current.End = new JumpT { Target = head };
            Enter(scalar);

            // a value's lanes
            VReg Lanes(Expr e)
            {
                if (splats.TryGetValue(e, out VReg? every)) return every;
                switch (e)
                {
                    case IndexExpr element:
                    {
                        VReg loaded = fn.NewVec();
                        Emit(new LoadIdxI { Dst = loaded, Base = bases[(VariableSymbol)((NameExpr)element.Target).Symbol!], Index = at64, Scale = size, Kind = MemKind.V128 });
                        return loaded;
                    }
                    case CastExpr cast:
                        return Lanes(cast.Operand);
                    case UnaryExpr u:
                        return One(u.Op == UnaryOp.Negate ? VOp.Neg : VOp.Not, Lanes(u.Operand), null, default);
                    case BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRight } shift:
                    {
                        long by = ConstantFolder.Evaluate(shift.Right)!.Value;
                        return One(shift.Op == BinaryOp.ShiftLeft ? VOp.Shl : VOp.Shr, Lanes(shift.Left), null, Opnd.Const(by));
                    }
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
                        return One(op, left, right, default);
                    }
                }
                throw new NotYet($"a {e.GetType().Name} in a loop made vector code");
            }

            VReg One(VOp op, VReg a, VReg? b, Opnd count)
            {
                VReg d = fn.NewVec();
                Emit(new VecI { Op = op, Dst = d, A = a, B = b, Count = count, Lane = work.Kind });
                return d;
            }
        }

        static IEnumerable<Node> Children(Expr e) => e switch
        {
            BinaryExpr b => new Node[] { b.Left, b.Right },
            UnaryExpr u => new Node[] { u.Operand },
            CastExpr c => new Node[] { c.Operand },
            _ => Array.Empty<Node>(),
        };
    }
}

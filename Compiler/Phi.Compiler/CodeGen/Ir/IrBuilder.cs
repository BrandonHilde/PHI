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
    public sealed class IrBuilder
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

        IrBuilder(MethodSymbol m)
        {
            method = m;
            fn = new IrFunction { Name = m.Name };
            current = fn.NewBlock();
            layout.Add(current);
            Need(m.ReturnType.Kind == TypeKind.Void || Supported(m.ReturnType), $"it gives {m.ReturnType}");
        }

        /// <summary>The method as the intermediate form, or null and why it isn't.</summary>
        public static IrFunction? Build(MethodSymbol m, out string why)
        {
            why = "";
            try
            {
                var builder = new IrBuilder(m);
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
                or TypeKind.F64;

        /// <summary>An f64, which this form keeps in an xmm register (f32 and f16 are the generator's).</summary>
        static bool IsF(PhiType t) => !t.IsArray && t.Kind == TypeKind.F64;

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
            Need(m.Parameters.All(p => Supported(p.Type) && !p.Grows && !p.TextRef && p.Ownership is Ownership.None or Ownership.Borrowed),
                 "a parameter it doesn't take");
            Need(!m.ReturnType.IsObject, "it gives an object");
            Need(m.Locals.All(v => Supported(v.Type) && !v.Type.IsObject && v.Ownership == Ownership.None && !v.Grows && !v.GrowingArray && !v.GrowingElements),
                 "a variable it doesn't take");

            // the parameters, from their places in the frame: the last at rbp + 16, each 8 bytes
            int count = m.Parameters.Count;
            for (int i = 0; i < count; i++)
            {
                VariableSymbol p = m.Parameters[i];
                VReg v = Variable(p);
                Emit(new LoadI { Dst = v, Place = $"rbp + {16 + 8 * (count - 1 - i)}", Kind = KindOf(p.Type) });
            }
            // (a variable is set where it's declared; one that could be read before that is
            // given 0 at the start, by IrPeephole, which knows which)
            foreach (VariableSymbol v in m.Locals) Variable(v);

            Statements(m.Decl.Body);
            if (m.Decl.Result is Expr value)
            {
                Mark(value);
                Return(value);
            }
            else
            {
                Need(m.ReturnType.Kind == TypeKind.Void, "a method that can end without its result");
                if (current.End == null) current.End = new RetT();
            }
            // laid out in the order they were written, which keeps a loop's body together
            var order = layout.Concat(fn.Blocks.Where(b => !layout.Contains(b))).ToList();
            fn.Blocks.Clear();
            fn.Blocks.AddRange(order);
            RemoveUnreachable();
        }

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
        VReg New(PhiType t, string name = "") => IsF(t) ? fn.NewFloat(name) : fn.NewValue(Is64(t), name);

        VReg Variable(VariableSymbol v)
        {
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
            current = b;
            if (!layout.Contains(b)) layout.Add(b);
        }

        // ---------------------------------------------------------------- statements

        void Statements(List<Stmt> body)
        {
            foreach (Stmt s in body) Statement(s);
        }

        void Statement(Stmt s)
        {
            switch (s)
            {
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
                        case NameExpr { Symbol: VariableSymbol g } when Global(g) is string place:
                            Emit(new StoreI { Place = place, Src = Value(a.Value, g.Type), Kind = KindOf(g.Type) });
                            return;
                        case Expr field when Field(field) is var (fb, fo, ft) && fb != null:
                        {
                            // (the value first, then where it goes)
                            Opnd value = Value(a.Value, ft);
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
                    return;
                }

                case WhenStmt when:
                {
                    PhiType st = Real(when.Subject);
                    Need(Supported(st) && !IsF(st) && !st.IsPointer && !st.IsObject && when.Branches.All(br => br.TypeName == null), "a when it doesn't take");
                    Mark(when);
                    VReg subject = InReg(Value(when.Subject), Is64(st));
                    Block after = fn.NewBlock();
                    Block otherwise = when.Else != null ? fn.NewBlock() : after;
                    var sw = new SwitchT { Value = subject, Default = otherwise };
                    var bodies = new List<(Block Block, List<Stmt> Body)>();
                    foreach (WhenBranch branch in when.Branches)
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
                    if (when.Else != null)
                    {
                        Enter(otherwise);
                        Statements(when.Else);
                        if (current.End == null) current.End = new JumpT { Target = after };
                    }
                    Enter(after);
                    return;
                }

                case BreakStmt:
                    Need(loops.Count > 0, "a break outside a loop");
                    Mark(s);
                    if (current.End == null) current.End = new JumpT { Target = loops[^1].Break };
                    return;

                case ContinueStmt:
                    Need(loops.Count > 0, "a continue outside a loop");
                    Mark(s);
                    if (current.End == null) current.End = new JumpT { Target = loops[^1].Continue };
                    return;

                case ReturnStmt ret:
                    Mark(ret);
                    if (ret.Value != null) Return(ret.Value);
                    else if (current.End == null) current.End = new RetT();
                    return;

                case FreeStmt { Owner: null, Releaser: MethodSymbol releaser } free:
                    Mark(free);
                    Call(releaser, new List<Expr> { free.Pointer }, wantResult: false);
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
                    Need(call.ResultTarget is NameExpr { Symbol: VariableSymbol }, "a call's result kept somewhere it doesn't take");
                    var target = (VariableSymbol)((NameExpr)call.ResultTarget).Symbol!;
                    VReg got = Call(callee, call.Arguments, wantResult: true)!;
                    Opnd converted = Convert(got, callee.ReturnType, target.Type);
                    if (Global(target) is string place && !variables.ContainsKey(target) && !method.Locals.Contains(target) && !method.Parameters.Contains(target))
                        Emit(new StoreI { Place = place, Src = converted, Kind = KindOf(target.Type) });
                    else Emit(new CopyI { Dst = Variable(target), Src = converted });
                    return;
                }
            }
            throw new NotYet($"a {s.GetType().Name}");
        }

        void Assign(VariableSymbol v, Expr? value)
        {
            VReg dst = Variable(v);
            Opnd src = value == null ? Opnd.Const(0) : Value(value, v.Type);
            if (src.IsImm) Emit(new ConstI { Dst = dst, Value = src.Imm });
            else Emit(new CopyI { Dst = dst, Src = src });
        }

        /// <summary>Each return gives its value back where it is (no value carried to one exit, which would live across every call).</summary>
        void Return(Expr value)
        {
            Need(method.ReturnType.Kind != TypeKind.Void, "a return with a value from a method that gives none");
            Opnd v = Value(value, method.ReturnType);
            if (current.End != null) Enter(fn.NewBlock());
            current.End = new RetT { Value = v, Wide = method.ReturnType.IsWide, Pointer = method.ReturnType.IsPointer, Float = IsF(method.ReturnType) };
        }

        /// <summary>
        /// A field this form reads and writes where it is: its base (an address, in a value),
        /// its offset from there and its type. self's field by name; p.field through a pointer
        /// or a borrowed object; and a path through objects (a.next.value), each address on the
        /// way read from the one before. (null, 0, Error) for anything else.
        /// </summary>
        (VReg? Base, long Offset, PhiType Type) Field(Expr e)
        {
            switch (e)
            {
                case NameExpr { Symbol: SelfFieldSymbol f } when Ours(f.Self) && Supported(f.Type) && !f.Type.IsObject:
                    return (Variable(f.Self), f.Offset, f.Type);
                case MemberExpr { Field: FieldSymbol f, Call: null } m when Supported(f.Type) && !f.Type.IsObject && Is64(Real(m.Target)) && Supported(Real(m.Target)):
                    return (InReg(Value(m.Target), true), f.Offset, f.Type);
                case NameExpr { Symbol: PathSymbol p } when p.BaseOffset == 0 && Ours(p.Root) && Is64(p.Root.Type) && p.Hops.Count > 0
                                                             && Supported(p.Type) && !p.Type.IsObject:
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
            Need(scale is 1 or 2 or 4 or 8, $"an element of {scale} bytes");
            PhiType it = Real(e.Index);
            Need(Supported(it) && !it.IsPointer && !IsF(it), $"an index that's a {it}");
            Opnd index = Convert(Value(e.Index), it, it.IsSigned ? PhiType.I64 : PhiType.U64);
            if (e.CheckLimit is int limit && !(index.IsImm && index.Imm >= 0 && index.Imm < limit) && !MaskedBelow(e.Index, limit))
            {
                VReg checkedIndex = InReg(index, true);
                Emit(new CheckI { Index = checkedIndex, Limit = limit, At = e });
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
            !v.IsLocal && !v.IsParameter && Supported(v.Type) && v.Label.Length > 0 && !v.Label.Contains("bp")
            && v.Label.All(c => char.IsLetterOrDigit(c) || c == '_') ? v.Label : null;

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
                // both made f64s, the right first; the condition is the signed one's, read as IEEE
                Need(!l.IsPointer && !r.IsPointer, "a pointer compared with a float");
                VReg fb = (VReg)Convert(Value(cmp.Right), r, PhiType.F64).Reg!;
                VReg fa = (VReg)Convert(Value(cmp.Left), l, PhiType.F64).Reg!;
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
            // (the right side is worked out first, as the generator does: see Arithmetic)
            Opnd b = Small(Convert(Value(cmp.Right, r), r, at), wide);
            VReg a = InReg(Convert(Value(cmp.Left, l), l, at), wide);
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
            switch (e)
            {
                case NumberExpr n:
                    return Opnd.Const(n.Value);
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
                case DecimalExpr dec when IsF(e.Type):
                    return FloatConst(dec.Value);
                case NameExpr { Symbol: FloatConstantSymbol fk } when IsF(e.Type):
                    return FloatConst(fk.Value);
                case NameExpr { Symbol: VariableSymbol v } when variables.ContainsKey(v) || method.Locals.Contains(v) || method.Parameters.Contains(v):
                    return Variable(v);
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
                    VReg a = InReg(Convert(Value(u.Operand), Real(u.Operand), Arith(u.Type)), u.Type.IsWide);
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
                // (the right side first, as everywhere)
                VReg fb = (VReg)Convert(Value(b.Right), Real(b.Right), PhiType.F64).Reg!;
                VReg fa = (VReg)Convert(Value(b.Left), Real(b.Left), PhiType.F64).Reg!;
                VReg fd = fn.NewFloat();
                Emit(new FBinI { Op = fop, Dst = fd, A = fa, B = fb });
                return fd;
            }
            if (b.Op == BinaryOp.Subtract && Real(b.Left).IsPointer && Real(b.Right).IsPointer)
            {
                // p - q: how far apart they are, a number (the binder's type), from all 64 bits of each
                VReg pd = fn.NewValue(true);
                Opnd q = Value(b.Right);
                Emit(new BinI { Op = BinOp.Sub, Dst = pd, A = InReg(Value(b.Left), true), B = q });
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
            // the right side is worked out first, then the left, as the generator does (it keeps
            // the right on the stack while it works out the left): which a program sees when
            // both call methods that change what the other reads. (A shift's count is a number
            // of places, at whatever width it's written.)
            Opnd right = op is BinOp.Shl or BinOp.Shr or BinOp.Sar
                ? Convert(Value(b.Right), Real(b.Right), b.Right.Type.IsWide ? b.Right.Type : PhiType.I32)
                : Convert(Value(b.Right), Real(b.Right), t);
            Opnd left = Convert(Value(b.Left), Real(b.Left), t);
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

        /// <summary>p + n or p - n: the pointer moved by n bytes (n with its own sign), all 64 bits.</summary>
        Opnd MovePointer(BinaryExpr b)
        {
            bool leftIsPointer = Real(b.Left).IsPointer;
            Expr pointer = leftIsPointer ? b.Left : b.Right, number = leftIsPointer ? b.Right : b.Left;
            Need(Supported(Real(number)), $"a pointer moved by a {Real(number)}");
            Need(leftIsPointer || b.Op == BinaryOp.Add, "a number minus a pointer");
            // (the right side first, as everywhere)
            Opnd n = default;
            VReg p = null!;
            if (leftIsPointer)
            {
                n = Small(Convert(Value(number), Real(number), Real(number).IsSigned ? PhiType.I64 : PhiType.U64), true);
                p = InReg(Value(pointer), true);
            }
            else
            {
                p = InReg(Value(pointer), true);
                n = Small(Convert(Value(number), Real(number), Real(number).IsSigned ? PhiType.I64 : PhiType.U64), true);
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
            Need(scale is 1 or 2 or 4 or 8, $"an element of {scale} bytes");
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
            // (a struct's method has the struct it's called on among its arguments already: self)
            Need(call.Resolved is MethodSymbol m && (call.CalleeTarget == null || m.Decl.Owner.ForStruct != null), "a call of a built-in or a method value");
            var callee = (MethodSymbol)call.Resolved!;
            return Call(callee, call.Arguments, wantResult: true)!;
        }

        VReg? Call(MethodSymbol callee, List<Expr> arguments, bool wantResult)
        {
            Need(!callee.IsHook && !callee.IsInterruptHandler, "a call of an event");
            Need(callee.Parameters.All(p => Supported(p.Type) && !p.Grows && !p.TextRef), $"a call of {callee.Name}, which takes what this form doesn't");
            Need(callee.ReturnType.Kind == TypeKind.Void || Supported(callee.ReturnType) && !callee.ReturnType.IsObject, $"a call of {callee.Name}, which gives a {callee.ReturnType}");
            Need(arguments.Count == callee.Parameters.Count, $"a call of {callee.Name} that leaves out some of its arguments");
            var call = new CallI
            {
                Label = callee.Label,
                Dst = wantResult && callee.ReturnType.Kind != TypeKind.Void ? New(callee.ReturnType) : null,
                Result = callee.ReturnType.Kind == TypeKind.Void ? CallResult.None : callee.ReturnType.IsWide ? CallResult.EdxEax
                    : IsF(callee.ReturnType) ? CallResult.St0 : Is64(callee.ReturnType) ? CallResult.Rax : CallResult.Eax,
            };
            for (int i = 0; i < arguments.Count; i++)
                call.Args.Add(Value(arguments[i], callee.Parameters[i].Type));
            Need(!wantResult || call.Dst != null, $"{callee.Name} gives nothing to use");
            Emit(call);
            return call.Dst;
        }
    }
}

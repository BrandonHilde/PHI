using System.Text;
using Phi.Compiler.CodeGen.Ir;
using Phi.Compiler.Semantics;

namespace Phi.Compiler.CodeGen
{
    // The intermediate form's x64 code (stage 1 of PerformanceStrategy.md): a method the form
    // can take (Ir/IrBuilder.cs) is written from it here, its values in the registers
    // Ir/RegisterAllocator.cs gave them, in place of the generator's own code for it. It keeps
    // the generator's contract: arguments pushed first to last, 8 bytes each, read from rbp +
    // 16 up; the frame on rbp; the result in eax, or in edx:eax for 64 bits (as the generator
    // gives one); r12 to r15 as the caller left them. So a method of either kind calls the
    // other. Its lines are marked as the generator's are, for errors and phi profile.
    //
    // It's for 64-bit programs (Linux and Windows), where it's what builds every method it
    // takes; PHI_IR=0 leaves them all to the generator, to compare the two or to find which
    // one a difference is in. 32-bit code, kernels and debug builds are the generator's.
    public sealed partial class X86Generator
    {
        /// <summary>Methods the intermediate form takes are compiled through it (64-bit programs), unless PHI_IR=0.</summary>
        static readonly bool IrWanted = Environment.GetEnvironmentVariable("PHI_IR") is not ("0" or "off");

        static readonly HashSet<string> IrSkip = (Environment.GetEnvironmentVariable("PHI_IR_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        /// <summary>PHI_IR_TRACE=1 says which methods went through it and why the others didn't; 2 shows the form too.</summary>
        static readonly int IrTrace = int.TryParse(Environment.GetEnvironmentVariable("PHI_IR_TRACE"), out int trace) ? trace : 0;

        /// <summary>The method written from the intermediate form, if it takes it; false leaves it to the generator.</summary>
        bool TryEmitIr(MethodSymbol m)
        {
            // a method that uses a vec is always the intermediate form's: the generator has no vectors
            bool vectors = UsesVec(m);
            // (32-bit programs through it too, while it learns them: PHI_IR32=1, and only what Fits32 says it writes)
            bool x32 = !bits64 && !vectors && Ir32Wanted && IrWanted && unit.Kind == UnitKind.Program && debugInfo == null;
            if (!vectors && !x32 && (!IrWanted || !bits64 || unit.Kind != UnitKind.Program || debugInfo != null)) return false;
            // (PHI_IR_SKIP=A.B,C.D leaves those methods to the generator: for finding which one a difference is in)
            if (!vectors && IrSkip.Contains(m.Name)) return false;
            // (a class's variable a loop may keep in a register: the generator's own rule, see EmitLoopHolding)
            HashSet<VariableSymbol> reached = ReachedByAddress();
            bool Holdable(VariableSymbol v) => !v.IsLocal && !v.IsParameter && !v.IsOutside && v.Unit == unit.Kind && !v.Decl.Volatile && !reached.Contains(v);
            IrFunction? fn = IrBuilder.Build(m, Holdable, Catches && !m.IsInterruptHandler, GrowResults, unit.Kind == UnitKind.Program, out string why);
            if (fn == null)
            {
                if (IrTrace > 0) Console.Error.WriteLine($"ir: not {m.Name}: {why}");
                if (!vectors) return false;
                // (the generator has no vectors: the build stops here, with why)
                diagnostics.Error(m.Decl.File, m.Decl.Span,
                    $"{m.Decl.Name} uses a vec, and what writes vector code doesn't take {why} yet; put that part in a method of its own and call it");
                Emit("ud2");
                return true;
            }
            IrPeephole.Run(fn);
            if (x32 && Fits32(fn) is string not32)
            {
                if (IrTrace > 0) Console.Error.WriteLine($"ir: not {m.Name}: in 32-bit code, {not32}");
                return false;
            }
            RegisterAllocator regs = RegisterAllocator.Allocate(fn, x32);
            if (IrTrace > 0) Console.Error.WriteLine($"ir: {m.Name} ({fn.Values.Count} values, {regs.SlotCount} in the frame)");
            // (as the generator's frames: no more than the stack it runs on takes)
            int frameBytes = 8 * (regs.SlotCount + regs.UsedCalleeSaved.Count);
            if (frameBytes > MaxFrame)
                diagnostics.Error(m.Decl.File, m.Decl.Span, $"{m.Decl.Name} needs {frameBytes} bytes of stack for its variables (at most {MaxFrame} in {FrameOwner}); declare large buffers in the class, or make them with new");
            if (IrTrace > 0)
                foreach (var (at, note) in fn.Notes) Console.Error.WriteLine($"ir: {m.Name}: the loop on line {LineOf(at)} {note}");
            if (IrTrace > 1) Console.Error.WriteLine(fn.Dump());
            // (what was done with each loop, in the listing too)
            foreach (var (at, note) in fn.Notes) Comment($"the loop on line {LineOf(at)} {note}");
            new IrEmitter(this, fn, regs, m.Label, x32).Emit();
            return true;
        }

        /// <summary>PHI_IR32=1: 32-bit programs' methods through the intermediate form, where it writes them (see Fits32).</summary>
        static readonly bool Ir32Wanted = Environment.GetEnvironmentVariable("PHI_IR32") == "1";

        /// <summary>
        /// What the emitter writes in 32-bit code so far, or null when it writes all of a method: values
        /// of 32 bits (no i64, f64 or vec), numbers worked out, read and written, calls of methods,
        /// index checks, and the control flow; why not, for PHI_IR_TRACE.
        /// </summary>
        static string? Fits32(IrFunction fn)
        {
            if (fn.Values.Any(v => v.Vec)) return "a vec";
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    bool fits = i switch
                    {
                        LineMark or ConstI or CopyI or BinI or UnI or NarrowI or ExtendI or SetCcI or AddrI or LeaI or CheckI or ObjectLineI => true,
                        TextI or ZeroI or SignCheckI or AddrOfI or RawCallI or ObjCallI or ArrayGrowI or FixedStoreI or RoutineI => true,
                        CopyBytesI or CompareBytesI or HasTextI or GrowStoreI or StopI or ExitI or ThrowI or InterfaceMethodI => true,
                        ResultTextI or FreeTextI or ArrayRoutineI => true,
                        CatchI or UncatchI or CaughtI or UnwindI or LockI or UnlockI => true,
                        SysCallI => true,
                        FConstI or FBinI or FNegI or RoundF32I or X87I => true,
                        ToFloatI tf => tf.Signed,
                        PrintI => true,
                        NumberTextI => true,
                        AtomicI at => at.Bytes <= 4,
                        LoadI l => l.Kind is not MemKind.V128,
                        StoreI st => st.Kind is not MemKind.V128,
                        LoadIdxI li => li.Kind is not MemKind.V128 && li.Scale <= 8,
                        StoreIdxI si => si.Kind is not MemKind.V128 && si.Scale <= 8,
                        CallI c => !c.Routine && c.Result is CallResult.None or CallResult.Eax or CallResult.Rax or CallResult.St0 or CallResult.EdxEax
                                   && c.ArgKinds.All(k => k is not MemKind.V128),
                        ToWholeI => true,
                        _ => false,
                    };
                    if (!fits) return $"a {i.GetType().Name}";
                    // (an i64 is two halves there: only what Wide32 and the calls work on with them)
                    bool wide = RegisterAllocator.Uses(i).Any(v => v.Int64) || RegisterAllocator.Def(i) is { Int64: true };
                    if (wide && i is not (ConstI or CopyI or LoadI or StoreI or LoadIdxI or StoreIdxI or BinI or UnI or ExtendI or NarrowI
                                          or SetCcI or ToFloatI { Signed: true } or ToWholeI or PrintI or NumberTextI or CallI or SysCallI or LineMark))
                        return $"an i64 in a {i.GetType().Name}";
                }
                bool ends = b.End switch
                {
                    JumpT => true,
                    BranchT => true,
                    SwitchT or TryT => true,
                    RetT r => !r.Vec,
                    _ => false,
                };
                if (!ends) return $"a {b.End?.GetType().Name}";
            }
            return null;
        }

        /// <summary>
        /// A method that takes or gives a vec, has one, works on one, or calls a method that
        /// takes one (whose arguments only the intermediate form passes).
        /// </summary>
        static bool UsesVec(MethodSymbol m)
        {
            if (HasVec(m.ReturnType) || m.Parameters.Any(p => HasVec(p.Type)) || m.Locals.Any(v => HasVec(v.Type))) return true;
            IEnumerable<Syntax.Node> nodes = m.Decl.Body.SelectMany(Syntax.SyntaxWalker.Nodes);
            if (m.Decl.Result != null) nodes = nodes.Concat(Syntax.SyntaxWalker.Nodes(m.Decl.Result));
            foreach (Syntax.Node n in nodes)
            {
                if (n is Syntax.Expr e && HasVec(e.Type)) return true;
                object? callee = n switch { Syntax.CallExpr c => c.Resolved, Syntax.CallStmt c => c.Resolved, _ => null };
                if (callee is VecMethod || callee is MethodSymbol called && (called.Parameters.Any(p => HasVec(p.Type)) || HasVec(called.ReturnType))) return true;
            }
            return false;
        }

        /// <summary>A class's own statement that uses a vec (a declaration only when it gives one a value).</summary>
        static bool StatementUsesVec(Syntax.Stmt s)
        {
            if (s is Syntax.VarDecl d) return d.Values.Count > 0 && (d.Symbol is VariableSymbol v && HasVec(v.Type) || d.Values.SelectMany(Syntax.SyntaxWalker.Nodes).Any(VecNode));
            return Syntax.SyntaxWalker.Nodes(s).Any(VecNode);
        }

        static bool VecNode(Syntax.Node n)
        {
            if (n is Syntax.Expr e && HasVec(e.Type)) return true;
            if (n is Syntax.VarDecl { Values.Count: > 0, Symbol: VariableSymbol v } && HasVec(v.Type)) return true;
            object? callee = n switch { Syntax.CallExpr c => c.Resolved, Syntax.CallStmt c => c.Resolved, _ => null };
            return callee is VecMethod || callee is MethodSymbol called && (called.Parameters.Any(p => HasVec(p.Type)) || HasVec(called.ReturnType));
        }

        /// <summary>The class whose statements are being written (for a statement made a method of its own).</summary>
        Syntax.ClassDecl? currentClass;

        /// <summary>A class's statements that use a vec, each written as a method of its own after the methods.</summary>
        readonly List<(string Label, Syntax.Stmt Statement, Syntax.ClassDecl Class)> vecStatements = new();

        void EmitVecStatements()
        {
            foreach (var (label, statement, cls) in vecStatements)
            {
                file = cls.File;
                var decl = new Syntax.MethodDecl { Name = $"line {LineOf(statement)}", Owner = cls, Span = statement.Span };
                decl.Body.Add(statement);
                var m = new MethodSymbol { Name = $"{cls.Name} (its statement on line {LineOf(statement)})", Label = label, Decl = decl, Unit = unit.Kind };
                Line("");
                Comment($"{cls.Name}'s statement on line {LineOf(statement)} ({FileName(file)}), which uses a vec");
                Label(label);
                if (!TryEmitIr(m)) Emit("ud2");
            }
            vecStatements.Clear();
        }

        /// <summary>A vec, an array of them, or a pointer to one.</summary>
        static bool HasVec(PhiType t) => t.Kind == TypeKind.Vec || t.Kind == TypeKind.Pointer && t.Pointee != null && HasVec(t.Pointee)
            || t.Kind == TypeKind.Method && t.Signature is MethodSignature s && (s.Parameters.Any(HasVec) || HasVec(s.Result));

        /// <summary>Writes one method's form as x64 assembly into the generator's code.</summary>
        sealed partial class IrEmitter
        {
            readonly X86Generator g;
            readonly IrFunction fn;
            readonly RegisterAllocator regs;
            readonly Dictionary<Block, string> labels = new();
            readonly int frame;

            /// <summary>The method's label: a throw going past calls this with _unwind after it, to free its own objects.</summary>
            readonly string methodLabel;

            /// <summary>32-bit code: what's written with 64-bit names is made 32-bit as it's written (see E).</summary>
            readonly bool x32;

            public IrEmitter(X86Generator generator, IrFunction function, RegisterAllocator allocation, string label, bool x32 = false)
            {
                this.x32 = x32;
                g = generator;
                fn = function;
                regs = allocation;
                methodLabel = label;
                foreach (Block b in fn.Blocks) labels[b] = g.NewLabel("ir");
                // the slots of values with no register, then where r12 to r15 are kept
                frame = 8 * (regs.SlotCount + regs.UsedCalleeSaved.Count);
                if (frame % 16 != 0) frame += 8;
            }

            /// <summary>
            /// A line's mark (its label, for errors and phi profile) waits for the line's first
            /// instruction: a line whose code was taken out (a value nothing read) leaves no mark
            /// at the same place as the next line's, which the lookup would take for the next.
            /// </summary>
            Syntax.Node? pendingLine;

            void E(string instruction)
            {
                if (pendingLine != null)
                {
                    g.SourceComment(pendingLine);
                    pendingLine = null;
                }
                if (x32) instruction = To32(instruction);
                g.code.Append("    ").AppendLine(instruction);
            }

            static readonly System.Text.RegularExpressions.Regex Names64 = new(@"\b(rax|rbx|rcx|rdx|rsi|rdi|rbp|rsp|r11d|r11|qword)\b");
            static readonly System.Text.RegularExpressions.Regex Not32 = new(@"\b(r(8|9|1[0-5])[dwb]?|sil|dil|cqo|cdqe|movsxd|movq|xmm(8|9|1[0-5]))\b|\[rel ");
            static readonly System.Text.RegularExpressions.Regex FloatLine = new(@"xmm|^f[a-z]+\b");
            static readonly System.Text.RegularExpressions.Regex HighXmm = new(@"\bxmm1([2-5])\b");

            /// <summary>
            /// An instruction written for 64-bit code, made 32-bit: each 64-bit register its 32-bit one
            /// (r11, the emitter's own, edx), a qword a dword, [rel x] [x]. Only for the instructions
            /// Fits32 lets through, whose values are all 32 bits: anything left that 32-bit code hasn't
            /// is the emitter's mistake, and stops the build.
            /// </summary>
            static string To32(string instruction)
            {
                // (an f64's qword is its 8 bytes in 32-bit code too: kept where the line works on floats)
                bool floats = FloatLine.IsMatch(instruction);
                string made = Names64.Replace(instruction.Replace("[rel ", "["), m => m.Value switch
                {
                    "rax" => "eax", "rbx" => "ebx", "rcx" => "ecx", "rdx" => "edx", "rsi" => "esi", "rdi" => "edi",
                    "rbp" => "ebp", "rsp" => "esp", "r11" or "r11d" => "edx", _ => floats ? "qword" : "dword",
                });
                made = HighXmm.Replace(made, m => $"xmm{int.Parse(m.Groups[1].Value) + 2}");     // (xmm12 to xmm15: xmm4 to xmm7)
                if (Not32.IsMatch(made)) throw new InvalidOperationException($"32-bit code with what only 64-bit code has: {made}");
                return made;
            }

            int SavedAt(string register) => 8 * (regs.SlotCount + 1 + regs.UsedCalleeSaved.IndexOf(register));

            public void Emit()
            {
                E("push rbp");
                E("mov rbp, rsp");
                if (frame > Page)
                {
                    // a frame of more than a page goes down a page at a time, touching each (the
                    // generator's EmitFrameSetup): the page under the stack is always the one that's
                    // reached, however big the frame, so going past it is the stack running out
                    string probe = g.NewLabel("frame");
                    g.Label(probe);
                    E($"sub rsp, {Page}");
                    E("mov [rsp], rbp");
                    E("sub [rsp], rsp");
                    E($"cmp qword [rsp], {frame - Page}");
                    E($"jbe {probe}");
                    E($"lea rsp, [rbp - {frame}]");
                }
                else if (frame > 0) E($"sub rsp, {frame}");
                foreach (string r in regs.UsedCalleeSaved) E($"mov [rbp - {SavedAt(r)}], {r}");
                for (int k = 0; k < fn.Blocks.Count; k++)
                {
                    Block b = fn.Blocks[k];
                    Block? next = k + 1 < fn.Blocks.Count ? fn.Blocks[k + 1] : null;
                    g.Label(labels[b]);
                    foreach (Instr i in b.Instrs)
                    {
                        // (32-bit code's mistakes name the method and the instruction)
                        try { Instruction(i); }
                        catch (InvalidOperationException e) when (x32) { throw new InvalidOperationException($"{methodLabel}: {IrFunction.Text(i)}: {e.Message}", e); }
                    }
                    End(b.End!, next);
                }
                foreach (var (table, targets) in tables)
                {
                    g.Line("align 8");
                    g.Label(table);
                    foreach (string target in targets) g.Line($"    {(x32 ? "dd" : "dq")} {target}");
                }
                if (fn.UnwindRecord != 0)
                {
                    // what a throw on its way past calls with rbp at this method's frame: its
                    // objects freed, as the end of the method frees them
                    g.Line("");
                    g.Label($"{methodLabel}_unwind");
                    foreach (var (home, s) in fn.Owned)
                    {
                        E($"mov rax, qword [{home}]");
                        E($"call {g.ObjectRoutine("free", s)}");
                    }
                    foreach (string slot in fn.OwnedTexts)
                    {
                        E($"push qword [{slot}]");
                        E($"call {g.HeapMethodLabel("Heap.Free")}");
                        E($"add rsp, {Word}");
                    }
                    foreach (var (slot, room) in fn.OwnedTableTexts)
                    {
                        // an element that moved to the heap (one still in its own buffer stays)
                        string own = g.NewLabel("unwindown");
                        E($"test dword [{room}], {0x80000000}");
                        E($"jnz {own}");
                        E($"push qword [{slot}]");
                        E($"call {g.HeapMethodLabel("Heap.Free")}");
                        E($"add rsp, {Word}");
                        g.Label(own);
                    }
                    foreach (var (view, objects) in fn.OwnedViews)
                    {
                        // each element's object (up to its room) or heap text (up to how many), then the room
                        string next = g.NewLabel("unwindview"), done = g.NewLabel("unwoundview"), kept = g.NewLabel("unwindkept");
                        E("push qword 0");
                        g.Label(next);
                        E("mov eax, [rsp]");
                        E(objects != null ? $"cmp eax, [{view} + {g.ViewMax}]" : $"cmp eax, [{view} + {g.ViewCount}]");
                        E($"jae {done}");
                        E($"mov rcx, [{view}]");
                        if (objects != null)
                        {
                            E($"lea rdx, [rcx + rax * {Word}]");
                            E("mov rax, [rdx]");
                            E("mov qword [rdx], 0");
                            E($"call {g.ObjectRoutine("free", objects)}");
                        }
                        else
                        {
                            E($"mov edx, [{view} + {g.ViewMax}]");
                            E($"lea rdx, [rcx + rdx * {Word}]");
                            E($"test dword [rdx + rax * {Word}], {0x80000000}");
                            E($"jnz {kept}");
                            E($"push qword [rcx + rax * {Word}]");
                            E($"call {g.HeapMethodLabel("Heap.Free")}");
                            E($"add rsp, {Word}");
                            g.Label(kept);
                        }
                        E("inc dword [rsp]");
                        E($"jmp {next}");
                        g.Label(done);
                        E($"add rsp, {Word}");
                        E($"push qword [{view}]");
                        E($"call {g.HeapMethodLabel("Heap.Free")}");
                        E($"add rsp, {Word}");
                    }
                    E("ret");
                }
            }

            /// <summary>A line's mark that's waiting, written now: before code the generator writes itself.</summary>
            void MarkNow()
            {
                if (pendingLine == null) return;
                g.SourceComment(pendingLine);
                pendingLine = null;
            }

            // ---------------------------------------------------------------- where values are

            static readonly Dictionary<string, (string D, string W, string B)> Parts = new()
            {
                ["rax"] = ("eax", "ax", "al"), ["rbx"] = ("ebx", "bx", "bl"), ["rcx"] = ("ecx", "cx", "cl"),
                ["rdx"] = ("edx", "dx", "dl"), ["rsi"] = ("esi", "si", "sil"), ["rdi"] = ("edi", "di", "dil"),
                ["r8"] = ("r8d", "r8w", "r8b"), ["r9"] = ("r9d", "r9w", "r9b"), ["r10"] = ("r10d", "r10w", "r10b"),
                ["r11"] = ("r11d", "r11w", "r11b"), ["r12"] = ("r12d", "r12w", "r12b"), ["r13"] = ("r13d", "r13w", "r13b"),
                ["r14"] = ("r14d", "r14w", "r14b"), ["r15"] = ("r15d", "r15w", "r15b"),
            };

            /// <summary>A 64-bit register's name at a width: 8, 16, 32 or 64 bits.</summary>
            static string Sub(string r64, int bits) => bits switch
            {
                64 => r64,
                32 => Parts[r64].D,
                16 => Parts[r64].W,
                _ => Parts[r64].B,
            };

            static string Size(int bits) => bits switch { 64 => "qword", 32 => "dword", 16 => "word", _ => "byte" };

            static int Bits(VReg v) => v.Wide ? 64 : 32;

            /// <summary>The generator's EmitBlockField (this thread's block's field), with its instruction made 32-bit in 32-bit code.</summary>
            void BlockField(string instruction, string field) => g.EmitBlockField(x32 ? To32(instruction) : instruction, field);

            /// <summary>An address's bytes (what a push of one takes): 8, or 4 in 32-bit code.</summary>
            int Word => x32 ? 4 : 8;

            /// <summary>Where a str result's place is, above rbp: past the return address and the caller's rbp.</summary>
            int ResultAt => 2 * Word;

            /// <summary>In 32-bit code esi and edi have no low byte (sil and dil are 64-bit code's).</summary>
            static bool NoLowByte(string r64) => r64 is "rsi" or "rdi";

            string? Reg(VReg v) => regs.Registers.TryGetValue(v, out string? r) ? r : null;

            /// <summary>Where v is, as an operand at a width: its register, or its slot in the frame.</summary>
            string At(VReg v, int bits)
            {
                // (an i64 in 32-bit code is two halves: Lo and Hi, never one operand)
                if (x32 && v.Int64) throw new InvalidOperationException($"32-bit code with an i64 as one operand ({v})");
                if (Reg(v) is string r) return Sub(r, bits);
                return $"{Size(bits)} [rbp - {regs.Slots[v]}]";
            }

            string At(VReg v) => At(v, Bits(v));

            bool InRegister(VReg v) => Reg(v) != null;

            /// <summary>An operand at a width: a number, or where its value is.</summary>
            string Operand(Opnd o, int bits) => o.IsImm ? Number(o.Imm, bits) : At(o.Reg!, bits);

            static string Number(long value, int bits) => bits == 64 ? value.ToString() : unchecked((uint)value).ToString();

            static bool FitsImm32(long value) => value >= int.MinValue && value <= int.MaxValue;

            /// <summary>Puts o into the scratch register r (rax, rcx, rdx or r11) at a width.</summary>
            void Load(string r64, Opnd o, int bits)
            {
                string r = Sub(r64, bits);
                if (o.IsImm)
                {
                    if (o.Imm == 0) E($"xor {Sub(r64, 32)}, {Sub(r64, 32)}");
                    else E($"mov {r}, {Number(o.Imm, bits)}");
                }
                else if (!(Reg(o.Reg!) == r64)) E($"mov {r}, {At(o.Reg!, bits)}");
            }

            /// <summary>Writes the scratch register r into v.</summary>
            void Store(VReg v, string r64)
            {
                if (Reg(v) == r64) return;
                E($"mov {At(v)}, {Sub(r64, Bits(v))}");
            }

            /// <summary>v = o, at v's width, by way of rax when both are in memory.</summary>
            void Move(VReg v, Opnd o)
            {
                int bits = Bits(v);
                if (o.IsImm)
                {
                    if (InRegister(v))
                    {
                        if (o.Imm == 0) E($"xor {At(v, 32)}, {At(v, 32)}");
                        else E($"mov {At(v)}, {Number(o.Imm, bits)}");
                    }
                    else if (bits == 32 || FitsImm32(o.Imm)) E($"mov {At(v)}, {Number(o.Imm, bits)}");
                    else
                    {
                        E($"mov rax, {o.Imm}");
                        E($"mov {At(v)}, rax");
                    }
                    return;
                }
                VReg src = o.Reg!;
                if (src == v || Reg(src) != null && Reg(src) == Reg(v)) return;
                if (InRegister(v) || InRegister(src)) E($"mov {At(v)}, {At(src, bits)}");
                else
                {
                    E($"mov {Sub("rax", bits)}, {At(src, bits)}");
                    E($"mov {At(v)}, {Sub("rax", bits)}");
                }
            }

            // ---------------------------------------------------------------- f64s

            /// <summary>Where an f64 is: its xmm register, or its 8 bytes in the frame.</summary>
            string F(VReg v) => Reg(v) is string r ? r : $"qword [rbp - {regs.Slots[v]}]";

            /// <summary>The xmm register an f64 instruction works in: the destination's own, unless avoid is in it.</summary>
            string FTarget(VReg dst, VReg? avoid = null) =>
                InRegister(dst) && !(avoid != null && Reg(avoid) == Reg(dst)) ? Reg(dst)! : "xmm15";

            // (between registers, movaps: movsd keeps the rest of the destination, so it waits
            // for whatever last wrote it, which ties unrelated work together)
            void FLoad(string xmm, VReg v)
            {
                if (Reg(v) == xmm) return;
                E(InRegister(v) ? $"movaps {xmm}, {Reg(v)}" : $"movsd {xmm}, {F(v)}");
            }

            void FStore(VReg v, string xmm)
            {
                if (Reg(v) == xmm) return;
                E(InRegister(v) ? $"movaps {Reg(v)}, {xmm}" : $"movsd {F(v)}, {xmm}");
            }

            void FMove(VReg dst, VReg src)
            {
                if (dst == src || Reg(dst) != null && Reg(dst) == Reg(src)) return;
                if (InRegister(dst) && InRegister(src)) E($"movaps {Reg(dst)}, {Reg(src)}");
                else if (InRegister(dst) || InRegister(src)) E($"movsd {F(dst)}, {F(src)}");
                else
                {
                    E($"movsd xmm15, {F(src)}");
                    E($"movsd {F(dst)}, xmm15");
                }
            }

            /// <summary>
            /// Sets the flags for an f64 comparison (ucomisd: all three set when one side isn't a
            /// number), returning the jump taken when it holds: &lt; and &lt;= compare the other way
            /// round, so each of &lt;, &lt;=, &gt; and &gt;= is one jump that not-a-number fails. ==
            /// and != also ask the parity flag (null here: see FloatHolds).
            /// </summary>
            string? FCompare(Cond c, VReg a, VReg b)
            {
                bool swap = c is Cond.LtS or Cond.LeS;
                VReg left = swap ? b : a, right = swap ? a : b;
                string l = InRegister(left) ? Reg(left)! : "xmm15";
                if (l == "xmm15") E($"movsd xmm15, {F(left)}");
                E($"ucomisd {l}, {F(right)}");
                return c switch
                {
                    Cond.GtS or Cond.LtS => "a",
                    Cond.GeS or Cond.LeS => "ae",
                    _ => null,
                };
            }

            // ---------------------------------------------------------------- i64s in 32-bit code
            //
            // An i64 or u64 is two halves in the frame (the allocator gives it no register there):
            // the low at its slot, the high 4 bytes on. Each instruction on one works them as the
            // generator's EmitWide does, in edx:eax (and ebx:ecx for the library's routines).

            string Lo(VReg v) => $"dword [rbp - {regs.Slots[v]}]";
            string Hi(VReg v) => $"dword [rbp - {regs.Slots[v] - 4}]";

            /// <summary>o's two halves into lo and hi (a 32-bit value widened by its sign: shouldn't happen, an i64's operands are i64s).</summary>
            void LoadPair(Opnd o, string lo, string hi)
            {
                if (o.IsImm)
                {
                    E($"mov {lo}, {unchecked((uint)o.Imm)}");
                    E($"mov {hi}, {unchecked((uint)(o.Imm >> 32))}");
                    return;
                }
                if (!o.Reg!.Wide) throw new InvalidOperationException("a 32-bit value where an i64 goes");
                E($"mov {lo}, {Lo(o.Reg)}");
                E($"mov {hi}, {Hi(o.Reg)}");
            }

            void StorePair(VReg v, string lo, string hi)
            {
                E($"mov {Lo(v)}, {lo}");
                E($"mov {Hi(v)}, {hi}");
            }

            string LoOf(Opnd o) => o.IsImm ? unchecked((uint)o.Imm).ToString() : Lo(o.Reg!);
            string HiOf(Opnd o) => o.IsImm ? unchecked((uint)(o.Imm >> 32)).ToString() : Hi(o.Reg!);

            /// <summary>A place's operand 4 bytes on: [x] made [x + 4].</summary>
            static string Plus4(string place) => place[..^1] + " + 4]";

            /// <summary>An instruction with an i64 in it, in 32-bit code: true when it's done here.</summary>
            bool Wide32(Instr i)
            {
                switch (i)
                {
                    case ConstI { Dst.Int64: true } c:
                        E($"mov {Lo(c.Dst)}, {unchecked((uint)c.Value)}");
                        E($"mov {Hi(c.Dst)}, {unchecked((uint)(c.Value >> 32))}");
                        return true;
                    case CopyI { Dst.Int64: true } c:
                        LoadPair(c.Src, "eax", "edx");
                        StorePair(c.Dst, "eax", "edx");
                        return true;
                    case LoadI { Kind: MemKind.Q64 } l:
                    {
                        string place = Place(l.Place);
                        E($"mov eax, dword {place}");
                        E($"mov edx, dword {Plus4(place)}");
                        StorePair(l.Dst, "eax", "edx");
                        return true;
                    }
                    case StoreI { Kind: MemKind.Q64 } s:
                    {
                        string place = Place(s.Place);
                        LoadPair(s.Src, "eax", "edx");
                        E($"mov dword {place}, eax");
                        E($"mov dword {Plus4(place)}, edx");
                        return true;
                    }
                    case LoadIdxI { Kind: MemKind.Q64 } l:
                        E($"lea eax, {Address(l.Base, l.Index, l.Scale)}");
                        E("mov ecx, [eax]");
                        E("mov edx, [eax + 4]");
                        StorePair(l.Dst, "ecx", "edx");
                        return true;
                    case StoreIdxI { Kind: MemKind.Q64 } st:
                        E($"lea eax, {Address(st.Base, st.Index, st.Scale)}");
                        LoadPair(st.Src, "ecx", "edx");
                        E("mov [eax], ecx");
                        E("mov [eax + 4], edx");
                        return true;
                    case BinI { Dst.Wide: true } b:
                        WideBinary(b);
                        return true;
                    case UnI { Dst.Wide: true } u:
                        LoadPair(u.A, "eax", "edx");
                        if (u.Op == UnOp.Neg)
                        {
                            E("neg eax");
                            E("adc edx, 0");
                            E("neg edx");
                        }
                        else
                        {
                            E("not eax");
                            E("not edx");
                        }
                        StorePair(u.Dst, "eax", "edx");
                        return true;
                    case ExtendI { Dst.Wide: true } x:
                        E($"mov eax, {At(x.Src, 32)}");
                        if (x.Signed) E("cdq");
                        else E("xor edx, edx");
                        StorePair(x.Dst, "eax", "edx");
                        return true;
                    case NarrowI { Src.Wide: true } n:
                    {
                        // (its low half, then as narrow as it's made)
                        string t = InRegister(n.Dst) ? Reg(n.Dst)! : "rax";
                        E($"mov eax, {Lo(n.Src)}");
                        string op = n.Kind switch { MemKind.U8 or MemKind.U16 => "movzx", MemKind.S8 or MemKind.S16 => "movsx", _ => "mov" };
                        string from = n.Kind switch { MemKind.U8 or MemKind.S8 => "al", MemKind.U16 or MemKind.S16 => "ax", _ => "eax" };
                        if (op != "mov" || Sub(t, 32) != "eax") E($"{op} {Sub(t, 32)}, {from}");
                        Store(n.Dst, t);
                        return true;
                    }
                    case SetCcI { Float: false } s when s.A.Int64 || s.B.Reg is { Int64: true }:
                    {
                        string yes = g.NewLabel("wideyes"), done = g.NewLabel("widedone");
                        WideCompare(s.A, s.B, s.Cond, yes, null);
                        E("xor eax, eax");
                        E($"jmp {done}");
                        g.Label(yes);
                        E("mov eax, 1");
                        g.Label(done);
                        Store(s.Dst, "rax");
                        return true;
                    }
                    case ToFloatI { Src.Wide: true } tf:
                    {
                        // (the FPU's fild: all 64 bits, then the f64 it's nearest)
                        string t = FTarget(tf.Dst);
                        E($"fild qword [rbp - {regs.Slots[tf.Src]}]");
                        E("sub rsp, 8");
                        E("fstp qword [rsp]");
                        E($"movsd {t}, qword [rsp]");
                        E("add rsp, 8");
                        FStore(tf.Dst, t);
                        return true;
                    }
                    case ToWholeI tw:
                        // toward zero, all 64 bits (cvttsd2si's: fistp with the FPU rounding toward zero)
                        E("sub rsp, 16");
                        E($"movsd xmm14, {F(tw.Src)}");
                        E("movsd qword [rsp], xmm14");
                        E("fld qword [rsp]");
                        E("fnstcw [rsp + 8]");
                        E("mov ax, [rsp + 8]");
                        E("or ax, 0x0C00");
                        E("mov [rsp + 10], ax");
                        E("fldcw [rsp + 10]");
                        E($"fistp qword [rbp - {regs.Slots[tw.Dst]}]");
                        E("fldcw [rsp + 8]");
                        E("add rsp, 16");
                        return true;
                    case PrintI { Kind: PrintKind.Int64 or PrintKind.Uint64 } p:
                    {
                        string convert = p.Kind == PrintKind.Uint64 ? "phi_uint64_to_str" : "phi_int64_to_str";
                        g.Need(convert);
                        string print = "phi_print" + (p.Serial ? "_serial" : "");
                        g.Need(print);
                        LoadPair(p.Value, "eax", "edx");
                        E($"lea rdi, {S(0)}");
                        E("mov ecx, 24");
                        E($"call {convert}");
                        E($"lea rsi, {S(0)}");
                        E($"call {print}");
                        return true;
                    }
                    case NumberTextI { Kind: PrintKind.Int64 or PrintKind.Uint64 } nt:
                    {
                        string convert = nt.Kind == PrintKind.Uint64 ? "phi_uint64_to_str" : "phi_int64_to_str";
                        g.Need(convert);
                        LoadPair(nt.Value, "eax", "edx");
                        E($"lea rdi, {S(0)}");
                        E("mov ecx, 24");
                        E($"call {convert}");
                        string r = InRegister(nt.Dst) ? Reg(nt.Dst)! : "rax";
                        E($"lea {r}, {S(0)}");
                        Store(nt.Dst, r);
                        return true;
                    }
                }
                return false;
            }

            /// <summary>An i64's add, take away, bit operations (half by half), shift, or the library's multiply, divide and remainder.</summary>
            void WideBinary(BinI b)
            {
                switch (b.Op)
                {
                    case BinOp.Add or BinOp.Sub or BinOp.And or BinOp.Or or BinOp.Xor:
                    {
                        LoadPair(b.A, "eax", "edx");
                        (string lo, string hi) = b.Op switch
                        {
                            BinOp.Add => ("add", "adc"),
                            BinOp.Sub => ("sub", "sbb"),
                            BinOp.And => ("and", "and"),
                            BinOp.Or => ("or", "or"),
                            _ => ("xor", "xor"),
                        };
                        E($"{lo} eax, {LoOf(b.B)}");
                        E($"{hi} edx, {HiOf(b.B)}");
                        StorePair(b.Dst, "eax", "edx");
                        return;
                    }
                    case BinOp.Shl or BinOp.Shr or BinOp.Sar:
                    {
                        // the count in cl (32 or more: a half moved over first), as the generator's
                        if (b.B.IsImm) E($"mov ecx, {b.B.Imm & 63}");
                        else E($"mov ecx, {At(b.B.Reg!, 32)}");
                        LoadPair(b.A, "eax", "edx");
                        string small = g.NewLabel("shift");
                        E("test cl, 32");
                        E($"jz {small}");
                        if (b.Op == BinOp.Shl)
                        {
                            E("mov edx, eax");
                            E("xor eax, eax");
                            g.Label(small);
                            E("shld edx, eax, cl");
                            E("shl eax, cl");
                        }
                        else if (b.Op == BinOp.Shr)
                        {
                            E("mov eax, edx");
                            E("xor edx, edx");
                            g.Label(small);
                            E("shrd eax, edx, cl");
                            E("shr edx, cl");
                        }
                        else
                        {
                            E("mov eax, edx");
                            E("sar edx, 31");
                            g.Label(small);
                            E("shrd eax, edx, cl");
                            E("sar edx, cl");
                        }
                        StorePair(b.Dst, "eax", "edx");
                        return;
                    }
                    default:
                    {
                        string routine = b.Op switch
                        {
                            BinOp.Mul => "phi_mul64",
                            BinOp.DivS => "phi_div64",
                            BinOp.DivU => "phi_udiv64",
                            BinOp.RemS => "phi_mod64",
                            _ => "phi_umod64",
                        };
                        g.Need(routine);
                        LoadPair(b.B, "ecx", "ebx");
                        LoadPair(b.A, "eax", "edx");
                        E($"call {routine}");
                        StorePair(b.Dst, "eax", "edx");
                        return;
                    }
                }
            }

            /// <summary>
            /// Two i64s compared: the high halves (with the sign, or without), then on the same high
            /// half the low ones, without; to yes when it holds, else on (or to no).
            /// </summary>
            void WideCompare(Opnd a, Opnd b, Cond c, string yes, string? no)
            {
                string other = g.NewLabel("wideno");
                LoadPair(a, "eax", "edx");
                E($"cmp edx, {HiOf(b)}");
                (string high, string highNot, string low) = c switch
                {
                    Cond.Eq => ("", "jne", "je"),
                    Cond.Ne => ("jne", "", "jne"),
                    Cond.LtS => ("jl", "jg", "jb"),
                    Cond.LeS => ("jl", "jg", "jbe"),
                    Cond.GtS => ("jg", "jl", "ja"),
                    Cond.GeS => ("jg", "jl", "jae"),
                    Cond.LtU => ("jb", "ja", "jb"),
                    Cond.LeU => ("jb", "ja", "jbe"),
                    Cond.GtU => ("ja", "jb", "ja"),
                    _ => ("ja", "jb", "jae"),
                };
                if (high != "") E($"{high} {yes}");
                if (highNot != "") E($"{highNot} {no ?? other}");
                E($"cmp eax, {LoOf(b)}");
                E($"{low} {yes}");
                if (no != null) E($"jmp {no}");
                g.Label(other);
            }

            // ---------------------------------------------------------------- instructions

            void Instruction(Instr i)
            {
                if (x32 && Wide32(i)) return;
                switch (i)
                {
                    case LineMark mark:
                        pendingLine = mark.Node;
                        break;
                    case ConstI { Dst.Vec: true } zv:
                    {
                        // (a vec that's read before it's set starts as zeros)
                        string t = VTarget(zv.Dst);
                        E($"pxor {t}, {t}");
                        VStore(zv.Dst, t);
                        break;
                    }
                    case ConstI { Dst.Float: true } zf:
                    {
                        string t = FTarget(zf.Dst);
                        E($"xorps {t}, {t}");
                        FStore(zf.Dst, t);
                        break;
                    }
                    case ConstI c:
                        Move(c.Dst, Opnd.Const(c.Value));
                        break;
                    case CopyI c when c.Dst.Vec:
                        VMove(c.Dst, c.Src.Reg!);
                        break;
                    case CopyI c when c.Dst.Float:
                        FMove(c.Dst, c.Src.Reg!);
                        break;
                    case VecI or VecMakeI or VecGetI or VecSetI or VecReduceI or RoundF32I or PrintI or RoutineI:
                        Vector(i);
                        break;
                    case TextI t:
                        Text(t);
                        break;
                    case AtomicI at:
                    {
                        string size = Size(8 * at.Bytes);
                        string address = InRegister(at.Address) ? Reg(at.Address)! : "r11";
                        if (address == "r11") E($"mov r11, {At(at.Address, 64)}");
                        if (at.Op == Syntax.AtomicOp.CompareSwap)
                        {
                            Load("rcx", at.Value, at.Bytes == 8 ? 64 : 32);
                            Load("rax", at.Expected, at.Bytes == 8 ? 64 : 32);
                            E($"lock cmpxchg {size} [{address}], {Sub("rcx", 8 * at.Bytes)}");
                            E("sete al");
                            E("movzx eax, al");
                            Store(at.Dst!, "rax");
                            break;
                        }
                        Load("rax", at.Value, at.Bytes == 8 ? 64 : 32);
                        E(at.Op switch
                        {
                            Syntax.AtomicOp.Add => $"lock add {size} [{address}], {Sub("rax", 8 * at.Bytes)}",
                            Syntax.AtomicOp.Subtract => $"lock sub {size} [{address}], {Sub("rax", 8 * at.Bytes)}",
                            _ => $"xchg {size} [{address}], {Sub("rax", 8 * at.Bytes)}",
                        });
                        break;
                    }
                    case SysCallI sc:
                    {
                        // the generator's EmitSystemCall: each argument by way of the stack (a value may
                        // be in a register another is to go in), then the call
                        MarkNow();
                        g.Comment($"sys.{sc.Name}, system call {sc.Number}");
                        if (x32)
                        {
                            // 32-bit Linux's: int 0x80, the arguments in ebx, ecx, edx, esi, edi and ebp,
                            // the four kept around it (the generator's EmitSystemCall; an i64 gives its low half)
                            string[] kept = { "ebp", "edi", "esi", "ebx" };
                            string[] into32 = { "ebx", "ecx", "edx", "esi", "edi", "ebp" };
                            foreach (string r in kept) g.Emit($"push {r}");
                            foreach (Opnd a in sc.Args)
                            {
                                if (a.IsImm) E($"push {unchecked((int)a.Imm)}");
                                else if (a.Reg!.Int64) E($"push {Lo(a.Reg)}");
                                else
                                {
                                    Load("rax", a, 32);
                                    E("push rax");
                                }
                            }
                            for (int k = sc.Args.Count - 1; k >= 0; k--) g.Emit($"pop {into32[k]}");
                            E($"mov eax, {sc.Number}");
                            g.Emit("int 0x80");
                            foreach (string r in kept.Reverse()) g.Emit($"pop {r}");
                            if (sc.Dst != null)
                            {
                                if (sc.Dst.Int64)
                                {
                                    E("cdq");
                                    StorePair(sc.Dst, "eax", "edx");
                                }
                                else Store(sc.Dst, "rax");
                            }
                            break;
                        }
                        string[] into = { "rdi", "rsi", "rdx", "r10", "r8", "r9" };
                        foreach (Opnd a in sc.Args)
                        {
                            if (a.IsImm && FitsImm32(a.Imm)) E($"push {a.Imm}");
                            else
                            {
                                Load("rax", a, 64);
                                E("push rax");
                            }
                        }
                        for (int k = sc.Args.Count - 1; k >= 0; k--) E($"pop {into[k]}");
                        E($"mov eax, {sc.Number}");
                        E("syscall");
                        if (sc.Dst != null) Store(sc.Dst, "rax");
                        break;
                    }
                    case GrowStoreI gs:
                    {
                        // the generator's own store into a str that grows: the value, then the slot, on the stack
                        Load("rax", gs.Value, gs.SourceIsString ? 64 : 32);
                        E("push rax");
                        Load("rax", gs.Slot, 64);
                        E("push rax");
                        MarkNow();
                        g.textMayCut = gs.MayCut;
                        g.EmitGrowStoreAt(gs.RoomOffset ?? g.RoomAt, gs.SourceIsString, gs.Append, gs.Unsigned,
                                          rooms: gs.Rooms != null ? $"dword [{(x32 ? To32(gs.Rooms) : gs.Rooms)}]" : null, known: gs.Known);
                        g.textMayCut = false;
                        break;
                    }
                    case ThrowI th:
                    {
                        // the generator's throw: the text in rdx, the line, the file, the kind; phi_throw copies the text first
                        g.Need("phi_throw");
                        WriteBack(th.Held);
                        Load("rdx", th.Text, 64);
                        E($"mov eax, {g.LineOf(th.At)}");
                        E($"lea rsi, [rel {g.StringLabel(System.IO.Path.GetFileName(g.file.Path))}]");
                        E($"mov ebx, {ErrorThrown}");
                        E("jmp phi_throw");
                        break;
                    }
                    case InterfaceMethodI im:
                        Load("rax", im.Receiver, 64);
                        E("mov eax, [rax - 4]      ; its type table");
                        E($"mov eax, [rax + {g.InterfaceSlot(im.Interface)} + {4 * im.Index}]");
                        Store(im.Dst, "rax");
                        break;
                    case HasTextI ht:
                    {
                        string routine = ht.Character ? "phi_str_has_char" : "phi_str_has";
                        g.Need(routine);
                        // (through the stack: each may be where the other goes)
                        E($"push {Operand(ht.Value, 64)}");
                        Load("rsi", ht.Text, 64);
                        E(ht.Character ? "pop rax" : "pop rdi");
                        E($"call {routine}");
                        Store(ht.Dst, "rax");
                        break;
                    }
                    case StopI st:
                        WriteBack(st.Held);
                        MarkNow();
                        g.EmitStop(st.Text, st.At, st.Kind);
                        break;
                    case ArrayRoutineI ar:
                        if (ar.Made != null) E($"push {Operand(ar.Made, 64)}");
                        Load("rax", ar.View, 64);
                        if (ar.Made != null) E("pop rdx");
                        E($"call {g.ArrayRoutine(ar.Kind, ar.Element)}");
                        break;
                    case ExitI ex:
                        g.Need("phi_exit");
                        WriteBack(ex.Held);
                        Load("rax", ex.Code, 32);
                        E("mov ebx, eax");
                        E("jmp phi_exit");
                        break;
                    case AddrOfI ao:
                    {
                        string r = InRegister(ao.Dst) ? Reg(ao.Dst)! : "rax";
                        E($"lea {r}, [rbp - {regs.Slots[ao.Of]}]");
                        Store(ao.Dst, r);
                        break;
                    }
                    case CompareBytesI cmb:
                        E($"push {Operand(cmb.Count, 64)}");
                        E($"push {Operand(cmb.B, 64)}");
                        Load("rsi", cmb.A, 64);
                        E("pop rdi");
                        E("pop rcx");
                        E("cld");
                        E("xor eax, eax            ; the same so far (ZF set), even for none");
                        E("repe cmpsb");
                        E("sete al");
                        Store(cmb.Dst, "rax");
                        break;
                    case CopyBytesI cb:
                        // (by way of the stack: each may be where another goes)
                        E($"push {Operand(cb.Count, 64)}");
                        E($"push {Operand(cb.Src, 64)}");
                        Load("rdi", cb.Dst, 64);
                        E("pop rsi");
                        E("pop rcx");
                        E("cld");
                        E("rep movsb");
                        break;
                    case ResultTextI rt:
                    {
                        // as the generator's method end: into the caller's slot (it grows) or buffer
                        MarkNow();
                        string place = g.NewLabel("result");
                        if (rt.Grow)
                        {
                            Load("rax", rt.Value, 64);
                            E("push rax");
                            E($"push qword [rbp + {ResultAt}]");
                            g.EmitGrowStoreAt(g.RoomAt, true, false, false);
                            E($"mov rax, [rbp + {ResultAt}]");
                            E("mov rax, [rax]");
                            E("test rax, rax");
                            E($"jnz {place}");
                            E($"lea rax, [rel {g.StringLabel("")}]");
                        }
                        else
                        {
                            g.Need("phi_strcpy");
                            Load("rsi", rt.Value, 64);
                            E($"mov rdi, [rbp + {ResultAt}]");
                            E($"mov ecx, {rt.Room}");
                            E("call phi_strcpy");
                            string ok = g.NewLabel("textfits");
                            E($"jnc {ok}");
                            WriteBack(rt.Held);
                            g.EmitTextError();
                            g.Label(ok);
                            E($"mov rax, [rbp + {ResultAt}]");
                        }
                        g.Label(place);
                        Store(rt.Dst, "rax");
                        break;
                    }
                    case FixedStoreI fs:
                    {
                        // the generator's store into a str with a size (EmitStringStore's last part)
                        Load("rax", fs.Value, fs.SourceIsString ? 64 : 32);
                        Load("rdi", fs.Dst, 64);
                        E($"mov ecx, {fs.Room}");
                        if (fs.Append)
                        {
                            g.Need("phi_str_end");
                            E("call phi_str_end");
                        }
                        if (fs.SourceIsString)
                        {
                            g.Need("phi_strcpy");
                            E("mov rsi, rax");
                            E("call phi_strcpy");
                        }
                        else
                        {
                            string convert = fs.Unsigned ? "phi_uint_to_str" : "phi_int_to_str";
                            g.Need(convert);
                            E($"call {convert}");
                        }
                        if (!fs.MayCut)
                        {
                            string ok = g.NewLabel("textfits");
                            E($"jnc {ok}");
                            WriteBack(fs.Held);
                            MarkNow();
                            g.EmitTextError();
                            g.Label(ok);
                        }
                        break;
                    }
                    case NumberTextI nt:
                        NumberText(nt);
                        break;
                    case FreeTextI ft:
                        E(ft.Value != null ? $"push {Operand(ft.Value, 64)}" : $"push qword {Place(ft.Place)}");
                        E($"call {g.HeapMethodLabel("Heap.Free")}");
                        E($"add rsp, {Word}");
                        break;
                    case ArrayGrowI grow:
                        // (the generator's EmitArrayGrowCall: rax the array's place, edx the count, ecx the size)
                        g.Need("phi_out_of_memory");
                        E($"mov edx, {At(grow.Count, 32)}");
                        Load("rax", grow.View, 64);
                        E($"mov ecx, {grow.Size}");
                        if (grow.Texts) g.EmitTextArrayGrowCall();
                        else
                        {
                            g.usesArrayGrow = true;
                            E("call phi_array_grow");
                        }
                        break;
                    case CatchI c:
                    {
                        // the generator's EmitIfThrows' record: the catch around it, rsp, rbp, the
                        // methods' records, where a throw goes, how deep in the log's lock
                        MarkNow();
                        g.Need("phi_throw");
                        g.Need("phi_log_held");
                        BlockField("mov rax, qword [{0}]", "CATCH");
                        E($"mov qword [{c.Record}], rax");
                        E($"mov qword [{c.Record} + {Word}], rsp");
                        E($"mov qword [{c.Record} + {2 * Word}], rbp");
                        BlockField("mov rax, qword [{0}]", "UNWIND");
                        E($"mov qword [{c.Record} + {3 * Word}], rax");
                        E($"lea rax, [rel {labels[c.Caught]}]");
                        E($"mov qword [{c.Record} + {4 * Word}], rax");
                        E("call phi_log_held");
                        E($"mov qword [{c.Record} + {5 * Word}], rax");
                        E($"lea rax, [{c.Record}]");
                        BlockField("mov qword [{0}], rax", "CATCH");
                        break;
                    }
                    case UncatchI u:
                        E($"mov rax, qword [{u.Record}]");
                        MarkNow();
                        BlockField("mov qword [{0}], rax", "CATCH");
                        break;
                    case CaughtI caughtHere:
                    {
                        // the thread's error, copied into err (nothing else is in a register here)
                        E($"lea rdi, [{caughtHere.Place}]");
                        MarkNow();
                        g.EmitLoadBlock("esi");
                        E($"add rsi, {g.BlockField("ERROR")}");
                        E($"mov ecx, {ErrorBytes}");
                        E("rep movsb");
                        break;
                    }
                    case ZeroI z:
                        E("push rdi");
                        E($"lea rdi, [{z.Place}]");
                        E("xor eax, eax");
                        E($"mov ecx, {z.Size}");
                        E("rep stosb");
                        E("pop rdi");
                        break;
                    case LoadI { Kind: MemKind.V128 } vl:
                    {
                        string t = VTarget(vl.Dst);
                        E($"movdqu {t}, {Place(vl.Place)}");
                        VStore(vl.Dst, t);
                        break;
                    }
                    case LoadIdxI { Kind: MemKind.V128 } vli:
                    {
                        string t = VTarget(vli.Dst);
                        E($"movdqu {t}, {Address(vli.Base, vli.Index, vli.Scale)}");
                        VStore(vli.Dst, t);
                        break;
                    }
                    case StoreI { Kind: MemKind.V128 } vs:
                        E($"movdqu {Place(vs.Place)}, {VIn(vs.Src.Reg!, "xmm15")}");
                        break;
                    case StoreIdxI { Kind: MemKind.V128 } vsi:
                    {
                        string x = VIn(vsi.Src.Reg!, "xmm15");
                        E($"movdqu {Address(vsi.Base, vsi.Index, vsi.Scale)}, {x}");
                        break;
                    }
                    case LoadI { Kind: MemKind.F32 } f32l:
                    {
                        string t = FTarget(f32l.Dst);
                        E($"cvtss2sd {t}, dword {Place(f32l.Place)}");
                        FStore(f32l.Dst, t);
                        break;
                    }
                    case LoadIdxI { Kind: MemKind.F32 } f32li:
                    {
                        string t = FTarget(f32li.Dst);
                        E($"cvtss2sd {t}, dword {Address(f32li.Base, f32li.Index, f32li.Scale)}");
                        FStore(f32li.Dst, t);
                        break;
                    }
                    case StoreI { Kind: MemKind.F32 } f32s:
                        E($"cvtsd2ss xmm15, {F(f32s.Src.Reg!)}");
                        E($"movss dword {Place(f32s.Place)}, xmm15");
                        break;
                    case StoreIdxI { Kind: MemKind.F32 } f32si:
                    {
                        E($"cvtsd2ss xmm15, {F(f32si.Src.Reg!)}");
                        E($"movss dword {Address(f32si.Base, f32si.Index, f32si.Scale)}, xmm15");
                        break;
                    }
                    case CopyI c:
                        Move(c.Dst, c.Src);
                        break;
                    case FConstI fc:
                    {
                        string t = FTarget(fc.Dst);
                        long bits = BitConverter.DoubleToInt64Bits(fc.Value);
                        if (bits == 0) E($"xorps {t}, {t}");
                        else E($"movsd {t}, qword [rel {g.FloatLabel(fc.Value)}]");
                        FStore(fc.Dst, t);
                        break;
                    }
                    case FBinI fb:
                    {
                        string t = FTarget(fb.Dst, fb.B);
                        FLoad(t, fb.A);
                        string op = fb.Op switch { FOp.Add => "addsd", FOp.Sub => "subsd", FOp.Mul => "mulsd", _ => "divsd" };
                        E($"{op} {t}, {F(fb.B)}");
                        FStore(fb.Dst, t);
                        break;
                    }
                    case X87I x87:
                    {
                        // through the stack to the FPU and back, as the generator works it out
                        E("sub rsp, 16");
                        E($"movsd xmm14, {F(x87.A)}");
                        E("movsd qword [rsp], xmm14");
                        if (x87.B != null)
                        {
                            E($"movsd xmm14, {F(x87.B)}");
                            E("movsd qword [rsp + 8], xmm14");
                            E("fld qword [rsp + 8]");
                            E("fld qword [rsp]          ; st0 = the left, st1 = the right");
                            string again = g.NewLabel("fprem");
                            g.Label(again);
                            E("fprem");
                            E("fnstsw ax");
                            E("test ah, 4");
                            E($"jnz {again}              ; c2: not finished");
                            E("fstp st1");
                        }
                        else
                        {
                            E("fld qword [rsp]");
                            if (x87.Rounding < 0) E(x87.Instruction);
                            else
                            {
                                E("fnstcw [rsp + 8]");
                                E("mov ax, [rsp + 8]");
                                E("and ax, 0xF3FF");
                                E($"or ax, {x87.Rounding << 10}         ; how frndint rounds");
                                E("mov [rsp + 10], ax");
                                E("fldcw [rsp + 10]");
                                E("frndint");
                                E("fldcw [rsp + 8]");
                            }
                        }
                        E("fstp qword [rsp]");
                        E("movsd xmm14, qword [rsp]");
                        E("add rsp, 16");
                        FStore(x87.Dst, "xmm14");
                        break;
                    }
                    case FNegI fneg:
                    {
                        string t = FTarget(fneg.Dst);
                        FLoad(t, fneg.A);
                        if (x32) E($"movsd xmm14, qword [rel {g.FloatLabel(-0.0)}]");     // (its sign bit alone)
                        else
                        {
                            E("mov rax, 0x8000000000000000");
                            E("movq xmm14, rax");
                        }
                        E($"xorpd {t}, xmm14");
                        FStore(fneg.Dst, t);
                        break;
                    }
                    case ToFloatI tf:
                    {
                        string t = FTarget(tf.Dst);
                        int bits = tf.Src.Wide ? 64 : 32;
                        E($"xorps {t}, {t}");
                        if (tf.Signed) E($"cvtsi2sd {t}, {At(tf.Src, bits)}");
                        else
                        {
                            // a u64 with its top bit set is halved (keeping the bit that rounds), made an f64, and doubled
                            string big = g.NewLabel("u64big"), done = g.NewLabel("u64done");
                            E($"mov rax, {At(tf.Src, 64)}");
                            E("test rax, rax");
                            E($"js {big}");
                            E($"cvtsi2sd {t}, rax");
                            E($"jmp {done}");
                            g.Label(big);
                            E("mov rcx, rax");
                            E("shr rcx, 1");
                            E("and eax, 1");
                            E("or rcx, rax");
                            E($"cvtsi2sd {t}, rcx");
                            E($"addsd {t}, {t}");
                            g.Label(done);
                        }
                        FStore(tf.Dst, t);
                        break;
                    }
                    case ToWholeI tw:
                        E($"cvttsd2si rax, {F(tw.Src)}");
                        Store(tw.Dst, "rax");
                        break;
                    case LoadI { Kind: MemKind.F64 } fl:
                    {
                        string t = FTarget(fl.Dst);
                        E($"movsd {t}, qword {Place(fl.Place)}");
                        FStore(fl.Dst, t);
                        break;
                    }
                    case LoadIdxI { Kind: MemKind.F64 } fli:
                    {
                        string t = FTarget(fli.Dst);
                        E($"movsd {t}, qword {Address(fli.Base, fli.Index, fli.Scale)}");
                        FStore(fli.Dst, t);
                        break;
                    }
                    case StoreI { Kind: MemKind.F64 } fs:
                    {
                        string src = InRegister(fs.Src.Reg!) ? Reg(fs.Src.Reg!)! : "xmm15";
                        FLoad(src, fs.Src.Reg!);
                        E($"movsd qword {Place(fs.Place)}, {src}");
                        break;
                    }
                    case StoreIdxI { Kind: MemKind.F64 } fsi:
                    {
                        string src = InRegister(fsi.Src.Reg!) ? Reg(fsi.Src.Reg!)! : "xmm15";
                        FLoad(src, fsi.Src.Reg!);
                        E($"movsd qword {Address(fsi.Base, fsi.Index, fsi.Scale)}, {src}");
                        break;
                    }
                    case SetCcI { Float: true } fsc:
                    {
                        string? holds = FCompare(fsc.Cond, fsc.A, fsc.B.Reg!);
                        if (holds != null) E($"set{holds} al");
                        else if (fsc.Cond == Cond.Eq)
                        {
                            E("sete al");
                            E("setnp cl");
                            E("and al, cl");
                        }
                        else
                        {
                            E("setne al");
                            E("setp cl");
                            E("or al, cl");
                        }
                        E("movzx eax, al");
                        Store(fsc.Dst, "rax");
                        break;
                    }
                    case BinI b:
                        Binary(b);
                        break;
                    case UnI u:
                    {
                        string t = InRegister(u.Dst) ? Reg(u.Dst)! : "rax";
                        Load(t, u.A, Bits(u.Dst));
                        E($"{(u.Op == UnOp.Neg ? "neg" : "not")} {Sub(t, Bits(u.Dst))}");
                        Store(u.Dst, t);
                        break;
                    }
                    case ExtendI x32x when x32:
                        // (in 32-bit code an address is 32 bits: widening one to it is a copy)
                        Move(x32x.Dst, x32x.Src);
                        break;
                    case ExtendI x:
                    {
                        string t = InRegister(x.Dst) ? Reg(x.Dst)! : "rax";
                        if (x.Signed) E($"movsxd {t}, {At(x.Src, 32)}");
                        else if (!(Reg(x.Src) == t)) E($"mov {Sub(t, 32)}, {At(x.Src, 32)}");
                        else E($"mov {Sub(t, 32)}, {Sub(t, 32)}");     // (clears the top half)
                        Store(x.Dst, t);
                        break;
                    }
                    case NarrowI n:
                    {
                        string t = InRegister(n.Dst) ? Reg(n.Dst)! : "rax";
                        if (x32 && n.Kind is MemKind.U8 or MemKind.S8 && InRegister(n.Src) && NoLowByte(Reg(n.Src)!))
                        {
                            // (32-bit code has no low byte of esi or edi: by way of eax)
                            E($"mov eax, {At(n.Src, 32)}");
                            E($"{(n.Kind == MemKind.U8 ? "movzx" : "movsx")} {Sub(t, 32)}, al");
                            Store(n.Dst, t);
                            break;
                        }
                        string from = n.Kind switch
                        {
                            MemKind.U8 or MemKind.S8 => At(n.Src, 8),
                            MemKind.U16 or MemKind.S16 => At(n.Src, 16),
                            _ => At(n.Src, 32),
                        };
                        string op = n.Kind switch
                        {
                            MemKind.U8 or MemKind.U16 => "movzx",
                            MemKind.S8 or MemKind.S16 => "movsx",
                            _ => "mov",
                        };
                        E($"{op} {Sub(t, 32)}, {from}");
                        Store(n.Dst, t);
                        break;
                    }
                    case SetCcI s:
                    {
                        Compare(s.A, s.B);
                        E($"set{Cc(s.Cond)} al");
                        E("movzx eax, al");
                        Store(s.Dst, "rax");
                        break;
                    }
                    case LoadI l:
                    {
                        string t = InRegister(l.Dst) ? Reg(l.Dst)! : "rax";
                        string place = Place(l.Place);
                        E(l.Kind switch
                        {
                            MemKind.U8 => $"movzx {Sub(t, 32)}, byte {place}",
                            MemKind.S8 => $"movsx {Sub(t, 32)}, byte {place}",
                            MemKind.U16 => $"movzx {Sub(t, 32)}, word {place}",
                            MemKind.S16 => $"movsx {Sub(t, 32)}, word {place}",
                            MemKind.Q64 => $"mov {t}, qword {place}",
                            _ => $"mov {Sub(t, 32)}, dword {place}",
                        });
                        Store(l.Dst, t);
                        break;
                    }
                    case StoreI s:
                    {
                        int bits = s.Kind switch { MemKind.U8 or MemKind.S8 => 8, MemKind.U16 or MemKind.S16 => 16, MemKind.Q64 => 64, _ => 32 };
                        string place = Place(s.Place);
                        if (s.Src.IsImm && (bits < 64 || FitsImm32(s.Src.Imm)))
                            E($"mov {Size(bits)} {place}, {(bits == 64 ? s.Src.Imm : unchecked((uint)s.Src.Imm) & (bits == 32 ? 0xFFFFFFFF : (1L << bits) - 1))}");
                        else
                        {
                            string src = s.Src.IsImm || !InRegister(s.Src.Reg!) || x32 && bits == 8 && NoLowByte(Reg(s.Src.Reg!)!) ? "rax" : Reg(s.Src.Reg!)!;
                            if (src == "rax") Load("rax", s.Src, bits == 64 ? 64 : 32);
                            E($"mov {Size(bits)} {place}, {Sub(src, bits)}");
                        }
                        break;
                    }
                    case CallI call:
                        Call(call);
                        break;
                    case ObjCallI oc:
                        // (the object copied from in rdx, by way of the stack: it may be in rax's way)
                        if (oc.Second != null) E($"push {Operand(oc.Second, 64)}");
                        if (oc.Arg != null) Load("rax", oc.Arg, 64);
                        else E($"lea rax, [{oc.Home}]");
                        if (oc.Second != null) E("pop rdx");
                        E($"call {g.ObjectRoutine(oc.Kind, oc.Struct)}");
                        if (oc.Dst != null) Store(oc.Dst, "rax");
                        break;
                    case ObjectLineI ol:
                        g.usesObjects = true;
                        E($"mov dword [phi_object_line], {g.LineOf(ol.At)}");
                        break;
                    case UnwindI { On: true }:
                    {
                        // onto the list a throw looks at: the record before, what frees this
                        // method's objects, and this frame (the generator's own record)
                        int at = fn.UnwindRecord;
                        MarkNow();
                        BlockField("mov rax, qword [{0}]", "UNWIND");
                        E($"mov qword [rbp - {at}], rax");
                        E($"lea rax, [rel {methodLabel}_unwind]");
                        E($"mov qword [rbp - {at - Word}], rax");
                        E($"mov qword [rbp - {at - 2 * Word}], rbp");
                        E($"lea rax, [rbp - {at}]");
                        BlockField("mov qword [{0}], rax", "UNWIND");
                        break;
                    }
                    case UnwindI { On: false }:
                        // off it: the record before is the one a throw looks at again
                        E($"mov rcx, qword [rbp - {fn.UnwindRecord}]");
                        MarkNow();
                        BlockField("mov qword [{0}], rcx", "UNWIND");
                        break;
                    case RawCallI raw:
                        // (every value lives in the frame across it: the allocator saw to that)
                        E($"call {raw.Label}");
                        if (raw.Dst != null) Store(raw.Dst, "rax");
                        break;
                    case LockI lk:
                    {
                        // the generator's EmitLocked, for a program: a plain store while it has one thread
                        g.Need("phi_threads_many");
                        string spin = g.NewLabel("lock"), wait = g.NewLabel("lockwait"), heldLabel = g.NewLabel("locked");
                        E($"lea rax, [rel {lk.Label}]");
                        E("cmp dword [rel phi_threads_many], 0");
                        E($"jne {spin}");
                        E("cmp dword [rax], 0");
                        E($"jne {spin}");
                        E("mov dword [rax], 1");
                        E($"jmp {heldLabel}");
                        g.Label(spin);
                        E("mov ecx, 1");
                        E("xchg [rax], ecx");
                        E("test ecx, ecx");
                        E($"jz {heldLabel}");
                        g.Label(wait);
                        E("pause");
                        E("cmp dword [rax], 0");
                        E($"jne {wait}");
                        E($"jmp {spin}");
                        g.Label(heldLabel);
                        if (g.Catches)
                        {
                            // what the lock guards may be half changed when something goes wrong in
                            // here, so nothing is caught until it's given back: it stops the program
                            BlockField("mov rcx, qword [{0}]", "CATCH");
                            Store(lk.Saved, "rcx");
                            BlockField("mov qword [{0}], 0", "CATCH");
                        }
                        break;
                    }
                    case UnlockI ul:
                        if (g.Catches)
                        {
                            Load("rcx", ul.Saved, 64);
                            BlockField("mov qword [{0}], rcx", "CATCH");
                        }
                        E($"lea rcx, [rel {ul.Label}]");
                        E("mov dword [rcx], 0");
                        break;
                    case LeaI lea:
                    {
                        // (lea works out an address: 32 bits of it is the 32-bit number, wrapped as a multiply would)
                        int bits = Bits(lea.Dst);
                        string t = InRegister(lea.Dst) ? Reg(lea.Dst)! : "rax";
                        string a = InRegister(lea.A) ? Reg(lea.A)! : "rcx";
                        if (a == "rcx") E($"mov {Sub("rcx", bits)}, {At(lea.A, bits)}");
                        string disp = lea.Add == 0 ? "" : lea.Add > 0 ? $" + {lea.Add}" : $" - {-lea.Add}";
                        E($"lea {Sub(t, bits)}, [{Sub(a, bits)} + {Sub(a, bits)}*{lea.Factor - 1}{disp}]");
                        Store(lea.Dst, t);
                        break;
                    }
                    case AddrI addr:
                    {
                        string t = InRegister(addr.Dst) ? Reg(addr.Dst)! : "rax";
                        E($"lea {t}, {Place(addr.Label)}");
                        Store(addr.Dst, t);
                        break;
                    }
                    case LoadIdxI l:
                    {
                        string t = InRegister(l.Dst) ? Reg(l.Dst)! : "rax";
                        string place = Address(l.Base, l.Index, l.Scale);
                        E(l.Kind switch
                        {
                            MemKind.U8 => $"movzx {Sub(t, 32)}, byte {place}",
                            MemKind.S8 => $"movsx {Sub(t, 32)}, byte {place}",
                            MemKind.U16 => $"movzx {Sub(t, 32)}, word {place}",
                            MemKind.S16 => $"movsx {Sub(t, 32)}, word {place}",
                            MemKind.Q64 => $"mov {t}, qword {place}",
                            _ => $"mov {Sub(t, 32)}, dword {place}",
                        });
                        Store(l.Dst, t);
                        break;
                    }
                    case StoreIdxI st:
                    {
                        int bits = st.Kind switch { MemKind.U8 or MemKind.S8 => 8, MemKind.U16 or MemKind.S16 => 16, MemKind.Q64 => 64, _ => 32 };
                        string place = Address(st.Base, st.Index, st.Scale);
                        if (st.Src.IsImm)
                            E($"mov {Size(bits)} {place}, {(bits == 64 ? st.Src.Imm.ToString() : (unchecked((ulong)st.Src.Imm) & (bits == 32 ? 0xFFFFFFFFUL : (1UL << bits) - 1)).ToString())}");
                        else
                        {
                            string src = InRegister(st.Src.Reg!) && !(x32 && bits == 8 && NoLowByte(Reg(st.Src.Reg!)!)) ? Reg(st.Src.Reg!)! : "rax";
                            if (src == "rax") E($"mov {(bits == 64 ? "rax" : "eax")}, {At(st.Src.Reg!, bits == 64 ? 64 : 32)}");
                            E($"mov {Size(bits)} {place}, {Sub(src, bits)}");
                        }
                        break;
                    }
                    case CheckI ic:
                    {
                        // as the generator's EmitIndexCheck: unsigned, so a negative index fails too
                        g.Need("phi_index_error");
                        string ok = g.NewLabel("inbounds");
                        if (ic.LimitValue is VReg limit)
                        {
                            E($"mov rax, {At(limit, 64)}");
                            E($"cmp {At(ic.Index, 64)}, rax");
                        }
                        else E($"cmp {At(ic.Index, 64)}, {ic.Limit}");
                        E($"jb {ok}");
                        WriteBack(ic.Held);
                        g.EmitIndexError(ic.At);
                        g.Label(ok);
                        break;
                    }
                    case SignCheckI check:
                    {
                        // as the generator's EmitNumberCheck: the top bit set stops with its error
                        string ok = g.NewLabel("numberok");
                        E($"test {At(check.Value, 32)}, {(InRegister(check.Value) ? At(check.Value, 32) : "0x80000000")}");
                        E($"j{(InRegister(check.Value) ? "ns" : "z")} {ok}");
                        WriteBack(check.Held);
                        g.EmitStop(check.Text, check.At, ErrorConvert);
                        g.Label(ok);
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"the intermediate form has an instruction the emitter doesn't know: {i.GetType().Name}");
                }
            }

            /// <summary>
            /// The address of base + index * scale, as an operand: the base in its register (or
            /// r11 when it's in the frame), the index in its own (or rcx) or written in as a number.
            /// </summary>
            string Address(VReg bas, Opnd index, int scale)
            {
                string b = InRegister(bas) ? Reg(bas)! : "r11";
                if (b == "r11") E($"mov r11, {At(bas, 64)}");
                if (index.IsImm)
                {
                    long offset = index.Imm * scale;
                    return offset == 0 ? $"[{b}]" : offset > 0 ? $"[{b} + {offset}]" : $"[{b} - {-offset}]";
                }
                if (scale == 16)
                {
                    // (an address scales by 8 at most: a vec's index is shifted first)
                    E($"mov rcx, {At(index.Reg!, 64)}");
                    E("shl rcx, 4");
                    return $"[{b} + rcx]";
                }
                string i = InRegister(index.Reg!) ? Reg(index.Reg!)! : "rcx";
                if (i == "rcx") E($"mov rcx, {At(index.Reg!, 64)}");
                return scale == 1 ? $"[{b} + {i}]" : $"[{b} + {i}*{scale}]";
            }

            /// <summary>Before an error leaves the method: the class variables a loop holds, back where they're kept.</summary>
            void WriteBack(List<StoreI> held)
            {
                foreach (StoreI st in held) Instruction(st);
            }

            /// <summary>A place an instruction names: a parameter's in the frame, or a class's variable's label.</summary>
            static string Place(string place) => place.StartsWith("rbp") ? $"[{place}]" : $"[rel {place}]";

            void Binary(BinI b)
            {
                int bits = Bits(b.Dst);
                switch (b.Op)
                {
                    case BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU when bits == 32 && !x32 && b.B.IsImm && DivideByConstant(b):
                        return;
                    case BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU:
                    {
                        bool signed = b.Op is BinOp.DivS or BinOp.RemS;
                        Load("rax", b.A, bits);
                        // (a divisor that's a number goes in rcx; div takes only a register or memory)
                        string divisor = b.B.IsImm ? Sub("rcx", bits) : At(b.B.Reg!, bits);
                        if (b.B.IsImm) Load("rcx", b.B, bits);
                        if (signed) E(bits == 64 ? "cqo" : "cdq");
                        else E("xor edx, edx");
                        E($"{(signed ? "idiv" : "div")} {divisor}");
                        Store(b.Dst, b.Op is BinOp.DivS or BinOp.DivU ? "rax" : "rdx");
                        return;
                    }
                    case BinOp.Shl or BinOp.Shr or BinOp.Sar:
                    {
                        string op = b.Op switch { BinOp.Shl => "shl", BinOp.Shr => "shr", _ => "sar" };
                        string t = InRegister(b.Dst) && !(b.B.Reg != null && Reg(b.B.Reg) == Reg(b.Dst)) ? Reg(b.Dst)! : "rax";
                        if (b.B.IsImm)
                        {
                            Load(t, b.A, bits);
                            E($"{op} {Sub(t, bits)}, {b.B.Imm & (bits == 64 ? 63 : 31)}");
                        }
                        else
                        {
                            E($"mov ecx, {At(b.B.Reg!, 32)}");
                            Load(t, b.A, bits);
                            E($"{op} {Sub(t, bits)}, cl");
                        }
                        Store(b.Dst, t);
                        return;
                    }
                }
                // the rest: t = A, then t op= B, with t the destination's own register unless B is in it
                bool bInDst = b.B.Reg != null && InRegister(b.Dst) && Reg(b.B.Reg) == Reg(b.Dst);
                string target = InRegister(b.Dst) && !bInDst ? Reg(b.Dst)! : "rax";
                string tb = Sub(target, bits);
                if (b.Op == BinOp.Mul && b.B.IsImm)
                {
                    // imul with a number takes its own source: no copy of A first
                    string src = b.A.IsImm ? null! : At(b.A.Reg!, bits);
                    if (b.A.IsImm)
                    {
                        Load(target, b.A, bits);
                        src = tb;
                    }
                    E($"imul {tb}, {src}, {Number(b.B.Imm, bits == 64 ? 64 : 32)}");
                    Store(b.Dst, target);
                    return;
                }
                Load(target, b.A, bits);
                string op2 = b.Op switch
                {
                    BinOp.Add => "add",
                    BinOp.Sub => "sub",
                    BinOp.Mul => "imul",
                    BinOp.And => "and",
                    BinOp.Or => "or",
                    _ => "xor",
                };
                E($"{op2} {tb}, {Operand(b.B, bits)}");
                Store(b.Dst, target);
            }

            /// <summary>
            /// x / d and x % d, 32 bits, with d a number of 2 or more: the generator's own
            /// TryDivideByConstant, instruction for instruction (a shift or a mask for a power of
            /// two, a multiply by a number near 2^32 / d for any other), into the destination.
            /// A divisor of 0, 1 or less is left to the CPU's divide, whose error dividing by 0 is.
            /// </summary>
            bool DivideByConstant(BinI b)
            {
                bool unsigned = b.Op is BinOp.DivU or BinOp.RemU, modulo = b.Op is BinOp.RemS or BinOp.RemU;
                long d = b.B.Imm;
                if (d < 2 || d > (unsigned ? uint.MaxValue : int.MaxValue)) return false;
                bool powerOfTwo = (d & (d - 1)) == 0;
                int shift = System.Numerics.BitOperations.Log2((ulong)d);
                Load("rax", b.A, 32);
                if (powerOfTwo && unsigned)
                    E(modulo ? $"and eax, {(int)(d - 1)}" : $"shr eax, {shift}");
                else if (powerOfTwo)
                {
                    E("cdq");
                    E($"and edx, {(int)(d - 1)}");
                    E("add eax, edx");
                    if (modulo)
                    {
                        E($"and eax, {(int)(d - 1)}");
                        E("sub eax, edx");
                    }
                    else E($"sar eax, {shift}");
                }
                else if (unsigned)
                {
                    DivisionMagic.Unsigned magic = DivisionMagic.ForUnsigned((uint)d);
                    if (magic.Add && modulo) E("push rax");
                    if (magic.Add || modulo) E("mov ecx, eax");
                    E($"mov edx, {magic.Multiplier}");
                    E("mul edx");
                    if (magic.Add)
                    {
                        E("sub ecx, edx");
                        E("shr ecx, 1");
                        E("add edx, ecx");
                        if (magic.Shift > 1) E($"shr edx, {magic.Shift - 1}");
                        if (modulo) E("pop rcx");
                    }
                    else if (magic.Shift > 0) E($"shr edx, {magic.Shift}");
                    if (modulo)
                    {
                        E($"imul edx, edx, {(int)d}");
                        E("sub ecx, edx");
                        E("mov eax, ecx");
                    }
                    else E("mov eax, edx");
                }
                else
                {
                    DivisionMagic.Signed magic = DivisionMagic.ForSigned((int)d);
                    E("mov ecx, eax");
                    E($"mov edx, {magic.Multiplier}");
                    E("imul edx");
                    if (magic.AddDividend) E("add edx, ecx");
                    if (magic.Shift > 0) E($"sar edx, {magic.Shift}");
                    E("mov eax, edx");
                    E("shr eax, 31");
                    E("add eax, edx");
                    if (modulo)
                    {
                        E($"imul eax, eax, {(int)d}");
                        E("sub ecx, eax");
                        E("mov eax, ecx");
                    }
                }
                Store(b.Dst, "rax");
                return true;
            }

            /// <summary>Sets the flags for A against B, at A's width.</summary>
            void Compare(VReg a, Opnd b)
            {
                int bits = Bits(a);
                if (b.IsImm && b.Imm == 0 && InRegister(a))
                {
                    E($"test {At(a)}, {At(a)}");
                    return;
                }
                string left = At(a);
                if (!InRegister(a) && !b.IsImm && !InRegister(b.Reg!))
                {
                    Load("rax", a, bits);
                    left = Sub("rax", bits);
                }
                E($"cmp {left}, {Operand(b, bits)}");
            }

            /// <summary>The jump tables the method's whens use, written after its code.</summary>
            readonly List<(string Label, List<string> Targets)> tables = new();

            /// <summary>
            /// when: with 4 or more cases close together (no more than three places for each case),
            /// a table of where each value jumps, indexed by the value less the smallest; otherwise
            /// a compare and jump for each case.
            /// </summary>
            void Switch(SwitchT sw, Block? next)
            {
                int bits = Bits(sw.Value);
                string v = At(sw.Value);
                if (sw.Cases.Count >= 4)
                {
                    long low = sw.Cases.Min(c => c.Value), high = sw.Cases.Max(c => c.Value);
                    long span = high - low + 1;
                    if (span <= 3 * sw.Cases.Count && span <= 1024 && low >= int.MinValue && low <= int.MaxValue)
                    {
                        string table = g.NewLabel("irtable");
                        var targets = new List<string>();
                        for (long k = low; k <= high; k++)
                        {
                            Block to = sw.Cases.FirstOrDefault(c => c.Value == k).Target ?? sw.Default;
                            targets.Add(labels[to]);
                        }
                        tables.Add((table, targets));
                        E($"mov {Sub("rax", bits)}, {v}");
                        if (low != 0) E($"sub {Sub("rax", bits)}, {Number(low, bits)}");
                        E($"cmp {Sub("rax", bits)}, {span - 1}");
                        E($"ja {labels[sw.Default]}");
                        if (bits == 32) E("mov eax, eax");
                        E($"lea r11, [rel {table}]");
                        E($"jmp [r11 + rax*{Word}]");
                        return;
                    }
                }
                foreach (var (value, target) in sw.Cases)
                {
                    if (bits == 64 && !FitsImm32(value))
                    {
                        E($"mov rax, {value}");
                        E($"cmp {v}, rax");
                    }
                    else E($"cmp {v}, {Number(value, bits)}");
                    E($"je {labels[target]}");
                }
                if (sw.Default != next) E($"jmp {labels[sw.Default]}");
            }

            static string Cc(Cond c) => c switch
            {
                Cond.Eq => "e",
                Cond.Ne => "ne",
                Cond.LtS => "l",
                Cond.LeS => "le",
                Cond.GtS => "g",
                Cond.GeS => "ge",
                Cond.LtU => "b",
                Cond.LeU => "be",
                Cond.GtU => "a",
                _ => "ae",
            };

            static Cond Not(Cond c) => c switch
            {
                Cond.Eq => Cond.Ne,
                Cond.Ne => Cond.Eq,
                Cond.LtS => Cond.GeS,
                Cond.LeS => Cond.GtS,
                Cond.GtS => Cond.LeS,
                Cond.GeS => Cond.LtS,
                Cond.LtU => Cond.GeU,
                Cond.LeU => Cond.GtU,
                Cond.GtU => Cond.LeU,
                _ => Cond.LtU,
            };

            void Call(CallI call)
            {
                int pushed = 0;
                for (int k = 0; k < call.Args.Count; k++)
                {
                    Opnd a = call.Args[k];
                    MemKind kind = k < call.ArgKinds.Count ? call.ArgKinds[k] : MemKind.Q64;
                    pushed += kind == MemKind.V128 ? 16 : x32 ? 4 : 8;
                    if (kind == MemKind.V128)
                    {
                        E("sub rsp, 16");
                        E($"movdqu [rsp], {VIn(a.Reg!, "xmm15")}");
                        continue;
                    }
                    if (x32 && kind == MemKind.Q64)
                    {
                        // (an i64: its high half, then its low, 8 bytes as the generator pushes one)
                        pushed += 4;
                        E($"push {HiOf(a)}");
                        E($"push {LoOf(a)}");
                        continue;
                    }
                    if (x32 && (kind == MemKind.F64 || kind == MemKind.F32 || !a.IsImm && a.Reg!.Float))
                    {
                        // (an f64 takes 8 bytes of the stack in 32-bit code, an f32 4, as the generator pushes them)
                        bool f32 = kind == MemKind.F32;
                        pushed += f32 ? 0 : 4;      // (4 counted above)
                        if (f32) E($"cvtsd2ss xmm15, {F(a.Reg!)}");
                        else FLoad("xmm15", a.Reg!);
                        E($"sub rsp, {(f32 ? 4 : 8)}");
                        E(f32 ? "movss dword [rsp], xmm15" : "movsd qword [rsp], xmm15");
                        continue;
                    }
                    if (kind == MemKind.F32)
                    {
                        // (an f32 is in the low half of its 8 bytes, as the generator pushes one)
                        E($"cvtsd2ss xmm15, {F(a.Reg!)}");
                        E("sub rsp, 8");
                        E("movss dword [rsp], xmm15");
                        continue;
                    }
                    if (a.IsImm)
                    {
                        if (FitsImm32(a.Imm)) E($"push {a.Imm}");
                        else
                        {
                            E($"mov rax, {a.Imm}");
                            E("push rax");
                        }
                    }
                    else if (a.Reg!.Float && InRegister(a.Reg))
                    {
                        E("sub rsp, 8");
                        E($"movsd qword [rsp], {Reg(a.Reg)}");
                    }
                    else E(InRegister(a.Reg!) ? $"push {Reg(a.Reg!)}" : $"push qword [rbp - {regs.Slots[a.Reg!]}]");
                }
                if (call.Target != null)
                {
                    // through a method value: the address it holds
                    E($"mov r11, {At(call.Target, 64)}");
                    E("call r11");
                }
                else
                {
                    if (call.Routine) g.Need(call.Label);
                    E($"call {call.Label}");
                }
                if (pushed > 0) E($"add rsp, {pushed}");
                if (call.Result == CallResult.Xmm0)
                {
                    if (call.Dst != null) VStore(call.Dst, "xmm0");
                    return;
                }
                if (call.Result == CallResult.St0)
                {
                    // an f64 comes back on the x87's stack (as the generator gives one): it's taken
                    // off it, used or not
                    if (call.Dst == null)
                    {
                        E("fstp st0");
                        return;
                    }
                    E("sub rsp, 8");
                    E("fstp qword [rsp]");
                    string t = FTarget(call.Dst);
                    E($"movsd {t}, qword [rsp]");
                    E("add rsp, 8");
                    FStore(call.Dst, t);
                    return;
                }
                if (call.Dst == null) return;
                if (call.Result == CallResult.EdxEax && x32)
                {
                    StorePair(call.Dst, "eax", "edx");
                    return;
                }
                if (call.Result == CallResult.EdxEax)
                {
                    // (a 64-bit result comes back in edx:eax, as the generator gives one)
                    E("shl rdx, 32");
                    E("mov eax, eax");
                    E("or rax, rdx");
                }
                Store(call.Dst, "rax");
            }

            void End(Terminator t, Block? next)
            {
                switch (t)
                {
                    case JumpT j:
                        if (j.Target != next) E($"jmp {labels[j.Target]}");
                        break;
                    case BranchT { Float: true } fbr:
                    {
                        string? holds = FCompare(fbr.Cond, fbr.A, fbr.B.Reg!);
                        if (holds != null)
                        {
                            // (not a number fails the jump: it goes to Else, as it should)
                            if (fbr.Then == next) E($"jn{holds} {labels[fbr.Else]}");
                            else
                            {
                                E($"j{holds} {labels[fbr.Then]}");
                                if (fbr.Else != next) E($"jmp {labels[fbr.Else]}");
                            }
                        }
                        else if (fbr.Cond == Cond.Eq)
                        {
                            E($"jne {labels[fbr.Else]}");
                            E($"jp {labels[fbr.Else]}");
                            if (fbr.Then != next) E($"jmp {labels[fbr.Then]}");
                        }
                        else
                        {
                            E($"jne {labels[fbr.Then]}");
                            E($"jp {labels[fbr.Then]}");
                            if (fbr.Else != next) E($"jmp {labels[fbr.Else]}");
                        }
                        break;
                    }
                    case SwitchT sw:
                        Switch(sw, next);
                        break;
                    case TryT tr:
                        if (tr.Body != next) E($"jmp {labels[tr.Body]}");
                        break;
                    case BranchT { TestMask: long mask } tb:
                        E($"test {At(tb.A)}, {Number(mask, Bits(tb.A))}");
                        if (tb.Then == next) E($"j{Cc(Not(tb.Cond))} {labels[tb.Else]}");
                        else
                        {
                            E($"j{Cc(tb.Cond)} {labels[tb.Then]}");
                            if (tb.Else != next) E($"jmp {labels[tb.Else]}");
                        }
                        break;
                    case BranchT wbr when x32 && (wbr.A.Int64 || wbr.B.Reg is { Int64: true }):
                        WideCompare(wbr.A, wbr.B, wbr.Cond, labels[wbr.Then], labels[wbr.Else]);
                        break;
                    case BranchT br:
                        Compare(br.A, br.B);
                        if (br.Then == next) E($"j{Cc(Not(br.Cond))} {labels[br.Else]}");
                        else
                        {
                            E($"j{Cc(br.Cond)} {labels[br.Then]}");
                            if (br.Else != next) E($"jmp {labels[br.Else]}");
                        }
                        break;
                    case RetT ret:
                        if (ret.Value is Opnd vv && ret.Vec)
                        {
                            // a vec is given back in xmm0
                            string x = VIn(vv.Reg!, "xmm0");
                            if (x != "xmm0") E($"movdqa xmm0, {x}");
                        }
                        else if (ret.Value is Opnd fv && ret.Float)
                        {
                            // an f64 is given back in st0, as the generator gives one
                            if (InRegister(fv.Reg!))
                            {
                                E("sub rsp, 8");
                                E($"movsd qword [rsp], {Reg(fv.Reg!)}");
                                E("fld qword [rsp]");
                                E("add rsp, 8");
                            }
                            else E($"fld {F(fv.Reg!)}");
                        }
                        else if (ret.Value is Opnd v)
                        {
                            if (ret.Pointer) Load("rax", v, 64);
                            else if (ret.Wide && x32) LoadPair(v, "eax", "edx");
                            else if (ret.Wide)
                            {
                                Load("rax", v, 64);
                                E("mov rdx, rax");
                                E("shr rdx, 32");
                                E("mov eax, eax");
                            }
                            else Load("rax", v, 32);
                        }
                        foreach (string r in regs.UsedCalleeSaved) E($"mov {r}, [rbp - {SavedAt(r)}]");
                        E("mov rsp, rbp");
                        E("pop rbp");
                        E("ret");
                        break;
                }
            }
        }
    }
}

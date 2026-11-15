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
            if (!IrWanted || !bits64 || unit.Kind != UnitKind.Program || debugInfo != null) return false;
            // (PHI_IR_SKIP=A.B,C.D leaves those methods to the generator: for finding which one a difference is in)
            if (IrSkip.Contains(m.Name)) return false;
            // (a class's variable a loop may keep in a register: the generator's own rule, see EmitLoopHolding)
            HashSet<VariableSymbol> reached = ReachedByAddress();
            bool Holdable(VariableSymbol v) => !v.IsLocal && !v.IsParameter && !v.IsOutside && v.Unit == unit.Kind && !v.Decl.Volatile && !reached.Contains(v);
            IrFunction? fn = IrBuilder.Build(m, Holdable, Catches && !m.IsInterruptHandler, out string why);
            if (fn == null)
            {
                if (IrTrace > 0) Console.Error.WriteLine($"ir: not {m.Name}: {why}");
                return false;
            }
            IrPeephole.Run(fn);
            RegisterAllocator regs = RegisterAllocator.Allocate(fn);
            if (IrTrace > 0) Console.Error.WriteLine($"ir: {m.Name} ({fn.Values.Count} values, {regs.SlotCount} in the frame)");
            if (IrTrace > 1) Console.Error.WriteLine(fn.Dump());
            new IrEmitter(this, fn, regs, m.Label).Emit();
            return true;
        }

        /// <summary>Writes one method's form as x64 assembly into the generator's code.</summary>
        sealed class IrEmitter
        {
            readonly X86Generator g;
            readonly IrFunction fn;
            readonly RegisterAllocator regs;
            readonly Dictionary<Block, string> labels = new();
            readonly int frame;

            /// <summary>The method's label: a throw going past calls this with _unwind after it, to free its own objects.</summary>
            readonly string methodLabel;

            public IrEmitter(X86Generator generator, IrFunction function, RegisterAllocator allocation, string label)
            {
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
                g.code.Append("    ").AppendLine(instruction);
            }

            int SavedAt(string register) => 8 * (regs.SlotCount + 1 + regs.UsedCalleeSaved.IndexOf(register));

            public void Emit()
            {
                E("push rbp");
                E("mov rbp, rsp");
                if (frame > 0) E($"sub rsp, {frame}");
                foreach (string r in regs.UsedCalleeSaved) E($"mov [rbp - {SavedAt(r)}], {r}");
                for (int k = 0; k < fn.Blocks.Count; k++)
                {
                    Block b = fn.Blocks[k];
                    Block? next = k + 1 < fn.Blocks.Count ? fn.Blocks[k + 1] : null;
                    g.Label(labels[b]);
                    foreach (Instr i in b.Instrs) Instruction(i);
                    End(b.End!, next);
                }
                foreach (var (table, targets) in tables)
                {
                    g.Line("align 8");
                    g.Label(table);
                    foreach (string target in targets) g.Line($"    dq {target}");
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

            string? Reg(VReg v) => regs.Registers.TryGetValue(v, out string? r) ? r : null;

            /// <summary>Where v is, as an operand at a width: its register, or its slot in the frame.</summary>
            string At(VReg v, int bits)
            {
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

            // ---------------------------------------------------------------- instructions

            void Instruction(Instr i)
            {
                switch (i)
                {
                    case LineMark mark:
                        pendingLine = mark.Node;
                        break;
                    case ConstI c:
                        Move(c.Dst, Opnd.Const(c.Value));
                        break;
                    case CopyI c when c.Dst.Float:
                        FMove(c.Dst, c.Src.Reg!);
                        break;
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
                    case FNegI fneg:
                    {
                        string t = FTarget(fneg.Dst);
                        FLoad(t, fneg.A);
                        E("mov rax, 0x8000000000000000");
                        E("movq xmm14, rax");
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
                            string src = s.Src.IsImm || !InRegister(s.Src.Reg!) ? "rax" : Reg(s.Src.Reg!)!;
                            if (src == "rax") Load("rax", s.Src, bits == 64 ? 64 : 32);
                            E($"mov {Size(bits)} {place}, {Sub(src, bits)}");
                        }
                        break;
                    }
                    case CallI call:
                        Call(call);
                        break;
                    case ObjCallI oc:
                        if (oc.Arg != null) Load("rax", oc.Arg, 64);
                        else E($"lea rax, [{oc.Home}]");
                        E($"call {g.ObjectRoutine(oc.Kind, oc.Struct)}");
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
                        g.EmitBlockField("mov rax, qword [{0}]", "UNWIND");
                        E($"mov qword [rbp - {at}], rax");
                        E($"lea rax, [rel {methodLabel}_unwind]");
                        E($"mov qword [rbp - {at - 8}], rax");
                        E($"mov qword [rbp - {at - 16}], rbp");
                        E($"lea rax, [rbp - {at}]");
                        g.EmitBlockField("mov qword [{0}], rax", "UNWIND");
                        break;
                    }
                    case UnwindI { On: false }:
                        // off it: the record before is the one a throw looks at again
                        E($"mov rcx, qword [rbp - {fn.UnwindRecord}]");
                        MarkNow();
                        g.EmitBlockField("mov qword [{0}], rcx", "UNWIND");
                        break;
                    case RawCallI raw:
                        // (every value lives in the frame across it: the allocator saw to that)
                        E($"call {raw.Label}");
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
                            g.EmitBlockField("mov rcx, qword [{0}]", "CATCH");
                            Store(lk.Saved, "rcx");
                            g.EmitBlockField("mov qword [{0}], 0", "CATCH");
                        }
                        break;
                    }
                    case UnlockI ul:
                        if (g.Catches)
                        {
                            Load("rcx", ul.Saved, 64);
                            g.EmitBlockField("mov qword [{0}], rcx", "CATCH");
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
                        E($"lea {t}, [rel {addr.Label}]");
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
                            string src = InRegister(st.Src.Reg!) ? Reg(st.Src.Reg!)! : "rax";
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
                        E($"cmp {At(ic.Index, 64)}, {ic.Limit}");
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
                    case BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU when bits == 32 && b.B.IsImm && DivideByConstant(b):
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
                        E("jmp [r11 + rax*8]");
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
                foreach (Opnd a in call.Args)
                {
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
                E($"call {call.Label}");
                if (call.Args.Count > 0) E($"add rsp, {8 * call.Args.Count}");
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
                    case BranchT { TestMask: long mask } tb:
                        E($"test {At(tb.A)}, {Number(mask, Bits(tb.A))}");
                        if (tb.Then == next) E($"j{Cc(Not(tb.Cond))} {labels[tb.Else]}");
                        else
                        {
                            E($"j{Cc(tb.Cond)} {labels[tb.Then]}");
                            if (tb.Else != next) E($"jmp {labels[tb.Else]}");
                        }
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
                        if (ret.Value is Opnd fv && ret.Float)
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

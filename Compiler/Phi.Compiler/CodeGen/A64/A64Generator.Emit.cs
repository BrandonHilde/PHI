using System.Text.RegularExpressions;
using Phi.Compiler.CodeGen.Ir;

namespace Phi.Compiler.CodeGen.A64
{
    public sealed partial class A64Generator
    {
        /// <summary>
        /// One method's AArch64 code from its intermediate form, its values in the registers the
        /// allocator gave them (see the class's summary for the contract). Apple's assembler
        /// doesn't make an immediate or an offset fit (as NASM does), so this does: a number too
        /// big for an instruction goes in a register first (x17), an address too far from its
        /// base is worked out first (x16). An f64 is in a d register (v16-v27; d28-d31 are this
        /// emitter's own). What stops the program (an index out of range, a division by zero)
        /// is written after the method, out of the way of the code that runs.
        /// </summary>
        sealed partial class Emitter
        {
            readonly A64Generator g;
            readonly IrFunction fn;
            readonly RegisterAllocator regs;
            readonly string methodLabel;
            readonly Dictionary<Block, string> blockLabels = new();

            /// <summary>Bytes below x29: the values' slots and the kept registers, then the arguments of the biggest call.</summary>
            readonly int frame;

            /// <summary>What's written after the method: the ways out to an error, and jump tables.</summary>
            readonly List<Action> after = new();

            Syntax.Node? pendingLine;

            /// <summary>The line the code being written is for (a text error names it).</summary>
            Syntax.Node? currentLine;

            public Emitter(A64Generator generator, IrFunction function, RegisterAllocator allocation, string label)
            {
                g = generator;
                fn = function;
                regs = allocation;
                methodLabel = label;
                foreach (Block b in fn.Blocks) blockLabels[b] = g.NewLabel("b");
                int most = fn.Blocks.SelectMany(b => b.Instrs).Select(i => i switch
                {
                    CallI { Outside: true } oc => OutsideStack(oc),
                    CallI c => c.Args.Count + c.ArgKinds.Count(k => k == MemKind.V128),
                    ObjCallI or ArrayRoutineI => 2,
                    FreeTextI => 1,
                    _ => 0,
                }).DefaultIfEmpty(0).Max();
                int outgoing = Align16(8 * most);
                frame = Align16(8 * (regs.SlotCount + regs.UsedCalleeSaved.Count)) + outgoing;
            }

            static int Align16(int n) => (n + 15) & ~15;

            /// <summary>Where a kept register is saved, below x29 (after the values' slots), as x86's SavedAt.</summary>
            int SavedAt(string register) => 8 * (regs.SlotCount + 1 + regs.UsedCalleeSaved.IndexOf(register));

            void E(string instruction)
            {
                if (pendingLine != null)
                {
                    g.Line($"\t; {Line(pendingLine)}");
                    g.MarkLine(pendingLine);
                    pendingLine = null;
                }
                g.Line("\t" + instruction);
            }

            string Line(Syntax.Node node)
            {
                var at = g.File.GetLocation(node.Span.Start);
                string[] lines = g.File.Text.Split('\n');
                string text = at.Line - 1 < lines.Length ? lines[at.Line - 1].Trim() : "";
                return $"{at.Line}: {text}";
            }

            void Label(string label) => g.Line($"{label}:");

            // ---------------------------------------------------------------- the method

            public void Emit()
            {
                E("stp x29, x30, [sp, #-16]!");
                E("mov x29, sp");
                if (frame > 4096)
                {
                    // a big frame goes down a page at a time, touching each, so the guard page
                    // below the stack is what's reached when it runs out (as the x86 one's)
                    MovImm("x17", frame, true);
                    string probe = g.NewLabel("probe");
                    E("mov x16, sp");
                    Label(probe);
                    E("sub x16, x16, #4096");
                    E("str xzr, [x16]");
                    E("sub x17, x17, #4096");
                    E($"cmp x17, #4096");
                    E($"b.hi {probe}");
                    SubImm("sp", "x29", frame);
                }
                else if (frame > 0) SubImm("sp", "sp", frame);
                foreach (string r in regs.UsedCalleeSaved) Mem("str", r, "x29", -SavedAt(r), 8);
                for (int k = 0; k < fn.Blocks.Count; k++)
                {
                    Block b = fn.Blocks[k];
                    Block? next = k + 1 < fn.Blocks.Count ? fn.Blocks[k + 1] : null;
                    Label(blockLabels[b]);
                    foreach (Instr i in b.Instrs) Instruction(i);
                    End(b.End!, next);
                }
                pendingLine = null;
                for (int k = 0; k < after.Count; k++) after[k]();
                EmitUnwind();
            }

            void Epilogue()
            {
                foreach (string r in regs.UsedCalleeSaved) Mem("ldr", r, "x29", -SavedAt(r), 8);
                E("mov sp, x29");
                E("ldp x29, x30, [sp], #16");
                E("ret");
            }

            // ---------------------------------------------------------------- where values are

            string? Reg(VReg v) => regs.Registers.TryGetValue(v, out string? r) ? r : null;

            /// <summary>A register's name at a width: x for 64 bits, w for 32.</summary>
            static string W(string x, bool wide) => x is "xzr" or "wzr" ? (wide ? "xzr" : "wzr") : wide ? "x" + x[1..] : "w" + x[1..];

            /// <summary>v in a register, read from its slot into temp if it has none; at v's width unless asked.</summary>
            string Get(VReg v, string temp, bool? wide = null)
            {
                bool w = wide ?? v.Wide;
                if (Reg(v) is string r) return W(r, w);
                Mem("ldr", W(temp, w), "x29", -regs.Slots[v], w ? 8 : 4);
                return W(temp, w);
            }

            /// <summary>An operand in a register: a number made in temp (0 is the zero register).</summary>
            string GetOp(Opnd o, string temp, bool wide)
            {
                if (!o.IsImm) return Get(o.Reg!, temp, wide);
                if (o.Imm == 0) return wide ? "xzr" : "wzr";
                MovImm(temp, o.Imm, wide);
                return W(temp, wide);
            }

            /// <summary>An operand into a given register (x0 for a call, say), whatever it is.</summary>
            void Into(string reg, Opnd o, bool wide)
            {
                string s = GetOp(o, reg, wide);
                if (s != W(reg, wide)) E($"mov {W(reg, wide)}, {s}");
            }

            /// <summary>The register v's value is made in: its own, or temp (then Put writes it to its slot).</summary>
            string Target(VReg v, string temp) => W(Reg(v) ?? temp, v.Wide);

            /// <summary>v's value, made in r (from Target), kept: written to v's slot when v has no register.</summary>
            void Put(VReg v, string r)
            {
                if (Reg(v) != null) return;
                Mem("str", W(r, v.Wide), "x29", -regs.Slots[v], v.Wide ? 8 : 4);
            }

            /// <summary>An f64's d register (v16 is d16).</summary>
            static string D(string v) => "d" + v[1..];

            /// <summary>An f64 in a d register, read from its slot into temp if it has none.</summary>
            string FGet(VReg v, string temp = "d31")
            {
                if (Reg(v) is string r) return D(r);
                Mem("ldr", temp, "x29", -regs.Slots[v], 8);
                return temp;
            }

            string FTarget(VReg v, string temp = "d31") => Reg(v) is string r ? D(r) : temp;

            void FPut(VReg v, string d)
            {
                if (Reg(v) != null) return;
                Mem("str", d, "x29", -regs.Slots[v], 8);
            }

            // ---------------------------------------------------------------- numbers and addresses that fit

            /// <summary>
            /// A number into a register, 16 bits at a time: movz for the first part that isn't 0,
            /// movk for each other; or, for one that's mostly ones (a negative number), movn for the
            /// first part that isn't all ones, movk for each other.
            /// </summary>
            void MovImm(string reg, long value, bool wide)
            {
                string r = W(reg, wide);
                ulong bits = wide ? (ulong)value : (uint)value;
                int parts = wide ? 4 : 2;
                ulong Part(int k) => (bits >> (16 * k)) & 0xFFFF;
                int zeros = Enumerable.Range(0, parts).Count(k => Part(k) == 0);
                int ones = Enumerable.Range(0, parts).Count(k => Part(k) == 0xFFFF);
                bool inverted = ones > zeros;
                ulong skip = inverted ? 0xFFFFUL : 0;
                int first = Enumerable.Range(0, parts).FirstOrDefault(k => Part(k) != skip, -1);
                if (first < 0)
                {
                    E(inverted ? $"movn {r}, #0" : $"movz {r}, #0");
                    return;
                }
                E(inverted ? $"movn {r}, #{~Part(first) & 0xFFFF}, lsl #{16 * first}" : $"movz {r}, #{Part(first)}, lsl #{16 * first}");
                for (int k = first + 1; k < parts; k++)
                    if (Part(k) != skip) E($"movk {r}, #{Part(k)}, lsl #{16 * k}");
            }

            string MovTo(string reg, long value, bool wide)
            {
                MovImm(reg, value, wide);
                return W(reg, wide);
            }

            /// <summary>dst = src + n (64 bits, for addresses), however big n is.</summary>
            void AddImm(string dst, string src, long n)
            {
                if (n < 0) { SubImm(dst, src, -n); return; }
                if (n < 4096) { if (n != 0 || dst != src) E($"add {dst}, {src}, #{n}"); return; }
                if (n < 1 << 24)
                {
                    E($"add {dst}, {src}, #{n >> 12}, lsl #12");
                    if ((n & 0xFFF) != 0) E($"add {dst}, {dst}, #{n & 0xFFF}");
                    return;
                }
                MovImm("x17", n, true);
                E($"add {dst}, {src}, x17");
            }

            void SubImm(string dst, string src, long n)
            {
                if (n < 0) { AddImm(dst, src, -n); return; }
                if (n < 4096) { if (n != 0 || dst != src) E($"sub {dst}, {src}, #{n}"); return; }
                if (n < 1 << 24)
                {
                    E($"sub {dst}, {src}, #{n >> 12}, lsl #12");
                    if ((n & 0xFFF) != 0) E($"sub {dst}, {dst}, #{n & 0xFFF}");
                    return;
                }
                MovImm("x17", n, true);
                E($"sub {dst}, {src}, x17");
            }

            /// <summary>
            /// A load or store of size bytes at base + offset (op: ldr, ldrb, ldrsb, ldrh, ldrsh,
            /// str, strb, strh, with an x, w, s or d register): the scaled form where it fits, the
            /// unscaled (ldur...) for a small offset that doesn't, or the address worked out in x16.
            /// </summary>
            void Mem(string op, string reg, string baseReg, long offset, int size)
            {
                if (offset >= 0 && offset % size == 0 && offset / size < 4096)
                {
                    E(offset == 0 ? $"{op} {reg}, [{baseReg}]" : $"{op} {reg}, [{baseReg}, #{offset}]");
                    return;
                }
                if (offset >= -256 && offset <= 255)
                {
                    string unscaled = op.StartsWith("ld") ? "ldur" + op[3..] : "stur" + op[3..];
                    E($"{unscaled} {reg}, [{baseReg}, #{offset}]");
                    return;
                }
                AddImm("x16", baseReg, offset);
                E($"{op} {reg}, [x16]");
            }

            static readonly Regex FramePlace = new(@"^rbp((?:\s*[-+]\s*\d+)*)$");
            static readonly Regex GlobalPlace = new(@"^([A-Za-z_$.][\w$.<>]*)((?:\s*\+\s*\d+)*)$");
            static readonly Regex Term = new(@"([-+])\s*(\d+)");

            /// <summary>
            /// A place as the intermediate form writes it ("rbp - 8", "rbp + 24", "rbp - 40 + 8",
            /// "label", "label + 12"): its base register (x29, or temp holding the label's address)
            /// and offset. Any other shape is this emitter's mistake, and stops the build.
            /// </summary>
            (string Base, long Offset) Place(string place, string temp = "x16")
            {
                place = place.Trim();
                if (FramePlace.Match(place) is { Success: true } f) return ("x29", Sum(f.Groups[1].Value));
                if (GlobalPlace.Match(place) is { Success: true } gl)
                {
                    Address(temp, gl.Groups[1].Value);
                    return (temp, Sum(gl.Groups[2].Value));
                }
                throw new InvalidOperationException($"{methodLabel}: a place the ARM code can't read: '{place}'");
            }

            /// <summary>A place's address into a register.</summary>
            void PlaceAddress(string reg, string place)
            {
                var (b, off) = Place(place, reg);
                if (b == reg) AddImm(reg, reg, off);
                else if (off == 0) E($"mov {reg}, {b}");
                else AddImm(reg, b, off);
            }

            static long Sum(string terms) => Term.Matches(terms).Sum(m => (m.Groups[1].Value == "-" ? -1 : 1) * long.Parse(m.Groups[2].Value));

            /// <summary>A label's address into a register (position-independent: its page, then where in it).</summary>
            void Address(string reg, string label)
            {
                string s = Sym(label);
                E($"adrp {reg}, {s}@PAGE");
                E($"add {reg}, {reg}, {s}@PAGEOFF");
            }

            static (string Load, string Store, int Size) Ops(MemKind k) => k switch
            {
                MemKind.U8 => ("ldrb", "strb", 1),
                MemKind.S8 => ("ldrsb", "strb", 1),
                MemKind.U16 => ("ldrh", "strh", 2),
                MemKind.S16 => ("ldrsh", "strh", 2),
                MemKind.D32 => ("ldr", "str", 4),
                MemKind.Q64 => ("ldr", "str", 8),
                MemKind.F64 => ("ldr", "str", 8),
                MemKind.F32 => ("ldr", "str", 4),
                MemKind.F16 => ("ldr", "str", 2),
                _ => ("ldr", "str", 16),
            };

            static string CondName(Cond c) => c switch
            {
                Cond.Eq => "eq", Cond.Ne => "ne",
                Cond.LtS => "lt", Cond.LeS => "le", Cond.GtS => "gt", Cond.GeS => "ge",
                Cond.LtU => "lo", Cond.LeU => "ls", Cond.GtU => "hi", _ => "hs",
            };

            /// <summary>
            /// A comparison of f64s after fcmp: not a number (unordered) makes each false but !=,
            /// as x86's ucomisd and its jumps have it.
            /// </summary>
            static string FCondName(Cond c) => c switch
            {
                Cond.Eq => "eq", Cond.Ne => "ne",
                Cond.LtS or Cond.LtU => "mi", Cond.LeS or Cond.LeU => "ls",
                Cond.GtS or Cond.GtU => "gt", _ => "ge",
            };

            /// <summary>cmp a, b at a width (b a number that fits, or in a register).</summary>
            void Compare(string a, Opnd b, bool wide)
            {
                if (b.IsImm && b.Imm >= 0 && b.Imm < 4096) E($"cmp {a}, #{b.Imm}");
                else if (b.IsImm && b.Imm < 0 && b.Imm > -4096) E($"cmn {a}, #{-b.Imm}");
                else E($"cmp {a}, {GetOp(b, "x17", wide)}");
            }

            void WriteBack(List<StoreI> held)
            {
                foreach (StoreI s in held) Instruction(s);
            }

            // ---------------------------------------------------------------- errors

            int LineOf(Syntax.Node node) => g.File.GetLocation(node.Span.Start).Line;

            string FileLabel => g.StringLabel(Path.GetFileName(g.File.Path));

            /// <summary>
            /// A way out to an error, written after the method: the label a branch goes to when
            /// the check fails. The class variables a loop holds go back where they're kept first.
            /// </summary>
            string Stop(List<StoreI> held, Action stop)
            {
                string label = g.NewLabel("stop");
                after.Add(() =>
                {
                    Label(label);
                    WriteBack(held);
                    stop();
                });
                return label;
            }

            /// <summary>w0 = the line, x1 = the file, then the library's routine for the error (phi_index_error...).</summary>
            void ErrorAt(int line, string routine)
            {
                g.Need(routine);
                MovImm("x0", line, false);
                Address("x1", FileLabel);
                E($"b {routine}");
            }

            /// <summary>The generator's EmitStop: the error's text and kind, at a line, thrown.</summary>
            void Throw(string text, int line, int kind)
            {
                g.Need("phi_throw");
                MovImm("x0", line, false);
                Address("x1", FileLabel);
                Address("x2", g.StringLabel(text));
                MovImm("x3", kind, false);
                E("b phi_throw");
            }

            /// <summary>A label at the code here, and the way out to "division by zero" there (as x86's divide error is, from where it happened).</summary>
            string DivideError()
            {
                string here = g.NewLabel("div");
                Label(here);
                g.Need("phi_fault_entry");
                return Stop(new List<StoreI>(), () =>
                {
                    E("mov w0, #3                  ; ErrorKind.DIVIDE");
                    E($"adr x1, {here}");
                    E("b phi_fault_entry");
                });
            }

            string TextError(List<StoreI> held)
            {
                int line = currentLine != null ? LineOf(currentLine) : 0;
                return Stop(held, () => ErrorAt(line, "phi_text_error"));
            }

            // ---------------------------------------------------------------- instructions

            void Instruction(Instr i)
            {
                if (i is VecI or VecMakeI or VecGetI or VecSetI or VecReduceI or ConstI { Dst.Vec: true } or CopyI { Dst.Vec: true }
                    or LoadI { Kind: MemKind.V128 } or StoreI { Kind: MemKind.V128 } or LoadIdxI { Kind: MemKind.V128 } or StoreIdxI { Kind: MemKind.V128 })
                {
                    Vector(i);
                    return;
                }
                switch (i)
                {
                    case LineMark mark:
                        pendingLine = mark.Node;
                        currentLine = mark.Node;
                        break;

                    case ConstI { Dst.Float: true } zf:
                    {
                        string t = FTarget(zf.Dst);
                        E($"fmov {t}, xzr");
                        FPut(zf.Dst, t);
                        break;
                    }

                    case ConstI c:
                    {
                        string t = Target(c.Dst, "x9");
                        MovImm(t, c.Value, c.Dst.Wide);
                        Put(c.Dst, t);
                        break;
                    }

                    case CopyI { Dst.Float: true } fc:
                    {
                        string t = FTarget(fc.Dst);
                        string s = FGet(fc.Src.Reg!, t);
                        if (s != t) E($"fmov {t}, {s}");
                        FPut(fc.Dst, t);
                        break;
                    }

                    case CopyI c:
                    {
                        string t = Target(c.Dst, "x9");
                        string s = GetOp(c.Src, "x9", c.Dst.Wide);
                        if (s != t) E($"mov {t}, {s}");
                        Put(c.Dst, t);
                        break;
                    }

                    case BinI b:
                        Binary(b);
                        break;

                    case UnI u:
                    {
                        string t = Target(u.Dst, "x9");
                        string a = Get(u.A, "x9", u.Dst.Wide);
                        E($"{(u.Op == UnOp.Neg ? "neg" : "mvn")} {t}, {a}");
                        Put(u.Dst, t);
                        break;
                    }

                    case ExtendI x:
                    {
                        string t = Target(x.Dst, "x9");
                        string s = Get(x.Src, "x9", false);
                        E(x.Signed ? $"sxtw {W(t, true)}, {s}" : $"mov {W(t, false)}, {s}");
                        Put(x.Dst, t);
                        break;
                    }

                    case NarrowI n:
                    {
                        string t = W(Reg(n.Dst) ?? "x9", false);
                        string s = Get(n.Src, "x9", false);
                        E(n.Kind switch
                        {
                            MemKind.U8 => $"uxtb {t}, {s}",
                            MemKind.S8 => $"sxtb {t}, {s}",
                            MemKind.U16 => $"uxth {t}, {s}",
                            MemKind.S16 => $"sxth {t}, {s}",
                            _ => $"mov {t}, {s}",
                        });
                        Put(n.Dst, t);
                        break;
                    }

                    case SetCcI { Float: true } fs:
                    {
                        string a = FGet(fs.A, "d30");
                        string b = FGet(fs.B.Reg!, "d31");
                        E($"fcmp {a}, {b}");
                        string t = Target(fs.Dst, "x9");
                        E($"cset {W(t, false)}, {FCondName(fs.Cond)}");
                        Put(fs.Dst, t);
                        break;
                    }

                    case SetCcI s:
                    {
                        string a = Get(s.A, "x9");
                        Compare(a, s.B, s.A.Wide);
                        string t = Target(s.Dst, "x9");
                        E($"cset {W(t, false)}, {CondName(s.Cond)}");
                        Put(s.Dst, t);
                        break;
                    }

                    case LoadI l:
                    {
                        var (b, off) = Place(l.Place);
                        Load(l.Dst, l.Kind, b, off);
                        break;
                    }

                    case StoreI st:
                    {
                        if (st.Kind is MemKind.F64 or MemKind.F32 or MemKind.F16)
                        {
                            string d = StoreFloat(st.Src, st.Kind);
                            var (fb, fo) = Place(st.Place);
                            Mem("str", d, fb, fo, FloatSize(st.Kind));
                            break;
                        }
                        var (_, op, size) = Ops(st.Kind);
                        string s = GetOp(st.Src, "x9", size == 8);
                        var (b, off) = Place(st.Place);
                        Mem(op, s, b, off, size);
                        break;
                    }

                    case LoadIdxI l:
                    {
                        var (b, off) = Element(l.Base, l.Index, l.Scale);
                        Load(l.Dst, l.Kind, b, off);
                        break;
                    }

                    case StoreIdxI st:
                    {
                        if (st.Kind is MemKind.F64 or MemKind.F32 or MemKind.F16)
                        {
                            string d = StoreFloat(st.Src, st.Kind);
                            var (fb, fo) = Element(st.Base, st.Index, st.Scale);
                            Mem("str", d, fb, fo, FloatSize(st.Kind));
                            break;
                        }
                        var (_, op, size) = Ops(st.Kind);
                        string s = GetOp(st.Src, "x8", size == 8);
                        var (b, off) = Element(st.Base, st.Index, st.Scale);
                        Mem(op, s, b, off, size);
                        break;
                    }

                    case AddrI a:
                    {
                        string t = Target(a.Dst, "x9");
                        PlaceAddress(W(t, true), a.Label);
                        Put(a.Dst, t);
                        break;
                    }

                    case AddrOfI ao:
                    {
                        string t = Target(ao.Dst, "x9");
                        SubImm(W(t, true), "x29", regs.Slots[ao.Of]);
                        Put(ao.Dst, t);
                        break;
                    }

                    case LeaI l:
                    {
                        string t = Target(l.Dst, "x9");
                        string a = Get(l.A, "x8", l.Dst.Wide);
                        int shift = l.Factor switch { 3 => 1, 5 => 2, _ => 3 };
                        E($"add {t}, {a}, {a}, lsl #{shift}");
                        if (l.Add != 0)
                        {
                            if (l.Add > 0 && l.Add < 4096) E($"add {t}, {t}, #{l.Add}");
                            else if (l.Add < 0 && l.Add > -4096) E($"sub {t}, {t}, #{-l.Add}");
                            else
                            {
                                MovImm("x17", l.Add, l.Dst.Wide);
                                E($"add {t}, {t}, {W("x17", l.Dst.Wide)}");
                            }
                        }
                        Put(l.Dst, t);
                        break;
                    }

                    case CallI call:
                        Call(call);
                        break;

                    case PrintI p:
                        Print(p);
                        break;

                    case RoutineI r:
                        g.Need(r.Label);
                        E($"bl {r.Label}");
                        break;

                    case ExitI ex:
                        g.Need("phi_exit");
                        WriteBack(ex.Held);
                        Into("x0", ex.Code, false);
                        E("b phi_exit");
                        break;

                    case CheckI ic:
                    {
                        // as the generator's EmitIndexCheck: unsigned, so a negative index fails too
                        string ix = Get(ic.Index, "x9", true);
                        if (ic.LimitValue is VReg limit) E($"cmp {ix}, {Get(limit, "x17", true)}");
                        else Compare(ix, Opnd.Const(ic.Limit), true);
                        int line = LineOf(ic.At);
                        E($"b.hs {Stop(ic.Held, () => ErrorAt(line, "phi_index_error"))}");
                        break;
                    }

                    case SignCheckI check:
                    {
                        // as the generator's EmitNumberCheck: the top bit set stops with its error
                        string v = Get(check.Value, "x9", false);
                        int line = LineOf(check.At);
                        E($"tbnz {v}, #31, {Stop(check.Held, () => Throw(check.Text, line, ErrorConvert))}");
                        break;
                    }

                    case StopI st:
                        WriteBack(st.Held);
                        Throw(st.Text, LineOf(st.At), st.Kind);
                        break;

                    case ThrowI th:
                        g.Need("phi_throw");
                        WriteBack(th.Held);
                        Into("x2", th.Text, true);
                        MovImm("x0", LineOf(th.At), false);
                        Address("x1", FileLabel);
                        MovImm("x3", th.Kind != 0 ? th.Kind : ErrorThrown, false);
                        E("b phi_throw");
                        break;

                    case TextI t:
                        Text(t);
                        break;

                    case HasTextI ht:
                    {
                        string routine = ht.Character ? "phi_str_has_char" : "phi_str_has";
                        g.Need(routine);
                        Into("x0", ht.Text, true);
                        Into("x1", ht.Value, !ht.Character);
                        E($"bl {routine}");
                        Result(ht.Dst);
                        break;
                    }

                    case FixedStoreI fs:
                    {
                        // the generator's store into a str with a size (EmitStringStore's last part); the
                        // value first, in x1, which phi_str_end keeps (a routine may change x9-x17,
                        // where the value may be)
                        Into("x1", fs.Value, fs.SourceIsString);
                        Into("x0", fs.Dst, true);
                        MovImm("x2", fs.Room, false);
                        if (fs.Append)
                        {
                            g.Need("phi_str_end");
                            E("bl phi_str_end");
                        }
                        string convert = fs.SourceIsString ? "phi_strcpy" : fs.Unsigned ? "phi_uint_to_str" : "phi_int_to_str";
                        g.Need(convert);
                        E($"bl {convert}");
                        if (!fs.MayCut) E($"cbnz w3, {TextError(fs.Held)}");
                        break;
                    }

                    case ResultTextI { Grow: false } rt:
                    {
                        // as the generator's method end: into the caller's buffer, too long an error
                        g.Need("phi_strcpy");
                        Into("x1", rt.Value, true);
                        E("ldr x0, [x29, #16]");
                        MovImm("x2", rt.Room, false);
                        E("bl phi_strcpy");
                        E($"cbnz w3, {TextError(rt.Held)}");
                        string t = Target(rt.Dst, "x9");
                        E($"ldr {W(t, true)}, [x29, #16]");
                        Put(rt.Dst, t);
                        break;
                    }

                    case NumberTextI nt:
                        NumberText(nt);
                        break;

                    case ZeroI z:
                        PlaceAddress("x16", z.Place);
                        Zero("x16", z.Size);
                        break;

                    case CopyBytesI cb:
                        // (forward, a byte at a time when they overlap, as rep movsb copies)
                        g.Need("phi_copy_bytes");
                        Into("x0", cb.Dst, true);
                        Into("x1", cb.Src, true);
                        Into("x2", cb.Count, true);
                        E("bl phi_copy_bytes");
                        break;

                    case CompareBytesI cmb:
                        Into("x0", cmb.A, true);
                        Into("x1", cmb.B, true);
                        Into("x2", cmb.Count, true);
                        E("bl _memcmp");
                        E("cmp w0, #0");
                        E("cset w0, eq");
                        Result(cmb.Dst);
                        break;

                    case FConstI fc:
                    {
                        string t = FTarget(fc.Dst);
                        long bits = BitConverter.DoubleToInt64Bits(fc.Value);
                        if (bits == 0) E($"fmov {t}, xzr");
                        else
                        {
                            MovImm("x17", bits, true);
                            E($"fmov {t}, x17");
                        }
                        FPut(fc.Dst, t);
                        break;
                    }

                    case FBinI fb:
                    {
                        string a = FGet(fb.A, "d30");
                        string b = FGet(fb.B, "d31");
                        string t = FTarget(fb.Dst);
                        string op = fb.Op switch { FOp.Add => "fadd", FOp.Sub => "fsub", FOp.Mul => "fmul", _ => "fdiv" };
                        E($"{op} {t}, {a}, {b}");
                        FPut(fb.Dst, t);
                        break;
                    }

                    case FNegI fneg:
                    {
                        string a = FGet(fneg.A, "d30");
                        string t = FTarget(fneg.Dst);
                        E($"fneg {t}, {a}");
                        FPut(fneg.Dst, t);
                        break;
                    }

                    case X87I x:
                        FloatMath(x);
                        break;

                    case RoundF32I r:
                    {
                        string a = FGet(r.Src, "d30");
                        string t = FTarget(r.Dst);
                        E($"fcvt s31, {a}");
                        if (r.Half)
                        {
                            Half();
                            E($"fcvt {t}, h31");
                        }
                        else E($"fcvt {t}, s31");
                        FPut(r.Dst, t);
                        break;
                    }

                    case ToFloatI tf:
                    {
                        string t = FTarget(tf.Dst);
                        string s = Get(tf.Src, "x9", tf.Src.Wide);
                        E($"{(tf.Signed ? "scvtf" : "ucvtf")} {t}, {s}");
                        FPut(tf.Dst, t);
                        break;
                    }

                    case ToWholeI tw:
                    {
                        // toward zero, all 64 bits of it; one too big, or not a number, is the
                        // smallest i64, as x86's cvttsd2si makes it (fcvtzs makes the nearest it can)
                        string a = FGet(tw.Src, "d30");
                        E($"fcvtzs x9, {a}");
                        E("mov x17, #0x7FFFFFFFFFFFFFFF");
                        E("cmp x9, x17");
                        E("cinc x9, x9, eq");
                        E("mov x17, #0x8000000000000000");
                        E($"fcmp {a}, {a}");
                        E("csel x9, x17, x9, vs");
                        string t = Target(tw.Dst, "x9");
                        if (t != W("x9", tw.Dst.Wide)) E($"mov {t}, {W("x9", tw.Dst.Wide)}");
                        Put(tw.Dst, t);
                        break;
                    }

                    case LockI lk:
                    {
                        // an atomic swap until it's had (other threads may be after it too)
                        string spin = g.NewLabel("lock"), wait = g.NewLabel("lockwait"), held = g.NewLabel("locked");
                        if (lk.At != null) E($"mov x16, {Get(lk.At, "x16", true)}");
                        else Address("x16", lk.Label);
                        E("mov w17, #1");
                        Label(spin);
                        E("swpa w17, w9, [x16]");
                        E($"cbz w9, {held}");
                        Label(wait);
                        E("yield");
                        E("ldr w9, [x16]");
                        E($"cbnz w9, {wait}");
                        E($"b {spin}");
                        Label(held);
                        if (g.Catches)
                        {
                            // what the lock guards may be half changed when something goes wrong in
                            // here, so nothing is caught until it's given back: it stops the program
                            string t = Target(lk.Saved, "x9");
                            E($"ldr {W(t, true)}, [x28, #8]");
                            Put(lk.Saved, t);
                            E("str xzr, [x28, #8]");
                        }
                        break;
                    }

                    case UnlockI ul:
                        if (g.Catches) E($"str {Get(ul.Saved, "x9", true)}, [x28, #8]");
                        if (ul.At != null) E($"mov x16, {Get(ul.At, "x16", true)}");
                        else Address("x16", ul.Label);
                        E("stlr wzr, [x16]");
                        break;

                    case AtomicI at:
                        Atomic(at);
                        break;

                    case RawCallI raw:
                        // (an arm. block: every value lives in the frame across it, the allocator saw to that)
                        E($"bl {Sym(raw.Label)}");
                        if (raw.Dst != null) Result(raw.Dst);
                        break;

                    default:
                        if (!More(i)) throw new InvalidOperationException($"{methodLabel}: {KindName(i)}, which the ARM code doesn't write yet");
                        break;
                }
            }

            /// <summary>A value read from base + offset, as Kind keeps it (an f32 made an f64).</summary>
            void Load(VReg dst, MemKind kind, string b, long off)
            {
                if (kind == MemKind.F64)
                {
                    string t = FTarget(dst);
                    Mem("ldr", t, b, off, 8);
                    FPut(dst, t);
                    return;
                }
                if (kind == MemKind.F32)
                {
                    string t = FTarget(dst);
                    Mem("ldr", "s31", b, off, 4);
                    E($"fcvt {t}, s31");
                    FPut(dst, t);
                    return;
                }
                if (kind == MemKind.F16)
                {
                    // (any f16 is an f64, exactly)
                    string t = FTarget(dst);
                    Mem("ldr", "h31", b, off, 2);
                    E($"fcvt {t}, h31");
                    FPut(dst, t);
                    return;
                }
                var (op, _, size) = Ops(kind);
                string r = Target(dst, "x9");
                // (a signed byte or half into 64 bits: its sign fills the whole register)
                bool wide = size == 8 || dst.Wide && op is "ldrsb" or "ldrsh";
                Mem(op, W(r, wide), b, off, size);
                Put(dst, r);
            }

            /// <summary>An f64 to be written as an f64 (its d register) or an f32 (rounded, in s31).</summary>
            string StoreFloat(Opnd src, MemKind kind)
            {
                string d = FGet(src.Reg!, "d31");
                if (kind == MemKind.F64) return d;
                E($"fcvt s31, {d}");
                if (kind != MemKind.F16) return "s31";
                Half();
                return "h31";
            }

            /// <summary>The f32 in s31 made an f16, in h31, as x86 rounds one (lib/arm64_macos/half.s; w0-w3 changed).</summary>
            void Half()
            {
                g.Need("phi_f32_to_f16");
                E("fmov w0, s31");
                E("bl phi_f32_to_f16");
                E("fmov s31, w0");
            }

            static int FloatSize(MemKind kind) => kind switch { MemKind.F64 => 8, MemKind.F32 => 4, _ => 2 };

            /// <summary>The result of a routine (in w0 or x0) into a value.</summary>
            void Result(VReg dst)
            {
                string t = Target(dst, "x9");
                if (t != W("x0", dst.Wide)) E($"mov {t}, {W("x0", dst.Wide)}");
                Put(dst, t);
            }

            /// <summary>size bytes made zero from the address in reg (which changes).</summary>
            void Zero(string reg, int size)
            {
                int k = 0;
                if (size >= 256)
                {
                    // 64 bytes a time, then what's left
                    string loop = g.NewLabel("zero");
                    MovImm("x17", size / 64, true);
                    Label(loop);
                    E($"stp xzr, xzr, [{reg}]");
                    E($"stp xzr, xzr, [{reg}, #16]");
                    E($"stp xzr, xzr, [{reg}, #32]");
                    E($"stp xzr, xzr, [{reg}, #48]");
                    E($"add {reg}, {reg}, #64");
                    E("subs x17, x17, #1");
                    E($"b.ne {loop}");
                    size %= 64;
                }
                for (; k + 16 <= size; k += 16) E($"stp xzr, xzr, [{reg}, #{k}]");
                for (; k + 8 <= size; k += 8) E($"str xzr, [{reg}, #{k}]");
                for (; k + 4 <= size; k += 4) E($"str wzr, [{reg}, #{k}]");
                for (; k < size; k++) E($"strb wzr, [{reg}, #{k}]");
            }

            /// <summary>
            /// The FPU's own (X87I) as libm and the FPU here have it: fsqrt, fabs and frndint's four
            /// roundings are one instruction; sin and cos are libm's, but a number too big for
            /// fsin and fcos (2^63 or more) is itself, as they leave it; and what's left of A over
            /// B with A's sign (fprem until it's done) is fmod's.
            /// </summary>
            void FloatMath(X87I x)
            {
                string a = FGet(x.A, "d30");
                string t = FTarget(x.Dst);
                if (x.B != null)
                {
                    string b = FGet(x.B, "d31");
                    E($"fmov d0, {a}");
                    E($"fmov d1, {b}");
                    E("bl _fmod");
                    if (t != "d0") E($"fmov {t}, d0");
                    FPut(x.Dst, t);
                    return;
                }
                switch (x.Instruction)
                {
                    case "fsqrt": E($"fsqrt {t}, {a}"); break;
                    case "fabs": E($"fabs {t}, {a}"); break;
                    case "frndint":
                        E($"{x.Rounding switch { 0 => "frintn", 1 => "frintm", 2 => "frintp", _ => "frintz" }} {t}, {a}");
                        break;
                    case "fsin" or "fcos":
                    {
                        string big = g.NewLabel("fbig");
                        E($"fmov d0, {a}");
                        E("fabs d1, d0");
                        E("mov x17, #0x43E0000000000000   ; 2^63");
                        E("fmov d2, x17");
                        E("fcmp d1, d2");
                        E($"b.ge {big}");
                        E(x.Instruction == "fsin" ? "bl _sin" : "bl _cos");
                        Label(big);
                        if (t != "d0") E($"fmov {t}, d0");
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"{methodLabel}: the FPU's {x.Instruction}, which the ARM code doesn't write yet");
                }
                FPut(x.Dst, t);
            }

            /// <summary>One step no other core comes between (ARMv8.1's atomics, which every Apple CPU has).</summary>
            void Atomic(AtomicI at)
            {
                string sfx = at.Bytes switch { 1 => "b", 2 => "h", _ => "" };
                bool wide = at.Bytes == 8;
                string addr = Get(at.Address, "x16", true);
                if (at.Op == Syntax.AtomicOp.CompareSwap)
                {
                    string expected = W("x9", wide), value = W("x8", wide);
                    Into("x9", at.Expected, wide);
                    Into("x8", at.Value, wide);
                    E($"mov {W("x17", wide)}, {expected}");
                    E($"casal{sfx} {expected}, {value}, [{addr}]");
                    if (sfx != "")
                    {
                        E($"and w17, w17, #{(at.Bytes == 1 ? 0xFF : 0xFFFF)}");
                        E($"and w9, w9, #{(at.Bytes == 1 ? 0xFF : 0xFFFF)}");
                    }
                    E($"cmp {expected}, {W("x17", wide)}");
                    string t = Target(at.Dst!, "x9");
                    E($"cset {W(t, false)}, eq");
                    Put(at.Dst!, t);
                    return;
                }
                Into("x9", at.Value, wide);
                string v = W("x9", wide);
                switch (at.Op)
                {
                    case Syntax.AtomicOp.Add: E($"ldaddal{sfx} {v}, {W("x17", wide)}, [{addr}]"); break;
                    case Syntax.AtomicOp.Subtract:
                        E($"neg {v}, {v}");
                        E($"ldaddal{sfx} {v}, {W("x17", wide)}, [{addr}]");
                        break;
                    default: E($"swpal{sfx} {v}, {W("x17", wide)}, [{addr}]"); break;
                }
            }

            /// <summary>An element's place: the base's register and an offset (a known index), or x16 worked out.</summary>
            (string Base, long Offset) Element(VReg baseValue, Opnd index, int scale)
            {
                string b = Get(baseValue, "x16", true);
                if (index.IsImm) return (b, index.Imm * scale);
                string ix = Get(index.Reg!, "x17", true);
                if (scale is 1 or 2 or 4 or 8 or 16)
                {
                    int shift = scale switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };
                    E(shift == 0 ? $"add x16, {b}, {ix}" : $"add x16, {b}, {ix}, lsl #{shift}");
                }
                else
                {
                    MovImm("x16", scale, true);
                    E($"madd x16, {ix}, x16, {b}");
                }
                return ("x16", 0);
            }

            void Binary(BinI b)
            {
                bool wide = b.Dst.Wide;
                string t = Target(b.Dst, "x9");
                // (a number first is made in a register, 0 too: the zero register can't be an add's first operand)
                string a = b.A.IsImm ? MovTo("x9", b.A.Imm, wide) : Get(b.A.Reg!, "x9", wide);
                int bits = wide ? 64 : 32;
                switch (b.Op)
                {
                    case BinOp.Add or BinOp.Sub when b.B.IsImm && b.B.Imm > -4096 && b.B.Imm < 4096:
                    {
                        long n = b.Op == BinOp.Sub ? -b.B.Imm : b.B.Imm;
                        E(n >= 0 ? $"add {t}, {a}, #{n}" : $"sub {t}, {a}, #{-n}");
                        break;
                    }
                    case BinOp.Shl or BinOp.Shr or BinOp.Sar when b.B.IsImm:
                    {
                        long count = b.B.Imm & (bits - 1);
                        string op = b.Op switch { BinOp.Shl => "lsl", BinOp.Shr => "lsr", _ => "asr" };
                        E($"{op} {t}, {a}, #{count}");
                        break;
                    }
                    case BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU:
                    {
                        // x86's divide error, made here: dividing by zero, or (signed) the smallest
                        // number by -1, is "division by zero"; ARM's divide would give an answer
                        bool signed = b.Op is BinOp.DivS or BinOp.RemS;
                        if (b.B.IsImm && b.B.Imm == 0)
                        {
                            E($"b {DivideError()}");
                            break;
                        }
                        string d = GetOp(b.B, "x8", wide);
                        if (!b.B.IsImm) E($"cbz {d}, {DivideError()}");
                        if (signed && (!b.B.IsImm || b.B.Imm == -1))
                        {
                            // (a - 1 overflows only for the smallest number)
                            string ok = g.NewLabel("divok");
                            if (!b.B.IsImm)
                            {
                                E($"cmn {d}, #1");
                                E($"b.ne {ok}");
                            }
                            E($"cmp {a}, #1");
                            E($"b.vs {DivideError()}");
                            Label(ok);
                        }
                        string div = signed ? "sdiv" : "udiv";
                        if (b.Op is BinOp.DivS or BinOp.DivU) E($"{div} {t}, {a}, {d}");
                        else
                        {
                            string q = W("x17", wide);
                            E($"{div} {q}, {a}, {d}");
                            E($"msub {t}, {q}, {d}, {a}");
                        }
                        break;
                    }
                    default:
                    {
                        string bb = GetOp(b.B, "x8", wide);
                        string op = b.Op switch
                        {
                            BinOp.Add => "add", BinOp.Sub => "sub", BinOp.Mul => "mul",
                            BinOp.And => "and", BinOp.Or => "orr", BinOp.Xor => "eor",
                            BinOp.Shl => "lsl", BinOp.Shr => "lsr", _ => "asr",
                        };
                        E($"{op} {t}, {a}, {bb}");
                        break;
                    }
                }
                Put(b.Dst, t);
            }

            void Call(CallI call)
            {
                if (call.Outside)
                {
                    OutsideCall(call);
                    return;
                }
                // the arguments into the bottom of the frame, the last at sp (as x86's pushes
                // leave them): 8 bytes each, a vec's 16; an f32 in the low half of its 8
                int n = call.Args.Count;
                var sizes = Enumerable.Range(0, n).Select(k => KindAt(call, k) == MemKind.V128 ? 16 : 8).ToList();
                int at = sizes.Sum();
                for (int k = 0; k < n; k++)
                {
                    at -= sizes[k];
                    Opnd a = call.Args[k];
                    MemKind kind = KindAt(call, k);
                    if (kind == MemKind.V128 || !a.IsImm && a.Reg!.Vec)
                    {
                        VecArgument(a, at);
                        continue;
                    }
                    if (kind is MemKind.F64 or MemKind.F32 or MemKind.F16 || !a.IsImm && a.Reg!.Float)
                    {
                        MemKind fk = kind is MemKind.F32 or MemKind.F16 ? kind : MemKind.F64;
                        string d = StoreFloat(a, fk);
                        Mem("str", d, "sp", at, FloatSize(fk));
                        continue;
                    }
                    string r = GetOp(a, "x9", true);
                    Mem("str", r, "sp", at, 8);
                }
                if (call.Target != null)
                {
                    string target = Get(call.Target, "x16", true);
                    E($"blr {target}");
                }
                else
                {
                    if (call.Routine) g.Need(call.Label);
                    E($"bl {Sym(call.Label)}");
                }
                if (call.Dst == null) return;
                if (call.Result == CallResult.St0)
                {
                    // an f64 comes back in d0
                    string ft = FTarget(call.Dst);
                    if (ft != "d0") E($"fmov {ft}, d0");
                    FPut(call.Dst, ft);
                    return;
                }
                if (call.Result == CallResult.Xmm0)
                {
                    VecResult(call.Dst);
                    return;
                }
                // (a result in x0: w0 for 32 bits, a 64-bit number or a pointer whole)
                string t = Target(call.Dst, "x9");
                string from = W("x0", call.Dst.Wide && call.Result != CallResult.Eax);
                if (call.Dst.Wide && call.Result == CallResult.Eax) E($"mov {W(t, false)}, w0");
                else if (t != from) E($"mov {t}, {from}");
                Put(call.Dst, t);
            }

            static MemKind KindAt(CallI call, int k) => k < call.ArgKinds.Count ? call.ArgKinds[k] : MemKind.Q64;

            void Print(PrintI p)
            {
                string suffix = p.Serial ? "_serial" : "";
                string routine;
                switch (p.Kind)
                {
                    case PrintKind.Text:
                        if (p.Literal != null) Address("x0", g.StringLabel(p.Literal));
                        else if (p.Label != null) Address("x0", p.Label);
                        else Into("x0", p.Value, true);
                        routine = "phi_print";
                        break;
                    case PrintKind.Int or PrintKind.Uint or PrintKind.Char:
                        Into("x0", p.Value, false);
                        routine = p.Kind switch { PrintKind.Int => "phi_print_int", PrintKind.Uint => "phi_print_uint", _ => "phi_print_char" };
                        break;
                    case PrintKind.Int64 or PrintKind.Uint64:
                        Into("x0", p.Value, true);
                        routine = p.Kind == PrintKind.Int64 ? "phi_print_int64" : "phi_print_uint64";
                        break;
                    case PrintKind.Float:
                    {
                        string d = FGet(p.Value.Reg!, "d0");
                        if (d != "d0") E($"fmov d0, {d}");
                        routine = "phi_print_float";
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"{methodLabel}: a log of a {p.Kind}, which the ARM code doesn't write yet");
                }
                g.Need(routine + suffix);
                E($"bl {routine}{suffix}");
            }

            /// <summary>A number written out as text into the scratch bytes (fn.Scratch: 48 of them), as Print does it.</summary>
            void NumberText(NumberTextI nt)
            {
                switch (nt.Kind)
                {
                    case PrintKind.Char:
                        Into("x9", nt.Value, false);
                        PlaceAddress("x0", fn.Scratch!);
                        E("strb w9, [x0]");
                        E("strb wzr, [x0, #1]");
                        break;
                    case PrintKind.Int64 or PrintKind.Uint64 or PrintKind.Int or PrintKind.Uint:
                    {
                        bool wide = nt.Kind is PrintKind.Int64 or PrintKind.Uint64;
                        string convert = nt.Kind switch
                        {
                            PrintKind.Int64 => "phi_int64_to_str", PrintKind.Uint64 => "phi_uint64_to_str",
                            PrintKind.Int => "phi_int_to_str", _ => "phi_uint_to_str",
                        };
                        g.Need(convert);
                        Into("x1", nt.Value, wide);
                        PlaceAddress("x0", fn.Scratch!);
                        E("mov w2, #24");
                        E($"bl {convert}");
                        break;
                    }
                    default:
                    {
                        g.Need("phi_float_to_str");
                        string d = FGet(nt.Value.Reg!, "d0");
                        if (d != "d0") E($"fmov d0, {d}");
                        PlaceAddress("x0", fn.Scratch!);
                        E("mov w2, #48");
                        E("bl phi_float_to_str");
                        break;
                    }
                }
                string t = Target(nt.Dst, "x9");
                PlaceAddress(W(t, true), fn.Scratch!);
                Put(nt.Dst, t);
            }

            void Text(TextI t)
            {
                switch (t.Op)
                {
                    case TextOp.Literal:
                    {
                        string r = Target(t.Dst!, "x9");
                        Address(W(r, true), g.StringLabel(t.Literal!));
                        Put(t.Dst!, r);
                        return;
                    }
                    case TextOp.OrEmpty:
                    {
                        // A, or '' when A is 0
                        string r = Target(t.Dst!, "x9");
                        string a = Get(t.A!, "x8", true);
                        Address("x16", g.StringLabel(""));
                        E($"cmp {a}, #0");
                        E($"csel {W(r, true)}, {a}, x16, ne");
                        Put(t.Dst!, r);
                        return;
                    }
                    case TextOp.Copy:
                    {
                        g.Need("phi_strcpy");
                        Into("x1", t.A!, true);
                        PlaceAddress("x0", t.Place!);
                        MovImm("x2", t.Room, false);
                        E("bl phi_strcpy");
                        E($"cbnz w3, {TextError(t.Held)}");
                        return;
                    }
                    case TextOp.Length:
                        g.Need("phi_strlen");
                        Into("x0", t.A!, true);
                        E("bl phi_strlen");
                        Result(t.Dst!);
                        return;
                    case TextOp.Equal or TextOp.Order:
                    {
                        string routine = t.Op == TextOp.Equal ? "phi_strcmp" : "phi_strorder";
                        g.Need(routine);
                        Into("x0", t.A!, true);
                        Into("x1", t.B!, true);
                        E($"bl {routine}");
                        Result(t.Dst!);
                        return;
                    }
                }
            }

            // ---------------------------------------------------------------- the ends of blocks

            void End(Terminator t, Block? next)
            {
                switch (t)
                {
                    case JumpT j:
                        if (j.Target != next) E($"b {blockLabels[j.Target]}");
                        break;

                    case BranchT { Float: true } fbr:
                    {
                        string a = FGet(fbr.A, "d30");
                        string b = FGet(fbr.B.Reg!, "d31");
                        E($"fcmp {a}, {b}");
                        // (not a number: each but != fails, so it goes to Else)
                        E($"b.{FCondName(fbr.Cond)} {blockLabels[fbr.Then]}");
                        if (fbr.Else != next) E($"b {blockLabels[fbr.Else]}");
                        break;
                    }

                    case BranchT br:
                    {
                        string a = Get(br.A, "x9");
                        if (br.TestMask is long mask)
                        {
                            MovImm("x17", mask, br.A.Wide);
                            E($"tst {a}, {W("x17", br.A.Wide)}");
                        }
                        else Compare(a, br.B, br.A.Wide);
                        string cond = CondName(br.Cond);
                        if (br.Then == next)
                        {
                            E($"b.{Invert(cond)} {blockLabels[br.Else]}");
                        }
                        else
                        {
                            E($"b.{cond} {blockLabels[br.Then]}");
                            if (br.Else != next) E($"b {blockLabels[br.Else]}");
                        }
                        break;
                    }

                    case SwitchT sw:
                        Switch(sw, next);
                        break;

                    case TryT tr:
                        if (tr.Body != next) E($"b {blockLabels[tr.Body]}");
                        break;

                    case RetT r:
                        if (r.Value is Opnd fv && r.Float)
                        {
                            // an f64 is given back in d0
                            string d = FGet(fv.Reg!, "d0");
                            if (d != "d0") E($"fmov d0, {d}");
                        }
                        else if (r.Value is Opnd vv && r.Vec) VecReturn(vv);
                        else if (r.Value is Opnd v) Into("x0", v, r.Wide || r.Pointer);
                        Epilogue();
                        break;

                    default:
                        throw new InvalidOperationException($"{methodLabel}: {KindName(t)}, which the ARM code doesn't write yet");
                }
            }

            /// <summary>
            /// when: with 4 or more cases close together (no more than three places for each case),
            /// a table of where each value jumps (as x86's), indexed by the value less the smallest;
            /// otherwise a compare and jump for each case.
            /// </summary>
            void Switch(SwitchT sw, Block? next)
            {
                bool wide = sw.Value.Wide;
                string v = Get(sw.Value, "x9");
                if (sw.Cases.Count >= 4)
                {
                    long low = sw.Cases.Min(c => c.Value), high = sw.Cases.Max(c => c.Value);
                    long span = high - low + 1;
                    if (span <= 3 * sw.Cases.Count && span <= 1024 && low >= int.MinValue && low <= int.MaxValue)
                    {
                        string table = g.NewLabel("table");
                        var targets = new List<string>();
                        for (long k = low; k <= high; k++)
                            targets.Add(blockLabels[sw.Cases.FirstOrDefault(c => c.Value == k).Target ?? sw.Default]);
                        string i = W("x9", wide);
                        if (low > 0 && low < 4096) E($"sub {i}, {v}, #{low}");
                        else if (low < 0 && low > -4096) E($"add {i}, {v}, #{-low}");
                        else if (low != 0) E($"sub {i}, {v}, {MovTo("x17", low, wide)}");
                        else if (i != v) E($"mov {i}, {v}");
                        Compare(i, Opnd.Const(span - 1), wide);
                        E($"b.hi {blockLabels[sw.Default]}");
                        if (!wide) E("mov w9, w9");
                        E($"adr x16, {table}");
                        E("ldrsw x17, [x16, x9, lsl #2]");
                        E("add x16, x16, x17");
                        E("br x16");
                        after.Add(() =>
                        {
                            g.Line("\t.p2align 2");
                            Label(table);
                            foreach (string to in targets) g.Line($"\t.long {to} - {table}");
                        });
                        return;
                    }
                }
                foreach (var (value, target) in sw.Cases)
                {
                    Compare(v, Opnd.Const(value), wide);
                    E($"b.eq {blockLabels[target]}");
                }
                if (sw.Default != next) E($"b {blockLabels[sw.Default]}");
            }

            static string Invert(string cond) => cond switch
            {
                "eq" => "ne", "ne" => "eq", "lt" => "ge", "ge" => "lt", "le" => "gt", "gt" => "le",
                "lo" => "hs", "hs" => "lo", "ls" => "hi", _ => "ls",
            };
        }
    }
}

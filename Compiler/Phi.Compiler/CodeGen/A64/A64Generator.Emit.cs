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
        /// base is worked out first (x16).
        /// </summary>
        sealed class Emitter
        {
            readonly A64Generator g;
            readonly IrFunction fn;
            readonly RegisterAllocator regs;
            readonly string methodLabel;
            readonly Dictionary<Block, string> blockLabels = new();

            /// <summary>Bytes below x29: the values' slots and the kept registers, then the arguments of the biggest call.</summary>
            readonly int frame;

            /// <summary>The bytes at sp where a call's arguments are written (the biggest call's).</summary>
            readonly int outgoing;

            Syntax.Node? pendingLine;

            public Emitter(A64Generator generator, IrFunction function, RegisterAllocator allocation, string label)
            {
                g = generator;
                fn = function;
                regs = allocation;
                methodLabel = label;
                foreach (Block b in fn.Blocks) blockLabels[b] = g.NewLabel("b");
                int most = fn.Blocks.SelectMany(b => b.Instrs).OfType<CallI>().Select(c => c.Args.Count).DefaultIfEmpty(0).Max();
                outgoing = Align16(8 * most);
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
            static string W(string x, bool wide) => wide ? x : "w" + x[1..];

            /// <summary>v in a register, read from its slot into temp if it has none; at v's width unless asked.</summary>
            string Get(VReg v, string temp, bool? wide = null)
            {
                bool w = wide ?? v.Wide;
                if (Reg(v) is string r) return W(r, w);
                Mem(w ? "ldr" : "ldr", W(temp, w), "x29", -regs.Slots[v], w ? 8 : 4);
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

            /// <summary>The register v's value is made in: its own, or temp (then Put writes it to its slot).</summary>
            string Target(VReg v, string temp) => W(Reg(v) ?? temp, v.Wide);

            /// <summary>v's value, made in r (from Target), kept: written to v's slot when v has no register.</summary>
            void Put(VReg v, string r)
            {
                if (Reg(v) != null) return;
                Mem("str", W(r, v.Wide), "x29", -regs.Slots[v], v.Wide ? 8 : 4);
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
            /// str, strb, strh): the scaled form where it fits, the unscaled (ldur...) for a small
            /// offset that doesn't, or the address worked out in x16 first.
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
            /// "label", "label + 12"): its base register (x29, or x16 holding the label's address)
            /// and offset. Any other shape is this emitter's mistake, and stops the build.
            /// </summary>
            (string Base, long Offset) Place(string place)
            {
                place = place.Trim();
                if (FramePlace.Match(place) is { Success: true } f) return ("x29", Sum(f.Groups[1].Value));
                if (GlobalPlace.Match(place) is { Success: true } gl)
                {
                    Address("x16", gl.Groups[1].Value);
                    return ("x16", Sum(gl.Groups[2].Value));
                }
                throw new InvalidOperationException($"{methodLabel}: a place the ARM code can't read: '{place}'");
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
                _ => throw new InvalidOperationException($"a {k} in memory, which the ARM code doesn't take yet"),
            };

            static string CondName(Cond c) => c switch
            {
                Cond.Eq => "eq", Cond.Ne => "ne",
                Cond.LtS => "lt", Cond.LeS => "le", Cond.GtS => "gt", Cond.GeS => "ge",
                Cond.LtU => "lo", Cond.LeU => "ls", Cond.GtU => "hi", _ => "hs",
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

            // ---------------------------------------------------------------- instructions

            void Instruction(Instr i)
            {
                switch (i)
                {
                    case LineMark mark:
                        pendingLine = mark.Node;
                        break;

                    case ConstI c:
                    {
                        string t = Target(c.Dst, "x9");
                        MovImm(t, c.Value, c.Dst.Wide);
                        Put(c.Dst, t);
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
                        var (op, _, size) = Ops(l.Kind);
                        var (b, off) = Place(l.Place);
                        string t = Target(l.Dst, "x9");
                        Mem(op, size == 8 ? W(t, true) : W(t, false), b, off, size);
                        Put(l.Dst, t);
                        break;
                    }

                    case StoreI st:
                    {
                        var (_, op, size) = Ops(st.Kind);
                        string s = GetOp(st.Src, "x9", size == 8);
                        var (b, off) = Place(st.Place);
                        Mem(op, s, b, off, size);
                        break;
                    }

                    case LoadIdxI l:
                    {
                        var (op, _, size) = Ops(l.Kind);
                        var (b, off) = Element(l.Base, l.Index, l.Scale);
                        string t = Target(l.Dst, "x9");
                        Mem(op, size == 8 ? W(t, true) : W(t, false), b, off, size);
                        Put(l.Dst, t);
                        break;
                    }

                    case StoreIdxI st:
                    {
                        var (_, op, size) = Ops(st.Kind);
                        string s = GetOp(st.Src, "x8", size == 8);
                        var (b, off) = Element(st.Base, st.Index, st.Scale);
                        Mem(op, s, b, off, size);
                        break;
                    }

                    case AddrI a:
                    {
                        string t = Target(a.Dst, "x9");
                        if (GlobalPlace.Match(a.Label.Trim()) is not { Success: true } gl)
                            throw new InvalidOperationException($"{methodLabel}: an address the ARM code can't read: '{a.Label}'");
                        Address(W(t, true), gl.Groups[1].Value);
                        AddImm(W(t, true), W(t, true), Sum(gl.Groups[2].Value));
                        Put(a.Dst, t);
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
                        {
                            string c = GetOp(ex.Code, "x9", false);
                            if (c != "w0") E($"mov w0, {c}");
                        }
                        E("b phi_exit");
                        break;

                    case TextI { Op: TextOp.Literal } t:
                    {
                        string r = Target(t.Dst!, "x9");
                        Address(W(r, true), g.StringLabel(t.Literal!));
                        Put(t.Dst!, r);
                        break;
                    }

                    case TextI { Op: TextOp.OrEmpty } t:
                    {
                        // A, or '' when A is 0
                        string r = Target(t.Dst!, "x9");
                        string a = Get(t.A!, "x8", true);
                        Address("x16", g.StringLabel(""));
                        E($"cmp {a}, #0");
                        E($"csel {W(r, true)}, {a}, x16, ne");
                        Put(t.Dst!, r);
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"{methodLabel}: {KindName(i)}, which the ARM code doesn't write yet");
                }
            }

            /// <summary>An element's place: the base's register and an offset (a known index), or x16 worked out.</summary>
            (string Base, long Offset) Element(VReg baseValue, Opnd index, int scale)
            {
                string b = Get(baseValue, "x16", true);
                if (index.IsImm) return (b, index.Imm * scale);
                string ix = Get(index.Reg!, "x17", true);
                if (scale is 1 or 2 or 4 or 8)
                {
                    int shift = scale switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };
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
                        // (a divisor known when it's built, not 0 or -1: Unsupported takes no other yet)
                        string d = GetOp(b.B, "x8", wide);
                        string div = b.Op is BinOp.DivS or BinOp.RemS ? "sdiv" : "udiv";
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
                // the arguments into the bottom of the frame, the last at sp (as x86's pushes leave them)
                int n = call.Args.Count;
                for (int k = 0; k < n; k++)
                {
                    string r = GetOp(call.Args[k], "x9", true);
                    Mem("str", r, "sp", 8 * (n - 1 - k), 8);
                }
                if (call.Target != null)
                {
                    string target = Get(call.Target, "x16", true);
                    E($"blr {target}");
                }
                else E($"bl {Sym(call.Label)}");
                if (call.Dst == null) return;
                // (a result in x0: w0 for 32 bits, a 64-bit number or a pointer whole)
                string t = Target(call.Dst, "x9");
                string from = W("x0", call.Dst.Wide && call.Result != CallResult.Eax);
                if (call.Dst.Wide && call.Result == CallResult.Eax) E($"mov {W(t, false)}, w0");
                else if (t != from) E($"mov {t}, {from}");
                Put(call.Dst, t);
            }

            void Print(PrintI p)
            {
                string suffix = p.Serial ? "_serial" : "";
                switch (p.Kind)
                {
                    case PrintKind.Text:
                        if (p.Literal != null) Address("x0", g.StringLabel(p.Literal));
                        else if (p.Label != null) Address("x0", p.Label);
                        else
                        {
                            string r = GetOp(p.Value, "x0", true);
                            if (r != "x0") E($"mov x0, {r}");
                        }
                        g.Need("phi_print" + suffix);
                        E($"bl phi_print{suffix}");
                        return;
                    case PrintKind.Int or PrintKind.Uint or PrintKind.Char:
                    {
                        string r = GetOp(p.Value, "x0", false);
                        if (r != "w0") E($"mov w0, {r}");
                        string routine = (p.Kind switch { PrintKind.Int => "phi_print_int", PrintKind.Uint => "phi_print_uint", _ => "phi_print_char" }) + suffix;
                        g.Need(routine);
                        E($"bl {routine}");
                        return;
                    }
                    default:
                        throw new InvalidOperationException($"{methodLabel}: a log of a {p.Kind}, which the ARM code doesn't write yet");
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
                    {
                        string value0 = Get(sw.Value, "x9");
                        foreach (var (value, target) in sw.Cases)
                        {
                            Compare(value0, Opnd.Const(value), sw.Value.Wide);
                            E($"b.eq {blockLabels[target]}");
                        }
                        if (sw.Default != next) E($"b {blockLabels[sw.Default]}");
                        break;
                    }

                    case RetT r:
                        if (r.Value is Opnd v)
                        {
                            bool wide = r.Wide || r.Pointer;
                            string s = GetOp(v, "x0", wide);
                            if (s != W("x0", wide)) E($"mov {W("x0", wide)}, {s}");
                        }
                        Epilogue();
                        break;

                    default:
                        throw new InvalidOperationException($"{methodLabel}: {KindName(t)}, which the ARM code doesn't write yet");
                }
            }

            static string Invert(string cond) => cond switch
            {
                "eq" => "ne", "ne" => "eq", "lt" => "ge", "ge" => "lt", "le" => "gt", "gt" => "le",
                "lo" => "hs", "hs" => "lo", "ls" => "hi", _ => "ls",
            };
        }
    }
}

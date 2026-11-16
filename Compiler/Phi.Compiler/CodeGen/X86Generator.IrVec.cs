using Phi.Compiler.CodeGen.Ir;
using Phi.Compiler.Semantics;

namespace Phi.Compiler.CodeGen
{
    // vec<T>'s code, written from the intermediate form: SSE2, which every x86-64 CPU has
    // (Plan.md: the user's choice; --x86-64-v2 may later use the one-instruction forms of
    // what's done here in several). An operation SSE2 has no instructions for at all (a
    // 64-bit lane's multiply, min, max and comparisons; some conversions) is worked out a
    // lane at a time through the method's scratch bytes. xmm12 to xmm15 are this code's own.
    public sealed partial class X86Generator
    {
        sealed partial class IrEmitter
        {
            // ---------------------------------------------------------------- where vecs are

            string VSlot(VReg v) => $"[rbp - {regs.Slots[v]}]";

            /// <summary>Built for x86-64-v2 (SSSE3, SSE4.1, SSE4.2): their one-instruction forms.</summary>
            static bool V2 => X8664V2;

            /// <summary>The register a vec is worked out in: the destination's own, or xmm15.</summary>
            string VTarget(VReg dst) => InRegister(dst) ? Reg(dst)! : "xmm15";

            /// <summary>v in a register: its own, or (from the frame) the scratch one given.</summary>
            string VIn(VReg v, string scratch)
            {
                if (InRegister(v)) return Reg(v)!;
                E($"movdqu {scratch}, {VSlot(v)}");
                return scratch;
            }

            void VLoad(string xmm, VReg v)
            {
                if (Reg(v) == xmm) return;
                E(InRegister(v) ? $"movdqa {xmm}, {Reg(v)}" : $"movdqu {xmm}, {VSlot(v)}");
            }

            void VStore(VReg dst, string xmm)
            {
                if (Reg(dst) == xmm) return;
                E(InRegister(dst) ? $"movdqa {Reg(dst)}, {xmm}" : $"movdqu {VSlot(dst)}, {xmm}");
            }

            void VMove(VReg dst, VReg src)
            {
                if (dst == src || Reg(dst) != null && Reg(dst) == Reg(src)) return;
                if (InRegister(dst)) VLoad(Reg(dst)!, src);
                else E($"movdqu {VSlot(dst)}, {VIn(src, "xmm15")}");
            }

            /// <summary>One of the method's scratch bytes (see IrFunction.NeedScratch).</summary>
            string S(int offset) => $"[{fn.Scratch} + {offset}]";

            static int LaneBytes(TypeKind k) => k switch
            {
                TypeKind.I8 or TypeKind.U8 => 1,
                TypeKind.I16 or TypeKind.U16 => 2,
                TypeKind.I32 or TypeKind.U32 or TypeKind.F32 => 4,
                _ => 8,
            };

            static bool LaneSigned(TypeKind k) => k is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64;

            static bool LaneFloat(TypeKind k) => k is TypeKind.F32 or TypeKind.F64;

            static string Suffix(int bytes) => bytes switch { 1 => "b", 2 => "w", 4 => "d", _ => "q" };

            /// <summary>16 bytes made of one lane's bytes again and again, in the program's data (aligned, so an instruction can read it).</summary>
            string Splat(long value, int bytes)
            {
                byte[] one = BitConverter.GetBytes(value);
                var all = new byte[PhiType.VecBytes];
                for (int k = 0; k < all.Length; k++) all[k] = one[k % bytes];
                return $"[rel {g.VecLabel(all)}]";
            }

            /// <summary>The bit at the top of each lane: whole numbers xored with it compare as signed ones.</summary>
            string SignBits(int bytes) => Splat(bytes == 8 ? long.MinValue : 1L << (8 * bytes - 1), bytes);

            /// <summary>A lane read from memory into r64 as 64 bits, with its sign or zeros.</summary>
            void LaneInto(string r64, string place, TypeKind k)
            {
                int bytes = LaneBytes(k);
                bool signed = LaneSigned(k);
                E(bytes switch
                {
                    1 => signed ? $"movsx {r64}, byte {place}" : $"movzx {Sub(r64, 32)}, byte {place}",
                    2 => signed ? $"movsx {r64}, word {place}" : $"movzx {Sub(r64, 32)}, word {place}",
                    4 => signed ? $"movsxd {r64}, dword {place}" : $"mov {Sub(r64, 32)}, dword {place}",
                    _ => $"mov {r64}, qword {place}",
                });
            }

            void LaneFrom(string place, string r64, int bytes) => E($"mov {Size(8 * bytes)} {place}, {Sub(r64, 8 * bytes)}");

            // ---------------------------------------------------------------- the instructions

            void Vector(Instr i)
            {
                switch (i)
                {
                    case VecMakeI m: Make(m); break;
                    case VecGetI get: Get(get); break;
                    case VecSetI set: Set(set); break;
                    case VecReduceI r: Reduce(r); break;
                    case VecI v: Op(v); break;
                    case RoundF32I r:
                    {
                        string t = FTarget(r.Dst);
                        E($"cvtsd2ss xmm15, {F(r.Src)}");
                        E($"cvtss2sd {t}, xmm15");
                        FStore(r.Dst, t);
                        break;
                    }
                    case PrintI p: Print(p); break;
                    case RoutineI r:
                        g.Need(r.Label);
                        E($"call {r.Label}");
                        break;
                }
            }

            void Make(VecMakeI m)
            {
                string t = VTarget(m.Dst);
                int bytes = LaneBytes(m.Lane);
                if (m.Bytes != null)
                {
                    if (m.Bytes.All(b => b == 0)) E($"pxor {t}, {t}");
                    else E($"movdqa {t}, [rel {g.VecLabel(m.Bytes)}]");
                }
                else if (m.Lanes.Count == 0) E($"pxor {t}, {t}");
                else if (m.Lanes.Count == 1)
                {
                    // one value in every lane
                    Opnd one = m.Lanes[0];
                    switch (m.Lane)
                    {
                        case TypeKind.F64:
                            E(InRegister(one.Reg!) ? $"movaps {t}, {Reg(one.Reg!)}" : $"movsd {t}, {F(one.Reg!)}");
                            E($"movlhps {t}, {t}");
                            break;
                        case TypeKind.F32:
                            E($"cvtsd2ss {t}, {F(one.Reg!)}");
                            E($"shufps {t}, {t}, 0");
                            break;
                        default:
                            Load("rax", one, bytes == 8 ? 64 : 32);
                            if (bytes == 8)
                            {
                                E($"movq {t}, rax");
                                E($"punpcklqdq {t}, {t}");
                                break;
                            }
                            E($"movd {t}, eax");
                            if (bytes == 1) E($"punpcklbw {t}, {t}");
                            if (bytes <= 2) E($"pshuflw {t}, {t}, 0");
                            E($"pshufd {t}, {t}, 0");
                            break;
                    }
                }
                else
                {
                    // each lane where it goes in the scratch bytes, then all 16 at once
                    for (int k = 0; k < m.Lanes.Count; k++)
                    {
                        Opnd lane = m.Lanes[k];
                        if (m.Lane == TypeKind.F64)
                        {
                            if (InRegister(lane.Reg!)) E($"movsd qword {S(8 * k)}, {Reg(lane.Reg!)}");
                            else
                            {
                                E($"mov rax, {F(lane.Reg!)}");
                                E($"mov qword {S(8 * k)}, rax");
                            }
                        }
                        else if (m.Lane == TypeKind.F32)
                        {
                            E($"cvtsd2ss xmm14, {F(lane.Reg!)}");
                            E($"movss dword {S(4 * k)}, xmm14");
                        }
                        else
                        {
                            Load("rax", lane, bytes == 8 ? 64 : 32);
                            LaneFrom(S(bytes * k), "rax", bytes);
                        }
                    }
                    E($"movdqu {t}, {S(0)}");
                }
                VStore(m.Dst, t);
            }

            /// <summary>Where lane Index of the vec in the scratch bytes is (its index in rcx, if it isn't a number).</summary>
            string LanePlace(Opnd index, int bytes)
            {
                if (index.IsImm) return S((int)index.Imm * bytes);
                E($"mov rcx, {At(index.Reg!, 64)}");
                return $"[{fn.Scratch} + rcx{(bytes > 1 ? $"*{bytes}" : "")}]";
            }

            void Get(VecGetI get)
            {
                int bytes = LaneBytes(get.Lane);
                if (get.Index.IsImm && !LaneFloat(get.Lane) && (V2 || bytes == 2))
                {
                    // one instruction (pextrw is SSE2's; the others SSE4.1's)
                    string r = InRegister(get.Dst) ? Reg(get.Dst)! : "rax";
                    string a = VIn(get.A, "xmm15");
                    E(bytes == 8 ? $"pextrq {r}, {a}, {get.Index.Imm}" : $"pextr{Suffix(bytes)} {Sub(r, 32)}, {a}, {get.Index.Imm}");
                    if (LaneSigned(get.Lane) && bytes < 4) E($"movsx {Sub(r, 32)}, {Sub(r, 8 * bytes)}");
                    Store(get.Dst, r);
                    return;
                }
                E($"movdqu {S(0)}, {VIn(get.A, "xmm15")}");
                string place = LanePlace(get.Index, bytes);
                if (LaneFloat(get.Lane))
                {
                    string ft = FTarget(get.Dst);
                    E(get.Lane == TypeKind.F64 ? $"movsd {ft}, qword {place}" : $"cvtss2sd {ft}, dword {place}");
                    FStore(get.Dst, ft);
                    return;
                }
                string t = InRegister(get.Dst) ? Reg(get.Dst)! : "rax";
                LaneInto(t, place, get.Lane);
                // (a lane of up to 32 bits is kept as 32, with the top half clear)
                if (bytes < 8 && LaneSigned(get.Lane)) E($"mov {Sub(t, 32)}, {Sub(t, 32)}");
                Store(get.Dst, t);
            }

            void Set(VecSetI set)
            {
                int bytes = LaneBytes(set.Lane);
                if (set.Index.IsImm && !LaneFloat(set.Lane) && (V2 || bytes == 2))
                {
                    string x = VTarget(set.Dst);
                    VLoad(x, set.A);
                    Load("rax", set.Value, bytes == 8 ? 64 : 32);
                    E(bytes == 8 ? $"pinsrq {x}, rax, {set.Index.Imm}" : $"pinsr{Suffix(bytes)} {x}, eax, {set.Index.Imm}");
                    VStore(set.Dst, x);
                    return;
                }
                E($"movdqu {S(0)}, {VIn(set.A, "xmm15")}");
                string place = LanePlace(set.Index, bytes);
                if (set.Lane == TypeKind.F64)
                {
                    E($"movsd xmm14, {F(set.Value.Reg!)}");
                    E($"movsd qword {place}, xmm14");
                }
                else if (set.Lane == TypeKind.F32)
                {
                    E($"cvtsd2ss xmm14, {F(set.Value.Reg!)}");
                    E($"movss dword {place}, xmm14");
                }
                else
                {
                    Load("rax", set.Value, bytes == 8 ? 64 : 32);
                    E($"mov {Size(8 * bytes)} {place}, {Sub("rax", 8 * bytes)}");
                }
                string t = VTarget(set.Dst);
                E($"movdqu {t}, {S(0)}");
                VStore(set.Dst, t);
            }

            void Reduce(VecReduceI r)
            {
                TypeKind k = r.Lane;
                int bytes = LaneBytes(k), lanes = PhiType.VecBytes / bytes;
                switch (r.Op)
                {
                    case VReduce.Same:
                    {
                        VLoad("xmm14", r.A);
                        string b = VIn(r.B!, "xmm13");
                        // floats compare as numbers (0 is -0; nan is never the same); whole numbers by their bytes
                        int all = k switch { TypeKind.F32 => 0xF, TypeKind.F64 => 0x3, _ => 0xFFFF };
                        E(k switch
                        {
                            TypeKind.F32 => $"cmpeqps xmm14, {b}",
                            TypeKind.F64 => $"cmpeqpd xmm14, {b}",
                            _ => $"pcmpeqb xmm14, {b}",
                        });
                        E(k switch { TypeKind.F32 => "movmskps eax, xmm14", TypeKind.F64 => "movmskpd eax, xmm14", _ => "pmovmskb eax, xmm14" });
                        E($"cmp eax, {all}");
                        E("sete al");
                        E("movzx eax, al");
                        Store(r.Dst, "rax");
                        return;
                    }
                    case VReduce.Any or VReduce.All:
                    {
                        // a mask's lanes are all ones or all zeros: the top bit of each says which
                        string a = VIn(r.A, "xmm14");
                        int mask = bytes switch { 1 => 0xFFFF, 2 => 0xAAAA, 4 => 0xF, _ => 0x3 };
                        E(bytes switch { 4 => $"movmskps eax, {a}", 8 => $"movmskpd eax, {a}", _ => $"pmovmskb eax, {a}" });
                        if (r.Op == VReduce.Any)
                        {
                            E($"test eax, {mask}");
                            E("setnz al");
                        }
                        else
                        {
                            E($"and eax, {mask}");
                            E($"cmp eax, {mask}");
                            E("sete al");
                        }
                        E("movzx eax, al");
                        Store(r.Dst, "rax");
                        return;
                    }
                }
                // the sum, the smallest or the largest: lane by lane, in order
                E($"movdqu {S(0)}, {VIn(r.A, "xmm15")}");
                if (LaneFloat(k))
                {
                    // (an f32's lanes as f64s, as the x87 adds f32s; rounded where it's kept)
                    string op = r.Op switch { VReduce.Sum => "addsd", VReduce.Smallest => "minsd", _ => "maxsd" };
                    E(k == TypeKind.F64 ? $"movsd xmm14, qword {S(0)}" : $"cvtss2sd xmm14, dword {S(0)}");
                    for (int n = 1; n < lanes; n++)
                    {
                        if (k == TypeKind.F64) E($"{op} xmm14, qword {S(8 * n)}");
                        else
                        {
                            E($"cvtss2sd xmm13, dword {S(4 * n)}");
                            E($"{op} xmm14, xmm13");
                        }
                    }
                    FStore(r.Dst, "xmm14");
                    return;
                }
                bool signed = LaneSigned(k);
                LaneInto("rax", S(0), k);
                for (int n = 1; n < lanes; n++)
                {
                    LaneInto("rcx", S(bytes * n), k);
                    switch (r.Op)
                    {
                        case VReduce.Sum: E("add rax, rcx"); break;
                        case VReduce.Smallest:
                            E("cmp rax, rcx");
                            E($"{(signed ? "cmovg" : "cmova")} rax, rcx");
                            break;
                        default:
                            E("cmp rax, rcx");
                            E($"{(signed ? "cmovl" : "cmovb")} rax, rcx");
                            break;
                    }
                }
                // (a sum is a lane's kind, wrapping as its numbers do)
                if (bytes == 1) E(signed ? "movsx eax, al" : "movzx eax, al");
                else if (bytes == 2) E(signed ? "movsx eax, ax" : "movzx eax, ax");
                else if (bytes == 4) E("mov eax, eax");
                Store(r.Dst, "rax");
            }

            // ---------------------------------------------------------------- lane by lane

            /// <summary>t: where an operation's result is worked out, with A put in it first (the destination's own register, unless B or C is in it).</summary>
            string Start(VecI v)
            {
                bool clash = InRegister(v.Dst) && (v.B != null && Reg(v.B) == Reg(v.Dst) || v.C != null && Reg(v.C) == Reg(v.Dst));
                string t = InRegister(v.Dst) && !clash ? Reg(v.Dst)! : "xmm15";
                VLoad(t, v.A);
                return t;
            }

            /// <summary>
            /// a and b put in the scratch bytes (at 0 and 16), each lane worked out by lane(k)
            /// into the result (at 32), and that into the destination: what SSE2 can't do at once.
            /// </summary>
            void PerLane(VecI v, int lanes, Action<int> lane)
            {
                E($"movdqu {S(0)}, {VIn(v.A, "xmm15")}");
                if (v.B != null) E($"movdqu {S(16)}, {VIn(v.B, "xmm15")}");
                for (int k = 0; k < lanes; k++) lane(k);
                string t = VTarget(v.Dst);
                E($"movdqu {t}, {S(32)}");
                VStore(v.Dst, t);
            }

            void Op(VecI v)
            {
                TypeKind k = v.Lane;
                int bytes = LaneBytes(k), lanes = PhiType.VecBytes / bytes;
                bool isFloat = LaneFloat(k), signed = LaneSigned(k);
                string sfx = Suffix(bytes), fs = k == TypeKind.F32 ? "ps" : "pd";
                switch (v.Op)
                {
                    case VOp.Mul when isFloat || bytes == 2:
                    case VOp.Add or VOp.Sub or VOp.Div or VOp.And or VOp.Or or VOp.Xor or VOp.AddSat or VOp.SubSat or VOp.Average:
                    {
                        string t = Start(v);
                        string b = VIn(v.B!, "xmm14");
                        string op = v.Op switch
                        {
                            VOp.Add => isFloat ? "add" + fs : "padd" + sfx,
                            VOp.Sub => isFloat ? "sub" + fs : "psub" + sfx,
                            VOp.Mul => isFloat ? "mul" + fs : "pmullw",
                            VOp.Div => "div" + fs,
                            VOp.And => "pand",
                            VOp.Or => "por",
                            VOp.Xor => "pxor",
                            VOp.AddSat => (signed ? "padds" : "paddus") + sfx,
                            VOp.SubSat => (signed ? "psubs" : "psubus") + sfx,
                            _ => "pavg" + sfx,
                        };
                        E($"{op} {t}, {b}");
                        VStore(v.Dst, t);
                        return;
                    }
                    case VOp.Mul when bytes == 4 && V2:
                    {
                        string t = Start(v);
                        E($"pmulld {t}, {VIn(v.B!, "xmm14")}");
                        VStore(v.Dst, t);
                        return;
                    }
                    case VOp.Mul when bytes == 4:
                    {
                        // two pmuludqs (lanes 0 and 2, then 1 and 3), their low halves put back together
                        string t = Start(v);
                        string b = VIn(v.B!, "xmm14");
                        E($"movdqa xmm13, {t}");
                        E($"pmuludq {t}, {b}");
                        E("psrlq xmm13, 32");
                        E($"movdqa xmm12, {b}");
                        E("psrlq xmm12, 32");
                        E("pmuludq xmm13, xmm12");
                        E($"pshufd {t}, {t}, 0x08");
                        E("pshufd xmm13, xmm13, 0x08");
                        E($"punpckldq {t}, xmm13");
                        VStore(v.Dst, t);
                        return;
                    }
                    case VOp.Mul when bytes == 1:
                    {
                        // the even bytes' and the odd bytes' products as 16 bits each, their low bytes kept
                        string t = Start(v);
                        string b = VIn(v.B!, "xmm14");
                        E($"movdqa xmm13, {t}");
                        E($"movdqa xmm12, {b}");
                        E($"pmullw {t}, {b}");
                        E("psrlw xmm13, 8");
                        E("psrlw xmm12, 8");
                        E("pmullw xmm13, xmm12");
                        E("psllw xmm13, 8");
                        E($"pand {t}, {Splat(0x00FF, 2)}");
                        E($"por {t}, xmm13");
                        VStore(v.Dst, t);
                        return;
                    }
                    case VOp.Mul:
                        PerLane(v, lanes, n =>
                        {
                            E($"mov rax, qword {S(8 * n)}");
                            E($"imul rax, qword {S(16 + 8 * n)}");
                            E($"mov qword {S(32 + 8 * n)}, rax");
                        });
                        return;
                    case VOp.Not:
                    {
                        string t = Start(v);
                        E("pcmpeqd xmm14, xmm14");
                        E($"pxor {t}, xmm14");
                        VStore(v.Dst, t);
                        return;
                    }
                    case VOp.Neg when isFloat:
                    {
                        string t = Start(v);
                        E($"xorps {t}, {SignBits(bytes)}");
                        VStore(v.Dst, t);
                        return;
                    }
                    case VOp.Neg:
                    {
                        string a = VIn(v.A, "xmm13");
                        E("pxor xmm14, xmm14");
                        E($"psub{sfx} xmm14, {a}");
                        VStore(v.Dst, "xmm14");
                        return;
                    }
                    case VOp.Sqrt:
                    {
                        string t = VTarget(v.Dst);
                        E($"sqrt{fs} {t}, {VIn(v.A, "xmm14")}");
                        VStore(v.Dst, t);
                        return;
                    }
                    case VOp.Shl or VOp.Shr:
                        Shift(v, bytes, signed, lanes);
                        return;
                    case VOp.Min or VOp.Max:
                        MinMax(v, k, bytes, lanes);
                        return;
                    case VOp.Abs:
                        Abs(v, k, bytes, lanes);
                        return;
                    case VOp.Less or VOp.Greater or VOp.Equal:
                        Compare(v, k, bytes, lanes);
                        return;
                    case VOp.Pick:
                    {
                        VLoad("xmm13", v.A);
                        E($"movdqa xmm12, {VIn(v.B!, "xmm14")}");
                        E("pand xmm12, xmm13");
                        E($"pandn xmm13, {VIn(v.C!, "xmm14")}");
                        E("por xmm13, xmm12");
                        VStore(v.Dst, "xmm13");
                        return;
                    }
                    case VOp.Widen or VOp.WidenHigh:
                        Widen(v, k, bytes);
                        return;
                    case VOp.Narrow:
                        Narrow(v, k, bytes);
                        return;
                    case VOp.Convert:
                        ConvertLanes(v);
                        return;
                }
                throw new InvalidOperationException($"a vec's {v.Op} on {k} lanes");
            }

            /// <summary>
            /// v &lt;&lt; n and v &gt;&gt; n, every lane by the same count (one of a lane's width or
            /// more leaves 0, or the sign in each bit for a signed &gt;&gt;, as the instructions do).
            /// </summary>
            void Shift(VecI v, int bytes, bool signed, int lanes)
            {
                bool right = v.Op == VOp.Shr;
                long? known = v.Count.IsImm ? Math.Clamp(v.Count.Imm, 0, 255) : null;
                if (bytes == 8 && right && signed)
                {
                    // (no psraq in SSE2: each lane, its count no more than 63)
                    Load("rcx", v.Count, 32);
                    E("mov edx, 63");
                    E("cmp ecx, 63");
                    E("cmova ecx, edx");
                    PerLane(v, lanes, n =>
                    {
                        E($"mov rax, qword {S(8 * n)}");
                        E("sar rax, cl");
                        E($"mov qword {S(32 + 8 * n)}, rax");
                    });
                    return;
                }
                string t = Start(v);
                string count = known?.ToString() ?? "xmm14";
                if (known == null)
                {
                    Load("rax", v.Count, 32);
                    E("movd xmm14, eax");
                }
                if (bytes > 1)
                {
                    E($"{(right ? signed ? "psra" : "psrl" : "psll")}{Suffix(bytes)} {t}, {count}");
                    VStore(v.Dst, t);
                    return;
                }
                if (right && signed)
                {
                    // bytes: each made a word with its sign, shifted, and packed back
                    E($"movdqa xmm13, {t}");
                    E("punpcklbw xmm13, xmm13");
                    E($"punpckhbw {t}, {t}");
                    E("psraw xmm13, 8");
                    E($"psraw {t}, 8");
                    E($"psraw xmm13, {count}");
                    E($"psraw {t}, {count}");
                    E($"packsswb xmm13, {t}");
                    VStore(v.Dst, "xmm13");
                    return;
                }
                // bytes: shifted as words, with the bits that crossed from the next byte cleared
                E($"{(right ? "psrlw" : "psllw")} {t}, {count}");
                if (known != null)
                {
                    long keep = known >= 8 ? 0 : right ? 0xFF >> (int)known : (0xFF << (int)known) & 0xFF;
                    E($"pand {t}, {Splat(keep, 1)}");
                }
                else
                {
                    Load("rcx", v.Count, 32);
                    E("mov eax, 0xFF");
                    E($"{(right ? "shr" : "shl")} eax, cl");
                    E("and eax, 0xFF");
                    E("imul eax, eax, 0x01010101");
                    E("movd xmm13, eax");
                    E("pshufd xmm13, xmm13, 0");
                    E($"pand {t}, xmm13");
                }
                VStore(v.Dst, t);
            }

            void MinMax(VecI v, TypeKind k, int bytes, int lanes)
            {
                bool min = v.Op == VOp.Min, signed = LaneSigned(k);
                if (LaneFloat(k) || k is TypeKind.U8 or TypeKind.I16)
                {
                    string t = Start(v);
                    string b = VIn(v.B!, "xmm14");
                    string op = LaneFloat(k) ? (min ? "min" : "max") + (k == TypeKind.F32 ? "ps" : "pd")
                        : k == TypeKind.U8 ? (min ? "pminub" : "pmaxub") : (min ? "pminsw" : "pmaxsw");
                    E($"{op} {t}, {b}");
                    VStore(v.Dst, t);
                    return;
                }
                if (V2 && bytes <= 4)
                {
                    string t = Start(v);
                    string op = (min ? "pmin" : "pmax") + (signed ? "s" : "u") + Suffix(bytes);
                    E($"{op} {t}, {VIn(v.B!, "xmm14")}");
                    VStore(v.Dst, t);
                    return;
                }
                if (k is TypeKind.I8 or TypeKind.U16)
                {
                    // the other's instruction, on numbers with their top bits turned over
                    string t = Start(v);
                    string b = VIn(v.B!, "xmm14");
                    string flip = SignBits(bytes);
                    E($"pxor {t}, {flip}");
                    E($"movdqa xmm13, {b}");
                    E($"pxor xmm13, {flip}");
                    E(k == TypeKind.I8 ? $"{(min ? "pminub" : "pmaxub")} {t}, xmm13" : $"{(min ? "pminsw" : "pmaxsw")} {t}, xmm13");
                    E($"pxor {t}, {flip}");
                    VStore(v.Dst, t);
                    return;
                }
                if (bytes == 4 || V2)
                {
                    // a > b as a mask, then each lane picked from a or b by it (64 bits: SSE4.2's pcmpgtq)
                    string t = Start(v);
                    string b = VIn(v.B!, "xmm14");
                    E($"movdqa xmm13, {t}");
                    E($"movdqa xmm12, {b}");
                    if (!signed)
                    {
                        E($"pxor xmm13, {SignBits(bytes)}");
                        E($"pxor xmm12, {SignBits(bytes)}");
                    }
                    E($"pcmpgt{Suffix(bytes)} xmm13, xmm12");
                    E($"movdqa xmm12, {(min ? b : t)}");
                    E("pand xmm12, xmm13");
                    E($"pandn xmm13, {(min ? t : b)}");
                    E("por xmm13, xmm12");
                    VStore(v.Dst, "xmm13");
                    return;
                }
                PerLane(v, lanes, n =>
                {
                    E($"mov rax, qword {S(8 * n)}");
                    E($"mov rdx, qword {S(16 + 8 * n)}");
                    E("cmp rax, rdx");
                    E($"{(min ? signed ? "cmovg" : "cmova" : signed ? "cmovl" : "cmovb")} rax, rdx");
                    E($"mov qword {S(32 + 8 * n)}, rax");
                });
            }

            void Abs(VecI v, TypeKind k, int bytes, int lanes)
            {
                if (LaneFloat(k))
                {
                    string t = Start(v);
                    E($"pand {t}, {Splat(bytes == 8 ? long.MaxValue : int.MaxValue, bytes)}");
                    VStore(v.Dst, t);
                    return;
                }
                if (bytes == 8)
                {
                    PerLane(v, lanes, n =>
                    {
                        E($"mov rax, qword {S(8 * n)}");
                        E("mov rdx, rax");
                        E("sar rdx, 63");
                        E("xor rax, rdx");
                        E("sub rax, rdx");
                        E($"mov qword {S(32 + 8 * n)}, rax");
                    });
                    return;
                }
                if (V2)
                {
                    string t = VTarget(v.Dst);
                    E($"pabs{Suffix(bytes)} {t}, {VIn(v.A, "xmm14")}");
                    VStore(v.Dst, t);
                    return;
                }
                // (a ^ sign) - sign, with sign each lane's top bit spread over it
                string tt = Start(v);
                if (bytes == 1)
                {
                    E("pxor xmm13, xmm13");
                    E($"pcmpgtb xmm13, {tt}");
                }
                else
                {
                    E($"movdqa xmm13, {tt}");
                    E($"psra{Suffix(bytes)} xmm13, {8 * bytes - 1}");
                }
                E($"pxor {tt}, xmm13");
                E($"psub{Suffix(bytes)} {tt}, xmm13");
                VStore(v.Dst, tt);
            }

            void Compare(VecI v, TypeKind k, int bytes, int lanes)
            {
                string fs = k == TypeKind.F32 ? "ps" : "pd";
                if (LaneFloat(k))
                {
                    string t = Start(v);
                    string b = VIn(v.B!, "xmm14");
                    if (v.Op == VOp.Greater)
                    {
                        // a > b is b < a
                        E($"movdqa xmm13, {b}");
                        E($"cmplt{fs} xmm13, {t}");
                        VStore(v.Dst, "xmm13");
                        return;
                    }
                    E($"{(v.Op == VOp.Equal ? "cmpeq" : "cmplt")}{fs} {t}, {b}");
                    VStore(v.Dst, t);
                    return;
                }
                if (v.Op == VOp.Equal)
                {
                    string t = Start(v);
                    string b = VIn(v.B!, "xmm14");
                    if (bytes < 8 || V2) E($"pcmpeq{Suffix(bytes)} {t}, {b}");
                    else
                    {
                        // both halves of each 64-bit lane the same
                        E($"pcmpeqd {t}, {b}");
                        E($"pshufd xmm13, {t}, 0xB1");
                        E($"pand {t}, xmm13");
                    }
                    VStore(v.Dst, t);
                    return;
                }
                bool signed = LaneSigned(k), less = v.Op == VOp.Less;
                if (bytes == 8 && !V2)
                {
                    PerLane(v, lanes, n =>
                    {
                        E($"mov rax, qword {S(8 * n)}");
                        E($"cmp rax, qword {S(16 + 8 * n)}");
                        E($"set{(less ? signed ? "l" : "b" : signed ? "g" : "a")} al");
                        E("movzx eax, al");
                        E("neg rax");
                        E($"mov qword {S(32 + 8 * n)}, rax");
                    });
                    return;
                }
                {
                    // a > b (or b > a for less), signed; unsigned ones with their top bits turned over
                    string t = Start(v);
                    string b = VIn(v.B!, "xmm14");
                    E($"movdqa xmm13, {b}");
                    if (!signed)
                    {
                        E($"pxor {t}, {SignBits(bytes)}");
                        E($"pxor xmm13, {SignBits(bytes)}");
                    }
                    if (less)
                    {
                        E($"pcmpgt{Suffix(bytes)} xmm13, {t}");
                        VStore(v.Dst, "xmm13");
                    }
                    else
                    {
                        E($"pcmpgt{Suffix(bytes)} {t}, xmm13");
                        VStore(v.Dst, t);
                    }
                }
            }

            void Widen(VecI v, TypeKind k, int bytes)
            {
                bool high = v.Op == VOp.WidenHigh;
                if (k == TypeKind.F32)
                {
                    string a = VIn(v.A, "xmm14");
                    string t = VTarget(v.Dst);
                    if (high)
                    {
                        E($"movhlps xmm14, {a}");
                        a = "xmm14";
                    }
                    E($"cvtps2pd {t}, {a}");
                    VStore(v.Dst, t);
                    return;
                }
                if (V2 && !high)
                {
                    string t = VTarget(v.Dst);
                    string unpacked = bytes switch { 1 => "bw", 2 => "wd", _ => "dq" };
                    E($"pmov{(LaneSigned(k) ? "sx" : "zx")}{unpacked} {t}, {VIn(v.A, "xmm14")}");
                    VStore(v.Dst, t);
                    return;
                }
                // each lane next to what goes above it: zeros, or its sign spread over them
                string tt = Start(v);
                E("pxor xmm13, xmm13");
                if (LaneSigned(k)) E($"pcmpgt{Suffix(bytes)} xmm13, {tt}");
                string unpack = bytes switch { 1 => "bw", 2 => "wd", _ => "dq" };
                E($"punpck{(high ? "h" : "l")}{unpack} {tt}, xmm13");
                VStore(v.Dst, tt);
            }

            void Narrow(VecI v, TypeKind k, int bytes)
            {
                switch (k)
                {
                    case TypeKind.F64:
                    {
                        bool clash = InRegister(v.Dst) && Reg(v.B!) == Reg(v.Dst);
                        string t = InRegister(v.Dst) && !clash ? Reg(v.Dst)! : "xmm15";
                        E($"cvtpd2ps {t}, {VIn(v.A, "xmm13")}");
                        E($"cvtpd2ps xmm14, {VIn(v.B!, "xmm14")}");
                        E($"movlhps {t}, xmm14");
                        VStore(v.Dst, t);
                        return;
                    }
                    case TypeKind.I16 or TypeKind.I32:
                    {
                        string t = Start(v);
                        E($"{(k == TypeKind.I16 ? "packsswb" : "packssdw")} {t}, {VIn(v.B!, "xmm14")}");
                        VStore(v.Dst, t);
                        return;
                    }
                    case TypeKind.U16:
                    {
                        // each made no more than 255 (x less what's over 255), then packed
                        string t = Start(v);
                        string b = VIn(v.B!, "xmm14");
                        string most = Splat(255, 2);
                        E($"movdqa xmm13, {t}");
                        E($"psubusw xmm13, {most}");
                        E($"psubw {t}, xmm13");
                        E($"movdqa xmm13, {b}");
                        E($"psubusw xmm13, {most}");
                        E($"movdqa xmm12, {b}");
                        E("psubw xmm12, xmm13");
                        E($"packuswb {t}, xmm12");
                        VStore(v.Dst, t);
                        return;
                    }
                }
                if (k == TypeKind.U32 && V2)
                {
                    // each made no more than 65535, then SSE4.1's packusdw
                    string t = Start(v);
                    string most = Splat(0xFFFF, 4);
                    E($"pminud {t}, {most}");
                    E($"movdqa xmm12, {VIn(v.B!, "xmm14")}");
                    E($"pminud xmm12, {most}");
                    E($"packusdw {t}, xmm12");
                    VStore(v.Dst, t);
                    return;
                }
                // u32 to u16, and 64 bits to 32: each lane, stopping at the ends
                int half = PhiType.VecBytes / bytes;
                PerLane(v, 2 * half, n =>
                {
                    string from = S((n < half ? 0 : 16) + bytes * (n % half));
                    if (k == TypeKind.U32)
                    {
                        E($"mov eax, dword {from}");
                        E("mov edx, 0xFFFF");
                        E("cmp eax, edx");
                        E("cmova eax, edx");
                        E($"mov word {S(32 + 2 * n)}, ax");
                        return;
                    }
                    E($"mov rax, qword {from}");
                    if (k == TypeKind.U64)
                    {
                        E("mov edx, 0xFFFFFFFF");
                        E("cmp rax, rdx");
                        E("cmova rax, rdx");
                    }
                    else
                    {
                        E("mov rdx, 0x7FFFFFFF");
                        E("cmp rax, rdx");
                        E("cmovg rax, rdx");
                        E("mov rdx, -0x80000000");
                        E("cmp rax, rdx");
                        E("cmovl rax, rdx");
                    }
                    E($"mov dword {S(32 + 4 * n)}, eax");
                });
            }

            /// <summary>v as vec&lt;T&gt; between floats and whole numbers: each lane's value, toward zero.</summary>
            void ConvertLanes(VecI v)
            {
                switch (v.Lane, v.To)
                {
                    case (TypeKind.I32, TypeKind.F32):
                    case (TypeKind.F32, TypeKind.I32):
                    {
                        string t = VTarget(v.Dst);
                        E($"{(v.To == TypeKind.F32 ? "cvtdq2ps" : "cvttps2dq")} {t}, {VIn(v.A, "xmm14")}");
                        VStore(v.Dst, t);
                        return;
                    }
                }
                int bytes = LaneBytes(v.Lane), lanes = PhiType.VecBytes / bytes;
                PerLane(v, lanes, n =>
                {
                    string from = S(bytes * n), to = S(32 + bytes * n);
                    switch (v.Lane, v.To)
                    {
                        case (TypeKind.U32, TypeKind.F32):
                            E($"mov eax, dword {from}");
                            E("cvtsi2ss xmm13, rax");
                            E($"movss dword {to}, xmm13");
                            break;
                        case (TypeKind.F32, TypeKind.U32):
                            E($"cvttss2si rax, dword {from}");
                            E($"mov dword {to}, eax");
                            break;
                        case (TypeKind.I64, TypeKind.F64):
                            E($"cvtsi2sd xmm13, qword {from}");
                            E($"movsd qword {to}, xmm13");
                            break;
                        case (TypeKind.U64, TypeKind.F64):
                        {
                            // (one with its top bit set is halved, keeping the bit that rounds, and doubled)
                            string big = g.NewLabel("u64big"), done = g.NewLabel("u64done");
                            E($"mov rax, qword {from}");
                            E("test rax, rax");
                            E($"js {big}");
                            E("cvtsi2sd xmm13, rax");
                            E($"jmp {done}");
                            g.Label(big);
                            E("mov rcx, rax");
                            E("shr rcx, 1");
                            E("and eax, 1");
                            E("or rcx, rax");
                            E("cvtsi2sd xmm13, rcx");
                            E("addsd xmm13, xmm13");
                            g.Label(done);
                            E($"movsd qword {to}, xmm13");
                            break;
                        }
                        default:
                            // f64 to i64 or u64
                            E($"cvttsd2si rax, qword {from}");
                            E($"mov qword {to}, rax");
                            break;
                    }
                });
            }

            // ---------------------------------------------------------------- log

            void Print(PrintI p)
            {
                string suffix = p.Serial ? "_serial" : "";
                string print = "phi_print" + suffix;
                switch (p.Kind)
                {
                    case PrintKind.Text:
                        if (p.Literal != null) E($"lea rsi, [rel {g.StringLabel(p.Literal)}]");
                        else if (p.Label != null) E($"lea rsi, [rel {p.Label}]");
                        else Load("rsi", p.Value, 64);
                        break;
                    case PrintKind.Int or PrintKind.Uint:
                    {
                        string number = (p.Kind == PrintKind.Uint ? "phi_print_uint" : "phi_print_int") + suffix;
                        g.Need(number);
                        Load("rax", p.Value, 32);
                        E($"call {number}");
                        return;
                    }
                    case PrintKind.Char:
                        Load("rax", p.Value, 32);
                        E($"mov byte {S(0)}, al");
                        E($"mov byte {S(1)}, 0");
                        E($"lea rsi, {S(0)}");
                        break;
                    case PrintKind.Int64 or PrintKind.Uint64:
                    {
                        string convert = p.Kind == PrintKind.Uint64 ? "phi_uint64_to_str" : "phi_int64_to_str";
                        g.Need(convert);
                        Load("rax", p.Value, 64);
                        E("mov rdx, rax");
                        E("shr rdx, 32");
                        E($"lea rdi, {S(0)}");
                        E("mov ecx, 24");
                        E($"call {convert}");
                        E($"lea rsi, {S(0)}");
                        break;
                    }
                    case PrintKind.Float:
                    {
                        g.Need("phi_float_to_str");
                        VReg f = p.Value.Reg!;
                        if (InRegister(f))
                        {
                            E("sub rsp, 8");
                            E($"movsd qword [rsp], {Reg(f)}");
                            E("fld qword [rsp]");
                            E("add rsp, 8");
                        }
                        else E($"fld {F(f)}");
                        E($"lea rdi, {S(0)}");
                        E("mov ecx, 48");
                        E("call phi_float_to_str");
                        E($"lea rsi, {S(0)}");
                        break;
                    }
                }
                g.Need(print);
                E($"call {print}");
            }
        }
    }
}

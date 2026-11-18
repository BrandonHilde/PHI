using Phi.Compiler.CodeGen.Ir;
using Phi.Compiler.Semantics;

namespace Phi.Compiler.CodeGen.A64
{
    // vec<T>'s code for ARM: NEON (Advanced SIMD), which every 64-bit ARM CPU has, as SSE2 is
    // x86-64's (X86Generator.IrVec.cs). Each operation gives what x86's does, lane for lane,
    // where the two instruction sets differ: min and max of floats pick as minps and maxps do
    // (a < b ? a : b: not a number gives b), a float made a whole number is the smallest one
    // when it's too big or not a number (cvttps2dq's, not NEON's nearest), a shift by a lane's
    // width or more leaves 0 (or the sign), and a sum or the smallest or largest is worked out
    // lane by lane in order, an f32's as f64s. A vec is in a q register (v16-v27, as f64s are)
    // or 16 bytes of the frame; v28-v31 are this code's own.
    public sealed partial class A64Generator
    {
        /// <summary>A vec's 16 bytes written in the program, in its data: their label.</summary>
        readonly Dictionary<string, string> vecConstants = new();

        string VecConstant(byte[] bytes)
        {
            string key = Convert.ToHexString(bytes);
            if (!vecConstants.TryGetValue(key, out string? label))
            {
                label = $"phi_vec_{vecConstants.Count}";
                vecConstants[key] = label;
            }
            return label;
        }

        void EmitVecConstants(System.Text.StringBuilder output)
        {
            if (vecConstants.Count == 0) return;
            output.AppendLine("\t.section __TEXT,__const");
            foreach (var (hex, label) in vecConstants)
            {
                output.AppendLine("\t.p2align 4");
                output.AppendLine($"{label}:");
                output.AppendLine($"\t.byte {string.Join(", ", Convert.FromHexString(hex))}");
            }
        }

        sealed partial class Emitter
        {
            static int LaneBytes(TypeKind k) => k switch
            {
                TypeKind.I8 or TypeKind.U8 => 1,
                TypeKind.I16 or TypeKind.U16 => 2,
                TypeKind.I32 or TypeKind.U32 or TypeKind.F32 => 4,
                _ => 8,
            };

            static bool LaneSigned(TypeKind k) => k is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.I64;

            static bool LaneFloat(TypeKind k) => k is TypeKind.F32 or TypeKind.F64;

            /// <summary>A whole vec's arrangement by lane size: 16b, 8h, 4s, 2d.</summary>
            static string Arr(int bytes) => bytes switch { 1 => "16b", 2 => "8h", 4 => "4s", _ => "2d" };

            /// <summary>Half a vec's: 8b, 4h, 2s (the low half's arrangement for widening and narrowing).</summary>
            static string Half(int bytes) => bytes switch { 1 => "8b", 2 => "4h", _ => "2s" };

            static string El(int bytes) => bytes switch { 1 => "b", 2 => "h", 4 => "s", _ => "d" };

            /// <summary>v in a register (vN), read from its 16 bytes in the frame into temp if it has none.</summary>
            string VIn(VReg v, string temp = "v31")
            {
                if (Reg(v) is string r) return r;
                Mem("ldr", "q" + temp[1..], "x29", -regs.Slots[v], 16);
                return temp;
            }

            string VTarget(VReg v, string temp = "v31") => Reg(v) ?? temp;

            void VPut(VReg v, string reg)
            {
                if (Reg(v) != null) return;
                Mem("str", "q" + reg[1..], "x29", -regs.Slots[v], 16);
            }

            /// <summary>t (a register) = v's value.</summary>
            void VInto(string t, VReg v)
            {
                string s = VIn(v, t);
                if (s != t) E($"mov {t}.16b, {s}.16b");
            }

            /// <summary>One vec instruction, or false if it isn't one.</summary>
            bool Vector(Instr i)
            {
                switch (i)
                {
                    case ConstI { Dst.Vec: true } zero:
                    {
                        string t = VTarget(zero.Dst);
                        E($"movi {t}.2d, #0");
                        VPut(zero.Dst, t);
                        return true;
                    }
                    case CopyI { Dst.Vec: true } copy:
                    {
                        string t = VTarget(copy.Dst);
                        VInto(t, copy.Src.Reg!);
                        VPut(copy.Dst, t);
                        return true;
                    }
                    case LoadI { Kind: MemKind.V128 } load:
                    {
                        var (b, off) = Place(load.Place);
                        string t = VTarget(load.Dst);
                        Mem("ldr", "q" + t[1..], b, off, 16);
                        VPut(load.Dst, t);
                        return true;
                    }
                    case StoreI { Kind: MemKind.V128 } store:
                    {
                        string s = VIn(store.Src.Reg!);
                        var (b, off) = Place(store.Place);
                        Mem("str", "q" + s[1..], b, off, 16);
                        return true;
                    }
                    case LoadIdxI { Kind: MemKind.V128 } load:
                    {
                        var (b, off) = Element(load.Base, load.Index, load.Scale);
                        string t = VTarget(load.Dst);
                        Mem("ldr", "q" + t[1..], b, off, 16);
                        VPut(load.Dst, t);
                        return true;
                    }
                    case StoreIdxI { Kind: MemKind.V128 } store:
                    {
                        string s = VIn(store.Src.Reg!);
                        var (b, off) = Element(store.Base, store.Index, store.Scale);
                        Mem("str", "q" + s[1..], b, off, 16);
                        return true;
                    }
                    case VecMakeI m: Make(m); return true;
                    case VecGetI get: LaneGet(get); return true;
                    case VecSetI set: LaneSet(set); return true;
                    case VecReduceI r: Reduce(r); return true;
                    case VecI v: Op(v); return true;
                }
                return false;
            }

            void Make(VecMakeI m)
            {
                string t = VTarget(m.Dst);
                int bytes = LaneBytes(m.Lane);
                if (m.Bytes != null && m.Bytes.All(b => b == 0) || m.Bytes == null && m.Lanes.Count == 0) E($"movi {t}.2d, #0");
                else if (m.Bytes != null)
                {
                    string label = g.VecConstant(m.Bytes);
                    E($"adrp x16, {label}@PAGE");
                    E($"ldr q{t[1..]}, [x16, {label}@PAGEOFF]");
                }
                else if (m.Lanes.Count == 1)
                {
                    // one value in every lane
                    Opnd one = m.Lanes[0];
                    if (m.Lane == TypeKind.F64) E($"dup {t}.2d, {D2V(FGet(one.Reg!, "d30"))}.d[0]");
                    else if (m.Lane == TypeKind.F32)
                    {
                        E($"fcvt s30, {FGet(one.Reg!, "d30")}");
                        E($"dup {t}.4s, v30.s[0]");
                    }
                    else E($"dup {t}.{Arr(bytes)}, {GetOp(one, "x9", bytes == 8)}");
                }
                else
                {
                    // each lane put in, in turn (the others zeros first)
                    E($"movi {t}.2d, #0");
                    for (int k = 0; k < m.Lanes.Count; k++) PutLane(t, k, m.Lanes[k], m.Lane);
                }
                VPut(m.Dst, t);
            }

            /// <summary>The v register a d register is the low half of.</summary>
            static string D2V(string d) => "v" + d[1..];

            /// <summary>Lane k of the vec in register t made value (a float in its d register, or a whole number).</summary>
            void PutLane(string t, int k, Opnd value, TypeKind lane)
            {
                int bytes = LaneBytes(lane);
                if (lane == TypeKind.F64) E($"mov {t}.d[{k}], {D2V(FGet(value.Reg!, "d30"))}.d[0]");
                else if (lane == TypeKind.F32)
                {
                    E($"fcvt s30, {FGet(value.Reg!, "d30")}");
                    E($"mov {t}.s[{k}], v30.s[0]");
                }
                else E($"mov {t}.{El(bytes)}[{k}], {GetOp(value, "x9", bytes == 8)}");
            }

            /// <summary>
            /// A lane whose number is known only when it runs: the vec into the frame's scratch bytes
            /// (fn.Scratch), and x16 = where the lane is in them.
            /// </summary>
            void LaneInScratch(string vec, Opnd index, int bytes)
            {
                PlaceAddress("x16", fn.Scratch ?? throw new InvalidOperationException($"{methodLabel}: a lane by a number known when it runs, without scratch bytes"));
                E($"str q{vec[1..]}, [x16]");
                string ix = Get(index.Reg!, "x17", true);
                int shift = bytes switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };
                E(shift == 0 ? $"add x16, x16, {ix}" : $"add x16, x16, {ix}, lsl #{shift}");
            }

            void LaneGet(VecGetI get)
            {
                int bytes = LaneBytes(get.Lane);
                string a = VIn(get.A, "v31");
                if (!get.Index.IsImm)
                {
                    LaneInScratch(a, get.Index, bytes);
                    if (LaneFloat(get.Lane))
                    {
                        string ft = FTarget(get.Dst);
                        if (get.Lane == TypeKind.F64) E($"ldr {ft}, [x16]");
                        else
                        {
                            E("ldr s30, [x16]");
                            E($"fcvt {ft}, s30");
                        }
                        FPut(get.Dst, ft);
                        return;
                    }
                    string r = Target(get.Dst, "x9");
                    string op = bytes switch { 1 => LaneSigned(get.Lane) ? "ldrsb" : "ldrb", 2 => LaneSigned(get.Lane) ? "ldrsh" : "ldrh", _ => "ldr" };
                    E($"{op} {W(r, bytes == 8)}, [x16]");
                    Put(get.Dst, r);
                    return;
                }
                int k = (int)get.Index.Imm;
                if (LaneFloat(get.Lane))
                {
                    string ft = FTarget(get.Dst);
                    if (get.Lane == TypeKind.F64) E($"mov {D2V(ft)}.d[0], {a}.d[{k}]");
                    else
                    {
                        E($"mov v30.s[0], {a}.s[{k}]");
                        E($"fcvt {ft}, s30");
                    }
                    FPut(get.Dst, ft);
                    return;
                }
                string t = Target(get.Dst, "x9");
                // (a lane of up to 32 bits is kept as 32, its sign in them for a signed one)
                if (bytes == 8) E($"umov {W(t, true)}, {a}.d[{k}]");
                else if (bytes == 4) E($"umov {W(t, false)}, {a}.s[{k}]");
                else E($"{(LaneSigned(get.Lane) ? "smov" : "umov")} {W(t, false)}, {a}.{El(bytes)}[{k}]");
                Put(get.Dst, t);
            }

            void LaneSet(VecSetI set)
            {
                int bytes = LaneBytes(set.Lane);
                string t = VTarget(set.Dst, "v29");
                VInto(t, set.A);
                if (set.Index.IsImm)
                {
                    PutLane(t, (int)set.Index.Imm, set.Value, set.Lane);
                    VPut(set.Dst, t);
                    return;
                }
                LaneInScratch(t, set.Index, bytes);
                if (set.Lane == TypeKind.F64) E($"str {FGet(set.Value.Reg!, "d30")}, [x16]");
                else if (set.Lane == TypeKind.F32)
                {
                    E($"fcvt s30, {FGet(set.Value.Reg!, "d30")}");
                    E("str s30, [x16]");
                }
                else
                {
                    string v = GetOp(set.Value, "x9", bytes == 8);
                    E($"{bytes switch { 1 => "strb", 2 => "strh", _ => "str" }} {W(v, bytes == 8)}, [x16]");
                }
                PlaceAddress("x16", fn.Scratch!);
                E($"ldr q{t[1..]}, [x16]");
                VPut(set.Dst, t);
            }

            void Reduce(VecReduceI r)
            {
                TypeKind k = r.Lane;
                int bytes = LaneBytes(k), lanes = 16 / bytes;
                string a = VIn(r.A, "v31");
                switch (r.Op)
                {
                    case VReduce.Same:
                    {
                        // floats compare as numbers (0 is -0; not a number is never the same); whole numbers by their bytes
                        string b = VIn(r.B!, "v30");
                        E(k switch
                        {
                            TypeKind.F32 => $"fcmeq v29.4s, {a}.4s, {b}.4s",
                            TypeKind.F64 => $"fcmeq v29.2d, {a}.2d, {b}.2d",
                            _ => $"cmeq v29.16b, {a}.16b, {b}.16b",
                        });
                        E("uminv b29, v29.16b");
                        E("umov w9, v29.b[0]");
                        E("cmp w9, #0xFF");
                        string t = Target(r.Dst, "x9");
                        E($"cset {W(t, false)}, eq");
                        Put(r.Dst, t);
                        return;
                    }
                    case VReduce.Any or VReduce.All:
                    {
                        // a mask's lanes are all ones or all zeros: the top bit of each says which
                        E($"sshr v29.{Arr(bytes)}, {a}.{Arr(bytes)}, #{8 * bytes - 1}");
                        E(r.Op == VReduce.Any ? "umaxv b29, v29.16b" : "uminv b29, v29.16b");
                        E("umov w9, v29.b[0]");
                        string t = Target(r.Dst, "x9");
                        E($"cmp w9, #0");
                        E($"cset {W(t, false)}, ne");
                        Put(r.Dst, t);
                        return;
                    }
                }
                if (LaneFloat(k))
                {
                    // lane by lane, in order, as f64s (an f32's made one, as x86's adds them)
                    for (int n = 0; n < lanes; n++)
                    {
                        string into = n == 0 ? "d29" : "d30";
                        if (k == TypeKind.F64) E($"mov v{into[1..]}.d[0], {a}.d[{n}]");
                        else
                        {
                            E($"mov v{into[1..]}.s[0], {a}.s[{n}]");
                            E($"fcvt {into}, s{into[1..]}");
                        }
                        if (n == 0) continue;
                        switch (r.Op)
                        {
                            case VReduce.Sum: E("fadd d29, d29, d30"); break;
                            case VReduce.Smallest:
                                // (minsd: the first unless it's less, so not a number gives the second)
                                E("fcmp d29, d30");
                                E("fcsel d29, d29, d30, mi");
                                break;
                            default:
                                E("fcmp d29, d30");
                                E("fcsel d29, d29, d30, gt");
                                break;
                        }
                    }
                    string ft = FTarget(r.Dst);
                    E($"fmov {ft}, d29");
                    FPut(r.Dst, ft);
                    return;
                }
                bool signed = LaneSigned(k);
                // in 64 bits, each lane with its sign or zeros, then the result's lane's kind
                for (int n = 0; n < lanes; n++)
                {
                    string into = n == 0 ? "x7" : "x8";
                    if (bytes == 8) E($"umov {into}, {a}.d[{n}]");
                    else if (signed) E($"smov {into}, {a}.{El(bytes)}[{n}]");
                    else E($"umov {W(into, false)}, {a}.{El(bytes)}[{n}]");
                    if (n == 0) continue;
                    switch (r.Op)
                    {
                        case VReduce.Sum: E("add x7, x7, x8"); break;
                        case VReduce.Smallest:
                            E("cmp x7, x8");
                            E($"csel x7, x8, x7, {(signed ? "gt" : "hi")}");
                            break;
                        default:
                            E("cmp x7, x8");
                            E($"csel x7, x8, x7, {(signed ? "lt" : "lo")}");
                            break;
                    }
                }
                if (bytes == 1) E(signed ? "sxtb w7, w7" : "uxtb w7, w7");
                else if (bytes == 2) E(signed ? "sxth w7, w7" : "uxth w7, w7");
                else if (bytes == 4) E("mov w7, w7");
                string rt = Target(r.Dst, "x9");
                if (rt != W("x7", r.Dst.Wide)) E($"mov {rt}, {W("x7", r.Dst.Wide)}");
                Put(r.Dst, rt);
            }

            /// <summary>Each lane of a and b (64 bits each) through x9 and x8, worked out by lane into x9, into t's lane.</summary>
            void PerLane64(string t, string a, string? b, Action lane)
            {
                for (int n = 0; n < 2; n++)
                {
                    E($"umov x9, {a}.d[{n}]");
                    if (b != null) E($"umov x8, {b}.d[{n}]");
                    lane();
                    E($"mov v28.d[{n}], x9");
                }
                E($"mov {t}.16b, v28.16b");
            }

            void Op(VecI v)
            {
                TypeKind k = v.Lane;
                int bytes = LaneBytes(k);
                bool isFloat = LaneFloat(k), signed = LaneSigned(k);
                string ar = Arr(bytes);
                string a = VIn(v.A, "v31");
                string? b = v.B != null ? VIn(v.B, "v30") : null;
                string t = VTarget(v.Dst, "v29");
                switch (v.Op)
                {
                    case VOp.Add: E($"{(isFloat ? "fadd" : "add")} {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.Sub: E($"{(isFloat ? "fsub" : "sub")} {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.Div: E($"fdiv {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.And: E($"and {t}.16b, {a}.16b, {b}.16b"); break;
                    case VOp.Or: E($"orr {t}.16b, {a}.16b, {b}.16b"); break;
                    case VOp.Xor: E($"eor {t}.16b, {a}.16b, {b}.16b"); break;
                    case VOp.AddSat: E($"{(signed ? "sqadd" : "uqadd")} {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.SubSat: E($"{(signed ? "sqsub" : "uqsub")} {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.Average: E($"urhadd {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.Mul when isFloat: E($"fmul {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.Mul when bytes < 8: E($"mul {t}.{ar}, {a}.{ar}, {b}.{ar}"); break;
                    case VOp.Mul: PerLane64(t, a, b, () => E("mul x9, x9, x8")); break;
                    case VOp.Not: E($"mvn {t}.16b, {a}.16b"); break;
                    case VOp.Neg: E($"{(isFloat ? "fneg" : "neg")} {t}.{ar}, {a}.{ar}"); break;
                    case VOp.Sqrt: E($"fsqrt {t}.{ar}, {a}.{ar}"); break;
                    case VOp.Abs: E($"{(isFloat ? "fabs" : "abs")} {t}.{ar}, {a}.{ar}"); break;
                    case VOp.Shl or VOp.Shr: Shift(v, t, a, bytes, signed); break;
                    case VOp.Min or VOp.Max: MinMax(v, t, a, b!, k, bytes); break;
                    case VOp.Less or VOp.Greater or VOp.Equal: Compare(v, t, a, b!, k, bytes); break;
                    case VOp.Pick:
                    {
                        // A the mask: B where it's set, C where it isn't
                        string c = VIn(v.C!, "v28");
                        E($"mov v28.16b, {c}.16b");          // (c may be what's overwritten next)
                        E($"mov v29.16b, {a}.16b");
                        E($"bsl v29.16b, {b}.16b, v28.16b");
                        if (t != "v29") E($"mov {t}.16b, v29.16b");
                        break;
                    }
                    case VOp.Widen or VOp.WidenHigh:
                    {
                        bool high = v.Op == VOp.WidenHigh;
                        if (k == TypeKind.F32) E(high ? $"fcvtl2 {t}.2d, {a}.4s" : $"fcvtl {t}.2d, {a}.2s");
                        else
                        {
                            string op = (signed ? "sxtl" : "uxtl") + (high ? "2" : "");
                            E($"{op} {t}.{Arr(bytes * 2)}, {a}.{(high ? Arr(bytes) : Half(bytes))}");
                        }
                        break;
                    }
                    case VOp.Narrow:
                    {
                        // A's lanes then B's, half the size each, stopping at the ends (an f64's rounded)
                        string to = Arr(bytes / 2), half = Half(bytes / 2);
                        string op = k switch
                        {
                            TypeKind.F64 => "fcvtn",
                            TypeKind.I16 or TypeKind.I32 or TypeKind.I64 => "sqxtn",
                            _ => "uqxtn",
                        };
                        E($"{op} v28.{half}, {a}.{ar}");
                        E($"{op}2 v28.{to}, {b}.{ar}");
                        E($"mov {t}.16b, v28.16b");
                        break;
                    }
                    case VOp.Convert:
                        ConvertLanes(v, t, a);
                        break;
                    default:
                        throw new InvalidOperationException($"{methodLabel}: a vec's {v.Op} on {k} lanes");
                }
                VPut(v.Dst, t);
            }

            /// <summary>
            /// v &lt;&lt; n and v &gt;&gt; n, every lane by the same count: one of a lane's width or more
            /// leaves 0, or the sign in each bit for a signed &gt;&gt;, as x86's instructions do (a
            /// count is unsigned there: a negative one is more than any width).
            /// </summary>
            void Shift(VecI v, string t, string a, int bytes, bool signed)
            {
                bool right = v.Op == VOp.Shr;
                int width = 8 * bytes;
                string ar = Arr(bytes);
                if (v.Count.IsImm)
                {
                    ulong count = (ulong)v.Count.Imm;
                    if (count >= (ulong)width)
                    {
                        if (right && signed) E($"sshr {t}.{ar}, {a}.{ar}, #{width - 1}");
                        else E($"movi {t}.2d, #0");
                    }
                    else if (count == 0) { if (t != a) E($"mov {t}.16b, {a}.16b"); }
                    else E($"{(right ? signed ? "sshr" : "ushr" : "shl")} {t}.{ar}, {a}.{ar}, #{count}");
                    return;
                }
                // the count, as 32 bits unsigned, no more than the width (width - 1 for a signed
                // >>), then shifted by it, left, or right as a negative count
                string c = Get(v.Count.Reg!, "x9", false);
                int most = right && signed ? width - 1 : width;
                E($"mov w8, #{most}");
                E($"cmp {c}, w8");
                E($"csel w9, {c}, w8, lo");
                if (right) E("neg w9, w9");
                if (bytes == 8)
                {
                    E("sxtw x9, w9");
                    E("dup v28.2d, x9");
                }
                else E($"dup v28.{ar}, w9");
                E($"{(right && signed ? "sshl" : "ushl")} {t}.{ar}, {a}.{ar}, v28.{ar}");
            }

            void MinMax(VecI v, string t, string a, string b, TypeKind k, int bytes)
            {
                bool min = v.Op == VOp.Min;
                string ar = Arr(bytes);
                if (LaneFloat(k))
                {
                    // minps: a < b ? a : b, maxps: a > b ? a : b (not a number: b)
                    E(min ? $"fcmgt v28.{ar}, {b}.{ar}, {a}.{ar}" : $"fcmgt v28.{ar}, {a}.{ar}, {b}.{ar}");
                    E($"bsl v28.16b, {a}.16b, {b}.16b");
                    E($"mov {t}.16b, v28.16b");
                    return;
                }
                bool signed = LaneSigned(k);
                if (bytes < 8)
                {
                    E($"{(min ? signed ? "smin" : "umin" : signed ? "smax" : "umax")} {t}.{ar}, {a}.{ar}, {b}.{ar}");
                    return;
                }
                // (no 64-bit min or max: a mask of which is bigger, then each lane picked by it)
                string gt = signed ? "cmgt" : "cmhi";
                E($"{gt} v28.2d, {a}.2d, {b}.2d");
                E(min ? $"bsl v28.16b, {b}.16b, {a}.16b" : $"bsl v28.16b, {a}.16b, {b}.16b");
                E($"mov {t}.16b, v28.16b");
            }

            void Compare(VecI v, string t, string a, string b, TypeKind k, int bytes)
            {
                string ar = Arr(bytes);
                if (LaneFloat(k))
                {
                    E(v.Op switch
                    {
                        VOp.Equal => $"fcmeq {t}.{ar}, {a}.{ar}, {b}.{ar}",
                        VOp.Greater => $"fcmgt {t}.{ar}, {a}.{ar}, {b}.{ar}",
                        _ => $"fcmgt {t}.{ar}, {b}.{ar}, {a}.{ar}",
                    });
                    return;
                }
                string gt = LaneSigned(k) ? "cmgt" : "cmhi";
                E(v.Op switch
                {
                    VOp.Equal => $"cmeq {t}.{ar}, {a}.{ar}, {b}.{ar}",
                    VOp.Greater => $"{gt} {t}.{ar}, {a}.{ar}, {b}.{ar}",
                    _ => $"{gt} {t}.{ar}, {b}.{ar}, {a}.{ar}",
                });
            }

            /// <summary>v as vec&lt;T&gt; between floats and whole numbers: each lane's value, toward zero, as x86's instructions make it.</summary>
            void ConvertLanes(VecI v, string t, string a)
            {
                switch (v.Lane, v.To)
                {
                    case (TypeKind.I32, TypeKind.F32): E($"scvtf {t}.4s, {a}.4s"); return;
                    case (TypeKind.U32, TypeKind.F32): E($"ucvtf {t}.4s, {a}.4s"); return;
                    case (TypeKind.I64, TypeKind.F64): E($"scvtf {t}.2d, {a}.2d"); return;
                    case (TypeKind.U64, TypeKind.F64): E($"ucvtf {t}.2d, {a}.2d"); return;
                    case (TypeKind.F32, TypeKind.I32):
                    {
                        // cvttps2dq: one too big, or not a number, is the smallest i32
                        E($"fcvtzs v28.4s, {a}.4s");
                        E("mvni v30.4s, #0x80, lsl #24");      // 0x7FFFFFFF
                        E("cmeq v30.4s, v28.4s, v30.4s");
                        E("sub v28.4s, v28.4s, v30.4s");        // (0x7FFFFFFF + 1 is the smallest)
                        E($"fcmeq v30.4s, {a}.4s, {a}.4s");
                        E("movi v29.4s, #0x80, lsl #24");
                        E("bsl v30.16b, v28.16b, v29.16b");
                        E($"mov {t}.16b, v30.16b");
                        return;
                    }
                }
                // f32 to u32 (cvttss2si's 64 bits, the low half kept), and f64 to i64 or u64
                // (cvttsd2si's): a lane at a time, as a float is made a whole number elsewhere
                bool f32 = v.Lane == TypeKind.F32;
                int lanes = f32 ? 4 : 2;
                for (int n = 0; n < lanes; n++)
                {
                    if (f32)
                    {
                        E($"mov v30.s[0], {a}.s[{n}]");
                        E("fcvt d30, s30");
                    }
                    else E($"mov v30.d[0], {a}.d[{n}]");
                    E("fcvtzs x9, d30");
                    E("mov x17, #0x7FFFFFFFFFFFFFFF");
                    E("cmp x9, x17");
                    E("cinc x9, x9, eq");
                    E("mov x17, #0x8000000000000000");
                    E("fcmp d30, d30");
                    E("csel x9, x17, x9, vs");
                    E(f32 ? $"mov v28.s[{n}], w9" : $"mov v28.d[{n}], x9");
                }
                E($"mov {t}.16b, v28.16b");
            }
        }
    }
}

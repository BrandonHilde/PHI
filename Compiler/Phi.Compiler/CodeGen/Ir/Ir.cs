using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // PHI's intermediate form (stage 1 of PerformanceStrategy.md): a method as blocks of
    // three-address instructions over values that live in registers, made from the checked
    // syntax tree (IrBuilder), given registers (RegisterAllocator), and written as x64
    // assembly by the generator (X86Generator.Ir.cs). It's tried on a method only when every
    // statement and expression in it is one it knows exactly; any other method is compiled by
    // the generator as before. Both follow one contract (arguments pushed, a frame on rbp, the
    // result in eax or edx:eax, r12 to r15 kept), so either calls the other.

    /// <summary>
    /// A value an instruction gives: a register to the allocator, or a place in the frame if
    /// there aren't enough. A method's variable is one too, written again whenever it's
    /// assigned (the form isn't SSA). Wide: 64 bits; otherwise 32 (the upper half is zero).
    /// </summary>
    public sealed class VReg
    {
        public int Id { get; init; }
        public bool Wide { get; init; }

        /// <summary>An f64, in an xmm register (or its slot in the frame).</summary>
        public bool Float { get; init; }

        /// <summary>A vec: all 16 bytes of an xmm register (or two slots in the frame).</summary>
        public bool Vec { get; init; }
        public string Name { get; init; } = "";
        public override string ToString() => Name.Length > 0 ? $"v{Id}({Name})" : $"v{Id}";
    }

    /// <summary>An instruction's operand: a value, or a number written into the instruction.</summary>
    public readonly struct Opnd
    {
        public VReg? Reg { get; }
        public long Imm { get; }
        public bool IsImm => Reg == null;
        Opnd(VReg? reg, long imm) { Reg = reg; Imm = imm; }
        public static Opnd Of(VReg reg) => new(reg, 0);
        public static Opnd Const(long value) => new(null, value);
        public static implicit operator Opnd(VReg reg) => Of(reg);
        public override string ToString() => IsImm ? Imm.ToString() : Reg!.ToString();
    }

    public enum BinOp { Add, Sub, Mul, And, Or, Xor, Shl, Shr, Sar, DivS, DivU, RemS, RemU }

    public enum UnOp { Neg, Not }

    /// <summary>A comparison: S for signed numbers, U for unsigned ones.</summary>
    public enum Cond { Eq, Ne, LtS, LeS, GtS, GeS, LtU, LeU, GtU, GeU }

    /// <summary>How a number is kept in memory: its bytes, and whether a smaller one is signed.</summary>
    /// <summary>F32: an f32 in memory, an f64 in a register (read, it's made one; written, rounded). V128: a vec's 16 bytes.</summary>
    public enum MemKind { U8, S8, U16, S16, D32, Q64, F64, F32, V128 }

    /// <summary>What a call gives back: nothing, 32 bits in eax, 64 in edx:eax (a 64-bit number), a pointer in rax, an f64 in st0, or a vec in xmm0.</summary>
    public enum CallResult { None, Eax, EdxEax, Rax, St0, Xmm0 }

    public enum FOp { Add, Sub, Mul, Div }

    /// <summary>Dst = an f64 written in the program.</summary>
    public sealed class FConstI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public double Value { get; init; }
    }

    /// <summary>Dst = A op B, f64s (each rounded as an f64, as the generator's x87 code, set to double precision, does).</summary>
    public sealed class FBinI : Instr
    {
        public FOp Op { get; init; }
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public VReg B { get; init; } = null!;
    }

    public sealed class FNegI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
    }

    /// <summary>A whole number made an f64 (the nearest one): Kind is how Src is kept (D32 signed, U32 as Q64 zero-extended...).</summary>
    public sealed class ToFloatI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Src { get; init; } = null!;
        public bool Signed { get; init; }
    }

    /// <summary>An f64 made a whole number toward zero, all 64 bits of it (as the generator's fistp qword), Dst keeping what fits.</summary>
    public sealed class ToWholeI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Src { get; init; } = null!;
    }

    public abstract class Instr { }

    // ---------------------------------------------------------------- vecs

    /// <summary>What a vec instruction does to each lane (or across them).</summary>
    public enum VOp
    {
        Add, Sub, Mul, Div, And, Or, Xor, Neg, Not, Shl, Shr, Min, Max, Abs, Sqrt, AddSat, SubSat, Average,
        Less, Equal, Greater, Pick, Widen, WidenHigh, Narrow, Convert,
    }

    /// <summary>
    /// Dst = Op of A (and B, and C for Pick: A the mask, B where it's set, C where it isn't), lane
    /// by lane, the lanes Lane's kind; a shift's count is Count; Widen, Narrow and Convert make
    /// lanes of To's kind.
    /// </summary>
    public sealed class VecI : Instr
    {
        public VOp Op { get; init; }
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public VReg? B { get; init; }
        public VReg? C { get; init; }
        public Opnd Count { get; init; }
        public Semantics.TypeKind Lane { get; init; }
        public Semantics.TypeKind To { get; init; }
    }

    /// <summary>
    /// Dst = a vec: Bytes when every lane is known when it's built, zeros when there are no
    /// lanes, one value in every lane, or a value for each (whole numbers in a register or
    /// written in, floats in an f64 register, rounded for f32 lanes).
    /// </summary>
    public sealed class VecMakeI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public byte[]? Bytes { get; init; }
        public List<Opnd> Lanes { get; } = new();
        public Semantics.TypeKind Lane { get; init; }
    }

    /// <summary>Dst = lane Index of A (a whole number, or an f64 for float lanes).</summary>
    public sealed class VecGetI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public Opnd Index { get; init; }
        public Semantics.TypeKind Lane { get; init; }
    }

    /// <summary>Dst = A with lane Index made Value.</summary>
    public sealed class VecSetI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public Opnd Index { get; init; }
        public Opnd Value { get; init; }
        public Semantics.TypeKind Lane { get; init; }
    }

    public enum VReduce { Sum, Smallest, Largest, Any, All, Same }

    /// <summary>Dst = one value from A's lanes: their sum, the smallest or largest, whether any or all are set (a mask's), or whether A is B lane for lane (Same).</summary>
    public sealed class VecReduceI : Instr
    {
        public VReduce Op { get; init; }
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public VReg? B { get; init; }
        public Semantics.TypeKind Lane { get; init; }
    }

    /// <summary>Dst = an f64 rounded to the f32 nearest it (what an f32 variable holds).</summary>
    public sealed class RoundF32I : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Src { get; init; } = null!;
    }

    // ---------------------------------------------------------------- log

    public enum PrintKind { Text, Int, Uint, Int64, Uint64, Float, Char }

    /// <summary>
    /// One value of a log written out, as the generator's EmitLog writes it (to the screen,
    /// or with Serial to COM1 or standard error): text at a label or an address, a number,
    /// a character. A call of the library, which may change what any call changes.
    /// </summary>
    public sealed class PrintI : Instr
    {
        public PrintKind Kind { get; init; }
        public Opnd Value { get; init; }

        /// <summary>Text written in the program (Kind Text): printed from the generator's label for it.</summary>
        public string? Literal { get; init; }

        /// <summary>A class's str (Kind Text): its label, where its text is.</summary>
        public string? Label { get; init; }
        public bool Serial { get; init; }
    }

    /// <summary>A call of one of the library's routines that takes nothing (phi_log_take, phi_log_give, phi_flush).</summary>
    public sealed class RoutineI : Instr
    {
        public string Label { get; init; } = "";
    }

    /// <summary>The code of a new line of the source starts here (its label, for errors and the profiler).</summary>
    public sealed class LineMark : Instr
    {
        public Node Node { get; init; } = null!;
    }

    public sealed class ConstI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public long Value { get; init; }
    }

    public sealed class CopyI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public Opnd Src { get; init; }
    }

    /// <summary>Dst = A op B, at Dst's width. A shift's B is a count (its low bits).</summary>
    public sealed class BinI : Instr
    {
        public BinOp Op { get; init; }
        public VReg Dst { get; init; } = null!;
        public Opnd A { get; init; }
        public Opnd B { get; init; }
    }

    public sealed class UnI : Instr
    {
        public UnOp Op { get; init; }
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
    }

    /// <summary>A 32-bit value made 64 bits: with its sign (Signed) or with zeros.</summary>
    public sealed class ExtendI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Src { get; init; } = null!;
        public bool Signed { get; init; }
    }

    /// <summary>The low part of Src, as Kind keeps it, made 32 bits again (cutting a 64-bit value, or a u8's low byte).</summary>
    public sealed class NarrowI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Src { get; init; } = null!;
        public MemKind Kind { get; init; }
    }

    /// <summary>Dst = 1 if A cond B, else 0 (A and B at A's width).</summary>
    public sealed class SetCcI : Instr
    {
        /// <summary>A and B are f64s (B a value): not a number makes every comparison false but !=.</summary>
        public bool Float { get; init; }
        public Cond Cond { get; init; }
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public Opnd B { get; init; }
    }

    /// <summary>A class's variable (or a parameter, at its place in the frame) read into a value.</summary>
    public sealed class LoadI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public string Place { get; init; } = "";
        public MemKind Kind { get; init; }
    }

    public sealed class StoreI : Instr
    {
        public string Place { get; init; } = "";
        public Opnd Src { get; init; }
        public MemKind Kind { get; init; }
    }

    /// <summary>
    /// A call of one of the generator's routines for a struct's objects (phi_free_T with the
    /// object in rax, phi_renew_T with the address of where it's kept), which changes what a
    /// call may change, as a method does: what lives across it is in r12 to r15.
    /// </summary>
    public sealed class ObjCallI : Instr
    {
        public string Kind { get; init; } = "";
        public Semantics.StructSymbol Struct { get; init; } = null!;
        /// <summary>The object, for free.</summary>
        public VReg? Arg { get; init; }
        /// <summary>Where a method's own object is kept in its frame, for renew.</summary>
        public string? Home { get; init; }
    }

    /// <summary>The line an object is made on, for the message if the heap runs out (the generator's ObjectLine).</summary>
    public sealed class ObjectLineI : Instr
    {
        public Syntax.Node At { get; init; } = null!;
    }

    /// <summary>
    /// The method onto (On) or off the list a throw looks at as it goes past, in a program that
    /// catches errors: then a throw frees the method's own objects (see IrFunction.UnwindRecord).
    /// </summary>
    public sealed class UnwindI : Instr
    {
        public bool On { get; init; }
    }

    /// <summary>A call of an assembly block (call asm.Name;), which may change every register: nothing lives in one across it.</summary>
    public sealed class RawCallI : Instr
    {
        public string Label { get; init; } = "";
    }

    /// <summary>
    /// locked: the lock at a class's variable taken, as the generator takes it (a plain store
    /// while the program has one thread, xchg after); in a program that catches errors, what
    /// catches them is kept in Saved and none is caught until the lock is given back.
    /// </summary>
    public sealed class LockI : Instr
    {
        public string Label { get; init; } = "";
        public VReg Saved { get; init; } = null!;
    }

    /// <summary>The lock given back (and what catches errors put back): at the block's end, and before a return or break out of it.</summary>
    public sealed class UnlockI : Instr
    {
        public string Label { get; init; } = "";
        public VReg Saved { get; init; } = null!;
    }

    /// <summary>Dst = A * Factor + Add, with Factor 3, 5 or 9: one lea (A + A * 2, 4 or 8, plus a number), at Dst's width.</summary>
    public sealed class LeaI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public int Factor { get; init; }
        public long Add { get; init; }
    }

    /// <summary>Dst = the address of a class's variable (addr x).</summary>
    public sealed class AddrI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public string Label { get; init; } = "";
    }

    /// <summary>Dst = the element at Base + Index * Scale (a pointer's: p:i). Index is 64 bits, or a number.</summary>
    public sealed class LoadIdxI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Base { get; init; } = null!;
        public Opnd Index { get; init; }
        public int Scale { get; init; }
        public MemKind Kind { get; init; }
    }

    /// <summary>The element at Base + Index * Scale = Src.</summary>
    public sealed class StoreIdxI : Instr
    {
        public VReg Base { get; init; } = null!;
        public Opnd Index { get; init; }
        public int Scale { get; init; }
        public Opnd Src { get; init; }
        public MemKind Kind { get; init; }
    }

    /// <summary>
    /// The check a number gets going between int and u32 (the binder's CheckedNumberExpr): its
    /// top bit set (negative, or 2 billion or more) stops with the error the generator gives.
    /// </summary>
    public sealed class SignCheckI : Instr
    {
        /// <summary>The class variables a loop holds in registers, written back before the error (as the generator's StoreLoopHeld).</summary>
        public List<StoreI> Held { get; init; } = new();

        public VReg Value { get; init; } = null!;
        public Node At { get; init; } = null!;
        public string Text { get; init; } = "";
    }

    /// <summary>A call of a method: its arguments pushed first to last, 8 bytes each, as the generator does.</summary>
    public sealed class CallI : Instr
    {
        public string Label { get; init; } = "";
        public List<Opnd> Args { get; } = new();

        /// <summary>How each argument is kept: an f32's is rounded into the low half of its 8 bytes, a vec's takes 16.</summary>
        public List<MemKind> ArgKinds { get; } = new();
        public VReg? Dst { get; init; }
        public CallResult Result { get; init; }
    }

    /// <summary>
    /// An index checked against an array's size (unsigned, so a negative one fails too): out of
    /// range stops with the generator's error for the line, which if call ... throws catches.
    /// </summary>
    public sealed class CheckI : Instr
    {
        /// <summary>The class variables a loop holds in registers, written back before the error (as the generator's StoreLoopHeld).</summary>
        public List<StoreI> Held { get; init; } = new();

        public VReg Index { get; init; } = null!;
        public long Limit { get; init; }
        public Node At { get; init; } = null!;
    }

    public abstract class Terminator { }

    /// <summary>when: the branch for Value's case, or Default (a table of jumps when the cases are close together).</summary>
    public sealed class SwitchT : Terminator
    {
        public VReg Value { get; init; } = null!;
        public List<(long Value, Block Target)> Cases { get; } = new();
        public Block Default { get; set; } = null!;
    }

    public sealed class JumpT : Terminator
    {
        public Block Target { get; set; } = null!;
    }

    public sealed class BranchT : Terminator
    {
        /// <summary>
        /// Set when the branch asks whether A has any of these bits (A &amp; mask is or isn't 0):
        /// one test instruction, with Cond Eq or Ne and B 0.
        /// </summary>
        public long? TestMask { get; set; }

        /// <summary>A and B are f64s (B a value), compared as IEEE has it.</summary>
        public bool Float { get; init; }
        public Cond Cond { get; init; }
        public VReg A { get; set; } = null!;
        public Opnd B { get; init; }
        public Block Then { get; set; } = null!;
        public Block Else { get; set; } = null!;
    }

    public sealed class RetT : Terminator
    {
        public Opnd? Value { get; init; }

        /// <summary>A 64-bit number, given back in edx:eax.</summary>
        public bool Wide { get; init; }

        /// <summary>A pointer, given back whole in rax.</summary>
        public bool Pointer { get; init; }

        /// <summary>An f64, given back in st0 (as the generator gives one).</summary>
        public bool Float { get; init; }

        /// <summary>A vec, given back in xmm0.</summary>
        public bool Vec { get; init; }
    }

    public sealed class Block
    {
        public int Id { get; init; }
        public List<Instr> Instrs { get; } = new();
        public Terminator? End { get; set; }
        public override string ToString() => $"b{Id}";
    }

    public sealed class IrFunction
    {
        public VReg NewFloat(string name = "")
        {
            var v = new VReg { Id = Values.Count, Wide = true, Float = true, Name = name };
            Values.Add(v);
            return v;
        }

        public VReg NewVec(string name = "")
        {
            var v = new VReg { Id = Values.Count, Wide = true, Vec = true, Name = name };
            Values.Add(v);
            return v;
        }

        /// <summary>
        /// 48 bytes in the frame for what's worked out a part at a time (a vec's lanes one by
        /// one, a number written out as text), within one instruction; null until one needs it.
        /// </summary>
        public string? Scratch { get; private set; }

        public string NeedScratch()
        {
            if (Scratch == null)
            {
                for (int k = 0; k < 6; k++) NewHome();
                Scratch = $"rbp - {8 * Homes}";
            }
            return Scratch;
        }

        public string Name { get; init; } = "";
        public List<Block> Blocks { get; } = new();

        /// <summary>
        /// The 8-byte places at the top of the frame (rbp - 8, - 16, ...) the method keeps
        /// things in that must be in memory: its own objects, which are freed from there at its
        /// end and by a throw going past. The values the allocator finds no register for come after.
        /// </summary>
        public int Homes { get; private set; }

        public string NewHome() => $"rbp - {8 * ++Homes}";

        /// <summary>The method's own objects: where each is kept, and its struct (freed at the end, in this order).</summary>
        public List<(string Home, Semantics.StructSymbol Struct)> Owned { get; } = new();

        /// <summary>
        /// In a program that catches errors, with objects of its own: how far below rbp the
        /// record the throw looks at is (the record before, the routine that frees the objects,
        /// and rbp, 8 bytes each), or 0.
        /// </summary>
        public int UnwindRecord { get; set; }
        public List<VReg> Values { get; } = new();

        public VReg NewValue(bool wide, string name = "")
        {
            var v = new VReg { Id = Values.Count, Wide = wide, Name = name };
            Values.Add(v);
            return v;
        }

        public Block NewBlock()
        {
            var b = new Block { Id = Blocks.Count };
            Blocks.Add(b);
            return b;
        }

        /// <summary>The form as text, for reading when something's wrong (PHI_IR_TRACE=2).</summary>
        public string Dump()
        {
            var sb = new System.Text.StringBuilder($"ir {Name}\n");
            foreach (Block b in Blocks)
            {
                sb.Append($"  {b}:\n");
                foreach (Instr i in b.Instrs) sb.Append("    ").Append(Text(i)).Append('\n');
                sb.Append("    ").Append(b.End switch
                {
                    JumpT j => $"jump {j.Target}",
                    BranchT br => $"if {br.A} {br.Cond} {br.B} then {br.Then} else {br.Else}",
                    RetT r => $"ret {r.Value}",
                    SwitchT sw => $"switch {sw.Value} {string.Join(" ", sw.Cases.Select(c => $"{c.Value}:{c.Target}"))} else {sw.Default}",
                    _ => "(no end)",
                }).Append('\n');
            }
            return sb.ToString();
        }

        static string Text(Instr i) => i switch
        {
            LineMark => "line",
            ConstI c => $"{c.Dst} = {c.Value}",
            CopyI c => $"{c.Dst} = {c.Src}",
            BinI b => $"{b.Dst} = {b.Op} {b.A}, {b.B}",
            UnI u => $"{u.Dst} = {u.Op} {u.A}",
            ExtendI e => $"{e.Dst} = extend{(e.Signed ? "s" : "z")} {e.Src}",
            NarrowI n => $"{n.Dst} = narrow {n.Kind} {n.Src}",
            SetCcI s => $"{s.Dst} = {s.A} {s.Cond} {s.B}",
            LoadI l => $"{l.Dst} = load {l.Kind} [{l.Place}]",
            StoreI s => $"store {s.Kind} [{s.Place}] = {s.Src}",
            CallI c => $"{c.Dst} = call {c.Label}({string.Join(", ", c.Args)})",
            LoadIdxI l => $"{l.Dst} = load {l.Kind} [{l.Base} + {l.Index} * {l.Scale}]",
            StoreIdxI st => $"store {st.Kind} [{st.Base} + {st.Index} * {st.Scale}] = {st.Src}",
            SignCheckI c => $"check {c.Value}",
            CheckI c => $"check {c.Index} below {c.Limit}",
            AddrI a => $"{a.Dst} = addr {a.Label}",
            LeaI l => $"{l.Dst} = {l.A} * {l.Factor} + {l.Add}",
            RawCallI r => $"call asm {r.Label}",
            ObjCallI o => $"call {o.Kind} {o.Struct.Name} ({(object?)o.Arg ?? o.Home})",
            ObjectLineI => "object line",
            UnwindI u => u.On ? "unwind on" : "unwind off",
            LockI l => $"{l.Saved} = lock {l.Label}",
            UnlockI u => $"unlock {u.Label} ({u.Saved})",
            FConstI f => $"{f.Dst} = {f.Value}f",
            FBinI f => $"{f.Dst} = f{f.Op} {f.A}, {f.B}",
            FNegI f => $"{f.Dst} = fneg {f.A}",
            ToFloatI f => $"{f.Dst} = tofloat{(f.Signed ? "s" : "u")} {f.Src}",
            ToWholeI f => $"{f.Dst} = towhole {f.Src}",
            VecI v => $"{v.Dst} = v{v.Op}.{v.Lane} {v.A}{(v.B != null ? ", " + v.B : "")}{(v.C != null ? ", " + v.C : "")}{(v.Op == VOp.Shl || v.Op == VOp.Shr ? ", " + v.Count : "")}",
            VecMakeI m => $"{m.Dst} = vmake.{m.Lane} {(m.Bytes != null ? "[" + string.Join(" ", m.Bytes) + "]" : string.Join(" ", m.Lanes))}",
            VecGetI g => $"{g.Dst} = {g.A}:{g.Index}",
            VecSetI st => $"{st.Dst} = {st.A} with :{st.Index} = {st.Value}",
            VecReduceI r => $"{r.Dst} = v{r.Op}.{r.Lane} {r.A}{(r.B != null ? ", " + r.B : "")}",
            RoundF32I r => $"{r.Dst} = roundf32 {r.Src}",
            PrintI p => $"print {p.Kind} {(object?)p.Literal ?? (object?)p.Label ?? p.Value}",
            RoutineI r => $"call {r.Label}",
            _ => i.GetType().Name,
        };
    }
}

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

        /// <summary>An i64 or u64 (Wide, and not a float's or vec's register).</summary>
        public bool Int64 => Wide && !Float && !Vec;

        /// <summary>An f64, in an xmm register (or its slot in the frame).</summary>
        public bool Float { get; init; }

        /// <summary>A vec: all 16 bytes of an xmm register (or two slots in the frame).</summary>
        public bool Vec { get; init; }
        public string Name { get; init; } = "";

        /// <summary>
        /// Kept in its own slot in the frame the whole method, never a register: a variable whose
        /// address is taken (addr n), which what it's given to may read and write at any time.
        /// </summary>
        public bool Pinned { get; set; }

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

    /// <summary>
    /// The FPU's own, as the generator works it out: an instruction on A (fsqrt, fabs, fsin, fcos),
    /// frndint with a rounding (0 nearest, 1 down, 2 up, 3 toward zero), or (B given) what's left
    /// of A over B with A's sign, fprem until it's done. Dst is the f64 it comes to.
    /// </summary>
    public sealed class X87I : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public VReg? B { get; init; }
        public string Instruction { get; init; } = "";
        public int Rounding { get; init; } = -1;
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

    // ---------------------------------------------------------------- text

    public enum TextOp { Literal, OrEmpty, Copy, Length, Equal, Order }

    /// <summary>
    /// Text, as the generator has it (the address of zero-terminated bytes): Dst = where text
    /// written in the program is (Literal); A, or '' when A is 0 (OrEmpty); the text at A copied
    /// into Place, which has Room bytes, too long an error (Copy); Dst = how long A is (Length);
    /// 0 when A and B are the same text, 1 when not (Equal); -1, 0 or 1 as A comes before B, is
    /// it, or after (Order). Copy, Length, Equal and Order call the library.
    /// </summary>
    public sealed class TextI : Instr
    {
        public TextOp Op { get; init; }
        public VReg? Dst { get; init; }
        public VReg? A { get; init; }
        public VReg? B { get; init; }
        public string? Literal { get; init; }
        public string? Place { get; init; }
        public int Room { get; init; }

        /// <summary>The class variables a loop holds in registers, written back before the error a Copy may stop with.</summary>
        public List<StoreI> Held { get; init; } = new();
    }

    /// <summary>
    /// An array that grows (or a struct's without a size) given room for Count elements of
    /// Size bytes, its elements moved when there isn't: the generator's phi_array_grow, with
    /// the array's place (its elements' address, then how many, then its room) at View.
    /// </summary>
    public sealed class ArrayGrowI : Instr
    {
        public VReg View { get; init; } = null!;
        public VReg Count { get; init; } = null!;
        public int Size { get; init; }

        /// <summary>A str array without a most: phi_text_array_grow, which moves the rooms after the texts too.</summary>
        public bool Texts { get; init; }
    }

    /// <summary>A Linux system call (sys.Read: ...): Number in eax, the arguments in rdi, rsi, rdx, r10, r8 and r9, the answer in rax.</summary>
    public sealed class SysCallI : Instr
    {
        public long Number { get; init; }
        public string Name { get; init; } = "";
        public List<Opnd> Args { get; } = new();
        public VReg? Dst { get; init; }
    }

    /// <summary>
    /// One step no other core can come between, on the Bytes at Address: Value added, taken
    /// away, or swapped in; or (CompareSwap) Value put there when it holds Expected, Dst 1 if it was.
    /// </summary>
    public sealed class AtomicI : Instr
    {
        public Syntax.AtomicOp Op { get; init; }
        public VReg Address { get; init; } = null!;
        public Opnd Value { get; init; }
        public Opnd Expected { get; init; }
        public int Bytes { get; init; }
        public VReg? Dst { get; init; }
    }

    /// <summary>
    /// A text put in a str that grows, through its slot (its text's address, then its room): the
    /// generator's EmitGrowStoreAt, which makes room and copies it in (Value a text's address,
    /// or a number written out as text), after what's there (Append) or in its place.
    /// </summary>
    public sealed class GrowStoreI : Instr
    {
        public VReg Slot { get; init; } = null!;
        public Opnd Value { get; init; }
        public bool SourceIsString { get; init; }
        public bool Append { get; init; }
        public bool Unsigned { get; init; }
        public int? Known { get; init; }
        public bool MayCut { get; init; }

        /// <summary>Where the room is after the slot (null: a str's own, just after its address).</summary>
        public int? RoomOffset { get; init; }

        /// <summary>An element of a str array without a most: a frame place holding its .max, its room that many slots on.</summary>
        public string? Rooms { get; init; }
    }

    /// <summary>
    /// A text (or a whole number written out) put in a str with a size at Dst, Room bytes: after
    /// what's there (Append) or in its place; cut short, an error, unless MayCut.
    /// </summary>
    public sealed class FixedStoreI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public int Room { get; init; }
        public Opnd Value { get; init; }
        public bool SourceIsString { get; init; }
        public bool Append { get; init; }
        public bool Unsigned { get; init; }
        public bool MayCut { get; init; }
        public List<StoreI> Held { get; init; } = new();
    }

    /// <summary>
    /// A method's str result put where its caller said ([rbp + 16]) before its frame goes: a slot
    /// that grows (Grow), or a buffer of Room bytes (cut short, an error); Dst is the text there.
    /// </summary>
    public sealed class ResultTextI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Value { get; init; } = null!;
        public bool Grow { get; init; }
        public int Room { get; init; }
        public List<StoreI> Held { get; init; } = new();
    }

    /// <summary>Dst = 1 when Count bytes at A and B are the same (repe cmpsb; none are the same).</summary>
    public sealed class CompareBytesI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg A { get; init; } = null!;
        public VReg B { get; init; } = null!;
        public VReg Count { get; init; } = null!;
    }

    /// <summary>Count bytes copied from Src to Dst, the first first (rep movsb).</summary>
    public sealed class CopyBytesI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Src { get; init; } = null!;
        public VReg Count { get; init; } = null!;
    }

    /// <summary>Dst = where a pinned value's slot is (addr n of a method's own variable).</summary>
    public sealed class AddrOfI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Of { get; init; } = null!;
    }

    /// <summary>An interface's method of an object: from its type table, just before it (the generator's).</summary>
    public sealed class InterfaceMethodI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Receiver { get; init; } = null!;
        public Semantics.StructSymbol Interface { get; init; } = null!;
        public int Index { get; init; }
    }

    /// <summary>text has x (or a character): phi_str_has, or phi_str_has_char; Dst is 1 when it does.</summary>
    public sealed class HasTextI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public VReg Text { get; init; } = null!;
        public Opnd Value { get; init; }
        public bool Character { get; init; }
    }

    /// <summary>An error the program stops with here (the generator's EmitStop): its text and kind; never comes back.</summary>
    public sealed class StopI : Instr
    {
        public string Text { get; init; } = "";
        public int Kind { get; init; }
        public Node At { get; init; } = null!;
        public List<StoreI> Held { get; init; } = new();
    }

    /// <summary>The generator's routine for an array's elements (phi_array_clone_..., phi_array_free_...): rax the view, rdx where a clone goes.</summary>
    public sealed class ArrayRoutineI : Instr
    {
        public string Kind { get; init; } = "";
        public Semantics.PhiType Element { get; init; } = null!;
        public VReg View { get; init; } = null!;
        public VReg? Made { get; init; }
    }

    /// <summary>exit code; in a program: phi_exit with the code (the generator's), which never comes back.</summary>
    public sealed class ExitI : Instr
    {
        public Opnd Code { get; init; }
        public List<StoreI> Held { get; init; } = new();
    }

    /// <summary>A number written out as text into the method's scratch bytes (an i64, a u64, a float, a chr): Dst is where.</summary>
    public sealed class NumberTextI : Instr
    {
        public VReg Dst { get; init; } = null!;
        public Opnd Value { get; init; }
        public PrintKind Kind { get; init; }
    }

    /// <summary>A text that grows given back to the heap: the one whose address is at Place (a slot in the frame).</summary>
    public sealed class FreeTextI : Instr
    {
        public string Place { get; init; } = "";

        /// <summary>Or the text's address itself, in a value.</summary>
        public VReg? Value { get; init; }
    }

    /// <summary>throw text;: the generator's (phi_throw, with the text, the line and the file), which never comes back.</summary>
    public sealed class ThrowI : Instr
    {
        public VReg Text { get; init; } = null!;
        public Node At { get; init; } = null!;
        public List<StoreI> Held { get; init; } = new();
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

        /// <summary>The object copied from (set, assign), in rdx.</summary>
        public VReg? Second { get; init; }

        /// <summary>What it gives back (clone: the copy), in rax.</summary>
        public VReg? Dst { get; init; }
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

        /// <summary>Where its result goes (what it leaves in eax), when it's kept.</summary>
        public VReg? Dst { get; init; }
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

        /// <summary>A routine of the library's (a built-in's), which the program must be given: Label is its name.</summary>
        public bool Routine { get; init; }

        /// <summary>A call through a method value: the address it holds (worked out after the arguments, as the generator does), in place of Label.</summary>
        public VReg? Target { get; init; }
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

        /// <summary>A limit known only when it runs (how many an array has), 64 bits, in place of Limit.</summary>
        public VReg? LimitValue { get; init; }
        public Node At { get; init; } = null!;
    }

    public abstract class Terminator { }

    /// <summary>
    /// if call ... throws err: Body (the call, with its catch record set by the CatchI before
    /// this) runs next; Caught is where a throw comes to, with nothing in a register.
    /// </summary>
    public sealed class TryT : Terminator
    {
        public Block Body { get; set; } = null!;
        public Block Caught { get; set; } = null!;
    }

    /// <summary>
    /// A catch record put on the list a throw looks at (the generator's EmitIfThrows): the one
    /// around it, rsp and rbp, the methods' records, where Caught is, and how deep in the log's
    /// lock. A call of the library (phi_log_held), so it changes what a call does.
    /// </summary>
    public sealed class CatchI : Instr
    {
        public string Record { get; init; } = "";
        public Block Caught { get; init; } = null!;
    }

    /// <summary>The call came back: the catch record comes off the list.</summary>
    public sealed class UncatchI : Instr
    {
        public string Record { get; init; } = "";
    }

    /// <summary>Where a throw came to: the error copied into err, a struct in the frame at Place.</summary>
    public sealed class CaughtI : Instr
    {
        public string Place { get; init; } = "";
    }

    /// <summary>A struct kept in the frame at Place, all Size bytes of it made zero.</summary>
    public sealed class ZeroI : Instr
    {
        public string Place { get; init; } = "";
        public int Size { get; init; }
    }

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

        /// <summary>What the optimiser did with each loop, and why not more, for PHI_IR_TRACE.</summary>
        public List<(Syntax.Node At, string Note)> Notes { get; } = new();

        /// <summary>
        /// The 8-byte places at the top of the frame (rbp - 8, - 16, ...) the method keeps
        /// things in that must be in memory: its own objects, which are freed from there at its
        /// end and by a throw going past. The values the allocator finds no register for come after.
        /// </summary>
        public int Homes { get; private set; }

        public string NewHome() => $"rbp - {8 * ++Homes}";

        /// <summary>The slots of the method's own strs that grow: each one's text is freed at its end, and by a throw going past.</summary>
        public List<string> OwnedTexts { get; } = new();

        /// <summary>
        /// The views of the method's own arrays that grow and hold objects (the struct) or texts
        /// (null): those freed, then the elements' room, at its end and by a throw going past.
        /// </summary>
        public List<(string Home, Semantics.StructSymbol? Objects)> OwnedViews { get; } = new();

        /// <summary>The elements of the method's own str tables whose texts grow: each slot and its room; freed when not in their own buffer.</summary>
        public List<(string Slot, string Room)> OwnedTableTexts { get; } = new();

        /// <summary>The method's own objects: where each is kept, and its struct (freed at the end, in this order).</summary>
        public List<(string Home, Semantics.StructSymbol Struct)> Owned { get; } = new();

        /// <summary>
        /// In a program that catches errors, with objects of its own: how far below rbp the
        /// record the throw looks at is (the record before, the routine that frees the objects,
        /// and rbp, 8 bytes each), or 0.
        /// </summary>
        public int UnwindRecord { get; set; }
        public List<VReg> Values { get; } = new();

        /// <summary>A value as wide as an address: 64 bits in 64-bit code, 32 in 32-bit.</summary>
        public VReg NewAddress(string name = "") => NewValue(Semantics.PhiType.AddressSize == 8, name);

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
                    TryT t => $"try {t.Body} caught {t.Caught}",
                    _ => "(no end)",
                }).Append('\n');
            }
            return sb.ToString();
        }

        public static string Text(Instr i) => i switch
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
            CallI c => $"{c.Dst} = call {(object?)c.Target ?? c.Label}({string.Join(", ", c.Args)})",
            LoadIdxI l => $"{l.Dst} = load {l.Kind} [{l.Base} + {l.Index} * {l.Scale}]",
            StoreIdxI st => $"store {st.Kind} [{st.Base} + {st.Index} * {st.Scale}] = {st.Src}",
            SignCheckI c => $"check {c.Value}",
            CheckI c => $"check {c.Index} below {(object?)c.LimitValue ?? c.Limit}",
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
            TextI t => $"{t.Dst} = text{t.Op} {(object?)t.Literal ?? t.A}{(t.B != null ? ", " + t.B : "")}{(t.Place != null ? $" into [{t.Place}] {t.Room}" : "")}",
            CatchI c => $"catch [{c.Record}] at {c.Caught}",
            ArrayGrowI g => $"grow [{g.View}] to {g.Count} of {g.Size}",
            GrowStoreI gs => $"text [{gs.Slot}] {(gs.Append ? "++" : "is")} {gs.Value}{(gs.SourceIsString ? "" : " as a number")}",
            NumberTextI nt => $"{nt.Dst} = text of {nt.Kind} {nt.Value}",
            ExitI ex => $"exit {ex.Code}",
            X87I x => x.B != null ? $"{x.Dst} = {x.A} fprem {x.B}" : $"{x.Dst} = {x.Instruction}{(x.Rounding >= 0 ? $" ({x.Rounding})" : "")} {x.A}",
            ArrayRoutineI ar => $"array {ar.Kind} [{ar.View}]{(ar.Made != null ? $" into [{ar.Made}]" : "")}",
            StopI st => $"stop: {st.Text}",
            HasTextI ht => $"{ht.Dst} = {ht.Text} has {ht.Value}",
            InterfaceMethodI im => $"{im.Dst} = {im.Interface.Name} method {im.Index} of {im.Receiver}",
            FreeTextI ft => ft.Value != null ? $"free text {ft.Value}" : $"free text [{ft.Place}]",
            AddrOfI ao => $"{ao.Dst} = addr {ao.Of}",
            CopyBytesI cb => $"copy {cb.Count} bytes from {cb.Src} to {cb.Dst}",
            CompareBytesI cmb => $"{cmb.Dst} = {cmb.Count} bytes at {cmb.A} and {cmb.B} the same",
            ResultTextI rt => $"{rt.Dst} = result text {rt.Value}",
            FixedStoreI fs => $"text [{fs.Dst}] ({fs.Room}) {(fs.Append ? "++" : "is")} {fs.Value}{(fs.SourceIsString ? "" : " as a number")}",
            ThrowI th => $"throw {th.Text}",
            SysCallI sc => $"{sc.Dst} = sys.{sc.Name}({string.Join(", ", sc.Args)})",
            AtomicI at => $"{at.Dst} = atomic {at.Op} [{at.Address}] {at.Value}{(at.Op == Syntax.AtomicOp.CompareSwap ? " when " + at.Expected : "")}",
            UncatchI u => $"uncatch [{u.Record}]",
            CaughtI c => $"caught into [{c.Place}]",
            ZeroI z => $"zero [{z.Place}] {z.Size}",
            _ => i.GetType().Name,
        };
    }
}

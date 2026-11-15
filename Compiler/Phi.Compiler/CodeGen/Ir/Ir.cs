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
    public enum MemKind { U8, S8, U16, S16, D32, Q64 }

    /// <summary>What a call gives back: nothing, 32 bits in eax, 64 in edx:eax (a 64-bit number), or a pointer in rax.</summary>
    public enum CallResult { None, Eax, EdxEax, Rax }

    public abstract class Instr { }

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
        public VReg Value { get; init; } = null!;
        public Node At { get; init; } = null!;
        public string Text { get; init; } = "";
    }

    /// <summary>A call of a method: its arguments pushed first to last, 8 bytes each, as the generator does.</summary>
    public sealed class CallI : Instr
    {
        public string Label { get; init; } = "";
        public List<Opnd> Args { get; } = new();
        public VReg? Dst { get; init; }
        public CallResult Result { get; init; }
    }

    public abstract class Terminator { }

    public sealed class JumpT : Terminator
    {
        public Block Target { get; set; } = null!;
    }

    public sealed class BranchT : Terminator
    {
        public Cond Cond { get; init; }
        public VReg A { get; init; } = null!;
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
        public string Name { get; init; } = "";
        public List<Block> Blocks { get; } = new();
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
            AddrI a => $"{a.Dst} = addr {a.Label}",
            _ => i.GetType().Name,
        };
    }
}

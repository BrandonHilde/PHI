using Phi.Compiler.Semantics;

namespace Phi.Compiler.Syntax
{
    public abstract class Node
    {
        public Span Span { get; init; }
    }

    // ---------------------------------------------------------------- declarations

    public sealed class ProgramNode : Node
    {
        public SourceFile File { get; init; } = null!;
        public List<ClassDecl> Classes { get; } = new();
        public List<RawBlockDecl> RawBlocks { get; } = new();
        public List<StructDecl> Structs { get; } = new();
        public List<UseDecl> Uses { get; } = new();
    }

    /// <summary>use drivers.vga_text;   (loads drivers/vga_text.phi)</summary>
    public sealed class UseDecl : Node
    {
        public string Path { get; init; } = "";
    }

    /// <summary>phi.Name:Base { members }</summary>
    public sealed class ClassDecl : Node
    {
        public SourceFile File { get; init; } = null!;
        public string Name { get; init; } = "";
        public string Base { get; init; } = "";
        public Span BaseSpan { get; init; }
        public List<MethodDecl> Methods { get; } = new();

        /// <summary>Statements at class level run in order when the class starts.</summary>
        public List<Stmt> Body { get; } = new();
    }

    /// <summary>asm.Name { raw } or arm.Name { raw }</summary>
    public sealed class RawBlockDecl : Node
    {
        public SourceFile File { get; init; } = null!;
        public string Language { get; init; } = "asm";
        public string Name { get; init; } = "";
        public string Text { get; init; } = "";
        public Span TextSpan { get; init; }
    }

    /// <summary>struct.Name { type field; type field[N]; }</summary>
    public sealed class StructDecl : Node
    {
        public SourceFile File { get; init; } = null!;
        public string Name { get; init; } = "";
        public List<FieldDecl> Fields { get; } = new();
        public StructSymbol? Symbol { get; set; }
    }

    public sealed class FieldDecl : Node
    {
        public TypeRef Type { get; init; } = null!;
        public string Name { get; init; } = "";
        public Expr? Count { get; init; }
    }

    /// <summary>[Name: params] body [end: result]   or   [isr Name] body [end]</summary>
    public sealed class MethodDecl : Node
    {
        public string Name { get; init; } = "";
        public bool IsInterruptHandler { get; init; }
        public List<VarDecl> Parameters { get; } = new();
        public List<Stmt> Body { get; } = new();
        public Expr? Result { get; set; }

        public ClassDecl Owner { get; set; } = null!;
        public MethodSymbol? Symbol { get; set; }
    }

    /// <summary>A type as written: int, u16, str, Task, ptr&lt;u8&gt;</summary>
    public sealed class TypeRef : Node
    {
        public string Name { get; init; } = "";
        public TypeRef? Pointee { get; init; }

        public override string ToString() => Pointee == null ? Name : $"{Name}<{Pointee}>";
    }

    /// <summary>
    /// type name: value;          a scalar
    /// type name: v1 v2 v3;       an array
    /// type name: [N];            (or type name[N];) an empty buffer of N elements
    /// type name;                 zero
    /// </summary>
    public sealed class VarDecl : Stmt
    {
        public TypeRef Type { get; init; } = null!;
        public string Name { get; init; } = "";
        public Span NameSpan { get; init; }
        public List<Expr> Values { get; } = new();
        public Expr? BufferSize { get; init; }

        public VariableSymbol? Symbol { get; set; }
    }

    /// <summary>const type name: value;</summary>
    public sealed class ConstDecl : Stmt
    {
        public TypeRef Type { get; init; } = null!;
        public string Name { get; init; } = "";
        public Span NameSpan { get; init; }
        public Expr Value { get; init; } = null!;
    }

    // ---------------------------------------------------------------- statements

    public abstract class Stmt : Node { }

    public enum LogTarget { ScreenAndSerial, SerialOnly }

    /// <summary>log a b c;   debug a b c;</summary>
    public sealed class LogStmt : Stmt
    {
        public LogTarget Target { get; init; }
        public List<Expr> Values { get; } = new();
    }

    /// <summary>ask name;</summary>
    public sealed class AskStmt : Stmt
    {
        public NameExpr Target { get; init; } = null!;
    }

    /// <summary>exit code;   (ends QEMU through isa-debug-exit)</summary>
    public sealed class ExitStmt : Stmt
    {
        public Expr? Code { get; init; }
    }

    /// <summary>out port value;  outw port value;  outd port value;</summary>
    public sealed class OutStmt : Stmt
    {
        public int Size { get; init; } = 1;
        public Expr Port { get; set; } = null!;
        public Expr Value { get; set; } = null!;
    }

    /// <summary>free pointer;   (gives memory from new back to the heap)</summary>
    public sealed class FreeStmt : Stmt
    {
        public Expr Pointer { get; set; } = null!;
        public MethodSymbol? Releaser { get; set; }
    }

    /// <summary>unsafe ... ;;   (no bounds checks inside)</summary>
    public sealed class UnsafeStmt : Stmt
    {
        public List<Stmt> Body { get; } = new();
    }

    /// <summary>call [target is] Name[: args];</summary>
    public sealed class CallStmt : Stmt
    {
        public Expr? ResultTarget { get; init; }
        public string Callee { get; init; } = "";
        public Span CalleeSpan { get; init; }
        public List<Expr> Arguments { get; } = new();

        public object? Resolved { get; set; } // MethodSymbol, BuiltinFunction or RawBlockDecl
    }

    public sealed class IfStmt : Stmt
    {
        public Expr Condition { get; init; } = null!;
        public List<Stmt> Then { get; } = new();

        /// <summary>An elif becomes a nested IfStmt as the only statement of Else.</summary>
        public List<Stmt>? Else { get; set; }
    }

    /// <summary>
    /// while cond ... ;;
    /// while type i: start; cond; step; ... ;;
    /// </summary>
    public sealed class WhileStmt : Stmt
    {
        public VarDecl? Init { get; init; }
        public Expr Condition { get; init; } = null!;
        public Stmt? Step { get; set; }
        public List<Stmt> Body { get; } = new();
    }

    public enum AssignOp { Set, Add, Subtract, Multiply, Divide, Modulo, Power }

    /// <summary>x is 5;  x = 5;  x++ 2;  x-- y;  x** 3;  x// 2;  x%% 2;  x++;  x--;</summary>
    public sealed class AssignStmt : Stmt
    {
        public Expr Target { get; init; } = null!;
        public AssignOp Op { get; init; }
        public Expr Value { get; init; } = null!;
    }

    // ---------------------------------------------------------------- expressions

    public abstract class Expr : Node
    {
        /// <summary>Filled in by the binder.</summary>
        public PhiType Type { get; set; } = PhiType.Error;
    }

    public sealed class NumberExpr : Expr
    {
        public long Value { get; init; }
    }

    public sealed class DecimalExpr : Expr
    {
        public double Value { get; init; }
    }

    public sealed class StringExpr : Expr
    {
        public string Value { get; init; } = "";
    }

    public sealed class BoolExpr : Expr
    {
        public bool Value { get; init; }
    }

    /// <summary>A name, possibly dotted: x, Colors.Black, days.len, task.id</summary>
    public sealed class NameExpr : Expr
    {
        public string Name { get; init; } = "";

        public Symbol? Symbol { get; set; }
    }

    /// <summary>array:index  (also pointer:index and str:index)</summary>
    public sealed class IndexExpr : Expr
    {
        public Expr Target { get; init; } = null!;
        public Expr Index { get; set; } = null!;

        /// <summary>Set by the binder when the index must be checked against this many elements.</summary>
        public int? CheckLimit { get; set; }
    }

    /// <summary>tasks:i.id   (a field of an indexed struct)</summary>
    public sealed class MemberExpr : Expr
    {
        public Expr Target { get; init; } = null!;
        public string Member { get; init; } = "";
        public Span MemberSpan { get; init; }

        public FieldSymbol? Field { get; set; }
    }

    /// <summary>addr x  (the address of a variable, element or method)</summary>
    public sealed class AddrExpr : Expr
    {
        public Expr Operand { get; init; } = null!;

        /// <summary>When the operand names a method or asm block.</summary>
        public string? CodeLabel { get; set; }
    }

    /// <summary>new Type   or   new Type[count]   (32-bit kernels: memory from the heap, zeroed)</summary>
    public sealed class NewExpr : Expr
    {
        public TypeRef ElementType { get; init; } = null!;
        public Expr? Count { get; set; }

        /// <summary>Set by the binder: the number of bytes to ask for, and the method that allocates.</summary>
        public Expr Bytes { get; set; } = null!;
        public MethodSymbol? Allocator { get; set; }
    }

    /// <summary>in port, inw port, ind port</summary>
    public sealed class InExpr : Expr
    {
        public int Size { get; init; } = 1;
        public Expr Port { get; set; } = null!;
    }

    public enum UnaryOp { Negate, Not, BitNot }

    public sealed class UnaryExpr : Expr
    {
        public UnaryOp Op { get; init; }
        public Expr Operand { get; set; } = null!;
    }

    public enum BinaryOp
    {
        Add, Subtract, Multiply, Divide, Modulo,
        BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight,
        Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
        And, Or,
    }

    public sealed class BinaryExpr : Expr
    {
        public BinaryOp Op { get; init; }
        public Expr Left { get; set; } = null!;
        public Expr Right { get; set; } = null!;

        public bool IsComparison => Op is >= BinaryOp.Equal and <= BinaryOp.GreaterEqual;
        public bool IsLogical => Op is BinaryOp.And or BinaryOp.Or;
    }
}

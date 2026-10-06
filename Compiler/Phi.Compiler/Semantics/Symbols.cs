using Phi.Compiler.Syntax;

namespace Phi.Compiler.Semantics
{
    public enum TypeKind { Error, Void, Int, Byte, Bool, Str }

    /// <summary>
    /// int is 32-bit signed, byt is 8-bit unsigned, bln is 8-bit 0/1,
    /// str is a zero-terminated byte buffer with a fixed capacity.
    /// </summary>
    public sealed record PhiType(TypeKind Kind, bool IsArray = false)
    {
        public static readonly PhiType Error = new(TypeKind.Error);
        public static readonly PhiType Void = new(TypeKind.Void);
        public static readonly PhiType Int = new(TypeKind.Int);
        public static readonly PhiType Byte = new(TypeKind.Byte);
        public static readonly PhiType Bool = new(TypeKind.Bool);
        public static readonly PhiType Str = new(TypeKind.Str);

        public bool IsNumeric => !IsArray && Kind is TypeKind.Int or TypeKind.Byte or TypeKind.Bool;
        public bool IsString => !IsArray && Kind == TypeKind.Str;
        public bool IsError => Kind == TypeKind.Error;
        public PhiType Element => this with { IsArray = false };
        public PhiType AsArray => this with { IsArray = true };

        /// <summary>Bytes per element in memory.</summary>
        public int ElementSize => Kind switch
        {
            TypeKind.Int => 4,
            TypeKind.Str when IsArray => 2, // arrays of strings hold 16-bit pointers
            _ => 1,
        };

        public override string ToString()
        {
            string name = Kind switch
            {
                TypeKind.Int => "int",
                TypeKind.Byte => "byt",
                TypeKind.Bool => "bln",
                TypeKind.Str => "str",
                TypeKind.Void => "nothing",
                _ => "?",
            };
            return IsArray ? name + " array" : name;
        }
    }

    public enum UnitKind
    {
        /// <summary>The 512-byte boot sector at 0x7C00.</summary>
        Boot,

        /// <summary>Everything loaded after the boot sector, at 0x7E00.</summary>
        Kernel,
    }

    public abstract class Symbol
    {
        public string Name { get; init; } = "";
    }

    public sealed class VariableSymbol : Symbol
    {
        public PhiType Type { get; set; } = PhiType.Error;

        /// <summary>The assembly label of the variable's storage.</summary>
        public string Label { get; set; } = "";

        public VarDecl Decl { get; init; } = null!;
        public UnitKind Unit { get; init; }
        public bool IsParameter { get; init; }

        /// <summary>Declared inside a method (as a parameter or in its body).</summary>
        public bool IsLocal { get; set; }

        /// <summary>Array element count.</summary>
        public int Count { get; set; } = 1;

        /// <summary>For str (and each element of a str array): bytes of storage, including the zero terminator.</summary>
        public int Capacity { get; set; }

        /// <summary>
        /// Values known at compile time, placed directly in the data. Each is a long, or a
        /// string for str elements. Null when the declaration has to run code instead.
        /// </summary>
        public List<object>? StaticValues { get; set; }

        /// <summary>True when the declaration must emit code where it appears.</summary>
        public bool NeedsInitCode { get; set; }
    }

    public sealed class ConstantSymbol : Symbol
    {
        public long Value { get; init; }
    }

    /// <summary>x.len: the element count of an array, or the current length of a str.</summary>
    public sealed class LengthSymbol : Symbol
    {
        public VariableSymbol Of { get; init; } = null!;
    }

    public sealed class MethodSymbol : Symbol
    {
        public string Label { get; init; } = "";
        public MethodDecl Decl { get; init; } = null!;
        public UnitKind Unit { get; init; }
        public List<VariableSymbol> Parameters { get; } = new();
        public PhiType ReturnType { get; set; } = PhiType.Void;
        public bool IsHook { get; init; }
    }
}

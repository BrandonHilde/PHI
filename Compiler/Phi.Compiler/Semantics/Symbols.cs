using Phi.Compiler.Syntax;

namespace Phi.Compiler.Semantics
{
    public enum TypeKind { Error, Void, I8, U8, I16, U16, I32, U32, Bool, Str, Pointer, Struct }

    /// <summary>
    /// int is i32 and byt is u8; bln is 8-bit 0/1; str is a zero-terminated byte buffer with a
    /// fixed capacity; ptr&lt;T&gt; is a 32-bit linear address; structs are packed (no padding).
    /// <see cref="Length"/> is the element count for arrays and 0 for everything else.
    /// </summary>
    public sealed record PhiType(TypeKind Kind, int Length = 0, PhiType? Pointee = null, StructSymbol? Struct = null)
    {
        public static readonly PhiType Error = new(TypeKind.Error);
        public static readonly PhiType Void = new(TypeKind.Void);
        public static readonly PhiType I8 = new(TypeKind.I8);
        public static readonly PhiType U8 = new(TypeKind.U8);
        public static readonly PhiType I16 = new(TypeKind.I16);
        public static readonly PhiType U16 = new(TypeKind.U16);
        public static readonly PhiType I32 = new(TypeKind.I32);
        public static readonly PhiType U32 = new(TypeKind.U32);
        public static readonly PhiType Bool = new(TypeKind.Bool);
        public static readonly PhiType Str = new(TypeKind.Str);

        public static PhiType Int => I32;
        public static PhiType Byte => U8;
        public static PhiType PointerTo(PhiType pointee) => new(TypeKind.Pointer, Pointee: pointee);
        public static PhiType Of(StructSymbol s) => new(TypeKind.Struct, Struct: s);

        public bool IsArray => Length > 0;
        public bool IsError => Kind == TypeKind.Error;
        public bool IsString => !IsArray && Kind == TypeKind.Str;
        public bool IsPointer => !IsArray && Kind == TypeKind.Pointer;
        public bool IsStruct => !IsArray && Kind == TypeKind.Struct;

        /// <summary>A single value that fits in a register: integers, bln and pointers.</summary>
        public bool IsNumeric => !IsArray && Kind is >= TypeKind.I8 and <= TypeKind.Bool or TypeKind.Pointer;

        public bool IsSigned => Kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32;

        /// <summary>32-bit unsigned values: math and comparisons on them must be unsigned.</summary>
        public bool IsUnsigned32 => !IsArray && Kind is TypeKind.U32 or TypeKind.Pointer;

        public PhiType Element => this with { Length = 0 };
        public PhiType ArrayOf(int length) => this with { Length = length };

        /// <summary>Bytes taken by one element in memory.</summary>
        public int ElementSize => Kind switch
        {
            TypeKind.I8 or TypeKind.U8 or TypeKind.Bool => 1,
            TypeKind.I16 or TypeKind.U16 => 2,
            TypeKind.I32 or TypeKind.U32 or TypeKind.Pointer => 4,
            TypeKind.Str => 4,  // arrays of strings hold pointers
            TypeKind.Struct => Struct!.Size,
            _ => 0,
        };

        public override string ToString()
        {
            string name = Kind switch
            {
                TypeKind.I8 => "i8",
                TypeKind.U8 => "u8",
                TypeKind.I16 => "i16",
                TypeKind.U16 => "u16",
                TypeKind.I32 => "int",
                TypeKind.U32 => "u32",
                TypeKind.Bool => "bln",
                TypeKind.Str => "str",
                TypeKind.Pointer => $"ptr<{Pointee}>",
                TypeKind.Struct => Struct!.Name,
                TypeKind.Void => "nothing",
                _ => "?",
            };
            return IsArray ? $"{name} array" : name;
        }
    }

    public enum UnitKind
    {
        /// <summary>The 512-byte boot sector at 0x7C00 (16-bit).</summary>
        Boot,

        /// <summary>The OS classes, loaded right after the boot sector at 0x7E00 (16-bit).</summary>
        Os,

        /// <summary>The Kernel classes: a 32-bit protected-mode kernel at 0x10000.</summary>
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

        /// <summary>Array element count (1 for anything that isn't an array).</summary>
        public int Count => Type.IsArray ? Type.Length : 1;

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
        public PhiType Type { get; init; } = PhiType.Int;
    }

    /// <summary>x.len: the element count of an array, or the current length of a str.</summary>
    public sealed class LengthSymbol : Symbol
    {
        /// <summary>Known element count, for arrays.</summary>
        public int? Count { get; init; }

        /// <summary>The str whose length is measured at run time.</summary>
        public VariableSymbol? Str { get; init; }
    }

    /// <summary>task.id: a field reached from a variable by name, at a fixed offset.</summary>
    public sealed class FieldPathSymbol : Symbol
    {
        public VariableSymbol Root { get; init; } = null!;
        public int Offset { get; init; }
        public PhiType Type { get; init; } = PhiType.Error;
    }

    public sealed class StructSymbol : Symbol
    {
        public StructDecl Decl { get; init; } = null!;
        public List<FieldSymbol> Fields { get; } = new();
        public int Size { get; set; }
        public bool LayoutDone { get; set; }

        public FieldSymbol? Find(string name) => Fields.FirstOrDefault(f => f.Name == name);
    }

    public sealed class FieldSymbol : Symbol
    {
        public PhiType Type { get; set; } = PhiType.Error;
        public int Offset { get; set; }
    }

    public sealed class MethodSymbol : Symbol
    {
        public string Label { get; init; } = "";
        public MethodDecl Decl { get; init; } = null!;
        public UnitKind Unit { get; init; }
        public List<VariableSymbol> Parameters { get; } = new();
        public PhiType ReturnType { get; set; } = PhiType.Void;
        public bool IsHook { get; init; }
        public bool IsInterruptHandler { get; init; }
    }
}

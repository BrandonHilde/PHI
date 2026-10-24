using Phi.Compiler.Syntax;

namespace Phi.Compiler.Semantics
{
    public enum TypeKind { Error, Void, I8, U8, I16, U16, I32, U32, Bool, Str, Pointer, Struct, Method, Object, Enum, I64, U64, F16, F32, F64, Chr }

    /// <summary>What a method takes and returns, for method&lt;...&gt; types.</summary>
    public sealed class MethodSignature
    {
        public IReadOnlyList<PhiType> Parameters { get; }
        public PhiType Result { get; }

        public MethodSignature(IReadOnlyList<PhiType> parameters, PhiType result)
        {
            Parameters = parameters;
            Result = result;
        }

        public override bool Equals(object? other) =>
            other is MethodSignature s && s.Result == Result && s.Parameters.SequenceEqual(Parameters);

        public override int GetHashCode() => Parameters.Aggregate(Result.GetHashCode(), (h, p) => h * 31 + p.GetHashCode());

        public override string ToString() =>
            $"method<{string.Join(" ", Parameters)}{(Result.Kind == TypeKind.Void ? "" : ": " + Result)}>";
    }

    /// <summary>A call through a method variable: its value is the address to call.</summary>
    public sealed class PointerCall
    {
        public Expr Target { get; init; } = null!;
        public MethodSignature Signature { get; init; } = null!;
    }

    /// <summary>
    /// int is i32 and byt is u8; bln is 8-bit 0/1; str is a zero-terminated byte buffer with a
    /// fixed capacity; ptr&lt;T&gt; is a 32-bit linear address; structs are packed (no padding).
    /// An Object is a struct reached through the address it lives at (a `new` variable or
    /// field, an `at` variable, a struct parameter): it's used like the struct itself.
    /// <see cref="Length"/> is the element count for arrays and 0 for everything else.
    /// A <see cref="Filled"/> array (name[atmost N]) has room for Length elements and keeps how
    /// many are in it in a u32 right after them, so its elements are where any array's are.
    /// </summary>
    public sealed record PhiType(TypeKind Kind, int Length = 0, PhiType? Pointee = null, StructSymbol? Struct = null,
                                 MethodSignature? Signature = null, bool Filled = false, EnumSymbol? Enum = null)
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
        public static readonly PhiType I64 = new(TypeKind.I64);
        public static readonly PhiType U64 = new(TypeKind.U64);
        public static readonly PhiType F16 = new(TypeKind.F16);

        /// <summary>A character: a byte that log and str building write as its character, not its number.</summary>
        public static readonly PhiType Chr = new(TypeKind.Chr);
        public static readonly PhiType F32 = new(TypeKind.F32);
        public static readonly PhiType F64 = new(TypeKind.F64);

        public static PhiType Int => I32;
        public static PhiType Byte => U8;
        public static PhiType PointerTo(PhiType pointee) => new(TypeKind.Pointer, Pointee: pointee);
        public static PhiType Of(StructSymbol s) => new(TypeKind.Struct, Struct: s);
        public static PhiType MethodOf(MethodSignature s) => new(TypeKind.Method, Signature: s);
        public static PhiType ObjectOf(StructSymbol s) => new(TypeKind.Object, Struct: s);
        public static PhiType EnumOf(EnumSymbol e) => new(TypeKind.Enum, Enum: e);

        public bool IsArray => Length > 0;
        public bool IsError => Kind == TypeKind.Error;
        public bool IsString => !IsArray && Kind == TypeKind.Str;
        public bool IsPointer => !IsArray && Kind == TypeKind.Pointer;
        public bool IsStruct => !IsArray && Kind == TypeKind.Struct;

        /// <summary>A struct reached through its address: x.field works on it like on a struct.</summary>
        public bool IsObject => !IsArray && Kind == TypeKind.Object;

        /// <summary>The struct whose fields x.field reaches: for a struct or an object.</summary>
        public StructSymbol? Fields => IsStruct || IsObject ? Struct : null;

        /// <summary>The address of a method that can be called: method&lt;int: int&gt;.</summary>
        public bool IsMethod => !IsArray && Kind == TypeKind.Method;

        /// <summary>A single value that fits in a register: integers, bln, pointers and enums.</summary>
        public bool IsNumeric => !IsArray && Kind is >= TypeKind.I8 and <= TypeKind.Bool or TypeKind.Pointer or TypeKind.Enum or TypeKind.Chr;

        /// <summary>A chr: written as a character in text.</summary>
        public bool IsChar => !IsArray && Kind == TypeKind.Chr;

        /// <summary>
        /// A 64-bit integer (i64, u64). Not IsNumeric: it takes two registers (edx:eax), so only
        /// code that knows that handles it, and a 32-bit place takes one only through `as`.
        /// </summary>
        public bool IsWide => !IsArray && Kind is TypeKind.I64 or TypeKind.U64;

        /// <summary>
        /// A floating point number (f16, f32, f64). Not IsNumeric: it's worked on in the FPU
        /// (st0), and becomes a whole number only through `as`.
        /// </summary>
        public bool IsFloat => !IsArray && Kind is TypeKind.F16 or TypeKind.F32 or TypeKind.F64;

        /// <summary>One of an enum's values: a number that only its own names go into.</summary>
        public bool IsEnum => !IsArray && Kind == TypeKind.Enum;

        public bool IsSigned => Kind is TypeKind.I8 or TypeKind.I16 or TypeKind.I32 or TypeKind.Enum or TypeKind.I64;

        /// <summary>32-bit unsigned values: math and comparisons on them must be unsigned.</summary>
        public bool IsUnsigned32 => !IsArray && Kind is TypeKind.U32 or TypeKind.Pointer or TypeKind.Method;

        public PhiType Element => this with { Length = 0, Filled = false };
        public PhiType ArrayOf(int length) => this with { Length = length };

        /// <summary>Bytes taken by one element in memory.</summary>
        public int ElementSize => Kind switch
        {
            TypeKind.I8 or TypeKind.U8 or TypeKind.Bool or TypeKind.Chr => 1,
            TypeKind.I16 or TypeKind.U16 => 2,
            TypeKind.I32 or TypeKind.U32 or TypeKind.Pointer or TypeKind.Method or TypeKind.Object => 4,
            TypeKind.Str => 4,  // arrays of strings hold pointers
            TypeKind.Struct => Struct!.Size,
            TypeKind.Enum => 4,
            TypeKind.I64 or TypeKind.U64 or TypeKind.F64 => 8,
            TypeKind.F32 => 4,
            TypeKind.F16 => 2,
            _ => 0,
        };

        /// <summary>Bytes taken by the whole value: every element, and a filled array's count.</summary>
        public int ByteSize => ElementSize * Math.Max(Length, 1) + (Filled ? 4 : 0);

        /// <summary>Where a filled array keeps its count, from the start of the array.</summary>
        public int CountOffset => ElementSize * Length;

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
                TypeKind.Struct or TypeKind.Object => Struct!.Name,
                TypeKind.Enum => Enum!.Name,
                TypeKind.I64 => "i64",
                TypeKind.U64 => "u64",
                TypeKind.F16 => "f16",
                TypeKind.Chr => "chr",
                TypeKind.F32 => "f32",
                TypeKind.F64 => "f64",
                TypeKind.Method => Signature!.ToString(),
                TypeKind.Void => "nothing",
                _ => "?",
            };
            return Filled ? $"{name} array (at most {Length})" : IsArray ? $"{name} array" : name;
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

        /// <summary>The Program classes: a 32-bit user program (ring 3) at 0x40000000, run by a kernel.</summary>
        Program,
    }

    public abstract class Symbol
    {
        public string Name { get; init; } = "";
    }

    /// <summary>What a variable holding an object does with it.</summary>
    public enum Ownership
    {
        /// <summary>Not an object.</summary>
        None,

        /// <summary>new T x: PHI allocates it, and frees it when x goes away.</summary>
        Owned,

        /// <summary>new T x at address: memory that already exists; never allocated or freed.</summary>
        At,

        /// <summary>A struct parameter: the caller's object, for the length of the call.</summary>
        Borrowed,
    }

    public sealed class VariableSymbol : Symbol
    {
        public PhiType Type { get; set; } = PhiType.Error;

        /// <summary>The assembly label of the variable's storage.</summary>
        public string Label { get; set; } = "";

        public VarDecl Decl { get; init; } = null!;
        public UnitKind Unit { get; init; }
        public bool IsParameter { get; init; }
        public Ownership Ownership { get; set; }

        /// <summary>Declared inside a method (as a parameter or in its body).</summary>
        public bool IsLocal { get; set; }

        /// <summary>Array element count (1 for anything that isn't an array).</summary>
        public int Count => Type.IsArray ? Type.Length : 1;

        /// <summary>For str (and each element of a str array): bytes of storage, including the zero terminator.</summary>
        public int Capacity { get; set; }

        /// <summary>
        /// A str declared without a size, in a 32-bit kernel or a user program: its text is on the
        /// heap and grows as it's written, up to <see cref="Capacity"/> - 1 characters. Its own
        /// storage is two u32s: the text's address (0 until something is written) and its room.
        /// A class variable's constant text starts in the program's data instead; the top bit of
        /// its room says so, so it's never freed.
        /// </summary>
        public bool Grows { get; set; }

        /// <summary>
        /// A str array whose elements grow, in a 32-bit kernel or a user program. Its storage is the
        /// table of each element's text (so it's still a ptr&lt;ptr&lt;u8&gt;&gt;), then each element's
        /// room. An element starts in a buffer of <see cref="Capacity"/> bytes of its own (the top bit
        /// of its room says so) and moves to the heap when it needs more.
        /// </summary>
        public bool GrowingElements { get; set; }

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

    /// <summary>x.len: the element count of an array, or the current length of a str; x.max: the most it can hold.</summary>
    public sealed class LengthSymbol : Symbol
    {
        /// <summary>Known element count, for arrays.</summary>
        public int? Count { get; init; }

        /// <summary>The str whose length is measured at run time.</summary>
        public VariableSymbol? Str { get; init; }

        /// <summary>The [atmost N] array whose count is read at run time.</summary>
        public Expr? Filled { get; init; }
    }

    /// <summary>task.id: a field reached from a variable by name, at a fixed offset.</summary>
    public sealed class FieldPathSymbol : Symbol
    {
        public VariableSymbol Root { get; init; } = null!;
        public int Offset { get; init; }
        public PhiType Type { get; init; } = PhiType.Error;
    }

    /// <summary>A field of the struct a struct method was called on, by name: self:0.field.</summary>
    public sealed class SelfFieldSymbol : Symbol
    {
        public VariableSymbol Self { get; init; } = null!;
        public int Offset { get; init; }
        public PhiType Type { get; init; } = PhiType.Error;
    }

    /// <summary>
    /// A field reached through one or more objects: start at Root's storage plus BaseOffset,
    /// then for each hop read the address stored there and add the hop. pt.x is Root pt, hops
    /// [x]; a.next.value is Root a, hops [next, value].
    /// </summary>
    public sealed class PathSymbol : Symbol
    {
        public VariableSymbol Root { get; init; } = null!;
        public int BaseOffset { get; init; }
        public List<int> Hops { get; init; } = new();
        public PhiType Type { get; init; } = PhiType.Error;
    }

    /// <summary>A `new` field inside a struct (directly or in an inline struct field): where, and of what.</summary>
    public readonly record struct OwnedSlot(int Offset, StructSymbol Of);

    /// <summary>enum.Name: its values by name, in the order they were written.</summary>
    public sealed class EnumSymbol : Symbol
    {
        public EnumDecl Decl { get; init; } = null!;
        public List<(string Name, long Value)> Members { get; } = new();
    }

    public sealed class StructSymbol : Symbol
    {
        public StructDecl Decl { get; init; } = null!;
        public List<FieldSymbol> Fields { get; } = new();
        public int Size { get; set; }
        public bool LayoutDone { get; set; }

        /// <summary>
        /// Every object this struct owns, in address order: its `new` fields, including those of
        /// struct fields stored inside it. Freeing, copying or comparing one of these structs also
        /// frees, copies or compares these.
        /// </summary>
        public List<OwnedSlot> OwnedSlots { get; } = new();
        public bool OwnsObjects => OwnedSlots.Count > 0;

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

        /// <summary>The method's own variables (not its parameters), which live in its stack frame.</summary>
        public List<VariableSymbol> Locals { get; } = new();

        /// <summary>The values of its return statements, for working out <see cref="ResultCapacity"/>.</summary>
        public List<Expr> ReturnValues { get; } = new();

        /// <summary>
        /// For a method that returns a str: the bytes its result can take, including the zero.
        /// The caller gives it a buffer this big to copy the result into.
        /// </summary>
        public int ResultCapacity { get; set; }
        public PhiType ReturnType { get; set; } = PhiType.Void;

        /// <summary>
        /// The [end: value] has been bound, so ReturnType is known. A call can be bound before
        /// the method it calls, so this happens on first use (see Binder.ReturnTypeOf).
        /// </summary>
        public bool ResultBound { get; set; }
        public bool ResultBinding { get; set; }

        /// <summary>The [end: value] uses the method's own result, so its type was assumed to be a number.</summary>
        public bool ReturnTypeAssumed { get; set; }
        public bool IsHook { get; init; }
        public bool IsInterruptHandler { get; init; }
    }
}

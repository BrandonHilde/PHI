using System.Text;
using System.Text.RegularExpressions;
using Phi.Compiler.Libraries;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen
{
    public sealed class AsmUnit
    {
        public UnitKind Kind { get; init; }
        public string Name { get; init; } = "";
        public string Text { get; init; } = "";

        /// <summary>What a debugger needs to know of the code, in a --debug build of a program.</summary>
        public DebugInfo? Debug { get; init; }

        /// <summary>A library's build: the lists its code comes with, which the object file doesn't say.</summary>
        public LibraryUnit? Library { get; init; }
    }

    /// <summary>What generating a library leaves besides its assembly (which NASM makes an object file of).</summary>
    public sealed class LibraryUnit
    {
        public CompiledTarget Target { get; } = new();
        public List<string> Exports { get; } = new();
        public List<(string Label, int Line, string File)> Lines { get; } = new();
    }

    /// <summary>
    /// Generates NASM source: 16-bit real mode for the Boot and Os units, 32-bit protected
    /// mode for the Kernel unit. Values are computed in eax. Class variables live at fixed
    /// labels; a method's parameters and variables live in its stack frame (ebp), so methods
    /// can call themselves and be used from interrupt handlers. In 32-bit code a method's busiest
    /// variables live in esi and edi instead (see "variables in registers"); library routines may
    /// change any general-purpose register except ebp, so they're stored around those. Everything
    /// else is read and written in memory each time (as device memory needs).
    /// </summary>
    public sealed class X86Generator
    {
        /// <summary>Where the boot sector loads the 16-bit OS classes, right after itself.</summary>
        public const int OsAddress = 0x7E00;

        /// <summary>Where the loader puts a 32-bit kernel (see docs/memory-map.md).</summary>
        public const int KernelAddress = 0x100000;

        /// <summary>Where a user program runs, in its own address space.</summary>
        public const uint ProgramAddress = 0x40000000;

        /// <summary>Where a Linux program is loaded: the usual address for a 32-bit ELF executable.</summary>
        public const uint LinuxAddress = 0x08048000;

        /// <summary>Where a 64-bit Linux program is loaded (below 2 GB, so a label fits in an instruction).</summary>
        public const uint Linux64Address = 0x400000;

        /// <summary>Where a Windows program is loaded: the usual image base for a 32-bit .exe.</summary>
        public const uint WindowsAddress = 0x400000;

        readonly BoundProgram program;
        readonly BoundUnit unit;
        readonly DiagnosticBag diagnostics;
        readonly bool bits32;

        /// <summary>
        /// 64-bit code (a 64-bit Linux program): addresses, pointers and the stack are 64 bits,
        /// and every number keeps its size (int stays 32 bits). bits32 is set too: what it means
        /// beyond the registers (a heap, objects, 32-bit instructions) is the same.
        /// </summary>
        readonly bool bits64;
        readonly string libraryTarget;

        /// <summary>
        /// A --debug build: what the code is, for a debugger. Such a build keeps every variable in
        /// memory and writes every method out, each with a frame, so what it says is always so.
        /// </summary>
        readonly DebugInfo? debugInfo;

        StringBuilder code = new();
        readonly HashSet<string> librarySymbols = new();
        readonly Dictionary<string, string> stringLabels = new();
        int labelCount;
        SourceFile file;
        (SourceFile?, int) lastComment;

        X86Generator(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics, bool debug = false)
        {
            debugInfo = debug && unit.Kind == UnitKind.Program ? new DebugInfo() : null;
            this.program = program;
            this.unit = unit;
            this.diagnostics = diagnostics;
            bits32 = unit.Kind is UnitKind.Kernel or UnitKind.Program;
            bits64 = unit.Kind == UnitKind.Program && program.Host.Is64();
            libraryTarget = Binder.TargetOf(unit.Kind, program.Host);
            file = program.File;
        }

        /// <summary>Reports what a unit uses that its code can't do yet (64-bit code, for now), without generating it.</summary>
        public static void Check(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics)
        {
            var gen = new X86Generator(program, unit, diagnostics);
            if (gen.bits64) gen.Check64();
        }

        public static AsmUnit Generate(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics, bool debug = false)
        {
            var gen = new X86Generator(program, unit, diagnostics, debug);
            string text = gen.Build();
            return new AsmUnit
            {
                Kind = unit.Kind,
                Name = unit.Kind switch { UnitKind.Boot => "boot", UnitKind.Os => "os", UnitKind.Program => "program", _ => "kernel" },
                Text = text,
                Debug = gen.debugInfo,
                Library = gen.libraryUnit,
            };
        }

        // ================================================================ layout

        // ================================================================ 64-bit code, so far

        /// <summary>
        /// What 64-bit programs can do so far (S10 is teaching the generator the rest): numbers
        /// of up to 32 bits (and bln, chr, enums), pointers, structs made of those (in place, or
        /// through a pointer), arrays of them kept in place, the heap (new and free), methods
        /// that take and give numbers and pointers, calls, if, while, when, log and exit.
        /// Anything else is an error naming it, rather than code that's wrong.
        /// </summary>
        void Check64()
        {
            var reported = new HashSet<int>();
            void Not(Node at, string what)
            {
                if (reported.Add(at.Span.Start))
                    diagnostics.Error(file, at.Span, $"{what} isn't in 64-bit programs yet");
            }

            static bool NumberKind(PhiType t) =>
                t.Kind is TypeKind.I8 or TypeKind.U8 or TypeKind.I16 or TypeKind.U16 or TypeKind.I32 or TypeKind.U32
                    or TypeKind.Bool or TypeKind.Chr or TypeKind.Enum or TypeKind.Lock
                    or TypeKind.I64 or TypeKind.U64 or TypeKind.F16 or TypeKind.F32 or TypeKind.F64;
            static bool Number(PhiType t) => !t.IsArray && NumberKind(t);
            // a number of up to 32 bits: what an index, and the number in pointer math, can be
            static bool Small(PhiType t) => Number(t) && !t.IsWide && !t.IsFloat;
            // a pointer to numbers (a ptr<u8> is text too), to a pointer, or to a struct
            static bool Pointer(PhiType t) => t.IsPointer && !t.IsArray && t.Pointee is { IsArray: false } p
                                              && (NumberKind(p) || p.IsPointer || p.IsStruct);
            static bool Value(PhiType t) => Number(t) || Pointer(t) || MethodType(t);
            // a method value (method<int: str> say: addr Say;) of a method that takes and gives what
            // 64-bit code can
            static bool MethodType(PhiType t) => t.Kind == TypeKind.Method && !t.IsArray && t.Signature is MethodSignature s
                                                 && s.Parameters.All(Given) && (s.Result.Kind == TypeKind.Void || Given(s.Result));
            // a struct of what can be kept, with no objects in it
            static bool Plain(StructSymbol s) => !s.OwnsObjects && s.Fields.All(f => Kept(f.Type) || f.Type.IsInlineText || TextsInPlace(f.Type));
            // what a variable or field can be: a value, a plain struct, or an array of either
            // kept in place (not text: that's still to come)
            static bool Kept(PhiType t) =>
                t.Kind != TypeKind.Str && !t.Open
                && (Value(t) || t.IsStruct && Plain(t.Struct!)
                    || t.IsArray && (NumberKind(t) || Pointer(t.Element) || t.Kind == TypeKind.Struct && Plain(t.Struct!)));
            // a str of its own: with a size, one that grows, or the caller's (a str target; parameter)
            static bool Text(PhiType t) => t.IsString && !t.IsInlineText && !t.IsHeapText;
            // text that's set and built where it is: a str of its own, or a str field (in place,
            // str name: [N];, or on the heap, new str bio;)
            static bool Str(PhiType t) => Text(t) || t.IsInlineText || t.IsHeapText;
            // an array of strs with a table of where each one's text is, fixed or [atmost N] (one
            // that grows: a variable is let through by KeptVar, a struct's field by FieldArray)
            static bool TextArray(PhiType t) => t.TextTable && !t.Open;
            // an array of texts kept in place (str codes[atmost 2]: [3];): each element is its bytes
            static bool TextsInPlace(PhiType t) => t.IsArray && !t.Open && t.Kind == TypeKind.Str && t.Room > 0;
            // a struct objects can be of (new Point a;), or an interface: its fields can be kept, are
            // text, are objects of such structs (or fixed or [atmost N] arrays of them), or are
            // arrays without a size (FieldArray)
            static bool ObjectStruct(StructSymbol s, HashSet<StructSymbol>? seen = null)
            {
                seen ??= new();
                if (!seen.Add(s)) return true;      // (a list's next: it's being checked already)
                return s.Fields.All(f => Kept(f.Type) || f.Type.IsInlineText || TextsInPlace(f.Type) || f.Type.IsHeapText
                                         || (f.Type.IsObject || f.Type.IsStruct || f.Type.IsObjectArray)
                                            && (!f.Type.IsArray || f.Type.IsObjectArray) && ObjectStruct(f.Type.Struct!, seen)
                                         || FieldArray(f.Type));
            }
            // a struct's array without a size (int scores[]; in a struct): of numbers, pointers,
            // structs of values, strs or objects
            static bool FieldArray(PhiType t) => t.Open && (ValueArray(t) || StructArray(t) || t.TextTable
                                                            || t.IsObjectArray && ObjectStruct(t.Struct!));
            // an object (or a struct parameter, which is the caller's object) of such a struct
            static bool Object(PhiType t) => (t.IsObject || t.IsStruct) && !t.IsArray && ObjectStruct(t.Struct!);
            // an array of objects (new Node list[4];, [atmost N], or one that grows)
            static bool ObjectArray(PhiType t) => t.IsObjectArray && ObjectStruct(t.Struct!);
            // an array copied or compared as a whole (b is a; if b is a)
            static bool WholeArray(PhiType t) => ValueArray(t) || StructArray(t) || ObjectArray(t) || t.TextTable || TextsInPlace(t);
            // what a field can be
            static bool Field(PhiType t) => Kept(t) || t.IsInlineText || TextsInPlace(t) || t.IsHeapText || Object(t) || ObjectArray(t) || FieldArray(t);
            static bool KeptVar(VariableSymbol v) =>
                v.Ownership == Ownership.None && !v.GrowingArray
                && (Text(v.Type) || TextArray(v.Type) || TextsInPlace(v.Type)
                    || !v.GrowingElements && Kept(v.Type) && !v.Grows && !v.TextRef
                    || v.IsParameter && v.Type.Open && (ValueArray(v.Type) && v.Type.Kind != TypeKind.Str || StructArray(v.Type) || v.Type.TextTable))
                || v.IsParameter && v.Type.Open && ObjectArray(v.Type)
                || v.Ownership is Ownership.Owned or Ownership.Borrowed && Object(v.Type)
                || v.Ownership == Ownership.Owned && !v.IsParameter && ObjectArray(v.Type)
                // an array that grows (int scores[];), of numbers, pointers or structs of them
                || v.GrowingArray && v.Ownership == Ownership.None && !v.IsParameter && (ValueArray(v.Type) || StructArray(v.Type) || v.Type.TextTable);
            // what a method takes and gives: also an array of numbers or pointers, as a parameter,
            // and objects
            static bool Given(PhiType t) => Value(t) || Text(t) || t.Open && (ValueArray(t) && t.Kind != TypeKind.Str || StructArray(t) || t.TextTable || ObjectArray(t)) || Object(t);
            // an array whose elements are numbers or pointers (what ++, .len is and while each work on so far)
            static bool ValueArray(PhiType t) => t.IsArray && (NumberKind(t) || Pointer(t.Element));
            // an array of structs of values (fixed, [atmost N], or one that grows)
            static bool StructArray(PhiType t) => t.IsArray && t.Kind == TypeKind.Struct && Plain(t.Struct!);

            bool Callable(object? callee) => callee switch
            {
                MethodSymbol m => m.Parameters.All(p => Given(p.Type)) && (m.ReturnType.Kind == TypeKind.Void || Given(m.ReturnType))
                                  && m.Locals.All(KeptVar) && !m.IsInterruptHandler
                                  && (m.Decl.Owner.ForStruct?.Symbol is not StructSymbol s || ObjectStruct(s)),
                // the system calls (the binder took only those the runtime has)
                BuiltinFunction b => b.Label.StartsWith("OS_") || b.Label == "Bootloader_WaitForKeyPress" || b.FloatInstruction != null,
                RawBlockDecl => true,      // (the binder took the x64 one, or said there isn't one)
                SysCallDecl or OutsideFunctionDecl => true,
                PointerCall p => MethodType(PhiType.MethodOf(p.Signature)),
                _ => false,
            };

            // where a value is kept that 64-bit code can reach: a variable, a field, an element
            // (checking the indexes and pointers on the way)
            bool Place(Expr e)
            {
                switch (e)
                {
                    case NameExpr { Symbol: VariableSymbol v }:
                        return KeptVar(v);
                    case NameExpr { Symbol: FieldPathSymbol f }:
                        return KeptVar(f.Root) && (Field(f.Type) || TextArray(f.Type) || ObjectArray(f.Type));
                    case NameExpr { Symbol: PathSymbol p }:
                        // through pointers and objects
                        return KeptVar(p.Root) && Field(p.Type);
                    case NameExpr { Symbol: SelfFieldSymbol f }:
                        // in a struct's method: through the struct it was called on
                        return Field(f.Type);
                    case IndexExpr ix when Small(ix.Index.Type):
                        Expression(ix.Index);
                        if (Pointer(ix.Target.Type))
                        {
                            Expression(ix.Target);
                            return true;
                        }
                        // an element of an array kept in place, or a character of a str variable
                        // or of an element of an array of strs
                        return (ix.Target.Type.IsArray || Str(ix.Target.Type) && ix.Target is NameExpr or IndexExpr or MemberExpr) && Place(ix.Target);
                    case MemberExpr { Call: null, Field: not null } m when Field(m.Type):
                        if (Pointer(m.Target.Type))
                        {
                            Expression(m.Target);
                            return true;
                        }
                        return Place(m.Target);
                    default:
                        return false;
                }
            }

            void Expression(Expr e)
            {
                switch (e)
                {
                    case NumberExpr or DecimalExpr or BoolExpr or SizeofExpr or StringExpr:
                        return;
                    case NameExpr { Symbol: ConstantSymbol }:
                        return;
                    case TextExpr text:
                        foreach (Expr part in text.Parts) Expression(part);
                        return;
                    case NameExpr { Symbol: LengthSymbol { Str: VariableSymbol s } } when KeptVar(s):
                        return;
                    case NameExpr { Symbol: LengthSymbol { TextRef: VariableSymbol s } } when KeptVar(s):
                        return;
                    case MemberExpr { TextLength: true } length:
                        Expression(length.Target);
                        return;
                    // atomic x is v when old, as a value: the variable, field or element in memory
                    case AtomicExpr swap:
                        if (!Place(swap.Target)) Not(swap.Target, A(swap.Target.Type));
                        Expression(swap.Value);
                        Expression(swap.Expected);
                        return;
                    case HasExpr { Kind: HasKind.Text or HasKind.Character } has:
                        Expression(has.Collection);
                        Expression(has.Value);
                        return;
                    // an array has a value: numbers and pointers, texts, objects, structs
                    case HasExpr has when has.Kind switch
                    {
                        HasKind.Numbers => Number(has.Collection.Type.Element) || Pointer(has.Collection.Type.Element),
                        HasKind.Texts => has.Collection.Type.TextTable || TextsInPlace(has.Collection.Type),
                        HasKind.Objects => ObjectArray(has.Collection.Type),
                        _ => StructArray(has.Collection.Type),
                    }:
                        if (!Place(has.Collection)) Not(has.Collection, A(has.Collection.Type));
                        Expression(has.Value);
                        return;
                    case BinaryExpr { ArrayCompare: true } arrays when WholeArray(arrays.Left.Type):
                        if (!Place(arrays.Left)) Not(arrays.Left, A(arrays.Left.Type));
                        if (!Place(arrays.Right)) Not(arrays.Right, A(arrays.Right.Type));
                        return;
                    case BinaryExpr { ObjectCompare: StructSymbol compared } same when ObjectStruct(compared):
                        // (is empty: whether an object field or a call's result has one)
                        foreach (Expr side in new[] { same.Left, same.Right })
                            if (side is EmptyExpr) continue;
                            else if (Binder.IsObjectCall(side)) Expression(side);
                            else if (!Place(side)) Not(side, A(side.Type));
                        return;
                    case BinaryExpr { IsComparison: true } same when same.Left.Type.IsString && same.Right.Type.IsString:
                        Expression(same.Left);
                        Expression(same.Right);
                        return;
                    case NameExpr { Call: CallExpr c }:
                        Call(c, c.Resolved, c.Arguments);
                        return;
                    case MemberExpr { Call: CallExpr c }:
                        Call(c, c.Resolved, c.Arguments);
                        return;
                    case CallExpr c:
                        Call(c, c.Resolved, c.Arguments);
                        return;
                    case NameExpr { Symbol: LengthSymbol { Count: int } }:
                        return;
                    case NameExpr { Symbol: LengthSymbol { Filled: Expr filled } } when Place(filled):
                        return;
                    case NameExpr { Symbol: LengthSymbol { Text: Expr text } } when Place(text):
                        return;
                    case NameExpr { Symbol: LengthSymbol { View: Expr view } } when Place(view):
                        return;
                    case NameExpr { Symbol: LengthSymbol { Open: VariableSymbol view } } when KeptVar(view):
                        return;
                    case MemberExpr { Count: int }:
                        return;
                    case MemberExpr { FilledLength: true, Target: var counted } when Place(counted):
                        return;
                    case NameExpr { Symbol: VariableSymbol or FieldPathSymbol or PathSymbol or SelfFieldSymbol } or IndexExpr or MemberExpr { Call: null }:
                        if (!Place(e)) Not(e, Number(e.Type) ? Kind(e) : A(e.Type));
                        return;
                    case AddrExpr { Operand: var operand } when Place(operand):
                        return;
                    case AddrExpr { CodeLabel: not null }:
                        return;     // (a method's address: a method value)
                    case IsTypeExpr test:
                        Expression(test.Value);
                        return;
                    case InterfaceMethodExpr method:
                        // (the method an interface value's struct has, from its type table)
                        Expression(method.Receiver);
                        return;
                    case NewExpr n:
                        if (n.Count != null) Expression(n.Count);
                        if (n.Allocator == null || !Callable(n.Allocator)) Not(n, "new");
                        return;
                    case BinaryExpr { IsComparison: true } none when MethodType(none.Left.Type) && ConstantFolder.Evaluate(none.Right) == 0:
                        // (is a method value there? if f is 0)
                        Expression(none.Left);
                        return;
                    case BinaryExpr b when Value(b.Left.Type) && Value(b.Right.Type)
                                           && (Number(b.Left.Type) && Number(b.Right.Type) && !IsAddress(b.Left) && !IsAddress(b.Right)
                                               // (pointer math, and comparing pointers, with numbers of up to 32 bits)
                                               || (b.Op is BinaryOp.Add or BinaryOp.Subtract || b.IsComparison)
                                                  && (Pointer(b.Left.Type) || Small(b.Left.Type)) && (Pointer(b.Right.Type) || Small(b.Right.Type))):
                        Expression(b.Left);
                        Expression(b.Right);
                        return;
                    case UnaryExpr u when Number(u.Operand.Type):
                        Expression(u.Operand);
                        return;
                    case CheckedNumberExpr c:
                        Expression(c.Operand);
                        return;
                    case PointerViewExpr view:
                        Expression(view.Pointer);
                        Expression(view.Count);
                        return;
                    case CastExpr c when Number(c.Type) && Number(c.Operand.Type)
                                         || (Pointer(c.Type) || Small(c.Type) || c.Type.IsWide) && (Pointer(c.Operand.Type) || Small(c.Operand.Type) || c.Operand.Type.IsWide)
                                         || MethodType(c.Type) && MethodType(c.Operand.Type):     // (the same address, called another way)
                        Expression(c.Operand);
                        return;
                    default:
                        Not(e, Number(e.Type) && e is not (StringExpr or TextExpr) ? Kind(e) : A(e.Type));
                        return;
                }
            }

            void Call(Node at, object? callee, List<Expr> arguments)
            {
                if (!Callable(callee))
                {
                    Not(at, callee is BuiltinFunction b ? b.Name : "calling a method that takes, gives or keeps what 64-bit code can't yet");
                    return;
                }
                if (callee is PointerCall through) Expression(through.Target);
                foreach (Expr a in arguments) Expression(a);
            }

            void Statement(Stmt s)
            {
                switch (s)
                {
                    case ConstDecl or BreakStmt or ContinueStmt:
                        return;
                    case VarDecl d when d.Symbol is VariableSymbol v && KeptVar(v) && !d.IsNew && d.AtAddress == null:
                        foreach (Expr value in d.Values) Expression(value);
                        return;
                    case VarDecl { IsNew: true, AtAddress: null } d when d.Symbol is VariableSymbol v && KeptVar(v):
                        // a new object: from one value, or field by field (or the object an
                        // interface value holds, narrowed to its struct)
                        if (d.Narrows != null) Expression(d.Narrows);
                        if (d.Source != null) Statement(d.Source);
                        foreach (Stmt field in d.FieldValues) Statement(field);
                        return;
                    case AssignStmt a when (Value(a.Target.Type) || Str(a.Target.Type)) && Place(a.Target):
                        Expression(a.Value);
                        return;
                    case ObjectAssignStmt o when ObjectStruct(o.Struct):
                        if (!Place(o.Target)) Not(o.Target, A(o.Target.Type));
                        if (o.Kind == ObjectAssignKind.Move) Expression(o.Value);
                        else if (o.Kind == ObjectAssignKind.Copy && !Place(o.Value)) Not(o.Value, A(o.Value.Type));
                        return;
                    case ArrayAssignStmt copy when WholeArray(copy.Target.Type) && Place(copy.Target):
                        if (!Place(copy.Source)) Not(copy.Source, A(copy.Source.Type));
                        return;
                    case TakesStmt t when t.Struct != null && ObjectStruct(t.Struct):
                        if (!Place(t.Source)) Not(t.Source, A(t.Source.Type));
                        if (!Place(t.Target)) Not(t.Target, A(t.Target.Type));
                        return;
                    case FreeStmt { Owner: StructSymbol owner } f when ObjectStruct(owner):
                        Expression(f.Pointer);
                        return;
                    case StringBuildStmt build when Str(build.Target.Type) && Place(build.Target):
                        foreach (Expr part in build.Parts) Expression(part);
                        return;
                    case AppendStmt add when (ValueArray(add.Target.Type) || add.Target.Type.TextTable || TextsInPlace(add.Target.Type) || StructArray(add.Target.Type) || ObjectArray(add.Target.Type)) && Place(add.Target):
                        foreach (Expr value in add.Values) Expression(value);
                        return;
                    case SetLengthStmt length when Place(length.Array):
                        Expression(length.Value);
                        return;
                    case WhileEachStmt each when (ValueArray(each.Array.Type) || each.Array.Type.TextTable || TextsInPlace(each.Array.Type) || StructArray(each.Array.Type) || ObjectArray(each.Array.Type)) && Place(each.Array):
                        foreach (Stmt t in each.Body) Statement(t);
                        return;
                    case FreeStmt { Owner: null, Releaser: not null } f:
                        Expression(f.Pointer);
                        return;
                    case LogStmt log:
                        foreach (Expr value in log.Values)
                        {
                            if (value is StringExpr) continue;
                            // a ptr<u8> prints as its text; another pointer would print half an address
                            if (Pointer(value.Type) && value.Type.Pointee != PhiType.U8) Not(value, $"printing {A(value.Type)}");
                            else Expression(value);
                        }
                        return;
                    case CallStmt c:
                        if (c.ResultTarget != null && !(Given(c.ResultTarget.Type) && Place(c.ResultTarget)))
                            Not(c, "keeping this kind of result");
                        Call(c, c.Resolved, c.Arguments);
                        return;
                    case ThrowStmt t:
                        if (t.Text != null) Expression(t.Text);
                        return;
                    case AskStmt ask when ask.Target.Symbol is VariableSymbol { TextRef: false } v && Text(v.Type) && KeptVar(v):
                        return;
                    case IfStmt { Condition: ThrowsExpr throws } i:
                        // if call ... throws err: the call, then err (an Error) and what's done with it
                        Statement(throws.Call);
                        foreach (Stmt t in i.Then) Statement(t);
                        foreach (Stmt t in i.Else ?? new()) Statement(t);
                        return;
                    case IfStmt i:
                        Expression(i.Condition);
                        foreach (Stmt t in i.Then) Statement(t);
                        foreach (Stmt t in i.Else ?? new()) Statement(t);
                        return;
                    case WhileStmt w:
                        if (w.Init != null) Statement(w.Init);
                        Expression(w.Condition);
                        if (w.Step != null) Statement(w.Step);
                        foreach (Stmt t in w.Body) Statement(t);
                        return;
                    case WhenStmt w when Number(w.Subject.Type) || Object(w.Subject.Type) || Text(w.Subject.Type):
                        Expression(w.Subject);
                        foreach (WhenBranch branch in w.Branches)
                        {
                            foreach (Expr v in branch.Values) Expression(v);
                            foreach (Stmt t in branch.Body) Statement(t);
                        }
                        foreach (Stmt t in w.Else ?? new()) Statement(t);
                        return;
                    case ReturnStmt ret:
                        if (ret.Value != null) Expression(ret.Value);
                        return;
                    case ExitStmt e:
                        if (e.Code != null) Expression(e.Code);
                        return;
                    case AtomicStmt atomic:
                        if (!Place(atomic.Target)) Not(atomic.Target, A(atomic.Target.Type));
                        Expression(atomic.Value);
                        if (atomic.Expected != null) Expression(atomic.Expected);
                        return;
                    case LockedStmt locked:
                        if (!Place(locked.Lock)) Not(locked.Lock, A(locked.Lock.Type));
                        foreach (Stmt t in locked.Body) Statement(t);
                        return;
                    case UnsafeStmt u:
                        foreach (Stmt t in u.Body) Statement(t);
                        return;
                    default:
                        Not(s, Kind(s));
                        return;
                }
            }

            foreach (ClassDecl cls in unit.Classes)
            {
                file = cls.File;
                foreach (Stmt s in cls.Body) Statement(s);
            }
            foreach (MethodSymbol m in unit.Methods)
            {
                file = m.Decl.Owner.File;
                foreach (VariableSymbol v in m.Parameters.Where(v => !Given(v.Type)).Concat(m.Locals.Where(v => !KeptVar(v))))
                    Not(v.Decl, $"{A(v.Type)} variable");
                if (m.ReturnType.Kind != TypeKind.Void && !Given(m.ReturnType)) Not(m.Decl, $"a method that gives {A(m.ReturnType)}");
                foreach (Stmt s in m.Decl.Body) Statement(s);
                if (m.Decl.Result != null) Expression(m.Decl.Result);
            }
            file = program.File;
        }

        static string A(PhiType t)
        {
            string name = t.ToString();
            return ("aeio".Contains(name[0]) ? "an " : "a ") + name;    // a u8, an int
        }

        /// <summary>What a statement or expression is, in words: for saying it isn't in 64-bit code yet.</summary>
        static string Kind(Node n) => n switch
        {
            StringBuildStmt => "building text",
            AppendStmt => "adding to an array",
            ArrayAssignStmt => "copying a whole array",
            ObjectAssignStmt => "copying an object",
            TakesStmt => "takes",
            WhileEachStmt => "while each",
            SetLengthStmt => "setting an array's .len",
            AtomicStmt or AtomicExpr => "atomic",
            LockedStmt => "locked",
            ThrowStmt => "throw",
            IfStmt { Condition: ThrowsExpr } or ThrowsExpr => "catching errors",
            AskStmt => "ask",
            OutStmt or InExpr => "ports",
            FreeStmt => "free",
            AssignStmt => "assigning to this",
            VarDecl { IsNew: true } => "new",
            IndexExpr => "an index",
            MemberExpr => "a field",
            AddrExpr => "addr",
            SizeofExpr => "sizeof",
            HasExpr => "has",
            TextExpr => "text in parentheses",
            StringExpr => "text",
            NewExpr => "new",
            EmptyExpr => "empty",
            IsTypeExpr => "is Struct name",
            _ => n.GetType().Name,
        };

        string Build()
        {
            if (bits64)
            {
                Check64();
                if (diagnostics.HasErrors) return "";
            }

            if (program.Library != null) return BuildLibrary();

            // every 32-bit kernel gets an interrupt table, so exceptions show the panic screen
            if (unit.Kind == UnitKind.Kernel) Need("phi_interrupts_init");

            // a Windows program reaches the system through kernel32.dll, named in its import table
            if (Host.IsWindows()) Need("phi_imports");

            // the CPU stopping a program is an error like any other, which ends it with the error's
            // message (and its line) whether or not the program catches any
            if (unit.Kind == UnitKind.Program) Need("phi_throw");

            // what the compiled libraries' code uses, which nothing here would ask for
            foreach (CompiledTarget compiled in unit.CompiledLibraries)
            {
                foreach (string symbol in compiled.RuntimeSymbols) Need(symbol);
                if (compiled.UsesObjects) usesObjects = true;
            }
            foreach (StructSymbol s in unit.LibraryTypeTables) TypeTable(s);
            foreach (StructSymbol iface in unit.LibraryInterfaceSlots) InterfaceSlot(iface);

            // the FPU (floating point numbers) starts clean, working in f64s: with its own more exact
            // numbers, a result would be rounded twice, and not always be the nearest f64 to the answer
            if (bits32) EmitFloatReset();

            // the program's own code first: it decides which library routines are needed
            var startedLibraries = new HashSet<CompiledTarget>();
            foreach (ClassDecl cls in unit.Classes)
            {
                file = cls.File;
                // a compiled library's classes run together, in the order they're in in it
                if (cls.File.Compiled is CompiledTarget compiled)
                {
                    if (startedLibraries.Add(compiled))
                    {
                        Comment($"---- the classes of the library {compiled.Name}");
                        Emit($"call {compiled.StartLabel}");
                    }
                    continue;
                }
                Comment($"---- class {cls.Name} ({FileName(file)})");
                foreach (Stmt s in cls.Body) EmitClassStatement(s);
            }

            if (debugInfo != null)
            {
                // what the classes' own statements do is one procedure, whose variables are the
                // classes' own, wherever they're used
                var start = new DebugMethod
                {
                    Name = unit.Classes.LastOrDefault(c => c.File == program.File)?.Name ?? "start",
                    Label = "phi_start",
                    EndLabel = "phi_halt",
                    IsStart = true,
                };
                // (the standard library's own can be asked for, but they aren't listed with the
                // program's)
                var libraryOwn = unit.Classes.Where(c => c.File.Path.Replace('\\', '/').StartsWith("lib/phi/"))
                    .SelectMany(c => c.Body).SelectMany(SyntaxWalker.Nodes)
                    .Select(n => (n as VarDecl)?.Symbol).OfType<VariableSymbol>().ToHashSet();
                start.Variables.AddRange(unit.Variables.Where(v => !v.IsOutside).Select(v => DebugVariableOf(v) with { Library = libraryOwn.Contains(v) }));
                debugInfo.Methods.Add(start);
            }

            Line("");
            Label("phi_halt");
            switch (unit.Kind)
            {
                case UnitKind.Program:
                    // the end of a program: exit with 0
                    Need("phi_exit");
                    Emit("xor ebx, ebx");
                    Emit("jmp phi_exit");
                    break;
                case UnitKind.Kernel:
                    // wait for interrupts, and let any programs run (int 48 = OS_Yield; a no-op until
                    // multitasking starts)
                    Emit("hlt");
                    Emit("int 48");
                    Emit("jmp phi_halt");
                    break;
                default:
                    Emit("hlt");
                    Emit("jmp phi_halt");
                    break;
            }

            foreach (MethodSymbol m in unit.Methods.Where(m => !m.IsOutside)) EmitMethod(m);
            foreach (MethodSymbol m in unit.ForeignEntries.Where(m => !m.IsOutside)) EmitForeignEntry(m);
            // the compiled libraries' code goes here, once it's known what of it the rest reaches
            int librariesAt = code.Length;
            // what follows isn't PHI code: an exception in it has no line
            bool lineEnd = lineMarks.Count > 0 || unit.CompiledLibraries.Any(c => c.Lines.Count > 0) && MarksLines;
            if (lineEnd) Label("phi_line_end");
            foreach (RawBlockDecl raw in unit.RawBlocks.Where(r => r.File.Compiled == null)) EmitRawBlock(raw);
            EmitObjectRoutines();
            EmitStrRoomRoutine();
            EmitArrayGrowRoutine();
            EmitTextArrayGrowRoutine();
            if (debugInfo != null && Host.IsWindows()) EmitWindowsDebugTables();

            List<LibraryFile> library = Library.Resolve(libraryTarget, librarySymbols.OrderBy(s => s));

            // the file names of the libraries' lines go in the data too
            if (MarksLines)
                foreach (string name in unit.CompiledLibraries.SelectMany(c => c.Lines).Select(l => l.File).Distinct())
                    StringLabel(name);

            // written now, as the libraries' code is only what these reach too
            var dataText = new StringBuilder();
            EmitData(dataText);
            var reached = ReachedLibrarySections(library, dataText);
            var libraryCode = new StringBuilder();
            foreach (CompiledTarget compiled in unit.CompiledLibraries) AppendCompiledCode(libraryCode, compiled, reached);
            code.Insert(librariesAt, libraryCode.ToString());
            if (lineEnd) lineMarks.Add(("phi_line_end", 0, "0"));
            EmitLineTable();

            // events used by the library that the program doesn't handle
            var defined = unit.Methods.Select(m => m.Label).ToHashSet();
            foreach (string hook in library.SelectMany(f => f.Hooks).Distinct().Where(h => !defined.Contains(h)))
            {
                Line("");
                Label(hook);
                Emit("ret");
            }

            var output = new StringBuilder();
            string what = unit.Kind switch { UnitKind.Boot => "Boot sector", UnitKind.Os => "OS classes", UnitKind.Program => "User program", _ => "32-bit kernel" };
            output.AppendLine($"; {what} generated by phi from {Path.GetFileName(program.File.Path)}");
            foreach (StructSymbol iface in interfaceSlots)
                output.AppendLine($"phi_slot_{Templates.Label(iface.Name)} equ {TypeTableMethods + 4 * iface.TableStart}");

            switch (unit.Kind)
            {
                case UnitKind.Boot:
                    output.AppendLine("bits 16");
                    output.AppendLine("org 0x7C00");
                    output.AppendLine();
                    output.AppendLine("%ifndef PHI_KERNEL_SECTORS");
                    output.AppendLine("%define PHI_KERNEL_SECTORS 0");
                    output.AppendLine("%endif");
                    output.AppendLine();
                    output.AppendLine("    jmp 0x0000:phi_start    ; some BIOSes start us at 07C0:0000");
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cli");
                    output.AppendLine("    xor ax, ax");
                    output.AppendLine("    mov ds, ax");
                    output.AppendLine("    mov es, ax");
                    output.AppendLine("    mov ss, ax");
                    output.AppendLine("    mov sp, 0x7C00          ; the stack grows down from just below us");
                    output.AppendLine("    sti");
                    output.AppendLine("    cld");
                    output.AppendLine("    mov [phi_boot_drive], dl");
                    break;

                case UnitKind.Os:
                    output.AppendLine("bits 16");
                    output.AppendLine($"org 0x{OsAddress:X}");
                    output.AppendLine();
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;

                case UnitKind.Program when Host == Host.Linux:
                    output.AppendLine("bits 32");
                    output.AppendLine($"org 0x{LinuxAddress:X}");
                    output.AppendLine();
                    EmitElfHeader(output);
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;

                case UnitKind.Program when Host == Host.Linux64:
                    output.AppendLine("bits 64");
                    output.AppendLine($"org 0x{Linux64Address:X}");
                    output.AppendLine();
                    EmitElf64Header(output);
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;

                case UnitKind.Program when Host == Host.Windows:
                    output.AppendLine("bits 32");
                    output.AppendLine($"org 0x{WindowsAddress:X}");
                    output.AppendLine();
                    EmitPeHeader(output, debugInfo != null);
                    output.AppendLine("phi_start:");
                    if (debugInfo != null) output.AppendLine("    push ebp\n    mov ebp, esp          ; where a debugger's walk of the stack ends");
                    output.AppendLine("    cld");
                    break;

                case UnitKind.Program when Host == Host.Windows64:
                    output.AppendLine("bits 64");
                    output.AppendLine($"org 0x{WindowsAddress:X}");
                    output.AppendLine();
                    EmitPe64Header(output, debugInfo != null);
                    output.AppendLine("phi_start:");
                    if (debugInfo != null) output.AppendLine("    push rbp\n    mov rbp, rsp          ; where a debugger's walk of the stack ends");
                    output.AppendLine("    cld");
                    break;

                case UnitKind.Program:
                    // the kernel starts us in ring 3 with our own address space and stack
                    output.AppendLine("bits 32");
                    output.AppendLine($"org 0x{ProgramAddress:X}");
                    output.AppendLine();
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;

                default:
                    // the loader has switched to protected mode with flat segments, set up a
                    // stack, and left the BootInfo address in ebx (see docs/memory-map.md)
                    output.AppendLine("bits 32");
                    output.AppendLine($"org 0x{KernelAddress:X}");
                    output.AppendLine();
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;
            }

            foreach (string init in library.SelectMany(f => f.Init))
                output.AppendLine($"    call {init}");

            output.Append(code);

            // before the library: its imports.asm puts these in the table
            if (Host.IsWindows())
            {
                output.AppendLine();
                EmitOutsideImports(output);
            }

            foreach (LibraryFile lib in library)
            {
                output.AppendLine();
                output.AppendLine($"; ---- library: {lib.Name}");
                output.Append(lib.Text);
            }

            output.AppendLine();
            // variables start on a page of their own: when code and the variables it writes share
            // a 4 KB page, QEMU throws away its translation of that code on every write, which made
            // hot loops run up to ~200 times slower
            if (bits32) output.AppendLine("align 4096, db 0");
            if (debugInfo != null && Host.IsWindows()) output.AppendLine("phi_pe_data:");
            output.AppendLine("; ---- data");
            output.Append(dataText);
            foreach (CompiledTarget compiled in unit.CompiledLibraries) AppendCompiledSection(output, compiled, CompiledTarget.Variables);

            if (Host.IsWindows()) output.AppendLine("align 4096, db 0             ; the file is whole pages (its file alignment)");
            if (Host != Host.PhiOS) output.AppendLine("phi_image_end:");

            if (unit.Kind == UnitKind.Boot)
            {
                output.AppendLine("phi_boot_drive: db 0");
                output.AppendLine();
                output.AppendLine("times 510 - ($ - $$) db 0   ; a negative size here means the boot sector is too big");
                output.AppendLine("dw 0xAA55");
            }

            return output.ToString();
        }

        /// <summary>Where this unit runs: a user program can be built for another system.</summary>
        Host Host => unit.Kind == UnitKind.Program ? program.Host : Host.PhiOS;

        // ================================================================ compiled libraries

        LibraryUnit? libraryUnit;

        /// <summary>
        /// A library's assembly, for NASM to make an object file of: its own classes' code as a
        /// routine the program calls where they'd run, its own methods, and its variables in a
        /// section of their own (a Windows program's code isn't writable). Labels it doesn't
        /// define are the program's to give: what's outside its own files, and the runtime's.
        /// Those it doesn't export stay inside it, so no two libraries' can clash.
        /// </summary>
        string BuildLibrary()
        {
            LibraryBuild build = program.Library!;
            var result = new LibraryUnit();
            libraryUnit = result;
            result.Target.Name = CompiledTarget.LabelName(build.Name);

            Label(result.Target.StartLabel);
            Emit("cld");
            foreach (ClassDecl cls in unit.Classes.Where(c => !program.IsOutside(c.File)))
            {
                file = cls.File;
                Comment($"---- class {cls.Name} ({FileName(file)})");
                foreach (Stmt s in cls.Body) EmitClassStatement(s);
            }
            Emit("ret");

            // each method in a section of its own, so a program can leave out those it doesn't reach
            int sections = 0;
            foreach (MethodSymbol m in unit.Methods.Where(m => !m.IsOutside))
            {
                Line($"section .t{sections++} progbits alloc exec nowrite align=1");
                EmitMethod(m);
            }
            Line("section .text");
            foreach (MethodSymbol m in unit.ForeignEntries.Where(m => !m.IsOutside)) EmitForeignEntry(m);
            foreach (RawBlockDecl raw in unit.RawBlocks.Where(r => !program.IsOutside(r.File))) EmitRawBlock(raw);
            EmitObjectRoutines();
            EmitStrRoomRoutine();
            EmitArrayGrowRoutine();
            EmitTextArrayGrowRoutine();

            var data = new StringBuilder();
            EmitData(data);

            // what the object file won't say
            CompiledTarget t = result.Target;
            t.RuntimeSymbols.AddRange(librarySymbols.OrderBy(s => s));
            t.RawBlocks.AddRange(unit.RawBlocks.Where(r => program.IsOutside(r.File)).Select(r => $"{r.Language}.{r.Name}"));
            t.OutsideFunctions.AddRange(unit.OutsideFunctions.Select(f => $"{f.Library}.{f.Name}"));
            t.TypeTables.AddRange(typeTables.Select(s => s.Name));
            t.InterfaceSlots.AddRange(interfaceSlots.Select(s => s.Name));
            t.UsesObjects = usesObjects;
            var fileNames = stringLabels.ToDictionary(p => p.Value, p => p.Key);
            foreach (var (label, line, name) in lineMarks) result.Lines.Add((label, line, fileNames[name]));

            result.Exports.Add(t.StartLabel);
            result.Exports.AddRange(unit.Methods.Where(m => !m.IsOutside).Select(m => m.Label));
            result.Exports.AddRange(unit.Variables.Where(v => !v.IsOutside && !v.IsLocal && !v.Type.IsError).Select(v => v.Label).Distinct());

            string text = code.ToString() + data;
            var defined = new HashSet<string>(Regex.Matches(text, @"^\s*([A-Za-z_$][\w$]*)(?::|\s+equ\b)", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
            var words = new HashSet<string>(Regex.Matches(text, @"[A-Za-z_$][\w$]*").Select(m => m.Value));

            // the labels that are the program's to give
            var outside = new HashSet<string>(unit.Methods.Where(m => m.IsOutside).Select(m => m.Label));
            outside.UnionWith(unit.Variables.Where(v => v.IsOutside && !v.IsLocal).Select(v => v.Label));
            outside.UnionWith(unit.RawBlocks.Where(r => program.IsOutside(r.File)).Select(r => r.Name));
            outside.UnionWith(unit.OutsideFunctions.Select(OutsideLabel));
            outside.UnionWith(typeTables.Select(s => $"phi_type_{Templates.Label(s.Name)}"));
            outside.UnionWith(interfaceSlots.Select(s => $"phi_slot_{Templates.Label(s.Name)}"));
            outside.Add("phi_halt");
            outside.Add("phi_object_line");
            var constants = new Dictionary<string, string>();
            foreach (LibraryFile runtime in Library.Load(libraryTarget))
            {
                outside.UnionWith(runtime.Provides);
                outside.UnionWith(runtime.Hooks);
                // a number the generated code uses by name, like a thread block's field: the library gets its own copy
                foreach (Match m in Regex.Matches(runtime.Text, @"^([A-Za-z_][\w]*)\s+equ\s+([-+*()|&<>\s0-9A-Fa-fxX]+?)\s*(?:;.*)?$", RegexOptions.Multiline))
                    constants.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            }

            var output = new StringBuilder();
            output.AppendLine($"; The library {build.Name} generated by phi from {Path.GetFileName(program.File.Path)}");
            output.AppendLine(bits64 ? "bits 64" : "bits 32");
            foreach (string label in result.Exports.Where(defined.Contains)) output.AppendLine($"global {label}");
            foreach (string label in outside.Where(l => words.Contains(l) && !defined.Contains(l)).OrderBy(l => l)) output.AppendLine($"extern {label}");
            foreach (var (name, value) in constants.Where(c => words.Contains(c.Key) && !defined.Contains(c.Key)).OrderBy(c => c.Key)) output.AppendLine($"{name} equ {value}");
            output.AppendLine("section .text align=16");
            output.Append(code);
            output.AppendLine("section .data align=16");
            output.Append(data);
            return output.ToString();
        }

        /// <summary>
        /// The sections of the compiled libraries a program gets: each one's code that's always
        /// there and its variables, and what those and the program reach, following the addresses
        /// each section needs. A method nothing reaches is left out, as if it weren't written.
        /// </summary>
        HashSet<(CompiledTarget Library, int Section)> ReachedLibrarySections(List<LibraryFile> runtime, StringBuilder data)
        {
            var reached = new HashSet<(CompiledTarget, int)>();
            if (unit.CompiledLibraries.Count == 0) return reached;

            var exports = new Dictionary<string, (CompiledTarget Library, int Section)>();
            foreach (CompiledTarget compiled in unit.CompiledLibraries)
                foreach (LibraryExport e in compiled.Exports)
                    exports.TryAdd(e.Name, (compiled, e.Section));

            var waiting = new Stack<(CompiledTarget Library, int Section)>();
            void Reach((CompiledTarget, int) section)
            {
                if (reached.Add(section)) waiting.Push(section);
            }

            foreach (CompiledTarget compiled in unit.CompiledLibraries)
            {
                Reach((compiled, CompiledTarget.AlwaysCode));
                Reach((compiled, CompiledTarget.Variables));
            }

            // what the program's code and data, and the runtime, name
            var names = Regex.Matches(code.ToString() + data + string.Concat(runtime.Select(f => f.Text)), @"[A-Za-z_$][\w$]*");
            foreach (string name in names.Select(m => m.Value).Distinct())
                if (exports.TryGetValue(name, out var section)) Reach(section);

            while (waiting.Count > 0)
            {
                var (library, section) = waiting.Pop();
                foreach (Relocation r in library.Relocations.Where(r => r.Section == section))
                {
                    if (r.Symbol == null) Reach((library, r.TargetSection));
                    else if (exports.TryGetValue(r.Symbol, out var target)) Reach(target);
                }
            }
            return reached;
        }

        /// <summary>
        /// A compiled library's code, in with the program's methods so an error in it can say its
        /// line: the sections that are reached, with the labels of what's in them.
        /// </summary>
        void AppendCompiledCode(StringBuilder into, CompiledTarget compiled, HashSet<(CompiledTarget, int)> reached)
        {
            into.AppendLine();
            into.AppendLine($"; the library {compiled.Name}");
            for (int section = 0; section < compiled.Sections.Count; section++)
                if (section != CompiledTarget.Variables && reached.Contains((compiled, section)))
                    AppendCompiledSection(into, compiled, section);
            foreach (LibraryExport e in compiled.Exports.Where(e => reached.Contains((compiled, e.Section))))
                into.AppendLine($"{e.Name} equ {compiled.SectionLabel(e.Section)} + {e.Offset}");
            if (MarksLines)
                foreach (LibraryLine line in compiled.Lines.Where(l => reached.Contains((compiled, l.Section))))
                    lineMarks.Add(($"{compiled.SectionLabel(line.Section)} + {line.Offset}", line.Line, StringLabel(line.File)));
        }

        /// <summary>
        /// A compiled library's bytes, with each address it couldn't know written as its label:
        /// NASM fills them in with the rest of the program's, so no linker is needed.
        /// </summary>
        static void AppendCompiledSection(StringBuilder output, CompiledTarget compiled, int section)
        {
            byte[] bytes = compiled.Sections[section].Bytes;
            output.AppendLine($"align {compiled.Sections[section].Align}, db 0");
            // which method this is (each is a section of its own), for a listing's reader and phi profile
            if (compiled.Exports.FirstOrDefault(e => e.Section == section && e.Offset == 0 && e.Name.StartsWith("M_")) is LibraryExport method)
            {
                string name = method.Name[2..];
                int dot = name.LastIndexOf('_');
                if (dot > 0) name = name[..dot] + "." + name[(dot + 1)..];
                string file = compiled.Lines.FirstOrDefault(l => l.Section == section)?.File ?? compiled.Name;
                output.AppendLine($"; method {name} ({file})");
            }
            output.AppendLine($"{compiled.SectionLabel(section)}:");

            void Raw(int from, int to)
            {
                for (int i = from; i < to; i += 64)
                {
                    output.Append("db ");
                    for (int b = i; b < Math.Min(i + 64, to); b++)
                    {
                        if (b > i) output.Append(',');
                        output.Append(bytes[b]);
                    }
                    output.AppendLine();
                }
            }

            int at = 0;
            foreach (Relocation r in compiled.Relocations.Where(r => r.Section == section))
            {
                Raw(at, r.Offset);
                string size = r.Width switch { 8 => "dq", 4 => "dd", 2 => "dw", _ => "db" };
                string target = r.Symbol ?? compiled.SectionLabel(r.TargetSection);
                output.AppendLine($"{size} {target} + ({r.Addend}){(r.Relative ? " - $" : "")}");
                at = r.Offset + r.Width;
            }
            Raw(at, bytes.Length);
        }

        /// <summary>
        /// The ELF header of a Linux program, which NASM writes itself (no linker): the file is one
        /// segment, loaded whole at LinuxAddress, readable, writable and runnable, as a PHI OS
        /// program's memory is. The heap grows after it (brk).
        /// </summary>
        static void EmitElfHeader(StringBuilder output)
        {
            output.AppendLine("phi_elf_header:");
            output.AppendLine("    db 0x7F, 'ELF', 1, 1, 1, 0          ; 32-bit, little-endian, version 1, System V");
            output.AppendLine("    times 8 db 0");
            output.AppendLine("    dw 2                                 ; an executable");
            output.AppendLine("    dw 3                                 ; for the i386");
            output.AppendLine("    dd 1");
            output.AppendLine("    dd phi_start                         ; where it starts");
            output.AppendLine("    dd phi_elf_segments - $$");
            output.AppendLine("    dd 0                                 ; no sections");
            output.AppendLine("    dd 0");
            output.AppendLine("    dw phi_elf_segments - phi_elf_header");
            output.AppendLine("    dw 32                                ; the size of a segment entry");
            output.AppendLine("    dw 2                                 ; segments");
            output.AppendLine("    dw 40, 0, 0");
            output.AppendLine("phi_elf_segments:");
            output.AppendLine("    dd 1, 0, $$, $$                      ; loaded: the whole file");
            output.AppendLine("    dd phi_image_end - $$, phi_image_end - $$");
            output.AppendLine("    dd 7, 0x1000                         ; read, write, run; page aligned");
            output.AppendLine("    dd 0x6474E551, 0, 0, 0, 0, 0, 6, 16  ; the stack: read and write, not run");
            output.AppendLine();
        }

        /// <summary>
        /// The ELF header of a 64-bit Linux program, as for a 32-bit one: one segment, the whole
        /// file, loaded at Linux64Address, readable, writable and runnable.
        /// </summary>
        static void EmitElf64Header(StringBuilder output)
        {
            output.AppendLine("phi_elf_header:");
            output.AppendLine("    db 0x7F, 'ELF', 2, 1, 1, 0          ; 64-bit, little-endian, version 1, System V");
            output.AppendLine("    times 8 db 0");
            output.AppendLine("    dw 2                                 ; an executable");
            output.AppendLine("    dw 0x3E                              ; for x86-64");
            output.AppendLine("    dd 1");
            output.AppendLine("    dq phi_start                         ; where it starts");
            output.AppendLine("    dq phi_elf_segments - $$");
            output.AppendLine("    dq 0                                 ; no sections");
            output.AppendLine("    dd 0");
            output.AppendLine("    dw phi_elf_segments - phi_elf_header");
            output.AppendLine("    dw 56                                ; the size of a segment entry");
            output.AppendLine("    dw 2                                 ; segments");
            output.AppendLine("    dw 64, 0, 0");
            output.AppendLine("phi_elf_segments:");
            output.AppendLine("    dd 1, 7                              ; loaded: read, write, run");
            output.AppendLine("    dq 0, $$, $$                         ; the whole file, at the start");
            output.AppendLine("    dq phi_image_end - $$, phi_image_end - $$");
            output.AppendLine("    dq 0x1000                            ; page aligned");
            output.AppendLine("    dd 0x6474E551, 6                     ; the stack: read and write, not run");
            output.AppendLine("    dq 0, 0, 0, 0, 0, 16");
            output.AppendLine();
        }

        /// <summary>
        /// The headers of a Windows .exe (PE): a DOS stub that only points on, then one section
        /// holding everything, read, write and run. Section and file alignment are both a page, so
        /// a label's place in the file is its address less WindowsAddress, and no linker is needed.
        /// The import table (phi_imports) comes from the library.
        /// </summary>
        static void EmitPeHeader(StringBuilder output, bool debug)
        {
            output.AppendLine("phi_pe_header:");
            output.AppendLine("    db 'MZ'");
            output.AppendLine("    times 0x3C - ($ - $$) db 0");
            output.AppendLine("    dd phi_pe_signature - $$");
            output.AppendLine("phi_pe_signature:");
            output.AppendLine("    db 'PE', 0, 0");
            output.AppendLine("    dw 0x014C                            ; for the i386");
            output.AppendLine($"    dw {(debug ? 2 : 1)}                                 ; sections");
            output.AppendLine("    dd 0, 0, 0");
            output.AppendLine("    dw phi_pe_sections - phi_pe_optional");
            output.AppendLine("    dw 0x0103                            ; no relocations (it's always at its base), runnable, 32-bit");
            output.AppendLine("phi_pe_optional:");
            output.AppendLine("    dw 0x010B                            ; PE32");
            output.AppendLine("    db 0, 0");
            output.AppendLine("    dd phi_image_end - phi_pe_code, 0, 0");
            output.AppendLine("    dd phi_start - $$                    ; where it starts");
            output.AppendLine("    dd phi_pe_code - $$, phi_pe_code - $$");
            output.AppendLine($"    dd 0x{WindowsAddress:X}");
            output.AppendLine("    dd 0x1000, 0x1000                    ; section and file alignment");
            output.AppendLine("    dw 6, 0, 0, 0, 6, 0                  ; Windows Vista and later");
            output.AppendLine("    dd 0");
            output.AppendLine("    dd phi_image_end - $$                ; the image's size");
            output.AppendLine("    dd phi_pe_code - $$                  ; the headers' size");
            output.AppendLine("    dd 0");
            output.AppendLine("    dw 3                                 ; a console program");
            output.AppendLine("    dw 0x8000                            ; aware of terminal services");
            output.AppendLine("    dd 0x100000, 0x10000                 ; stack: 1 MB reserved, 64 KB committed");
            output.AppendLine("    dd 0x100000, 0x1000                  ; the process heap (not used)");
            output.AppendLine("    dd 0, 16                             ; 16 data directories:");
            output.AppendLine("    dd 0, 0");
            output.AppendLine("    dd phi_imports - $$, phi_imports_size ; the import table");
            EmitDebugDirectories(output, debug, unwinds: false);
            output.AppendLine("phi_pe_sections:");
            EmitPeSections(output, debug);
            output.AppendLine("    align 0x1000, db 0");
            output.AppendLine("phi_pe_code:");
            output.AppendLine();
        }

        /// <summary>
        /// One section, which is everything: code and data, run, read, write. A --debug build has
        /// its variables in a second one, the same to run, as a debugger takes a variable in the
        /// section of the code that uses it for a piece of that code.
        /// </summary>
        static void EmitPeSections(StringBuilder output, bool debug)
        {
            string end = debug ? "phi_pe_data" : "phi_image_end";
            output.AppendLine("    db '.text', 0, 0, 0");
            output.AppendLine($"    dd {end} - phi_pe_code, phi_pe_code - $$");
            output.AppendLine($"    dd {end} - phi_pe_code, phi_pe_code - $$");
            output.AppendLine("    dd 0, 0, 0");
            output.AppendLine("    dd 0xE0000060                        ; code and data, run, read, write");
            if (!debug) return;
            output.AppendLine("    db '.data', 0, 0, 0");
            output.AppendLine("    dd phi_image_end - phi_pe_data, phi_pe_data - $$");
            output.AppendLine("    dd phi_image_end - phi_pe_data, phi_pe_data - $$");
            output.AppendLine("    dd 0, 0, 0");
            output.AppendLine("    dd 0xE0000060");
        }

        /// <summary>
        /// The data directories after the import table: in a --debug build, where the debug
        /// directory is and, in 64-bit code, how to walk the stack (see EmitWindowsDebugTables).
        /// </summary>
        static void EmitDebugDirectories(StringBuilder output, bool debug, bool unwinds)
        {
            if (!debug)
            {
                output.AppendLine("    times 14 dd 0, 0");
                return;
            }
            output.AppendLine("    dd 0, 0");
            output.AppendLine(unwinds ? "    dd phi_unwinds - $$, phi_unwinds_size ; how to walk the stack" : "    dd 0, 0");
            output.AppendLine("    times 2 dd 0, 0");
            output.AppendLine("    dd phi_debug_directory - $$, 28      ; where the .pdb is");
            output.AppendLine("    times 9 dd 0, 0");
        }

        /// <summary>
        /// The headers of a 64-bit Windows .exe (PE32+), laid out as EmitPeHeader's: for the
        /// x86-64, with an 8-byte image base and stack and heap sizes. It loads at its base
        /// (no relocations, and no DYNAMIC_BASE), below 2 GB, so the code's 32-bit addresses
        /// of labels hold.
        /// </summary>
        static void EmitPe64Header(StringBuilder output, bool debug)
        {
            output.AppendLine("phi_pe_header:");
            output.AppendLine("    db 'MZ'");
            output.AppendLine("    times 0x3C - ($ - $$) db 0");
            output.AppendLine("    dd phi_pe_signature - $$");
            output.AppendLine("phi_pe_signature:");
            output.AppendLine("    db 'PE', 0, 0");
            output.AppendLine("    dw 0x8664                            ; for the x86-64");
            output.AppendLine($"    dw {(debug ? 2 : 1)}                                 ; sections");
            output.AppendLine("    dd 0, 0, 0");
            output.AppendLine("    dw phi_pe_sections - phi_pe_optional");
            output.AppendLine("    dw 0x0023                            ; no relocations (it's always at its base), runnable, large addresses");
            output.AppendLine("phi_pe_optional:");
            output.AppendLine("    dw 0x020B                            ; PE32+");
            output.AppendLine("    db 0, 0");
            output.AppendLine("    dd phi_image_end - phi_pe_code, 0, 0");
            output.AppendLine("    dd phi_start - $$                    ; where it starts");
            output.AppendLine("    dd phi_pe_code - $$");
            output.AppendLine($"    dq 0x{WindowsAddress:X}");
            output.AppendLine("    dd 0x1000, 0x1000                    ; section and file alignment");
            output.AppendLine("    dw 6, 0, 0, 0, 6, 0                  ; Windows Vista and later");
            output.AppendLine("    dd 0");
            output.AppendLine("    dd phi_image_end - $$                ; the image's size");
            output.AppendLine("    dd phi_pe_code - $$                  ; the headers' size");
            output.AppendLine("    dd 0");
            output.AppendLine("    dw 3                                 ; a console program");
            output.AppendLine("    dw 0x8000                            ; aware of terminal services");
            output.AppendLine("    dq 0x100000, 0x10000                 ; stack: 1 MB reserved, 64 KB committed");
            output.AppendLine("    dq 0x100000, 0x1000                  ; the process heap (not used)");
            output.AppendLine("    dd 0, 16                             ; 16 data directories:");
            output.AppendLine("    dd 0, 0");
            output.AppendLine("    dd phi_imports - $$, phi_imports_size ; the import table");
            EmitDebugDirectories(output, debug, unwinds: true);
            output.AppendLine("phi_pe_sections:");
            EmitPeSections(output, debug);
            output.AppendLine("    align 0x1000, db 0");
            output.AppendLine("phi_pe_code:");
            output.AppendLine();
        }

        // ================================================================ emit helpers

        void Emit(string instruction) => code.Append("    ").AppendLine(bits64 ? Widen(instruction) : instruction);

        /// <summary>
        /// Bytes in an address kept in memory (a pointer, an element of a str or object array): 8 in
        /// 64-bit code, and 4 in 16-bit code too, as <see cref="PhiType.AddressSize"/> lays them out.
        /// </summary>
        int Word => bits64 ? 8 : 4;

        /// <summary>Bytes a pushed value takes on the stack (16-bit code pushes 32-bit values too).</summary>
        int Slot => bits64 ? 8 : 4;

        /// <summary>A register as wide as an address, by its 32-bit name: R("eax") is rax in 64-bit code.</summary>
        string R(string register) => bits64 && register.Length == 3 && register[0] == 'e' ? "r" + register[1..] : register;

        static readonly System.Text.RegularExpressions.Regex StackRegisters = new(@"\be(sp|bp)\b");
        static readonly System.Text.RegularExpressions.Regex PushPop = new(@"^(push|pop)\s+e(ax|bx|cx|dx|si|di|sp|bp)\b");
        // (a register variable, pushed whole: its top half is zero, and what takes it reads 32 bits)
        static readonly System.Text.RegularExpressions.Regex PushVariable = new(@"^push\s+(‹r1[2-5])d›");
        static readonly System.Text.RegularExpressions.Regex LeaInto = new(@"^lea\s+e(ax|bx|cx|dx|si|di)\b");
        static readonly System.Text.RegularExpressions.Regex MemoryOperand = new(@"\[[^\]]*\]");
        static readonly System.Text.RegularExpressions.Regex Register32 = new(@"\be(ax|bx|cx|dx|si|di)\b");

        /// <summary>
        /// An instruction written for 32-bit code, as 64-bit code: what's the same at either width
        /// is translated here. The stack pointer and frame are always 64 bits; push and pop take
        /// whole registers (a slot is 8 bytes); and lea works out an address, which is 64 bits.
        /// What isn't the same (a value that's an address, how many bytes the pushes took) each
        /// place that emits it gets right itself. A register in a memory operand ([ebx + 4],
        /// [label + eax*4]) is 64 bits too: what's in it is an address, or a number a 32-bit
        /// instruction wrote (which clears the top half), such as an index already checked.
        /// </summary>
        static string Widen(string instruction)
        {
            string s = MemoryOperand.Replace(instruction, m => Register32.Replace(m.Value, "r$1"));
            s = StackRegisters.Replace(s, "r$1");
            s = PushPop.Replace(s, "$1 r$2");
            s = PushVariable.Replace(s, "push $1›");
            s = LeaInto.Replace(s, "lea r$1");
            if (s.StartsWith("push dword ")) s = "push qword " + s["push dword ".Length..];
            else if (s.StartsWith("pushfd")) s = "pushfq" + s[6..];
            else if (s.StartsWith("popfd")) s = "popfq" + s[5..];
            return s;
        }
        void Label(string label) => code.Append(label).AppendLine(":");
        void Line(string text) => code.AppendLine(text);
        string NewLabel(string hint) => $"phi_{hint}_{labelCount++}";

        void Comment(string text) => code.Append("    ; ").AppendLine(text);

        int LineOf(Node node) => file.GetLocation(node.Span.Start).Line;

        static string FileName(SourceFile f) => f.Path.Replace('\\', '/');

        /// <summary>
        /// Where each line's code starts, the line and its file's name, in a program that catches
        /// errors: a CPU exception's error says its line from them (the runtime's phi_line_at).
        /// </summary>
        readonly List<(string Label, int Line, string File)> lineMarks = new();

        /// <summary>Puts the source line above the code it produced, which helps when reading the .asm.</summary>
        void SourceComment(Node node)
        {
            int line = LineOf(node);
            if (lastComment == (file, line)) return;
            lastComment = (file, line);

            string[] lines = file.Text.Split('\n');
            string text = line - 1 < lines.Length ? lines[line - 1].Trim() : "";
            code.AppendLine($"    ; {line}: {text}");
            if (debugInfo != null) MarkDebugLine(file, line);

            MarkLine(line);
        }

        /// <summary>Where this line's code starts (see phi_lines).</summary>
        void MarkLine(int line)
        {
            if (!MarksLines) return;
            string label = $"phi_line_{lineMarks.Count}";
            Label(label);
            lineMarks.Add((label, line, StringLabel(Path.GetFileName(file.Path))));
        }

        void MarkDebugLine(SourceFile where, int line)
        {
            string label = $"phi_debug_{debugInfo!.Lines.Count}";
            Label(label);
            debugInfo.Lines.Add(new DebugLine(label, line, where));
        }

        DebugVariable DebugVariableOf(VariableSymbol v) => new(v.Name, v.Type, v.Label, v.Grows, v.Capacity, v.TextRef);

        /// <summary>
        /// In a --debug build of a Windows program: the debug directory and the record it points
        /// at, which says which .pdb goes with this program (phi fills the record in once the
        /// program's built, see Builder), and in 64-bit code a table of the methods, as a debugger
        /// can't walk a 64-bit stack without one. They all start and end the same way, so one
        /// description of how serves them all: rbp was pushed, and then holds the frame.
        /// </summary>
        void EmitWindowsDebugTables()
        {
            Line("");
            Comment("what a debugger looks for");
            Line("align 4");
            Label("phi_debug_directory");
            Line("    dd 0, 0");
            Line("    dw 0, 0");
            Line("    dd 2                     ; CodeView");
            Line($"    dd {DebugInfo.RecordSize}");
            Line("    dd phi_debug_record - $$, phi_debug_record - $$");
            Label("phi_debug_record");
            Line($"    times {DebugInfo.RecordSize} db 0");
            if (!bits64) return;

            Line("align 4");
            Label("phi_unwind_info");
            Line("    db 1, 4, 2, 0x05         ; version 1; 4 bytes of start; 2 steps; rbp holds the frame");
            Line("    db 4, 0x03               ; after 4 bytes: rbp has been set");
            Line("    db 1, 0x50               ; after 1 byte: rbp has been pushed");
            Line("align 4");
            Label("phi_unwinds");
            foreach (DebugMethod m in debugInfo!.Methods.OrderBy(m => !m.IsStart))
                Line($"    dd {m.Label} - $$, {m.EndLabel} - $$, phi_unwind_info - $$");
            Line("phi_unwinds_size equ $ - phi_unwinds");
        }

        void Need(string librarySymbol) => librarySymbols.Add(librarySymbol);

        string StringLabel(string value)
        {
            if (!stringLabels.TryGetValue(value, out string? label))
            {
                label = $"phi_string_{stringLabels.Count}";
                stringLabels[value] = label;
            }
            return label;
        }

        // ================================================================ methods and asm blocks

        void EmitMethod(MethodSymbol m)
        {
            file = m.Decl.Owner.File;
            Line("");
            // the class and file are what phi profile reports a method by
            string kind = m.IsHook ? "event" : m.IsInterruptHandler ? "interrupt handler" : "method";
            Comment($"{kind} {m.Name} ({FileName(file)})");
            Label(m.Label);
            if (debugInfo != null)
            {
                lastComment = default;
                MarkDebugLine(file, LineOf(m.Decl));
            }
            // (the stack running out stops a method before its first line, setting up its variables)
            MarkLine(LineOf(m.Decl));

            if (m.IsInterruptHandler)
            {
                // the interrupted code must find every register as it left it
                Emit("pushad");
                if (!bits32)
                {
                    Emit("push ds");
                    Emit("push es");
                    Emit("push fs");
                    Emit("xor ax, ax");
                    Emit("mov ds, ax");
                    Emit("mov es, ax");
                }
                Emit("cld");
                if (Catches)
                {
                    // (a kernel's catches are this core's, through fs, which may be a program's now)
                    if (unit.Kind == UnitKind.Kernel)
                    {
                        Need("phi_load_core_fs");
                        Emit("push fs");
                        Emit("call phi_load_core_fs");
                    }
                    // a throw in here mustn't jump into the code it interrupted: none is caught
                    Emit($"push dword [{CatchTop}]");
                    Emit($"mov dword [{CatchTop}], 0");
                }
            }

            // the variables used most go in registers
            currentMethod = m;
            resultType = m.ReturnType;
            inRegisters.Clear();
            addressTaken.Clear();
            foreach (Node n in m.Decl.Body.SelectMany(SyntaxWalker.Nodes))
                if (n is AddrExpr { Operand: NameExpr { Symbol: VariableSymbol taken } }) addressTaken.Add(taken);
            List<VariableSymbol> chosen = bits32 && debugInfo == null
                ? VariablePlanner.Choose(m, VariableRegisters.Length, FitsRegister, RunsInRegisters)
                : new List<VariableSymbol>();
            Frame frame = LayOutFrame(m);
            DebugMethod? debugMethod = null;
            if (debugInfo != null)
            {
                debugMethod = new DebugMethod { Name = $"{m.Decl.Owner.Name}.{m.Decl.Name}", Label = m.Label, EndLabel = $"{m.Label}_debug_end" };
                debugMethod.Variables.AddRange(m.Parameters.Concat(m.Locals).Select(DebugVariableOf));
                debugInfo.Methods.Add(debugMethod);
            }
            // (a pointer, in 64-bit code, is in all of its register)
            for (int i = 0; i < chosen.Count; i++)
                inRegisters.Add((chosen[i], WholeVariable(chosen[i]) ? Whole(VariableRegisters[i]) : VariableRegisters[i], chosen[i].Label));
            bool keepsRegisters = inRegisters.Count > 0;

            resultSlots.Clear();
            lengthOf.Clear();
            frameSize = frame.LocalsSize;
            returnLabel = NewLabel("return");
            returnUsed = false;

            // the body first: the temporary space it needs decides the frame's size
            StringBuilder outer = code;
            code = new StringBuilder();
            SetRegistersLive(keepsRegisters);

            foreach (Stmt s in m.Decl.Body) EmitStatement(s);

            if (m.Decl.Result is Expr result)
            {
                SourceComment(result);
                void EmitResult()
                {
                    if (m.ReturnType.IsObject) EmitObjectResult(result);
                    else if (m.ReturnType.IsWide) EmitWide(result);   // in edx:eax
                    else if (m.ReturnType.IsFloat) EmitFloat(result); // in st0
                    else EmitExpr(result); // the result goes back in eax
                }
                if (OneRegister(m.ReturnType) && SafeValue(result)) EmitResult();
                else Fenced(EmitResult);
            }

            if (returnUsed) Label(returnLabel); // return jumps here with the result in eax

            // what's left only tidies up, and may use any register: the variables are done with
            if (keepsRegisters)
            {
                SetRegistersLive(false);
                Line(FenceStart);
            }

            // a method that has things to give back at its end, in a program that catches errors,
            // is on the list a throw looks at while it runs (see EmitIfThrows): off it now
            string? unwindRecord = Catches && !m.IsInterruptHandler && NeedsTidyUp(m) ? AllocTemp(3 * Slot) : null;
            if (unwindRecord != null)
            {
                Emit($"mov {R("ecx")}, {Q} [{unwindRecord}]");
                EmitBlockField($"mov {Q} [{{0}}], {R("ecx")}     ; done: off the list a throw looks at", "UNWIND");
            }

            if (m.ReturnType.IsString && GrowResults)
            {
                // copy the result into the caller's slot (it grows to fit) before this frame goes away
                Need("phi_strlen");
                Need("phi_strcpy");
                Emit("push eax                ; the result");
                Emit($"mov {R("esi")}, {R("eax")}");
                Emit("call phi_strlen");
                Emit("lea ecx, [eax + 1]");
                Emit($"mov {R("edi")}, {Q} [ebp + {frame.ResultSlot}]");
                EmitStrRoomCall();
                Emit("push edx");
                Emit($"mov {R("esi")}, [esp + {Slot}]");
                Emit("call phi_strcpy");
                EmitKeepCut(Slot);
                EmitFreeOld();
                Emit("pop eax");
                EmitTextFitsAfterFree();
                string text = NewLabel("result");
                Emit($"mov {R("eax")}, {Q} [ebp + {frame.ResultSlot}]");
                Emit($"mov {R("eax")}, {Q} [eax]");
                Emit($"test {R("eax")}, {R("eax")}");
                Emit($"jnz {text}");
                LoadAddress("eax", StringLabel(""));
                Label(text);
            }
            else if (m.ReturnType.IsString)
            {
                // copy the result into the caller's buffer before this frame goes away
                Need("phi_strcpy");
                Emit($"mov {R("esi")}, {R("eax")}");
                Emit($"mov {R("edi")}, [ebp + {frame.ResultSlot}]");
                Emit($"mov ecx, {m.ResultCapacity}");
                Emit("call phi_strcpy");
                EmitTextFits();
                Emit($"mov {R("eax")}, [ebp + {frame.ResultSlot}]");
            }

            EmitTidyUp(m);

            StringBuilder body = code;
            bool hasFrame = frameSize > 0 || m.Parameters.Count > 0 || m.ReturnType.IsString || debugInfo != null;
            code = new StringBuilder();
            if (hasFrame) EmitFrameStart(frame);
            if (unwindRecord != null)
            {
                // on the list a throw looks at: the record before, what gives back this method's
                // things, and this frame
                EmitBlockField($"mov {R("eax")}, {Q} [{{0}}]", "UNWIND");
                Emit($"mov {Q} [{unwindRecord}], {R("eax")}");
                LoadAddress("eax", $"{m.Label}_unwind");
                Emit($"mov {Q} [{unwindRecord} + {Slot}], {R("eax")}");
                Emit($"mov {Q} [{unwindRecord} + {2 * Slot}], ebp");
                LoadAddress("eax", unwindRecord);
                EmitBlockField($"mov {Q} [{{0}}], {R("eax")}", "UNWIND");
            }
            string start = code.ToString();
            code = outer;
            string bodyText = keepsRegisters || heldInLoop ? CheckRegisterCode(m.Name, body.ToString(), free: !keepsRegisters) : body.ToString();
            heldInLoop = false;

            // callers keep variables in esi and edi, so a method that changes them (itself, or
            // through anything but a method) saves them first, just below its frame, and gives them
            // back at the end (an interrupt handler has saved every register already)
            var saved = bits32 && !m.IsInterruptHandler
                ? VariableRegisters.Where(r => MayChange(start + bodyText, r)).ToList()
                : new List<string>();
            if (hasFrame) EmitFrameSetup();
            foreach (string register in saved) Emit($"push {Whole(register)}                ; the caller's");
            code.Append(start);
            foreach (var (v, register, home) in inRegisters)
                Emit(v.IsParameter ? $"mov {register}, [{home}]   ; {v.Name}" : $"xor {register}, {register}   ; {v.Name}");
            code.Append(bodyText);
            if (hasFrame)
            {
                for (int i = 0; i < saved.Count; i++) Emit($"mov {Whole(saved[i])}, [ebp - {frameSize + Slot * (i + 1)}]");
                Emit("mov esp, ebp");
                Emit("pop ebp");
            }
            else
            {
                for (int i = saved.Count - 1; i >= 0; i--) Emit($"pop {Whole(saved[i])}");
            }
            inRegisters.Clear();

            if (frameSize > MaxFrame)
                diagnostics.Error(file, m.Decl.Span, $"{m.Decl.Name} needs {frameSize} bytes of stack for its variables (at most {MaxFrame}); declare large buffers in the class instead");
            currentMethod = null;

            if (m.IsInterruptHandler)
            {
                if (Catches)
                {
                    Emit($"pop dword [{CatchTop}]");
                    if (unit.Kind == UnitKind.Kernel) Emit("pop fs");
                }
                if (!bits32)
                {
                    Emit("pop fs");
                    Emit("pop es");
                    Emit("pop ds");
                }
                Emit("popad");
                Emit(bits32 ? "iretd" : "iret");
            }
            else
            {
                Emit("ret");
            }
            if (debugMethod != null) Label(debugMethod.EndLabel);

            // what a throw on its way past calls with ebp at this method's frame: the same tidying up
            if (unwindRecord != null)
            {
                Line("");
                Label($"{m.Label}_unwind");
                currentMethod = m;
                EmitTidyUp(m);
                currentMethod = null;
                Emit("ret");
            }
        }

        /// <summary>True when a method gives something back at its end: texts that grow, or objects.</summary>
        bool NeedsTidyUp(MethodSymbol m) =>
            m.Parameters.Concat(m.Locals).Any(v => v.Grows) || resultSlots.Count > 0
            || m.Locals.Any(v => v.GrowingElements || v.GrowingArray || v.Ownership == Ownership.Owned);

        /// <summary>
        /// The end of a method: its strs that grow give their text back, and its new objects go
        /// away with it (one it returns was handed over already). The result in eax (or
        /// edx:eax) stays. Uses only the method's frame, so a throw can run it too.
        /// </summary>
        void EmitTidyUp(MethodSymbol m)
        {
            var growing = m.Parameters.Concat(m.Locals).Where(v => v.Grows).ToList();
            var growingArrays = m.Locals.Where(v => v.GrowingElements).ToList();
            bool wideResult = m.ReturnType.IsWide;
            // the objects first: an array of them that grows is freed element by element, which
            // reads the array's storage, so that storage must still be there (it goes below)
            var owned = m.Locals.Where(v => v.Ownership == Ownership.Owned).ToList();
            if (owned.Count > 0)
            {
                if (wideResult) Emit("push edx");
                Emit("push eax                ; the result");
                foreach (VariableSymbol v in owned)
                {
                    if (v.Type.IsObjectArray)
                    {
                        EmitFreeElements(NameOf(v), 0);
                        continue;
                    }
                    Emit($"mov {R("eax")}, {Q} [{v.Label}]   ; {v.Name}");
                    Emit($"call {ObjectRoutine("free", v.Type.Struct!)}");
                }
                Emit("pop eax");
                if (wideResult) Emit("pop edx");
            }

            if (growing.Count > 0 || resultSlots.Count > 0 || growingArrays.Count > 0 || m.Locals.Any(v => v.GrowingArray))
            {
                if (wideResult) Emit("push edx");
                Emit("push eax                ; the result");
                foreach (VariableSymbol v in growing)
                {
                    Emit($"push dword [{v.Label}]   ; {v.Name}'s text");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit($"add esp, {Slot}");
                }
                foreach (string slot in resultSlots)
                {
                    Emit($"push dword [{slot}]   ; a str result it was given");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit($"add esp, {Slot}");
                }
                foreach (VariableSymbol array in m.Locals.Where(v => v.GrowingArray))
                    EmitGrowingArrayFree(array);
                foreach (VariableSymbol array in growingArrays)
                    for (int i = 0; i < array.Count; i++)
                    {
                        // an element that moved to the heap (one still in its own buffer stays)
                        string kept = NewLabel("own");
                        Emit($"test dword [{array.Label} + {Word * (array.Count + i)}], {InData}");
                        Emit($"jnz {kept}");
                        Emit($"push dword [{array.Label} + {Word * i}]   ; {array.Name}:{i}");
                        Emit($"call {HeapMethodLabel("Heap.Free")}");
                        Emit($"add esp, {Slot}");
                        Label(kept);
                    }
                Emit("pop eax");
                if (wideResult) Emit("pop edx");
            }
        }

        /// <summary>
        /// The most stack one method may use for its variables. A program's kernel stack is
        /// 8 KB, and calls nest.
        /// </summary>
        const int MaxFrame = 4096;

        /// <summary>The method being generated (null for class code), and its frame's size so far.</summary>
        MethodSymbol? currentMethod;

        /// <summary>What return gives back: the method's result type, or an inlined method's.</summary>
        PhiType resultType = PhiType.Void;
        int frameSize;

        /// <summary>Space for class code's temporary values, which has no frame.</summary>
        readonly List<(string Label, int Size)> staticTemps = new();

        // ================================================================ variables in registers

        /// <summary>
        /// The registers a method's variables may live in (32-bit code's; 64-bit code's are below). A method that uses
        /// them saves them first and puts them back before it returns, so they're kept across calls
        /// to other methods. Everything else (library routines, asm blocks, str copies) may change
        /// them, so a statement that needs one of those runs with the variables in memory: they're
        /// stored to their places in the frame before it, and loaded again after it.
        /// </summary>
        string[] VariableRegisters => bits64 ? VariableRegisters64 : VariableRegisters32;
        static readonly string[] VariableRegisters32 = { "esi", "edi" };

        /// <summary>
        /// 64-bit code's: the low halves of r12 to r15, which every system's own calls keep too. A
        /// variable in one is a 32-bit number; writing it clears the register's top half, so the
        /// whole register is the number when it isn't negative (see <see cref="IndexRegister"/>).
        /// </summary>
        static readonly string[] VariableRegisters64 = { "r12d", "r13d", "r14d", "r15d" };

        /// <summary>All of a variable's register, to push, pop and put back: r12 for r12d.</summary>
        string Whole(string register) => bits64 ? register.TrimEnd('d') : register;

        /// <summary>Holds a register variable's number as 64 bits, sign and all, to be an index (64-bit code).</summary>
        const string IndexScratch = "r11";

        /// <summary>
        /// An index held in a register variable, as an address can use it. In 32-bit code that's
        /// the register. In 64-bit code an address takes whole registers: the variable's own
        /// when the number can't be negative here (it's unsigned, or was just checked against a
        /// limit), and otherwise r11, given the number with its sign.
        /// </summary>
        string IndexRegister(string held, Expr index, bool knownPositive)
        {
            if (!bits64) return held;
            string whole = held.Replace("d›", "›");
            if (knownPositive || !index.Type.IsSigned) return whole;
            Emit($"movsxd {IndexScratch}, {held}");
            return IndexScratch;
        }

        /// <summary>The method's variables in registers, with their places in the frame.</summary>
        readonly List<(VariableSymbol Variable, string Register, string Home)> inRegisters = new();

        /// <summary>True while the variables are in their registers (false inside a fence).</summary>
        bool registersLive;

        // Generated code marks: a register variable is written ‹esi› (so a stray esi is easy to
        // find), a fence's code sits between the two fence marks, and while the variables are in
        // registers their labels are «name», which nothing may use as memory. All are taken out
        // (or checked for) when the method is done.
        const string FenceStart = "    ; «memory»";
        const string FenceEnd = "    ; «registers»";

        /// <summary>A variable's register while it's in one, or null.</summary>
        string? RegisterOf(VariableSymbol v)
        {
            if (!registersLive) return null;
            foreach (var (variable, register, _) in inRegisters)
                if (variable == v) return $"‹{register}›";
            return null;
        }

        /// <summary>A 32-bit variable as an instruction's operand: its register, or its memory.</summary>
        string VariableOperand(VariableSymbol v) => RegisterOf(v) ?? $"dword [{v.Label}]";

        /// <summary>The variables in registers or in memory, by what their labels say.</summary>
        void SetRegistersLive(bool live)
        {
            registersLive = live;
            foreach (var (v, _, home) in inRegisters) v.Label = live ? $"«{v.Name}»" : home;
        }

        void StoreRegisters()
        {
            foreach (var (v, register, home) in inRegisters) Emit($"mov [{home}], {register}   ; {v.Name}");
        }

        void LoadRegisters()
        {
            foreach (var (v, register, home) in inRegisters) Emit($"mov {register}, [{home}]   ; {v.Name}");
        }

        /// <summary>Runs code that may change any register with the variables in memory.</summary>
        void Fenced(Action emit)
        {
            if (!registersLive)
            {
                emit();
                return;
            }
            Line(FenceStart);
            StoreRegisters();
            SetRegistersLive(false);
            emit();
            LoadRegisters();
            SetRegistersLive(true);
            Line(FenceEnd);
        }

        // ---------------------------------------------------------------- a class's own loops

        /// <summary>
        /// The class variables something other than the classes' own statements can reach: a
        /// method names it (which an interrupt, another thread or another core may be running),
        /// an asm block names it, or its address is taken. These stay in memory always.
        /// </summary>
        HashSet<VariableSymbol>? sharedVariables;

        HashSet<VariableSymbol> SharedVariables()
        {
            if (sharedVariables != null) return sharedVariables;
            var shared = new HashSet<VariableSymbol>();

            void Reach(Symbol? symbol)
            {
                switch (symbol)
                {
                    case VariableSymbol v: shared.Add(v); break;
                    case FieldPathSymbol f: shared.Add(f.Root); break;
                    case SelfFieldSymbol f: shared.Add(f.Self); break;
                    case PathSymbol path: shared.Add(path.Root); break;
                    case LengthSymbol length:
                        if (length.Open != null) shared.Add(length.Open);
                        if (length.Str != null) shared.Add(length.Str);
                        break;
                }
            }

            // every variable a method names, in any way
            foreach (MethodSymbol m in unit.Methods.Concat(unit.ForeignEntries))
            {
                if (m.Decl == null) continue;
                var nodes = m.Decl.Parameters.Cast<Stmt>().Concat(m.Decl.Body).SelectMany(SyntaxWalker.Nodes)
                    .Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>());
                foreach (Node n in nodes)
                    if (n is NameExpr name) Reach(name.Symbol);
            }

            // in the classes' own statements: one whose address is taken, or that an atomic step
            // or a while each (which reach it by address) works on
            foreach (Node n in unit.Classes.SelectMany(c => c.Body).SelectMany(SyntaxWalker.Nodes))
            {
                if (n is AddrExpr { Operand: NameExpr taken }) Reach(taken.Symbol);
                if (n is AtomicStmt { Target: NameExpr step }) Reach(step.Symbol);
                if (n is AtomicExpr { Target: NameExpr swapped }) Reach(swapped.Symbol);
                if (n is WhileEachStmt each)
                {
                    if (each.Index.Symbol != null) shared.Add(each.Index.Symbol);
                    if (each.Element.Symbol != null) shared.Add(each.Element.Symbol);
                }
            }

            // {name} or {name.field} in an asm block (or its label, written out)
            foreach (RawBlockDecl raw in unit.RawBlocks)
            {
                var named = System.Text.RegularExpressions.Regex.Matches(raw.Text, @"\{\s*([A-Za-z_][A-Za-z_0-9]*)")
                    .Select(m => m.Groups[1].Value).ToHashSet();
                foreach (VariableSymbol v in unit.Variables)
                    if (named.Contains(v.Name) || v.Label.Length > 0 && raw.Text.Contains(v.Label))
                        shared.Add(v);
            }

            return sharedVariables = shared;
        }

        /// <summary>
        /// A statement of a class's own code. A while there keeps the class variables it uses
        /// most in registers while it runs (32- and 64-bit code), when nothing else can reach
        /// them meanwhile (<see cref="SharedVariables"/>, and never a volatile one): they're
        /// loaded before the loop and stored after it, so the memory is right whenever anything
        /// else runs, and inside it the rules are a method's (fences, and the same check).
        /// </summary>
        void EmitClassStatement(Stmt s)
        {
            if (s is not WhileStmt loop || !bits32 || debugInfo != null || currentMethod != null || inRegisters.Count > 0)
            {
                EmitStatement(s);
                return;
            }

            HashSet<VariableSymbol> shared = SharedVariables();
            var candidates = new HashSet<VariableSymbol>();
            foreach (Node n in SyntaxWalker.Nodes(loop))
            {
                VariableSymbol? v = n switch
                {
                    NameExpr { Symbol: VariableSymbol named } => named,
                    VarDecl { Symbol: VariableSymbol declared } => declared,
                    _ => null,
                };
                if (v != null && !v.IsLocal && !v.IsParameter && !v.IsOutside && v.Unit == unit.Kind && !v.Decl.Volatile
                    && !shared.Contains(v) && FitsRegister(v))
                    candidates.Add(v);
            }
            List<VariableSymbol> chosen = VariablePlanner.Choose(candidates, new List<Stmt> { loop }, null, VariableRegisters.Length, RunsInRegisters);
            if (chosen.Count == 0)
            {
                EmitStatement(s);
                return;
            }

            for (int i = 0; i < chosen.Count; i++)
                inRegisters.Add((chosen[i], WholeVariable(chosen[i]) ? Whole(VariableRegisters[i]) : VariableRegisters[i], chosen[i].Label));
            Comment("(in registers for this loop: " + string.Join(", ", inRegisters.Select(r => $"{r.Variable.Name} in {r.Register}")) + ")");
            LoadRegisters();

            StringBuilder outer = code;
            code = new StringBuilder();
            SetRegistersLive(true);
            EmitStatement(loop);
            SetRegistersLive(false);
            string body = CheckRegisterCode("a class's loop", code.ToString());
            code = outer;
            code.Append(body);

            StoreRegisters();
            inRegisters.Clear();
        }

        /// <summary>A variable that can live in a register: a 32-bit number, enum or pointer.</summary>
        bool FitsRegister(VariableSymbol v) =>
            v.Type.IsNumeric && (StorageType(v.Type).ElementSize == 4 || WholeVariable(v)) && v.Ownership == Ownership.None && v.Decl.AtAddress == null;

        /// <summary>A pointer in 64-bit code: 8 bytes, so in a register it's the whole register (r12, not r12d).</summary>
        bool WholeVariable(VariableSymbol v) => bits64 && v.Type.IsPointer && StorageType(v.Type).ElementSize == 8;

        /// <summary>True for a whole 64-bit register that a variable is in.</summary>
        static bool IsWhole(string register) => register.StartsWith("‹r") && !register.EndsWith("d›");

        /// <summary>A value that's worked on in eax alone: a number, bln, chr, enum or pointer.</summary>
        static bool OneRegister(PhiType t) => t.IsNumeric;

        /// <summary>
        /// True when a statement can run with the variables in registers: its code only uses eax,
        /// ebx, ecx and edx, and calls nothing but methods (which keep esi and edi). An if, while
        /// or when whose test is like that runs its body with them too, statement by statement.
        /// Anything else is fenced.
        /// </summary>
        bool RunsInRegisters(Stmt s) => s switch
        {
            UnsafeStmt => true,
            LockedStmt l => SafeRoot(l.Lock),
            IfStmt i => SafeValue(i.Condition),
            WhileStmt w => (w.Init == null || SafeStatement(w.Init)) && SafeValue(w.Condition) && (w.Step == null || SafeStatement(w.Step)),
            WhenStmt w => OneRegister(w.Subject.Type) && !w.Subject.Type.IsTextPointer
                          && SafeValue(w.Subject) && w.Branches.All(b => b.Values.All(SafeValue)),
            _ => SafeStatement(s),
        };

        bool SafeStatement(Stmt s) => s switch
        {
            ConstDecl or BreakStmt or ContinueStmt => true,
            VarDecl { Symbol.NeedsInitCode: false } => true,
            VarDecl { IsNew: false, Symbol: VariableSymbol v } d => OneRegister(v.Type) && d.Values.Count <= 1 && d.Values.All(SafeValue)
                                                                    || SafeWideVariable(v) && d.Values.Count <= 1 && d.Values.All(SafeWideValue)
                                                                    || bits32 && v.Type.IsFloat && v.Type.Kind is TypeKind.F64 or TypeKind.F32 && !v.Decl.Volatile && v.Decl.AtAddress == null
                                                                       && d.Values.Count <= 1 && d.Values.All(SafeFloatValue),
            AssignStmt a when a.Target.Type.IsFloat => SafeFloatPlace(a.Target) && SafeFloatValue(a.Value),
            AssignStmt a => OneRegister(a.Target.Type) && SafeValue(a.Target) && SafeValue(a.Value)
                            || WideCopy(a.Target, a.Value) && SafePlace(a.Target) && SafePlace(a.Value)
                            // (64-bit code: worked out whole in rax and stored whole, as EmitAssign does it)
                            || a.Target.Type.IsWide && a.Value is BinaryExpr or UnaryExpr or CastExpr && WholeValue(a.Value)
                               && KeptSomewhere(a.Target) && SafePlace(a.Target)
                            || a.Target is NameExpr { Call: null, Symbol: VariableSymbol wide } && SafeWideVariable(wide) && SafeWideValue(a.Value),
            CallStmt c => SafeCall(c.Resolved, c.Arguments)
                          && (c.ResultTarget == null || OneRegister(c.ResultTarget.Type) && SafeValue(c.ResultTarget)),
            ReturnStmt r => r.Value == null || OneRegister(r.Value.Type) && resultType.IsNumeric && SafeValue(r.Value),
            OutStmt o => SafeValue(o.Port) && SafeValue(o.Value),
            AtomicStmt a => SafeValue(a.Target) && SafeValue(a.Value) && (a.Expected == null || SafeValue(a.Expected)),
            _ => false,
        };

        bool SafeValue(Expr e) => e switch
        {
            NumberExpr or BoolExpr or SizeofExpr => true,
            _ when OneRegister(e.Type) && ConstantFolder.Evaluate(e) != null => true,
            NameExpr { Call: CallExpr c } => SafeCall(c.Resolved, c.Arguments),
            MemberExpr { Call: CallExpr c } => SafeCall(c.Resolved, c.Arguments),
            CallExpr c => SafeCall(c.Resolved, c.Arguments),
            NameExpr { Symbol: ConstantSymbol } => true,
            NameExpr { Symbol: LengthSymbol { Count: int } } => true,
            // (how many a struct's array without a size has: one load, from where the array is kept)
            NameExpr { Symbol: LengthSymbol { View: Expr view } } => bits32 && PlaceKeepsEax(view) && SafePlace(view),
            MemberExpr { Count: int } => true,
            NameExpr { Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol } => OneRegister(e.Type),
            MemberExpr m => OneRegister(m.Type) && !m.FilledLength && !m.TextLength && SafeRoot(m.Target),
            IndexExpr ix => OneRegister(ix.Type) && !ix.Target.Type.IsString && SafeRoot(ix.Target) && SafeValue(ix.Index),
            AddrExpr a => a.CodeLabel != null || !a.OfObject && !a.Operand.Type.IsHeapText && SafeRoot(a.Operand),
            CastExpr c => OneRegister(c.Type) && (OneRegister(c.Operand.Type) && SafeValue(c.Operand)
                                                  || bits32 && c.Operand.Type.IsWide && SafeWideValue(c.Operand)),
            CheckedNumberExpr c => SafeValue(c.Operand),
            InExpr i => SafeValue(i.Port),
            AtomicExpr a => SafeValue(a.Target) && SafeValue(a.Value) && SafeValue(a.Expected),
            UnaryExpr u => OneRegister(u.Operand.Type) && SafeValue(u.Operand),
            BinaryExpr { IsComparison: true } c when c.Left.Type.IsFloat || c.Right.Type.IsFloat => SafeFloatValue(c.Left) && SafeFloatValue(c.Right),
            BinaryExpr { IsComparison: true } c when c.Left.Type.IsWide || c.Right.Type.IsWide => WholeValue(c.Left) && WholeValue(c.Right),
            BinaryExpr b => b.ObjectCompare == null && !b.ArrayCompare && OneRegister(b.Left.Type) && OneRegister(b.Right.Type)
                            && SafeValue(b.Left) && SafeValue(b.Right),
            _ => false,
        };

        /// <summary>An f64 or f32 in memory at a place that's found with the variables in registers.</summary>
        bool SafeFloatPlace(Expr e) => bits32 && e.Type.IsFloat && e.Type.Kind is TypeKind.F64 or TypeKind.F32 && SafePlace(e);

        /// <summary>
        /// A floating point value that EmitFloat works out in the FPU, eax, ebx, ecx and edx alone,
        /// so the variables in registers stay where they are: constants, f64s and f32s in memory,
        /// whole numbers that are safe themselves, and adding, taking away, multiplying and
        /// dividing those. (An f16 and a 64-bit number call the library, and so does a method.)
        /// </summary>
        bool SafeFloatValue(Expr e) => e switch
        {
            _ when !bits32 => false,
            DecimalExpr => true,
            _ when !e.Type.IsFloat => OneRegister(e.Type) && SafeValue(e),
            NameExpr { Call: null } or MemberExpr { Call: null } or IndexExpr => SafeFloatPlace(e),
            CastExpr c => SafeFloatValue(c.Operand),
            UnaryExpr u => SafeFloatValue(u.Operand),
            BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.Divide } b => SafeFloatValue(b.Left) && SafeFloatValue(b.Right),
            _ => false,
        };

        /// <summary>
        /// A 64-bit variable of 32-bit code that a statement can set while other variables are in
        /// registers: its two halves are at a fixed place in memory.
        /// </summary>
        bool SafeWideVariable(VariableSymbol v) =>
            bits32 && v.Type.IsWide && !v.Decl.Volatile && v.Ownership == Ownership.None && v.Decl.AtAddress == null;

        /// <summary>
        /// A 64-bit value that EmitWide works out in eax, ebx, ecx and edx alone, so the variables in
        /// registers (esi and edi) stay where they are: 64-bit variables, 32-bit values that are
        /// safe themselves, and adding, taking away, multiplying, the bit operations and shifts
        /// of those. (Dividing calls the library, and a comparison is a 32-bit value.)
        /// </summary>
        bool SafeWideValue(Expr e) => e switch
        {
            _ when !bits32 => false,
            _ when WholeValue(e) => true,           // (64-bit code: rax, rcx and rdx alone)
            _ when ConstantFolder.Evaluate(e) != null => true,
            _ when e.Type.IsFloat => false,
            _ when !e.Type.IsWide => OneRegister(e.Type) && SafeValue(e),
            NameExpr { Call: null, Symbol: VariableSymbol v } => SafeWideVariable(v),
            CastExpr c => !c.Operand.Type.IsFloat && SafeWideValue(c.Operand),
            UnaryExpr u => SafeWideValue(u.Operand),
            BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRight } shift =>
                SafeWideValue(shift.Left) && OneRegister(shift.Right.Type) && SafeValue(shift.Right),
            // (a product only when it's worked out in place: any other calls the library's multiply)
            BinaryExpr { Op: BinaryOp.Multiply } product => WideMultiplyInPlace(product) && SafeWideValue(product.Left) && SafeWideValue(product.Right),
            BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor } b =>
                SafeWideValue(b.Left) && SafeWideValue(b.Right),
            _ => false,
        };

        /// <summary>What a field or element is reached through: a plain variable, field or element, or an address.</summary>
        bool SafeRoot(Expr e) => e switch
        {
            _ when e.Type.IsString => false,
            _ when e.Type.IsPointer => SafeValue(e),
            // an object is at the address its variable, field or element holds
            _ when e.Type.IsObject => e is NameExpr { Call: null, Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol }
                                      || e is MemberExpr { Call: null } field && SafeRoot(field.Target)
                                      || e is IndexExpr element && SafeRoot(element.Target) && SafeValue(element.Index),
            NameExpr { Symbol: VariableSymbol v } => !v.GrowingArray && !v.GrowingElements && !v.Type.TextTable,
            NameExpr { Symbol: FieldPathSymbol or SelfFieldSymbol or PathSymbol } => true,
            MemberExpr m => m.Call == null && m.Count == null && !m.FilledLength && !m.TextLength && SafeRoot(m.Target),
            IndexExpr ix => SafeRoot(ix.Target) && SafeValue(ix.Index),
            _ => false,
        };

        /// <summary>A call to a method whose parameters and result are each one register.</summary>
        bool SafeCall(object? callee, List<Expr> arguments) =>
            callee is MethodSymbol method
            && (method.ReturnType.Kind == TypeKind.Void || OneRegister(method.ReturnType))
            && method.Parameters.All(p => OneRegister(p.Type) && !p.TextRef)
            && arguments.All(SafeValue)
            && method.Parameters.Skip(arguments.Count).All(p => p.Decl.Values.All(SafeValue));

        /// <summary>
        /// Checks a method's code: outside fences, nothing may use esi or edi except as the
        /// variables they hold, and no variable in a register may be read from memory.
        /// </summary>
        string CheckRegisterCode(MethodSymbol m, string body) => CheckRegisterCode(m.Name, body);

        /// <summary>free: the code begins as a fence does, with no variable in a register (a method whose loops alone hold any).</summary>
        string CheckRegisterCode(string where, string body, bool free = false)
        {
            var kept = new StringBuilder();
            bool fenced = free;
            foreach (string line in body.Split('\n'))
            {
                string text = line.TrimEnd('\r');
                if (text == FenceStart) { fenced = true; continue; }
                if (text == FenceEnd) { fenced = false; continue; }
                if (!fenced && !text.Contains("it stops here") && System.Text.RegularExpressions.Regex.IsMatch(WithoutComment(text), StrayRegister))
                    throw new InvalidOperationException($"{where}: generated code changes a variable's register: {text.Trim()}");
                if (text.Contains('«'))
                    throw new InvalidOperationException($"{where}: generated code reads a variable in a register from memory: {text.Trim()}");
                kept.Append(text.Replace("‹", "").Replace("›", "")).Append('\n');
            }
            return kept.ToString();
        }

        static string WithoutComment(string line) => line.IndexOf(';') is int at and >= 0 ? line[..at] : line;

        /// <summary>
        /// True when a method's code may change the register: it names it, uses an instruction
        /// that changes it by itself, or calls something other than a method (a library routine,
        /// an asm block, an address), which may change any register.
        /// </summary>
        static bool MayChange(string code, string register)
        {
            // (nothing uses r12 to r15 by itself)
            string itself = register == "esi" ? "movs|lods|cmps" : register == "edi" ? "movs|stos|cmps|scas" : "(?!)";
            string named = register.StartsWith('r') ? register.TrimEnd('d') + "[dwb]?" : register;
            var pattern = new System.Text.RegularExpressions.Regex($@"\b{named}\b|\b({itself})[bwd]\b|\bcall (?!M_)");
            return code.Split('\n').Any(line => pattern.IsMatch(WithoutComment(line)));
        }

        /// <summary>esi or edi named outside a ‹›, an instruction that uses them itself, or a library routine.</summary>
        const string StrayRegister = @"(?<!‹)\b(esi|edi|r1[2-5][dwb]?)\b|\b(rep|repe|repne|movs[bwd]|stos[bwd]|lods[bwd]|cmps[bwd]|scas[bwd]|pushad|popad)\b|\bcall phi_";

        sealed class Frame
        {
            public int LocalsSize;
            public int ResultSlot;
            public readonly List<(VariableSymbol Param, int Slot)> StrParams = new();
            public readonly List<(VariableSymbol Array, List<string> Elements)> StrArrays = new();
        }

        /// <summary>
        /// Gives every parameter and variable of the method its place, relative to ebp:
        ///
        ///   ebp + ...   the arguments, the last one nearest (each 4 bytes, pushed by the caller)
        ///   ebp + 8/6   where to copy a str result, for methods that return one
        ///   ebp + 4/2   the return address (4 bytes in 32-bit code, 2 in 16-bit)
        ///   ebp         the caller's ebp
        ///   ebp - ...   the variables, then temporary space for the calls' results
        ///
        /// A str parameter's argument is the address of the text, which the method copies into
        /// a buffer of its own when it starts.
        /// </summary>
        Frame LayOutFrame(MethodSymbol m)
        {
            var frame = new Frame();
            int slot = bits64 ? 16 : bits32 ? 8 : 6;
            if (m.ReturnType.IsString)
            {
                frame.ResultSlot = slot;
                slot += Slot;
            }

            int size = 0;
            string Reserve(int bytes)
            {
                size += (bytes + 3) & ~3;
                return $"ebp - {size}";
            }

            for (int i = m.Parameters.Count - 1; i >= 0; slot += ParameterBytes(m.Parameters[i].Type), i--)
            {
                VariableSymbol p = m.Parameters[i];
                if (p.Type.IsString && !p.TextRef)
                {
                    p.Label = Reserve(p.Grows ? TextSlot : p.Capacity);
                    frame.StrParams.Add((p, slot));
                }
                else p.Label = $"ebp + {slot}";
            }

            foreach (VariableSymbol v in m.Locals)
            {
                if (v.Type.TextTable && !v.GrowingArray)
                {
                    // a table of pointers, one per element, each to a buffer of its own (and then
                    // each one's room, when they grow, and a filled one's count)
                    v.Label = Reserve((v.GrowingElements ? 2 * Word : Word) * v.Count + (v.Type.Filled ? 4 : 0));
                    var elements = new List<string>();
                    for (int i = 0; i < v.Count; i++) elements.Add(Reserve(v.Capacity));
                    frame.StrArrays.Add((v, elements));
                }
                else v.Label = Reserve(StorageSize(v));
            }

            frame.LocalsSize = size;
            return frame;
        }

        void EmitFrameSetup()
        {
            Emit("push ebp");
            // in 16-bit code only sp is the stack pointer; the top of esp may hold anything
            Emit(bits32 ? "mov ebp, esp" : "movzx ebp, sp");
            if (frameSize > 0) Emit($"sub esp, {frameSize}");
        }

        void EmitFrameStart(Frame frame)
        {
            // variables start at zero each call, as they always have
            // (a store for each word up to 16 of them: rep stosd takes longer to get going than
            // that many stores take, and it needs edi, which a caller's variable may be in.
            // 16-bit code, where room matters more, keeps to 4.)
            int locals = frame.LocalsSize;
            int each = bits32 ? 64 : 16;
            if (locals > 0 && locals <= each)
            {
                for (int k = 4; k <= locals; k += 4) Emit($"mov dword [ebp - {k}], 0");
            }
            else if (locals > each)
            {
                Emit($"lea edi, [ebp - {locals}]");
                Emit($"mov ecx, {locals / 4}");
                Emit("xor eax, eax");
                Emit("rep stosd");
            }

            // the slots for str results start empty
            foreach (string slot in resultSlots)
            {
                Emit($"mov {Q} [{slot}], 0");
                Emit($"mov dword [{slot} + {RoomAt}], 0");
            }

            foreach (var (array, elements) in frame.StrArrays)
                for (int i = 0; i < elements.Count; i++)
                {
                    // (each element's address, then each one's room, a word apart in 64-bit code too)
                    Emit($"lea eax, [{elements[i]}]");
                    Emit($"mov {Q} [{array.Label} + {Word * i}], {R("eax")}");
                    if (array.GrowingElements)
                        Emit($"mov dword [{array.Label} + {Word * (array.Count + i)}], {array.Capacity} | {InData}");
                }

            foreach (var (p, slot) in frame.StrParams)
            {
                if (p.Grows)
                {
                    Emit($"mov {R("eax")}, {Q} [ebp + {slot}]   ; {p.Name}");
                    EmitGrowStore(p, sourceIsString: true, append: false, unsigned: false);
                    continue;
                }
                Need("phi_strcpy");
                Emit($"mov {R("esi")}, {Q} [ebp + {slot}]   ; {p.Name}");
                Emit($"lea edi, [{p.Label}]");
                Emit($"mov ecx, {p.Capacity}");
                Emit("call phi_strcpy");
                EmitTextFits();
            }
        }

        /// <summary>Room for a temporary value: in the frame inside a method, or a fixed place in class code.</summary>
        string AllocTemp(int bytes)
        {
            bytes = (bytes + 3) & ~3;
            if (currentMethod != null)
            {
                frameSize += bytes;
                return $"ebp - {frameSize}";
            }

            string label = $"phi_temp_{staticTemps.Count}";
            staticTemps.Add((label, bytes));
            return label;
        }

        static bool InFrame(string home) => home.StartsWith("ebp");

        /// <summary>Loads the address of a variable (a label, or a place in the frame) into a register.</summary>
        void LoadAddress(string register, string home, int offset = 0)
        {
            string plus = offset == 0 ? "" : offset > 0 ? $" + {offset}" : $" - {-offset}";
            // (in 64-bit code, a label's address too, worked out from where the code is)
            Emit(InFrame(home) ? $"lea {register}, [{home}{plus}]" : bits64 ? $"lea {R(register)}, [rel {home}{plus}]" : $"mov {register}, {home}{plus}");
        }

        /// <summary>
        /// sys.Name: values, a system call: each value in the register the system takes it in
        /// (ebx, ecx, edx, esi, edi, ebp for int 0x80, on PHI OS and 32-bit Linux; rdi, rsi,
        /// rdx, r10, r8, r9 for syscall, on 64-bit Linux), the call's number in eax, and the
        /// answer back in eax (all of rax, for a pointer in 64-bit code) as the system gives it
        /// (-errno when Linux says no). The values
        /// are worked out first, each pushed, so none is lost to another's register; the
        /// registers PHI keeps its own things in (the frame, the variables) are kept.
        /// </summary>
        /// <summary>
        /// Where outside code calls a PHI method (addr foreign Name), the way it calls things.
        /// A method takes its values on the stack, the last one nearest; Windows' way is the
        /// first one nearest (stdcall, in 32-bit code, whose callee takes them off), or the first
        /// four in rcx, rdx, r8 and r9 and the rest above 32 bytes of room (64-bit code). Outside
        /// code also expects to find the registers it keeps as it left them.
        /// </summary>
        void EmitForeignEntry(MethodSymbol m)
        {
            int n = m.Parameters.Count;
            Line("");
            Comment($"where outside code calls {m.Name}");
            Label(Binder.ForeignEntry(m));
            string[] kept = bits64 ? new[] { "rbx", "rsi", "rdi", "r12", "r13", "r14", "r15" } : new[] { "ebx", "esi", "edi" };
            Emit("push ebp");
            Emit("mov ebp, esp");
            foreach (string r in kept) Emit($"push {r}");
            Emit("cld");
            string[] registers = { "rcx", "rdx", "r8", "r9" };
            for (int i = 0; i < n; i++)
            {
                if (!bits64) Emit($"push dword [ebp + {8 + 4 * i}]");
                else if (i < 4) Emit($"push {registers[i]}");
                else Emit($"push qword [rbp + {48 + 8 * (i - 4)}]");
            }
            Emit($"call {m.Label}");
            if (n > 0) Emit($"add esp, {n * Slot}");
            foreach (string r in kept.Reverse()) Emit($"pop {r}");
            Emit("pop ebp");
            Emit(!bits64 && n > 0 ? $"ret {4 * n}" : "ret");
        }

        void EmitAddIndex()
        {
            // an address is all of rbx in 64-bit code; a 32-bit add would lose its top half.
            // (the index is 32 bits, and in unsafe code can be negative)
            if (bits64) Emit("movsxd rax, eax");
            Emit($"add {R("ebx")}, {R("eax")}");
        }

        void EmitSystemCall(SysCallDecl sys, List<Expr> arguments)
        {
            string[] registers = bits64 ? new[] { "rdi", "rsi", "rdx", "r10", "r8", "r9" } : new[] { "ebx", "ecx", "edx", "esi", "edi", "ebp" };
            string[] kept = bits64 ? new[] { "rsi", "rdi" } : new[] { "ebp", "edi", "esi", "ebx" };
            Comment($"sys.{sys.Name}, system call {sys.Number}");
            foreach (string r in kept) Emit($"push {r}");
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i < sys.ParameterTypes.Count && sys.ParameterTypes[i].IsWide && bits64)
                {
                    // a 64-bit number is all of one register: its halves, joined
                    EmitWide(arguments[i]);
                    Emit("shl rdx, 32");
                    Emit("mov eax, eax");
                    Emit("or rax, rdx");
                }
                else EmitExpr(arguments[i]);
                Emit("push eax");
            }
            for (int i = arguments.Count - 1; i >= 0; i--) Emit($"pop {registers[i]}");
            Emit($"mov eax, {sys.Number}");
            Emit(bits64 ? "syscall" : "int 0x80");
            foreach (string r in kept.Reverse()) Emit($"pop {r}");
            if (bits64 && sys.ResultType is { IsWide: true })
            {
                // and one comes back the same way, for edx:eax
                Emit("mov rdx, rax");
                Emit("shr rdx, 32");
                Emit("mov eax, eax");
            }
        }

        /// <summary>
        /// A call to an outside library's function (Kernel32.CreateThread: ...), through the
        /// address Windows wrote into the import table when it loaded the program. In 32-bit
        /// code (stdcall) the values are pushed last first and the function takes them off; in
        /// 64-bit code the first four go in rcx, rdx, r8 and r9 and the rest on the stack, above
        /// 32 bytes the function may write (its shadow space), the stack 16-byte aligned. Its
        /// answer is in eax (all of rax for a pointer). r12 keeps where the stack was.
        /// </summary>
        void EmitOutsideCall(OutsideFunctionDecl f, List<Expr> arguments)
        {
            string label = OutsideLabel(f);
            Comment($"{f.Library}.{f.Name}, from {f.LibraryPath}");
            if (!bits64)
            {
                for (int i = arguments.Count - 1; i >= 0; i--)
                {
                    EmitExpr(arguments[i]);
                    Emit("push eax");
                }
                Emit($"call [{label}]");
                return;
            }

            int n = arguments.Count;
            Emit("push r12");
            foreach (Expr value in arguments)
            {
                EmitExpr(value);
                Emit("push eax");
            }
            Emit("mov r12, rsp");                      // the values: the first at [r12 + 8 * (n - 1)]
            Emit($"sub rsp, {32 + 8 * Math.Max(0, n - 4)}");
            Emit("and rsp, -16");
            string[] registers = { "rcx", "rdx", "r8", "r9" };
            for (int i = 0; i < n; i++)
            {
                string value = $"[r12 + {8 * (n - 1 - i)}]";
                if (i < 4) Emit($"mov {registers[i]}, {value}");
                else
                {
                    Emit($"mov rax, {value}");
                    Emit($"mov [rsp + {32 + 8 * (i - 4)}], rax");
                }
            }
            Emit($"call [rel {label}]");
            Emit("mov rsp, r12");
            if (n > 0) Emit($"add rsp, {8 * n}");
            Emit("pop r12");
        }

        /// <summary>
        /// Where Windows puts an outside function's address: one slot for each library and name,
        /// whichever name the library was given (phi_outside_2_CreateThread).
        /// </summary>
        string OutsideLabel(OutsideFunctionDecl f)
        {
            return $"phi_outside_{ImportName(f.LibraryPath)}_{f.Name}";
        }

        /// <summary>
        /// The slot's label says which library by its file's name, not its number among the
        /// program's: a compiled library's code uses the label without knowing the rest.
        /// </summary>
        static string ImportName(string path) => CompiledTarget.LabelName(path.ToLowerInvariant());

        /// <summary>The outside libraries the code calls, each with the functions it calls of it, in the order they're met.</summary>
        List<(string Path, List<string> Functions)> OutsideLibraries()
        {
            var libraries = new List<(string Path, List<string> Functions)>();
            foreach (OutsideFunctionDecl f in unit.OutsideFunctions)
            {
                int at = libraries.FindIndex(l => l.Path.Equals(f.LibraryPath, StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                {
                    libraries.Add((f.LibraryPath, new List<string>()));
                    at = libraries.Count - 1;
                }
                if (!libraries[at].Functions.Contains(f.Name)) libraries[at].Functions.Add(f.Name);
            }
            return libraries;
        }

        /// <summary>
        /// The import table's entries for the outside functions the code calls, as two macros the
        /// library's imports.asm puts in its table: PHI_OUTSIDE_DESCRIPTORS (one for each library,
        /// among the runtime's own: kernel32.dll may have two, which Windows reads one by one)
        /// and PHI_OUTSIDE_TABLES (each library's names and the slots Windows writes addresses in).
        /// </summary>
        void EmitOutsideImports(StringBuilder output)
        {
            var libraries = OutsideLibraries();
            if (libraries.Count == 0) return;
            string entry = bits64 ? "dq" : "dd";
            output.AppendLine("; the outside libraries' functions this program calls (see imports.asm)");
            output.AppendLine("%macro PHI_OUTSIDE_DESCRIPTORS 0");
            for (int i = 0; i < libraries.Count; i++)
                output.AppendLine($"    dd phi_outside_{i}_lookup - $$, 0, 0, phi_outside_{i}_name - $$, phi_outside_{i}_addresses - $$");
            output.AppendLine("%endmacro");
            output.AppendLine("%macro PHI_OUTSIDE_TABLES 0");
            for (int i = 0; i < libraries.Count; i++)
            {
                var (path, functions) = libraries[i];
                output.AppendLine($"align {Slot}, db 0");
                output.AppendLine($"phi_outside_{i}_lookup:");
                foreach (string name in functions) output.AppendLine($"    {entry} phi_outside_{i}_{name}_name - $$");
                output.AppendLine($"    {entry} 0");
                output.AppendLine($"phi_outside_{i}_addresses:");
                foreach (string name in functions) output.AppendLine($"phi_outside_{ImportName(path)}_{name}: {entry} phi_outside_{i}_{name}_name - $$");
                output.AppendLine($"    {entry} 0");
                foreach (string name in functions)
                {
                    output.AppendLine($"phi_outside_{i}_{name}_name:");
                    output.AppendLine("    dw 0");
                    output.AppendLine($"    db '{name}', 0");
                    output.AppendLine("    align 2, db 0");
                }
                output.AppendLine($"phi_outside_{i}_name: db '{path}', 0");
            }
            output.AppendLine("%endmacro");
            output.AppendLine();
        }

        void EmitRawBlock(RawBlockDecl raw)
        {
            Line("");
            Comment($"{raw.Language}.{raw.Name}");
            Label(raw.Name);

            var text = new StringBuilder();
            int i = 0;
            while (i < raw.Text.Length)
            {
                int open = raw.Text.IndexOf('{', i);
                int close = open < 0 ? -1 : raw.Text.IndexOf('}', open);
                if (close < 0) { text.Append(raw.Text, i, raw.Text.Length - i); break; }

                text.Append(raw.Text, i, open - i);
                string name = raw.Text[(open + 1)..close].Trim();

                string? replacement = ResolveAsmName(name);
                if (replacement != null) text.Append(replacement);
                else diagnostics.Error(raw.File, new Span(raw.TextSpan.Start + open, close - open + 1),
                        $"{raw.Language}.{raw.Name} uses {{{name}}}, but there is no class variable or constant named '{name}'");

                i = close + 1;
            }

            foreach (string line in text.ToString().Replace("\r\n", "\n").Split('\n'))
                Line(line.TrimEnd());
        }

        /// <summary>{name} in an asm block: a variable's address, a field's address, or a constant's value.</summary>
        string? ResolveAsmName(string name)
        {
            string[] parts = name.Split('.');
            if (!unit.ClassNames.TryGetValue(parts[0], out Symbol? symbol))
                return Builtins.Constants.TryGetValue(name, out long builtin) ? builtin.ToString() : null;

            if (symbol is ConstantSymbol k) return parts.Length == 1 ? k.Value.ToString() : null;
            if (symbol is not VariableSymbol v) return null;

            int offset = 0;
            PhiType type = v.Type;
            foreach (string member in parts.Skip(1))
            {
                FieldSymbol? f = type.IsStruct ? type.Struct!.Find(member) : null;
                if (f == null) return null;
                offset += f.Offset;
                type = f.Type;
            }
            return offset == 0 ? v.Label : $"{v.Label} + {offset}";
        }

        // ================================================================ statements

        int unsafeDepth;

        /// <summary>Where return jumps to in the method being generated.</summary>
        string returnLabel = "";
        bool returnUsed;

        /// <summary>The loops around the current statement, innermost last, for break and continue.</summary>
        sealed class LoopLabels
        {
            public string Next = "", End = "";
            public bool NextUsed;
            public bool RegistersLive;  // a break or continue from a fence inside loads them again
        }
        readonly List<LoopLabels> loops = new();

        void EmitStatement(Stmt stmt)
        {
            if (stmt is WhileStmt loop && EmitLoopHolding(loop)) return;
            if (registersLive && (!RunsInRegisters(stmt) || loopHeld.Count > 0 && NeedsMemory(stmt))) Fenced(() => EmitStatementCode(stmt));
            else EmitStatementCode(stmt);
        }

        // ---------------------------------------------------------------- a method's loops
        //
        // A loop in a method keeps the class variables it works on in registers too, in the
        // registers the method's own variables left free: an interpreter's place in its code
        // and its stack, a parser's place in its text. Unlike a method's own variables, the rest
        // of the program reads these, so memory has to be right whenever anything else can run:
        //
        //   - a statement that calls anything is fenced (they're stored before it and loaded
        //     after it, as what it calls may read and change them), and so is a return;
        //   - an error raised in the loop's own code stores them first (EmitStop);
        //   - when the loop ends they're stored.
        //
        // Only variables the loop itself changes are taken (one it only reads is as likely a flag
        // it waits on something else to set), never a volatile one, one whose address is taken
        // anywhere, one an asm block names, or one an [isr] method names (itself or in what it
        // calls). What another thread changes behind a loop that's changing it too needs
        // `volatile`, as the language says.

        /// <summary>The class variables the loop being generated has in registers.</summary>
        readonly HashSet<VariableSymbol> loopHeld = new();

        /// <summary>True once a method's code has held class variables in registers: it's checked as code with variables in registers is.</summary>
        bool heldInLoop;

        /// <summary>The class variables that are reached other than by name somewhere: by address, by an atomic step, a while each, or an asm block.</summary>
        HashSet<VariableSymbol>? reachedByAddress;

        HashSet<VariableSymbol> ReachedByAddress()
        {
            if (reachedByAddress != null) return reachedByAddress;
            var reached = new HashSet<VariableSymbol>();
            void Reach(Symbol? symbol)
            {
                switch (symbol)
                {
                    case VariableSymbol v: reached.Add(v); break;
                    case FieldPathSymbol f: reached.Add(f.Root); break;
                    case SelfFieldSymbol f: reached.Add(f.Self); break;
                    case PathSymbol path: reached.Add(path.Root); break;
                }
            }
            var nodes = unit.Classes.SelectMany(c => c.Body).SelectMany(SyntaxWalker.Nodes);
            foreach (MethodSymbol m in unit.Methods.Concat(unit.ForeignEntries))
            {
                if (m.Decl == null) continue;
                nodes = nodes.Concat(m.Decl.Parameters.Cast<Stmt>().Concat(m.Decl.Body).SelectMany(SyntaxWalker.Nodes))
                    .Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>());
            }
            foreach (Node n in nodes)
            {
                if (n is AddrExpr { Operand: NameExpr taken }) Reach(taken.Symbol);
                if (n is AtomicStmt { Target: NameExpr step }) Reach(step.Symbol);
                if (n is AtomicExpr { Target: NameExpr swapped }) Reach(swapped.Symbol);
                if (n is WhileEachStmt each)
                {
                    if (each.Index.Symbol != null) reached.Add(each.Index.Symbol);
                    if (each.Element.Symbol != null) reached.Add(each.Element.Symbol);
                }
            }
            foreach (RawBlockDecl raw in unit.RawBlocks)
            {
                var named = System.Text.RegularExpressions.Regex.Matches(raw.Text, @"\{\s*([A-Za-z_][A-Za-z_0-9]*)")
                    .Select(m => m.Groups[1].Value).ToHashSet();
                foreach (VariableSymbol v in unit.Variables)
                    if (named.Contains(v.Name) || v.Label.Length > 0 && raw.Text.Contains(v.Label))
                        reached.Add(v);
            }
            // and what an interrupt handler names, itself or in the methods it calls: it runs in the
            // middle of anything, called by nothing
            var seen = new HashSet<MethodSymbol>();
            var todo = new Stack<MethodSymbol>(unit.Methods.Where(m => m.IsInterruptHandler));
            while (todo.Count > 0)
            {
                MethodSymbol m = todo.Pop();
                if (m.Decl == null || !seen.Add(m)) continue;
                foreach (Node n in m.Decl.Parameters.Cast<Stmt>().Concat(m.Decl.Body).SelectMany(SyntaxWalker.Nodes)
                             .Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>()))
                {
                    if (n is NameExpr name) Reach(name.Symbol);
                    object? callee = n switch { CallStmt c => c.Resolved, CallExpr c => c.Resolved, _ => null };
                    if (callee is MethodSymbol called) todo.Push(called);
                }
            }
            return reachedByAddress = reached;
        }

        /// <summary>
        /// True for a statement that needs the class variables a loop holds to be in memory: it
        /// calls something (in its own values, or in what it tests), or it leaves the method.
        /// </summary>
        static bool NeedsMemory(Stmt s) => s switch
        {
            ReturnStmt => true,
            UnsafeStmt => false,
            LockedStmt => true,
            IfStmt i => Leaves(i.Condition),
            WhileStmt w => w.Init != null && NeedsMemory(w.Init) || Leaves(w.Condition) || w.Step != null && NeedsMemory(w.Step),
            WhenStmt w => Leaves(w.Subject) || w.Branches.Any(b => b.Values.Any(Leaves)),
            AssignStmt { Op: AssignOp.Divide or AssignOp.Modulo } => true,
            _ => SyntaxWalker.Nodes(s).Any(LeavesAt),
        };

        static bool Leaves(Expr e) => SyntaxWalker.Nodes(e).Any(LeavesAt);

        /// <summary>
        /// Where control may go somewhere else: a call, or a division, which stops with an error
        /// when it's by zero (by a constant that isn't zero it can't).
        /// </summary>
        static bool LeavesAt(Node n) => n is CallStmt or CallExpr or NewExpr or NameExpr { Call: not null } or MemberExpr { Call: not null }
            || n is BinaryExpr { Op: BinaryOp.Divide or BinaryOp.Modulo } division && ConstantFolder.Evaluate(division.Right) is not (long and not 0);

        /// <summary>
        /// A while in a method, with the class variables it changes most in the registers that
        /// are free. False when it isn't to be done that way, and nothing was emitted.
        /// </summary>
        bool EmitLoopHolding(WhileStmt loop)
        {
            if (currentMethod == null || !bits32 || debugInfo != null || loopHeld.Count > 0 || currentMethod.IsInterruptHandler) return false;
            if (inRegisters.Count > 0 && !registersLive) return false;      // inside a fence
            int free = VariableRegisters.Length - inRegisters.Count;
            if (free <= 0 || !RunsInRegisters(loop) || NeedsMemory(loop)) return false;

            HashSet<VariableSymbol> reached = ReachedByAddress();
            var changed = new HashSet<VariableSymbol>();
            var candidates = new HashSet<VariableSymbol>();
            foreach (Node n in SyntaxWalker.Nodes(loop))
            {
                if (n is AssignStmt { Target: NameExpr { Symbol: VariableSymbol set } }) changed.Add(set);
                if (n is NameExpr { Symbol: VariableSymbol v } && !v.IsLocal && !v.IsParameter && !v.IsOutside && v.Unit == unit.Kind
                    && !v.Decl.Volatile && !reached.Contains(v) && FitsRegister(v))
                    candidates.Add(v);
            }
            candidates.IntersectWith(changed);
            if (candidates.Count == 0) return false;
            List<VariableSymbol> chosen = VariablePlanner.Choose(candidates, new List<Stmt> { loop }, null, free, s => RunsInRegisters(s) && !NeedsMemory(s));
            if (chosen.Count == 0) return false;

            int first = inRegisters.Count;
            bool were = registersLive;
            for (int i = 0; i < chosen.Count; i++)
            {
                string register = VariableRegisters[first + i];
                inRegisters.Add((chosen[i], WholeVariable(chosen[i]) ? Whole(register) : register, chosen[i].Label));
                loopHeld.Add(chosen[i]);
            }
            heldInLoop = true;
            Comment("(in registers for this loop: " + string.Join(", ", inRegisters.Skip(first).Select(r => $"{r.Variable.Name} in {r.Register}")) + ")");
            Line(FenceStart);
            foreach (var (v, register, home) in inRegisters.Skip(first)) Emit($"mov {register}, [{home}]   ; {v.Name}");
            Line(FenceEnd);
            SetRegistersLive(true);

            EmitStatementCode(loop);

            SetRegistersLive(false);
            Line(FenceStart);
            foreach (var (v, register, home) in inRegisters.Skip(first)) Emit($"mov [{home}], {register}   ; {v.Name}");
            // (a method with no variables of its own in registers is free with them outside its loops)
            if (were) Line(FenceEnd);
            inRegisters.RemoveRange(first, chosen.Count);
            loopHeld.Clear();
            SetRegistersLive(were);
            return true;
        }

        /// <summary>Before an error is raised in a loop's own code: the class variables the loop holds, into memory.</summary>
        void StoreLoopHeld()
        {
            if (loopHeld.Count == 0 || !registersLive) return;
            Line(FenceStart);
            foreach (var (v, register, home) in inRegisters)
                if (loopHeld.Contains(v)) Emit($"mov [{home}], {register}   ; {v.Name}");
            Line(FenceEnd);
        }

        void EmitStatementCode(Stmt stmt)
        {
            if (stmt is not (VarDecl { Symbol.NeedsInitCode: false } or ConstDecl or UnsafeStmt)) SourceComment(stmt);

            switch (stmt)
            {
                case VarDecl { IsNew: true, Symbol: { Type.IsObject: true } newObject } newDecl:
                    EmitNewDecl(newDecl, newObject);
                    break;

                case VarDecl { Symbol: { GrowingArray: true } growing }:
                    EmitGrowingArrayEmpty(growing);
                    break;

                case VarDecl { IsNew: true, Symbol: { Type.IsObjectArray: true } newArray } arrayDecl:
                    EmitNewArray(arrayDecl, newArray);
                    break;

                case VarDecl decl:
                    if (decl.Symbol is { NeedsInitCode: true } declared) EmitInitialize(declared, decl.Values);
                    if (decl.Symbol is VariableSymbol own && RemembersLength(own))
                    {
                        // how long it is isn't known yet (it's measured the first time it's added to)
                        if (!lengthOf.ContainsKey(own)) lengthOf[own] = AllocTemp(4);
                        Emit($"mov dword [{lengthOf[own]}], 0");
                    }
                    break;

                case ObjectAssignStmt assign:
                    EmitObjectAssign(assign);
                    break;

                case TakesStmt takes:
                    EmitTakes(takes);
                    break;

                case ConstDecl:
                    break;

                case LogStmt log:
                    // a whole log at a time: in a kernel across cores (nothing while there's one),
                    // in a program across its threads
                    bool lockLog = unit.Kind is UnitKind.Kernel or UnitKind.Program;
                    if (lockLog)
                    {
                        Need("phi_log_take");
                        Emit("call phi_log_take");
                    }
                    EmitLog(log);
                    if (unit.Kind == UnitKind.Program && log.Target == LogTarget.ScreenAndSerial)
                    {
                        // a whole log statement reaches the screen at once, even with other programs running
                        Need("phi_flush");
                        Emit("call phi_flush");
                    }
                    if (lockLog) Emit("call phi_log_give");
                    break;

                case AskStmt ask:
                {
                    var v = (VariableSymbol)ask.Target.Symbol!;
                    Need("phi_read_line");
                    if (v.Grows)
                    {
                        // a line of up to AskRoom - 1 characters
                        LoadAddress("edi", v.Label);
                        Emit($"mov ecx, {AskRoom}");
                        EmitStrRoomCall();
                        Emit("push edx");
                        Emit("dec ecx");
                        Emit("call phi_read_line");
                        EmitFreeOld();
                        break;
                    }
                    LoadAddress("edi", v.Label);
                    Emit($"mov ecx, {v.Capacity - 1}");
                    Emit("call phi_read_line");
                    break;
                }

                case ExitStmt exit when unit.Kind == UnitKind.Program:
                    if (exit.Code != null) EmitExpr(exit.Code);
                    else Emit("xor eax, eax");
                    Need("phi_exit");
                    Emit("mov ebx, eax            ; the exit code goes back to whoever waits for us");
                    Emit("jmp phi_exit");
                    break;

                case ExitStmt exit:
                    if (exit.Code != null) EmitExpr(exit.Code);
                    else Emit("xor eax, eax");
                    Emit("mov dx, 0xF4            ; QEMU isa-debug-exit");
                    Emit("out dx, al");
                    Emit("jmp phi_halt            ; not running in QEMU");
                    break;

                case OutStmt o:
                    if (ConstantFolder.Evaluate(o.Port) is long port)
                    {
                        EmitExpr(o.Value);
                        Emit($"mov dx, 0x{port & 0xFFFF:X}");
                    }
                    else
                    {
                        EmitExpr(o.Value);
                        Emit("push eax");
                        EmitExpr(o.Port);
                        Emit("mov dx, ax");
                        Emit("pop eax");
                    }
                    Emit(o.Size switch { 1 => "out dx, al", 2 => "out dx, ax", _ => "out dx, eax" });
                    break;

                case FreeStmt { Owner: StructSymbol owner } f:
                    // a struct that owns objects: they go with it
                    EmitExpr(f.Pointer);
                    Emit($"call {ObjectRoutine("free", owner)}");
                    break;

                case FreeStmt f:
                    EmitHeapCall(f.Releaser!, f.Pointer);
                    break;

                case UnsafeStmt u:
                    unsafeDepth++;
                    foreach (Stmt s in u.Body) EmitStatement(s);
                    unsafeDepth--;
                    break;

                case LockedStmt locked:
                    EmitLocked(locked);
                    break;

                case AtomicStmt atomic:
                    EmitAtomic(atomic);
                    break;

                case CallStmt call:
                    EmitCall(call);
                    break;

                case WhenStmt branches:
                    EmitWhen(branches);
                    break;

                case IfStmt { Condition: ThrowsExpr throws } ifs:
                    EmitIfThrows(ifs, throws);
                    break;

                case ThrowStmt t:
                    // the runtime copies the text (it may be in this frame) before anything goes
                    Need("phi_throw");
                    textMayCut = true;
                    EmitTextValue(t.Text!);
                    textMayCut = false;
                    Emit($"mov {R("edx")}, {R("eax")}            ; the text");
                    Emit($"mov eax, {LineOf(t)}");
                    LoadAddress("esi", StringLabel(System.IO.Path.GetFileName(file.Path)));
                    Emit($"mov ebx, {ErrorThrown}");
                    Emit("jmp phi_throw");
                    break;

                case IfStmt ifs:
                {
                    string elseLabel = NewLabel("else");
                    string endLabel = NewLabel("endif");

                    EmitBranch(ifs.Condition, jumpIf: false, ifs.Else != null ? elseLabel : endLabel);
                    foreach (Stmt s in ifs.Then) EmitStatement(s);

                    if (ifs.Else != null)
                    {
                        Emit($"jmp {endLabel}");
                        Label(elseLabel);
                        foreach (Stmt s in ifs.Else) EmitStatement(s);
                    }

                    Label(endLabel);
                    break;
                }

                case WhileStmt loop:
                {
                    if (loop.Init?.Symbol != null) EmitInitialize(loop.Init.Symbol, loop.Init.Values);

                    // the test is at the bottom, so each time round ends in one jump back; the first
                    // time, a test that calls nothing is written out again, and any other is jumped to
                    string top = NewLabel("while");
                    string test = NewLabel("whiletest");
                    string end = NewLabel("endwhile");
                    var labels = new LoopLabels { Next = NewLabel("next"), End = end, RegistersLive = registersLive };

                    bool repeatTest = !HasCall(loop.Condition);
                    if (repeatTest) EmitBranch(loop.Condition, jumpIf: false, end);
                    else Emit($"jmp {test}");
                    Label(top);
                    loops.Add(labels);
                    var bound = InRangeIn(loop);
                    if (bound is var (counter, below)) inRange.Add((counter, below));
                    foreach (Stmt s in loop.Body) EmitStatement(s);
                    if (bound != null) inRange.RemoveAt(inRange.Count - 1);
                    loops.RemoveAt(loops.Count - 1);
                    if (labels.NextUsed) Label(labels.Next); // continue runs the step, then tests again
                    if (loop.Step != null) EmitStatement(loop.Step);
                    if (!repeatTest) Label(test);
                    EmitBranch(loop.Condition, jumpIf: true, top);
                    Label(end);
                    break;
                }

                case WhileEachStmt each when each.Current != null:
                {
                    // the count from 0, while it's under how many the array has now
                    string count = each.Index.Symbol!.Label;
                    string top = NewLabel("each");
                    string end = NewLabel("endeach");
                    var labels = new LoopLabels { Next = NewLabel("nexteach"), End = end, RegistersLive = registersLive };
                    // (tested before the first time round, then at the bottom, like a while)
                    Emit($"mov dword [{count}], 0");
                    EmitArrayCount(each.Array);
                    Emit($"cmp [{count}], eax");
                    Emit($"jae {end}");
                    Label(top);
                    if (each.Fetch != null) EmitStatement(each.Fetch);
                    else
                    {
                        // the element itself: an object's address, or where a struct is in the array
                        if (each.Current.Type.IsObject) EmitExpr(each.Current);
                        else EmitPlaceAddress(each.Current);
                        Emit($"mov {Q} [{each.Element.Symbol!.Label}], {R("eax")}");
                    }
                    loops.Add(labels);
                    foreach (Stmt s in each.Body) EmitStatement(s);
                    loops.RemoveAt(loops.Count - 1);
                    if (labels.NextUsed) Label(labels.Next);
                    Emit($"inc dword [{count}]");
                    EmitArrayCount(each.Array);
                    Emit($"cmp [{count}], eax");
                    Emit($"jb {top}");
                    Label(end);
                    break;
                }

                case WhileEachStmt:
                    break;   // (an error was reported)

                case BreakStmt:
                    UnlockLeaving(loops.Count);
                    if (loops[^1].RegistersLive && !registersLive) LoadRegisters();  // out of a fence
                    Emit($"jmp {loops[^1].End}");
                    break;

                case ContinueStmt:
                    UnlockLeaving(loops.Count);
                    if (loops[^1].RegistersLive && !registersLive) LoadRegisters();
                    loops[^1].NextUsed = true;
                    Emit($"jmp {loops[^1].Next}");
                    break;

                case ReturnStmt ret:
                    if (ret.Value != null && resultType.IsObject) EmitObjectResult(ret.Value);
                    else if (ret.Value != null && resultType.IsWide) EmitWide(ret.Value);   // in edx:eax
                    else if (ret.Value != null && resultType.IsFloat) EmitFloat(ret.Value); // in st0
                    else if (ret.Value != null) EmitExpr(ret.Value); // the result goes back in eax
                    UnlockLeaving(-1);
                    returnUsed = true;
                    Emit($"jmp {returnLabel}");
                    break;

                case AssignStmt assign:
                    EmitAssign(assign.Target, assign.Value);
                    break;

                case AppendStmt append:
                    if (append.Values.Count == 0) EmitAppend(append, null);
                    foreach (Expr value in append.Values) EmitAppend(append, value);
                    break;

                case SetLengthStmt { Array: NameExpr { Symbol: VariableSymbol { GrowingArray: true } growing } } shrink:
                {
                    EmitExpr(shrink.Value);
                    EmitIndexCheck(shrink, "jbe", $"dword [{growing.Label} + {ViewCount}]");
                    if (growing.Type.TextTable)
                    {
                        // the dropped elements' texts go back, and their slots are empty again
                        Emit("push eax                ; the new count");
                        EmitFreeTexts(growing, "dword [esp]", empty: true);
                        Emit("pop eax");
                    }
                    Emit($"mov [{growing.Label} + {ViewCount}], eax");
                    if (growing.Type.IsObjectArray) EmitFreeElements(shrink.Array, -1);   // the dropped ones
                    break;
                }

                case SetLengthStmt shrink when Binder.IsFieldView(shrink.Array):
                {
                    EmitExpr(shrink.Value);
                    Emit("push eax                ; the new count");
                    EmitViewAddress(shrink.Array);
                    Emit($"mov {R("edx")}, {R("eax")}");
                    Emit("mov eax, [esp]");
                    EmitIndexCheck(shrink, "jbe", $"dword [edx + {ViewCount}]");
                    if (shrink.Array.Type.TextTable)
                    {
                        // the dropped elements' texts go back, and their slots are empty again
                        string view = ViewCopy(shrink.Array);
                        EmitFreeTexts(view, "it", "dword [esp]", empty: true);
                    }
                    EmitViewAddress(shrink.Array);
                    Emit($"mov {R("edx")}, {R("eax")}");
                    Emit("pop eax");
                    Emit($"mov [edx + {ViewCount}], eax");
                    if (shrink.Array.Type.IsObjectArray) EmitFreeElements(shrink.Array, -1);   // the dropped ones
                    break;
                }

                case SetLengthStmt shrink:
                {
                    EmitExpr(shrink.Value);
                    Emit("push eax");
                    EmitFilledCount(shrink.Array);
                    Emit("mov edx, eax");
                    Emit("pop eax");
                    EmitIndexCheck(shrink, "jbe", "edx");
                    Emit($"mov dword {Operand(new Place { Computed = true, Linear = true, Offset = shrink.Array.Type.CountOffset })}, eax");
                    if (shrink.Array.Type.IsObjectArray) EmitFreeElements(shrink.Array, -1);   // the dropped ones
                    break;
                }

                case ArrayAssignStmt copy:
                    EmitArrayAssign(copy);
                    break;

                case StringBuildStmt build:
                    if (build.AsideWhen.Count > 0)
                    {
                        // texts:i++ texts:j; when i is j as it runs, the values are built into a text
                        // of their own first, so each is read as it was; otherwise straight into it
                        string aside = NewLabel("aside"), built = NewLabel("built");
                        foreach (Expr place in build.AsideWhen)
                        {
                            EmitPlaceAddress(place);
                            Emit("push eax");
                            EmitPlaceAddress(build.Target);
                            Emit("pop edx");
                            Emit($"cmp {R("eax")}, {R("edx")}");
                            Emit($"je {aside}");
                        }
                        var straight = new StringBuildStmt { Target = build.Target, Append = build.Append, Span = build.Span };
                        straight.Parts.AddRange(build.Parts);
                        EmitStatementCode(straight);
                        Emit($"jmp {built}");
                        Label(aside);
                        var text = new TextExpr { Span = build.Span, Type = PhiType.Str };
                        text.Parts.AddRange(build.Parts);
                        var whole = new StringBuildStmt { Target = build.Target, Append = build.Append, Span = build.Span };
                        whole.Parts.Add(text);
                        EmitStatementCode(whole);
                        Label(built);
                        break;
                    }
                    for (int i = 0; i < build.Parts.Count; i++)
                    {
                        Expr part = build.Parts[i];
                        bool text = IsText(part.Type) || part.Type.IsWide || part.Type.IsFloat || part.Type.IsChar;
                        if (part.Type.IsWide || part.Type.IsFloat || part.Type.IsChar) EmitNumberText(part);
                        else if (text && StaticAddress(part) is string label) LoadAddress("eax", label);
                        else EmitExpr(part);
                        EmitStringStore(build.Target, text, append: build.Append || i > 0, unsigned: part.Type.IsUnsigned32);
                    }
                    break;

                default:
                    throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}");
            }
        }

        static NameExpr NameOf(VariableSymbol v) => new() { Name = v.Name, Symbol = v, Type = v.Type };

        void EmitInitialize(VariableSymbol v, List<Expr> values)
        {
            // no value: zero, every time the declaration runs
            if (values.Count == 0)
            {
                if (v.Type.IsNumeric)
                {
                    Emit("xor eax, eax");
                    EmitStoreEax(NameOf(v));
                }
                else if (v.Type.TextTable && !v.GrowingArray)
                {
                    // each element's text starts empty: the table says where each one is, and that stays
                    for (int i = 0; i < v.Count; i++)
                    {
                        Emit($"mov {R("eax")}, {Q} [{v.Label} + {Word * i}]");
                        Emit("mov byte [eax], 0");
                    }
                }
                else
                {
                    int size = StorageSize(v);
                    LoadAddress("edi", v.Label);
                    Emit($"mov ecx, {size}");
                    Emit("xor al, al");
                    Emit("rep stosb");
                }
                return;
            }

            if (!v.Type.IsArray)
            {
                // str s: f; with f a float or 64-bit number writes it out as text
                if (v.Type.IsString && (values[0].Type.IsFloat || values[0].Type.IsWide))
                {
                    var build = new StringBuildStmt { Target = NameOf(v) };
                    build.Parts.Add(values[0]);
                    EmitStatement(build);
                    return;
                }
                EmitAssign(NameOf(v), values[0]);
                return;
            }

            for (int i = 0; i < values.Count; i++)
            {
                var element = new IndexExpr
                {
                    Target = NameOf(v),
                    Index = new NumberExpr { Value = i, Type = PhiType.Int },
                    Type = v.Type.Element,
                };
                EmitAssign(element, values[i]);
            }
        }

        void EmitLog(LogStmt log)
        {
            bool serialOnly = log.Target == LogTarget.SerialOnly;
            string suffix = serialOnly ? "_serial" : "";

            foreach (Expr value in log.Values)
            {
                // a ptr<u8> prints as the zero-terminated text it points to, like a str
                if (IsText(value.Type))
                {
                    string print = "phi_print" + suffix;
                    Need(print);
                    if (StaticAddress(value) is string label) LoadAddress("esi", label);
                    else
                    {
                        EmitExpr(value);
                        Emit($"mov {R("esi")}, {R("eax")}");
                    }
                    Emit($"call {print}");
                }
                else if (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar)
                {
                    string print = "phi_print" + suffix;
                    Need(print);
                    EmitNumberText(value);
                    Emit($"mov {R("esi")}, {R("eax")}");
                    Emit($"call {print}");
                }
                else
                {
                    string print = (value.Type.IsUnsigned32 ? "phi_print_uint" : "phi_print_int") + suffix;
                    Need(print);
                    EmitExpr(value);
                    Emit($"call {print}");
                }
            }
        }

        /// <summary>A str, or a text pointer: the address of zero-terminated text, copied or printed as text.</summary>
        static bool IsText(PhiType t) => t.IsString || t.IsTextPointer;

        /// <summary>The label of a string whose address is known without running code.</summary>
        string? StaticAddress(Expr e) => e switch
        {
            StringExpr s => StringLabel(s.Value),
            NameExpr { Symbol: VariableSymbol { Type.IsString: true, Grows: false, TextRef: false } v } => v.Label,
            _ => null,
        };

        void EmitCall(CallStmt call)
        {
            EmitCallTo(call.Resolved, call.Arguments);
            PhiType result = call.Resolved switch
            {
                MethodSymbol method => method.ReturnType,
                PointerCall pointer => pointer.Signature.Result,
                BuiltinFunction { FloatInstruction: not null } => PhiType.F64,
                SysCallDecl sys => sys.ResultType ?? PhiType.Int,
                OutsideFunctionDecl outside => outside.ResultType ?? PhiType.Int,
                _ => PhiType.Int,
            };
            if (call.ResultTarget == null)
            {
                if (result.IsFloat) Emit("fstp st0               ; the result isn't kept");
                return;
            }

            if (result.IsObject)
            {
                // the call made a new object: the target takes it over
                Emit("push eax");
                EmitTakeOver(call.ResultTarget, call.ResultTarget.Type.Struct!);
                return;
            }
            EmitStoreResult(call.ResultTarget, result);
        }

        /// <summary>Mem's calls that run the CPU's string instructions for a count: their last parameter.</summary>
        static bool TakesAMemCount(MethodSymbol m) =>
            m.Decl.Name is "Copy" or "Move" or "Fill" or "Fill32" && m.Decl.Owner.File.Path == "lib/phi/memory/mem.phi";

        /// <summary>
        /// The count just pushed for a Mem call, 2 billion or more: a negative number, or a subtraction
        /// that went below 0, and copying that many bytes would run through all of memory. It's
        /// checked here rather than in mem.phi so that the error names the line of the call.
        /// </summary>
        void EmitMemCountCheck(MethodSymbol method, Expr count)
        {
            if (ConstantFolder.Evaluate(count) is long known && known >= 0 && known <= int.MaxValue) return;
            string ok = NewLabel("countok");
            Emit("cmp dword [esp], 0        ; the count, which has its top bit set from 2 billion up");
            Emit($"jge {ok}");
            EmitStop($"a count of 2 billion or more for Mem.{method.Decl.Name} (a negative number, or a subtraction below 0)", count, ErrorCount);
            Label(ok);
        }

        /// <summary>
        /// The number in eax, going to a variable, field, parameter or result of the other kind: its
        /// top bit is set if it's a negative number going to a u32, or a u32 of 2 billion or more
        /// going to an int, and either comes out as a different number.
        /// </summary>
        void EmitNumberCheck(CheckedNumberExpr check)
        {
            string ok = NewLabel("numberok");
            Emit("test eax, eax");
            Emit($"jns {ok}");
            EmitStop(check.Type == PhiType.U32 ? "a negative number can't be a u32" : "a u32 of 2 billion or more can't be an int", check, ErrorConvert);
            Label(ok);
        }

        /// <summary>Stops with an error (what if call ... throws catches) naming the line and file of a node.</summary>
        void EmitStop(string text, Node at, int kind)
        {
            Need("phi_throw");
            StoreLoopHeld();
            LoadAddress("edx", StringLabel(text));
            Emit($"mov eax, {LineOf(at)}");
            if (bits64) LoadAddress("esi", StringLabel(System.IO.Path.GetFileName(file.Path)));
            else Emit($"mov esi, {StringLabel(System.IO.Path.GetFileName(file.Path))}   ; it stops here, so esi is free");
            Emit($"mov ebx, {kind}");
            Emit("jmp phi_throw");
        }

        /// <summary>Calls a method, built-in or asm block; a result comes back in eax.</summary>
        void EmitCallTo(object? callee, List<Expr> arguments)
        {
            switch (callee)
            {
                case MethodSymbol small when CanInline(small):
                    EmitInline(small, arguments);
                    break;

                case MethodSymbol method:
                {
                    // push each argument in order (a missing one is the parameter's default, or zero);
                    // a str parameter gets the address of the text, and a 64-bit one takes two slots
                    int count = method.Parameters.Count;
                    int pushedBytes = 0;
                    var writeBacks = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        VariableSymbol p = method.Parameters[i];
                        Expr? value = i < arguments.Count ? arguments[i] : p.Decl.Values.FirstOrDefault();

                        if (p.Type.IsFloat || p.Type.IsWide)
                        {
                            pushedBytes += PushWideOrFloat(p.Type, value);
                            continue;
                        }
                        if (p.Type.Open)
                        {
                            pushedBytes += PushArrayView(value);
                            continue;
                        }
                        if (p.TextRef)
                        {
                            PushTextRef(value!, writeBacks);
                            pushedBytes += Slot;
                            continue;
                        }
                        if (bits32 && !p.Type.IsString && value != null && DirectOperand(value, constantOnly: false) is string asIs)
                        {
                            // a constant or a 32-bit variable is pushed as it is
                            Emit(asIs.Contains('[') || asIs.Contains('‹') ? $"push {asIs}" : $"push dword {asIs}");
                            pushedBytes += Slot;
                            continue;
                        }
                        if (!p.Type.IsString && value != null && PlaceOperand(value))
                        {
                            Emit($"push dword {Operand(PlaceKeepingEax(value))}");
                            pushedBytes += Slot;
                            continue;
                        }
                        if (value == null) Emit(p.Type.IsString ? $"mov eax, {StringLabel("")}" : "xor eax, eax");
                        else if (p.Type.IsString && (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar)) EmitNumberText(value);
                        else EmitExpr(value);

                        if (p.Type.IsString && value != null && !IsText(value.Type) && !value.Type.IsWide && !value.Type.IsFloat && !value.Type.IsChar)
                        {
                            // a number for a str parameter is written out as text first
                            string text = AllocTemp(12);
                            Need("phi_int_to_str");
                            LoadAddress("edi", text);
                            Emit("mov ecx, 12");
                            Emit("call phi_int_to_str");
                            LoadAddress("eax", text);
                        }
                        Emit("push eax");
                        pushedBytes += Slot;
                    }

                    if (method.ReturnType.IsString)
                    {
                        // somewhere for the method to put its result: a slot that grows to fit
                        // (the text and its room, freed when this method ends), or a buffer
                        if (GrowResults)
                        {
                            string slot = AllocTemp(TextSlot);
                            if (currentMethod != null) resultSlots.Add(slot);
                            LoadAddress("eax", slot);
                        }
                        else LoadAddress("eax", AllocTemp(method.ResultCapacity));
                        Emit("push eax");
                        pushedBytes += Slot;
                    }

                    if (bits32 && arguments.Count == count && TakesAMemCount(method)) EmitMemCountCheck(method, arguments[^1]);
                    Emit($"call {method.Label}");
                    if (pushedBytes > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {pushedBytes}");
                    EmitTextWriteBacks(writeBacks);
                    break;
                }

                case BuiltinFunction { FloatInstruction: not null } function:
                    EmitFloatFunction(function, arguments[0]);
                    break;

                case BuiltinFunction builtin:
                {
                    for (int i = arguments.Count - 1; i >= 0; i--)
                    {
                        EmitExpr(arguments[i]);
                        Emit("push eax");
                    }
                    Need(builtin.Label);
                    Emit($"call {builtin.Label}");
                    if (arguments.Count > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {arguments.Count * Slot}");
                    break;
                }

                case RawBlockDecl raw:
                    Emit($"call {raw.Name}");
                    break;

                case SysCallDecl sys:
                    EmitSystemCall(sys, arguments);
                    break;

                case OutsideFunctionDecl outside:
                    EmitOutsideCall(outside, arguments);
                    break;

                case PointerCall pointer:
                {
                    // the same as calling the method by name, through the address the variable holds
                    IReadOnlyList<PhiType> types = pointer.Signature.Parameters;
                    int pushed = 0;
                    var textWriteBacks = new List<string>();
                    for (int i = 0; i < types.Count; i++)
                    {
                        Expr value = arguments[i];
                        if (pointer.Signature.Into[i])
                        {
                            // str into: the caller's text and its room, as a call by name passes it
                            PushTextRef(value, textWriteBacks);
                            pushed += Slot;
                            continue;
                        }
                        if (types[i].IsFloat || types[i].IsWide)
                        {
                            pushed += PushWideOrFloat(types[i], value);
                            continue;
                        }
                        if (types[i].Open)
                        {
                            pushed += PushArrayView(value);
                            continue;
                        }
                        EmitExpr(value);
                        if (types[i].IsString && !IsText(value.Type))
                        {
                            string text = AllocTemp(12);
                            Need("phi_int_to_str");
                            LoadAddress("edi", text);
                            Emit("mov ecx, 12");
                            Emit("call phi_int_to_str");
                            LoadAddress("eax", text);
                        }
                        Emit("push eax");
                        pushed += Slot;
                    }
                    if (pointer.Signature.Result.IsString)
                    {
                        // somewhere for its str: a slot that grows to fit, freed when this method ends
                        string slot = AllocTemp(TextSlot);
                        if (currentMethod != null) resultSlots.Add(slot);
                        LoadAddress("eax", slot);
                        Emit("push eax");
                        pushed += Slot;
                    }
                    EmitExpr(pointer.Target);
                    Emit(bits64 ? "call rax" : bits32 ? "call eax" : "call ax");
                    if (pushed > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {pushed}");
                    EmitTextWriteBacks(textWriteBacks);
                    break;
                }
            }
        }

        // ================================================================ indexes known to be in range

        /// <summary>
        /// The counters of the whiles around the code being generated, each with what it's known
        /// to stay below in its loop's body: a number of elements, or an array parameter's .len.
        /// </summary>
        readonly List<(VariableSymbol Counter, object Below)> inRange = new();

        /// <summary>The method's variables whose address is taken (anything could change them).</summary>
        readonly HashSet<VariableSymbol> addressTaken = new();

        /// <summary>
        /// What a while's counter stays below in its body, when the loop proves it: the test is
        /// i &lt; n (or i &lt;= n - 1), n is a constant or an array parameter's .len, nothing in the
        /// body changes i, and i can't be below zero (it's unsigned, or a signed i starts at zero
        /// or more and the step only adds to it).
        /// </summary>
        (VariableSymbol, object)? InRangeIn(WhileStmt loop)
        {
            if (loop.Condition is not BinaryExpr { Op: BinaryOp.Less or BinaryOp.LessEqual } cmp) return null;
            if (cmp.Left is not NameExpr { Call: null, Symbol: VariableSymbol { IsLocal: true } i }) return null;
            if (!i.Type.IsNumeric || i.Type.IsPointer || StorageType(i.Type).ElementSize != 4 || i.Decl.Volatile || addressTaken.Contains(i)) return null;

            object? below = null;
            long? known = ConstantFolder.Evaluate(cmp.Right)
                ?? cmp.Right switch
                {
                    NameExpr { Symbol: LengthSymbol { Count: int count } } => count,
                    MemberExpr { Count: int count } => count,
                    _ => null,
                };
            if (known is long n) below = cmp.Op == BinaryOp.Less ? n : n + 1;
            else if (cmp.Op == BinaryOp.Less && cmp.Right is NameExpr { Symbol: LengthSymbol { Open: VariableSymbol open, Max: false } }) below = open;
            if (below == null) return null;

            // nothing in the body sets it
            bool sets = loop.Body.SelectMany(SyntaxWalker.Nodes).Any(n => n switch
            {
                AssignStmt a => Names(a.Target, i),
                CallStmt { ResultTarget: Expr target } => Names(target, i),
                AskStmt a => Names(a.Target, i),
                StringBuildStmt b => Names(b.Target, i),
                VarDecl d => d.Symbol == i,
                _ => false,
            });
            if (sets) return null;

            // an array parameter the body only indexes (adding to it, or passing it on, could
            // change how many it has)
            if (below is VariableSymbol array)
            {
                var nodes = loop.Body.SelectMany(SyntaxWalker.Nodes).ToList();
                int named = nodes.Count(n => n is NameExpr { Symbol: VariableSymbol v } && v == array);
                int indexed = nodes.Count(n => n is IndexExpr { Target: NameExpr { Symbol: VariableSymbol v } } && v == array);
                if (named != indexed) return null;
            }

            bool unsigned = cmp.Left.Type.IsUnsigned32 || cmp.Right.Type.IsUnsigned32;
            if (!unsigned)
            {
                // a signed counter: from zero or more, only going up
                bool startsAtZeroOrMore = loop.Init?.Symbol == i
                    && (loop.Init.Values.Count == 0 || ConstantFolder.Evaluate(loop.Init.Values[0]) is >= 0);
                bool goesUp = loop.Step is AssignStmt { Value: BinaryExpr { Op: BinaryOp.Add } step } stepAssign
                    && Names(stepAssign.Target, i) && Names(step.Left, i) && ConstantFolder.Evaluate(step.Right) is >= 0;
                if (!startsAtZeroOrMore || !goesUp) return null;
            }
            return (i, below);
        }

        static bool Names(Expr e, VariableSymbol v) => e is NameExpr { Call: null, Symbol: VariableSymbol named } && named == v;

        /// <summary>True when the element's index is a counter its loop keeps below the array's length.</summary>
        bool ProvedInRange(IndexExpr ix)
        {
            if (ix.Index is not NameExpr { Call: null, Symbol: VariableSymbol i }) return false;
            for (int k = inRange.Count - 1; k >= 0; k--)
            {
                if (inRange[k].Counter != i) continue;
                return inRange[k].Below switch
                {
                    long n => ix.CheckLimit is int limit && n <= limit,
                    VariableSymbol open => ix.Target is NameExpr { Symbol: VariableSymbol same } && same == open,
                    _ => false,
                };
            }
            return false;
        }

        // ================================================================ atomic steps and locks

        /// <summary>
        /// The locked blocks around the code being generated, innermost last: where each keeps
        /// its lock's address and the flags from before it (so interrupts go back as they were),
        /// how many loops were around it, and how deep in inlining it is.
        /// </summary>
        readonly List<(string Slot, int Loops, int Inlining)> lockedBlocks = new();

        /// <summary>
        /// locked guard ... ;; takes the lock with xchg (spinning while another core has it,
        /// with interrupts back on), runs the body with this core's interrupts off, and gives it
        /// back: at the end, and on a return, break or continue that leaves the block. A program
        /// leaves interrupts alone (they're its system's, and cli would stop it): it only takes
        /// the lock, waiting while another thread has it. The block keeps three slots (each as
        /// wide as an address): the lock's address, the flags from before it, and the catches.
        /// </summary>
        void EmitLocked(LockedStmt locked)
        {
            bool kernel = unit.Kind != UnitKind.Program;
            string slot = AllocTemp(3 * Slot);
            string spin = NewLabel("lock"), wait = NewLabel("lockwait"), held = NewLabel("locked");
            EmitPlaceAddress(locked.Lock);
            Emit($"mov [{slot}], {R("eax")}          ; the lock's address");
            if (kernel)
            {
                Emit("pushfd");
                Emit($"pop {R("ecx")}");
                Emit($"mov [{slot} + {Slot}], {R("ecx")}      ; interrupts as they were");
            }
            Label(spin);
            if (kernel) Emit("cli");
            Emit("mov ecx, 1");
            Emit("xchg [eax], ecx           ; take it if it's free (0)");
            Emit("test ecx, ecx");
            Emit($"jz {held}");
            if (kernel)
            {
                Emit($"push {Q} [{slot} + {Slot}]");
                Emit("popfd                    ; another core has it: interrupts as they were while waiting");
            }
            Label(wait);
            Emit("pause");
            Emit("cmp dword [eax], 0");
            Emit($"jne {wait}");
            Emit($"jmp {spin}");
            Label(held);
            if (Catches)
            {
                // what the lock guards may be half changed when something goes wrong in here, so
                // no error is caught until it's given back: it stops the program
                EmitBlockField($"mov {R("ecx")}, {Q} [{{0}}]", "CATCH");
                Emit($"mov [{slot} + {2 * Slot}], {R("ecx")}");
                EmitBlockField($"mov {Q} [{{0}}], 0", "CATCH");
            }

            lockedBlocks.Add((slot, loops.Count, inlining.Count));
            foreach (Stmt s in locked.Body) EmitStatement(s);
            lockedBlocks.RemoveAt(lockedBlocks.Count - 1);
            EmitUnlock(slot);
        }

        // ================================================================ catching errors

        /// <summary>ErrorKind.THROWN, the kind of error a throw makes (lib/phi/lang/error.phi).</summary>
        const int ErrorThrown = 0;

        /// <summary>ErrorKind.COUNT, a Mem call's count that can't be right.</summary>
        const int ErrorCount = 6;

        /// <summary>ErrorKind.HIERARCHY, an object taking the object it's inside of, found as it runs.</summary>
        const int ErrorHierarchy = 10;

        /// <summary>ErrorKind.CONVERT, a number that comes out as a different number where it went.</summary>
        const int ErrorConvert = 7;

        /// <summary>The bytes of an Error, as the runtime (throw.asm) lays it out: kind, line, file, text, message.</summary>
        const int ErrorSize = 4 + 4 + 65 + 256 + 384;

        /// <summary>
        /// True when the program catches errors (an if call ... throws err somewhere): then each
        /// method with something to give back at its end (objects, texts that grow) keeps a
        /// record of itself while it runs, so a throw can give those back on its way out.
        /// </summary>
        /// <summary>
        /// Where the innermost catch, the innermost method with things to give back and the error
        /// being thrown are kept: in a kernel, in this core's block (fs, interrupts.asm), so
        /// threads on several cores catch errors of their own and the scheduler keeps each task's;
        /// in a program, in each thread's block (throw.asm), through gs (PHI OS, 32-bit Linux) or
        /// fs (64-bit Linux). Windows keeps fs and gs for itself, so there the block's address is
        /// in a TLS slot (null here).
        /// </summary>
        string? BlockSegment => unit.Kind == UnitKind.Kernel || Host == Host.Linux64 ? "fs"
            : Host.IsWindows() ? null : "gs";
        string BlockField(string field) => unit.Kind == UnitKind.Kernel ? $"PHI_CORE_{field}" : $"PHI_THREAD_{field}";

        /// <summary>The catch field as a memory operand, where there's a segment for it (not on Windows).</summary>
        string CatchTop => $"{BlockSegment}:{BlockField("CATCH")}";

        /// <summary>
        /// An instruction on a field of this core's or thread's block (CATCH, UNWIND): {0} in it
        /// is the field's address. On Windows the block is loaded into a register that the
        /// instruction doesn't use, kept on the stack meanwhile.
        /// </summary>
        void EmitBlockField(string instruction, string field)
        {
            if (BlockSegment is string segment)
            {
                Emit(string.Format(instruction, $"{segment}:{BlockField(field)}"));
                return;
            }
            string spare = instruction.Contains("dx") ? "ecx" : "edx";
            Emit($"push {R(spare)}");
            EmitLoadBlock(spare);
            Emit(string.Format(instruction, $"{R(spare)} + {BlockField(field)}"));
            Emit($"pop {R(spare)}");
        }

        /// <summary>A register (named as in 32-bit code) = this core's or thread's block.</summary>
        void EmitLoadBlock(string register)
        {
            if (BlockSegment is string segment)
            {
                Emit($"mov {R(register)}, [{segment}:{BlockField("SELF")}]");
                return;
            }
            // the TLS slot phi_tls_index in the thread's TEB (its TlsSlots: fs:0xE10, or gs:0x1480 in 64-bit code)
            Need("phi_tls_index");
            if (bits64)
            {
                Emit($"mov {register}, [rel phi_tls_index]");
                Emit($"mov {R(register)}, [gs:0x1480 + {R(register)} * 8]");
            }
            else
            {
                Emit($"mov {register}, [phi_tls_index]");
                Emit($"mov {register}, [fs:0xE10 + {register} * 4]");
            }
        }

        /// <summary>
        /// True when the code says where each line starts, for an error from the CPU to say its
        /// line: every program, as one that catches nothing still prints it, and a kernel that catches.
        /// </summary>
        bool MarksLines => Catches || unit.Kind == UnitKind.Program;

        bool Catches => catches ??= bits32 && (program.Library != null || unit.CompiledLibraries.Count > 0 || unit.Classes.SelectMany(c => c.Body)
            .Concat(unit.Methods.SelectMany(m => m.Decl.Body))
            .SelectMany(SyntaxWalker.Nodes).Any(n => n is ThrowsExpr));
        bool? catches;

        /// <summary>
        /// if call Load: path throws err  ... ;;  else ... ;;
        ///
        /// Before the call, a catch record goes on the list a throw looks at (phi_catch_top):
        /// the catch before it, esp and ebp here, the methods' records at this point
        /// (phi_unwind_top), where to go, and how deep this thread (or core) is in the log's
        /// lock (phi_log_held), which the throw puts back: an error in a log's value doesn't
        /// keep the lock. A throw gives back what each method in between
        /// owns, puts esp and ebp back, and jumps there, with the error in phi_error; a call
        /// that returns takes the record off again. The statement runs with the variables in
        /// memory (it's fenced), since a throw doesn't come back through the methods that would
        /// have put esi and edi back.
        /// </summary>
        void EmitIfThrows(IfStmt ifs, ThrowsExpr throws)
        {
            Need("phi_throw");
            // (each field is 8 bytes in 64-bit code)
            string record = AllocTemp(6 * Slot);
            string caught = NewLabel("caught"), elseLabel = NewLabel("else"), endLabel = NewLabel("endif");
            EmitBlockField($"mov {R("eax")}, {Q} [{{0}}]", "CATCH");
            Emit($"mov {Q} [{record}], {R("eax")}         ; the catch around this one");
            Emit($"mov {Q} [{record} + {Slot}], esp");
            Emit($"mov {Q} [{record} + {2 * Slot}], ebp");
            EmitBlockField($"mov {R("eax")}, {Q} [{{0}}]", "UNWIND");
            Emit($"mov {Q} [{record} + {3 * Slot}], {R("eax")}    ; the methods running now");
            LoadAddress("eax", caught);
            Emit($"mov {Q} [{record} + {4 * Slot}], {R("eax")}");
            Need("phi_log_held");
            Emit("call phi_log_held");
            Emit($"mov {Q} [{record} + {5 * Slot}], {R("eax")}    ; how deep in the log's lock");
            LoadAddress("eax", record);
            EmitBlockField($"mov {Q} [{{0}}], {R("eax")}", "CATCH");
            EmitStatement(throws.Call);
            Emit($"mov {R("eax")}, {Q} [{record}]");
            EmitBlockField($"mov {Q} [{{0}}], {R("eax")}     ; it came back: this catch is done", "CATCH");
            Emit($"jmp {elseLabel}");

            // the error: err (declared first in Then) gets a copy of it. Whatever was being
            // worked out where it was thrown is gone, the FPU's part of it too.
            Label(caught);
            if (bits32) EmitFloatReset();
            var decl = (VarDecl)ifs.Then[0];
            EmitStatement(decl);
            if (decl.Symbol is VariableSymbol err)
            {
                if (err.Type.Struct?.Size != ErrorSize)
                    diagnostics.Error(file, decl.Span, $"the Error struct is {err.Type.Struct?.Size} bytes, and the runtime's is {ErrorSize}; lib/phi/lang/error.phi and throw.asm must agree");
                LoadAddress("edi", err.Label);
                // this core's error, or this thread's (the block's, through fs or gs)
                EmitLoadBlock("esi");
                Emit($"add {R("esi")}, {BlockField("ERROR")}");
                Emit($"mov ecx, {ErrorSize}");
                Emit("rep movsb");
            }
            // a kernel's throw turned interrupts off, so nothing came between it and the copy
            // (another error on this core, or this code moving to another): they're back now
            if (unit.Kind == UnitKind.Kernel)
            {
                Emit("push dword [fs:PHI_CORE_FLAGS]");
                Emit("popfd");
            }
            foreach (Stmt s in ifs.Then.Skip(1)) EmitStatement(s);
            Emit($"jmp {endLabel}");

            Label(elseLabel);
            if (ifs.Else != null)
                foreach (Stmt s in ifs.Else) EmitStatement(s);
            Label(endLabel);
        }

        /// <summary>Gives a lock back (through ecx, so a result in eax or edx:eax stays).</summary>
        void EmitUnlock(string slot)
        {
            if (Catches)
            {
                Emit($"mov {R("ecx")}, [{slot} + {2 * Slot}]");
                EmitBlockField($"mov {Q} [{{0}}], {R("ecx")}      ; errors can be caught again", "CATCH");
            }
            Emit($"mov {R("ecx")}, [{slot}]");
            Emit("mov dword [ecx], 0        ; the lock back");
            if (unit.Kind != UnitKind.Program)
            {
                Emit($"push {Q} [{slot} + {Slot}]");
                Emit("popfd                    ; and interrupts as they were");
            }
        }

        /// <summary>
        /// Gives back the locks a jump leaves, innermost first: the blocks inside the innermost
        /// loop for a break or continue (loops is how many loops there are), or every block of
        /// this method (or of the method being inlined) for a return (-1).
        /// </summary>
        void UnlockLeaving(int loopCount)
        {
            for (int i = lockedBlocks.Count - 1; i >= 0; i--)
            {
                var (slot, around, level) = lockedBlocks[i];
                if (level != inlining.Count) break;
                if (loopCount >= 0 && around < loopCount) break;
                EmitUnlock(slot);
            }
        }

        static string SizedRegister(string register, int bytes) => bytes switch
        {
            1 => register[1] + "l",
            2 => register[1..],
            8 => "r" + register[1..],
            _ => register,
        };

        /// <summary>atomic x++ n; atomic x-- n; atomic x is v; atomic x is v when old;</summary>
        void EmitAtomic(AtomicStmt atomic)
        {
            if (atomic.Op == AtomicOp.CompareSwap)
            {
                EmitCompareSwap(atomic.Target, atomic.Value, atomic.Expected!);
                return;
            }
            int bytes = StorageType(atomic.Target.Type).ElementSize;
            string size = bytes switch { 1 => "byte", 2 => "word", 8 => "qword", _ => "dword" };
            EmitExpr(atomic.Value);
            Emit("push eax");
            Place p = EmitPlace(atomic.Target);
            Emit("pop eax");
            string value = SizedRegister("eax", bytes);
            Emit(atomic.Op switch
            {
                AtomicOp.Add => $"lock add {size} {Operand(p)}, {value}",
                AtomicOp.Subtract => $"lock sub {size} {Operand(p)}, {value}",
                _ => $"xchg {size} {Operand(p)}, {value}            ; (an exchange with memory is always locked)",
            });
        }

        /// <summary>eax = 1 if target was expected and is value now (lock cmpxchg), else 0.</summary>
        void EmitCompareSwap(Expr target, Expr value, Expr expected)
        {
            int bytes = StorageType(target.Type).ElementSize;
            string size = bytes switch { 1 => "byte", 2 => "word", 8 => "qword", _ => "dword" };
            EmitExpr(value);
            Emit("push eax");
            EmitExpr(expected);
            Emit("push eax");
            Place p = EmitPlace(target);
            Emit("pop eax                  ; what it should be now");
            Emit("pop ecx                  ; and what it becomes");
            Emit($"lock cmpxchg {size} {Operand(p)}, {SizedRegister("ecx", bytes)}");
            Emit("sete al");
            Emit("movzx eax, al");
        }

        // ================================================================ inlining

        /// <summary>The methods being inlined, innermost last.</summary>
        readonly List<MethodSymbol> inlining = new();

        /// <summary>Whether each method is small and simple enough to inline (worked out once).</summary>
        readonly Dictionary<MethodSymbol, bool> inlinable = new();

        /// <summary>The most syntax nodes a method may have to be inlined.</summary>
        const int InlineNodes = 40;

        /// <summary>
        /// True when a call to the method is written out in place instead (32-bit code): a small
        /// method with no loops, whose parameters, variables and result are each one register,
        /// and whose every statement runs with the caller's variables in registers (so none needs
        /// a fence). Two levels at most, and never a method into itself.
        /// </summary>
        bool CanInline(MethodSymbol m)
        {
            if (m.Decl.IsCompiled) return false;   // its code is in its library
            // (not in 64-bit code yet. EmitInline can do it, and the tests pass with it on, but
            // measured on weblib's tests it made the programs 42% bigger and no faster, at this
            // limit or at 8, 14 or 20 nodes: an inlined method's parameters are still in memory.)
            if (debugInfo != null || !bits32 || bits64 || m == currentMethod || inlining.Contains(m) || inlining.Count >= 2) return false;
            if (!inlinable.TryGetValue(m, out bool can))
            {
                inlinable[m] = false;   // (a method that reaches itself isn't inlined)
                can = Inlinable(m);
                inlinable[m] = can;
            }
            return can;
        }

        bool Inlinable(MethodSymbol m)
        {
            if (m.IsHook || m.IsInterruptHandler) return false;
            if (!(m.ReturnType.Kind == TypeKind.Void || OneRegister(m.ReturnType))) return false;
            if (!m.Parameters.All(p => OneRegister(p.Type) && !p.TextRef && p.Decl.AtAddress == null)) return false;
            if (!m.Locals.All(v => OneRegister(v.Type) && v.Ownership == Ownership.None && v.Decl.AtAddress == null && !v.Decl.IsNew)) return false;

            var nodes = m.Decl.Body.SelectMany(SyntaxWalker.Nodes)
                .Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>()).ToList();
            if (nodes.Count > InlineNodes) return false;
            // its variables live in the caller's frame, so nothing may take their address
            if (nodes.Any(n => n is AddrExpr { Operand: NameExpr { Symbol: VariableSymbol { IsLocal: true } } })) return false;

            PhiType outer = resultType;
            resultType = m.ReturnType;
            bool safe = InlineSafe(m.Decl.Body) && (m.Decl.Result == null || OneRegister(m.Decl.Result.Type) && SafeValue(m.Decl.Result));
            resultType = outer;
            return safe;
        }

        bool InlineSafe(List<Stmt> body) => body.All(s => s switch
        {
            IfStmt i => SafeValue(i.Condition) && InlineSafe(i.Then) && (i.Else == null || InlineSafe(i.Else)),
            UnsafeStmt u => InlineSafe(u.Body),
            WhileStmt or WhileEachStmt or WhenStmt or BreakStmt or ContinueStmt => false,
            _ => SafeStatement(s),
        });

        /// <summary>
        /// A call written out in place, its result in eax. The method's parameters and variables
        /// get room in this frame for the call (each call its own, so a call in an argument can't
        /// change another's); a parameter the method never changes, given a variable that's in a
        /// register, is that register.
        /// </summary>
        void EmitInline(MethodSymbol m, List<Expr> arguments)
        {
            var labels = m.Parameters.Concat(m.Locals).Select(v => (Variable: v, Label: v.Label)).ToList();
            var changed = m.Decl.Body.SelectMany(SyntaxWalker.Nodes).SelectMany(n => n switch
            {
                AssignStmt a => new[] { a.Target },
                CallStmt { ResultTarget: Expr target } => new[] { target },
                _ => Array.Empty<Expr>(),
            }).OfType<NameExpr>().Select(n => n.Symbol).OfType<VariableSymbol>().ToHashSet();

            // the arguments first, all of them, before any parameter's place changes
            var places = new List<(VariableSymbol Variable, string Label, string? Register)>();
            Comment($"{m.Name}, written out here ({FileName(m.Decl.Owner.File)})");
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                VariableSymbol p = m.Parameters[i];
                Expr? value = i < arguments.Count ? arguments[i] : p.Decl.Values.FirstOrDefault();

                // (in 64-bit code a pointer is 8 bytes: its room, and its register, are whole ones)
                bool whole = WholeVariable(p);
                if (value is NameExpr { Symbol: VariableSymbol given } && !changed.Contains(p)
                    && (whole ? WholeVariable(given) : StorageType(given.Type).ElementSize == 4 && StorageType(p.Type).ElementSize == 4)
                    && inRegisters.FirstOrDefault(r => r.Variable == given) is { Variable: not null } held && registersLive)
                {
                    places.Add((p, held.Home, held.Register));
                    continue;
                }

                string slot = AllocTemp(whole ? 8 : 4);
                if (whole)
                {
                    // (a constant goes in as a pointer's does anywhere: one that fits in 32 bits as
                    // it is, a bigger one through eax, which clears the top)
                    if (value == null) Emit($"mov qword [{slot}], 0");
                    else if (ConstantFolder.Evaluate(value) is long k && k is >= int.MinValue and <= int.MaxValue)
                        Emit($"mov qword [{slot}], {(int)k}");
                    else
                    {
                        EmitExpr(value);
                        Emit($"mov qword [{slot}], rax");
                    }
                }
                else if (value == null) Emit($"mov dword [{slot}], 0");
                else if (DirectOperand(value, constantOnly: false) is string asIs && !asIs.Contains('['))
                    Emit($"mov dword [{slot}], {asIs}");
                else
                {
                    EmitExpr(value);
                    Emit($"mov dword [{slot}], eax");
                }
                places.Add((p, slot, null));
            }
            foreach (VariableSymbol v in m.Locals)
            {
                string slot = AllocTemp(WholeVariable(v) ? 8 : 4);
                if (!v.Decl.Values.Any() && !v.NeedsInitCode) Emit($"mov {(WholeVariable(v) ? "qword" : "dword")} [{slot}], 0");   // starts at zero, as it would
                places.Add((v, slot, null));
            }

            // the method's own code, in its own file, returning to the end of it
            var outer = (file, returnLabel, returnUsed, resultType, lastComment);
            int aliases = 0;
            foreach (var (v, label, register) in places)
            {
                if (register == null)
                {
                    v.Label = label;
                    continue;
                }
                inRegisters.Add((v, register, label));
                v.Label = $"«{v.Name}»";
                aliases++;
            }
            inlining.Add(m);
            file = m.Decl.Owner.File;
            returnLabel = NewLabel("inlined");
            returnUsed = false;
            resultType = m.ReturnType;

            foreach (Stmt s in m.Decl.Body) EmitStatement(s);
            if (m.Decl.Result != null)
            {
                SourceComment(m.Decl.Result);
                EmitExpr(m.Decl.Result);
            }
            if (returnUsed) Label(returnLabel);
            Comment($"{m.Name}, written out up to here");

            inlining.RemoveAt(inlining.Count - 1);
            inRegisters.RemoveRange(inRegisters.Count - aliases, aliases);
            foreach (var (v, label) in labels) v.Label = label;
            (file, returnLabel, returnUsed, resultType, _) = outer;
            lastComment = default;  // the caller's next line gets its comment again
        }

        /// <summary>
        /// After a call: each element of a str array that grows that was passed as a caller's text
        /// (PushTextRef) gets back where its text went, and its room. Keeps eax (the result).
        /// </summary>
        void EmitTextWriteBacks(List<string> writeBacks)
        {
            foreach (string slots in writeBacks)
            {
                Emit("push eax");
                Emit($"mov {R("esi")}, {Q} [{slots} + {TextSlot}]");
                Emit($"mov {R("eax")}, {Q} [{slots}]");
                Emit($"mov {Q} [esi], {R("eax")}");
                Emit($"mov {R("esi")}, {Q} [{slots} + {TextSlot + Word}]");
                Emit($"mov eax, [{slots} + {RoomAt}]");
                Emit("mov [esi], eax");
                Emit("pop eax");
            }
        }

        /// <summary>
        /// Pushes the address of a str for a str parameter with no default (str into;): two u32s,
        /// where its text is and its room. A str that grows passes its own (so the method can make
        /// it bigger); an element of a str array that grows passes a copy that's written back after
        /// the call; text with a fixed size passes its room with the Fixed bit; and anything else
        /// (a literal, a ptr&lt;u8&gt;, a number), which the method only reads, passes no room.
        /// </summary>
        void PushTextRef(Expr value, List<string> writeBacks)
        {
            switch (value)
            {
                case NameExpr { Symbol: VariableSymbol { Grows: true } v }:
                    LoadAddress("eax", v.Label);
                    Emit("push eax");
                    return;

                case NameExpr { Symbol: VariableSymbol { TextRef: true } v }:
                    Emit($"push dword [{v.Label}]   ; the same str this method was given");
                    return;

                case Expr { Type.IsHeapText: true }:
                    EmitPlaceAddress(value);    // its slot: the text and its room, so it can grow
                    Emit("push eax");
                    return;

                case IndexExpr { Target.Type: { Open: true, TextTable: true } } fieldText when Binder.IsFieldView(fieldText.Target):
                {
                    // an element of a struct's str array without a size: its text, and its room
                    // .max slots on
                    string slots = AllocTemp(TextSlot + 2 * Word);
                    string rooms = RoomsOf(fieldText.Target);
                    EmitPlaceAddress(value);
                    Emit($"mov {Q} [{slots} + {TextSlot}], {R("eax")}   ; the element's text slot");
                    Emit($"mov {R("esi")}, {R("eax")}");
                    Emit($"mov {R("ecx")}, {Q} [esi]");
                    Emit($"mov {Q} [{slots}], {R("ecx")}");
                    Emit($"mov ecx, {rooms}");
                    Emit($"lea esi, [esi + ecx*{Word}]");
                    Emit("mov ecx, [esi]");
                    Emit($"mov [{slots} + {RoomAt}], ecx");
                    Emit($"mov {Q} [{slots} + {TextSlot + Word}], {R("esi")}   ; and its room");
                    LoadAddress("eax", slots);
                    Emit("push eax");
                    writeBacks.Add(slots);
                    return;
                }

                case IndexExpr { Target: NameExpr { Symbol: VariableSymbol array } } when array.GrowingElements || array.Type is { Open: true, TextTable: true }:
                {
                    string slots = AllocTemp(TextSlot + 2 * Word);
                    EmitPlaceAddress(value);
                    Emit($"mov {Q} [{slots} + {TextSlot}], {R("eax")}   ; the element's text slot");
                    Emit($"mov {R("esi")}, {R("eax")}");
                    Emit($"mov {R("ecx")}, {Q} [esi]");
                    Emit($"mov {Q} [{slots}], {R("ecx")}");
                    if (array.GrowingElements) Emit($"lea esi, [esi + {Word * array.Count}]");
                    else
                    {
                        Emit($"mov ecx, [{array.Label} + {ViewMax}]");
                        Emit($"lea esi, [esi + ecx*{Word}]");
                    }
                    Emit("mov ecx, [esi]");
                    Emit($"mov [{slots} + {RoomAt}], ecx");
                    Emit($"mov {Q} [{slots} + {TextSlot + Word}], {R("esi")}   ; and its room");
                    LoadAddress("eax", slots);
                    Emit("push eax");
                    writeBacks.Add(slots);
                    return;
                }
            }

            int room = 0;
            if (value.Type.IsInlineText) room = value.Type.Room;
            else if (value is NameExpr { Symbol: VariableSymbol { Type.IsString: true, Type.IsArray: false } fixedStr }) room = fixedStr.Capacity;
            else if (value is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { Type.IsArray: true } table } } && value.Type.IsString) room = table.Capacity;

            string slot = AllocTemp(TextSlot);
            if (IsText(value.Type)) EmitExpr(value);
            else
            {
                // a number, written out as text first
                if (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar) EmitNumberText(value);
                else
                {
                    EmitExpr(value);
                    string text = AllocTemp(12);
                    Need("phi_int_to_str");
                    LoadAddress("edi", text);
                    Emit("mov ecx, 12");
                    Emit("call phi_int_to_str");
                    LoadAddress("eax", text);
                }
            }
            Emit($"mov {Q} [{slot}], {R("eax")}");
            if (room > 0) Emit($"mov dword [{slot} + {RoomAt}], {room} | {Fixed}");
            else
            {
                // text the method only reads: its room is what it has, so .max is its length
                Need("phi_strlen");
                Emit($"mov {R("esi")}, {R("eax")}");
                Emit("call phi_strlen");
                Emit("inc eax");
                Emit($"or eax, {Fixed}");
                Emit($"mov [{slot} + {RoomAt}], eax");
            }
            LoadAddress("eax", slot);
            Emit("push eax");
        }

        /// <summary>The bytes a parameter takes on the stack.</summary>
        int ParameterBytes(PhiType t) => bits64 ? (t.Open ? 16 : Slot) : t.Open ? 12 : t.ElementSize == 8 ? 8 : 4;

        /// <summary>
        /// Pushes an array for an array parameter: the most it holds, how many it has, and where its
        /// elements are (so they're at [slot], [slot + 4] and [slot + 8] in the method).
        /// </summary>
        int PushArrayView(Expr? array)
        {
            // in 64-bit code a view is laid out as in memory, 16 bytes: its elements' address,
            // then how many it has and the most it holds (pushed as one 8-byte value)
            if (bits64) return PushArrayView64(array);
            if (array == null)
            {
                Emit("push dword 0");
                Emit("push dword 0");
                Emit("push dword 0            ; no array: none");
                return 12;
            }
            if (array is PointerViewExpr pointerView)
            {
                EmitExpr(pointerView.Count);
                Emit("push eax                ; the most it holds");
                Emit("push eax                ; how many it has");
                EmitExpr(pointerView.Pointer);
                Emit("push eax                ; its elements");
                return 12;
            }
            PhiType t = array.Type;
            if (t.Open)
            {
                EmitViewAddress(array);
                Emit("push dword [eax + 8]");
                Emit("push dword [eax + 4]");
                Emit("push dword [eax]");
                return 12;
            }
            Emit($"push dword {t.Length}     ; the most it holds");
            EmitArrayCount(array);
            Emit("push eax                ; how many it has");
            EmitPlaceAddress(array);
            Emit("push eax                ; its elements");
            return 12;
        }

        int PushArrayView64(Expr? array)
        {
            if (array == null)
            {
                Emit("push qword 0");
                Emit("push qword 0            ; no array: none");
                return 16;
            }
            if (array is PointerViewExpr pointerView)
            {
                EmitExpr(pointerView.Count);
                Emit("mov eax, eax");
                Emit("mov rdx, rax");
                Emit("shl rdx, 32");
                Emit("or rax, rdx              ; how many it has, and the most it holds, the same");
                Emit("push rax");
                EmitExpr(pointerView.Pointer);
                Emit("push rax                ; its elements");
                return 16;
            }
            if (array.Type.Open)
            {
                EmitViewAddress(array);
                Emit($"push qword [rax + {ViewCount}]   ; how many and the most, together");
                Emit("push qword [rax]");
                return 16;
            }
            EmitArrayCount(array);
            Emit($"mov edx, {array.Type.Length}     ; the most it holds");
            Emit("shl rdx, 32");
            Emit("or rax, rdx              ; and how many it has, below it");
            Emit("push rax");
            EmitPlaceAddress(array);
            Emit("push rax                ; its elements");
            return 16;
        }

        /// <summary>Where a view of an array (a parameter, or one kept in memory) keeps how many it has, and the most it holds.</summary>
        int ViewCount => bits64 ? 8 : 4;
        int ViewMax => bits64 ? 12 : 8;

        /// <summary>
        /// Pushes an argument for a 64-bit or floating point parameter (0 when there's no value):
        /// an i64 or f64 takes 8 bytes, the others 4 (an f16 in the low half). Gives the bytes pushed.
        /// </summary>
        int PushWideOrFloat(PhiType type, Expr? value)
        {
            if (type.IsWide)
            {
                if (value == null)
                {
                    Emit("xor eax, eax");
                    Emit("xor edx, edx");
                }
                else EmitWide(value);
                if (bits64)
                {
                    // one 8-byte argument, as the halves are in memory
                    Emit("shl rdx, 32");
                    Emit("mov eax, eax");
                    Emit("or rax, rdx");
                    Emit("push eax");
                    return 8;
                }
                Emit("push edx");
                Emit("push eax");
                return 8;
            }

            if (value == null) Emit("fldz");
            else EmitFloat(value);
            if (type.Kind == TypeKind.F64)
            {
                Emit("sub esp, 8");
                Emit("fstp qword [esp]");
                return 8;
            }
            // (an argument takes 8 bytes in 64-bit code: an f32 is in its low half)
            Emit($"sub esp, {Slot}");
            Emit("fstp dword [esp]");
            if (type.Kind == TypeKind.F16)
            {
                Need("phi_f32_to_f16");
                Emit("mov eax, [esp]");
                Emit("call phi_f32_to_f16");
                Emit("mov [esp], eax");
            }
            return Slot;
        }

        /// <summary>Calls a one-parameter heap method (Heap.Alloc or Heap.Free) with a value.</summary>
        void EmitHeapCall(MethodSymbol method, Expr argument) =>
            EmitCallTo(method, new List<Expr> { argument });

        /// <summary>Stores a call result (in eax) into a variable.</summary>
        void EmitStoreResult(Expr target, PhiType resultType)
        {
            if (target.Type.IsFloat)
            {
                if (resultType.IsWide) PushAndLoad("qword", "edx", "eax");
                else if (!resultType.IsFloat) PushAndLoad("dword", "eax");
                EmitFloatStore(target);
                return;
            }
            if (target.Type.IsWide)
            {
                if (!resultType.IsWide) Emit(resultType.IsSigned ? "cdq" : "xor edx, edx");
                EmitWideStore(target);
                return;
            }
            if (target.Type.IsString) EmitStringStore(target, sourceIsString: IsText(resultType));
            else EmitStoreEax(target);
        }

        // ================================================================ memory places

        /// <summary>
        /// Where a value lives. Either a fixed address (Label + Offset), or Label + ebx + Offset
        /// when part of the address is computed, or, for memory reached through a pointer, a
        /// linear address in ebx (+ Offset) that may be anywhere in the first megabyte.
        ///
        /// With variables in registers (32-bit code) a computed place can be reached through them
        /// as they are: Base instead of ebx for the address, and Index times Scale added to it
        /// instead of ebx, so no code is needed to work the address out.
        /// </summary>
        sealed class Place
        {
            public string? Label;
            public int Offset;
            public bool Computed;   // ebx holds part of the address
            public bool Linear;     // ebx holds a linear address, not an offset from Label
            public string? Register; // a variable kept in a register: the value is there, not in memory
            public string Base = "ebx";
            public string? Index;
            public int Scale = 1;

            public Place Plus(int bytes) => new()
            {
                Label = Label, Offset = Offset + bytes, Computed = Computed, Linear = Linear,
                Register = Register, Base = Base, Index = Index, Scale = Scale,
            };

            /// <summary>What's added for the computed part: ebx, or a register variable times its scale.</summary>
            public string Added => Index == null ? "ebx" : Scale == 1 ? Index : $"{Index}*{Scale}";
        }

        Place EmitPlace(Expr e)
        {
            switch (e)
            {
                case NameExpr { Symbol: VariableSymbol v }:
                    return RegisterOf(v) is string register ? new Place { Register = register } : new Place { Label = v.Label };

                case NameExpr { Symbol: FieldPathSymbol f }:
                    return new Place { Label = f.Root.Label, Offset = f.Offset };

                case NameExpr { Symbol: SelfFieldSymbol s }:
                    // through the struct the method was called on
                    if (bits64 && RegisterOf(s.Self) is string self)
                        return new Place { Computed = true, Linear = true, Base = self, Offset = s.Offset };
                    Emit(bits64 ? $"mov rbx, qword [{s.Self.Label}]" : $"mov ebx, {VariableOperand(s.Self)}");
                    return new Place { Computed = true, Linear = true, Offset = s.Offset };

                case NameExpr { Symbol: PathSymbol path }:
                {
                    // through objects: read the address each one is at, then go on from there (a
                    // pointer in a register is the first address already; each is a whole
                    // address in 64-bit code)
                    string loadAddress = bits64 ? "mov rbx, qword" : "mov ebx, dword";
                    if (RegisterOf(path.Root) is string rootRegister && path.BaseOffset == 0)
                    {
                        if (path.Hops.Count == 1) return new Place { Computed = true, Linear = true, Base = rootRegister, Offset = path.Hops[0] };
                        Emit($"mov {R("ebx")}, {rootRegister}");
                    }
                    else Emit($"{loadAddress} {Operand(new Place { Label = path.Root.Label, Offset = path.BaseOffset })}");
                    for (int i = 0; i < path.Hops.Count - 1; i++)
                        Emit($"{loadAddress} {Operand(new Place { Computed = true, Linear = true, Offset = path.Hops[i] })}");
                    return new Place { Computed = true, Linear = true, Offset = path.Hops[^1] };
                }

                case MemberExpr m when m.Target.Type.IsObject || m.Target.Type.IsPointer:
                    // where the object (or the struct a pointer points at) is
                    if (HeldIn(m.Target) is string held)
                        return new Place { Computed = true, Linear = true, Base = held, Offset = m.Field!.Offset };
                    if (bits32 && TryLoadSimple(m.Target, "ebx", dryRun: true)) TryLoadSimple(m.Target, "ebx", dryRun: false);
                    else
                    {
                        EmitExpr(m.Target);
                        Emit($"mov {R("ebx")}, {R("eax")}");
                    }
                    return new Place { Computed = true, Linear = true, Offset = m.Field!.Offset };

                case MemberExpr m:
                {
                    Place p = EmitPlace(m.Target);
                    p.Offset += m.Field!.Offset;
                    return p;
                }

                case IndexExpr ix:
                    return EmitIndexPlace(ix);
            }

            throw new InvalidOperationException($"{e.GetType().Name} has no address");
        }

        Place EmitIndexPlace(IndexExpr ix)
        {
            PhiType target = ix.Target.Type;
            int size = target.IsString ? 1 : target.IsPointer ? target.Pointee!.ElementSize : target.ElementSize;
            long? constant = ConstantFolder.Evaluate(ix.Index);

            // an element of an array parameter: checked against how many it has, when it runs
            if (ix.Target is NameExpr { Symbol: VariableSymbol { Type.Open: true } open })
            {
                bool proved = ProvedInRange(ix);
                if ((proved || unsafeDepth > 0) && size is 1 or 2 or 4 or 8 && HeldIn(ix.Index) is string heldIndex)
                {
                    // known to be in range, with the index in a register: [elements + i*size]
                    Emit($"mov {R("ebx")}, {Q} [{open.Label}]");
                    return new Place { Computed = true, Linear = true, Index = IndexRegister(heldIndex, ix.Index, proved), Scale = size };
                }
                EmitExpr(ix.Index);
                if (unsafeDepth == 0 && !proved) EmitIndexCheck(ix, "jb", $"dword [{open.Label} + {ViewCount}]");
                EmitScale(size);
                if (bits64 && unsafeDepth > 0) Emit("movsxd rax, eax          ; (unchecked: it may be before the array)");
                Emit($"mov {R("ebx")}, {Q} [{open.Label}]");
                Emit($"add {R("ebx")}, {R("eax")}");
                return new Place { Computed = true, Linear = true };
            }

            // an element of a struct's array without a size: checked against how many it has
            if (Binder.IsFieldView(ix.Target))
            {
                // the index in a register variable, and the array kept somewhere reached without
                // eax (a field of self, say): its elements' address into ebx, and [ebx + i*size]
                if (bits32 && size is 1 or 2 or 4 or 8 && HeldIn(ix.Index) is string heldAt && PlaceKeepsEax(ix.Target))
                {
                    Place view = PlaceKeepingEax(ix.Target);
                    if (unsafeDepth == 0) EmitIndexCheck(ix, "jb", $"dword {Operand(view.Plus(ViewCount))}", heldAt);
                    Emit($"mov {R("ebx")}, {Q} {Operand(view)}");
                    return new Place { Computed = true, Linear = true, Index = IndexRegister(heldAt, ix.Index, knownPositive: unsafeDepth == 0), Scale = size };
                }
                EmitExpr(ix.Index);
                Emit("push eax");
                EmitViewAddress(ix.Target);
                Emit($"mov {R("edx")}, {R("eax")}");
                Emit("pop eax");
                if (unsafeDepth == 0) EmitIndexCheck(ix, "jb", $"dword [edx + {ViewCount}]");
                EmitScale(size);
                Emit($"mov {R("ebx")}, {Q} [edx]");
                Emit($"add {R("ebx")}, {R("eax")}");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of an element of a str array that grows: checked against its room
            if (ix.Target is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { GrowingElements: true } array } } element)
            {
                EmitExpr(ix.Index);
                Emit("push eax");
                EmitPlaceAddress(element);
                Emit($"mov {R("ebx")}, {R("eax")}");
                Emit("pop eax");
                if (unsafeDepth == 0)
                {
                    Emit($"mov edx, [ebx + {Word * array.Count}]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit($"mov {R("ebx")}, {Q} [ebx]");
                Emit($"add {R("ebx")}, {R("eax")}");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of a new str field: checked against its room
            if (ix.Target.Type.IsHeapText)
            {
                EmitExpr(ix.Index);
                Emit("push eax");
                EmitPlaceAddress(ix.Target);
                Emit($"mov {R("ebx")}, {R("eax")}");
                Emit("pop eax");
                if (unsafeDepth == 0)
                {
                    Emit($"mov edx, [ebx + {RoomAt}]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit($"mov {R("ebx")}, {Q} [ebx]");
                Emit($"add {R("ebx")}, {R("eax")}");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of the caller's str: checked against its room
            if (ix.Target is NameExpr { Symbol: VariableSymbol { TextRef: true } caller })
            {
                EmitExpr(ix.Index);
                Emit($"mov {R("ebx")}, {Q} [{caller.Label}]   ; where {caller.Name}'s text and room are");
                if (unsafeDepth == 0)
                {
                    Emit($"mov edx, [ebx + {RoomAt}]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit($"mov {R("ebx")}, {Q} [ebx]");
                Emit($"add {R("ebx")}, {R("eax")}");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of a str that grows: checked against its room, which can change
            if (ix.Target is NameExpr { Symbol: VariableSymbol { Grows: true } text })
            {
                EmitExpr(ix.Index);
                if (unsafeDepth == 0)
                {
                    Emit($"mov edx, [{text.Label} + {RoomAt}]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit($"mov {R("ebx")}, {Q} [{text.Label}]");
                Emit($"add {R("ebx")}, {R("eax")}");
                return new Place { Computed = true, Linear = true };
            }

            // through a pointer, or the characters of a str that isn't a plain variable
            bool viaValue = target.IsPointer || (target.IsString && ix.Target is not NameExpr { Symbol: VariableSymbol });

            // 64-bit code: the address in all 64 bits, then the index (a pointer's isn't checked,
            // so a negative one reaches before it)
            if (bits64 && viaValue)
            {
                // a pointer in a variable and a constant index: the address straight into rbx, so
                // eax keeps the value being stored (as PlaceKeepsEax says)
                if (PointerElementKeepsEax(ix))
                {
                    // (the pointer may be in a register variable, and so may the index: [p + i*size])
                    string address = HeldIn(ix.Target) ?? "ebx";
                    if (address == "ebx") TryLoadSimple(ix.Target, "ebx", dryRun: false);
                    return constant != null
                        ? new Place { Computed = true, Linear = true, Base = address, Offset = (int)constant.Value * size }
                        : new Place { Computed = true, Linear = true, Base = address, Index = IndexRegister(HeldIn(ix.Index)!, ix.Index, knownPositive: false), Scale = size };
                }
                // the pointer in a register variable, and an index worked out: one lea
                if (target.IsPointer && constant == null && size is 1 or 2 or 4 or 8 && HeldIn(ix.Target) is string heldPointer)
                {
                    EmitExpr(ix.Index);
                    if (ix.Index.Type.IsSigned) Emit("movsxd rax, eax");
                    else Emit("mov eax, eax");
                    Emit($"lea rbx, [{heldPointer} + rax*{size}]");
                    return new Place { Computed = true, Linear = true };
                }
                // a pointer and an index that are each one load: the two, then one lea, with
                // nothing on the stack
                if (target.IsPointer && constant == null && size is 1 or 2 or 4 or 8
                    && TryLoadSimple(ix.Target, "ebx", dryRun: true) && TryLoadSimple(ix.Index, "eax", dryRun: true))
                {
                    TryLoadSimple(ix.Target, "ebx", dryRun: false);
                    TryLoadSimple(ix.Index, "eax", dryRun: false);
                    if (ix.Index.Type.IsSigned) Emit("movsxd rax, eax");
                    Emit($"lea rbx, [rbx + rax*{size}]");
                    return new Place { Computed = true, Linear = true };
                }
                // a pointer that's one load, and an index that calls nothing (which could change
                // it): the index first, then the pointer straight into rbx, and one lea
                if (target.IsPointer && constant == null && size is 1 or 2 or 4 or 8 && ix.CheckLimit == null
                    && TryLoadSimple(ix.Target, "ebx", dryRun: true) && !HasCall(ix.Index))
                {
                    EmitExpr(ix.Index);
                    Emit(ix.Index.Type.IsSigned ? "movsxd rax, eax" : "mov eax, eax");
                    TryLoadSimple(ix.Target, "ebx", dryRun: false);
                    Emit($"lea rbx, [rbx + rax*{size}]");
                    return new Place { Computed = true, Linear = true };
                }
                EmitExpr(ix.Target);
                if (constant != null)
                {
                    Emit("mov rbx, rax");
                    return new Place { Computed = true, Linear = true, Offset = (int)constant.Value * size };
                }
                Emit("push rax");
                EmitIndexValue(ix, size);
                // (an int may be negative, and reaches before the pointer; a u32 never is, so one
                // of 2^31 or more reaches past it, not before)
                Emit(ix.Index.Type.IsSigned ? "movsxd rax, eax" : "mov eax, eax");
                Emit("pop rbx");
                Emit("add rbx, rax");
                return new Place { Computed = true, Linear = true };
            }

            // p:i with the index in a register variable (or a constant), and the pointer in one or in
            // memory: [p + i*size] as it is, with the pointer loaded into ebx at most
            if (PointerElementKeepsEax(ix))
            {
                string address = HeldIn(ix.Target) ?? "ebx";
                if (address == "ebx") TryLoadSimple(ix.Target, "ebx", dryRun: false);
                return constant != null
                    ? new Place { Computed = true, Linear = true, Base = address, Offset = (int)constant.Value * size }
                    : new Place { Computed = true, Linear = true, Base = address, Index = HeldIn(ix.Index), Scale = size };
            }

            // the same with a pointer that has to be worked out (map:0.keys:place)
            if (bits32 && target.IsPointer && IndexAsIs(ix))
            {
                // a pointer kept in a field or an element is loaded straight into ebx
                if (PlaceKeepsEax(ix.Target)) Emit($"mov ebx, dword {Operand(PlaceKeepingEax(ix.Target))}");
                else
                {
                    EmitExpr(ix.Target);
                    Emit($"mov {R("ebx")}, {R("eax")}");
                }
                return constant != null
                    ? new Place { Computed = true, Linear = true, Offset = (int)constant.Value * size }
                    : new Place { Computed = true, Linear = true, Index = HeldIn(ix.Index), Scale = size };
            }

            // the pointer in a register variable, and an index worked out: one lea
            if (target.IsPointer && constant == null && size is 1 or 2 or 4 or 8 && HeldIn(ix.Target) is string pointer)
            {
                EmitExpr(ix.Index);
                Emit($"lea ebx, [{pointer} + eax*{size}]");
                return new Place { Computed = true, Linear = true };
            }

            // p:i with a pointer and an index in variables: one scaled lea, nothing on the stack
            if (target.IsPointer && constant == null && size is 1 or 2 or 4 or 8
                && TryLoadSimple(ix.Target, "ebx", dryRun: true) && TryLoadSimple(ix.Index, "eax", dryRun: true))
            {
                TryLoadSimple(ix.Target, "ebx", dryRun: false);
                TryLoadSimple(ix.Index, "eax", dryRun: false);
                if (size == 1) EmitAddIndex();
                else Emit($"lea ebx, [ebx + eax*{size}]");
                return new Place { Computed = true, Linear = true };
            }

            if (viaValue)
            {
                EmitExpr(ix.Target);

                if (constant != null)
                {
                    Emit($"mov {R("ebx")}, {R("eax")}");
                    return new Place { Computed = true, Linear = true, Offset = (int)constant.Value * size };
                }
                Emit("push eax");
                EmitIndexValue(ix, size);
                Emit("pop ebx");
                EmitAddIndex();
                return new Place { Computed = true, Linear = true };
            }

            // an [atmost N] array: only the elements that are in it, however many fit
            if (target.Filled && unsafeDepth == 0)
            {
                EmitExpr(ix.Index);
                Emit("push eax");
                EmitFilledCount(ix.Target);
                Emit("mov edx, eax");
                Emit("pop eax");
                EmitIndexCheck(ix, "jb", "edx");
                EmitScale(size);
                Emit($"add {R("ebx")}, {R("eax")}   ; (the index is checked: at least 0)");
                return new Place { Computed = true, Linear = true };
            }

            Place p = EmitPlace(ix.Target);

            if (constant != null)
            {
                p.Offset += (int)constant.Value * size;
                return p;
            }

            // the index in a register variable: [array + i*size], once it's checked
            if (!p.Computed && size is 1 or 2 or 4 or 8 && HeldIn(ix.Index) is string index)
            {
                bool proved = ProvedInRange(ix);
                bool check = ix.CheckLimit is int && unsafeDepth == 0 && !proved;
                if (check) EmitIndexCheck(ix, "jb", ix.CheckLimit!.Value.ToString(), index);
                p.Computed = true;
                p.Index = IndexRegister(index, ix.Index, knownPositive: check || proved);
                p.Scale = size;
                return p;
            }

            if (p.Computed)
            {
                // (an address reached through a register variable goes into ebx first, which the
                // index is then added to)
                if (p.Linear && (p.Base != "ebx" || p.Index != null))
                {
                    Emit($"lea ebx, {Operand(p)}");
                    p = new Place { Computed = true, Linear = true };
                }
                Emit("push ebx");
                EmitIndexValue(ix, size);
                Emit("pop ebx");
                EmitAddIndex();
            }
            else
            {
                EmitIndexValue(ix, size);
                // (in 64-bit code, a negative one in unsafe code stays before the array)
                Emit(bits64 ? "movsxd rbx, eax" : "mov ebx, eax");
                p.Computed = true;
            }
            return p;
        }

        /// <summary>eax = index * size, after checking the index when needed.</summary>
        void EmitIndexValue(IndexExpr ix, int size)
        {
            EmitExpr(ix.Index);

            if (ix.CheckLimit is int limit && unsafeDepth == 0 && !ProvedInRange(ix))
                EmitIndexCheck(ix, "jb", limit.ToString());

            EmitScale(size);
        }

        /// <summary>
        /// when x: x is worked out once and kept in a temporary; each value is compared with it in
        /// order, and the first that's equal picks its branch.
        /// </summary>
        void EmitWhen(WhenStmt when)
        {
            PhiType t = when.Subject.Type;
            bool text = !t.IsNumeric && !t.IsWide && !t.IsFloat || t.IsTextPointer;
            string subject = AllocTemp(t.IsWide || t.IsFloat || text && bits64 ? 8 : 4);
            string end = NewLabel("endwhen");
            string otherwise = when.Else != null ? NewLabel("whenelse") : end;
            var bodies = when.Branches.Select(_ => NewLabel("whenis")).ToList();

            if (!text && DenseWhen(when) is var (lowest, highest) && highest >= lowest)
            {
                // numbers close together (an interpreter's opcodes, a state's enum): one jump
                // through a table of where each goes, however many there are. A number that's
                // there twice goes to its first branch, as the comparisons in order would.
                var targets = new Dictionary<long, string>();
                for (int i = 0; i < when.Branches.Count; i++)
                    foreach (Expr value in when.Branches[i].Values)
                        targets.TryAdd((int)ConstantFolder.Evaluate(value)!.Value, bodies[i]);
                string table = NewLabel("whentable");
                EmitExpr(when.Subject);
                if (lowest != 0) Emit($"sub eax, {lowest}");
                Emit($"cmp eax, {highest - lowest}");
                Emit($"ja {otherwise}              ; unsigned, so one below the lowest goes there too");
                if (bits64)
                {
                    Line("    mov eax, eax");
                    Line($"    jmp qword [{table} + rax*8]");
                }
                else Emit($"jmp dword [{table} + eax*4]");
                Label(table);
                for (long v = lowest; v <= highest; v++)
                    Line($"    {(bits64 ? "dq" : "dd")} {targets.GetValueOrDefault(v, otherwise)}");
            }
            else if (t.IsFloat)
            {
                EmitFloat(when.Subject);
                Emit($"fstp qword [{subject}]");
            }
            else if (t.IsWide)
            {
                EmitWide(when.Subject);
                Emit($"mov dword [{subject}], eax");
                Emit($"mov dword [{subject} + 4], edx");
            }
            else
            {
                EmitExpr(when.Subject);
                // (text is an address: all of it in 64-bit code)
                Emit(text && bits64 ? $"mov qword [{subject}], rax" : $"mov dword [{subject}], eax");
            }
            bool byTable = !text && DenseWhen(when) is var (first, last) && last >= first;
            if (when.Branches.Any(b => b.Struct != null))
            {
                // which struct the interface value holds, from the type table just before it
                Emit($"test {R("eax")}, {R("eax")}");
                Emit($"jz {otherwise}              ; empty: it holds nothing");
                Emit("mov eax, [eax - 4]");
            }
            for (int i = 0; i < when.Branches.Count && !byTable; i++)
            {
                if (when.Branches[i].Struct is StructSymbol type)
                {
                    Emit($"cmp eax, {TypeTable(type)}");
                    Emit($"je {bodies[i]}");
                }
                foreach (Expr value in when.Branches[i].Values)
                {
                    if (t.IsFloat)
                    {
                        string differs = NewLabel("whennot");
                        EmitFloat(value);
                        Emit($"fld qword [{subject}]");
                        Emit("fcompp");
                        Emit("fnstsw ax");
                        Emit("sahf");
                        Emit($"jp {differs}              ; not a number is equal to nothing");
                        Emit($"je {bodies[i]}");
                        Label(differs);
                    }
                    else if (t.IsWide)
                    {
                        string differs = NewLabel("whennot");
                        EmitWide(value);
                        Emit($"cmp eax, [{subject}]");
                        Emit($"jne {differs}");
                        Emit($"cmp edx, [{subject} + 4]");
                        Emit($"je {bodies[i]}");
                        Label(differs);
                    }
                    else if (text)
                    {
                        Need("phi_strcmp");
                        EmitExpr(value);
                        Emit($"mov {R("edi")}, {R("eax")}");
                        Emit($"mov {R("esi")}, [{subject}]");
                        Emit("call phi_strcmp          ; eax = 0 when equal");
                        Emit("test eax, eax");
                        Emit($"jz {bodies[i]}");
                    }
                    else if (ConstantFolder.Evaluate(value) is long k)
                    {
                        Emit($"cmp dword [{subject}], {(int)k}");
                        Emit($"je {bodies[i]}");
                    }
                    else
                    {
                        EmitExpr(value);
                        Emit($"cmp eax, [{subject}]");
                        Emit($"je {bodies[i]}");
                    }
                }
            }
            if (!byTable) Emit($"jmp {otherwise}");

            for (int i = 0; i < when.Branches.Count; i++)
            {
                Label(bodies[i]);
                foreach (Stmt s in when.Branches[i].Body) EmitStatement(s);
                Emit($"jmp {end}");
            }
            if (when.Else != null)
            {
                Label(otherwise);
                foreach (Stmt s in when.Else) EmitStatement(s);
            }
            Label(end);
        }

        /// <summary>
        /// The lowest and highest value of a when that goes by a jump table: 32- or 64-bit code,
        /// a subject that's one number, every value a constant, at least 5 of them, and close
        /// enough together that the table is no more than three entries a value. Otherwise
        /// (1, 0), and the values are compared one at a time.
        /// </summary>
        (long Lowest, long Highest) DenseWhen(WhenStmt when)
        {
            PhiType t = when.Subject.Type;
            if (!bits32 || !t.IsNumeric || t.IsWide || t.IsFloat || t.IsTextPointer || when.Branches.Any(b => b.Struct != null)) return (1, 0);
            var values = new HashSet<long>();
            foreach (Expr value in when.Branches.SelectMany(b => b.Values))
            {
                if (ConstantFolder.Evaluate(value) is not long k) return (1, 0);
                values.Add((int)k);
            }
            if (values.Count < 5) return (1, 0);
            long lowest = values.Min(), highest = values.Max();
            return highest - lowest < 3L * values.Count && highest - lowest < 4096 ? (lowest, highest) : (1, 0);
        }

        /// <summary>Stops on an index error unless the index (in eax) passes the unsigned jump against limit.</summary>
        void EmitIndexCheck(Node at, string jump, string limit, string index = "eax")
        {
            Need("phi_index_error");
            string ok = NewLabel("inbounds");
            Emit($"cmp {index}, {limit}");
            Emit($"{jump} {ok}                ; unsigned, so negative indexes fail too");
            EmitIndexError(at);
            Label(ok);
        }

        /// <summary>
        /// After phi_strcpy or a number written as text, with its carry still there: text that
        /// didn't fit is an error, unless it's a throw's own text (which is cut, so that the
        /// error is the one thrown) or the boot sector's (which has no room for the routine).
        /// </summary>
        void EmitTextFits()
        {
            if (textMayCut || unit.Kind == UnitKind.Boot) return;
            string ok = NewLabel("textfits");
            Emit($"jnc {ok}");
            EmitTextError();
            Label(ok);
        }

        /// <summary>The same, for a store with a text that may have moved: eax is 1 if it was cut (the old text is freed by now).</summary>
        void EmitTextFitsAfterFree()
        {
            if (textMayCut) return;
            string ok = NewLabel("textfits");
            Emit("test eax, eax");
            Emit($"jz {ok}");
            EmitTextError();
            Label(ok);
        }

        /// <summary>Keeps the carry phi_strcpy left in a stack slot, across the call that frees the old text.</summary>
        void EmitKeepCut(int offset)
        {
            if (textMayCut) return;
            Emit("setc al");
            Emit("movzx eax, al");
            Emit($"mov [esp + {offset}], {R("eax")}   ; whether it was cut");
        }

        bool textMayCut;

        void EmitTextError()
        {
            Need("phi_text_error");
            int line = lastComment.Item1 == file ? lastComment.Item2 : 0;
            StoreLoopHeld();
            Emit($"mov eax, {line}   ; it stops here, so eax is free");
            if (bits64) LoadAddress("esi", StringLabel(System.IO.Path.GetFileName(file.Path)));
            else Emit($"mov {(bits32 ? "esi" : "si")}, {StringLabel(System.IO.Path.GetFileName(file.Path))}   ; it stops here, so esi is free");
            Emit("jmp phi_text_error");
        }

        /// <summary>Stops with an index error naming the line and file of a node.</summary>
        void EmitIndexError(Node at)
        {
            StoreLoopHeld();
            Emit($"mov eax, {LineOf(at)}   ; it stops here, so eax is free");
            if (bits64) LoadAddress("esi", StringLabel(System.IO.Path.GetFileName(file.Path)));
            else Emit($"mov {(bits32 ? "esi" : "si")}, {StringLabel(System.IO.Path.GetFileName(file.Path))}   ; it stops here, so esi is free");
            Emit("jmp phi_index_error");
        }

        /// <summary>eax = eax * size, for an element's offset.</summary>
        void EmitScale(int size)
        {
            switch (size)
            {
                case 1: break;
                case 2: Emit("shl eax, 1"); break;
                case 4: Emit("shl eax, 2"); break;
                default: Emit($"imul eax, eax, {size}"); break;
            }
        }

        /// <summary>ebx = where an [atmost N] array is (linear), eax = how many elements are in it.</summary>
        void EmitFilledCount(Expr array)
        {
            EmitPlaceAddress(array);
            Emit($"mov {R("ebx")}, {R("eax")}");
            if (!bits32) Emit("push ebx");  // reaching memory in 16-bit code changes ebx
            Emit($"mov eax, dword {Operand(new Place { Computed = true, Linear = true, Offset = array.Type.CountOffset })}");
            if (!bits32) Emit("pop ebx");
        }

        /// <summary>One element at the end of an [atmost N] array: value (or zeros, for null).</summary>
        void EmitAppend(AppendStmt append, Expr? value)
        {
            PhiType t = append.Target.Type;
            PhiType element = t.Element;
            int size = element.ElementSize;

            // an array that grows moves its elements when it's out of room
            bool grows = Binder.IsFieldView(append.Target) || append.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true } };
            // a struct kept in place is copied from where it is, which can be in this array (list++ list:0)
            bool fromInside = grows && element.IsStruct && !element.IsObject && value != null;

            if (element.IsObject)
            {
                ObjectLine(append);
                if (value != null && Binder.IsObjectCall(value))
                {
                    EmitObjectAddress(value);
                    Emit("push eax                ; the call's new object");
                }
                else if (value != null)
                {
                    // The copy is made before the array changes: what's copied can hold this
                    // array (tree.kids++ tree), and would be copied with the new element in it.
                    if (!grows)
                    {
                        EmitFilledCount(append.Target);
                        EmitIndexCheck(append, "jb", t.Length.ToString());   // full: stop before anything is copied
                    }
                    EmitObjectAddress(value);
                    Emit($"call {ObjectRoutine("clone", element.Struct!)}");
                    Emit("push eax                ; the copy");
                }
            }
            else if (element.IsWide)
            {
                if (value == null) Emit("xor eax, eax");
                if (value == null) Emit("xor edx, edx");
                else EmitWide(value);
                Emit("push edx");
                Emit("push eax");
            }
            else if (element.IsFloat)
            {
                if (value == null) Emit("fldz");
                else EmitFloat(value);   // stays in st0 until it's stored
            }
            else if (element.IsStruct && value != null)
            {
                EmitObjectAddress(value);
                Emit("push eax                ; the struct to copy");
            }
            else if (element.IsString && value != null && (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar))
            {
                EmitNumberText(value);
                Emit("push eax                ; the number, as text");
            }
            else if (value != null)
            {
                EmitExpr(value);
                Emit("push eax");
            }
            if (Binder.IsFieldView(append.Target))
            {
                // room for one more (moving its elements when there isn't), then at its end
                ObjectLine(append);
                EmitViewAddress(append.Target);
                if (fromInside) Emit($"push {Q} [eax]          ; where its elements are now");
                Emit($"mov edx, [eax + {ViewCount}]");
                Emit("inc edx");
                Emit($"mov ecx, {size}");
                if (t.TextTable) EmitTextArrayGrowCall();
                else EmitArrayGrowCall();
                EmitViewAddress(append.Target);
                Emit($"mov {R("edx")}, {R("eax")}");
                Emit($"mov eax, [edx + {ViewCount}]");
                Emit($"inc dword [edx + {ViewCount}]");
                EmitScale(size);
                Emit($"mov {R("ebx")}, {Q} [edx]");
                Emit($"add {R("ebx")}, {R("eax")}");
                if (fromInside) Emit("push eax                ; the bytes of elements before the new one");
            }
            else if (append.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true } growing })
            {
                // room for one more (moving its elements when there isn't), then at its end
                ObjectLine(append);
                LoadAddress("eax", growing.Label);
                if (fromInside) Emit($"push {Q} [eax]          ; where its elements are now");
                Emit($"mov edx, [eax + {ViewCount}]");
                Emit("inc edx");
                Emit($"mov ecx, {size}");
                if (t.TextTable) EmitTextArrayGrowCall();
                else EmitArrayGrowCall();
                Emit($"mov eax, [{growing.Label} + {ViewCount}]");
                Emit($"inc dword [{growing.Label} + {ViewCount}]");
                EmitScale(size);
                Emit($"mov {R("ebx")}, {Q} [{growing.Label}]");
                Emit($"add {R("ebx")}, {R("eax")}");
                if (fromInside) Emit("push eax                ; the bytes of elements before the new one");
            }
            else
            {
                EmitFilledCount(append.Target);
                EmitIndexCheck(append, "jb", t.Length.ToString());   // full: no room for another

                Emit("mov edx, eax");
                Emit("inc eax");
                if (!bits32) Emit("push ebx");
                Emit($"mov dword {Operand(new Place { Computed = true, Linear = true, Offset = t.CountOffset })}, eax");
                if (!bits32) Emit("pop ebx");
                Emit("mov eax, edx");
                EmitScale(size);
                Emit($"add {R("ebx")}, {R("eax")}");
            }

            if (element.IsObject)
            {
                // the slot is empty (dropped ones are emptied): a zeroed object, or a copy
                Emit($"mov {R("eax")}, {R("ebx")}");
                if (value == null) Emit($"call {ObjectRoutine("renew", element.Struct!)}");
                else
                {
                    Emit("pop edx");
                    Emit($"mov {Q} [ebx], {R("edx")}          ; the call's new object, or the copy: the array's now");
                }
            }
            else if (element.IsWide)
            {
                Emit("pop eax");
                Emit("pop edx");
                Emit("mov [ebx], eax");
                Emit("mov [ebx + 4], edx");
            }
            else if (element.IsFloat)
            {
                switch (element.Kind)
                {
                    case TypeKind.F64: Emit("fstp qword [ebx]"); break;
                    case TypeKind.F32: Emit("fstp dword [ebx]"); break;
                    default:
                        Need("phi_f32_to_f16");
                        Emit($"sub esp, {Slot}");
                        Emit("fstp dword [esp]");
                        Emit("pop eax");
                        Emit("call phi_f32_to_f16");
                        Emit("mov [ebx], ax");
                        break;
                }
            }
            else if (element.IsStruct && value != null)
            {
                if (fromInside)
                {
                    // The struct was found before the array made room, and its elements may have
                    // moved since. One that was among them is that far into where they are now.
                    string apart = NewLabel("apart");
                    Emit("pop edx                 ; the bytes of elements before the new one");
                    Emit("pop eax                 ; where the elements were");
                    Emit("pop esi                 ; the struct to copy");
                    Emit($"mov {R("ecx")}, {R("esi")}");
                    Emit($"sub {R("ecx")}, {R("eax")}");
                    Emit($"cmp {R("ecx")}, {R("edx")}");
                    Emit($"jae {apart}");
                    Emit($"mov {R("esi")}, {R("ebx")}");
                    Emit($"sub {R("esi")}, {R("edx")}");
                    Emit($"add {R("esi")}, {R("ecx")}");
                    Label(apart);
                }
                else Emit("pop esi");
                Emit($"mov {R("edi")}, {R("ebx")}");
                Emit($"mov ecx, {size}");
                Emit("cld");
                Emit("rep movsb");
            }
            else if (element.IsString)
            {
                bool text = value != null && (IsText(value.Type) || value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar);
                if (element.IsInlineText)
                {
                    // the text goes where the element is
                    if (value == null) Emit("mov byte [ebx], 0        ; empty");
                    else
                    {
                        Emit("pop eax");
                        Emit($"mov {R("edi")}, {R("ebx")}");
                        Emit($"mov ecx, {element.Room}");
                        if (text)
                        {
                            Need("phi_strcpy");
                            Emit($"mov {R("esi")}, {R("eax")}");
                            Emit("call phi_strcpy");
                        }
                        else
                        {
                            string convert = value!.Type.IsUnsigned32 ? "phi_uint_to_str" : "phi_int_to_str";
                            Need(convert);
                            Emit($"call {convert}");
                        }
                        EmitTextFits();
                    }
                }
                else if (value == null)
                {
                    // the element's text (a dropped one's, if it was used before) starts empty
                    Emit($"mov {R("eax")}, {Q} [ebx]");
                    Emit("mov byte [eax], 0        ; empty");
                }
                else
                {
                    Emit("push ebx                ; the element's slot");
                    // its room: after the table (a str array without a most: .max slots on)
                    if (t.Open && append.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true } table })
                        EmitGrowStoreAt(0, sourceIsString: text, append: false, unsigned: value.Type.IsUnsigned32, rooms: $"dword [{table.Label} + {ViewMax}]");
                    else if (Binder.IsFieldView(append.Target))
                        EmitGrowStoreAt(0, sourceIsString: text, append: false, unsigned: value.Type.IsUnsigned32, rooms: RoomsOf(append.Target));
                    else EmitGrowStoreAt(Word * t.Length, sourceIsString: text, append: false, unsigned: value.Type.IsUnsigned32);
                }
            }
            else if (value != null)
            {
                Emit("pop eax");
                Emit(StoreInstruction(element, Operand(new Place { Computed = true, Linear = true })));
            }
            else if (element.IsNumeric || element.IsMethod)
            {
                Emit("xor eax, eax");
                Emit(StoreInstruction(element, Operand(new Place { Computed = true, Linear = true })));
            }
            else
            {
                // a struct: every byte of it, since one dropped earlier may have been here
                Emit($"mov {R("edi")}, {R("ebx")}");
                Emit($"mov ecx, {size}");
                Emit("xor al, al");
                Emit("rep stosb");
            }
        }

        /// <summary>
        /// The memory operand for a place. In 16-bit code a linear address is reached through fs
        /// (which changes ecx); 32-bit memory is flat.
        /// </summary>
        string Operand(Place p)
        {
            if (p.Register != null) return p.Register;
            string offset = p.Offset == 0 ? "" : p.Offset > 0 ? $" + {p.Offset}" : $" - {-p.Offset}";

            if (!p.Computed) return $"[{p.Label}{offset}]";
            if (!p.Linear) return $"[{p.Label} + {p.Added}{offset}]";

            if (bits32) return p.Index == null ? $"[{p.Base}{offset}]" : $"[{p.Base} + {p.Added}{offset}]"; // memory is flat

            if (p.Offset != 0) Emit($"add ebx, {p.Offset}");
            Emit("mov ecx, ebx");
            Emit("shr ecx, 4");
            Emit("mov fs, cx               ; reach any address below 1 MB");
            Emit("and ebx, 0xF");
            return "[fs:ebx]";
        }

        /// <summary>How a value of this type sits in memory: str array elements are 32-bit pointers.</summary>
        PhiType StorageType(PhiType t) => t.IsString ? (bits64 ? PhiType.PointerTo(PhiType.U8) : PhiType.U32) : t;

        string LoadInstruction(PhiType type, string register, string operand)
        {
            PhiType t = StorageType(type);
            return t.ElementSize switch
            {
                1 => $"{(t.IsSigned ? "movsx" : "movzx")} {register}, byte {operand}",
                2 => $"{(t.IsSigned ? "movsx" : "movzx")} {register}, word {operand}",
                8 when bits64 => $"mov {R(register)}, qword {operand}",    // an address, in the whole register
                _ => $"mov {register}, dword {operand}",
            };
        }

        string StoreInstruction(PhiType type, string operand) => StorageType(type).ElementSize switch
        {
            1 => $"mov byte {operand}, al",
            2 => $"mov word {operand}, ax",
            8 when bits64 => $"mov qword {operand}, rax",
            _ => $"mov dword {operand}, eax",
        };

        void EmitLoad(Expr e)
        {
            Place p = EmitPlace(e);
            Emit(LoadInstruction(e.Type, "eax", Operand(p)));
        }

        int StorageSize(VariableSymbol v) =>
            v.Grows ? TextSlot : v.Type.IsString ? v.Capacity : v.Type.ByteSize;

        // ================================================================ assignment

        void EmitAssign(Expr target, Expr value)
        {
            if (target.Type.IsFloat)
            {
                EmitFloat(value);
                EmitFloatStore(target);
                return;
            }
            if (target.Type.IsWide)
            {
                if (value is BinaryExpr or UnaryExpr or CastExpr && WholeValue(value) && KeptSomewhere(target))
                {
                    // 64-bit code: worked out in rax whole, and stored whole
                    EmitWhole(value);
                    EmitWholeStore(target);
                    return;
                }
                if (WideCopy(target, value))
                {
                    // 64-bit code: one 64-bit number from its place to another, in rax whole
                    Emit($"mov rax, qword {Operand(EmitPlace(value))}");
                    if (PlaceKeepsEax(target)) Emit($"mov qword {Operand(PlaceKeepingEax(target))}, rax");
                    else
                    {
                        Emit("push rax");
                        string to = Operand(EmitPlace(target));
                        Emit("pop rax");
                        Emit($"mov qword {to}, rax");
                    }
                    return;
                }
                EmitWide(value);
                EmitWideStore(target);
                return;
            }
            if (target.Type.IsString && (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar))
            {
                EmitNumberText(value);
                EmitStringStore(target, sourceIsString: true);
                return;
            }

            if (!target.Type.IsString)
            {
                // x++, x-- 5, x is x + y, a:i is a:i ^ t: change it where it is (the place is worked out
                // after the amount, so the amount mustn't call anything that could change it)
                if (value is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor } step
                    && (ReferenceEquals(step.Left, target) || SameValue(step.Left, target)) && PlaceKeepsEax(target) && !HasCall(step.Right))
                {
                    int bytes = StorageType(target.Type).ElementSize;
                    string size = bytes switch { 1 => "byte", 2 => "word", 8 when bits64 => "qword", _ => "dword" };
                    string op = step.Op switch
                    {
                        BinaryOp.Add => "add", BinaryOp.Subtract => "sub", BinaryOp.BitAnd => "and", BinaryOp.BitOr => "or", _ => "xor",
                    };
                    string? amount = ConstantFolder.Evaluate(step.Right) is long k ? ((int)k).ToString() : bytes == 4 ? HeldIn(step.Right) : null;
                    if (amount == null)
                    {
                        EmitExpr(step.Right);
                        // an address moved by a number, in 64-bit code: the number in 64 bits, as its type says
                        if (bits64 && bytes == 8) Emit(step.Right.Type.IsSigned ? "movsxd rax, eax" : "mov eax, eax");
                    }
                    string operand = Operand(PlaceKeepingEax(target));
                    Emit($"{op} {size} {operand}, {amount ?? (bytes switch { 1 => "al", 2 => "ax", 8 when bits64 => "rax", _ => "eax" })}");
                    return;
                }

                // into a register variable: straight there, when the value needs no other registers
                if (HeldIn(target) is string register && TryLoadSimple(value, register, dryRun: true))
                {
                    TryLoadSimple(value, register, dryRun: false);
                    return;
                }

                // a constant, or a register variable into a 32-bit place: stored as it is
                if (PlaceKeepsEax(target) && HeldIn(target) == null && DirectOperand(value, constantOnly: false) is string stored && !stored.Contains('['))
                {
                    int bytes = StorageType(target.Type).ElementSize;
                    bool constant = ConstantFolder.Evaluate(value) is long;
                    long k = constant ? ConstantFolder.Evaluate(value)!.Value : 0;
                    // (an 8-byte place in 64-bit code takes a constant that fits in 32 bits, sign-extended)
                    bool fits = !(bits64 && bytes == 8) || k is >= int.MinValue and <= int.MaxValue;
                    if (constant && fits || bytes == 4)
                    {
                        string amount = !constant ? stored : bytes switch { 1 => (k & 0xFF).ToString(), 2 => (k & 0xFFFF).ToString(), _ => stored };
                        string size = bytes switch { 1 => "byte", 2 => "word", 8 when bits64 => "qword", _ => "dword" };
                        Emit($"mov {size} {Operand(PlaceKeepingEax(target))}, {amount}");
                        return;
                    }
                }

                // an element or a field into a register variable: loaded straight there
                if (HeldIn(target) is string into && value is IndexExpr or MemberExpr { Call: null } && OneRegister(value.Type) && PlaceKeepsEax(value))
                {
                    Emit(LoadInstruction(value.Type, into, Operand(PlaceKeepingEax(value))));
                    return;
                }

                // a simple value into a computed place: the address first, then the value,
                // which needs no registers the address is in
                if (!IsStaticPlace(target) && TryLoadSimple(value, "eax", dryRun: true))
                {
                    string operand = Operand(EmitPlace(target));
                    TryLoadSimple(value, "eax", dryRun: false);
                    Emit(StoreInstruction(target.Type, operand));
                    return;
                }
            }

            EmitExpr(value);
            if (target.Type.IsString) EmitStringStore(target, sourceIsString: IsText(value.Type));
            else EmitStoreEax(target);
        }

        /// <summary>
        /// True for x is y in 64-bit code with both 64-bit numbers kept somewhere (a variable, a
        /// field or an element): the copy is one load and one store of the whole number.
        /// </summary>
        bool WideCopy(Expr target, Expr value) =>
            bits64 && target.Type.IsWide && value.Type.IsWide && KeptSomewhere(target) && KeptSomewhere(value);

        static bool KeptSomewhere(Expr e) => e is
            NameExpr { Call: null, Symbol: VariableSymbol { TextRef: false, Grows: false } or FieldPathSymbol or SelfFieldSymbol or PathSymbol }
            or MemberExpr { Call: null, Count: null, FilledLength: false, TextLength: false }
            or IndexExpr;

        /// <summary>
        /// A place whose address is worked out with the variables in registers, whatever is kept
        /// there (SafeValue asks the same of a value that's one register).
        /// </summary>
        bool SafePlace(Expr e) => e switch
        {
            NameExpr { Call: null, Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol } => true,
            MemberExpr { Call: null } m => !m.FilledLength && !m.TextLength && SafeRoot(m.Target),
            IndexExpr ix => !ix.Target.Type.IsString && SafeRoot(ix.Target) && SafeValue(ix.Index),
            _ => false,
        };

        /// <summary>Stores eax into a number variable, field or element.</summary>
        void EmitStoreEax(Expr target)
        {
            bool needsCode = !PlaceKeepsEax(target); // computing the address uses eax

            if (needsCode) Emit("push eax");
            Place p = needsCode ? EmitPlace(target) : PlaceKeepingEax(target);
            string operand = Operand(p);
            if (needsCode) Emit("pop eax");
            Emit(StoreInstruction(target.Type, operand));
        }

        /// <summary>The register a variable is in, for an expression that's only that variable.</summary>
        string? HeldIn(Expr e) => e is NameExpr { Symbol: VariableSymbol v } ? RegisterOf(v) : null;

        /// <summary>
        /// True when working out where the place is leaves eax alone: it's fixed, or it's reached
        /// through register variables, constants and at most one pointer loaded into ebx.
        /// </summary>
        bool PlaceKeepsEax(Expr e) => e switch
        {
            _ when IsStaticPlace(e) => true,
            NameExpr { Symbol: SelfFieldSymbol or PathSymbol } => true,
            MemberExpr m when m.Target.Type.IsObject || m.Target.Type.IsPointer =>
                HeldIn(m.Target) != null || bits32 && TryLoadSimple(m.Target, "ebx", dryRun: true),
            MemberExpr { Call: null } m when !m.Target.Type.IsObject && !m.Target.Type.IsPointer && m.Target is IndexExpr => PlaceKeepsEax(m.Target),
            IndexExpr ix when ix.Target.Type.IsPointer => PointerElementKeepsEax(ix),
            IndexExpr ix => HeldIn(ix.Index) != null && ElementSize(ix) is 1 or 2 or 4 or 8 && !ix.Target.Type.Filled && !ix.Target.Type.Open
                            && !ix.Target.Type.IsString && !ix.Target.Type.TextTable
                            && ix.Target is NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false, GrowingElements: false, GrowingArray: false } or FieldPathSymbol },
            _ => false,
        };

        /// <summary>A 32-bit element or field that's an instruction's operand as it is, its address in ebx at most.</summary>
        bool PlaceOperand(Expr e) =>
            bits32 && e is IndexExpr or MemberExpr { Call: null, Count: null, FilledLength: false, TextLength: false }
            && OneRegister(e.Type) && StorageType(e.Type).ElementSize == 4 && PlaceKeepsEax(e);

        /// <summary>p:i reached as [p + i*size], with i a register variable or a constant (see EmitIndexPlace).</summary>
        bool PointerElementKeepsEax(IndexExpr ix) =>
            bits32 && ix.Target.Type.IsPointer && IndexAsIs(ix)
            && (HeldIn(ix.Target) != null || TryLoadSimple(ix.Target, "ebx", dryRun: true));

        /// <summary>An index that's part of the address as it is: a constant, or a register variable times 1, 2, 4 or 8.</summary>
        bool IndexAsIs(IndexExpr ix) =>
            ConstantFolder.Evaluate(ix.Index) != null || HeldIn(ix.Index) != null && ElementSize(ix) is 1 or 2 or 4 or 8;

        static int ElementSize(IndexExpr ix) =>
            ix.Target.Type.IsString ? 1 : ix.Target.Type.IsPointer ? ix.Target.Type.Pointee!.ElementSize : ix.Target.Type.ElementSize;

        /// <summary>EmitPlace for a place that leaves eax alone, checking that it does.</summary>
        Place PlaceKeepingEax(Expr target)
        {
            int start = code.Length;
            Place p = EmitPlace(target);
            string emitted = code.ToString(start, code.Length - start);
            foreach (string line in emitted.Split('\n'))
                if (!line.Contains("it stops here") && System.Text.RegularExpressions.Regex.IsMatch(WithoutComment(line), @"\b(eax|ax|al|ah)\b"))
                    throw new InvalidOperationException($"working out a place changed eax: {line.Trim()}");
            return p;
        }

        /// <summary>True when two expressions are the same variable, field or element (so x is x + 1 can add in place).</summary>
        static bool SameValue(Expr a, Expr b) => (a, b) switch
        {
            (NameExpr { Call: null, Symbol: VariableSymbol x }, NameExpr { Call: null, Symbol: VariableSymbol y }) => x == y,
            (IndexExpr x, IndexExpr y) => x.Type == y.Type && SameValue(x.Target, y.Target) && SameValue(x.Index, y.Index),
            (MemberExpr { Call: null } x, MemberExpr { Call: null } y) => x.Field != null && x.Field == y.Field && SameValue(x.Target, y.Target),
            (NumberExpr x, NumberExpr y) => x.Value == y.Value,
            _ => false,
        };

        /// <summary>True when working out the expression may call something (which could change memory).</summary>
        static bool HasCall(Expr e) => SyntaxWalker.Nodes(e).Any(n => n is CallExpr or NewExpr or NameExpr { Call: not null } or MemberExpr { Call: not null });

        /// <summary>True when the place's address is fixed, so computing it emits no code.</summary>
        static bool IsStaticPlace(Expr e) => e switch
        {
            NameExpr { Symbol: VariableSymbol or FieldPathSymbol } => true,
            MemberExpr m => !m.Target.Type.IsObject && !m.Target.Type.IsPointer && IsStaticPlace(m.Target),
            IndexExpr ix => !ix.Target.Type.IsPointer && !ix.Target.Type.Filled && !ix.Target.Type.Open && ConstantFolder.Evaluate(ix.Index) != null && IsStaticPlace(ix.Target)
                            && !(ix.Target.Type.IsString && ix.Target is not NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false } }),
            _ => false,
        };

        /// <summary>eax = how many characters the text kept at a str field is.</summary>
        void EmitTextLength(Expr text)
        {
            EmitExpr(text);     // where its text is
            Need("phi_strlen");
            Emit($"mov {R("esi")}, {R("eax")}");
            Emit("call phi_strlen");
        }

        /// <summary>
        /// eax holds a string address (sourceIsString) or a number to write out as text;
        /// put it in a str variable, a str field or a str array element: it's an error if it doesn't fit.
        /// </summary>
        void EmitStringStore(Expr target, bool sourceIsString, bool append = false, bool unsigned = false)
        {
            if (target is NameExpr { Symbol: VariableSymbol { Grows: true } growing })
            {
                EmitGrowStore(growing, sourceIsString, append, unsigned);
                return;
            }

            if (target.Type.IsHeapText)
            {
                // a new str field: it grows like a str variable, through its slot
                Emit("push eax                ; the value");
                EmitPlaceAddress(target);
                Emit("push eax                ; the field's text slot (its room is after it)");
                EmitGrowStoreAt(RoomAt, sourceIsString, append, unsigned);
                return;
            }

            if (target is NameExpr { Symbol: VariableSymbol { TextRef: true } caller })
            {
                // into the caller's str: it grows if it can
                Emit("push eax                ; the value");
                Emit($"push dword [{caller.Label}]   ; where {caller.Name}'s text and room are");
                EmitGrowStoreAt(RoomAt, sourceIsString, append, unsigned);
                return;
            }

            if (target is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { Type: { Open: true, TextTable: true } } view } })
            {
                Emit("push eax                ; the value");
                EmitPlaceAddress(target);
                Emit("push eax                ; the element's slot");
                EmitGrowStoreAt(0, sourceIsString, append, unsigned, $"dword [{view.Label} + {ViewMax}]");
                return;
            }

            if (target is IndexExpr { Target.Type: { Open: true, TextTable: true } } fieldText && Binder.IsFieldView(fieldText.Target))
            {
                // an element of a struct's str array without a size
                Emit("push eax                ; the value");
                string rooms = RoomsOf(fieldText.Target);
                EmitPlaceAddress(target);
                Emit("push eax                ; the element's slot");
                EmitGrowStoreAt(0, sourceIsString, append, unsigned, rooms);
                return;
            }

            if (target is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { GrowingElements: true } array } })
            {
                // the element's text slot, and its room after the table
                Emit("push eax                ; the value");
                EmitPlaceAddress(target);
                Emit("push eax                ; the element's slot");
                EmitGrowStoreAt(Word * array.Count, sourceIsString, append, unsigned);
                return;
            }

            int capacity;
            if (target.Type.IsInlineText)
            {
                // a str field: the text goes where the field is
                capacity = target.Type.Room;
                Emit("push eax");
                EmitPlaceAddress(target);
                Emit($"mov {R("edi")}, {R("eax")}");
                Emit("pop eax");
            }
            else if (target is NameExpr { Symbol: VariableSymbol v })
            {
                capacity = v.Capacity;
                LoadAddress("edi", v.Label);
            }
            else
            {
                // an element of a str array: its address is the element's value
                capacity = ((VariableSymbol)((NameExpr)((IndexExpr)target).Target).Symbol!).Capacity;
                Emit("push eax");
                EmitExpr(target);
                Emit($"mov {R("edi")}, {R("eax")}");
                Emit("pop eax");
            }

            Emit($"mov ecx, {capacity}");
            if (append)
            {
                // past the text already there, with that much less room
                Need("phi_str_end");
                Emit("call phi_str_end");
            }
            if (sourceIsString)
            {
                Need("phi_strcpy");
                Emit($"mov {R("esi")}, {R("eax")}");
                Emit("call phi_strcpy");
            }
            else
            {
                string convert = unsigned ? "phi_uint_to_str" : "phi_int_to_str";
                Need(convert);
                Emit($"call {convert}");
            }
            EmitTextFits();
        }

        // ================================================================ expressions

        /// <summary>Leaves the value of <paramref name="e"/> in eax (strings: their address).</summary>
        void EmitExpr(Expr e)
        {
            if (e.Type.IsWide)
            {
                EmitWide(e);    // the low half is in eax too
                return;
            }
            if (e.Type.IsFloat)
                throw new InvalidOperationException($"a floating point value where a whole number goes: {e.GetType().Name}");

            if (e is BinaryExpr or UnaryExpr && ConstantFolder.Evaluate(e) is long folded)
            {
                LoadConstant("eax", folded);
                return;
            }

            // text kept in place (a str field): its value is where it is
            if (e.Type.IsInlineText && e is NameExpr { Symbol: FieldPathSymbol or SelfFieldSymbol or PathSymbol } or MemberExpr { Call: null } or IndexExpr)
            {
                EmitPlaceAddress(e);
                return;
            }

            // a new str field: the text its slot points at ('' while it has none)
            if (e.Type.IsHeapText && e is NameExpr { Symbol: FieldPathSymbol or SelfFieldSymbol or PathSymbol } or MemberExpr { Call: null } or IndexExpr)
            {
                string done = NewLabel("text");
                EmitPlaceAddress(e);
                Emit($"mov {R("eax")}, {Q} [eax]");
                Emit($"test {R("eax")}, {R("eax")}");
                Emit($"jnz {done}");
                LoadAddress("eax", StringLabel(""));
                Label(done);
                return;
            }

            switch (e)
            {
                case CallExpr call:
                    EmitCallTo(call.Resolved, call.Arguments);
                    break;

                case NameExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    break;

                case MemberExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    break;

                case NumberExpr n:
                    LoadConstant("eax", n.Value);
                    break;

                case BoolExpr b:
                    LoadConstant("eax", b.Value ? 1 : 0);
                    break;

                case StringExpr s:
                    LoadAddress("eax", StringLabel(s.Value));
                    break;

                case NameExpr { Symbol: VariableSymbol { Grows: true } v }:
                    LoadText("eax", v);
                    break;

                case NameExpr { Symbol: VariableSymbol { TextRef: true } v }:
                    LoadCallerText("eax", v);
                    break;

                case NameExpr { Symbol: VariableSymbol { Type.IsString: true } v }:
                    LoadAddress("eax", v.Label);
                    break;

                case NameExpr { Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol }:
                    EmitLoad(e);
                    break;

                case EmptyExpr:
                    Emit("xor eax, eax");
                    break;

                case NameExpr { Symbol: ConstantSymbol k }:
                    LoadConstant("eax", k.Value);
                    break;

                case NameExpr { Symbol: LengthSymbol { Count: int count } }:
                    LoadConstant("eax", count);
                    break;

                case MemberExpr { Count: int count }:
                    LoadConstant("eax", count);
                    break;

                case NameExpr { Symbol: LengthSymbol { Filled: Expr array } }:
                    EmitFilledCount(array);
                    break;

                case NameExpr { Symbol: LengthSymbol { Open: VariableSymbol open } length }:
                    Emit($"mov eax, [{open.Label} + {(length.Max ? ViewMax : ViewCount)}]");
                    break;

                case MemberExpr { FilledLength: true } m:
                    if (m.Target.Type.Open) EmitArrayCount(m.Target);
                    else EmitFilledCount(m.Target);
                    break;

                case NameExpr { Symbol: LengthSymbol { View: Expr view } length }:
                    // (kept somewhere that's reached without eax: read where it is)
                    if (bits32 && PlaceKeepsEax(view))
                    {
                        Emit($"mov eax, dword {Operand(PlaceKeepingEax(view).Plus(length.Max ? ViewMax : ViewCount))}");
                        break;
                    }
                    EmitViewAddress(view);
                    Emit($"mov eax, [eax + {(length.Max ? ViewMax : ViewCount)}]");
                    break;

                case MemberExpr { TextLength: true } m:
                    EmitTextLength(m.Target);
                    break;

                case NameExpr { Symbol: LengthSymbol { Text: Expr text } }:
                    EmitTextLength(text);
                    break;

                case NameExpr { Symbol: LengthSymbol { TextRef: VariableSymbol caller } }:
                {
                    // the caller's room when its str has a size, or the most a str grows to
                    string grows = NewLabel("grows");
                    Emit($"mov {R("eax")}, {Q} [{caller.Label}]");
                    Emit($"mov eax, [eax + {RoomAt}]");
                    Emit($"test eax, {Fixed}");
                    Emit($"mov eax, {Binder.StrMax}");
                    Emit($"jz {grows}");
                    Emit($"mov {R("eax")}, {Q} [{caller.Label}]");
                    Emit($"mov eax, [eax + {RoomAt}]");
                    Emit($"and eax, {RoomMask}");
                    Emit("dec eax                 ; less the zero");
                    Label(grows);
                    break;
                }

                case NameExpr { Symbol: LengthSymbol { Str: VariableSymbol str } }:
                    Need("phi_strlen");
                    if (str.TextRef) LoadCallerText("esi", str);
                    else if (str.Grows) LoadText("esi", str);
                    else LoadAddress("esi", str.Label);
                    Emit("call phi_strlen");
                    break;

                case IndexExpr or MemberExpr:
                    EmitLoad(e);
                    break;

                case AddrExpr addr:
                    EmitAddress(addr);
                    break;

                case HasExpr has:
                    EmitHas(has);
                    break;

                case TextExpr text:
                    EmitText(text);
                    break;

                case IsTypeExpr test:
                {
                    // the struct's type table is just before the object (and 0 holds nothing)
                    string none = NewLabel("nottype");
                    EmitExpr(test.Value);
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit($"jz {none}");
                    Emit($"cmp dword [eax - 4], {TypeTable(test.Struct!)}");
                    Emit("sete al");
                    Emit("movzx eax, al");
                    Label(none);
                    break;
                }

                case InterfaceMethodExpr method:
                    // the object's type table is just before it
                    EmitExpr(method.Receiver);
                    Emit("mov eax, [eax - 4]      ; its type table");
                    Emit($"mov eax, [eax + {InterfaceSlot(method.Interface)} + {4 * method.Index}]");
                    break;

                case SizeofExpr size:
                    LoadConstant("eax", size.Value);
                    break;

                case CheckedNumberExpr check:
                    EmitExpr(check.Operand);
                    if (bits32) EmitNumberCheck(check);
                    break;

                case CastExpr cast:
                    if (cast.Operand.Type.IsFloat) EmitFloatToWhole(cast.Operand);
                    else if (bits64 && cast.Type.IsPointer && cast.Operand.Type.IsWide)
                    {
                        // a 64-bit number made an address: its halves, joined
                        EmitWide(cast.Operand);
                        Emit("shl rdx, 32");
                        Emit("mov eax, eax");
                        Emit("or rax, rdx");
                        break;
                    }
                    else EmitExpr(cast.Operand);
                    // a smaller number keeps the low bytes, like storing it would
                    switch (cast.Type.Kind)
                    {
                        case TypeKind.U8 or TypeKind.Bool or TypeKind.Chr: Emit("movzx eax, al"); break;
                        case TypeKind.I8: Emit("movsx eax, al"); break;
                        case TypeKind.U16: Emit("movzx eax, ax"); break;
                        case TypeKind.I16: Emit("movsx eax, ax"); break;
                    }
                    break;

                case NewExpr n:
                    EmitHeapCall(n.Allocator!, n.Bytes); // the address comes back in eax
                    break;

                case AtomicExpr swap:
                    EmitCompareSwap(swap.Target, swap.Value, swap.Expected);
                    break;

                case InExpr input:
                    if (ConstantFolder.Evaluate(input.Port) is long port) Emit($"mov dx, 0x{port & 0xFFFF:X}");
                    else
                    {
                        EmitExpr(input.Port);
                        Emit("mov dx, ax");
                    }
                    switch (input.Size)
                    {
                        case 1: Emit("in al, dx"); Emit("movzx eax, al"); break;
                        case 2: Emit("in ax, dx"); Emit("movzx eax, ax"); break;
                        default: Emit("in eax, dx"); break;
                    }
                    break;

                case UnaryExpr { Op: UnaryOp.Not, Operand.Type.IsFloat: true } notFloat:
                    EmitFloatIsZero(notFloat.Operand);
                    Emit("sete al");
                    Emit("movzx eax, al");
                    break;

                case UnaryExpr unary:
                    EmitExpr(unary.Operand);
                    if (unary.Operand.Type.IsWide) Emit("or eax, edx            ; not: all 64 bits are zero");
                    switch (unary.Op)
                    {
                        case UnaryOp.Negate: Emit("neg eax"); break;
                        case UnaryOp.BitNot: Emit("not eax"); break;
                        default:
                            Emit("test eax, eax");
                            Emit("sete al");
                            Emit("movzx eax, al");
                            break;
                    }
                    break;

                case BinaryExpr { IsComparison: true } or BinaryExpr { IsLogical: true }:
                {
                    string falseLabel = NewLabel("false");
                    string endLabel = NewLabel("bool");
                    EmitBranch(e, jumpIf: false, falseLabel);
                    Emit("mov eax, 1");
                    Emit($"jmp {endLabel}");
                    Label(falseLabel);
                    Emit("xor eax, eax");
                    Label(endLabel);
                    break;
                }

                case BinaryExpr binary:
                    EmitArithmetic(binary);
                    break;

                default:
                    throw new InvalidOperationException($"unexpected expression {e.GetType().Name}");
            }
        }

        void EmitAddress(AddrExpr addr)
        {
            if (addr.CodeLabel != null)
            {
                LoadAddress("eax", addr.CodeLabel);
                return;
            }

            // an object is at the address it holds, and a new str field's text where its slot says
            if (addr.OfObject || addr.Operand.Type.IsHeapText) EmitExpr(addr.Operand);
            else EmitPlaceAddress(addr.Operand);
        }

        /// <summary>eax = the address of a variable, field or element.</summary>
        void EmitPlaceAddress(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol { Type.Open: true } open })
            {
                Emit($"mov {R("eax")}, {Q} [{open.Label}]   ; where {open.Name}'s elements are");
                return;
            }
            if (Binder.IsFieldView(e))
            {
                EmitViewAddress(e);
                Emit($"mov {R("eax")}, {Q} [eax]          ; where its elements are");
                return;
            }
            if (e is NameExpr { Symbol: VariableSymbol { Grows: true } text })
            {
                LoadText("eax", text);  // addr s: where its text is now
                return;
            }
            if (e is NameExpr { Symbol: VariableSymbol { TextRef: true } caller })
            {
                LoadCallerText("eax", caller);
                return;
            }
            if (e is NameExpr { Symbol: VariableSymbol v })
            {
                LoadAddress("eax", v.Label);
                return;
            }

            EmitStorageAddress(e);
        }

        /// <summary>eax = where a variable, field or element is kept.</summary>
        void EmitStorageAddress(Expr e)
        {
            Place p = EmitPlace(e);
            string offset = p.Offset == 0 ? "" : $" + {p.Offset}";
            if (!p.Computed) LoadAddress("eax", p.Label!, p.Offset);
            else if (bits32 || !p.Linear) Emit($"lea eax, {Operand(p)}");
            else Emit($"lea eax, [ebx{offset}]");   // (Operand would reach it through fs)
        }

        /// <summary>
        /// eax = the text ('Hello ' name) builds: each part written into a str of its own, as
        /// s is a b; writes them. In 32-bit code with a heap it's a slot that grows (reused each
        /// time this runs, and freed when the method ends); otherwise a buffer of a str's default size.
        /// </summary>
        void EmitText(TextExpr text)
        {
            var holder = new VariableSymbol { Name = "(text)", Type = PhiType.Str, IsLocal = currentMethod != null };
            if (GrowResults)
            {
                holder.Label = AllocTemp(TextSlot);
                holder.Grows = true;
                holder.Capacity = Binder.StrMax + 1;
                if (currentMethod != null) resultSlots.Add(holder.Label);
            }
            else
            {
                holder.Capacity = 256;
                holder.Label = AllocTemp(holder.Capacity);
            }
            var build = new StringBuildStmt { Target = NameOf(holder), Span = text.Span };
            build.Parts.AddRange(text.Parts);
            EmitStatementCode(build);
            EmitTextValue(NameOf(holder));
        }

        /// <summary>eax = a text's address: '' in place of none (a str that grows has none until it's written).</summary>
        void EmitTextValue(Expr e)
        {
            EmitExpr(e);
            string some = NewLabel("hastext");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit($"jnz {some}");
            LoadAddress("eax", StringLabel(""));
            Label(some);
        }

        /// <summary>
        /// eax = 1 when the array holds the value (each element compared in turn, as is compares),
        /// or the text holds the other text or character; 0 when it doesn't.
        /// </summary>
        void EmitHas(HasExpr has)
        {
            if (has.Kind is HasKind.Text or HasKind.Character)
            {
                if (has.Kind == HasKind.Text) EmitTextValue(has.Value);
                else EmitExpr(has.Value);
                Emit("push eax                ; what to look for");
                EmitTextValue(has.Collection);
                Emit($"mov {R("esi")}, {R("eax")}");
                if (has.Kind == HasKind.Text)
                {
                    Need("phi_str_has");
                    Emit("pop edi");
                    Emit("call phi_str_has");
                }
                else
                {
                    Need("phi_str_has_char");
                    Emit("pop eax");
                    Emit("call phi_str_has_char");
                }
                return;
            }

            PhiType element = has.Collection.Type.Element;
            int size = element.ElementSize;
            bool wide = has.Kind == HasKind.Numbers && element.IsWide, floating = has.Kind == HasKind.Numbers && element.IsFloat;
            string value = AllocTemp(wide || floating ? 8 : Slot), index = AllocTemp(4);
            string next = NewLabel("has"), yes = NewLabel("hasyes"), no = NewLabel("hasno"), done = NewLabel("hasdone");
            if (has.Kind == HasKind.Texts) EmitTextValue(has.Value);
            else if (has.Kind is HasKind.Objects or HasKind.Structs) EmitObjectAddress(has.Value);
            else if (wide) EmitWide(has.Value);
            else if (floating) EmitFloat(has.Value);
            else EmitExpr(has.Value);
            if (wide)
            {
                Emit($"mov [{value}], eax         ; what to look for");
                Emit($"mov [{value} + 4], edx");
            }
            else if (floating) Emit($"fstp qword [{value}]       ; what to look for (as an f64: exact for every kind)");
            else Emit($"mov {Q} [{value}], {R("eax")}         ; what to look for");
            Emit($"mov dword [{index}], 0");
            Label(next);
            EmitArrayCount(has.Collection);
            Emit($"cmp [{index}], eax");
            Emit($"jae {no}");
            EmitPlaceAddress(has.Collection);
            Emit($"mov ecx, [{index}]");
            if (size is 1 or 2 or 4 or 8) Emit($"lea eax, [eax + ecx * {size}]   ; the element");
            else
            {
                Emit($"imul ecx, ecx, {size}");
                Emit($"add {R("eax")}, {R("ecx")}            ; the element");
            }
            switch (has.Kind)
            {
                case HasKind.Numbers when wide:
                    // the low halves, then the high ones
                    Emit("mov ecx, [eax]");
                    Emit($"cmp ecx, [{value}]");
                    string differs = NewLabel("hasdiffers");
                    Emit($"jne {differs}");
                    Emit("mov ecx, [eax + 4]");
                    Emit($"cmp ecx, [{value} + 4]");
                    Emit($"je {yes}");
                    Label(differs);
                    break;

                case HasKind.Numbers when floating:
                    // as is compares them: on the FPU, where nan is equal to nothing
                    if (element.Kind == TypeKind.F64) Emit("fld qword [eax]");
                    else if (element.Kind == TypeKind.F32) Emit("fld dword [eax]");
                    else
                    {
                        Need("phi_f16_to_f32");
                        Emit("movzx eax, word [eax]");
                        Emit("call phi_f16_to_f32");
                        PushAndLoad("dword", "eax", isFloat: true);
                    }
                    Emit($"fld qword [{value}]");
                    Emit("fcompp");
                    Emit("fnstsw ax");
                    Emit("sahf");
                    string unordered = NewLabel("hasnan");
                    Emit($"jp {unordered}");
                    Emit($"je {yes}");
                    Label(unordered);
                    break;

                case HasKind.Numbers:
                    string extend = element.IsSigned ? "movsx" : "movzx";
                    if (size == 1) Emit($"{extend} eax, byte [eax]");
                    else if (size == 2) Emit($"{extend} eax, word [eax]");
                    else if (size == 8 && bits64) Emit("mov rax, [rax]          ; (a pointer: all of it)");
                    else Emit("mov eax, [eax]");
                    Emit(size == 8 && bits64 ? $"cmp rax, [{value}]" : $"cmp eax, [{value}]");
                    Emit($"je {yes}");
                    break;

                case HasKind.Texts:
                {
                    Need("phi_strcmp");
                    if (element.IsInlineText) Emit($"mov {R("esi")}, {R("eax")}");
                    else
                    {
                        string some = NewLabel("hastext");
                        Emit($"mov {R("esi")}, {Q} [eax]          ; its text");
                        Emit($"test {R("esi")}, {R("esi")}");
                        Emit($"jnz {some}");
                        LoadAddress("esi", StringLabel(""));
                        Label(some);
                    }
                    Emit($"mov {R("edi")}, {Q} [{value}]");
                    Emit("call phi_strcmp          ; eax = 0 when they're the same");
                    Emit("test eax, eax");
                    Emit($"jz {yes}");
                    break;
                }

                default:
                    if (has.Kind == HasKind.Objects) Emit($"mov {R("eax")}, {Q} [eax]          ; its object");
                    Emit($"mov {R("edx")}, {Q} [{value}]");
                    Emit($"call {ObjectRoutine("equal", element.Struct!)}");
                    Emit("test eax, eax");
                    Emit($"jnz {yes}");
                    break;
            }
            Emit($"inc dword [{index}]");
            Emit($"jmp {next}");
            Label(yes);
            Emit("mov eax, 1");
            Emit($"jmp {done}");
            Label(no);
            Emit("xor eax, eax");
            Label(done);
        }

        /// <summary>An operand holding a struct's str array field's room (.max), read now: for EmitGrowStoreAt.</summary>
        string RoomsOf(Expr array)
        {
            string room = AllocTemp(4);
            EmitViewAddress(array);
            Emit($"mov eax, [eax + {ViewMax}]");
            Emit($"mov [{room}], eax");
            return $"dword [{room}]";
        }

        /// <summary>A copy of an array field's view (where, how many, room), for code that reads it by a label.</summary>
        string ViewCopy(Expr array)
        {
            string view = AllocTemp(ViewMax + 4);
            EmitViewAddress(array);
            for (int i = 0; i < ViewMax + 4; i += 4)
            {
                Emit($"mov ecx, [eax + {i}]");
                Emit($"mov [{view} + {i}], ecx");
            }
            return view;
        }

        /// <summary>
        /// eax = where an array without a size keeps where its elements are, how many there are
        /// and its room (three u32s): an array variable's own, or a struct's field.
        /// </summary>
        void EmitViewAddress(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol v }) LoadAddress("eax", v.Label);
            else EmitStorageAddress(e);
        }

        /// <summary>
        /// In 64-bit code, a value that's an address (64 bits): a pointer, or a number added to
        /// or taken from one (p + n is typed as a number, but in 64-bit code it's worked out in
        /// all 64 bits, so storing it in a pointer keeps the address whole). Never in 32-bit code,
        /// where an address and a number are the same size.
        /// </summary>
        /// <summary>A pointer kept in a variable, as an instruction's operand in 64-bit code: its register, or its 8 bytes of memory.</summary>
        string? PointerOperand(Expr e) =>
            bits64 && e is NameExpr { Call: null, Symbol: VariableSymbol { Grows: false, TextRef: false } v }
            && v.Type.IsPointer && !v.Type.IsArray && !v.Type.Open && StorageType(v.Type).ElementSize == 8
                ? RegisterOf(v) ?? $"qword [{v.Label}]"
                : null;

        bool IsAddress(Expr e) => bits64 && e switch
        {
            _ when e.Type.IsPointer && !e.Type.IsArray => true,
            BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } b => IsAddress(b.Left) != IsAddress(b.Right),
            _ => false,
        };

        /// <summary>
        /// 64-bit code, with an address on at least one side: rax = the left, rcx = the right,
        /// each in 64 bits (a number widened as its type says: a signed one keeps its sign).
        /// </summary>
        void EmitAddressOperands(BinaryExpr b)
        {
            if (TryLoadSimple(b.Right, "ecx", dryRun: true))
            {
                // (a right side that's one load goes straight into rcx, with nothing on the stack)
                EmitExpr(b.Left);
                TryLoadSimple(b.Right, "ecx", dryRun: false);
            }
            else
            {
                EmitExpr(b.Right);
                Emit("push rax");
                EmitExpr(b.Left);
                Emit("pop rcx");
            }
            if (!IsAddress(b.Left)) Emit(b.Left.Type.IsSigned ? "movsxd rax, eax" : "mov eax, eax");
            if (!IsAddress(b.Right)) Emit(b.Right.Type.IsSigned ? "movsxd rcx, ecx" : "mov ecx, ecx");
        }

        void EmitArithmetic(BinaryExpr binary)
        {
            bool unsigned = binary.Type.IsUnsigned32;
            if (IsAddress(binary.Left) || IsAddress(binary.Right))
            {
                // an address moved by a number of bytes (or two addresses apart): in 64 bits
                EmitAddressOperands(binary);
                Emit(binary.Op == BinaryOp.Subtract ? "sub rax, rcx" : "add rax, rcx");
                return;
            }
            if (TryDivideByConstant(binary, unsigned)) return;
            if (TryDirectArithmetic(binary, unsigned)) return;
            EmitOperands(binary);

            switch (binary.Op)
            {
                case BinaryOp.Add: Emit("add eax, ecx"); break;
                case BinaryOp.Subtract: Emit("sub eax, ecx"); break;
                case BinaryOp.Multiply: Emit("imul eax, ecx"); break;
                case BinaryOp.BitAnd: Emit("and eax, ecx"); break;
                case BinaryOp.BitOr: Emit("or eax, ecx"); break;
                case BinaryOp.BitXor: Emit("xor eax, ecx"); break;
                case BinaryOp.ShiftLeft: Emit("shl eax, cl"); break;
                case BinaryOp.ShiftRight: Emit(unsigned ? "shr eax, cl" : "sar eax, cl"); break;
                case BinaryOp.Power:
                {
                    // multiplying by the base squared again and again, for each bit of the power;
                    // a power below 0 is 0 (in whole numbers)
                    string next = NewLabel("power"), skip = NewLabel("powerbit"), done = NewLabel("powered");
                    Emit("mov edx, eax            ; the base");
                    Emit("mov eax, 1");
                    Emit("test ecx, ecx");
                    Emit($"jns {next}");
                    Emit("xor eax, eax");
                    Emit($"jmp {done}");
                    Label(next);
                    Emit("test ecx, ecx");
                    Emit($"jz {done}");
                    Emit("test ecx, 1");
                    Emit($"jz {skip}");
                    Emit("imul eax, edx");
                    Label(skip);
                    Emit("imul edx, edx");
                    Emit("shr ecx, 1");
                    Emit($"jmp {next}");
                    Label(done);
                    break;
                }
                case BinaryOp.Divide:
                case BinaryOp.Modulo:
                    if (unsigned)
                    {
                        Emit("xor edx, edx");
                        Emit("div ecx");
                    }
                    else
                    {
                        Emit("cdq");
                        Emit("idiv ecx");
                    }
                    if (binary.Op == BinaryOp.Modulo) Emit("mov eax, edx");
                    break;
            }
        }

        /// <summary>
        /// x / d and x % d with d a constant of 2 or more, without the CPU's divide (which takes
        /// many times longer than a multiply). A power of two is a shift or a mask, with the
        /// rounding toward zero put right for a signed x. Any other d, in 32- and 64-bit code, is
        /// a multiply by a number near 2^32 / d and a shift (see <see cref="DivisionMagic"/>);
        /// 16-bit code keeps the divide, which is shorter. The remainder is x - (x / d) * d.
        /// Uses eax, ecx and edx, as a divide does. A divisor of 0, 1 or less is left to the
        /// divide, so dividing by zero is still the CPU's error.
        /// </summary>
        bool TryDivideByConstant(BinaryExpr binary, bool unsigned)
        {
            if (binary.Op is not (BinaryOp.Divide or BinaryOp.Modulo)) return false;
            if (ConstantFolder.Evaluate(binary.Right) is not long d || d < 2 || d > (unsigned ? uint.MaxValue : int.MaxValue)) return false;
            bool modulo = binary.Op == BinaryOp.Modulo;
            bool powerOfTwo = (d & (d - 1)) == 0;
            int shift = System.Numerics.BitOperations.Log2((ulong)d);
            if (!powerOfTwo && !bits32) return false;

            EmitExpr(binary.Left);
            if (powerOfTwo && unsigned)
            {
                Emit(modulo ? $"and eax, {(int)(d - 1)}" : $"shr eax, {shift}");
                return true;
            }
            if (powerOfTwo)
            {
                // a negative x rounds toward zero: d - 1 is added to it first (edx is -1 then)
                Emit("cdq");
                Emit($"and edx, {(int)(d - 1)}");
                Emit("add eax, edx");
                if (modulo)
                {
                    Emit($"and eax, {(int)(d - 1)}");
                    Emit("sub eax, edx");
                }
                else Emit($"sar eax, {shift}");
                return true;
            }

            if (unsigned)
            {
                DivisionMagic.Unsigned magic = DivisionMagic.ForUnsigned((uint)d);
                // (x is kept for the remainder: in ecx, or on the stack when ecx is needed)
                if (magic.Add && modulo) Emit("push eax");
                if (magic.Add || modulo) Emit("mov ecx, eax");
                Emit($"mov edx, {magic.Multiplier}");
                Emit($"mul edx                  ; edx = the top half of x * (2^{32 + magic.Shift} / {d}, rounded up)");
                if (magic.Add)
                {
                    Emit("sub ecx, edx");
                    Emit("shr ecx, 1");
                    Emit("add edx, ecx");
                    if (magic.Shift > 1) Emit($"shr edx, {magic.Shift - 1}");
                    if (modulo) Emit("pop ecx");
                }
                else if (magic.Shift > 0) Emit($"shr edx, {magic.Shift}");
                if (modulo)
                {
                    Emit($"imul edx, edx, {(int)d}");
                    Emit("sub ecx, edx");
                    Emit("mov eax, ecx");
                }
                else Emit("mov eax, edx");
                return true;
            }

            DivisionMagic.Signed signed = DivisionMagic.ForSigned((int)d);
            Emit("mov ecx, eax");
            Emit($"mov edx, {signed.Multiplier}");
            Emit($"imul edx                 ; edx = the top half of x * (2^{32 + signed.Shift} / {d})");
            if (signed.AddDividend) Emit("add edx, ecx");
            if (signed.Shift > 0) Emit($"sar edx, {signed.Shift}");
            Emit("mov eax, edx");
            Emit("shr eax, 31              ; 1 if it's negative: toward zero");
            Emit("add eax, edx");
            if (modulo)
            {
                Emit($"imul eax, eax, {(int)d}");
                Emit("sub ecx, eax");
                Emit("mov eax, ecx");
            }
            return true;
        }

        /// <summary>
        /// The operation with one side used as the instruction's operand, as it is: a constant or a
        /// 32-bit variable (add eax, 5 / cmp eax, dword [x]), instead of loading it into ecx first.
        /// When the left side is the simple one, the right is worked out first. False when neither
        /// side is simple, or for division (which needs its divisor in a register).
        /// </summary>
        bool TryDirectArithmetic(BinaryExpr binary, bool unsigned)
        {
            string? op = binary.Op switch
            {
                BinaryOp.Add => "add", BinaryOp.Subtract => "sub", BinaryOp.Multiply => "imul",
                BinaryOp.BitAnd => "and", BinaryOp.BitOr => "or", BinaryOp.BitXor => "xor",
                BinaryOp.ShiftLeft => "shl", BinaryOp.ShiftRight => unsigned ? "shr" : "sar",
                _ => null,
            };
            if (op == null) return false;
            bool shift = binary.Op is BinaryOp.ShiftLeft or BinaryOp.ShiftRight;

            string? right = DirectOperand(binary.Right, constantOnly: shift);
            if (right != null)
            {
                EmitExpr(binary.Left);
                // (times a power of two is a shift, which is quicker than a multiply)
                if (binary.Op == BinaryOp.Multiply && ConstantFolder.Evaluate(binary.Right) is long times && times >= 2 && (times & (times - 1)) == 0 && times <= 0x40000000)
                    Emit($"shl eax, {System.Numerics.BitOperations.Log2((ulong)times)}");
                else Emit($"{op} eax, {right}");
                return true;
            }

            // the left is simple: the right first, then the left goes with it
            string? left = DirectOperand(binary.Left, constantOnly: false);
            if (left == null || shift) return false;
            EmitExpr(binary.Right);
            if (binary.Op is BinaryOp.Add or BinaryOp.Multiply or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor)
            {
                Emit($"{op} eax, {left}");         // the order doesn't matter
            }
            else
            {
                Emit("mov ecx, eax");
                Emit($"mov eax, {left}");
                Emit($"{op} eax, ecx");
            }
            return true;
        }

        /// <summary>
        /// A constant, or a 32-bit number variable or field, written as an instruction's second
        /// operand; null for anything else. Reading it there reads memory once, as loading it would.
        /// </summary>
        string? DirectOperand(Expr e, bool constantOnly)
        {
            if (ConstantFolder.Evaluate(e) is long k && k >= int.MinValue && k <= uint.MaxValue)
                return ((int)k).ToString();
            if (constantOnly) return null;
            return e switch
            {
                NameExpr { Symbol: VariableSymbol { Type.IsNumeric: true } v } when StorageType(v.Type).ElementSize == 4 && !v.Type.IsFloat
                    => VariableOperand(v),
                NameExpr { Symbol: FieldPathSymbol { Type.IsNumeric: true } f } when StorageType(f.Type).ElementSize == 4 && !f.Type.IsFloat
                    => f.Offset == 0 ? $"dword [{f.Root.Label}]" : $"dword [{f.Root.Label} + {f.Offset}]",
                _ => null,
            };
        }

        void LoadConstant(string register, long value)
        {
            if (value == 0) Emit($"xor {register}, {register}");
            else Emit($"mov {register}, {(int)value}");
        }

        /// <summary>Left operand in eax, right operand in ecx.</summary>
        void EmitOperands(BinaryExpr b)
        {
            if (TryLoadSimple(b.Right, "ecx", dryRun: true))
            {
                EmitExpr(b.Left);
                TryLoadSimple(b.Right, "ecx", dryRun: false);
                return;
            }

            if (TryLoadSimple(b.Left, "eax", dryRun: true))
            {
                EmitExpr(b.Right);
                Emit("mov ecx, eax");
                TryLoadSimple(b.Left, "eax", dryRun: false);
                return;
            }

            // a left side worked out in eax alone: the right waits in ecx instead of on the stack
            if (OnlyEax(b.Left))
            {
                EmitExpr(b.Right);
                Emit("mov ecx, eax");
                int start = code.Length;
                EmitExpr(b.Left);
                foreach (string line in code.ToString(start, code.Length - start).Split('\n'))
                    if (!line.Contains("it stops here") && System.Text.RegularExpressions.Regex.IsMatch(WithoutComment(line), @"\b(ecx|cx|cl|ch|call)\b"))
                        throw new InvalidOperationException($"working out a value in eax alone changed ecx: {line.Trim()}");
                return;
            }

            EmitExpr(b.Right);
            Emit("push eax");
            EmitExpr(b.Left);
            Emit("pop ecx");
        }

        /// <summary>
        /// True when EmitExpr works the value out in eax (and ebx for a place) alone, leaving ecx
        /// and edx: a constant, a variable, an element or field reached without eax, or arithmetic
        /// whose right side is an operand as it is (see TryDirectArithmetic). 32-bit code only,
        /// since 16-bit code reaches memory through fs with ecx.
        /// </summary>
        bool OnlyEax(Expr e)
        {
            if (!bits32 || !OneRegister(e.Type)) return false;
            if (e is BinaryExpr or UnaryExpr && ConstantFolder.Evaluate(e) != null) return true;
            return e switch
            {
                NumberExpr or BoolExpr => true,
                NameExpr { Call: null, Symbol: VariableSymbol { Grows: false, TextRef: false } or FieldPathSymbol or ConstantSymbol } => true,
                NameExpr { Call: null, Symbol: SelfFieldSymbol or PathSymbol } => true,
                IndexExpr or MemberExpr { Call: null, Count: null, FilledLength: false, TextLength: false } => PlaceKeepsEax(e),
                BinaryExpr { IsComparison: false, IsLogical: false, Op: not (BinaryOp.Divide or BinaryOp.Modulo or BinaryOp.Power) } b =>
                    DirectOperand(b.Right, constantOnly: b.Op is BinaryOp.ShiftLeft or BinaryOp.ShiftRight) != null && OnlyEax(b.Left),
                UnaryExpr { Op: UnaryOp.Negate or UnaryOp.BitNot } u => OnlyEax(u.Operand),
                CastExpr c => OneRegister(c.Operand.Type) && OnlyEax(c.Operand),
                CheckedNumberExpr c => OnlyEax(c.Operand),
                _ => false,
            };
        }

        /// <summary>Loads values that need no other registers straight into a register.</summary>
        bool TryLoadSimple(Expr e, string register, bool dryRun)
        {
            // a label's address: in 64-bit code worked out from where the code is
            string LabelInto(string label) => bits64 ? $"lea {R(register)}, [rel {label}]" : $"mov {register}, {label}";
            // (a constant into a pointer's whole register, as it's stored into a pointer in memory:
            // one that fits in 32 bits with its sign, and a bigger one through the low half, which
            // clears the top as loading it into eax and storing rax would)
            string low = IsWhole(register) ? register.Replace("›", "d›") : register;
            string? instruction = ConstantFolder.Evaluate(e) is long k
                ? (k == 0 ? $"xor {low}, {low}" : k is >= int.MinValue and <= int.MaxValue ? $"mov {register}, {(int)k}" : $"mov {low}, {(int)k}")
                : e switch
                {
                    StringExpr s => LabelInto(StringLabel(s.Value)),
                    NameExpr { Symbol: VariableSymbol { Type.IsString: true, Grows: false, TextRef: false } v } =>
                        InFrame(v.Label) ? $"lea {register}, [{v.Label}]" : LabelInto(v.Label),
                    NameExpr { Symbol: VariableSymbol { Type.IsNumeric: true } v } =>
                        RegisterOf(v) is string held ? $"mov {(IsWhole(held) ? R(register) : register)}, {held}" : LoadInstruction(v.Type, register, $"[{v.Label}]"),
                    NameExpr { Call: null, Symbol: VariableSymbol { Type.IsObject: true } v } =>
                        bits64 ? $"mov {R(register)}, qword [{v.Label}]" : $"mov {register}, dword [{v.Label}]",
                    AddrExpr { CodeLabel: string code } => LabelInto(code),
                    AddrExpr { OfObject: false, Operand: NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false, Type.Open: false, Type.IsHeapText: false } v } } =>
                        InFrame(v.Label) ? $"lea {register}, [{v.Label}]" : LabelInto(v.Label),
                    NameExpr { Symbol: FieldPathSymbol { Type.IsNumeric: true } f } =>
                        LoadInstruction(f.Type, register, f.Offset == 0 ? $"[{f.Root.Label}]" : $"[{f.Root.Label} + {f.Offset}]"),
                    _ => null,
                };

            if (instruction == null) return false;
            if (!dryRun) Emit(instruction);
            return true;
        }

        /// <summary>Jumps to <paramref name="target"/> when the condition's truth equals <paramref name="jumpIf"/>.</summary>
        void EmitBranch(Expr condition, bool jumpIf, string target)
        {
            if (ConstantFolder.Evaluate(condition) is long known)
            {
                if ((known != 0) == jumpIf) Emit($"jmp {target}");
                return;
            }

            switch (condition)
            {
                case UnaryExpr { Op: UnaryOp.Not } not:
                    EmitBranch(not.Operand, !jumpIf, target);
                    return;

                case BinaryExpr { Op: BinaryOp.And } and:
                    if (jumpIf)
                    {
                        string skip = NewLabel("and");
                        EmitBranch(and.Left, false, skip);
                        EmitBranch(and.Right, true, target);
                        Label(skip);
                    }
                    else
                    {
                        EmitBranch(and.Left, false, target);
                        EmitBranch(and.Right, false, target);
                    }
                    return;

                case BinaryExpr { Op: BinaryOp.Or } or:
                    if (jumpIf)
                    {
                        EmitBranch(or.Left, true, target);
                        EmitBranch(or.Right, true, target);
                    }
                    else
                    {
                        string skip = NewLabel("or");
                        EmitBranch(or.Left, true, skip);
                        EmitBranch(or.Right, false, target);
                        Label(skip);
                    }
                    return;

                case BinaryExpr { ArrayCompare: true } arrays:
                {
                    EmitArrayCompare(arrays);
                    Emit("test eax, eax");
                    bool equalWanted = (arrays.Op == BinaryOp.Equal) == jumpIf;
                    Emit($"{(equalWanted ? "jnz" : "jz")} {target}");
                    return;
                }

                case BinaryExpr { ObjectCompare: StructSymbol compared } cmp:
                {
                    bool equalWanted = (cmp.Op == BinaryOp.Equal) == jumpIf;
                    if (cmp.Left is EmptyExpr || cmp.Right is EmptyExpr)
                    {
                        EmitExpr(cmp.Left is EmptyExpr ? cmp.Right : cmp.Left);
                        Emit($"test {R("eax")}, {R("eax")}          ; 0 = empty");
                        Emit($"{(equalWanted ? "jz" : "jnz")} {target}");
                        return;
                    }
                    EmitObjectAddress(cmp.Right);
                    Emit("push eax");
                    EmitObjectAddress(cmp.Left);
                    Emit("pop edx");
                    Emit($"call {ObjectRoutine("equal", compared)}   ; eax = 1 when the values match");
                    Emit("test eax, eax");
                    Emit($"{(equalWanted ? "jnz" : "jz")} {target}");
                    return;
                }

                case BinaryExpr { IsComparison: true, Left.Type.IsString: true, Right.Type.IsString: true } cmp:
                {
                    Need("phi_strcmp");
                    EmitExpr(cmp.Right);
                    Emit("push eax");
                    EmitExpr(cmp.Left);
                    Emit($"mov {R("esi")}, {R("eax")}");
                    Emit("pop edi");
                    Emit("call phi_strcmp          ; eax = 0 when equal");
                    Emit("test eax, eax");
                    bool equalWanted = (cmp.Op == BinaryOp.Equal) == jumpIf;
                    Emit($"{(equalWanted ? "jz" : "jnz")} {target}");
                    return;
                }

                case BinaryExpr { IsComparison: true } cmp when cmp.Left.Type.IsFloat || cmp.Right.Type.IsFloat:
                {
                    // The flags are an unsigned comparison's, with all three set when one side is
                    // not a number: then nothing holds but !=. Above and above-or-equal are false
                    // by themselves then; the others ask the parity flag first.
                    EmitFloatFlags(cmp);
                    string holds = cmp.Op switch
                    {
                        BinaryOp.Equal => "je",
                        BinaryOp.NotEqual => "jne",
                        BinaryOp.Less => "jb",
                        BinaryOp.LessEqual => "jbe",
                        BinaryOp.Greater => "ja",
                        _ => "jae",
                    };
                    string fails = cmp.Op switch
                    {
                        BinaryOp.Equal => "jne",
                        BinaryOp.NotEqual => "je",
                        BinaryOp.Less => "jae",
                        BinaryOp.LessEqual => "ja",
                        BinaryOp.Greater => "jbe",
                        _ => "jb",
                    };
                    bool byItself = cmp.Op is BinaryOp.Greater or BinaryOp.GreaterEqual;
                    // (with not a number: != holds, the rest fail)
                    bool nanJumps = (cmp.Op == BinaryOp.NotEqual) == jumpIf;
                    if (byItself) Emit($"{(jumpIf ? holds : fails)} {target}");
                    else if (nanJumps)
                    {
                        Emit($"jp {target}              ; not a number");
                        Emit($"{(jumpIf ? holds : fails)} {target}");
                    }
                    else
                    {
                        string past = NewLabel("nan");
                        Emit($"jp {past}              ; not a number");
                        Emit($"{(jumpIf ? holds : fails)} {target}");
                        Label(past);
                    }
                    return;
                }

                case BinaryExpr { IsComparison: true } cmp when (cmp.Left.Type.IsWide || cmp.Right.Type.IsWide) && WholeValue(cmp.Left) && WholeValue(cmp.Right):
                {
                    // 64-bit code: one cmp of the two whole, and the jump
                    bool asUnsigned = EmitWholeFlags(cmp);
                    Emit($"j{WholeCondition(cmp.Op, asUnsigned, holds: jumpIf)} {target}");
                    return;
                }

                case BinaryExpr { IsComparison: true } cmp when cmp.Left.Type.IsWide || cmp.Right.Type.IsWide:
                    EmitWideCompare(cmp);
                    Emit("test eax, eax");
                    Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
                    return;

                case BinaryExpr { IsComparison: true } cmp when IsAddress(cmp.Left) || IsAddress(cmp.Right):
                {
                    // two addresses (or an address and a number) in 64 bits, as unsigned numbers:
                    // pointers in variables, and a small constant, compared where they are
                    string? leftPointer = PointerOperand(cmp.Left);
                    string? rightPointer = PointerOperand(cmp.Right)
                        ?? (ConstantFolder.Evaluate(cmp.Right) is long small and >= 0 and <= int.MaxValue ? small.ToString() : null);
                    if (leftPointer != null && rightPointer != null)
                    {
                        if (leftPointer.Contains('[') && rightPointer.Contains('['))
                        {
                            Emit($"mov rax, {leftPointer}");
                            leftPointer = "rax";
                        }
                        Emit($"cmp {leftPointer}, {rightPointer}");
                    }
                    else
                    {
                        EmitAddressOperands(cmp);
                        Emit("cmp rax, rcx");
                    }
                    BinaryOp op = jumpIf ? cmp.Op : Invert(cmp.Op);
                    string jump = op switch
                    {
                        BinaryOp.Equal => "je",
                        BinaryOp.NotEqual => "jne",
                        BinaryOp.Less => "jb",
                        BinaryOp.LessEqual => "jbe",
                        BinaryOp.Greater => "ja",
                        _ => "jae",
                    };
                    Emit($"{jump} {target}");
                    return;
                }

                case BinaryExpr { IsComparison: true } cmp:
                {
                    bool unsigned = cmp.Left.Type.IsUnsigned32 || cmp.Right.Type.IsUnsigned32;
                    string? leftAsIs = HeldIn(cmp.Left) ?? (ConstantFolder.Evaluate(cmp.Left) == null ? DirectOperand(cmp.Left, constantOnly: false) : null);
                    string? rightAsIs = DirectOperand(cmp.Right, constantOnly: false);
                    if (leftAsIs == null && rightAsIs != null && !rightAsIs.Contains('[') && PlaceOperand(cmp.Left))
                        leftAsIs = $"dword {Operand(PlaceKeepingEax(cmp.Left))}";
                    if (leftAsIs != null && rightAsIs != null && !(leftAsIs.Contains('[') && rightAsIs.Contains('[')))
                    {
                        Emit($"cmp {leftAsIs}, {rightAsIs}");
                    }
                    else if (DirectOperand(cmp.Right, constantOnly: false) is string right)
                    {
                        EmitExpr(cmp.Left);
                        Emit($"cmp eax, {right}");
                    }
                    else
                    {
                        EmitOperands(cmp);
                        Emit("cmp eax, ecx");
                    }
                    BinaryOp op = jumpIf ? cmp.Op : Invert(cmp.Op);
                    string jump = op switch
                    {
                        BinaryOp.Equal => "je",
                        BinaryOp.NotEqual => "jne",
                        BinaryOp.Less => unsigned ? "jb" : "jl",
                        BinaryOp.LessEqual => unsigned ? "jbe" : "jle",
                        BinaryOp.Greater => unsigned ? "ja" : "jg",
                        _ => unsigned ? "jae" : "jge",
                    };
                    Emit($"{jump} {target}");
                    return;
                }
            }

            if (condition.Type.IsFloat)
            {
                EmitFloatIsZero(condition);
                Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
                return;
            }
            EmitExpr(condition);
            if (condition.Type.IsWide) Emit("or eax, edx");
            else Emit(IsAddress(condition) ? "test rax, rax" : "test eax, eax");
            Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
        }

        // ================================================================ floating point
        //
        // An f16, f32 or f64 is worked on in the FPU: EmitFloat leaves one value in st0, on top
        // of whatever was there. An operation takes a side that's a variable or a constant
        // straight from memory (fmul qword [x]), and keeps the left side on the FPU's stack while
        // the right is worked out. A call may use the FPU itself, all eight places of it over
        // enough calls, so nothing is kept there across one: when either side has a call, the
        // right is worked out first and waits on the CPU's stack, as it does when the FPU's own
        // stack is getting full (floatDepth). An error caught leaves the FPU as it started
        // (EmitFloatReset), whatever was on it where the error was.

        /// <summary>How many values are waiting on the FPU's stack under the one being worked out.</summary>
        int floatDepth;

        /// <summary>The most that wait there: the value being worked out and what a step needs for a moment come to three more, and a `when` keeps one.</summary>
        const int FloatDepthMost = 4;

        /// <summary>The FPU empty and rounding to f64s, as a program starts.</summary>
        void EmitFloatReset()
        {
            Emit("fninit");
            Emit("sub esp, 4");
            Emit("mov word [esp], 0x27F");
            Emit("fldcw [esp]");
            Emit("add esp, 4");
        }

        /// <summary>True for a floating point value that's in memory as an f64 or f32 and reached without a call: a constant, a variable, a field or an element.</summary>
        static bool IsFloatInMemory(Expr e) => e switch
        {
            DecimalExpr => true,
            NameExpr { Call: null } or MemberExpr { Call: null } or IndexExpr => e.Type.IsFloat && e.Type.Kind is TypeKind.F64 or TypeKind.F32 && !HasCall(e),
            _ => false,
        };

        /// <summary>st0 = st0 op e, or e op st0 when reversed, for an e that IsFloatInMemory (st0 waits while its place is found).</summary>
        void EmitFloatWithMemory(BinaryOp op, Expr e, bool reversed)
        {
            string instruction = op switch
            {
                BinaryOp.Add => "fadd",
                BinaryOp.Multiply => "fmul",
                BinaryOp.Subtract => reversed ? "fsubr" : "fsub",
                _ => reversed ? "fdivr" : "fdiv",
            };
            Emit($"{instruction} {FloatMemory(e)}");
        }

        /// <summary>Where an e that IsFloatInMemory is, as an instruction's operand (qword [x]); st0 waits while the place is found.</summary>
        string FloatMemory(Expr e)
        {
            if (e is DecimalExpr d) return $"qword [{FloatLabel(d.Value)}]";
            floatDepth++;
            Place place = EmitPlace(e);
            floatDepth--;
            return $"{(e.Type.Kind == TypeKind.F64 ? "qword" : "dword")} {Operand(place)}";
        }

        readonly Dictionary<double, string> floatLabels = new();

        string FloatLabel(double value)
        {
            if (!floatLabels.TryGetValue(value, out string? label))
            {
                label = $"phi_float_{floatLabels.Count}";
                floatLabels[value] = label;
            }
            return label;
        }

        /// <summary>st0 = e: a floating point value, or a whole number made one.</summary>
        void EmitFloat(Expr e)
        {
            if (!e.Type.IsFloat)
            {
                if (ConstantFolder.Evaluate(e) is long k)
                {
                    if (!e.Type.IsWide && !e.Type.IsSigned) k = (uint)k;
                    if (k == 0) Emit("fldz");
                    else if (k == 1) Emit("fld1");
                    else Emit($"fld qword [{FloatLabel(k)}]");
                }
                else if (e.Type.IsWide)
                {
                    EmitWide(e);
                    PushAndLoad("qword", "edx", "eax");
                    if (e.Type.Kind == TypeKind.U64)
                    {
                        // (the FPU read it as signed: one with its top bit set is 2^64 too low)
                        string positive = NewLabel("positive");
                        Emit("test edx, edx");
                        Emit($"jns {positive}");
                        Emit($"fadd qword [{FloatLabel(18446744073709551616.0)}]");
                        Label(positive);
                    }
                    // The FPU holds all 64 bits of it, which is more than an f64 does: adding
                    // nothing rounds it to the nearest f64, which is what the value is. Without
                    // this, (big as f64) + x was the nearest f64 to the whole sum, and not what
                    // it comes to when the f64 is stored first.
                    Emit("fldz");
                    Emit("faddp st1, st0");
                }
                else if (e.Type.IsUnsigned32)
                {
                    EmitExpr(e);
                    if (bits64) Emit("mov eax, eax              ; (the top half clear: one 8-byte push)");
                    else Emit("push dword 0");
                    Emit("push eax");
                    Emit("fild qword [esp]");
                    Emit("add esp, 8");
                }
                else
                {
                    EmitExpr(e);
                    PushAndLoad("dword", "eax");
                }
                return;
            }

            switch (e)
            {
                case DecimalExpr d:
                    if (d.Value == 0 && !double.IsNegative(d.Value)) Emit("fldz");
                    else if (d.Value == 1) Emit("fld1");
                    else Emit($"fld qword [{FloatLabel(d.Value)}]");
                    return;

                case CallExpr call:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case NameExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case MemberExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;

                case CastExpr cast:
                    EmitFloat(cast.Operand);
                    return;

                case UnaryExpr unary:
                    EmitFloat(unary.Operand);
                    Emit("fchs");
                    return;

                case BinaryExpr { Op: BinaryOp.Modulo } modulo:
                {
                    // what's left over, with the left's sign (like C's fmod): fprem goes until it's done
                    EmitFloatPair(modulo.Left, modulo.Right);
                    Emit("fxch                    ; st0 = the left, st1 = the right");
                    string again = NewLabel("fprem");
                    Label(again);
                    Emit("fprem");
                    Emit("fnstsw ax");
                    Emit("sahf");
                    Emit($"jp {again}              ; c2: not finished");
                    Emit("fstp st1");
                    return;
                }

                case BinaryExpr binary when IsFloatInMemory(binary.Right) && (binary.Right is DecimalExpr || !HasCall(binary.Left)):
                    // the right side straight from memory (after a call on the left, only a
                    // constant: a call could change a variable, which was read before it)
                    EmitFloat(binary.Left);
                    EmitFloatWithMemory(binary.Op, binary.Right, reversed: false);
                    return;

                case BinaryExpr binary when IsFloatInMemory(binary.Left):
                    // the left side straight from memory, after the right, calls and all
                    EmitFloat(binary.Right);
                    EmitFloatWithMemory(binary.Op, binary.Left, reversed: true);
                    return;

                case BinaryExpr binary:
                    EmitFloatPair(binary.Left, binary.Right);
                    Emit(binary.Op switch
                    {
                        BinaryOp.Add => "faddp st1, st0",
                        BinaryOp.Subtract => "fsubp st1, st0",
                        BinaryOp.Multiply => "fmulp st1, st0",
                        _ => "fdivp st1, st0",
                    });
                    return;
            }

            // a variable, field or element
            Place place = EmitPlace(e);
            switch (e.Type.Kind)
            {
                case TypeKind.F64: Emit($"fld qword {Operand(place)}"); break;
                case TypeKind.F32: Emit($"fld dword {Operand(place)}"); break;
                default:
                    Need("phi_f16_to_f32");
                    Emit($"movzx eax, word {Operand(place)}");
                    Emit("call phi_f16_to_f32");
                    PushAndLoad("dword", "eax", isFloat: true);
                    break;
            }
        }

        /// <summary>Pushes registers (the last is the low half) and loads them into st0 as a whole number (or as an f32).</summary>
        void PushAndLoad(string size, string first, string? second = null, bool isFloat = false)
        {
            if (bits64)
            {
                // (a push is 8 bytes there: the halves are written to the stack instead)
                Emit("sub esp, 8");
                Emit($"mov [esp], {second ?? first}");
                if (second != null) Emit($"mov [esp + 4], {first}");
                Emit($"{(isFloat ? "fld" : "fild")} {size} [esp]");
                Emit("add esp, 8");
                return;
            }
            Emit($"push {first}");
            if (second != null) Emit($"push {second}");
            Emit($"{(isFloat ? "fld" : "fild")} {size} [esp]");
            Emit($"add esp, {(second != null ? 8 : 4)}");
        }

        /// <summary>st0 = right, st1 = left.</summary>
        void EmitFloatPair(Expr left, Expr right)
        {
            if (floatDepth < FloatDepthMost && !HasCall(left) && !HasCall(right))
            {
                // the left waits on the FPU's stack
                EmitFloat(left);
                floatDepth++;
                EmitFloat(right);
                floatDepth--;
                return;
            }
            EmitFloat(right);
            Emit("sub esp, 8");
            Emit("fstp qword [esp]");
            EmitFloat(left);
            Emit("fld qword [esp]");
            Emit("add esp, 8");
        }

        /// <summary>Takes st0 off the FPU's stack into a floating point place.</summary>
        void EmitFloatStore(Expr target)
        {
            Place place = EmitPlace(target);
            switch (target.Type.Kind)
            {
                case TypeKind.F64: Emit($"fstp qword {Operand(place)}"); break;
                case TypeKind.F32: Emit($"fstp dword {Operand(place)}"); break;
                default:
                    Need("phi_f32_to_f16");
                    Emit($"sub esp, {Slot}");
                    Emit("fstp dword [esp]");
                    Emit("pop eax");
                    Emit("call phi_f32_to_f16");
                    Emit($"mov word {Operand(place)}, ax");
                    break;
            }
        }

        /// <summary>edx:eax = a floating point value's whole part (rounded toward zero, as `as` does).</summary>
        void EmitFloatToWhole(Expr e)
        {
            EmitFloat(e);
            Emit("sub esp, 12");
            Emit("fnstcw [esp]");
            Emit("mov ax, [esp]");
            Emit("or ax, 0x0C00            ; round toward zero");
            Emit("mov [esp + 2], ax");
            Emit("fldcw [esp + 2]");
            Emit("fistp qword [esp + 4]");
            Emit("fldcw [esp]");
            Emit("mov eax, [esp + 4]");
            Emit("mov edx, [esp + 8]");
            Emit("add esp, 12");
        }

        /// <summary>Flags as for `cmp`: ZF set when the floating point value is zero.</summary>
        void EmitFloatIsZero(Expr e)
        {
            EmitFloat(e);
            Emit("ftst");
            Emit("fnstsw ax");
            Emit("fstp st0");
            Emit("sahf");
        }

        /// <summary>eax = 1 when a comparison of floating point values holds (never, with not-a-number, except !=).</summary>
        void EmitFloatCompare(BinaryExpr cmp)
        {
            EmitFloatFlags(cmp);
            string unordered = NewLabel("nan");
            string done = NewLabel("fcompared");
            Emit($"jp {unordered}");
            Emit(cmp.Op switch
            {
                BinaryOp.Equal => "sete al",
                BinaryOp.NotEqual => "setne al",
                BinaryOp.Less => "setb al",
                BinaryOp.LessEqual => "setbe al",
                BinaryOp.Greater => "seta al",
                _ => "setae al",
            });
            Emit("movzx eax, al");
            Emit($"jmp {done}");
            Label(unordered);
            Emit($"mov eax, {(cmp.Op == BinaryOp.NotEqual ? 1 : 0)}");
            Label(done);
        }

        /// <summary>
        /// The CPU's flags as an unsigned comparison of the left with the right would leave them
        /// (below, equal, above), and the parity flag set when either is not a number. Changes eax.
        /// </summary>
        void EmitFloatFlags(BinaryExpr cmp)
        {
            if (IsFloatInMemory(cmp.Right) && (cmp.Right is DecimalExpr || !HasCall(cmp.Left)))
            {
                EmitFloat(cmp.Left);
                Emit($"fcomp {FloatMemory(cmp.Right)}");
            }
            else
            {
                EmitFloatPair(cmp.Left, cmp.Right);
                Emit("fxch                    ; st0 = the left, st1 = the right");
                Emit("fcompp");
            }
            Emit("fnstsw ax");
            Emit("sahf");
        }

        /// <summary>st0 = Float.Sqrt and the others, worked out on st0.</summary>
        void EmitFloatFunction(BuiltinFunction f, Expr argument)
        {
            EmitFloat(argument);
            if (f.FloatRounding < 0)
            {
                Emit(f.FloatInstruction!);
                return;
            }
            Emit("sub esp, 4");
            Emit("fnstcw [esp]");
            Emit("mov ax, [esp]");
            Emit("and ax, 0xF3FF");
            Emit($"or ax, {f.FloatRounding << 10}         ; how frndint rounds");
            Emit("mov [esp + 2], ax");
            Emit("fldcw [esp + 2]");
            Emit("frndint");
            Emit("fldcw [esp]");
            Emit("add esp, 4");
        }

        /// <summary>eax = the address of a 64-bit or floating point value written out, or of a chr's character (in a temporary).</summary>
        void EmitNumberText(Expr e)
        {
            if (e.Type.IsChar)
            {
                string one = AllocTemp(4);
                EmitExpr(e);
                Emit($"mov byte [{one}], al");
                Emit($"mov byte [{one} + 1], 0");
                LoadAddress("eax", one);
                return;
            }
            if (e.Type.IsWide)
            {
                EmitWideText(e);
                return;
            }
            string text = AllocTemp(48);
            EmitFloat(e);
            Need("phi_float_to_str");
            LoadAddress("edi", text);
            Emit("mov ecx, 48");
            Emit("call phi_float_to_str");
            LoadAddress("eax", text);
        }

        // ================================================================ 64-bit numbers
        //
        // An i64 or u64 is in edx:eax (edx the high half). The other side of an operation is
        // in ebx:ecx, and the library's int64.asm does multiplying, dividing and text.

        /// <summary>edx:eax = e, made 64 bits if it's a smaller number.</summary>
        void EmitWide(Expr e)
        {
            if (ConstantFolder.Evaluate(e) is long k)
            {
                if (!e.Type.IsWide && !e.Type.IsSigned) k = (uint)k;   // a u32 stays positive
                Emit($"mov eax, {(int)k}");
                Emit($"mov edx, {(int)(k >> 32)}");
                return;
            }
            if (e is BinaryExpr or UnaryExpr && WholeValue(e))
            {
                // 64-bit code: worked out in rax whole, then as two halves for whoever asked
                EmitWhole(e);
                Emit("mov rdx, rax");
                Emit("shr rdx, 32");
                Emit("mov eax, eax");
                return;
            }
            if (!e.Type.IsWide)
            {
                EmitExpr(e);
                if (IsAddress(e))
                {
                    // an address is all of rax in 64-bit code: its high half goes to edx
                    Emit("mov rdx, rax");
                    Emit("shr rdx, 32");
                    Emit("mov eax, eax");
                }
                else Emit(e.Type.IsSigned ? "cdq" : "xor edx, edx");
                return;
            }

            switch (e)
            {
                case CallExpr call:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case NameExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case MemberExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;

                case CastExpr cast:
                    if (cast.Operand.Type.IsFloat) EmitFloatToWhole(cast.Operand);
                    else EmitWide(cast.Operand);
                    return;

                case UnaryExpr unary:
                    EmitWide(unary.Operand);
                    if (unary.Op == UnaryOp.Negate)
                    {
                        Emit("neg eax");
                        Emit("adc edx, 0");
                        Emit("neg edx");
                    }
                    else
                    {
                        Emit("not eax");
                        Emit("not edx");
                    }
                    return;

                case BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRight } shift
                    when ConstantFolder.Evaluate(shift.Right) is long places and >= 0 and <= 63:
                {
                    // by a constant: the instructions for that many places, with nothing to test
                    EmitWide(shift.Left);
                    bool left = shift.Op == BinaryOp.ShiftLeft, unsigned = shift.Type.Kind == TypeKind.U64;
                    if (places == 0) return;
                    if (places < 32)
                    {
                        if (left)
                        {
                            Emit($"shld edx, eax, {places}");
                            Emit($"shl eax, {places}");
                        }
                        else
                        {
                            Emit($"shrd eax, edx, {places}");
                            Emit($"{(unsigned ? "shr" : "sar")} edx, {places}");
                        }
                        return;
                    }
                    // 32 or more: one half goes to the other, and what's left of the shift is its own
                    if (left)
                    {
                        Emit("mov edx, eax");
                        if (places > 32) Emit($"shl edx, {places - 32}");
                        Emit("xor eax, eax");
                    }
                    else if (unsigned)
                    {
                        Emit("mov eax, edx");
                        if (places > 32) Emit($"shr eax, {places - 32}");
                        Emit("xor edx, edx");
                    }
                    else
                    {
                        Emit("mov eax, edx");
                        Emit("sar edx, 31");
                        if (places > 32) Emit($"sar eax, {places - 32}");
                    }
                    return;
                }

                case BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRight } shift:
                {
                    EmitExpr(shift.Right);
                    Emit("push eax");
                    EmitWide(shift.Left);
                    Emit("pop ecx");
                    string small = NewLabel("shift");
                    Emit("test cl, 32");
                    Emit($"jz {small}");
                    if (shift.Op == BinaryOp.ShiftLeft)
                    {
                        Emit("mov edx, eax");
                        Emit("xor eax, eax");
                        Label(small);
                        Emit("shld edx, eax, cl");
                        Emit("shl eax, cl");
                    }
                    else if (shift.Type.Kind == TypeKind.U64)
                    {
                        Emit("mov eax, edx");
                        Emit("xor edx, edx");
                        Label(small);
                        Emit("shrd eax, edx, cl");
                        Emit("shr edx, cl");
                    }
                    else
                    {
                        Emit("mov eax, edx");
                        Emit("sar edx, 31");
                        Label(small);
                        Emit("shrd eax, edx, cl");
                        Emit("sar edx, cl");
                    }
                    return;
                }

                case BinaryExpr { Op: BinaryOp.Multiply } product when TryWideMultiply(product):
                    return;

                case BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor } sum
                    when TryWideInPlace(sum):
                    return;

                case BinaryExpr binary:
                {
                    EmitWideOperands(binary);
                    bool unsigned = binary.Type.Kind == TypeKind.U64;
                    switch (binary.Op)
                    {
                        case BinaryOp.Add: Emit("add eax, ecx"); Emit("adc edx, ebx"); break;
                        case BinaryOp.Subtract: Emit("sub eax, ecx"); Emit("sbb edx, ebx"); break;
                        case BinaryOp.BitAnd: Emit("and eax, ecx"); Emit("and edx, ebx"); break;
                        case BinaryOp.BitOr: Emit("or eax, ecx"); Emit("or edx, ebx"); break;
                        case BinaryOp.BitXor: Emit("xor eax, ecx"); Emit("xor edx, ebx"); break;
                        case BinaryOp.Multiply: CallWide("phi_mul64"); break;
                        case BinaryOp.Divide: CallWide(unsigned ? "phi_udiv64" : "phi_div64"); break;
                        case BinaryOp.Modulo: CallWide(unsigned ? "phi_umod64" : "phi_mod64"); break;
                        default: throw new InvalidOperationException($"no 64-bit {binary.Op}");
                    }
                    return;
                }
            }

            // a variable, field or element: its two halves
            Place place = EmitPlace(e);
            Emit($"mov eax, dword {Operand(place)}");
            Emit($"mov edx, dword {Operand(place.Plus(4))}");
        }

        // ---- 64-bit code: a 64-bit number whole in one register
        //
        // In a 64-bit program an i64 or u64 fits rax, and adding, multiplying, shifting and
        // comparing are one instruction each. EmitWhole works a value out that way, the other
        // side of an operation in rcx, for the values WholeValue says it can: nothing with a
        // call in it (so what's read when can't matter), a division (the library's says what
        // dividing by zero is) or a float. Everything else is EmitWide's, in edx:eax.

        /// <summary>
        /// True for a value EmitWhole works out in rax, rcx and rdx alone, in 64-bit code:
        /// constants, numbers of 32 bits or fewer that are safe with the variables in registers and
        /// call nothing, 64-bit variables, fields and elements, and casting, negating, adding,
        /// taking away, multiplying, shifting and the bit operations of those.
        /// </summary>
        bool WholeValue(Expr e) => e switch
        {
            _ when !bits64 => false,
            _ when e.Type.IsFloat || IsAddress(e) => false,
            _ when ConstantFolder.Evaluate(e) != null => true,
            _ when !e.Type.IsWide => OneRegister(e.Type) && SafeValue(e) && !HasCall(e),
            NameExpr { Call: null } or MemberExpr { Call: null } or IndexExpr => KeptSomewhere(e) && SafePlace(e),
            CastExpr c => WholeValue(c.Operand),
            UnaryExpr u => WholeValue(u.Operand),
            BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRight } shift =>
                WholeValue(shift.Left) && !shift.Right.Type.IsWide && WholeValue(shift.Right),
            BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor } b =>
                WholeValue(b.Left) && WholeValue(b.Right),
            _ => false,
        };

        /// <summary>The constant a value is, as EmitWide would make it 64 bits: an unsigned 32-bit number stays positive.</summary>
        static long? WholeConstant(Expr e)
        {
            if (ConstantFolder.Evaluate(e) is not long k) return null;
            return !e.Type.IsWide && !e.Type.IsSigned ? (uint)k : k;
        }

        /// <summary>rax = e, made 64 bits if it's a smaller number, for an e that's a WholeValue.</summary>
        void EmitWhole(Expr e)
        {
            if (WholeConstant(e) is long k)
            {
                Emit(k == 0 ? "xor eax, eax" : k is > 0 and <= uint.MaxValue ? $"mov eax, {k}" : $"mov rax, {k}");
                return;
            }
            if (!e.Type.IsWide)
            {
                EmitExpr(e);
                Emit(e.Type.IsSigned ? "movsxd rax, eax" : "mov eax, eax");
                return;
            }
            switch (e)
            {
                case CastExpr cast:
                    EmitWhole(cast.Operand);
                    return;

                case UnaryExpr unary:
                    EmitWhole(unary.Operand);
                    Emit(unary.Op == UnaryOp.Negate ? "neg rax" : "not rax");
                    return;

                case BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRight } shift:
                {
                    string how = shift.Op == BinaryOp.ShiftLeft ? "shl" : shift.Type.Kind == TypeKind.U64 ? "shr" : "sar";
                    if (ConstantFolder.Evaluate(shift.Right) is long places and >= 0 and <= 63)
                    {
                        EmitWhole(shift.Left);
                        if (places != 0) Emit($"{how} rax, {places}");
                        return;
                    }
                    // (the CPU takes the count's low six bits, as the two halves' shift does)
                    EmitExpr(shift.Right);
                    Emit("push rax");
                    EmitWhole(shift.Left);
                    Emit("pop rcx");
                    Emit($"{how} rax, cl");
                    return;
                }

                case BinaryExpr binary:
                {
                    string op = binary.Op switch
                    {
                        BinaryOp.Add => "add", BinaryOp.Subtract => "sub", BinaryOp.Multiply => "imul",
                        BinaryOp.BitAnd => "and", BinaryOp.BitOr => "or", _ => "xor",
                    };
                    Emit($"{op} rax, {EmitWholeSides(binary.Left, binary.Right)}");
                    return;
                }
            }

            // a variable, field or element
            Emit($"mov rax, qword {Operand(EmitPlace(e))}");
        }

        /// <summary>
        /// rax = the left side, and the right as an instruction's operand: a constant that fits,
        /// a 64-bit variable where it is, or rcx.
        /// </summary>
        string EmitWholeSides(Expr left, Expr right)
        {
            if (WholeConstant(right) is long k)
            {
                EmitWhole(left);
                if (k is >= int.MinValue and <= int.MaxValue) return k.ToString();
                Emit($"mov rcx, {k}");
                return "rcx";
            }
            if (right.Type.IsWide && IsStaticPlace(right))
            {
                EmitWhole(left);
                return $"qword {Operand(EmitPlace(right))}";
            }
            EmitWhole(right);
            Emit("push rax");
            EmitWhole(left);
            Emit("pop rcx");
            return "rcx";
        }

        /// <summary>Stores rax in a 64-bit place.</summary>
        void EmitWholeStore(Expr target)
        {
            if (IsStaticPlace(target))
            {
                Emit($"mov qword {Operand(EmitPlace(target))}, rax");
                return;
            }
            Emit("push rax");
            string to = Operand(EmitPlace(target));
            Emit("pop rax");
            Emit($"mov qword {to}, rax");
        }

        /// <summary>
        /// The CPU's flags as cmp leaves them for the left against the right, both 64 bits whole.
        /// True when they're to be read as unsigned (ja, jb), as either side being a u64 makes them.
        /// </summary>
        bool EmitWholeFlags(BinaryExpr cmp)
        {
            Emit($"cmp rax, {EmitWholeSides(cmp.Left, cmp.Right)}");
            return cmp.Left.Type.Kind == TypeKind.U64 || cmp.Right.Type.Kind == TypeKind.U64;
        }

        /// <summary>The condition (e, ne, l, ge, b, ae ...) a comparison holds on, or doesn't, after EmitWholeFlags.</summary>
        static string WholeCondition(BinaryOp op, bool unsigned, bool holds) => (op, holds) switch
        {
            (BinaryOp.Equal, true) or (BinaryOp.NotEqual, false) => "e",
            (BinaryOp.NotEqual, true) or (BinaryOp.Equal, false) => "ne",
            (BinaryOp.Less, true) or (BinaryOp.GreaterEqual, false) => unsigned ? "b" : "l",
            (BinaryOp.LessEqual, true) or (BinaryOp.Greater, false) => unsigned ? "be" : "le",
            (BinaryOp.Greater, true) or (BinaryOp.LessEqual, false) => unsigned ? "a" : "g",
            _ => unsigned ? "ae" : "ge",
        };

        void CallWide(string routine)
        {
            Need(routine);
            Emit($"call {routine}");
        }

        /// <summary>edx:eax = the left side, ebx:ecx = the right.</summary>
        void EmitWideOperands(BinaryExpr b)
        {
            // (a constant on the right goes straight into ebx:ecx, with nothing on the stack)
            if (ConstantFolder.Evaluate(b.Right) is long constant)
            {
                EmitWide(b.Left);
                LoadConstant("ecx", (int)constant);
                LoadConstant("ebx", (int)(constant >> 32));
                return;
            }
            // a 64-bit variable on the right: its halves are read where they are. (Read after
            // the left is worked out, so not when the left calls something that could change it.)
            if (b.Right.Type.IsWide && IsStaticPlace(b.Right) && !HasCall(b.Left))
            {
                EmitWide(b.Left);
                Place right = EmitPlace(b.Right);
                Emit($"mov ecx, dword {Operand(right)}");
                Emit($"mov ebx, dword {Operand(right.Plus(4))}");
                return;
            }
            // a 32-bit number on the right: only it waits on the stack, and its high half is
            // made (zeros, or its sign) once the left is worked out
            if (Narrow(b.Right) is Expr narrow)
            {
                if (!HasCall(b.Left) && (DirectOperand(narrow, constantOnly: false) != null || PlaceOperand(narrow)))
                {
                    EmitWide(b.Left);
                    if (ReadWhereItIs(narrow) is string direct) Emit($"mov ecx, {direct}");
                    else
                    {
                        // (reaching it needs more registers than are free: the left waits instead)
                        Emit("push edx");
                        Emit("push eax");
                        EmitExpr(narrow);
                        Emit("mov ecx, eax");
                        Emit("pop eax");
                        Emit("pop edx");
                    }
                }
                else if (b.Left.Type.IsWide && IsStaticPlace(b.Left))
                {
                    // the left is a variable: the right goes to ecx, and the left is loaded after
                    EmitExpr(narrow);
                    Emit("mov ecx, eax");
                    EmitWide(b.Left);
                }
                else
                {
                    EmitExpr(narrow);
                    Emit("push eax");
                    EmitWide(b.Left);
                    Emit("pop ecx");
                }
                if (narrow.Type.IsSigned)
                {
                    Emit("mov ebx, ecx");
                    Emit("sar ebx, 31");
                }
                else Emit("xor ebx, ebx");
                return;
            }
            EmitWide(b.Right);
            Emit("push edx");
            Emit("push eax");
            EmitWide(b.Left);
            Emit("pop ecx");
            Emit("pop ebx");
        }

        /// <summary>
        /// The 32-bit number that a side of a 64-bit operation is made from: the side itself if it
        /// isn't 64 bits, or what's under a cast to 64 bits (x as u64). Null for a 64-bit value, a
        /// constant (which EmitWide writes out whole), a float, or an address. EmitWide makes its
        /// high half zeros if its type is unsigned and its sign if not, and so must whoever uses this.
        /// </summary>
        Expr? Narrow(Expr e)
        {
            if (ConstantFolder.Evaluate(e) != null) return null;
            Expr inner = e is CastExpr { Type.IsWide: true } cast ? cast.Operand : e;
            if (inner.Type.IsWide || inner.Type.IsFloat || IsAddress(inner) || ConstantFolder.Evaluate(inner) != null) return null;
            return inner;
        }

        /// <summary>
        /// edx:eax = the left side, then the right side added to it (or taken away, and so on) from
        /// where it is, with no register of its own: a 64-bit variable, or an unsigned 32-bit number
        /// that's an operand as it is, whose high half is nothing. False when the right side isn't
        /// one of those, or the left calls something (which could change the right before it's read).
        /// </summary>
        bool TryWideInPlace(BinaryExpr b)
        {
            if (HasCall(b.Left) || ConstantFolder.Evaluate(b.Right) != null) return false;
            var (low, high) = b.Op switch
            {
                BinaryOp.Add => ("add", "adc"), BinaryOp.Subtract => ("sub", "sbb"),
                BinaryOp.BitAnd => ("and", "and"), BinaryOp.BitOr => ("or", "or"), _ => ("xor", "xor"),
            };
            if (b.Right.Type.IsWide && IsStaticPlace(b.Right))
            {
                EmitWide(b.Left);
                Place right = EmitPlace(b.Right);
                Emit($"{low} eax, dword {Operand(right)}");
                Emit($"{high} edx, dword {Operand(right.Plus(4))}");
                return true;
            }
            if (Narrow(b.Right) is not Expr narrow || narrow.Type.IsSigned) return false;
            if (DirectOperand(narrow, constantOnly: false) == null && !PlaceOperand(narrow)) return false;
            EmitWide(b.Left);
            if (ReadWhereItIs(narrow) is not string operand)
            {
                Emit("push edx");
                Emit("push eax");
                EmitExpr(narrow);
                Emit("mov ecx, eax");
                Emit("pop eax");
                Emit("pop edx");
                operand = "ecx";
            }
            Emit($"{low} eax, {operand}");
            if (b.Op is BinaryOp.Add or BinaryOp.Subtract) Emit($"{high} edx, 0");
            else if (b.Op == BinaryOp.BitAnd) Emit("xor edx, edx");
            return true;
        }

        /// <summary>
        /// A 32-bit number written as an instruction's operand where it is, for 64-bit math that has
        /// its other side in edx:eax already: a variable (DirectOperand), or an element or field of
        /// 32 bits whose address takes no more than ebx to reach. Whatever code reaching it takes is
        /// emitted here, and must leave eax, ecx and edx alone: if it wouldn't, it's taken back and
        /// this is null, and the caller works the number out the usual way.
        /// </summary>
        string? ReadWhereItIs(Expr narrow)
        {
            if (DirectOperand(narrow, constantOnly: false) is string direct) return direct;
            if (!PlaceOperand(narrow)) return null;
            int start = code.Length;
            string operand = $"dword {Operand(EmitPlace(narrow))}";
            foreach (string line in code.ToString(start, code.Length - start).Split('\n'))
                if (!line.Contains("it stops here") && System.Text.RegularExpressions.Regex.IsMatch(WithoutComment(line), @"\b(eax|ax|al|ah|ecx|cx|cl|ch|edx|dx|dl|dh|call)\b"))
                {
                    code.Length = start;
                    return null;
                }
            return operand;
        }

        /// <summary>True for a 64-bit product that TryWideMultiply works out itself, with no call.</summary>
        bool WideMultiplyInPlace(BinaryExpr binary)
        {
            Expr? left = Narrow(binary.Left), right = Narrow(binary.Right);
            if (left != null && right != null) return left.Type.IsSigned == right.Type.IsSigned;
            if (right != null) return !right.Type.IsSigned && ConstantFolder.Evaluate(binary.Left) == null;
            if (left != null) return !left.Type.IsSigned && ConstantFolder.Evaluate(binary.Right) == null;
            return false;
        }

        /// <summary>
        /// edx:eax = the product, when it takes less than the library's 64 by 64 multiply:
        ///  - two 32-bit numbers of the same signedness: one mul (or imul), which gives all 64 bits
        ///  - a 64-bit number and an unsigned 32-bit one: low * c, and high * c added to its top half
        /// The right side is worked out first, as EmitWideOperands does. False for anything else.
        /// </summary>
        bool TryWideMultiply(BinaryExpr binary)
        {
            if (!WideMultiplyInPlace(binary)) return false;
            Expr? left = Narrow(binary.Left), right = Narrow(binary.Right);
            if (left != null && right != null && left.Type.IsSigned == right.Type.IsSigned)
            {
                string one = left.Type.IsSigned ? "imul" : "mul";
                if (!HasCall(left) && (DirectOperand(right, constantOnly: false) != null || PlaceOperand(right)))
                {
                    EmitExpr(left);
                    if (ReadWhereItIs(right) is string direct) Emit($"{one} {direct}");
                    else
                    {
                        Emit("push eax");
                        EmitExpr(right);
                        Emit("mov ecx, eax");
                        Emit("pop eax");
                        Emit($"{one} ecx");
                    }
                    return true;
                }
                EmitExpr(right);
                Emit("push eax");
                EmitExpr(left);
                Emit("pop ecx");
                Emit($"{one} ecx");
                return true;
            }
            if (right != null && !right.Type.IsSigned && left == null && ConstantFolder.Evaluate(binary.Left) == null)
            {
                // wide * c
                if (!HasCall(binary.Left) && (DirectOperand(right, constantOnly: false) != null || PlaceOperand(right)))
                {
                    EmitWide(binary.Left);
                    if (ReadWhereItIs(right) is string direct) Emit($"mov ecx, {direct}");
                    else
                    {
                        Emit("push edx");
                        Emit("push eax");
                        EmitExpr(right);
                        Emit("mov ecx, eax");
                        Emit("pop eax");
                        Emit("pop edx");
                    }
                }
                else if (IsStaticPlace(binary.Left))
                {
                    EmitExpr(right);
                    Emit("mov ecx, eax");
                    EmitWide(binary.Left);
                }
                else
                {
                    EmitExpr(right);
                    Emit("push eax");
                    EmitWide(binary.Left);
                    Emit("pop ecx");
                }
            }
            else if (left != null && !left.Type.IsSigned && right == null && ConstantFolder.Evaluate(binary.Right) == null)
            {
                // c * wide
                EmitWide(binary.Right);
                Emit("push edx");
                Emit("push eax");
                EmitExpr(left);
                Emit("mov ecx, eax");
                Emit("pop eax");
                Emit("pop edx");
            }
            else return false;
            Emit("mov ebx, edx");
            Emit("mul ecx");                 // low * c, all 64 bits of it
            Emit("imul ebx, ecx");           // high * c: only its low half is below 2^64
            Emit("add edx, ebx");
            return true;
        }

        /// <summary>Stores edx:eax in a 64-bit place.</summary>
        void EmitWideStore(Expr target)
        {
            // (a place whose address is fixed takes no code to reach, so nothing has to wait)
            if (IsStaticPlace(target))
            {
                Place there = EmitPlace(target);
                Emit($"mov dword {Operand(there)}, eax");
                Emit($"mov dword {Operand(there.Plus(4))}, edx");
                return;
            }
            Emit("push edx");
            Emit("push eax");
            Place place = EmitPlace(target);
            Emit("pop eax");
            Emit("pop edx");
            Emit($"mov dword {Operand(place)}, eax");
            Emit($"mov dword {Operand(place.Plus(4))}, edx");
        }

        /// <summary>eax = 1 when a comparison of two 64-bit values holds, 0 when it doesn't.</summary>
        void EmitWideCompare(BinaryExpr cmp)
        {
            if (WholeValue(cmp.Left) && WholeValue(cmp.Right))
            {
                bool asUnsigned = EmitWholeFlags(cmp);
                Emit($"set{WholeCondition(cmp.Op, asUnsigned, holds: true)} al");
                Emit("movzx eax, al");
                return;
            }
            bool unsigned = cmp.Left.Type.Kind == TypeKind.U64 || cmp.Right.Type.Kind == TypeKind.U64;
            EmitWideOperands(cmp);
            // what the comparison gives when the left is less, equal and greater
            var (less, equal, greater) = cmp.Op switch
            {
                BinaryOp.Equal => (0, 1, 0),
                BinaryOp.NotEqual => (1, 0, 1),
                BinaryOp.Less => (1, 0, 0),
                BinaryOp.LessEqual => (1, 1, 0),
                BinaryOp.Greater => (0, 0, 1),
                _ => (0, 1, 1),
            };
            string lessLabel = NewLabel("wless");
            string greaterLabel = NewLabel("wgreater");
            string done = NewLabel("wcompared");
            Emit("cmp edx, ebx              ; the high halves decide, unless they're equal");
            Emit($"{(unsigned ? "jb" : "jl")} {lessLabel}");
            Emit($"{(unsigned ? "ja" : "jg")} {greaterLabel}");
            Emit("cmp eax, ecx");
            Emit($"jb {lessLabel}");
            Emit($"ja {greaterLabel}");
            Emit($"mov eax, {equal}");
            Emit($"jmp {done}");
            Label(lessLabel);
            Emit($"mov eax, {less}");
            Emit($"jmp {done}");
            Label(greaterLabel);
            Emit($"mov eax, {greater}");
            Label(done);
        }

        /// <summary>eax = the address of a 64-bit value written out in decimal (in a temporary).</summary>
        void EmitWideText(Expr e)
        {
            string text = AllocTemp(24);
            EmitWide(e);
            string convert = e.Type.Kind == TypeKind.U64 ? "phi_uint64_to_str" : "phi_int64_to_str";
            Need(convert);
            LoadAddress("edi", text);
            Emit("mov ecx, 24");
            Emit($"call {convert}");
            LoadAddress("eax", text);
        }

        static BinaryOp Invert(BinaryOp op) => op switch
        {
            BinaryOp.Equal => BinaryOp.NotEqual,
            BinaryOp.NotEqual => BinaryOp.Equal,
            BinaryOp.Less => BinaryOp.GreaterEqual,
            BinaryOp.LessEqual => BinaryOp.Greater,
            BinaryOp.Greater => BinaryOp.LessEqual,
            _ => BinaryOp.Less,
        };

        // ================================================================ objects
        //
        // An object variable or field holds the address of the struct's memory (0 = empty).
        // Each object has one owner, so `is` copies values, `takes` hands memory over, and an
        // owner frees what it owns. The work is done by routines generated for each struct:
        //
        //   phi_free_S    eax = object or 0         frees it and everything it owns
        //   phi_clone_S   eax = object or 0         eax = a new copy (or 0)
        //   phi_assign_S  eax = into, edx = from    copies the values, keeping into's memory
        //   phi_set_S     eax = slot, edx = from    copies into the slot's object (a new copy if empty)
        //   phi_renew_S   eax = slot                a zeroed object in the slot (old one emptied)
        //   phi_equal_S   eax = a, edx = b          eax = 1 when the values match
        //
        // Like library routines, they may change any register but ebp. A chain through a field
        // of the struct's own type (a list's next) is followed with a loop, not recursion, so a
        // long list can't use up the stack.

        readonly HashSet<(string Kind, StructSymbol Struct)> objectRoutines = new();
        bool usesObjects;

        readonly Dictionary<string, (string Kind, PhiType Element)> arrayRoutines = new();

        /// <summary>
        /// A routine for an array a struct owns (a field without a size), by where it's kept (eax):
        /// free (its objects and its room), clone (into the slot at edx), and equal (with edx;
        /// eax = 1 when they hold the same).
        /// </summary>
        string ArrayRoutine(string kind, PhiType element)
        {
            usesObjects = true;
            string label = $"phi_array_{kind}_{(element.IsObject ? Templates.Label(element.Struct!.Name) : element.Kind == TypeKind.Str ? "text" : $"b{element.ElementSize}")}";
            arrayRoutines.TryAdd(label, (kind, element));
            return label;
        }

        /// <summary>
        /// The routines for a struct's str array without a size (eax = where it's kept, and edx the
        /// copy's or the other's): its texts after the room, then each one's room. A text in the
        /// program's data (the InData bit: an empty one) is never freed or copied.
        /// </summary>
        void EmitTextArrayRoutine(string kind)
        {
            // (in 64-bit code each text's address and each room take 8 bytes, a room in the low
            // half, and the frame's values 8 bytes each)
            string array = Pushed(1), other = Pushed(2), which = $"dword [ebp - {3 * Slot}]";
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit($"push eax                ; {array} the array");
            Emit($"push edx                ; {other} the copy, or the other");
            Emit($"push dword 0            ; {which} which element");
            switch (kind)
            {
                case "free":
                    Label(".next");
                    Emit($"mov ecx, {which}");
                    Emit($"mov {R("eax")}, {array}");
                    Emit($"cmp ecx, [eax + {ViewCount}]");
                    Emit("jae .freed");
                    Emit($"mov edx, [eax + {ViewMax}]");
                    Emit($"mov {R("eax")}, {Q} [eax]");
                    Emit($"lea edx, [eax + edx * {Word}]   ; the rooms");
                    Emit($"inc {which}");
                    Emit($"test dword [edx + ecx * {Word}], {InData}");
                    Emit("jnz .next");
                    Emit($"push {Q} [eax + ecx * {Word}]");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit($"add esp, {Slot}");
                    Emit("jmp .next");
                    Label(".freed");
                    Emit($"mov {R("eax")}, {array}");
                    Emit($"push {Q} [eax]        ; the texts and rooms (freeing 0 does nothing)");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit($"add esp, {Slot}");
                    Emit($"mov {R("eax")}, {array}");
                    Emit($"mov {Q} [eax], 0");
                    Emit($"mov dword [eax + {ViewCount}], 0");
                    Emit($"mov dword [eax + {ViewMax}], 0");
                    break;

                case "clone":
                    Need("phi_out_of_memory");
                    Need("phi_strlen");
                    Emit($"mov ecx, [eax + {ViewCount}]");
                    Emit($"mov [edx + {ViewCount}], ecx");
                    Emit($"mov ecx, [eax + {ViewMax}]");
                    Emit($"mov [edx + {ViewMax}], ecx");
                    Emit($"mov {Q} [edx], 0");
                    Emit("test ecx, ecx");
                    Emit("jz .done                ; no room: nothing to copy");
                    Emit($"imul ecx, ecx, {2 * Word}    ; a text and a room for each");
                    Emit("push ecx");
                    Emit($"call {HeapMethodLabel("Heap.Alloc")}");
                    Emit($"add esp, {Slot}");
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jz phi_out_of_memory");
                    Emit($"mov {R("edx")}, {other}");
                    Emit($"mov {Q} [edx], {R("eax")}");
                    // every slot up to the room: one in the data stays as it is, one on the heap is copied
                    Label(".next");
                    Emit($"mov ecx, {which}");
                    Emit($"mov {R("eax")}, {array}");
                    Emit($"cmp ecx, [eax + {ViewMax}]");
                    Emit("jae .done");
                    Emit($"mov edx, [eax + {ViewMax}]");
                    Emit($"mov {R("eax")}, {Q} [eax]");
                    Emit($"lea edx, [eax + edx * {Word}]   ; the rooms");
                    Emit($"mov {R("eax")}, {Q} [eax + ecx * {Word}]   ; the text");
                    Emit($"mov edx, [edx + ecx * {Word}]   ; its room");
                    Emit($"test edx, {InData}");
                    Emit("jnz .keep");
                    Emit($"call {ObjectRoutine("clone", StructSymbol.Text)}");
                    Emit("push eax");
                    Emit($"mov {R("esi")}, {R("eax")}");
                    Emit("call phi_strlen");
                    Emit("lea edx, [eax + 1]      ; the copy's room: its text and zero");
                    Emit("pop eax");
                    Label(".keep");
                    Emit($"mov {R("esi")}, {other}");
                    Emit($"mov ecx, [esi + {ViewMax}]");
                    Emit($"mov {R("esi")}, {Q} [esi]");
                    Emit($"lea ecx, [esi + ecx * {Word}]");
                    Emit($"mov ebx, {which}");
                    Emit($"mov {Q} [esi + ebx * {Word}], {R("eax")}");
                    Emit($"mov [ecx + ebx * {Word}], edx");
                    Emit($"inc {which}");
                    Emit("jmp .next");
                    Label(".done");
                    break;

                case "equal":
                    Emit($"mov ecx, [eax + {ViewCount}]");
                    Emit($"cmp ecx, [edx + {ViewCount}]");
                    Emit("jne .different          ; not as many");
                    Label(".next");
                    Emit($"mov ecx, {which}");
                    Emit($"mov {R("eax")}, {array}");
                    Emit($"cmp ecx, [eax + {ViewCount}]");
                    Emit("jae .same");
                    Emit($"mov {R("eax")}, {Q} [eax]");
                    Emit($"mov {R("eax")}, {Q} [eax + ecx * {Word}]");
                    Emit($"mov {R("edx")}, {other}");
                    Emit($"mov {R("edx")}, {Q} [edx]");
                    Emit($"mov {R("edx")}, {Q} [edx + ecx * {Word}]");
                    Emit($"call {ObjectRoutine("equal", StructSymbol.Text)}");
                    Emit("test eax, eax");
                    Emit("jz .different");
                    Emit($"inc {which}");
                    Emit("jmp .next");
                    Label(".same");
                    Emit("mov eax, 1");
                    Emit("jmp .done");
                    Label(".different");
                    Emit("xor eax, eax");
                    Label(".done");
                    break;
            }
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitArrayRoutine(string kind, PhiType element)
        {
            if (element.Kind == TypeKind.Str)
            {
                EmitTextArrayRoutine(kind);
                return;
            }
            // the view is where its elements are, how many, its room (see ViewCount and ViewMax);
            // the frame's values are 8 bytes each in 64-bit code (Pushed)
            int size = element.ElementSize;
            bool objects = element.IsObject;
            string array = Pushed(1), other = Pushed(2);
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit($"push eax                ; {array} the array");
            switch (kind)
            {
                case "free":
                {
                    if (objects)
                    {
                        string which = $"dword [ebp - {2 * Slot}]";
                        Emit($"push dword 0            ; {which} which element");
                        Label(".next");
                        Emit($"mov eax, {which}");
                        Emit($"mov {R("edx")}, {array}");
                        Emit($"cmp eax, [edx + {ViewCount}]");
                        Emit("jae .freed");
                        Emit($"mov {R("edx")}, {Q} [edx]");
                        Emit($"mov {R("eax")}, {Q} [edx + eax * {Word}]");
                        Emit($"call {ObjectRoutine("free", element.Struct!)}");
                        Emit($"inc {which}");
                        Emit("jmp .next");
                        Label(".freed");
                    }
                    Emit($"mov {R("eax")}, {array}");
                    Emit($"push {Q} [eax]        ; its room (freeing 0 does nothing)");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit($"add esp, {Slot}");
                    Emit($"mov {R("eax")}, {array}");
                    Emit($"mov {Q} [eax], 0");
                    Emit($"mov dword [eax + {ViewCount}], 0");
                    Emit($"mov dword [eax + {ViewMax}], 0");
                    break;
                }

                case "clone":
                {
                    Need("phi_out_of_memory");
                    Emit($"push edx                ; {other} the copy");
                    Emit($"mov ecx, [eax + {ViewCount}]");
                    Emit($"mov [edx + {ViewCount}], ecx");
                    Emit($"mov ecx, [eax + {ViewMax}]");
                    Emit($"mov [edx + {ViewMax}], ecx");
                    Emit($"mov {Q} [edx], 0");
                    Emit("test ecx, ecx");
                    Emit("jz .done                ; no room: nothing to copy");
                    if (size > 1) Emit($"imul ecx, ecx, {size}");
                    Emit("push ecx");
                    Emit($"call {HeapMethodLabel("Heap.Alloc")}");
                    Emit($"add esp, {Slot}");
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jz phi_out_of_memory");
                    Emit($"mov {R("edx")}, {other}");
                    Emit($"mov {Q} [edx], {R("eax")}");
                    Emit($"mov {R("edi")}, {R("eax")}");
                    Emit($"mov {R("esi")}, {array}");
                    Emit($"mov ecx, [esi + {ViewCount}]");
                    Emit($"mov {R("esi")}, {Q} [esi]");
                    if (size > 1) Emit($"imul ecx, ecx, {size}");
                    Emit("rep movsb");
                    if (objects)
                    {
                        // each object copied too, so the copy has its own
                        string which = $"dword [ebp - {3 * Slot}]";
                        Emit($"push dword 0            ; {which} which element");
                        Label(".next");
                        Emit($"mov ecx, {which}");
                        Emit($"mov {R("edx")}, {other}");
                        Emit($"cmp ecx, [edx + {ViewCount}]");
                        Emit("jae .done");
                        Emit($"mov {R("edx")}, {Q} [edx]");
                        Emit($"mov {R("eax")}, {Q} [edx + ecx * {Word}]");
                        Emit($"call {ObjectRoutine("clone", element.Struct!)}");
                        Emit($"mov {R("edx")}, {other}");
                        Emit($"mov {R("edx")}, {Q} [edx]");
                        Emit($"mov ecx, {which}");
                        Emit($"mov {Q} [edx + ecx * {Word}], {R("eax")}");
                        Emit($"inc {which}");
                        Emit("jmp .next");
                    }
                    Label(".done");
                    break;
                }

                case "equal":
                {
                    Emit($"push edx                ; {other} the other");
                    Emit($"mov ecx, [eax + {ViewCount}]");
                    Emit($"cmp ecx, [edx + {ViewCount}]");
                    Emit("jne .different          ; not as many");
                    if (objects)
                    {
                        string which = $"dword [ebp - {3 * Slot}]";
                        Emit($"push dword 0            ; {which} which element");
                        Label(".next");
                        Emit($"mov ecx, {which}");
                        Emit($"mov {R("eax")}, {array}");
                        Emit($"cmp ecx, [eax + {ViewCount}]");
                        Emit("jae .same");
                        Emit($"mov {R("eax")}, {Q} [eax]");
                        Emit($"mov {R("eax")}, {Q} [eax + ecx * {Word}]");
                        Emit($"mov {R("edx")}, {other}");
                        Emit($"mov {R("edx")}, {Q} [edx]");
                        Emit($"mov {R("edx")}, {Q} [edx + ecx * {Word}]");
                        Emit($"call {ObjectRoutine("equal", element.Struct!)}");
                        Emit("test eax, eax");
                        Emit("jz .different");
                        Emit($"inc {which}");
                        Emit("jmp .next");
                    }
                    else
                    {
                        if (size > 1) Emit($"imul ecx, ecx, {size}");
                        Emit($"mov {R("esi")}, {Q} [eax]");
                        Emit($"mov {R("edi")}, {Q} [edx]");
                        Emit("test ecx, ecx");
                        Emit("jz .same");
                        Emit("repe cmpsb");
                        Emit("jne .different");
                    }
                    Label(".same");
                    Emit("mov eax, 1");
                    Emit("jmp .done");
                    Label(".different");
                    Emit("xor eax, eax");
                    Label(".done");
                    break;
                }
            }
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        string ObjectRoutine(string kind, StructSymbol s)
        {
            usesObjects = true;
            objectRoutines.Add((kind, s));
            return $"phi_{kind}_{Templates.Label(s.Name)}";
        }

        /// <summary>Remembers the source line, for the message if the heap runs out.</summary>
        void ObjectLine(Node node)
        {
            usesObjects = true;
            Emit($"mov dword [phi_object_line], {LineOf(node)}");
        }

        /// <summary>eax = where an object's values are: what an object holds, or a struct's own place.</summary>
        void EmitObjectAddress(Expr e)
        {
            if (e.Type.IsObject) EmitExpr(e);
            else EmitPlaceAddress(e);
        }

        void EmitNewDecl(VarDecl decl, VariableSymbol v)
        {
            if (decl.Narrows != null)
            {
                // the object an interface value holds, which an if or when found to be this struct
                EmitExpr(decl.Narrows);
                EmitStoreEax(NameOf(v));
                return;
            }

            if (decl.AtAddress != null)
            {
                // a view of memory that's already there
                EmitExpr(decl.AtAddress);
                EmitStoreEax(NameOf(v));
                return;
            }

            if (decl.Source != null)
            {
                EmitObjectAssign(decl.Source);
                return;
            }

            // a zeroed object (the first time; after that the old one is emptied and reused),
            // then its fields from the values, in order
            ObjectLine(decl);
            LoadAddress("eax", v.Label);
            Emit($"call {ObjectRoutine("renew", v.Type.Struct!)}");
            foreach (Stmt field in decl.FieldValues) EmitStatement(field);
        }

        // ================================================================ whole arrays

        /// <summary>eax = how many elements an array has: its length, or an atmost one's count.</summary>
        void EmitArrayCount(Expr array)
        {
            if (array is NameExpr { Symbol: VariableSymbol { Type.Open: true } open })
                Emit($"mov eax, [{open.Label} + {ViewCount}]");
            else if (array.Type.Open)
            {
                EmitViewAddress(array);
                Emit($"mov eax, [eax + {ViewCount}]      ; how many it has");
            }
            else if (array.Type.Filled) EmitFilledCount(array);
            else Emit($"mov eax, {array.Type.Length}");
        }

        /// <summary>b is a: every element copied (objects and strs too); an atmost b takes a's count.</summary>
        void EmitArrayAssign(ArrayAssignStmt copy)
        {
            PhiType t = copy.Target.Type;
            PhiType element = t.Element;
            int size = element.ElementSize;

            // an array copied onto itself is already what it would become (and a str array's
            // texts mustn't be let go while they're copied)
            if (copy.Target is NameExpr { Symbol: VariableSymbol into } && copy.Source is NameExpr { Symbol: VariableSymbol from } && into == from)
                return;

            if (copy.Overlaps && GrowingView(copy.Target) && GrowingView(copy.Source))
            {
                // one may be inside the other (tree.kids:0.kids is tree.kids, or the other way
                // round): the whole copy is made first, then the target lets go of what it has
                // and the copy's elements and room are its own
                ObjectLine(copy);
                string made = AllocTemp(ViewMax + 4);
                EmitViewAddress(copy.Source);
                Emit($"lea {R("edx")}, [{made}]");
                Emit($"call {ArrayRoutine("clone", element)}");
                EmitViewAddress(copy.Target);
                Emit("push eax");
                Emit($"call {ArrayRoutine("free", element)}");
                Emit("pop eax");
                for (int i = 0; i < ViewMax + 4; i += 4)
                {
                    Emit($"mov ecx, [{made} + {i}]");
                    Emit($"mov [eax + {i}], ecx");
                }
                return;
            }

            if (copy.Overlaps && element.IsObject)
            {
                EmitOverlappingObjects(copy);
                return;
            }

            if (element.IsObject)
            {
                // an atmost target first lets go of the objects past the source's count
                EmitArrayCount(copy.Source);
                EmitCopyFits(copy);
                if (t.Filled)
                {
                    Emit("push eax");
                    EmitFreeElements(copy.Target, -1);
                    EmitPlaceAddress(copy.Target);
                    Emit("pop ecx");
                    Emit($"mov [eax + {t.CountOffset}], ecx");
                    Emit("mov eax, ecx");
                }
                EmitEachElement(() =>
                {
                    EmitPlaceAddress(copy.Source);
                    Emit("mov ecx, [esp]");
                    Emit($"mov {R("edx")}, {Q} [eax + ecx * {Word}]");
                    Emit("push edx                ; the source's object (or 0)");
                    EmitPlaceAddress(copy.Target);
                    Emit($"mov ecx, [esp + {Slot}]");
                    Emit($"lea eax, [eax + ecx * {Word}]");
                    Emit("pop edx");
                    Emit($"call {ObjectRoutine("set", element.Struct!)}");
                });
                return;
            }

            if (element.Kind == TypeKind.Str && Binder.IsFieldView(copy.Target))
            {
                // into a struct's str array without a size: each text grows to fit
                EmitArrayCount(copy.Source);
                EmitCopyFits(copy);
                string rooms = RoomsOf(copy.Target);
                EmitArrayCount(copy.Target);
                EmitEachElement(() =>
                {
                    EmitPlaceAddress(copy.Source);
                    Emit("mov ecx, [esp]");
                    Emit($"mov {R("edx")}, {Q} [eax + ecx * {Word}]");
                    Emit("push edx                ; its text");
                    EmitPlaceAddress(copy.Target);
                    Emit($"mov ecx, [esp + {Slot}]");
                    Emit($"lea eax, [eax + ecx * {Word}]");
                    Emit("push eax                ; the element's slot");
                    EmitGrowStoreAt(0, sourceIsString: true, append: false, unsigned: false, rooms: rooms);
                });
                return;
            }

            if (element.Kind == TypeKind.Str)
            {
                var array = (VariableSymbol)((NameExpr)copy.Target).Symbol!;
                EmitArrayCount(copy.Source);
                EmitCopyFits(copy);
                if (t.Filled)
                {
                    // an atmost target has as many as the source now
                    Emit("push eax");
                    EmitPlaceAddress(copy.Target);
                    Emit("pop ecx");
                    Emit($"mov [eax + {t.CountOffset}], ecx");
                    Emit("mov eax, ecx");
                }
                EmitEachElement(() =>
                {
                    EmitPlaceAddress(copy.Source);
                    Emit("mov ecx, [esp]");
                    Emit($"mov {R("edx")}, {Q} [eax + ecx * {Word}]");
                    Emit("push edx                ; its text");
                    EmitPlaceAddress(copy.Target);
                    Emit($"mov ecx, [esp + {Slot}]");
                    Emit($"lea eax, [eax + ecx * {Word}]");
                    if (array.GrowingArray || array.Type.Open)
                    {
                        // its room is .max slots on (a str rows[] parameter's, the caller's)
                        Emit("push eax                ; the element's slot");
                        EmitGrowStoreAt(0, sourceIsString: true, append: false, unsigned: false, rooms: $"dword [{array.Label} + {ViewMax}]");
                    }
                    else if (array.GrowingElements)
                    {
                        Emit("push eax                ; the element's slot");
                        EmitGrowStoreAt(Word * array.Count, sourceIsString: true, append: false, unsigned: false);
                    }
                    else
                    {
                        Need("phi_strcpy");
                        Emit($"mov {R("edi")}, {Q} [eax]");
                        Emit("pop esi");
                        Emit($"mov ecx, {array.Capacity}");
                        Emit("call phi_strcpy");
                        EmitTextFits();
                    }
                });
                return;
            }

            // anything else is its bytes
            EmitArrayCount(copy.Source);
            EmitCopyFits(copy);
            Emit("push eax                ; how many");
            EmitPlaceAddress(copy.Source);
            Emit("push eax");
            EmitPlaceAddress(copy.Target);
            Emit($"mov {R("edi")}, {R("eax")}");
            Emit("pop esi");
            Emit("pop ecx");
            if (t.Filled) Emit($"mov [edi + {t.CountOffset}], ecx");
            if (size != 1) Emit($"imul ecx, ecx, {size}");
            Emit("cld");
            Emit("rep movsb");
        }

        /// <summary>
        /// b is a; for arrays of objects where one may be inside the other, and they aren't both
        /// arrays without a size (t.pair:1.pair is t.pair): a copy of each of the source's objects
        /// is made first, the last one first, onto the stack; then the target takes them in order
        /// and lets go of what it had.
        /// </summary>
        void EmitOverlappingObjects(ArrayAssignStmt copy)
        {
            PhiType t = copy.Target.Type;
            StructSymbol of = t.Element.Struct!;
            bool grows = copy.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true } } || Binder.IsFieldView(copy.Target);
            string count = AllocTemp(4), which = AllocTemp(4);
            string next = NewLabel("copying"), made = NewLabel("copied"), put = NewLabel("putting"), done = NewLabel("put");

            ObjectLine(copy);
            EmitArrayCount(copy.Source);
            if (!grows) EmitCopyFits(copy);     // (only a check: nothing is copied if they don't fit)
            Emit($"mov [{count}], eax");
            Emit($"mov [{which}], eax");
            Label(next);
            Emit($"dec dword [{which}]");
            Emit($"js {made}");
            EmitPlaceAddress(copy.Source);
            Emit($"mov ecx, [{which}]");
            Emit($"mov {R("eax")}, {Q} [eax + ecx * {Word}]");
            Emit($"call {ObjectRoutine("clone", of)}");
            Emit("push eax                ; a copy of the source's object (or 0)");
            Emit($"jmp {next}");
            Label(made);

            Emit($"mov eax, [{count}]");
            if (grows) EmitCopyFits(copy);
            if (t.Filled)
            {
                // an atmost target lets go of the objects past the source's count, and has that many
                Emit("push eax");
                EmitFreeElements(copy.Target, -1);
                EmitPlaceAddress(copy.Target);
                Emit("pop ecx");
                Emit($"mov [eax + {t.CountOffset}], ecx");
            }
            Emit($"mov dword [{which}], 0");
            Label(put);
            Emit($"mov ecx, [{which}]");
            Emit($"cmp ecx, [{count}]");
            Emit($"jae {done}");
            EmitPlaceAddress(copy.Target);
            Emit($"mov ecx, [{which}]");
            Emit($"lea eax, [eax + ecx * {Word}]");
            Emit("pop edx");
            Emit($"xchg {R("edx")}, {Q} [eax]         ; what it had, freed");
            Emit($"mov {R("eax")}, {R("edx")}");
            Emit($"call {ObjectRoutine("free", of)}");
            Emit($"inc dword [{which}]");
            Emit($"jmp {put}");
            Label(done);
        }

        /// <summary>An array without a size that owns its room: a struct's, or a variable's (not a parameter, which is the caller's).</summary>
        static bool GrowingView(Expr e) =>
            e.Type.Open && (Binder.IsFieldView(e) || e is NameExpr { Symbol: VariableSymbol { GrowingArray: true, Type.TextTable: false } });

        /// <summary>
        /// eax = how many the source has: when either array is a parameter (its length known only
        /// when it runs), an index error unless they fit as the binder checks for the others.
        /// </summary>
        void EmitCopyFits(ArrayAssignStmt copy)
        {
            PhiType t = copy.Target.Type;
            if (copy.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true, Type.TextTable: true } texts })
            {
                // a str array: the texts past the source's count go back, then it grows to fit
                ObjectLine(copy);
                Emit("push eax");
                EmitFreeTexts(texts, "dword [esp]", empty: true);
                Emit("mov edx, [esp]");
                LoadAddress("eax", texts.Label);
                EmitTextArrayGrowCall();
                Emit("pop eax");
                Emit($"mov [{texts.Label} + {ViewCount}], eax");
                return;
            }
            if (copy.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true } growing })
            {
                // it grows to fit, and has that many (letting go of objects past them)
                ObjectLine(copy);
                Emit("push eax");
                Emit("mov edx, eax");
                LoadAddress("eax", growing.Label);
                Emit($"mov ecx, {t.Element.ElementSize}");
                EmitArrayGrowCall();
                Emit("mov eax, [esp]");
                if (t.IsObjectArray) EmitFreeElements(copy.Target, -1);
                Emit("pop eax");
                Emit($"mov [{growing.Label} + {ViewCount}], eax");
                return;
            }
            if (Binder.IsFieldView(copy.Target))
            {
                // a struct's array without a size grows to fit too (a str one first gives back
                // the texts past the source's count)
                ObjectLine(copy);
                Emit("push eax                ; how many it gets");
                if (t.TextTable) EmitFreeTexts(ViewCopy(copy.Target), "it", "dword [esp]", empty: true);
                EmitViewAddress(copy.Target);
                Emit("mov edx, [esp]");
                Emit($"mov ecx, {t.Element.ElementSize}");
                if (t.TextTable) EmitTextArrayGrowCall();
                else EmitArrayGrowCall();
                Emit("mov eax, [esp]");
                if (t.IsObjectArray) EmitFreeElements(copy.Target, -1);
                EmitViewAddress(copy.Target);
                Emit("pop ecx");
                Emit($"mov [eax + {ViewCount}], ecx");
                Emit("mov eax, ecx");
                return;
            }
            if (!t.Open && !copy.Source.Type.Open) return;
            Need("phi_index_error");
            string ok = NewLabel("fits");
            if (t.Open)
                Emit($"cmp eax, [{((VariableSymbol)((NameExpr)copy.Target).Symbol!).Label} + {ViewCount}]");
            else Emit($"cmp eax, {t.Length}");
            Emit($"{(t.Filled ? "jbe" : "je")} {ok}");
            EmitIndexError(copy);
            Label(ok);
        }

        bool usesArrayGrow;

        /// <summary>
        /// eax = an array that grows (where its elements are, how many, its room), edx = how many
        /// it needs room for, ecx = the bytes of one; after, it has room for them.
        /// </summary>
        void EmitArrayGrowCall()
        {
            usesArrayGrow = true;
            Need("phi_out_of_memory");
            Emit("call phi_array_grow");
        }

        /// <summary>An array that grows, empty again: its objects and its room let go.</summary>
        void EmitGrowingArrayEmpty(VariableSymbol v)
        {
            EmitGrowingArrayFree(v);
            Emit($"mov {Q} [{v.Label}], 0");
            Emit($"mov dword [{v.Label} + {ViewCount}], 0");
            Emit($"mov dword [{v.Label} + {ViewMax}], 0");
        }

        void EmitGrowingArrayFree(VariableSymbol v)
        {
            if (v.Type.IsObjectArray) EmitFreeElements(NameOf(v), 0);
            if (v.Type.TextTable) EmitFreeTexts(v, "0", empty: false);
            Emit($"push {Q} [{v.Label}]   ; {v.Name}'s elements");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit($"add esp, {Slot}");
        }

        /// <summary>
        /// The texts of a str array that grows, from element `from` (an operand) to how many it
        /// has: each one on the heap goes back (one still empty, in the program's data, stays),
        /// and with `empty` its slot is empty again, as a new one starts.
        /// </summary>
        void EmitFreeTexts(VariableSymbol v, string from, bool empty) => EmitFreeTexts(v.Label, v.Name, from, empty);

        /// <summary>The same for the view at label (an array's own, or a copy of a field's).</summary>
        void EmitFreeTexts(string label, string name, string from, bool empty)
        {
            string next = NewLabel("freetext"), kept = NewLabel("keptext"), done = NewLabel("freedtexts");
            Emit($"mov eax, {from}");
            Emit("push eax                ; which element");
            Label(next);
            // (each address and each room is 8 bytes in 64-bit code)
            Emit("mov eax, [esp]");
            Emit($"cmp eax, [{label} + {ViewCount}]   ; how many {name} has");
            Emit($"jae {done}");
            Emit($"mov {R("ecx")}, {Q} [{label}]");
            Emit($"mov edx, [{label} + {ViewMax}]");
            Emit($"lea edx, [ecx + edx * {Word}]   ; the rooms, after the texts");
            Emit($"test dword [edx + eax * {Word}], {InData}");
            Emit($"jnz {kept}");
            Emit("push edx");
            Emit("push ecx");
            Emit($"push {Q} [ecx + eax * {Word}]   ; an element's text");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit($"add esp, {Slot}");
            Emit("pop ecx");
            Emit("pop edx");
            Emit("mov eax, [esp]");
            Label(kept);
            if (empty)
            {
                if (bits64)
                {
                    LoadAddress("ebx", StringLabel(""));
                    Emit("mov [rcx + rax * 8], rbx");
                }
                else Emit($"mov dword [ecx + eax * 4], {StringLabel("")}");
                Emit($"mov dword [edx + eax * {Word}], {InData}");
            }
            Emit("inc dword [esp]");
            Emit($"jmp {next}");
            Label(done);
            Emit($"add esp, {Slot}");
        }

        bool usesTextArrayGrow;

        void EmitTextArrayGrowCall()
        {
            usesTextArrayGrow = true;
            Need("phi_out_of_memory");
            Emit("call phi_text_array_grow");
        }

        /// <summary>
        /// phi_text_array_grow, for str arrays without a most: like phi_array_grow, but the
        /// array holds each element's text address and then each one's room, so both move,
        /// and a new element starts as the empty text in the program's data (never freed).
        /// </summary>
        void EmitTextArrayGrowRoutine()
        {
            if (!usesTextArrayGrow) return;
            Line("");
            Comment("room in a str array that grows: eax = the array, edx = how many");
            Label("phi_text_array_grow");
            // (each address and each room is 8 bytes in 64-bit code: stosq and movsq)
            string each = bits64 ? "q" : "d";
            Emit($"cmp edx, [eax + {ViewMax}]");
            Emit("jbe .fits");
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit($"push eax                ; {Pushed(1)} the array");
            Emit($"mov ebx, [eax + {ViewMax}]");
            Emit("add ebx, ebx");
            Emit("cmp ebx, edx");
            Emit("jae .twice");
            Emit("mov ebx, edx");
            Label(".twice");
            Emit("cmp ebx, 4");
            Emit("jae .room");
            Emit("mov ebx, 4");
            Label(".room");
            Emit($"push ebx                ; {Pushed(2)} the new room");
            Emit($"imul eax, ebx, {2 * Word}");
            Emit("push eax");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit($"add esp, {Slot}");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jz phi_out_of_memory");
            Emit($"push eax                ; {Pushed(3)} where they go now");
            // every slot empty to begin with, then the ones it has move over
            string room = $"dword [ebp - {2 * Slot}]";
            Emit("cld");
            Emit($"mov {R("edi")}, {R("eax")}");
            Emit($"mov ecx, {room}");
            LoadAddress("eax", StringLabel(""));
            Emit($"rep stos{each}");
            Emit($"mov ecx, {room}");
            Emit($"mov eax, {InData}");
            Emit($"rep stos{each}");
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"mov {R("esi")}, {Q} [edx]");
            Emit($"test {R("esi")}, {R("esi")}");
            Emit("jz .moved");
            Emit($"mov {R("edi")}, {Pushed(3)}");
            Emit($"mov ecx, [edx + {ViewCount}]");
            Emit($"rep movs{each}               ; their texts' addresses");
            Emit($"mov {R("esi")}, {Q} [edx]");
            Emit($"mov ecx, [edx + {ViewMax}]");
            Emit($"lea esi, [esi + ecx * {Word}]");
            Emit($"mov {R("edi")}, {Pushed(3)}");
            Emit($"mov ecx, {room}");
            Emit($"lea edi, [edi + ecx * {Word}]");
            Emit($"mov ecx, [edx + {ViewCount}]");
            Emit($"rep movs{each}               ; and their rooms");
            Emit($"push {Q} [edx]");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit($"add esp, {Slot}");
            Label(".moved");
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"mov {R("eax")}, {Pushed(3)}");
            Emit($"mov {Q} [edx], {R("eax")}");
            Emit($"mov eax, {room}");
            Emit($"mov [edx + {ViewMax}], eax");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Label(".fits");
            Emit("ret");
        }

        /// <summary>phi_array_grow, for arrays without a size (see EmitArrayGrowCall).</summary>
        void EmitArrayGrowRoutine()
        {
            if (!usesArrayGrow) return;
            Line("");
            Comment("room in an array that grows: eax = the array, edx = how many, ecx = bytes each");
            Label("phi_array_grow");
            Emit($"cmp edx, [eax + {ViewMax}]");
            Emit("jbe .fits");
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit($"push eax                ; {Pushed(1)} the array");
            Emit($"push ecx                ; {Pushed(2)} bytes each");
            // the new room: twice the old, at least what's needed, and at least 4
            Emit($"mov ebx, [eax + {ViewMax}]");
            Emit("add ebx, ebx");
            Emit("cmp ebx, edx");
            Emit("jae .twice");
            Emit("mov ebx, edx");
            Label(".twice");
            Emit("cmp ebx, 4");
            Emit("jae .room");
            Emit("mov ebx, 4");
            Label(".room");
            Emit($"push ebx                ; {Pushed(3)} the new room");
            Emit("mov eax, ebx");
            Emit($"mul dword [ebp - {2 * Slot}]");
            Emit("push eax");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit($"add esp, {Slot}");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jz phi_out_of_memory");
            Emit($"push eax                ; {Pushed(4)} where the elements go now");
            // the elements so far move over (their objects, if any, go with them)
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"mov {R("esi")}, {Q} [edx]");
            Emit($"mov {R("edi")}, {R("eax")}");
            Emit($"mov ecx, [edx + {ViewCount}]");
            Emit($"imul ecx, [ebp - {2 * Slot}]");
            Emit("cld");
            Emit("rep movsb");
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"push {Q} [edx]");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit($"add esp, {Slot}");
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"mov {R("eax")}, {Pushed(4)}");
            Emit($"mov {Q} [edx], {R("eax")}");
            Emit($"mov eax, [ebp - {3 * Slot}]");
            Emit($"mov [edx + {ViewMax}], eax");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Label(".fits");
            Emit("ret");
        }

        /// <summary>
        /// Runs `body` for each element, 0 to eax - 1: the element's number is in dword [esp] when
        /// it starts, and it leaves the stack as it found it.
        /// </summary>
        void EmitEachElement(Action body)
        {
            string next = NewLabel("each");
            string done = NewLabel("eachdone");
            Emit("push eax                ; how many");
            Emit("push dword 0            ; which one");
            Label(next);
            Emit("mov eax, [esp]");
            Emit($"cmp eax, [esp + {Slot}]");
            Emit($"jae {done}");
            body();
            Emit("inc dword [esp]");
            Emit($"jmp {next}");
            Label(done);
            Emit($"add esp, {2 * Slot}");
        }

        /// <summary>eax = 1 when two whole arrays have the same number of elements, all equal.</summary>
        void EmitArrayCompare(BinaryExpr cmp)
        {
            PhiType element = cmp.Left.Type.Element;
            string differ = NewLabel("differ");
            string done = NewLabel("compared");

            EmitArrayCount(cmp.Left);
            Emit("push eax");
            EmitArrayCount(cmp.Right);
            Emit("pop ecx");
            Emit("cmp eax, ecx");
            Emit($"jne {differ}             ; different lengths");

            if (element.IsObject || element.Kind == TypeKind.Str)
            {
                string unequal = NewLabel("unequal");
                EmitEachElement(() =>
                {
                    EmitPlaceAddress(cmp.Right);
                    Emit("mov ecx, [esp]");
                    Emit($"mov {R("edx")}, {Q} [eax + ecx * {Word}]");
                    Emit("push edx");
                    EmitPlaceAddress(cmp.Left);
                    Emit($"mov ecx, [esp + {Slot}]");
                    Emit($"mov {R("eax")}, {Q} [eax + ecx * {Word}]");
                    Emit("pop edx");
                    if (element.IsObject)
                    {
                        Emit($"call {ObjectRoutine("equal", element.Struct!)}");
                        Emit("test eax, eax");
                        Emit($"jz {unequal}");
                    }
                    else
                    {
                        Need("phi_strcmp");
                        Emit($"mov {R("esi")}, {R("eax")}");
                        Emit($"mov {R("edi")}, {R("edx")}");
                        Emit("call phi_strcmp          ; eax = 0 when equal");
                        Emit("test eax, eax");
                        Emit($"jnz {unequal}");
                    }
                });
                Emit("mov eax, 1");
                Emit($"jmp {done}");
                Label(unequal);
                Emit($"add esp, {2 * Slot}              ; the loop's count and number");
                Emit($"jmp {differ}");
            }
            else
            {
                if (element.ElementSize != 1) Emit($"imul eax, eax, {element.ElementSize}");
                Emit("push eax                ; bytes");
                EmitPlaceAddress(cmp.Right);
                Emit("push eax");
                EmitPlaceAddress(cmp.Left);
                Emit($"mov {R("esi")}, {R("eax")}");
                Emit("pop edi");
                Emit("pop ecx");
                Emit("cld");
                Emit("xor eax, eax            ; equal so far (ZF set), even for none");
                Emit("repe cmpsb");
                Emit("sete al");
                Emit($"jmp {done}");
            }
            Label(differ);
            Emit("xor eax, eax");
            Label(done);
        }

        /// <summary>
        /// new T list[N]: N zeroed objects (each one emptied and used again when the declaration
        /// runs again). new T list[atmost N]: none, freeing any from before.
        /// </summary>
        void EmitNewArray(VarDecl decl, VariableSymbol v)
        {
            ObjectLine(decl);
            StructSymbol s = v.Type.Struct!;
            if (v.Type.Filled)
            {
                EmitFreeElements(NameOf(v), 0);
                LoadAddress("eax", v.Label);
                Emit($"mov dword [eax + {v.Type.CountOffset}], 0   ; none in it");
                return;
            }
            string next = NewLabel("element");
            Emit("push dword 0            ; which element");
            Label(next);
            LoadAddress("eax", v.Label);
            Emit("mov edx, [esp]");
            Emit($"lea eax, [eax + edx * {Word}]");
            Emit($"call {ObjectRoutine("renew", s)}");
            Emit("inc dword [esp]");
            Emit($"cmp dword [esp], {v.Type.Length}");
            Emit($"jb {next}");
            Emit($"add esp, {Slot}");
        }

        /// <summary>
        /// Frees the objects of an array of objects from element `from` on (each slot becomes empty);
        /// for an atmost array, from a number in eax instead when from is -1.
        /// </summary>
        void EmitFreeElements(Expr array, int from)
        {
            StructSymbol s = array.Type.Struct!;
            string next = NewLabel("free");
            string done = NewLabel("freed");
            if (from >= 0) Emit($"push dword {from}");
            else Emit("push eax");
            Label(next);
            if (array.Type.Open)
            {
                // past how many it has, up to its room, the slots are empty already
                EmitViewAddress(array);
                Emit($"mov eax, [eax + {ViewMax}]");
                Emit("cmp [esp], eax");
            }
            else Emit($"cmp dword [esp], {array.Type.Length}");
            Emit($"jae {done}");
            EmitPlaceAddress(array);
            Emit("mov edx, [esp]");
            Emit($"lea edx, [eax + edx * {Word}]");
            Emit($"mov {R("eax")}, {Q} [edx]");
            Emit($"mov {Q} [edx], 0       ; empty");
            Emit($"call {ObjectRoutine("free", s)}");
            Emit("inc dword [esp]");
            Emit($"jmp {next}");
            Label(done);
            Emit($"add esp, {Slot}");
        }

        void EmitObjectAssign(ObjectAssignStmt o)
        {
            StructSymbol s = o.Struct;
            switch (o.Kind)
            {
                case ObjectAssignKind.Clear:
                    EmitPlaceAddress(o.Target);
                    Emit($"mov {R("edx")}, {R("eax")}");
                    Emit($"mov {R("eax")}, {Q} [edx]");
                    Emit($"mov {Q} [edx], 0       ; empty");
                    Emit($"call {ObjectRoutine("free", s)}");
                    break;

                case ObjectAssignKind.Copy:
                    ObjectLine(o);
                    EmitObjectAddress(o.Value);
                    Emit("push eax");
                    if (o.TargetOwned)
                    {
                        EmitPlaceAddress(o.Target);
                        Emit("pop edx");
                        Emit($"call {ObjectRoutine("set", s)}");
                    }
                    else
                    {
                        EmitObjectAddress(o.Target);
                        Emit("pop edx");
                        Emit($"call {ObjectRoutine("assign", s)}");
                    }
                    break;

                case ObjectAssignKind.Move:
                    ObjectLine(o);
                    EmitExpr(o.Value); // the call's new object
                    Emit("push eax");
                    EmitTakeOver(o.Target, s);
                    break;
            }
        }

        /// <summary>
        /// A new object (on the stack) goes to the target: an owner takes it and frees what it
        /// had; anything else gets a copy of its values, and the new object is freed.
        /// </summary>
        void EmitTakeOver(Expr target, StructSymbol s)
        {
            if (Binder.IsOwnedPlace(target))
            {
                EmitPlaceAddress(target);
                Emit("pop ecx");
                Emit($"mov {R("edx")}, {Q} [eax]          ; what it had");
                Emit($"mov {Q} [eax], {R("ecx")}");
                Emit($"mov {R("eax")}, {R("edx")}");
                Emit($"call {ObjectRoutine("free", s)}");
                return;
            }

            EmitObjectAddress(target);
            Emit($"mov {R("edx")}, {Q} [esp]");
            Emit($"call {ObjectRoutine("assign", s)}");
            Emit("pop eax");
            Emit($"call {ObjectRoutine("free", s)}");
        }

        void EmitTakes(TakesStmt t)
        {
            EmitPlaceAddress(t.Source);
            Emit("push eax");
            if (bits32 && t.Inside != null)
            {
                // nodes:a.left takes nodes:b; when a is b, it would own itself
                string apart = NewLabel("apart");
                EmitPlaceAddress(t.Inside);
                Emit($"cmp {R("eax")}, {Q} [esp]");
                Emit($"jne {apart}");
                EmitStop("an object can't take the object it's inside of (it would own itself)", t, ErrorHierarchy);
                Label(apart);
            }
            EmitPlaceAddress(t.Target);
            Emit("pop edx");
            Emit($"mov {R("ecx")}, {Q} [edx]          ; the object handed over");
            Emit($"mov {Q} [edx], 0       ; leaves the source empty");
            Emit($"mov {R("ebx")}, {Q} [eax]          ; what the target had");
            Emit($"mov {Q} [eax], {R("ecx")}");
            string kept = NewLabel("taken");
            Emit($"cmp {R("ebx")}, {R("ecx")}");
            Emit($"je {kept}");
            Emit($"mov {R("eax")}, {R("ebx")}");
            Emit($"call {ObjectRoutine("free", t.Struct!)}");
            Label(kept);
        }

        /// <summary>
        /// The object a method returns, in eax: one of its own new variables is handed over
        /// (and so not freed when the method ends), a call's new object passes straight
        /// through, and anything else is copied, so the caller always gets one of its own.
        /// </summary>
        void EmitObjectResult(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol { Ownership: Ownership.Owned, IsLocal: true, IsParameter: false } v })
            {
                EmitExpr(e);
                Emit($"mov {Q} {Operand(new Place { Label = v.Label })}, 0   ; handed to the caller");
                return;
            }
            if (Binder.IsObjectCall(e))
            {
                EmitExpr(e);
                return;
            }
            ObjectLine(e);
            EmitObjectAddress(e);
            Emit($"call {ObjectRoutine("clone", currentMethod!.ReturnType.Struct!)}");
        }

        // ---------------------------------------------------------------- the routines

        /// <summary>
        /// phi_lines: how many marks, then each mark's address, line and file name (as wide as an
        /// address each), in the order the code is in. The runtime looks an address up in it (phi_line_at).
        /// </summary>
        void EmitLineTable()
        {
            if (!bits32) return;
            string size = bits64 ? "dq" : "dd";
            Line("");
            Comment("where each line's code starts, the line and its file (a CPU exception's error says its line)");
            Line("align 8");
            Label("phi_lines");
            Line($"    {size} {lineMarks.Count}");
            foreach (var (label, line, name) in lineMarks)
                Line($"    {size} {label}, {line}, {name}");
        }

        void EmitObjectRoutines()
        {
            var done = new HashSet<(string, StructSymbol)>();
            var arraysDone = new HashSet<string>();
            while (true)
            {
                if (arrayRoutines.Keys.FirstOrDefault(l => !arraysDone.Contains(l)) is string pending)
                {
                    arraysDone.Add(pending);
                    var (kind, element) = arrayRoutines[pending];
                    Line("");
                    Comment($"{kind} for a struct's array of {element} without a size");
                    Label(pending);
                    EmitArrayRoutine(kind, element);
                    continue;
                }
                if (objectRoutines.FirstOrDefault(r => !done.Contains(r)) is not { Struct: not null } next) break;
                done.Add(next);
                Line("");
                Comment($"{next.Kind} for {next.Struct.Name} objects");
                Label($"phi_{next.Kind}_{Templates.Label(next.Struct.Name)}");
                if (next.Struct == StructSymbol.Text)
                {
                    EmitTextRoutine(next.Kind);
                    continue;
                }
                if (next.Struct.IsInterface)
                {
                    EmitInterfaceRoutine(next.Kind, next.Struct);
                    continue;
                }
                switch (next.Kind)
                {
                    case "free": EmitFreeRoutine(next.Struct); break;
                    case "clone": EmitCloneRoutine(next.Struct); break;
                    case "assign": EmitAssignRoutine(next.Struct); break;
                    case "set": EmitSetRoutine(next.Struct); break;
                    case "renew": EmitRenewRoutine(next.Struct); break;
                    case "equal": EmitEqualRoutine(next.Struct); break;
                }
            }
        }

        string HeapMethodLabel(string name) => unit.Methods.First(m => m.Name == name).Label;

        /// <summary>
        /// A method's str result grows to fit (up to a str's max) when there's a heap: the caller
        /// passes a slot like a growing str's, which it frees when it ends. Without a heap (16-bit
        /// code), it's a buffer as big as the method can return.
        /// </summary>
        bool GrowResults => bits32 && unit.Methods.Any(m => m.Name == "Heap.Alloc");

        /// <summary>The current method's slots for the str results of the calls it makes.</summary>
        readonly List<string> resultSlots = new();

        /// <summary>The room <c>ask</c> makes in a str that grows: a line of up to 255 characters.</summary>
        const int AskRoom = 256;

        /// <summary>The bit of a growing str's room that says its text is in the program's data, not the heap.</summary>
        const string InData = "0x80000000";

        /// <summary>
        /// In the room passed for a str parameter that's the caller's text: the caller's str has a
        /// size, so its text never moves, and what doesn't fit is an error.
        /// </summary>
        const string Fixed = "0x40000000";

        /// <summary>A room without its flags.</summary>
        const string RoomMask = "0x3FFFFFFF";

        bool usesStrRoom;

        /// <summary>A memory operand that holds an address: dword, or qword in 64-bit code.</summary>
        string Q => bits64 ? "qword" : "dword";

        /// <summary>Where a growing text's room is in its slot: right after its address.</summary>
        int RoomAt => bits64 ? 8 : 4;

        /// <summary>A growing text's slot: its address and its room (16 bytes in 64-bit code, to stay aligned).</summary>
        int TextSlot => bits64 ? 16 : 8;

        /// <summary>register = where the text of the caller's str (a str parameter with no default) is.</summary>
        void LoadCallerText(string register, VariableSymbol v)
        {
            string done = NewLabel("text");
            Emit($"mov {R(register)}, {Q} [{v.Label}]");
            Emit($"mov {R(register)}, {Q} [{R(register)}]");
            Emit($"test {R(register)}, {R(register)}");
            Emit($"jnz {done}");
            LoadAddress(register, StringLabel(""));
            Label(done);
        }

        /// <summary>A register = the text of a str that grows; "" while nothing has been written to it.</summary>
        void LoadText(string register, VariableSymbol v)
        {
            string done = NewLabel("text");
            Emit($"mov {R(register)}, {Q} [{v.Label}]");
            Emit($"test {R(register)}, {R(register)}");
            Emit($"jnz {done}");
            LoadAddress(register, StringLabel(""));
            Label(done);
        }

        /// <summary>
        /// edi = where a str that grows keeps its text's address (its room right after, or at ebx
        /// with roomInEbx), ecx = the bytes wanted in it (with the zero). After: edi = its text,
        /// with room for that many (or as many as the heap had), ecx = its room, and edx = its old
        /// text, to free once nothing reads from it (0 if it didn't move).
        /// </summary>
        void EmitStrRoomCall(bool roomInEbx = false)
        {
            usesStrRoom = true;
            if (!roomInEbx) Emit($"lea ebx, [edi + {RoomAt}]");
            Emit("call phi_str_room");
        }

        /// <summary>Frees the old text EmitStrRoomCall left, which was pushed after it.</summary>
        void EmitFreeOld()
        {
            Emit($"call {HeapMethodLabel("Heap.Free")}   ; its old text (freeing 0 does nothing)");
            Emit($"add esp, {Slot}");
        }

        /// <summary>
        /// eax holds a string address (sourceIsString) or a number to write out as text; put it
        /// in a str that grows, making room first, up to its max.
        /// </summary>
        void EmitGrowStore(VariableSymbol v, bool sourceIsString, bool append, bool unsigned)
        {
            Emit("push eax                ; the value");
            LoadAddress("eax", v.Label);
            Emit("push eax                ; where its text is (its room is after it)");
            EmitGrowStoreAt(RoomAt, sourceIsString, append, unsigned, length: lengthOf.GetValueOrDefault(v));
        }

        // ---------------------------------------------------------------- a str that remembers its length
        //
        // A str's text ends at a zero, and adding to it means finding that zero: all of a text
        // that has grown long is read each time something is added. For a method's own str
        // that grows, when nothing but the method's own statements can change it, the method
        // keeps how long it is (plus 1; 0 when it isn't known) beside its other variables, and
        // an append starts from there.
        //
        // "Nothing else can change it" is what makes that right, so it's only done for a str
        // every one of whose uses is one of these: built or added to (s is a b; s++ c;), a
        // value of another str's or of a log (other is s; too), the method's result, or given to a method's
        // ptr<u8> parameter that the method only reads through (Text.Length: s). Anything
        // else (an index, addr, ask, a str parameter, an assembly block) and its length is
        // found the old way every time. A class's variable never is: another method, another
        // thread or a program using a library could change it.

        /// <summary>For the method being compiled: where each of its strs that remember their length keeps it.</summary>
        readonly Dictionary<VariableSymbol, string> lengthOf = new();

        Dictionary<VariableSymbol, bool>? remembers;
        HashSet<VariableSymbol>? onlyRead;

        static IEnumerable<Node> NodesOf(MethodSymbol m) =>
            m.Decl.Body.SelectMany(SyntaxWalker.Nodes).Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>());

        static (MethodSymbol? Method, List<Expr> Arguments) CallOf(Node n) => n switch
        {
            CallStmt { Resolved: MethodSymbol m } c => (m, c.Arguments),
            CallExpr { Resolved: MethodSymbol m } c => (m, c.Arguments),
            _ => (null, new List<Expr>()),
        };

        /// <summary>
        /// The ptr&lt;u8&gt; parameters their methods only read through: every use is an element
        /// read (text:i), a value of a log or of a str, or giving it to another such parameter. A method with an assembly
        /// block, or one in a compiled library (no code to look at), has none.
        /// </summary>
        HashSet<VariableSymbol> OnlyReadPointers()
        {
            if (onlyRead != null) return onlyRead;
            var bodies = unit.Methods.Where(m => !m.Decl.IsCompiled).ToDictionary(m => m, m => NodesOf(m).ToList());
            onlyRead = bodies.Where(b => !b.Value.Any(n => n is CallStmt { Resolved: RawBlockDecl } or CallExpr { Resolved: RawBlockDecl }))
                             .SelectMany(b => b.Key.Parameters.Where(p => p.Type.IsTextPointer && !p.Type.IsArray)).ToHashSet();
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var (m, nodes) in bodies)
                    foreach (VariableSymbol p in m.Parameters.Where(onlyRead.Contains).ToList())
                    {
                        // an element that's written, or whose address is taken, isn't a read
                        var written = new HashSet<Node>();
                        foreach (Node n in nodes)
                        {
                            Expr? target = n switch
                            {
                                AssignStmt a => a.Target,
                                AtomicStmt a => a.Target,
                                AppendStmt a => a.Target,
                                StringBuildStmt b => b.Target,
                                ArrayAssignStmt c => c.Target,
                                ObjectAssignStmt o => o.Target,
                                SetLengthStmt l => l.Array,
                                AskStmt a => a.Target,
                                CallStmt c => c.ResultTarget,
                                AddrExpr a => a.Operand,
                                _ => null,
                            };
                            if (target != null) written.Add(target);
                        }
                        int uses = nodes.Count(n => n is NameExpr name && name.Symbol == p), reads = 0;
                        foreach (Node n in nodes)
                        {
                            if (n is IndexExpr { Target: NameExpr element } index && element.Symbol == p && !written.Contains(index)) reads++;
                            // written out as text: a value of a log, or of a str's
                            bool Is(Expr e) => e is NameExpr name && name.Symbol == p;
                            reads += n switch
                            {
                                LogStmt log => log.Values.Count(Is),
                                StringBuildStmt build => build.Parts.Count(Is),
                                TextExpr text => text.Parts.Count(Is),
                                _ => 0,
                            };
                            (MethodSymbol? callee, List<Expr> arguments) = CallOf(n);
                            if (callee == null) continue;
                            for (int i = 0; i < Math.Min(arguments.Count, callee.Parameters.Count); i++)
                                if (arguments[i] is NameExpr given && given.Symbol == p && onlyRead.Contains(callee.Parameters[i])) reads++;
                        }
                        if (reads != uses)
                        {
                            onlyRead.Remove(p);
                            changed = true;
                        }
                    }
            }
            return onlyRead;
        }

        /// <summary>True for a method's own str that grows and that only the method's statements change (see above).</summary>
        bool RemembersLength(VariableSymbol v)
        {
            if (!bits32 || !v.Grows || !v.IsLocal || v.IsParameter || v.TextRef || !v.Type.IsString || v.Type.IsArray) return false;
            if (remembers == null)
            {
                remembers = new();
                HashSet<VariableSymbol> pointers = OnlyReadPointers();
                foreach (MethodSymbol m in unit.Methods.Where(m => !m.Decl.IsCompiled))
                {
                    List<Node> nodes = NodesOf(m).ToList();
                    foreach (VariableSymbol local in m.Locals.Where(l => l.Grows && !l.IsParameter))
                    {
                        int uses = nodes.Count(n => n is NameExpr name && name.Symbol == local), known = 0;
                        bool Is(Expr? e) => e is NameExpr name && name.Symbol == local;
                        foreach (Node n in nodes)
                        {
                            switch (n)
                            {
                                case StringBuildStmt build:
                                    known += (Is(build.Target) ? 1 : 0) + build.Parts.Count(Is);
                                    break;
                                case TextExpr text:
                                    known += text.Parts.Count(Is);
                                    break;
                                case LogStmt log:
                                    known += log.Values.Count(Is);
                                    break;
                                // s is value; and other is s; between strs
                                case AssignStmt { Op: AssignOp.Set, Target.Type: { IsString: true, IsArray: false } } assign:
                                    known += (Is(assign.Target) ? 1 : 0) + (Is(assign.Value) ? 1 : 0);
                                    break;
                            }
                            (MethodSymbol? callee, List<Expr> arguments) = CallOf(n);
                            if (callee == null) continue;
                            for (int i = 0; i < Math.Min(arguments.Count, callee.Parameters.Count); i++)
                                if (Is(arguments[i]) && pointers.Contains(callee.Parameters[i])) known++;
                        }
                        if (Is(m.Decl.Result)) known++;
                        remembers[local] = known == uses;
                    }
                }
            }
            return remembers.GetValueOrDefault(v);
        }

        /// <summary>
        /// The value is on the stack, and above it the slot that holds the text's address; the
        /// room is roomOffset bytes after the slot. Writes the value in, making room first.
        /// (In 64-bit code the addresses are whole registers, and each pushed value takes 8 bytes.)
        /// </summary>
        /// <param name="length">Where the method keeps this str's length (plus 1, or 0 for not known), for one that remembers it.</param>
        void EmitGrowStoreAt(int roomOffset, bool sourceIsString, bool append, bool unsigned, string? rooms = null, string? length = null)
        {
            string esi = R("esi"), edi = R("edi"), eax = R("eax");
            if (sourceIsString)
            {
                Need("phi_strlen");
                Emit($"mov {esi}, [esp + {Slot}]");
                Emit("call phi_strlen");
                Emit("inc eax                 ; with its zero");
            }
            else Emit("mov eax, 12             ; the most a number takes, with its zero");
            if (append)
            {
                Need("phi_strlen");
                string text = NewLabel("text");
                Emit("push eax");
                Emit($"mov {esi}, [esp + {Slot}]");
                Emit($"mov {esi}, {Q} [{esi}]          ; what's there now");
                Emit($"test {esi}, {esi}");
                Emit($"jnz {text}");
                LoadAddress("esi", StringLabel(""));
                Label(text);
                if (length != null)
                {
                    // as long as the method last knew it to be, when that's still where it ends
                    string measure = NewLabel("measure"), known = NewLabel("known");
                    Emit($"mov eax, [{length}]");
                    Emit("dec eax");
                    Emit($"js {measure}");
                    Emit($"cmp byte [{esi} + {eax}], 0");
                    Emit($"je {known}");
                    Label(measure);
                    Emit("call phi_strlen");
                    Label(known);
                }
                else Emit("call phi_strlen");
                Emit("pop ecx");
                Emit("push eax                ; how long it is now: where the value goes");
                Emit("add eax, ecx            ; after what's there");
            }
            Emit("mov ecx, eax");
            Emit($"mov {edi}, [esp{(append ? $" + {Slot}" : "")}]");
            if (rooms != null)
            {
                // an element of a str array without a most: its room is .max slots on
                Emit($"mov ebx, {rooms}");
                Emit($"lea ebx, [edi + ebx*{(bits64 ? 8 : 4)}]");
            }
            else Emit($"lea ebx, [edi + {roomOffset}]");
            EmitStrRoomCall(roomInEbx: true);
            if (append)
            {
                // on from its end, which was measured above (not found again: a long text is
                // added to many times), with that much less room; all its room when the
                // heap had no more is the zero's, and the value is cut
                string some = NewLabel("room");
                Emit("pop eax");
                Emit("cmp eax, ecx");
                Emit($"jb {some}");
                Emit("lea eax, [ecx - 1]");
                Label(some);
                if (length != null)
                {
                    Emit("inc eax");
                    Emit($"mov [{length}], eax         ; as long as it was (and 1), until what's added is measured");
                    Emit("dec eax");
                }
                Emit($"add {edi}, {eax}");
                Emit("sub ecx, eax");
            }
            Emit("push edx");
            Emit($"mov {eax}, [esp + {2 * Slot}]      ; the value");
            if (sourceIsString)
            {
                Need("phi_strcpy");
                Emit($"mov {esi}, {eax}");
                Emit("call phi_strcpy");
            }
            else
            {
                string convert = unsigned ? "phi_uint_to_str" : "phi_int_to_str";
                Need(convert);
                Emit($"call {convert}");
            }
            EmitKeepCut(2 * Slot);
            EmitFreeOld();
            if (length != null && append)
            {
                // longer by what was added: only that part is measured
                Emit($"mov {esi}, [esp]");
                Emit($"mov {esi}, {Q} [{esi}]");
                Emit($"mov eax, [{length}]");
                Emit("dec eax");
                Emit($"add {esi}, {eax}");
                Emit("call phi_strlen");
                Emit($"add [{length}], eax");
            }
            else if (length != null) Emit($"mov dword [{length}], 0   ; started again: measured when it's next added to");
            if (!textMayCut) Emit($"mov eax, [esp + {Slot}]");
            Emit($"add esp, {2 * Slot}");
            EmitTextFitsAfterFree();
        }

        /// <summary>phi_str_room, for strs that grow (see EmitStrRoomCall).</summary>
        void EmitStrRoomRoutine()
        {
            if (!usesStrRoom) return;
            Need("phi_strcpy");
            string max = (Binder.StrMax + 1).ToString();
            Line("");
            Comment("room in a str that grows: edi = the str, ecx = bytes wanted");
            Label("phi_str_room");
            Emit($"cmp ecx, {max}");
            Emit("jbe .capped");
            Emit($"mov ecx, {max}               ; never more than its max");
            Label(".capped");
            Emit("xor edx, edx");
            Emit($"test dword [ebx], {Fixed}");
            Emit("jnz .fits               ; a caller's str with a size: it stays, and what doesn't fit is an error");
            Emit("mov eax, [ebx]");
            Emit($"and eax, {RoomMask}      ; its room (the top bits: its text isn't on the heap, or never moves)");
            Emit("cmp ecx, eax");
            Emit("jbe .fits");
            // the new room: twice the old, and at least what's wanted (and 16), up to the max
            Emit("add eax, eax");
            Emit("cmp eax, ecx");
            Emit("jae .twice");
            Emit("mov eax, ecx");
            Label(".twice");
            Emit("cmp eax, 16");
            Emit("jae .small");
            Emit("mov eax, 16");
            Label(".small");
            Emit($"cmp eax, {max}");
            Emit("jbe .room");
            Emit($"mov eax, {max}");
            Label(".room");
            // (the addresses are whole registers in 64-bit code)
            string eax = R("eax"), esi = R("esi"), edi = R("edi"), edx = R("edx");
            Emit("push ebx");
            Emit("push edi");
            Emit("push eax                ; [esp] the new room, then the text's slot, then the room's");
            Emit("push eax");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit($"add esp, {Slot}");
            Emit("pop ecx");
            Emit("pop edi");
            Emit("pop ebx");
            Emit($"test {eax}, {eax}");
            Emit("jz .fits                ; no memory: it keeps the room it has");
            // the text so far goes along, then the old place is the caller's to free
            Emit("push ebx");
            Emit("push edi");
            Emit("push ecx");
            Emit("push eax");
            Emit($"mov {esi}, {Q} [{edi}]");
            Emit($"test {esi}, {esi}");
            Emit("jz .copied");
            Emit($"mov {edi}, {eax}");
            Emit("call phi_strcpy");
            Label(".copied");
            Emit("pop eax");
            Emit("pop ecx");
            Emit("pop edi");
            Emit("pop ebx");
            Emit($"mov {edx}, {Q} [{edi}]          ; the old text, to free (unless it wasn't on the heap)");
            Emit($"test dword [ebx], {InData}");
            Emit("jz .heap");
            Emit("xor edx, edx");
            Label(".heap");
            Emit($"mov {Q} [{edi}], {eax}");
            Emit("mov [ebx], ecx");
            Label(".fits");
            Emit("mov ecx, [ebx]");
            Emit($"and ecx, {RoomMask}");
            Emit($"mov {edi}, {Q} [{edi}]");
            Emit("ret");
        }

        /// <summary>eax = size bytes of zeroed memory from the heap; stops the kernel if there's none.</summary>
        void EmitObjectAlloc(StructSymbol s)
        {
            Need("phi_out_of_memory");
            Emit($"push dword {s.Size + (s.Typed ? 4 : 0)}");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit($"add esp, {Slot}");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jz phi_out_of_memory");
            if (s.Typed)
            {
                Emit($"mov dword [eax], {TypeTable(s)}   ; what it is, for an interface that holds it");
                Emit($"add {R("eax")}, 4");
            }
        }

        // ---------------------------------------------------------------- interfaces

        /// <summary>The structs with interfaces whose objects were made, so their type tables go in the data.</summary>
        readonly List<StructSymbol> typeTables = new();

        /// <summary>Where a type table's methods start: after its free, clone and equal routines.</summary>
        const int TypeTableMethods = 12;

        string TypeTable(StructSymbol s)
        {
            if (!typeTables.Contains(s))
            {
                typeTables.Add(s);
                // (there's one table of each in a program, which the final build writes)
                if (program.Library != null) return $"phi_type_{Templates.Label(s.Name)}";
                ObjectRoutine("free", s);
                ObjectRoutine("clone", s);
                ObjectRoutine("equal", s);
            }
            return $"phi_type_{Templates.Label(s.Name)}";
        }

        /// <summary>The interfaces whose methods this code calls, each with a slot label.</summary>
        readonly List<StructSymbol> interfaceSlots = new();

        /// <summary>
        /// Where an interface's methods start in a type table, as a label: only the whole program
        /// knows every interface, so it's a name in the code, which a library's can be compiled with.
        /// </summary>
        string InterfaceSlot(StructSymbol iface)
        {
            if (!interfaceSlots.Contains(iface)) interfaceSlots.Add(iface);
            return $"phi_slot_{Templates.Label(iface.Name)}";
        }

        /// <summary>
        /// A struct's type table: its free, clone and equal routines, then for each interface it
        /// has, at that interface's place, its methods (0 where it has another interface's place).
        /// </summary>
        void EmitTypeTables(StringBuilder output)
        {
            foreach (StructSymbol s in typeTables)
            {
                int count = s.Interfaces.Max(i => i.TableStart + i.Decl.Methods.Count);
                var methods = Enumerable.Repeat("0", count).ToArray();
                foreach (StructSymbol iface in s.Interfaces)
                    for (int i = 0; i < iface.Decl.Methods.Count; i++)
                    {
                        string wanted = iface.Decl.Methods[i].Name;
                        MethodSymbol? m = unit.Methods.FirstOrDefault(m => m.Decl.Owner.ForStruct?.Symbol == s && m.Decl.Name == wanted);
                        if (m != null) methods[iface.TableStart + i] = m.Label;
                    }
                output.AppendLine($"phi_type_{Templates.Label(s.Name)}: dd phi_free_{Templates.Label(s.Name)}, phi_clone_{Templates.Label(s.Name)}, phi_equal_{Templates.Label(s.Name)}" +
                                  (count > 0 ? ", " + string.Join(", ", methods) : ""));
            }
        }

        /// <summary>
        /// The routines for an interface's objects, called as any object's are: each goes to the
        /// routine of whichever struct the object is, from its type table. A slot that gets
        /// another one frees what it had (it may be another struct), and a new one starts empty.
        /// </summary>
        void EmitInterfaceRoutine(string kind, StructSymbol iface)
        {
            switch (kind)
            {
                // (a type table's entries are 4 bytes in 64-bit code too: the program is below 4 GB,
                // so each is read into a register, then jumped to)
                case "free":
                case "clone":
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jz .done                ; nothing to free or copy");
                    Emit("mov edx, [eax - 4]");
                    if (bits64)
                    {
                        Emit($"mov edx, [rdx + {(kind == "free" ? 0 : 4)}]");
                        Emit("jmp rdx");
                    }
                    else Emit($"jmp [edx + {(kind == "free" ? 0 : 4)}]");
                    Label(".done");
                    Emit("ret");
                    break;

                case "equal":
                    Emit($"cmp {R("eax")}, {R("edx")}");
                    Emit("je .same                ; the same object (or both empty)");
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jz .different");
                    Emit($"test {R("edx")}, {R("edx")}");
                    Emit("jz .different");
                    Emit("mov ecx, [eax - 4]");
                    Emit("cmp ecx, [edx - 4]");
                    Emit("jne .different          ; different structs");
                    if (bits64)
                    {
                        Emit("mov ecx, [rcx + 8]");
                        Emit("jmp rcx");
                    }
                    else Emit("jmp [ecx + 8]");
                    Label(".same");
                    Emit("mov eax, 1");
                    Emit("ret");
                    Label(".different");
                    Emit("xor eax, eax");
                    Emit("ret");
                    break;

                case "set":
                    // a copy of the source (made first: it may be inside what the slot had)
                    Emit("push eax                ; the slot");
                    Emit($"mov {R("eax")}, {R("edx")}");
                    Emit($"call {ObjectRoutine("clone", iface)}");
                    Emit("pop edx");
                    Emit($"xchg {R("eax")}, {Q} [edx]         ; what it had, freed");
                    Emit($"jmp {ObjectRoutine("free", iface)}");
                    break;

                case "renew":
                    Emit($"mov {R("edx")}, {R("eax")}");
                    Emit($"mov {R("eax")}, {Q} [edx]");
                    Emit($"mov {Q} [edx], 0       ; a new one starts empty");
                    Emit($"jmp {ObjectRoutine("free", iface)}");
                    break;

                default:
                    // assign: the binder only lets a slot that owns its object get another one
                    Emit("ret");
                    break;
            }
        }

        /// <summary>
        /// The routines for a new str field's text, called the way an owned object's are:
        /// free (eax = the text), clone (eax = the text; eax = a copy on the heap, or 0) and
        /// equal (eax, edx = the texts; eax = 1 when they match). No text is the same as ''.
        /// </summary>
        void EmitTextRoutine(string kind)
        {
            switch (kind)
            {
                case "free":
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jz .done");
                    Emit("push eax");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit($"add esp, {Slot}");
                    Label(".done");
                    Emit("ret");
                    break;

                case "clone":
                    Need("phi_strlen");
                    Need("phi_strcpy");
                    Need("phi_out_of_memory");
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jnz .some");
                    Emit("ret                     ; no text: a copy has none either");
                    Label(".some");
                    Emit("push ebp");
                    Emit("mov ebp, esp");
                    Emit($"push eax                ; {Pushed(1)} the text");
                    Emit($"mov {R("esi")}, {R("eax")}");
                    Emit("call phi_strlen");
                    Emit("inc eax                 ; with its zero");
                    Emit($"push eax                ; {Pushed(2)} the room it takes");
                    Emit("push eax");
                    Emit($"call {HeapMethodLabel("Heap.Alloc")}");
                    Emit($"add esp, {Slot}");
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jz phi_out_of_memory");
                    Emit("push eax");
                    Emit($"mov {R("edi")}, {R("eax")}");
                    Emit($"mov {R("esi")}, {Pushed(1)}");
                    Emit($"mov ecx, [ebp - {2 * Slot}]");
                    Emit("call phi_strcpy");
                    Emit("pop eax");
                    Emit("mov esp, ebp");
                    Emit("pop ebp");
                    Emit("ret");
                    break;

                case "equal":
                {
                    Need("phi_strcmp");
                    string empty = StringLabel("");
                    Emit($"test {R("eax")}, {R("eax")}");
                    Emit("jnz .a");
                    LoadAddress("eax", empty);
                    Label(".a");
                    Emit($"test {R("edx")}, {R("edx")}");
                    Emit("jnz .b");
                    LoadAddress("edx", empty);
                    Label(".b");
                    Emit($"mov {R("esi")}, {R("eax")}");
                    Emit($"mov {R("edi")}, {R("edx")}");
                    Emit("call phi_strcmp");
                    Emit("xor eax, 1              ; 1 when they match");
                    Emit("ret");
                    break;
                }

                default:
                    throw new InvalidOperationException($"no {kind} routine for text");
            }
        }

        /// <summary>
        /// After a struct's owned texts were copied: each copy's room is what its text takes (the
        /// struct's bytes still hold the source's). target is where the struct is, like [ebp - 12].
        /// </summary>
        void EmitTextRooms(StructSymbol s, string target)
        {
            foreach (OwnedSlot slot in s.OwnedSlots.Where(o => o.Of == StructSymbol.Text))
            {
                string none = $".room{labelCount++}";
                Need("phi_strlen");
                Emit($"mov {R("edx")}, {target}");
                Emit($"mov {R("esi")}, {Q} [edx + {slot.Offset}]");
                Emit("xor eax, eax");
                Emit($"test {R("esi")}, {R("esi")}");
                Emit($"jz {none}");
                Emit("call phi_strlen");
                Emit("inc eax");
                Label(none);
                Emit($"mov {R("edx")}, {target}");
                Emit($"mov [edx + {slot.Offset + RoomAt}], eax   ; the copy's room");
            }
        }

        /// <summary>
        /// In an object routine's frame (push ebp; mov ebp, esp), the i-th value it pushed: [ebp - 4 * i],
        /// or a qword at [ebp - 8 * i] in 64-bit code.
        /// </summary>
        string Pushed(int i) => $"{Q} [ebp - {Slot * i}]";

        /// <summary>The owned slot followed with a loop: one of the struct's own type (a list's next).</summary>
        static OwnedSlot? Chain(StructSymbol s) =>
            s.OwnedSlots.Where(o => o.Of == s && o.Array == null).Select(o => (OwnedSlot?)o).LastOrDefault();

        /// <summary>
        /// Calls a slot's free routine for the struct whose address is at obj: an object's (or
        /// text's) with what the slot holds, an array's with where the slot is.
        /// </summary>
        void EmitSlotFree(OwnedSlot slot, string obj)
        {
            Emit($"mov {R("eax")}, {obj}");
            if (slot.Array != null)
            {
                Emit($"add {R("eax")}, {slot.Offset}");
                Emit($"call {ArrayRoutine("free", slot.Array)}");
                return;
            }
            Emit($"mov {R("eax")}, {Q} [eax + {slot.Offset}]");
            Emit($"call {ObjectRoutine("free", slot.Of)}");
        }

        static IEnumerable<OwnedSlot> NotChain(StructSymbol s) =>
            s.OwnedSlots.Where(o => o != Chain(s));

        void EmitFreeRoutine(StructSymbol s)
        {
            OwnedSlot? chain = Chain(s);
            Emit("push ebp");
            Emit("mov ebp, esp");
            Label(".next");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jz .done");
            Emit($"push eax                ; {Pushed(1)} the object");
            foreach (OwnedSlot slot in NotChain(s))
                EmitSlotFree(slot, Pushed(1));
            if (chain is OwnedSlot c)
            {
                Emit($"mov {R("eax")}, {Pushed(1)}");
                Emit($"push {Q} [eax + {c.Offset}]   ; {Pushed(2)} the next one, freed by going round again");
            }
            if (s.Typed)
            {
                Emit($"mov {R("eax")}, {Pushed(1)}");
                Emit($"sub {R("eax")}, 4              ; its type table is where its memory starts");
                Emit("push eax");
            }
            else Emit($"push {Pushed(1)}");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit($"add esp, {Slot}");
            if (chain != null)
            {
                Emit("pop eax");
                Emit("mov esp, ebp");
                Emit("jmp .next");
            }
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitCloneRoutine(StructSymbol s)
        {
            OwnedSlot? chain = Chain(s);
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jnz .some");
            Emit("ret                     ; a copy of nothing is nothing");
            Label(".some");
            Emit("push ebp");
            Emit("mov ebp, esp");
            string source = Pushed(1), copy = Pushed(2), filling = Pushed(3);
            Emit($"push eax                ; {source} the source");
            EmitObjectAlloc(s);
            Emit($"push eax                ; {copy} the copy, returned");
            Emit($"push eax                ; {filling} the copy being filled");
            Label(".fill");
            Emit($"mov {R("esi")}, {source}");
            Emit($"mov {R("edi")}, {filling}");
            Emit($"mov ecx, {s.Size}");
            Emit("rep movsb");
            foreach (OwnedSlot slot in NotChain(s))
            {
                Emit($"mov {R("eax")}, {source}");
                if (slot.Array != null)
                {
                    // its own room, with copies of the elements
                    Emit($"add {R("eax")}, {slot.Offset}");
                    Emit($"mov {R("edx")}, {filling}");
                    Emit($"add {R("edx")}, {slot.Offset}");
                    Emit($"call {ArrayRoutine("clone", slot.Array)}");
                    continue;
                }
                Emit($"mov {R("eax")}, {Q} [eax + {slot.Offset}]");
                Emit($"call {ObjectRoutine("clone", slot.Of)}");
                Emit($"mov {R("edx")}, {filling}");
                Emit($"mov {Q} [edx + {slot.Offset}], {R("eax")}");
            }
            EmitTextRooms(s, filling);
            if (chain is OwnedSlot c)
            {
                Emit($"mov {R("edx")}, {filling}");
                Emit($"mov {Q} [edx + {c.Offset}], 0");
                Emit($"mov {R("eax")}, {source}");
                Emit($"mov {R("eax")}, {Q} [eax + {c.Offset}]");
                Emit($"test {R("eax")}, {R("eax")}");
                Emit("jz .done");
                Emit($"mov {source}, {R("eax")}      ; the next source");
                EmitObjectAlloc(s);
                Emit($"mov {R("edx")}, {filling}");
                Emit($"mov {Q} [edx + {c.Offset}], {R("eax")}");
                Emit($"mov {filling}, {R("eax")}");
                Emit("jmp .fill");
            }
            Label(".done");
            Emit($"mov {R("eax")}, {copy}");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitAssignRoutine(StructSymbol s)
        {
            if (!s.OwnsObjects)
            {
                Emit($"mov {R("edi")}, {R("eax")}");
                Emit($"mov {R("esi")}, {R("edx")}");
                Emit($"mov ecx, {s.Size}");
                Emit("rep movsb");
                Emit("ret");
                return;
            }

            if (s.OwnedSlots.Any(o => o.Array != null))
            {
                // a whole copy of the source first (it may be inside the target); then the target
                // lets go of what it owns and takes the copy's values, and the copy's memory goes back
                Emit("push ebp");
                Emit("mov ebp, esp");
                Emit($"push eax                ; {Pushed(1)} into");
                Emit($"mov {R("eax")}, {R("edx")}");
                Emit($"call {ObjectRoutine("clone", s)}");
                Emit($"push eax                ; {Pushed(2)} the copy");
                foreach (OwnedSlot slot in s.OwnedSlots) EmitSlotFree(slot, Pushed(1));
                Emit($"mov {R("esi")}, {Pushed(2)}");
                Emit($"mov {R("edi")}, {Pushed(1)}");
                Emit($"mov ecx, {s.Size}");
                Emit("rep movsb");
                Emit($"mov {R("eax")}, {Pushed(2)}");
                if (s.Typed) Emit($"sub {R("eax")}, 4");
                Emit("push eax");
                Emit($"call {HeapMethodLabel("Heap.Free")}");
                Emit($"add esp, {Slot}");
                Emit("mov esp, ebp");
                Emit("pop ebp");
                Emit("ret");
                return;
            }

            // copies of what the source owns are made before anything changes, because the
            // source may be inside the target (head is head.next); the target's old objects are
            // freed last
            int n = s.OwnedSlots.Count;
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit($"push eax                ; {Pushed(1)} into");
            Emit($"push edx                ; {Pushed(2)} from");
            foreach (OwnedSlot slot in s.OwnedSlots)
            {
                Emit($"mov {R("eax")}, {Pushed(2)}");
                Emit($"mov {R("eax")}, {Q} [eax + {slot.Offset}]");
                Emit($"call {ObjectRoutine("clone", slot.Of)}");
                Emit("push eax");
            }
            foreach (OwnedSlot slot in s.OwnedSlots)
            {
                Emit($"mov {R("eax")}, {Pushed(1)}");
                Emit($"push {Q} [eax + {slot.Offset}]");
            }
            Emit($"mov {R("esi")}, {Pushed(2)}");
            Emit($"mov {R("edi")}, {Pushed(1)}");
            Emit($"mov ecx, {s.Size}");
            Emit("rep movsb");
            for (int i = 0; i < n; i++)
            {
                Emit($"mov {R("edx")}, {Pushed(1)}");
                Emit($"mov {R("eax")}, {Pushed(3 + i)}");
                Emit($"mov {Q} [edx + {s.OwnedSlots[i].Offset}], {R("eax")}");
            }
            EmitTextRooms(s, Pushed(1));
            for (int i = 0; i < n; i++)
            {
                Emit($"mov {R("eax")}, {Pushed(3 + n + i)}");
                Emit($"call {ObjectRoutine("free", s.OwnedSlots[i].Of)}");
            }
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitSetRoutine(StructSymbol s)
        {
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit($"push eax                ; {Pushed(1)} the slot");
            Emit($"push edx                ; {Pushed(2)} the source");
            Emit($"test {R("edx")}, {R("edx")}");
            Emit("jnz .copy");
            Emit($"mov {R("edx")}, {Pushed(1)}      ; a copy of nothing: the slot is emptied");
            Emit($"mov {R("eax")}, {Q} [edx]");
            Emit($"mov {Q} [edx], 0");
            Emit($"call {ObjectRoutine("free", s)}");
            Emit("jmp .done");
            Label(".copy");
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"mov {R("eax")}, {Q} [edx]");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jnz .have");
            // an empty slot gets a fresh copy, made before the slot changes: the source may
            // contain it (a.next is a)
            Emit($"mov {R("eax")}, {Pushed(2)}");
            Emit($"call {ObjectRoutine("clone", s)}");
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"mov {Q} [edx], {R("eax")}");
            Emit("jmp .done");
            Label(".have");
            Emit($"mov {R("edx")}, {Pushed(2)}");
            Emit($"call {ObjectRoutine("assign", s)}");
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitRenewRoutine(StructSymbol s)
        {
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit($"push eax                ; {Pushed(1)} the slot");
            Emit($"mov {R("eax")}, {Q} [eax]");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jnz .reuse");
            EmitObjectAlloc(s);
            Emit($"mov {R("edx")}, {Pushed(1)}");
            Emit($"mov {Q} [edx], {R("eax")}");
            Emit("jmp .done");
            Label(".reuse");
            Emit($"push eax                ; {Pushed(2)} the object, emptied to be used again");
            foreach (OwnedSlot slot in s.OwnedSlots)
                EmitSlotFree(slot, Pushed(2));
            Emit($"mov {R("edi")}, {Pushed(2)}");
            Emit($"mov ecx, {s.Size}");
            Emit("xor al, al");
            Emit("rep stosb");
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitEqualRoutine(StructSymbol s)
        {
            OwnedSlot? chain = Chain(s);
            Emit("push ebp");
            Emit("mov ebp, esp");
            string a = $"{Q} [ebp - {Slot}]", b = $"{Q} [ebp - {2 * Slot}]";
            Emit($"push eax                ; {a} a");
            Emit($"push edx                ; {b} b");
            Label(".next");
            Emit($"mov {R("eax")}, {a}");
            Emit($"mov {R("edx")}, {b}");
            Emit($"cmp {R("eax")}, {R("edx")}");
            Emit("je .same                ; the same object (or both empty)");
            Emit($"test {R("eax")}, {R("eax")}");
            Emit("jz .different");
            Emit($"test {R("edx")}, {R("edx")}");
            Emit("jz .different");

            // the bytes between the owned slots, which hold addresses rather than values
            int start = 0;
            foreach (int end in s.OwnedSlots.Select(o => o.Offset).Append(s.Size))
            {
                if (end > start)
                {
                    Emit($"lea esi, [eax + {start}]");
                    Emit($"lea edi, [edx + {start}]");
                    Emit($"mov ecx, {end - start}");
                    Emit("repe cmpsb");
                    Emit("jne .different");
                }
                // past the slot: an object's address, an array's view, or a text's address and its room
                start = end + (s.OwnedSlots.Any(o => o.Offset == end && o.Array != null) ? ViewMax + 4
                               : s.OwnedSlots.Any(o => o.Offset == end && o.Of == StructSymbol.Text) ? TextSlot : Word);
            }

            foreach (OwnedSlot slot in NotChain(s))
            {
                Emit($"mov {R("eax")}, {a}");
                Emit($"mov {R("edx")}, {b}");
                if (slot.Array != null)
                {
                    Emit($"add {R("eax")}, {slot.Offset}");
                    Emit($"add {R("edx")}, {slot.Offset}");
                    Emit($"call {ArrayRoutine("equal", slot.Array)}");
                }
                else
                {
                    Emit($"mov {R("eax")}, {Q} [eax + {slot.Offset}]");
                    Emit($"mov {R("edx")}, {Q} [edx + {slot.Offset}]");
                    Emit($"call {ObjectRoutine("equal", slot.Of)}");
                }
                Emit("test eax, eax");
                Emit("jz .different");
            }
            if (chain is OwnedSlot c)
            {
                Emit($"mov {R("eax")}, {a}");
                Emit($"mov {R("eax")}, {Q} [eax + {c.Offset}]");
                Emit($"mov {a}, {R("eax")}");
                Emit($"mov {R("edx")}, {b}");
                Emit($"mov {R("edx")}, {Q} [edx + {c.Offset}]");
                Emit($"mov {b}, {R("edx")}");
                Emit("jmp .next");
            }
            Label(".same");
            Emit("mov eax, 1");
            Emit("jmp .done");
            Label(".different");
            Emit("xor eax, eax");
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        // ================================================================ data

        void EmitData(StringBuilder output)
        {
            foreach (VariableSymbol v in unit.Variables.Distinct())
            {
                if (v.Type.IsError || v.IsLocal || v.IsOutside) continue; // a method's variables are in its frame
                // each starts on a word, so a number or pointer in it is read and written whole by
                // other threads (and atomic steps on it stay one cache line's)
                if (bits32) output.AppendLine($"align {Slot}, db 0");
                output.Append(DataFor(v));
            }

            foreach (var (label, size) in staticTemps)
                output.AppendLine($"{label}: times {size} db 0");

            if (program.Library == null)
            {
                if (usesObjects) output.AppendLine("phi_object_line: dd 0");
                EmitTypeTables(output);
            }

            foreach (var (value, label) in stringLabels)
                output.AppendLine($"{label}: db {NasmBytes(value)}");

            foreach (var (value, label) in floatLabels)
                output.AppendLine($"{label}: dq 0x{BitConverter.DoubleToInt64Bits(value):X}   ; {value}");
        }

        string DataFor(VariableSymbol v)
        {
            var sb = new StringBuilder();
            List<object>? values = v.StaticValues;
            PhiType t = v.Type;

            if (v.GrowingArray)
            {
                sb.AppendLine($"{v.Label}: {(bits64 ? "dq 0\n    dd 0, 0" : "dd 0, 0, 0")}   ; an array that grows: its elements (on the heap), how many, its room");
                return sb.ToString();
            }

            if (t.Kind is TypeKind.F16 or TypeKind.F32 or TypeKind.F64)
            {
                // floating point: each constant's bits
                var bits = (values ?? new List<object>()).Select(x => x is double d ? d : (double)(long)x).Select(d => t.Kind switch
                {
                    TypeKind.F64 => $"0x{BitConverter.DoubleToInt64Bits(d):X}",
                    TypeKind.F32 => $"0x{BitConverter.SingleToInt32Bits((float)d):X}",
                    _ => $"0x{BitConverter.HalfToInt16Bits((Half)d):X}",
                }).ToList();
                string kind = t.Kind switch { TypeKind.F64 => "dq", TypeKind.F32 => "dd", _ => "dw" };
                if (bits.Count == 0) sb.AppendLine($"{v.Label}: times {v.Count} {kind} 0   ; {t}");
                else
                {
                    sb.AppendLine($"{v.Label}: {kind} {string.Join(", ", bits)}   ; {t}");
                    if (bits.Count < v.Count) sb.AppendLine($"    times {v.Count - bits.Count} {kind} 0");
                }
                if (t.Filled) sb.AppendLine("    dd 0   ; how many are in it");
                return sb.ToString();
            }

            if (v.Grows)
            {
                if (values is [var first])
                {
                    // its first text is here, until it needs more room than that
                    string text = AsText(first);
                    AppendString(sb, $"{v.Label}__text", text, text.Length + 1);
                    sb.AppendLine(bits64
                        ? $"{v.Label}: dq {v.Label}__text\n    dd {text.Length + 1} | {InData}, 0   ; a str that grows: its text, its room"
                        : $"{v.Label}: dd {v.Label}__text, {text.Length + 1} | {InData}   ; a str that grows: its text, its room");
                }
                else sb.AppendLine(bits64
                    ? $"{v.Label}: dq 0\n    dd 0, 0   ; a str that grows: its text (on the heap), its room"
                    : $"{v.Label}: dd 0, 0   ; a str that grows: its text (on the heap), its room");
                return sb.ToString();
            }

            if (t.IsString)
            {
                string text = values is [var first] ? AsText(first) : "";
                AppendString(sb, v.Label, text, v.Capacity);
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Str && t.Room > 0)
            {
                sb.AppendLine($"{v.Label}: times {t.ByteSize} db 0   ; {t.Length} texts of up to {t.Room - 1}");
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Str)
            {
                var pointers = new List<string>();
                for (int i = 0; i < v.Count; i++)
                {
                    string element = $"{v.Label}__{i}";
                    pointers.Add(element);
                    string text = values != null && i < values.Count ? AsText(values[i]) : "";
                    AppendString(sb, element, text, v.Capacity);
                }
                // each element's address, then (when they grow) each one's room, a word for each
                sb.AppendLine($"{v.Label}: {(bits64 ? "dq" : "dd")} {string.Join(", ", pointers)}");
                if (v.GrowingElements)
                    sb.AppendLine($"    dd {string.Join(", ", Enumerable.Repeat($"{v.Capacity} | {InData}{(bits64 ? ", 0" : "")}", v.Count))}   ; each one's room: its own buffer");
                if (t.Filled) sb.AppendLine("    dd 0   ; how many are in it");
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Struct)
            {
                sb.AppendLine($"{v.Label}: times {StorageSize(v)} db 0   ; {t}");
                return sb.ToString();
            }

            string directive = t.ElementSize switch { 1 => "db", 2 => "dw", 8 => "dq", _ => "dd" };
            if (values == null || values.Count == 0)
            {
                sb.AppendLine(v.Count == 1 ? $"{v.Label}: {directive} 0" : $"{v.Label}: times {v.Count} {directive} 0");
                if (t.Filled) sb.AppendLine("    dd 0   ; how many are in it");
                return sb.ToString();
            }

            long mask = t.ElementSize switch { 1 => 0xFF, 2 => 0xFFFF, 8 => -1, _ => 0xFFFFFFFF };
            var numbers = values.Select(x => x is long n ? n & mask : 0).Select(n => n.ToString());
            sb.AppendLine($"{v.Label}: {directive} {string.Join(", ", numbers)}");
            if (values.Count < v.Count) sb.AppendLine($"    times {v.Count - values.Count} {directive} 0");
            return sb.ToString();
        }

        /// <summary>A constant destined for a str: text as-is, numbers written in decimal.</summary>
        static string AsText(object value) => value switch
        {
            string s => s,
            long n => ((int)n).ToString(),
            _ => "",
        };

        static void AppendString(StringBuilder sb, string label, string text, int capacity)
        {
            if (text.Length + 1 > capacity) text = text[..Math.Max(capacity - 1, 0)];
            sb.AppendLine($"{label}: db {NasmBytes(text)}");
            int padding = capacity - text.Length - 1;
            if (padding > 0) sb.AppendLine($"    times {padding} db 0");
        }

        /// <summary>'Hello', 13, 10, 0 — printable text quoted, everything else as numbers.</summary>
        static string NasmBytes(string text)
        {
            var parts = new List<string>();
            var run = new StringBuilder();

            void Flush()
            {
                if (run.Length == 0) return;
                parts.Add($"'{run}'");
                run.Clear();
            }

            foreach (char c in text)
            {
                if (c >= 0x20 && c < 0x7F && c != '\'') run.Append(c);
                else
                {
                    Flush();
                    parts.Add(c < 256 ? ((int)c).ToString() : "'?'");
                }
            }

            Flush();
            parts.Add("0");
            return string.Join(", ", parts);
        }
    }
}

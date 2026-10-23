using Phi.Compiler.Syntax;

namespace Phi.Compiler.Semantics
{
    public sealed class BoundUnit
    {
        public UnitKind Kind { get; init; }
        public List<ClassDecl> Classes { get; } = new();
        public List<MethodSymbol> Methods { get; } = new();
        public List<VariableSymbol> Variables { get; } = new();
        public List<RawBlockDecl> RawBlocks { get; } = new();
        public HashSet<BuiltinFunction> UsedBuiltins { get; } = new();

        /// <summary>Class-level names, for {name} in asm blocks: variables and constants.</summary>
        public Dictionary<string, Symbol> ClassNames { get; } = new();
    }

    /// <summary>
    /// A 16-bit program has a Boot unit and maybe an Os unit; a 32-bit program has only a
    /// Kernel unit (the compiler supplies its boot sector and loader).
    /// </summary>
    public sealed class BoundProgram
    {
        public SourceFile File { get; init; } = null!;
        public BoundUnit? Boot { get; init; }
        public BoundUnit? Os { get; init; }
        public BoundUnit? Kernel { get; init; }
        public BoundUnit? Program { get; init; }

        public IEnumerable<BoundUnit> Units => new[] { Boot, Os, Kernel, Program }.Where(u => u != null)!;
    }

    /// <summary>
    /// Resolves names, works out types and storage, and checks the program. It also rewrites
    /// compound assignments (x++ 2) into plain ones (x is x + 2) so code generation stays small.
    /// </summary>
    public sealed class Binder
    {
        public const string BootBase = "Bootloader";
        public const string KernelBase = "OS";
        public const string Kernel32Base = "Kernel";

        /// <summary>Shared code (like drivers): joins the OS classes, the 32-bit kernel or the user program, whichever the program has.</summary>
        public const string LibraryBase = "Library";
        public const string ProgramBase = "Program";

        /// <summary>The library each unit's code is built against.</summary>
        public static string TargetOf(UnitKind unit) => unit switch
        {
            UnitKind.Kernel => "x86_32",
            UnitKind.Program => "x86_32_user",
            _ => "x86_16",
        };

        /// <summary>A str filled from a number needs room for "-2147483648".</summary>
        const int IntTextCapacity = 12;

        /// <summary>Capacity of a str copied from another str whose size isn't known yet.</summary>
        const int DefaultStrCapacity = 64;

        readonly SourceFile mainFile;
        readonly DiagnosticBag diagnostics;

        /// <summary>The file the code being checked came from, for error locations.</summary>
        SourceFile file;

        sealed class Scope
        {
            public ClassDecl Class { get; init; } = null!;
            public UnitKind Unit { get; init; }
            public MethodSymbol? Method { get; init; }
            public Dictionary<string, Symbol> Names { get; } = new();
        }

        readonly Dictionary<ClassDecl, Scope> classScopes = new();
        readonly Dictionary<MethodDecl, Scope> methodScopes = new();
        readonly Dictionary<UnitKind, BoundUnit> units = new();
        readonly Dictionary<(UnitKind, string), MethodSymbol> methodsByFullName = new();
        readonly Dictionary<string, RawBlockDecl> rawBlocks = new();
        readonly Dictionary<string, StructSymbol> structs = new();
        readonly HashSet<VarDecl> loopCounters = new();

        /// <summary>
        /// Method variables whose name is declared more than once, in separate blocks (two
        /// branches of an if, a loop and a later block). Each is seen only in its own block.
        /// </summary>
        readonly HashSet<VariableSymbol> blockScoped = new();

        /// <summary>The block-scoped variables in sight while binding blockMethod: one table per open block.</summary>
        readonly List<Dictionary<string, VariableSymbol>> blockNames = new();
        MethodSymbol? blockMethod;
        readonly HashSet<StructSymbol> slotsDone = new();
        int unsafeDepth;

        Binder(SourceFile file, DiagnosticBag diagnostics)
        {
            mainFile = file;
            this.file = file;
            this.diagnostics = diagnostics;
        }

        public static BoundProgram? Bind(ProgramNode program, DiagnosticBag diagnostics) =>
            new Binder(program.File, diagnostics).BindProgram(program);

        void Error(Span span, string message) => diagnostics.Error(file, span, message);

        // ================================================================ declarations

        BoundProgram? BindProgram(ProgramNode program)
        {
            DeclareStructs(program.Structs);

            // Kernel classes make a 32-bit kernel, Program classes a user program; otherwise
            // it is a 16-bit program
            bool userProgram = program.Classes.Any(c => c.Base == ProgramBase);
            bool protectedMode = !userProgram && program.Classes.Any(c => c.Base == Kernel32Base);
            BoundUnit? boot = null, os = null, kernel = null, user = null;

            if (userProgram)
            {
                user = new BoundUnit { Kind = UnitKind.Program };
                units[UnitKind.Program] = user;
            }
            else if (protectedMode)
            {
                kernel = new BoundUnit { Kind = UnitKind.Kernel };
                units[UnitKind.Kernel] = kernel;
            }
            else
            {
                var bootClasses = program.Classes.Where(c => c.Base == BootBase).ToList();
                if (bootClasses.Count == 0)
                {
                    diagnostics.Error(mainFile, new Span(0, 0), $"a program needs a class that inherits {BootBase} (a 16-bit program), " +
                                                               $"or classes that inherit {Kernel32Base} (a 32-bit kernel), e.g. phi.Hello:{Kernel32Base} {{ log 'hi'; }}");
                    return null;
                }
                foreach (ClassDecl extra in bootClasses.Skip(1))
                {
                    file = extra.File;
                    Error(extra.BaseSpan, $"only one class can inherit {BootBase}; it becomes the 512-byte boot sector");
                }

                boot = new BoundUnit { Kind = UnitKind.Boot };
                units[UnitKind.Boot] = boot;
            }

            // a struct's methods belong to the program's main code: the OS classes of a 16-bit
            // program that has them (Library classes go there too), otherwise its only unit
            bool hasOs = program.Classes.Any(c => c.Base is KernelBase or LibraryBase);
            UnitKind structUnit = userProgram ? UnitKind.Program : protectedMode ? UnitKind.Kernel : hasOs ? UnitKind.Os : UnitKind.Boot;

            foreach (ClassDecl cls in program.Classes)
            {
                file = cls.File;
                UnitKind unit;
                if (cls.ForStruct != null) continue; // placed below, once every unit exists
                if (userProgram && cls.Base is not (ProgramBase or LibraryBase))
                {
                    Error(cls.BaseSpan, $"class {cls.Name} ({cls.Base}) can't be part of a user program; a program's classes inherit " +
                                        $"{ProgramBase} (or {LibraryBase})");
                    continue;
                }
                if (protectedMode && cls.Base is BootBase or KernelBase)
                {
                    Error(cls.BaseSpan, $"class {cls.Name} is 16-bit code ({cls.Base}), but this program is a 32-bit kernel; " +
                                        $"use :{Kernel32Base} for every class (PHI supplies the boot sector and loader)");
                    continue;
                }
                if (cls.Base == BootBase) unit = UnitKind.Boot;
                else if (cls.Base == KernelBase) unit = UnitKind.Os;
                else if (cls.Base == Kernel32Base) unit = UnitKind.Kernel;
                else if (cls.Base == ProgramBase) unit = UnitKind.Program;
                else if (cls.Base == LibraryBase) unit = userProgram ? UnitKind.Program : protectedMode ? UnitKind.Kernel : UnitKind.Os;
                else
                {
                    string why = cls.Base == "" ? "has no base class" : $"inherits '{cls.Base}', which PHI doesn't know";
                    Error(cls.BaseSpan, $"class {cls.Name} {why}; use :{BootBase} for the boot sector, :{KernelBase} for 16-bit code " +
                                        $"loaded after it, :{Kernel32Base} for a 32-bit kernel, or :{LibraryBase} for code shared by both");
                    continue;
                }

                if (unit == UnitKind.Os && os == null)
                {
                    os = new BoundUnit { Kind = UnitKind.Os };
                    units[UnitKind.Os] = os;
                }

                units[unit].Classes.Add(cls);
                classScopes[cls] = new Scope { Class = cls, Unit = unit };
            }

            foreach (ClassDecl cls in program.Classes.Where(c => c.ForStruct != null))
            {
                if (!units.ContainsKey(structUnit)) continue; // no code at all to use them from
                units[structUnit].Classes.Add(cls);
                classScopes[cls] = new Scope { Class = cls, Unit = structUnit };
            }

            foreach (RawBlockDecl raw in program.RawBlocks)
            {
                file = raw.File;
                if (!rawBlocks.TryAdd(raw.Name, raw))
                    Error(raw.Span, $"an assembly block named {raw.Name} already exists");
            }

            foreach (var (cls, scope) in classScopes)
            {
                file = cls.File;
                DeclareNames(scope, CollectDeclarations(cls.Body), isParameter: false);
                foreach (MethodDecl m in cls.Methods) DeclareMethod(scope, m);
            }

            AssignVariableLabels();

            foreach (var (cls, scope) in classScopes)
            {
                file = cls.File;
                BindStatements(cls.Body, scope, classLevel: true);

                foreach (MethodDecl m in cls.Methods)
                {
                    if (m.Symbol == null) continue;
                    Scope ms = methodScopes[m];
                    foreach (VarDecl p in m.Parameters)
                    {
                        BindVarDecl(p, ms, classLevel: false);
                        // a default is worked out by the caller, which can't see this method's variables
                        Expr? local = p.Values.SelectMany(SyntaxWalker.Nodes).OfType<NameExpr>()
                            .FirstOrDefault(n => n.Symbol is VariableSymbol { IsLocal: true } or FieldPathSymbol { Root.IsLocal: true } or SelfFieldSymbol or PathSymbol { Root.IsLocal: true });
                        if (local != null)
                            Error(local.Span, $"a parameter's default can't use the method's own variables or parameters ('{((NameExpr)local).Name}')");
                    }
                    blockMethod = m.Symbol;
                    blockNames.Add(new());
                    BindStatements(m.Body, ms, classLevel: false);
                    ReturnTypeOf(m.Symbol);
                    blockNames.Clear();
                    blockMethod = null;
                }
            }

            WorkOutResultCapacities();

            foreach (var (cls, scope) in classScopes)
                foreach (var (name, symbol) in scope.Names)
                    units[scope.Unit].ClassNames.TryAdd(name, symbol);

            if (boot != null && boot.UsedBuiltins.Any(b => b.Name == "Bootloader.JumpToSectorTwo") && os == null)
            {
                file = boot.Classes[0].File;
                Error(boot.Classes[0].BaseSpan, $"Bootloader.JumpToSectorTwo needs a class that inherits {KernelBase} to jump to");
            }

            return new BoundProgram { File = mainFile, Boot = boot, Os = os, Kernel = kernel, Program = user };
        }

        /// <summary>
        /// What a method returns, from its [end: value]. A call may come before the method it
        /// calls, so the value is bound the first time anyone asks.
        /// </summary>
        PhiType ReturnTypeOf(MethodSymbol m)
        {
            Expr? result = m.Decl.Result;
            if (result == null || m.ResultBound) return m.ReturnType;

            if (m.ResultBinding)
            {
                // A's [end: ...] calls A, as in [end: n * (Factorial: n - 1)]: assume a number,
                // and check the assumption once the value's type is known
                m.ReturnTypeAssumed = true;
                return PhiType.Int;
            }

            m.ResultBinding = true;
            SourceFile outerFile = file;
            int outerUnsafe = unsafeDepth;
            file = m.Decl.Owner.File;
            unsafeDepth = 0;

            allowObjectTemp = true;
            BindExpr(result, methodScopes[m.Decl]);
            if (result.Type.IsString) m.ReturnType = PhiType.Str;
            else if (result.Type.IsPointer) m.ReturnType = result.Type; // a ptr<u8> result still prints as text
            else if (MovedPointer(result) is PhiType moved) m.ReturnType = moved; // [end: text + n] is still a ptr<u8>
            else if (result.Type.IsNumeric) m.ReturnType = result.Type.IsUnsigned32 ? PhiType.U32 : PhiType.Int;
            else if (result.Type.IsObject || result.Type.IsStruct)
            {
                // the caller gets an object of its own: a new local is handed over, anything else copied
                m.ReturnType = PhiType.ObjectOf(result.Type.Struct!);
                HeapMethod("Heap.Alloc", result.Span, methodScopes[m.Decl], $"returning a {result.Type}");
            }
            else if (!result.Type.IsError)
                Error(result.Span, $"a method can return a number, a str or an object, not a {result.Type}");

            if (m.ReturnTypeAssumed && (m.ReturnType.IsString || m.ReturnType.IsObject))
                Error(result.Span, $"{m.Decl.Name} returns a {m.ReturnType} and uses its own result in [end: ...]; store that result in a variable first");

            file = outerFile;
            unsafeDepth = outerUnsafe;
            m.ResultBinding = false;
            m.ResultBound = true;
            return m.ReturnType;
        }

        /// <summary>
        /// The pointer type of p + n, n + p or p - n (through more of them, as in p + i - 1):
        /// the address moves and it still points at the same kind of thing.
        /// </summary>
        static PhiType? MovedPointer(Expr e)
        {
            if (e.Type.IsPointer) return e.Type;
            if (e is not BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } b) return null;
            PhiType? left = MovedPointer(b.Left), right = MovedPointer(b.Right);
            if (left != null && right == null) return left;
            if (right != null && left == null && b.Op == BinaryOp.Add) return right;
            return null; // p - q is a distance, not a pointer
        }

        /// <summary>
        /// A str method's result is a temporary in the caller's frame, gone after the statement
        /// that called it: a pointer that keeps it would point at whatever is written there next.
        /// </summary>
        void CheckNotKeepingResult(Expr value, PhiType target, string what)
        {
            if (!target.IsPointer || !value.Type.IsString) return;
            CallExpr? call = value switch
            {
                CallExpr c => c,
                NameExpr { Call: CallExpr c } => c,
                MemberExpr { Call: CallExpr c } => c,
                _ => null,
            };
            if (call == null) return;
            string name = call.Resolved is MethodSymbol m ? m.Decl.Name : call.Callee;
            Error(value.Span, $"{name} returns a str, which only lasts until the end of this statement, so {what} can't keep a pointer to it; " +
                              $"store it in a str, or have {name} return a ptr<u8>");
        }

        /// <summary>value.Method, where value is a struct with that method.</summary>
        bool IsStructMethodName(string name, Scope scope)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0) return false;
            PhiType? t = Lookup(name[..dot], scope) switch
            {
                VariableSymbol v => v.Type,
                FieldPathSymbol f => f.Type,
                SelfFieldSymbol s => s.Type,
                PathSymbol p => p.Type,
                _ => null,
            };
            return t != null && FieldsOf(t) is StructSymbol fields && FindStructMethod(fields, name[(dot + 1)..], scope) != null;
        }

        /// <summary>The struct whose fields and methods x.name reaches: x's own, or through a ptr&lt;T&gt; to one.</summary>
        static StructSymbol? FieldsOf(PhiType t) =>
            t.Fields ?? (t.IsPointer && !t.IsArray && t.Pointee!.IsStruct ? t.Pointee.Struct : null);

        /// <summary>The method `name` of a struct, if it has one in this unit.</summary>
        MethodSymbol? FindStructMethod(StructSymbol s, string name, Scope scope) =>
            methodsByFullName.TryGetValue((scope.Unit, $"{s.Name}.{name}"), out MethodSymbol? m) && m.Decl.Owner.ForStruct?.Symbol == s ? m : null;

        /// <summary>addr receiver, for a struct method's self.</summary>
        Expr SelfArgument(Expr receiver, Scope scope)
        {
            if (receiver.Type.IsPointer) return receiver; // call p.Add; already has the address
            var self = new AddrExpr { Operand = receiver, Span = receiver.Span };
            BindAddr(self, scope);
            return self;
        }

        /// <summary>What a method takes and returns, as a method&lt;...&gt; type would say it.</summary>
        MethodSignature SignatureOf(MethodSymbol m)
        {
            PhiType result = ReturnTypeOf(m);
            return new MethodSignature(m.Parameters.Select(p => p.Type.IsString ? PhiType.Str : p.Type).ToList(),
                                       result.IsNumeric ? PhiType.Int : result);
        }

        // ---------------------------------------------------------------- str results

        /// <summary>
        /// The room a str-returning method needs for its result: enough for the largest of the
        /// values it can return. The caller passes a buffer this big (see the code generator).
        /// </summary>
        void WorkOutResultCapacities()
        {
            foreach (BoundUnit unit in units.Values)
                foreach (MethodSymbol m in unit.Methods)
                    if (m.ReturnType.IsString) ResultCapacityOf(m, new HashSet<MethodSymbol>());
        }

        /// <summary>A method that returns its own result (directly or through others) gets a default.</summary>
        const int DefaultResultCapacity = 256;

        int ResultCapacityOf(MethodSymbol m, HashSet<MethodSymbol> busy)
        {
            if (m.ResultCapacity > 0) return m.ResultCapacity;
            if (!busy.Add(m)) return DefaultResultCapacity;

            var values = new List<Expr>(m.ReturnValues);
            if (m.Decl.Result != null) values.Add(m.Decl.Result);
            int capacity = values.Select(v => StrCapacity(v, busy)).DefaultIfEmpty(1).Max();

            busy.Remove(m);
            m.ResultCapacity = capacity;
            return capacity;
        }

        /// <summary>The bytes (with the zero) a str-valued expression can need.</summary>
        int StrCapacity(Expr e, HashSet<MethodSymbol> busy) => e switch
        {
            StringExpr s => s.Value.Length + 1,
            NameExpr { Symbol: VariableSymbol v } => v.Capacity,
            NameExpr { Call: CallExpr { Resolved: MethodSymbol m } } => ResultCapacityOf(m, busy),
            IndexExpr { Target: NameExpr { Symbol: VariableSymbol v } } => v.Capacity,
            CallExpr { Resolved: MethodSymbol m } => ResultCapacityOf(m, busy),
            _ when e.Type.IsNumeric => 12, // a number written out as text
            _ => DefaultResultCapacity,
        };


        // ---------------------------------------------------------------- structs

        void DeclareStructs(List<StructDecl> decls)
        {
            foreach (StructDecl d in decls)
            {
                file = d.File;
                if (Parser.ReservedWords.Contains(d.Name))
                {
                    Error(d.Span, Parser.KeywordNameMessage(d.Name, "struct"));
                    continue;
                }
                var symbol = new StructSymbol { Name = d.Name, Decl = d };
                if (!structs.TryAdd(d.Name, symbol))
                {
                    Error(d.Span, $"a struct named {d.Name} already exists");
                    continue;
                }
                d.Symbol = symbol;
            }

            foreach (StructSymbol s in structs.Values) Layout(s, new HashSet<StructSymbol>());
            foreach (StructSymbol s in structs.Values) FindOwnedSlots(s);
        }

        /// <summary>
        /// The objects a struct owns: its new fields, and those of the structs stored inside it.
        /// A struct that owns objects can't be an array element, because PHI frees and copies
        /// what it owns one slot at a time.
        /// </summary>
        void FindOwnedSlots(StructSymbol s)
        {
            if (!slotsDone.Add(s)) return;
            file = s.Decl.File;
            foreach (FieldSymbol f in s.Fields)
            {
                if (f.Type.IsObject)
                {
                    s.OwnedSlots.Add(new OwnedSlot(f.Offset, f.Type.Struct!));
                    continue;
                }
                if (f.Type.Kind != TypeKind.Struct) continue;

                StructSymbol inner = f.Type.Struct!;
                FindOwnedSlots(inner);
                file = s.Decl.File;
                if (!inner.OwnsObjects) continue;
                if (f.Type.IsArray)
                {
                    FieldDecl decl = s.Decl.Fields.First(d => d.Name == f.Name);
                    Error(decl.Span, $"{inner.Name} owns objects (it has new fields), so it can't be an array element yet");
                    continue;
                }
                foreach (OwnedSlot inside in inner.OwnedSlots)
                    s.OwnedSlots.Add(inside with { Offset = f.Offset + inside.Offset });
            }
        }

        /// <summary>Works out field offsets; fields follow each other with no padding.</summary>
        void Layout(StructSymbol s, HashSet<StructSymbol> inProgress)
        {
            if (s.LayoutDone) return;
            file = s.Decl.File;

            if (!inProgress.Add(s))
            {
                Error(s.Decl.Span, $"struct {s.Name} contains itself; use a new {s.Name} field (an object it owns) or a ptr<{s.Name}> field instead");
                s.LayoutDone = true;
                return;
            }

            int offset = 0;
            foreach (FieldDecl f in s.Decl.Fields)
            {
                file = s.Decl.File;
                if (s.Find(f.Name) != null)
                {
                    Error(f.Span, $"struct {s.Name} already has a field named {f.Name}");
                    continue;
                }

                PhiType type = ResolveType(f.Type, scope: null, allowStr: false);
                if (f.IsNew)
                {
                    // a separate object: the field holds its address
                    if (type.IsError) continue;
                    if (!type.IsStruct)
                    {
                        Error(f.Type.Span, $"new is for structs; a {type} field is stored in the struct itself, so leave out new");
                        continue;
                    }
                    if (f.Count != null)
                    {
                        Error(f.Count.Span, "a field of objects can't be an array yet");
                        continue;
                    }
                    s.Fields.Add(new FieldSymbol { Name = f.Name, Type = PhiType.ObjectOf(type.Struct!), Offset = offset });
                    offset += 4;
                    continue;
                }
                if (type.IsStruct) Layout(type.Struct!, inProgress);

                if (f.Count != null)
                {
                    int? count = ConstantCount(f.Count, null);
                    if (count == null) continue;
                    type = type.ArrayOf(count.Value);
                }

                int size = type.ElementSize * Math.Max(type.Length, 1);
                s.Fields.Add(new FieldSymbol { Name = f.Name, Type = type, Offset = offset });
                offset += size;
            }

            s.Size = offset;
            s.LayoutDone = true;
            inProgress.Remove(s);
        }

        /// <summary>Turns a written type into a PhiType. str is only allowed for variables.</summary>
        PhiType ResolveType(TypeRef t, Scope? scope, bool allowStr)
        {
            switch (t.Name)
            {
                case "int":
                case "i32": return PhiType.I32;
                case "byt":
                case "u8": return PhiType.U8;
                case "bln": return PhiType.Bool;
                case "u16": return PhiType.U16;
                case "u32": return PhiType.U32;
                case "i8": return PhiType.I8;
                case "i16": return PhiType.I16;
                case "str":
                    if (allowStr) return PhiType.Str;
                    Error(t.Span, "str can't be used here; use a u8 array (u8 name[16]) or ptr<u8>");
                    return PhiType.Error;
                case "dec":
                case "fin":
                    Error(t.Span, $"{t.Name} (decimal numbers) isn't supported yet");
                    return PhiType.Error;
                case "var":
                    Error(t.Span, "var needs a value to work out its type");
                    return PhiType.Error;
                case "ptr":
                {
                    PhiType pointee = ResolveType(t.Pointee!, scope, allowStr: false);
                    return pointee.IsError ? PhiType.Error : PhiType.PointerTo(pointee);
                }
                case "method":
                {
                    // a struct parameter is the caller's object
                    var parameters = t.Parameters.Select(p => ResolveType(p, scope, allowStr: true))
                        .Select(p => p.IsStruct ? PhiType.ObjectOf(p.Struct!) : p).ToList();
                    PhiType result = t.Result == null ? PhiType.Void : ResolveType(t.Result, scope, allowStr: true);
                    if (parameters.Any(p => p.IsError) || result.IsError) return PhiType.Error;
                    if (result.IsString)
                    {
                        Error(t.Result!.Span, "a method called through a variable can't return a str yet; return a number");
                        return PhiType.Error;
                    }
                    if (!result.IsNumeric && result.Kind != TypeKind.Void)
                    {
                        Error(t.Result!.Span, $"a method returns a number or a str, not a {result}");
                        return PhiType.Error;
                    }
                    return PhiType.MethodOf(new MethodSignature(parameters, result.IsNumeric ? PhiType.Int : result));
                }
            }

            if (structs.TryGetValue(t.Name, out StructSymbol? s)) return PhiType.Of(s);

            Error(t.Span, $"unknown type '{t.Name}'");
            return PhiType.Error;
        }

        /// <summary>An element count that must be known when the program is built.</summary>
        int? ConstantCount(Expr e, Scope? scope)
        {
            if (scope != null) BindExpr(e, scope);
            else if (e is NumberExpr) e.Type = PhiType.Int;

            long? value = ConstantFolder.Evaluate(e);
            if (value == null)
            {
                Error(e.Span, "a size must be a number or a const known when the program is built");
                return null;
            }
            if (value < 1 || value > 0x4000)
            {
                Error(e.Span, "a size must be between 1 and 16384");
                return null;
            }
            return (int)value;
        }

        // ---------------------------------------------------------------- variables and methods

        /// <summary>Every declaration in a body, including those nested in blocks.</summary>
        IEnumerable<Stmt> CollectDeclarations(IEnumerable<Stmt> body)
        {
            foreach (Stmt s in body)
            {
                switch (s)
                {
                    case VarDecl or ConstDecl:
                        yield return s;
                        break;
                    case IfStmt i:
                        foreach (Stmt v in CollectDeclarations(i.Then)) yield return v;
                        if (i.Else != null)
                            foreach (Stmt v in CollectDeclarations(i.Else)) yield return v;
                        break;
                    case UnsafeStmt u:
                        foreach (Stmt v in CollectDeclarations(u.Body)) yield return v;
                        break;
                    case WhileStmt w:
                        if (w.Init != null)
                        {
                            loopCounters.Add(w.Init);
                            yield return w.Init;
                        }
                        foreach (Stmt v in CollectDeclarations(w.Body)) yield return v;
                        break;
                }
            }
        }

        /// <summary>A block of a method (its body, a branch of an if, a loop), for which declarations can see each other.</summary>
        sealed class Block
        {
            public Block? Parent { get; init; }
            public bool Within(Block other) => this == other || (Parent?.Within(other) ?? false);
        }

        /// <summary>Like CollectDeclarations, with the block each declaration is in. A loop's counter is in the loop.</summary>
        IEnumerable<(Stmt Decl, Block Block)> CollectBlockDeclarations(IEnumerable<Stmt> body, Block block)
        {
            foreach (Stmt s in body)
            {
                switch (s)
                {
                    case VarDecl or ConstDecl:
                        yield return (s, block);
                        break;
                    case IfStmt i:
                        foreach (var d in CollectBlockDeclarations(i.Then, new Block { Parent = block })) yield return d;
                        if (i.Else != null)
                            foreach (var d in CollectBlockDeclarations(i.Else, new Block { Parent = block })) yield return d;
                        break;
                    case UnsafeStmt u:
                        foreach (var d in CollectBlockDeclarations(u.Body, block)) yield return d;
                        break;
                    case WhileStmt w:
                        var loop = new Block { Parent = block };
                        if (w.Init != null)
                        {
                            loopCounters.Add(w.Init);
                            yield return (w.Init, loop);
                        }
                        foreach (var d in CollectBlockDeclarations(w.Body, loop)) yield return d;
                        break;
                }
            }
        }

        /// <summary>
        /// A method's own variables. A name can be declared again where the earlier declaration
        /// is out of sight (its block has ended); each declaration is then a variable of its own.
        /// </summary>
        void DeclareLocals(Scope scope, List<Stmt> body)
        {
            var declaredIn = new Dictionary<string, List<(VariableSymbol Symbol, Block Block)>>();

            foreach (var (stmt, block) in CollectBlockDeclarations(body, new Block()).ToList())
            {
                if (stmt is not VarDecl decl || !declaredIn.TryGetValue(decl.Name, out var earlier))
                {
                    DeclareNames(scope, new[] { stmt }, isParameter: false);
                    if (stmt is VarDecl { Symbol: VariableSymbol first } d && !first.IsParameter)
                        declaredIn[d.Name] = new() { (first, block) };
                    continue;
                }

                // two loops can each declare the same counter: while int i: 0; ...
                if (loopCounters.Contains(decl) && earlier.FirstOrDefault(e => loopCounters.Contains(e.Symbol.Decl)
                        && e.Symbol.Decl.Type.ToString() == decl.Type.ToString() && e.Symbol.Decl.BufferSize == null).Symbol is VariableSymbol counter)
                {
                    decl.Symbol = counter;
                    counter.NeedsInitCode = true;
                    continue;
                }

                if (earlier.Any(e => block.Within(e.Block)))
                {
                    Error(decl.NameSpan, $"'{decl.Name}' is already declared in {ScopeName(scope)}; a name can be declared again only in a separate block (another branch or loop)");
                    continue;
                }

                // the earlier declarations' blocks have ended: this one is a variable of its own
                if (blockScoped.Add(earlier[0].Symbol))
                {
                    scope.Names.Remove(decl.Name);
                    scope.Names[$"{decl.Name}#0"] = earlier[0].Symbol;
                }
                var symbol = new VariableSymbol { Name = decl.Name, Decl = decl, Unit = scope.Unit };
                decl.Symbol = symbol;
                scope.Names[$"{decl.Name}#{earlier.Count}"] = symbol;
                blockScoped.Add(symbol);
                earlier.Add((symbol, block));
                units[scope.Unit].Variables.Add(symbol);
                DetermineStorage(symbol, scope);
            }
        }

        /// <summary>A block-scoped variable comes into sight where it's declared, until its block ends.</summary>
        void ShowInBlock(VarDecl decl)
        {
            if (decl.Symbol is VariableSymbol v && blockScoped.Contains(v) && blockNames.Count > 0)
                blockNames[^1][decl.Name] = v;
        }

        void BindBlock(List<Stmt> body, Scope scope)
        {
            blockNames.Add(new());
            BindStatements(body, scope, classLevel: false);
            blockNames.RemoveAt(blockNames.Count - 1);
        }

        void DeclareNames(Scope scope, IEnumerable<Stmt> decls, bool isParameter)
        {
            foreach (Stmt stmt in decls)
            {
                if (stmt is ConstDecl c)
                {
                    DeclareConstant(scope, c);
                    continue;
                }

                var decl = (VarDecl)stmt;
                if (Parser.ReservedWords.Contains(decl.Name))
                {
                    Error(decl.NameSpan, Parser.KeywordNameMessage(decl.Name, "variable"));
                    continue;
                }

                if (scope.Names.TryGetValue(decl.Name, out Symbol? existing))
                {
                    // two loops can each declare the same counter: while int i: 0; ...
                    if (existing is VariableSymbol ev && loopCounters.Contains(decl) && ev.Decl.Type.ToString() == decl.Type.ToString() && ev.Decl.BufferSize == null)
                    {
                        decl.Symbol = ev;
                        ev.NeedsInitCode = true;
                        continue;
                    }
                    Error(decl.NameSpan, $"'{decl.Name}' is already declared in {ScopeName(scope)}");
                    continue;
                }

                var symbol = new VariableSymbol { Name = decl.Name, Decl = decl, Unit = scope.Unit, IsParameter = isParameter };
                decl.Symbol = symbol;
                scope.Names[decl.Name] = symbol;
                units[scope.Unit].Variables.Add(symbol);
                DetermineStorage(symbol, scope);

                if (isParameter) scope.Method!.Parameters.Add(symbol);
            }
        }

        void DeclareConstant(Scope scope, ConstDecl c)
        {
            if (Parser.ReservedWords.Contains(c.Name))
            {
                Error(c.NameSpan, Parser.KeywordNameMessage(c.Name, "constant"));
                return;
            }
            if (scope.Names.ContainsKey(c.Name))
            {
                Error(c.NameSpan, $"'{c.Name}' is already declared in {ScopeName(scope)}");
                return;
            }

            PhiType type = ResolveType(c.Type, scope, allowStr: false);
            BindExpr(c.Value, scope);
            if (type.IsError || c.Value.Type.IsError) return;

            if (!type.IsNumeric)
            {
                Error(c.Type.Span, "constants must be numbers");
                return;
            }

            Expr value = Convert(c.Value, type, $"constant '{c.Name}'");
            long? known = ConstantFolder.Evaluate(value);
            if (known == null)
            {
                Error(c.Value.Span, $"the value of constant '{c.Name}' must be known when the program is built");
                return;
            }

            scope.Names[c.Name] = new ConstantSymbol { Name = c.Name, Value = known.Value, Type = type };
        }

        static string ScopeName(Scope scope) =>
            scope.Method != null ? $"method {scope.Method.Name}" : $"class {scope.Class.Name}";

        void DeclareMethod(Scope classScope, MethodDecl method)
        {
            string name = method.Name;
            bool dotted = name.Contains('.');
            HookInfo? hook = dotted ? Builtins.FindHook(name) : null;

            if (dotted && hook == null)
            {
                string hooks = string.Join(", ", Builtins.Hooks.Select(h => h.Name));
                Error(method.Span, $"'{name}' isn't an event PHI knows; method names can't contain '.' unless they are one of: {hooks}");
                return;
            }

            if (!dotted && Parser.ReservedWords.Contains(name))
            {
                Error(method.Span, Parser.KeywordNameMessage(name, "method"));
                return;
            }

            if (method.IsInterruptHandler)
            {
                if (classScope.Unit == UnitKind.Program) Error(method.Span, "user programs can't handle interrupts");
                if (dotted) Error(method.Span, "an event can't also be an isr");
                if (method.Parameters.Count > 0) Error(method.Parameters[0].Span, "interrupt handlers can't take parameters");
                if (method.Result != null) Error(method.Result.Span, "interrupt handlers can't return a value");
            }

            if (hook != null && method.Parameters.Count > 0)
                Error(method.Parameters[0].Span, "events can't take parameters; the library calls them without any");

            UnitKind unit = classScope.Unit;
            string fullName = dotted ? name : $"{classScope.Class.Name}.{name}";
            string label = hook?.Label ?? $"M_{classScope.Class.Name}_{name}";

            var symbol = new MethodSymbol
            {
                Name = fullName,
                Label = label,
                Decl = method,
                Unit = unit,
                IsHook = hook != null,
                IsInterruptHandler = method.IsInterruptHandler,
            };

            if (!methodsByFullName.TryAdd((unit, fullName), symbol))
            {
                Error(method.Span, $"method {fullName} is already defined");
                return;
            }

            method.Symbol = symbol;
            units[unit].Methods.Add(symbol);

            var scope = new Scope { Class = classScope.Class, Unit = unit, Method = symbol };
            methodScopes[method] = scope;
            DeclareNames(scope, method.Parameters, isParameter: true);
            DeclareLocals(scope, method.Body);
        }

        /// <summary>Decides type, element count and str capacity from the declaration.</summary>
        void DetermineStorage(VariableSymbol v, Scope scope)
        {
            VarDecl d = v.Decl;
            PhiType type;

            if (d.Type.Name == "var")
            {
                if (d.Values.Count == 0)
                {
                    Error(d.Type.Span, "var needs a value to work out its type");
                    return;
                }
                type = d.Values.All(e => e is StringExpr) ? PhiType.Str : PhiType.Int;
            }
            else
            {
                type = ResolveType(d.Type, scope, allowStr: true);
            }

            if (type.IsError)
            {
                v.Type = PhiType.Error;
                return;
            }

            if (d.IsNew)
            {
                DetermineObject(v, type, scope);
                return;
            }

            if (type.IsStruct && v.IsParameter)
            {
                // a struct parameter borrows the caller's struct for the call: no copy
                if (d.Values.Count > 0) Error(d.Values[0].Span, $"a {type} parameter can't have a default; leave out the ':' and value");
                v.Type = PhiType.ObjectOf(type.Struct!);
                v.Ownership = Ownership.Borrowed;
                return;
            }

            if (type.IsStruct && type.Struct!.OwnsObjects)
            {
                Error(d.Type.Span, $"{type} owns objects (it has new fields), so PHI has to manage it: declare it with new, like new {type} {d.Name};");
                v.Type = PhiType.Error;
                return;
            }

            if (d.BufferSize != null)
            {
                int? size = ConstantCount(d.BufferSize, scope);
                if (size == null) return;

                if (type.IsString)
                {
                    v.Type = PhiType.Str;
                    v.Capacity = size.Value + 1;
                }
                else
                {
                    v.Type = type.ArrayOf(size.Value);
                }
                return;
            }

            if (type.IsStruct && d.Values.Count > 0)
            {
                Error(d.Values[0].Span, $"a {type} starts as all zeros; set its fields one at a time, like {d.Name}.field is 5;");
                v.Type = PhiType.Error;
                return;
            }

            if (type.IsString)
            {
                if (d.Values.Count == 0)
                {
                    Error(d.NameSpan, $"str '{d.Name}' needs a value or a size, like str {d.Name}: [40];");
                    v.Type = PhiType.Error;
                    return;
                }

                if (d.Values.Count > 1)
                {
                    // every element gets the same room, so any element can be replaced by index
                    v.Type = PhiType.Str.ArrayOf(d.Values.Count);
                    v.Capacity = d.Values.Max(e => e is StringExpr s ? s.Value.Length + 1 : IntTextCapacity);
                    return;
                }

                v.Type = PhiType.Str;
                v.Capacity = d.Values[0] switch
                {
                    StringExpr s => s.Value.Length + 1,
                    NameExpr n when Lookup(n.Name, scope) is VariableSymbol { Type.IsString: true } source =>
                        source.Capacity > 0 ? source.Capacity : DefaultStrCapacity,
                    NameExpr n when Lookup(n.Name, scope) is VariableSymbol { Type: { IsPointer: true, Pointee: var p } } && p == PhiType.U8 =>
                        DefaultStrCapacity, // the text a ptr<u8> points at
                    IndexExpr => DefaultStrCapacity, // could be an element of a str array
                    _ => IntTextCapacity,            // a number, written out as text
                };
                return;
            }

            v.Type = d.Values.Count > 1 ? type.ArrayOf(d.Values.Count) : type;
        }

        /// <summary>new T name ...: an object, which PHI allocates and frees, or one that's already at an address.</summary>
        void DetermineObject(VariableSymbol v, PhiType type, Scope scope)
        {
            VarDecl d = v.Decl;
            v.Type = PhiType.Error;

            if (!type.IsStruct)
            {
                Error(d.Type.Span, $"new is for structs; a {type} is stored where it's declared, so leave out new: {type} {d.Name};");
                return;
            }
            if (d.BufferSize != null)
            {
                Error(d.BufferSize.Span, "arrays of objects aren't supported yet");
                return;
            }

            if (d.AtAddress != null)
            {
                if (scope.Unit == UnitKind.Program)
                {
                    Error(d.AtAddress.Span, "a user program can't place an object at an address; only the kernel reaches memory directly");
                    return;
                }
                if (type.Struct!.OwnsObjects)
                {
                    Error(d.AtAddress.Span, $"{type} owns objects (it has new fields), which PHI can't manage in memory it didn't allocate");
                    return;
                }
                v.Ownership = Ownership.At;
            }
            else v.Ownership = Ownership.Owned;

            v.Type = PhiType.ObjectOf(type.Struct!);
        }

        /// <summary>
        /// Class variables are VALUE_name (so asm blocks can use {name}); if two classes in the
        /// same unit use a name, both get the class added. Method variables are always qualified.
        /// </summary>
        void AssignVariableLabels()
        {
            foreach (BoundUnit unit in units.Values)
            {
                var classLevel = classScopes.Values.Where(s => s.Unit == unit.Kind)
                    .SelectMany(s => s.Names.Values.OfType<VariableSymbol>()).ToList();
                var shared = classLevel.GroupBy(v => v.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

                foreach (var (cls, scope) in classScopes.Where(p => p.Value.Unit == unit.Kind))
                    foreach (VariableSymbol v in scope.Names.Values.OfType<VariableSymbol>())
                        v.Label = shared.Contains(v.Name) ? $"VALUE_{cls.Name}_{v.Name}" : $"VALUE_{v.Name}";

                foreach (var (method, scope) in methodScopes.Where(p => p.Value.Unit == unit.Kind))
                    foreach (var (key, symbol) in scope.Names)
                    {
                        if (symbol is not VariableSymbol v) continue;
                        // the code generator gives it a place in the method's stack frame
                        v.Label = $"VALUE_{method.Symbol!.Label}_{key.Replace('#', '_')}";
                        v.IsLocal = true;
                        if (!v.IsParameter) method.Symbol.Locals.Add(v);
                    }
            }
        }

        // ================================================================ lookup

        Symbol? LookupSimple(string name, Scope scope)
        {
            Symbol? here = LookupHere(name, scope);
            if (here != null) return here;

            if (OtherClassWith(name, scope) is ClassDecl other)
                return classScopes[other].Names[name];

            if (Builtins.Constants.TryGetValue(name, out long value))
                return new ConstantSymbol { Name = name, Value = value };

            // ClassName.name
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                ClassDecl? cls = classScopes.Keys.FirstOrDefault(c => c.Name == name[..dot] && classScopes[c].Unit == scope.Unit);
                if (cls != null && classScopes[cls].Names.TryGetValue(name[(dot + 1)..], out Symbol? qualified))
                    return qualified;
            }

            return null;
        }

        /// <summary>A name from another class in the same unit, if only one class has it.</summary>
        ClassDecl? OtherClassWith(string name, Scope scope)
        {
            var others = classScopes.Where(c => c.Value.Unit == scope.Unit && c.Key != scope.Class && c.Value.Names.ContainsKey(name)).ToList();
            return others.Count == 1 ? others[0].Key : null;
        }

        /// <summary>
        /// A name written without its class that is found in another file's class: convenient,
        /// but a typo or a missing declaration binds to it without a word, so it's a warning.
        /// Once per name in each class.
        /// </summary>
        void WarnOtherClass(string name, Span span, Scope scope)
        {
            string head = name.Split('.')[0];
            if (LookupHere(head, scope) != null || OtherClassWith(head, scope) is not ClassDecl other) return;
            if (other.File == file || classScopes[other].Names[head] is not VariableSymbol) return;
            if (!otherClassWarned.Add((scope.Class, head))) return;
            diagnostics.Warning(file, span, $"'{head}' is a variable of {other.Name}, in {Path.GetFileName(other.File.Path)}; write {other.Name}.{head} to say so");
        }

        readonly HashSet<(ClassDecl, string)> otherClassWarned = new();

        /// <summary>A name in the block, the method, self's fields or the class: everything but other classes.</summary>
        Symbol? LookupHere(string name, Scope scope)
        {
            if (scope.Method != null && scope.Method == blockMethod)
                for (int i = blockNames.Count - 1; i >= 0; i--)
                    if (blockNames[i].TryGetValue(name, out VariableSymbol? inBlock))
                        return inBlock;

            if (scope.Method != null && methodScopes.TryGetValue(scope.Method.Decl, out Scope? ms)
                && ms.Names.TryGetValue(name, out Symbol? local))
                return local;

            // in a struct's method, its fields by name
            if (scope.Class.ForStruct?.Symbol is StructSymbol owner && scope.Method is { Parameters.Count: > 0 } method
                && owner.Find(name) is FieldSymbol field)
                return new SelfFieldSymbol { Name = name, Self = method.Parameters[0], Offset = field.Offset, Type = field.Type };

            if (classScopes[scope.Class].Names.TryGetValue(name, out Symbol? member))
                return member;

            return null;
        }

        /// <summary>Names, plus x.len and struct fields reached by name (task.id, a.b.c).</summary>
        Symbol? Lookup(string name, Scope scope)
        {
            Symbol? direct = LookupSimple(name, scope);
            if (direct != null) return direct;

            string[] parts = name.Split('.');
            for (int split = parts.Length - 1; split >= 1; split--)
            {
                Symbol? head = LookupSimple(string.Join('.', parts[..split]), scope);
                if (head is SelfFieldSymbol sf) return ResolveSelfMembers(sf, parts[split..]);
                if (head is not VariableSymbol root) continue;
                return ResolveMembers(root, parts[split..]);
            }

            return null;
        }

        /// <summary>inner.x or items.len, where inner and items are fields of self.</summary>
        Symbol? ResolveSelfMembers(SelfFieldSymbol start, string[] members) =>
            // self holds the struct's address: one hop to the field, then on from there
            ResolvePath(start.Self, start.Name, start.Type, 0, new List<int> { start.Offset }, members);

        Symbol? ResolveMembers(VariableSymbol root, string[] members) =>
            ResolvePath(root, root.Name, root.Type, 0, null, members);

        /// <summary>
        /// Follows fields by name from a variable. Inside a struct a field is at a fixed offset;
        /// reaching into an object means reading the address it's at first (a hop).
        /// </summary>
        Symbol? ResolvePath(VariableSymbol root, string name, PhiType type, int offset, List<int>? hops, string[] members)
        {
            for (int i = 0; i < members.Length; i++)
            {
                string m = members[i];
                bool last = i == members.Length - 1;

                // a struct's own field named len comes first
                StructSymbol? fields = FieldsOf(type);
                if (m == "len" && last && fields?.Find(m) == null)
                {
                    if (type.IsArray) return new LengthSymbol { Name = m, Count = type.Length };
                    if (type.IsString && offset == 0 && hops == null) return new LengthSymbol { Name = m, Str = root };
                    return null;
                }

                // an object, or a pointer to a struct (p.x is p:0.x), holds the address its fields are at
                if (type.IsObject || type.IsPointer)
                {
                    hops ??= new List<int>();
                    hops.Add(0);
                }

                FieldSymbol? field = fields?.Find(m);
                if (field == null) return null;
                if (hops != null) hops[^1] += field.Offset;
                else offset += field.Offset;
                type = field.Type;
            }

            if (hops != null) return new PathSymbol { Name = name, Root = root, BaseOffset = offset, Hops = hops, Type = type };
            return new FieldPathSymbol { Name = name, Root = root, Offset = offset, Type = type };
        }

        /// <summary>A name used as a value that is really a method: how to call it there.</summary>
        string? MethodHint(string name, string arguments, Scope scope)
        {
            if (ResolveCallee(name, scope) is not MethodSymbol and not BuiltinFunction && !IsStructMethodName(name, scope)) return null;
            return $"'{name}' is a method; to use its result as a value, put the call in parentheses: ({name}{arguments})";
        }

        string SourceText(Expr e) => file.Text[e.Span.Start..e.Span.End];

        string DescribeMissing(string name, Scope scope)
        {
            UnitKind other = scope.Unit == UnitKind.Boot ? UnitKind.Os : UnitKind.Boot;
            string head = name.Split('.')[0];
            if (scope.Method != null && methodScopes[scope.Method.Decl].Names.ContainsKey(head + "#0"))
                return $"'{head}' is declared only inside blocks (if, else, while) that have ended here; declare it before them to use it here";
            if (name.Contains('.') && LookupSimple(head, scope) is VariableSymbol v)
                return FieldsOf(v.Type) is StructSymbol fields ? $"{fields.Name} has no field '{name[(head.Length + 1)..]}'" : $"'{head}' is a {v.Type}, which has no '{name[(head.Length + 1)..]}'";

            if (scope.Unit == UnitKind.Kernel) return $"unknown name '{name}'";
            bool inOtherUnit = classScopes.Values.Any(s => s.Unit == other && s.Names.ContainsKey(head));
            if (inOtherUnit)
            {
                string where = other == UnitKind.Boot ? $"the {BootBase} class" : $"an {KernelBase} class";
                return $"'{head}' is declared in {where}; the boot sector and the OS classes are built separately and can't share variables";
            }

            return $"unknown name '{name}'";
        }

        // ================================================================ statements

        void BindStatements(List<Stmt> body, Scope scope, bool classLevel)
        {
            for (int i = 0; i < body.Count; i++)
                body[i] = BindStatement(body[i], scope, classLevel);
        }

        Stmt BindStatement(Stmt stmt, Scope scope, bool classLevel)
        {
            switch (stmt)
            {
                case VarDecl decl:
                    BindVarDecl(decl, scope, classLevel);
                    ShowInBlock(decl);
                    return decl;

                case ConstDecl:
                    return stmt; // handled when names were declared

                case LogStmt log:
                    foreach (Expr value in log.Values)
                    {
                        BindExpr(value, scope);
                        if (value.Type.IsArray && value.Type.Element == PhiType.U8 && value is NameExpr or MemberExpr)
                            Error(value.Span, $"can't print a u8 array; to print the text in it (up to a zero byte), write addr {Describe(value)}");
                        else if (!value.Type.IsError && !value.Type.IsNumeric && !value.Type.IsString)
                            Error(value.Span, $"can't print a {value.Type}; print one element or field at a time");
                    }
                    return log;

                case AskStmt ask:
                    BindExpr(ask.Target, scope);
                    if (!ask.Target.Type.IsError && (!ask.Target.Type.IsString || ask.Target.Symbol is not VariableSymbol))
                        Error(ask.Target.Span, "ask needs a str variable to store the answer in, like: str name:[40]; ask name;");
                    return ask;

                case ExitStmt exit:
                    if (exit.Code != null)
                    {
                        BindExpr(exit.Code, scope);
                        RequireNumeric(exit.Code, "exit code");
                    }
                    return exit;

                case OutStmt o:
                    if (scope.Unit == UnitKind.Program)
                        Error(o.Span, "user programs can't use ports; only the kernel can talk to hardware");
                    BindExpr(o.Port, scope);
                    BindExpr(o.Value, scope);
                    o.Port = Convert(o.Port, PhiType.U16, "the port");
                    o.Value = Convert(o.Value, PhiType.U32, "the value");
                    return o;

                case FreeStmt f:
                    BindExpr(f.Pointer, scope);
                    if (f.Pointer.Type.IsObject)
                        Error(f.Pointer.Span, $"PHI frees {Describe(f.Pointer)} itself when it goes away; to empty a new field now, write {Describe(f.Pointer)} is empty;");
                    else if (!f.Pointer.Type.IsError && !f.Pointer.Type.IsNumeric)
                        Error(f.Pointer.Span, $"free needs a pointer from new, not a {f.Pointer.Type}");
                    else if (f.Pointer.Type is { IsPointer: true, Pointee: { IsStruct: true } pointee } && pointee.Struct!.OwnsObjects)
                        f.Owner = pointee.Struct; // its objects go with it
                    f.Releaser = HeapMethod("Heap.Free", f.Span, scope, "free");
                    return f;

                case TakesStmt takes:
                    BindTakes(takes, scope);
                    return takes;

                case UnsafeStmt u:
                    unsafeDepth++;
                    BindStatements(u.Body, scope, classLevel: false);
                    unsafeDepth--;
                    return u;

                case CallStmt call:
                    BindCall(call, scope);
                    return call;

                case IfStmt ifs:
                    BindCondition(ifs.Condition, scope);
                    BindBlock(ifs.Then, scope);
                    if (ifs.Else != null) BindBlock(ifs.Else, scope);
                    return ifs;

                case WhileStmt loop:
                    blockNames.Add(new()); // the counter is in sight only in the loop
                    if (loop.Init != null)
                    {
                        BindVarDecl(loop.Init, scope, classLevel: false);
                        ShowInBlock(loop.Init);
                    }
                    BindCondition(loop.Condition, scope);
                    if (loop.Step != null) loop.Step = BindStatement(loop.Step, scope, classLevel: false);
                    loopDepth++;
                    BindBlock(loop.Body, scope);
                    loopDepth--;
                    blockNames.RemoveAt(blockNames.Count - 1);
                    return loop;

                case BreakStmt or ContinueStmt:
                    if (loopDepth == 0)
                        Error(stmt.Span, $"{(stmt is BreakStmt ? "break" : "continue")} can only be used inside a while loop");
                    return stmt;

                case ReturnStmt ret:
                    BindReturn(ret, scope);
                    return ret;

                case AssignStmt assign:
                    return BindAssign(assign, scope);
            }

            throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}");
        }

        void BindVarDecl(VarDecl decl, Scope scope, bool classLevel)
        {
            VariableSymbol? v = decl.Symbol;
            if (v == null) return;          // already reported (e.g. a duplicate name)
            if (v.Type.IsError) return;     // already reported (e.g. dec isn't supported)

            if (decl.IsNew)
            {
                BindNewDecl(decl, v, scope);
                return;
            }

            for (int i = 0; i < decl.Values.Count; i++)
            {
                Expr value = decl.Values[i];
                BindExpr(value, scope);
                CheckNotKeepingResult(value, v.Type.Element, $"'{decl.Name}'");
                decl.Values[i] = Convert(value, v.Type.Element, $"'{decl.Name}'");
            }

            // text for a ptr<u8> is the address of the string, which only the generated code knows
            var constants = decl.Values.Select(e => e.Type.IsString && !v.Type.Element.IsString ? null : ConstantValue(e)).ToList();
            bool allConstant = constants.All(c => c != null);

            if (allConstant)
                v.StaticValues = constants.Select(c => c!).ToList();

            // a method or loop variable is set every time its declaration runs (to zero if it
            // has no value); a class variable with constant values is simply stored that way
            if (!classLevel || v.IsParameter || !allConstant)
                v.NeedsInitCode = true;
        }

        /// <summary>
        /// new T x;  new T x: 1 2;  new T x: other;  new T x at 0xB8000;
        /// The declaration runs every time it's reached: it allocates the object the first time,
        /// and after that empties and reuses it (so a loop doesn't allocate on every round).
        /// </summary>
        void BindNewDecl(VarDecl decl, VariableSymbol v, Scope scope)
        {
            v.NeedsInitCode = true;
            StructSymbol type = v.Type.Struct!;

            if (decl.AtAddress != null)
            {
                BindExpr(decl.AtAddress, scope);
                if (decl.AtAddress.Type.IsError) return;
                if (!decl.AtAddress.Type.IsNumeric)
                    Error(decl.AtAddress.Span, $"at needs an address (a number or a pointer), not a {decl.AtAddress.Type}");
                else
                    decl.AtAddress = Convert(decl.AtAddress, PhiType.U32, "the address");
                return;
            }

            if (HeapMethod("Heap.Alloc", decl.Span, scope, "new") == null) return;
            var target = new NameExpr { Name = decl.Name, Span = decl.NameSpan, Symbol = v, Type = v.Type };

            // new Point b: a;  new Point p: (Make: 1 2);  new Node first: empty;  the whole object from one value
            if (decl.Values.Count == 1)
            {
                Expr only = decl.Values[0];
                allowObjectTemp = true;
                BindExpr(only, scope);
                allowObjectTemp = false;
                if (only.Type.IsError) return;
                if (only.Type.IsObject || only.Type.IsStruct || only is EmptyExpr)
                {
                    decl.Source = BindObjectAssign(target, only, decl.Span, scope);
                    return;
                }
            }
            else foreach (Expr value in decl.Values) BindExpr(value, scope);

            // new Point p: 1 2;  the fields in order; any left over start at zero
            List<FieldSymbol> fields = type.Fields;
            if (decl.Values.Count > fields.Count)
            {
                Error(decl.Values[fields.Count].Span, $"{type.Name} has {Plural(fields.Count, "field")}, but {decl.Values.Count} values were given");
                return;
            }
            for (int i = 0; i < decl.Values.Count; i++)
            {
                FieldSymbol field = fields[i];
                Expr value = decl.Values[i];
                if (value.Type.IsError) continue;
                if (!field.Type.IsNumeric && !field.Type.IsMethod)
                {
                    Error(value.Span, $"{type.Name}.{field.Name} is a {field.Type}, which can't be given in a list of values; set it after, like {decl.Name}.{field.Name} is ...;");
                    continue;
                }
                var fieldTarget = new NameExpr
                {
                    Name = $"{decl.Name}.{field.Name}",
                    Span = value.Span,
                    Symbol = new PathSymbol { Name = decl.Name, Root = v, Hops = new List<int> { field.Offset }, Type = field.Type },
                    Type = field.Type,
                };
                CheckNotKeepingResult(value, field.Type, $"{type.Name}.{field.Name}");
                Expr converted = Convert(value, field.Type, $"{type.Name}.{field.Name}");
                decl.FieldValues.Add(new AssignStmt { Target = fieldTarget, Op = AssignOp.Set, Value = converted, Span = value.Span });
            }
        }

        /// <summary>
        /// x is value, where x is an object or a struct: the values are copied (never the
        /// address), a call's new object is handed over, and empty frees an owned object.
        /// </summary>
        ObjectAssignStmt? BindObjectAssign(Expr target, Expr value, Span span, Scope scope)
        {
            StructSymbol type = target.Type.Struct!;
            bool owned = IsOwnedPlace(target);
            ObjectAssignKind kind;

            if (value is EmptyExpr)
            {
                if (!owned)
                {
                    Error(value.Span, $"only a new variable or new field can be emptied, and '{Describe(target)}' isn't one");
                    return null;
                }
                kind = ObjectAssignKind.Clear;
            }
            else if (value.Type.IsPointer && value.Type.Pointee?.Struct == type)
            {
                Error(value.Span, $"this is a pointer; to copy what it points at, write {Describe(value)}:0");
                return null;
            }
            else if (!(value.Type.IsObject || value.Type.IsStruct) || value.Type.Struct != type)
            {
                Error(value.Span, $"'{Describe(target)}' is a {type.Name}, so it can only be given another {type.Name}, not a {value.Type}");
                return null;
            }
            else kind = IsObjectCall(value) ? ObjectAssignKind.Move : ObjectAssignKind.Copy;

            if (kind == ObjectAssignKind.Copy && !(value is NameExpr or IndexExpr or MemberExpr))
            {
                Error(value.Span, $"can't copy a {type.Name} from this; use a variable, field or element");
                return null;
            }
            if (scope.Unit is UnitKind.Boot or UnitKind.Os)
            {
                Error(span, "copying a whole struct needs 32-bit code (a Kernel or a Program); set its fields one at a time");
                return null;
            }

            // whatever may allocate or free needs the heap
            if (owned || kind != ObjectAssignKind.Copy || type.OwnsObjects)
            {
                if (HeapMethod("Heap.Alloc", span, scope, $"copying a {type.Name}") == null) return null;
            }

            return new ObjectAssignStmt { Target = target, Value = value, Kind = kind, Struct = type, TargetOwned = owned, Span = span };
        }

        /// <summary>A call whose result is a new object, which the caller takes over.</summary>
        internal static bool IsObjectCall(Expr e) => e.Type.IsObject && e is CallExpr or NameExpr { Call: not null } or MemberExpr { Call: not null };

        /// <summary>Set just before binding a value that may be a call returning an object (the only places those can go).</summary>
        bool allowObjectTemp;

        /// <summary>A long, a string, or null if the value isn't known at compile time.</summary>
        static object? ConstantValue(Expr e) => e switch
        {
            StringExpr s when e.Type.IsString => s.Value,
            _ => ConstantFolder.Evaluate(e),
        };

        static bool IsAssignable(Expr target) => target switch
        {
            NameExpr { Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol } => true,
            IndexExpr or MemberExpr => true,
            _ => false,
        };

        Stmt BindAssign(AssignStmt assign, Scope scope)
        {
            BindExpr(assign.Target, scope);
            bool toStr = assign.Target.Type.IsString && !assign.Target.Type.IsArray;
            if ((assign.Values is { Count: > 1 } && (toStr || !assign.ValueIsWhole)) || (toStr && assign.Op == AssignOp.Add))
                return BindStringBuild(assign, scope);

            allowObjectTemp = assign.Target.Type.IsObject || assign.Target.Type.IsStruct;
            BindExpr(assign.Value, scope);
            allowObjectTemp = false;

            Expr target = assign.Target;
            if (target.Type.IsError || assign.Value.Type.IsError) return assign;

            // objects (and whole structs) are copied, never shared
            if ((target.Type.IsObject || target.Type.IsStruct) && IsAssignable(target))
            {
                if (assign.Op != AssignOp.Set)
                {
                    Error(assign.Span, $"{OpText(assign.Op)} only works on numbers, and '{Describe(target)}' is a {target.Type}");
                    return assign;
                }
                return BindObjectAssign(target, assign.Value, assign.Span, scope) ?? (Stmt)assign;
            }

            if (!IsAssignable(target))
            {
                Error(target.Span, "only variables, fields and array elements can be assigned");
                return assign;
            }

            if (target.Type.IsArray)
            {
                Error(target.Span, $"can't assign a whole array; set one element at a time, like {Describe(target)}:0 is 5;");
                return assign;
            }

            if (target.Type.IsStruct)
            {
                Error(target.Span, $"can't assign a whole {target.Type}; set its fields one at a time");
                return assign;
            }

            if (assign.Op == AssignOp.Power)
            {
                Error(assign.Span, "^^ (power) isn't supported yet");
                return assign;
            }

            Expr value = assign.Value;

            if (assign.Op != AssignOp.Set)
            {
                if (!target.Type.IsNumeric)
                {
                    Error(assign.Span, $"{OpText(assign.Op)} only works on numbers, and '{Describe(target)}' is a {target.Type}");
                    return assign;
                }

                value = Convert(value, PhiType.Int, "the right side");
                var op = assign.Op switch
                {
                    AssignOp.Add => BinaryOp.Add,
                    AssignOp.Subtract => BinaryOp.Subtract,
                    AssignOp.Multiply => BinaryOp.Multiply,
                    AssignOp.Divide => BinaryOp.Divide,
                    _ => BinaryOp.Modulo,
                };
                var combined = new BinaryExpr { Op = op, Left = target, Right = value, Span = assign.Span };
                combined.Type = ArithmeticType(target.Type, value.Type);
                value = combined;
            }
            else
            {
                CheckNotKeepingResult(value, target.Type, $"'{Describe(target)}'");
                value = Convert(value, target.Type, $"'{Describe(target)}'");
            }

            return new AssignStmt { Target = target, Op = AssignOp.Set, Value = value, Span = assign.Span };
        }

        /// <summary>s is a b c; or s++ a b; with s a str: each value written in as text, one after another.</summary>
        Stmt BindStringBuild(AssignStmt assign, Scope scope)
        {
            Expr target = assign.Target;
            bool append = assign.Op == AssignOp.Add;
            List<Expr> parts = assign.Values ?? new List<Expr> { assign.Value };
            foreach (Expr part in parts) BindExpr(part, scope);

            if (target.Type.IsError) return assign;
            if (!target.Type.IsString || target.Type.IsArray || !IsAssignable(target))
            {
                Error(parts.Count > 1 ? parts[1].Span : assign.Span,
                      $"'{Describe(target)}' ({target.Type}) takes one value; only a str can be built from several");
                return assign;
            }
            if (assign.Bare)
            {
                Error(assign.Span, $"'{Describe(target)}' is a str: write what to add to its end, like {Describe(target)}++ ' more';");
                return assign;
            }

            var build = new StringBuildStmt { Target = target, Append = append, Span = assign.Span };
            for (int i = 0; i < parts.Count; i++)
            {
                Expr part = parts[i];
                if (part.Type.IsError) continue;
                if (part.Type.IsArray && part.Type.Element == PhiType.U8 && part is NameExpr or MemberExpr)
                    Error(part.Span, $"can't write a u8 array into a str; for the text in it (up to a zero byte), write addr {Describe(part)}");
                else if (!part.Type.IsNumeric && !part.Type.IsString)
                    Error(part.Span, $"can't write a {part.Type} into a str; write one element or field at a time");

                // the str is being rewritten, so reading it after that would read the new text
                else if ((append || i > 0) && part is NameExpr or IndexExpr && Describe(part) == Describe(target))
                    Error(part.Span, i == 0
                        ? $"'{Describe(target)}' can't be added to itself; copy it to another str first"
                        : $"'{Describe(target)}' is being rewritten here, so it can only be the first value; write {Describe(target)}++ ... to add to its end");
                build.Parts.Add(part);
            }
            return build;
        }

        /// <summary>
        /// True for a place that holds an object PHI owns: a new variable, or a new field
        /// (every object field is one). Those can be allocated, handed over and freed.
        /// </summary>
        internal static bool IsOwnedPlace(Expr e) => e.Type.IsObject && e switch
        {
            NameExpr { Symbol: VariableSymbol v } => v.Ownership == Ownership.Owned,
            NameExpr { Symbol: FieldPathSymbol or SelfFieldSymbol or PathSymbol } => true,
            MemberExpr { Call: null } => true,
            _ => false,
        };

        /// <summary>dest takes source;  both must be places that own their object.</summary>
        void BindTakes(TakesStmt takes, Scope scope)
        {
            BindExpr(takes.Target, scope);
            BindExpr(takes.Source, scope);
            if (takes.Target.Type.IsError || takes.Source.Type.IsError) return;

            foreach (Expr side in new[] { takes.Target, takes.Source })
            {
                if (IsOwnedPlace(side)) continue;
                string why = side switch
                {
                    NameExpr { Symbol: VariableSymbol { Ownership: Ownership.Borrowed } } => "a parameter, which borrows its object",
                    NameExpr { Symbol: VariableSymbol { Ownership: Ownership.At } } => "a view of memory at an address",
                    _ when side.Type.IsStruct => $"a {side.Type} stored in place, not a new object",
                    _ when side.Type.IsObject => $"not a variable or field that owns its object",
                    _ => $"a {side.Type}, not an object",
                };
                Error(side.Span, $"takes works between new variables and new fields; '{Describe(side)}' is {why}");
                return;
            }

            if (takes.Target.Type.Struct != takes.Source.Type.Struct)
            {
                Error(takes.Source.Span, $"'{Describe(takes.Target)}' holds a {takes.Target.Type}, but '{Describe(takes.Source)}' is a {takes.Source.Type}");
                return;
            }

            // a.next takes a would make a own itself
            string target = Describe(takes.Target), source = Describe(takes.Source);
            if (target.StartsWith(source + "."))
            {
                Error(takes.Span, $"'{target}' is inside '{source}', so it can't take it (it would own itself)");
                return;
            }

            takes.Struct = takes.Target.Type.Struct;
            HeapMethod("Heap.Free", takes.Span, scope, "takes");
        }

        static string OpText(AssignOp op) => op switch
        {
            AssignOp.Add => "++",
            AssignOp.Subtract => "--",
            AssignOp.Multiply => "**",
            AssignOp.Divide => "//",
            AssignOp.Modulo => "%%",
            _ => "^^",
        };

        static string Describe(Expr e) => e switch
        {
            NameExpr n => n.Name,
            IndexExpr i => Describe(i.Target),
            MemberExpr m => Describe(m.Target) + "." + m.Member,
            _ => "value",
        };

        int loopDepth;

        void BindReturn(ReturnStmt ret, Scope scope)
        {
            if (ret.Value != null)
            {
                allowObjectTemp = true;
                BindExpr(ret.Value, scope);
                allowObjectTemp = false;
            }

            MethodSymbol? method = scope.Method;
            if (method == null)
            {
                Error(ret.Span, "return can only be used inside a method");
                return;
            }

            string name = method.Decl.Name;
            if (method.Decl.Result == null)
            {
                if (ret.Value != null)
                    Error(ret.Value.Span, $"{name} doesn't return a value (it ends with [end]), so return can't take one");
                return;
            }

            if (ret.Value == null)
            {
                Error(ret.Span, $"{name} returns a value (it ends with [end: value]), so return needs one: return value;");
                return;
            }

            PhiType result = ReturnTypeOf(method);
            if (ret.Value.Type.IsError || result.IsError) return;
            method.ReturnValues.Add(ret.Value);

            if (result.IsObject || ret.Value.Type.IsObject || ret.Value.Type.IsStruct)
            {
                if (ret.Value.Type.Struct != result.Struct || !(ret.Value.Type.IsObject || ret.Value.Type.IsStruct))
                    Error(ret.Value.Span, $"{name} returns a {result} (from its [end: value]), but this is a {ret.Value.Type}");
                return;
            }

            // a one-character string like '/' is a number here, as anywhere a number goes
            if (!result.IsString && ret.Value is StringExpr { Value.Length: 1 })
                ret.Value = Convert(ret.Value, PhiType.Int, "the returned value");
            // a string literal lives as long as the program, so its address can be handed back
            else if (result.IsPointer && result.Pointee == PhiType.U8 && ret.Value is StringExpr)
                ret.Value = Convert(ret.Value, result, "the returned value");
            else if (result.IsString != ret.Value.Type.IsString)
                Error(ret.Value.Span, $"{name} returns a {(result.IsString ? "str" : result.IsPointer ? result.ToString() : "number")} (from its [end: value]), but this is a {ret.Value.Type}");
            else if (!result.IsString)
                ret.Value = Convert(ret.Value, result.IsPointer ? result : PhiType.Int, "the returned value");
        }

        void BindCall(CallStmt call, Scope scope)
        {
            if (call.ResultTarget != null) BindExpr(call.ResultTarget, scope);
            object? callee = BindCallee(call.Callee, call.CalleeSpan, call.Arguments, call.Span, scope, call.CalleeTarget);
            call.Resolved = callee;

            if (call.ResultTarget == null) return;
            switch (callee)
            {
                case PointerCall pc:
                    if (pc.Signature.Result.Kind == TypeKind.Void)
                        Error(call.CalleeSpan, $"{call.Callee} is a {pc.Target.Type}, which doesn't return a value");
                    else
                        CheckResultTarget(call, pc.Signature.Result);
                    break;

                case MethodSymbol method:
                    if (method.Decl.Result == null)
                        Error(call.CalleeSpan, $"{method.Name} doesn't return a value (it would need [end: value])");
                    else
                        CheckResultTarget(call, ReturnTypeOf(method));
                    break;

                case BuiltinFunction builtin:
                    if (!builtin.Returns) Error(call.CalleeSpan, $"{builtin.Name} doesn't return a value");
                    else CheckResultTarget(call, PhiType.Int);
                    break;

                case RawBlockDecl:
                    CheckResultTarget(call, PhiType.Int); // eax
                    break;
            }
        }

        /// <summary>(Name: args) used as a value.</summary>
        void BindCallExpr(CallExpr call, Scope scope)
        {
            if (call.CalleeTarget == null && Lookup(call.Callee, scope) is VariableSymbol { Type.IsMethod: false } or FieldPathSymbol { Type.IsMethod: false, Type.IsStruct: false }
                or SelfFieldSymbol { Type.IsMethod: false, Type.IsStruct: false } or PathSymbol { Type.IsMethod: false, Type.IsStruct: false })
            {
                Error(call.CalleeSpan, $"'{call.Callee}' isn't a method; for an element, leave out the space after ':' ({call.Callee}:i)");
                call.Type = PhiType.Error;
                return;
            }

            object? callee = BindCallee(call.Callee, call.CalleeSpan, call.Arguments, call.Span, scope, call.CalleeTarget);
            call.Resolved = callee;
            call.Type = PhiType.Error;

            switch (callee)
            {
                case PointerCall pc:
                    if (pc.Signature.Result.Kind == TypeKind.Void)
                        Error(call.CalleeSpan, $"{call.Callee} is a {pc.Target.Type}, which doesn't return a value, so it can't be used as one");
                    else
                        call.Type = pc.Signature.Result;
                    break;

                case MethodSymbol method:
                    if (method.Decl.Result == null)
                        Error(call.CalleeSpan, $"{method.Name} doesn't return a value (it ends with [end]), so it can't be used as one");
                    else
                        call.Type = ReturnTypeOf(method);
                    break;

                case BuiltinFunction builtin:
                    if (!builtin.Returns) Error(call.CalleeSpan, $"{builtin.Name} doesn't return a value, so it can't be used as one");
                    else call.Type = PhiType.Int;
                    break;

                case RawBlockDecl:
                    call.Type = PhiType.Int; // eax
                    break;
            }
        }

        /// <summary>
        /// Resolves a call's method and checks its arguments; returns the MethodSymbol,
        /// BuiltinFunction or RawBlockDecl, or null after reporting an error.
        /// </summary>
        object? BindCallee(string calleeName, Span calleeSpan, List<Expr> arguments, Span span, Scope scope,
                           Expr? calleeTarget = null)
        {
            BoundUnit unit = units[scope.Unit];
            foreach (Expr arg in arguments) BindExpr(arg, scope);
            WarnSplitNegatives(arguments);

            // a struct's method, called on a struct: call c.Add: 3;  call shapes:i.Draw;
            MethodSymbol? structMethod = null;
            if (calleeTarget is MemberExpr member)
            {
                BindExpr(member.Target, scope);
                if (FieldsOf(member.Target.Type) is StructSymbol fields && FindStructMethod(fields, member.Member, scope) is MethodSymbol found)
                {
                    structMethod = found;
                    arguments.Insert(0, SelfArgument(member.Target, scope));
                    calleeTarget = null;
                }
            }
            else if (calleeTarget == null && calleeName.LastIndexOf('.') is int dot and > 0
                     && Lookup(calleeName[..dot], scope) is VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol)
            {
                var receiver = new NameExpr { Name = calleeName[..dot], Span = calleeSpan };
                BindExpr(receiver, scope);
                if (FieldsOf(receiver.Type) is StructSymbol fields && FindStructMethod(fields, calleeName[(dot + 1)..], scope) is MethodSymbol found)
                {
                    structMethod = found;
                    arguments.Insert(0, SelfArgument(receiver, scope));
                }
            }

            // a method variable: call handler: x;  call handlers:i: x;
            Expr? pointer = structMethod != null ? null : calleeTarget;
            if (pointer == null && structMethod == null && Lookup(calleeName, scope) is VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol)
                pointer = new NameExpr { Name = calleeName, Span = calleeSpan };
            if (pointer != null)
            {
                // call F:x; (no space) reads as an element of F
                if (calleeTarget is IndexExpr { Target: NameExpr root } && Lookup(root.Name, scope) == null
                    && ResolveCallee(root.Name, scope) != null)
                {
                    Error(calleeSpan, $"put a space after the ':' that starts the arguments: {root.Name}: ...");
                    return null;
                }

                BindExpr(pointer, scope);
                if (pointer.Type.IsError) return null;
                if (!pointer.Type.IsMethod)
                {
                    Error(calleeSpan, $"'{calleeName}' is a {pointer.Type}, not a method");
                    return null;
                }

                MethodSignature signature = pointer.Type.Signature!;
                if (arguments.Count != signature.Parameters.Count)
                    Error(span, $"{calleeName} is a {pointer.Type}, which takes {Plural(signature.Parameters.Count, "argument")}, but was given {arguments.Count}");
                for (int i = 0; i < Math.Min(arguments.Count, signature.Parameters.Count); i++)
                    arguments[i] = Convert(arguments[i], signature.Parameters[i], $"argument {i + 1} of {calleeName}");
                return new PointerCall { Target = pointer, Signature = signature };
            }

            object? callee = structMethod ?? ResolveCallee(calleeName, scope);
            if (structMethod == null && callee is MethodSymbol { Decl.Owner.ForStruct: StructDecl forStruct } sibling)
            {
                // another method of the same struct, from inside one: on the same self
                if (scope.Class.ForStruct == forStruct && scope.Method is { Parameters.Count: > 0 } here)
                {
                    var self = new NameExpr { Name = "self", Span = calleeSpan, Symbol = here.Parameters[0], Type = here.Parameters[0].Type };
                    arguments.Insert(0, self);
                }
                else
                {
                    Error(calleeSpan, $"{sibling.Decl.Name} is a method of struct {forStruct.Name}; call it on one, like value.{sibling.Decl.Name}");
                    return null;
                }
            }
            switch (callee)
            {
                case MethodSymbol method:
                {
                    if (method.Unit != scope.Unit)
                    {
                        Error(calleeSpan, $"{method.Name} is in the {UnitName(method.Unit)} and can't be called from the {UnitName(scope.Unit)}");
                        return null;
                    }

                    if (method.IsInterruptHandler)
                    {
                        Error(calleeSpan, $"{method.Name} is an interrupt handler; install it with call OS.SetInterruptHandler: vector addr {method.Decl.Name};");
                        return null;
                    }

                    int count = method.Decl.Parameters.Count;
                    int hidden = method.Decl.Owner.ForStruct != null ? 1 : 0;     // self
                    if (arguments.Count > count)
                        Error(arguments[count].Span, $"{method.Name} takes {Plural(count - hidden, "argument")} but was given {arguments.Count - hidden}");

                    for (int i = 0; i < Math.Min(count, Math.Min(method.Parameters.Count, arguments.Count)); i++)
                        arguments[i] = Convert(arguments[i], method.Parameters[i].Type, $"parameter '{method.Parameters[i].Name}'");

                    return method;
                }

                case BuiltinFunction builtin:
                {
                    if (builtin.OnlyIn is UnitKind only && only != scope.Unit)
                    {
                        Error(calleeSpan, $"{builtin.Name} can only be used in the {UnitName(only)}");
                        return null;
                    }

                    if (!CodeGen.Library.Provides(TargetOf(scope.Unit), builtin.Label))
                    {
                        string why = scope.Unit switch
                        {
                            UnitKind.Kernel => "it needs the BIOS; for graphics in a 32-bit kernel, use drivers.vbe and gfx.draw",
                            UnitKind.Program => "user programs reach the hardware only through the kernel's system calls",
                            _ => "it needs the 32-bit kernel's drivers; make the program a phi.Name:Kernel",
                        };
                        Error(calleeSpan, $"{builtin.Name} isn't available in the {UnitName(scope.Unit)} ({why})");
                        return null;
                    }

                    unit.UsedBuiltins.Add(builtin);

                    int count = builtin.Parameters.Length;
                    if (arguments.Count != count)
                    {
                        string expected = count == 0 ? "no arguments" : $"{Plural(count, "argument")} ({string.Join(" ", builtin.Parameters)})";
                        Error(span, $"{builtin.Name} takes {expected} but was given {arguments.Count}");
                    }

                    for (int i = 0; i < Math.Min(count, arguments.Count); i++)
                    {
                        PhiType expected = builtin.TextParameters.Contains(i) ? PhiType.PointerTo(PhiType.U8) : PhiType.Int;
                        arguments[i] = Convert(arguments[i], expected, $"'{builtin.Parameters[i]}'");

                        // whoever calls a handler (an IRQ, a system call) gives it no arguments
                        if (arguments[i] is AddrExpr { Method: MethodSymbol handler } && handler.Decl.Parameters.Count > 0)
                            Error(arguments[i].Span, $"{handler.Decl.Name} takes parameters, so {builtin.Name} can't call it");
                    }
                    return builtin;
                }

                case RawBlockDecl raw:
                {
                    if (raw.Language != "asm")
                    {
                        Error(calleeSpan, $"{raw.Language}.{raw.Name} can't run on x86; only asm. blocks can be called");
                        return null;
                    }
                    if (arguments.Count > 0)
                        Error(arguments[0].Span, "assembly blocks don't take arguments; set variables and use {name} inside the block");
                    UseRawBlock(unit, raw);
                    return raw;
                }

                default:
                    Error(calleeSpan, $"unknown method '{calleeName}'");
                    return null;
            }
        }

        /// <summary>
        /// call Show: x -1;  passes x and -1, which is easy to misread as x minus 1. After a
        /// number (as in 5 -1) it's plainly a list, and (-1) says it's meant.
        /// </summary>
        void WarnSplitNegatives(List<Expr> arguments)
        {
            for (int i = 1; i < arguments.Count; i++)
            {
                if (arguments[i] is not NumberExpr { Value: < 0 } negative || arguments[i - 1] is NumberExpr) continue;
                if (file.Text[negative.Span.Start] != '-' || negative.Span.Start == 0 || !char.IsWhiteSpace(file.Text[negative.Span.Start - 1])) continue;
                string before = SourceText(arguments[i - 1]), number = SourceText(negative);
                diagnostics.Warning(file, negative.Span, $"'{before} {number}' is two arguments, {before} and {number}; " +
                                                         $"to subtract, write {before} - {number[1..]}, or write ({number}) if two are meant");
            }
        }

        static void UseRawBlock(BoundUnit unit, RawBlockDecl raw)
        {
            if (!unit.RawBlocks.Contains(raw)) unit.RawBlocks.Add(raw);
        }

        object? ResolveCallee(string name, Scope scope)
        {
            if (methodsByFullName.TryGetValue((scope.Unit, name), out MethodSymbol? m)) return m;

            // a plain name: a method of this class first, then any class in the unit
            if (!name.Contains('.'))
            {
                if (methodsByFullName.TryGetValue((scope.Unit, $"{scope.Class.Name}.{name}"), out m)) return m;

                var matches = methodsByFullName.Where(p => p.Key.Item1 == scope.Unit && p.Key.Item2.EndsWith("." + name)).ToList();
                if (matches.Count == 1) return matches[0].Value;
            }

            if (Builtins.Find(name) is BuiltinFunction builtin) return builtin;

            string rawName = name.StartsWith("asm.") ? name[4..] : name;
            if (rawBlocks.TryGetValue(rawName, out RawBlockDecl? raw)) return raw;

            // a method in the other unit, for a better error message
            UnitKind other = scope.Unit == UnitKind.Boot ? UnitKind.Os : UnitKind.Boot;
            if (methodsByFullName.TryGetValue((other, name), out m)) return m;
            var otherMatches = methodsByFullName.Where(p => p.Key.Item1 == other && p.Key.Item2.EndsWith("." + name)).ToList();
            if (otherMatches.Count == 1) return otherMatches[0].Value;

            return null;
        }

        void CheckResultTarget(CallStmt call, PhiType result)
        {
            Expr target = call.ResultTarget!;
            if (target.Type.IsError) return;

            // a new object from the call is handed over to the target
            if (result.IsObject || target.Type.IsObject)
            {
                if (!IsAssignable(target) || !(target.Type.IsObject || target.Type.IsStruct) || target.Type.Struct != result.Struct)
                    Error(target.Span, $"{call.Callee} returns a {result}, which can't be stored in {target.Type} '{Describe(target)}'");
                return;
            }

            if (!IsAssignable(target) || target.Type.IsArray || target.Type.IsStruct)
            {
                Error(target.Span, "the result of a call must go into a variable");
                return;
            }

            if (target.Type.IsNumeric && result.IsString)
                Error(target.Span, $"{call.Callee} returns a str, which can't be stored in {target.Type} '{Describe(target)}'");
        }

        static string UnitName(UnitKind unit) => unit switch
        {
            UnitKind.Boot => "boot sector (Bootloader class)",
            UnitKind.Os => "OS classes",
            UnitKind.Program => "user program",
            _ => "32-bit kernel",
        };
        static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

        // ================================================================ expressions

        void BindCondition(Expr condition, Scope scope)
        {
            BindExpr(condition, scope);
            if (!condition.Type.IsError && !condition.Type.IsNumeric)
                Error(condition.Span, $"a condition must be a number or a comparison, not a {condition.Type}");
        }

        void RequireNumeric(Expr e, string what)
        {
            if (!e.Type.IsError && !e.Type.IsNumeric)
                Error(e.Span, $"{what} must be a number, not a {e.Type}");
        }

        /// <summary>Math on two values happens in 32 bits, unsigned if either side is u32 or a pointer.</summary>
        /// <summary>
        /// A method&lt;: R&gt; in parentheses is a call, as (Name) is for a method: it takes no
        /// arguments and gives a value. Without parentheses it's still the method (f is 0 checks it).
        /// </summary>
        static bool CalledInParens(PhiType t) =>
            t.IsMethod && t.Signature!.Parameters.Count == 0 && t.Signature.Result.Kind != TypeKind.Void;

        static PhiType ArithmeticType(PhiType a, PhiType b) =>
            a.IsUnsigned32 || b.IsUnsigned32 ? PhiType.U32 : PhiType.I32;

        void BindExpr(Expr expr, Scope scope)
        {
            bool allowObject = allowObjectTemp;
            allowObjectTemp = false;
            BindExprKind(expr, scope);

            // a call's new object has to go somewhere that takes it over, or nothing would free it
            if (!allowObject && IsObjectCall(expr))
                Error(expr.Span, $"this call returns a new {expr.Type}; store it first, like call x is ...; or new {expr.Type} x: (...);");
        }

        void BindExprKind(Expr expr, Scope scope)
        {
            switch (expr)
            {
                case NumberExpr n:
                    // literals too big for int (like 0x80000000) are u32, so math on them is unsigned
                    expr.Type = n.Value > int.MaxValue ? PhiType.U32 : PhiType.Int;
                    break;

                case BoolExpr:
                    expr.Type = PhiType.Bool;
                    break;

                case EmptyExpr:
                    // only meaningful next to an object: x is empty, x.next is not empty
                    expr.Type = PhiType.U32;
                    break;

                case StringExpr:
                    expr.Type = PhiType.Str;
                    break;

                case DecimalExpr:
                    Error(expr.Span, "decimal numbers aren't supported yet");
                    expr.Type = PhiType.Error;
                    break;

                case NameExpr name:
                {
                    Symbol? symbol = Lookup(name.Name, scope);

                    // (OS.GetTicks) or (c.Area): a call with no arguments, when no variable has the name
                    if (symbol == null && name.InParens && (ResolveCallee(name.Name, scope) != null || IsStructMethodName(name.Name, scope)))
                    {
                        name.Call = new CallExpr { Callee = name.Name, CalleeSpan = name.Span, Span = name.Span };
                        BindCallExpr(name.Call, scope);
                        name.Type = name.Call.Type;
                        break;
                    }

                    PhiType type = symbol switch
                    {
                        VariableSymbol v => v.Type,
                        ConstantSymbol k => k.Type,
                        LengthSymbol => PhiType.Int,
                        FieldPathSymbol f => f.Type,
                        SelfFieldSymbol s => s.Type,
                        PathSymbol p => p.Type,
                        _ => PhiType.Error,
                    };

                    // (next): a method variable that takes nothing is called, like (Name)
                    if (name.InParens && CalledInParens(type))
                    {
                        name.Call = new CallExpr { Callee = name.Name, CalleeSpan = name.Span, Span = name.Span };
                        BindCallExpr(name.Call, scope);
                        name.Type = name.Call.Type;
                        break;
                    }

                    name.Symbol = symbol;
                    name.Type = type;
                    if (symbol != null) WarnOtherClass(name.Name, name.Span, scope);
                    if (symbol == null) Error(name.Span, MethodHint(name.Name, "", scope) ?? DescribeMissing(name.Name, scope));
                    break;
                }

                case IndexExpr index:
                    BindIndex(index, scope);
                    break;

                case MemberExpr member:
                {
                    BindExpr(member.Target, scope);
                    PhiType t = member.Target.Type;
                    if (t.IsError) { member.Type = PhiType.Error; break; }

                    // through a pointer to a struct as well: ptrs:i.x is ptrs:i:0.x
                    StructSymbol? fields = FieldsOf(t);
                    FieldSymbol? field = fields?.Find(member.Member);

                    // (shapes:i.Area): the struct's method, with no arguments
                    if (field == null && member.InParens && fields != null && FindStructMethod(fields, member.Member, scope) != null)
                    {
                        member.Call = new CallExpr { Callee = member.Member, CalleeSpan = member.MemberSpan, Span = member.Span, CalleeTarget = member };
                        BindCallExpr(member.Call, scope);
                        member.Type = member.Call.Type;
                        break;
                    }

                    if (field == null)
                    {
                        Error(member.MemberSpan, fields != null ? $"{fields.Name} has no field '{member.Member}'" : $"a {t} has no fields");
                        member.Type = PhiType.Error;
                        break;
                    }

                    // (apps:id.closing): a method field that takes nothing is called too
                    if (member.InParens && CalledInParens(field.Type))
                    {
                        var place = new MemberExpr { Target = member.Target, Member = member.Member, MemberSpan = member.MemberSpan, Span = member.Span };
                        member.Call = new CallExpr { Callee = member.Member, CalleeSpan = member.MemberSpan, Span = member.Span, CalleeTarget = place };
                        BindCallExpr(member.Call, scope);
                        member.Type = member.Call.Type;
                        break;
                    }

                    member.Field = field;
                    member.Type = field.Type;
                    break;
                }

                case AddrExpr addr:
                    BindAddr(addr, scope);
                    break;

                case NewExpr n:
                    BindNew(n, scope);
                    break;

                case SizeofExpr size:
                    BindSizeof(size, scope);
                    break;

                case CallExpr call:
                    BindCallExpr(call, scope);
                    break;

                case CastExpr cast:
                {
                    BindExpr(cast.Operand, scope);
                    PhiType to = ResolveType(cast.TargetType, scope, allowStr: false);
                    cast.Type = PhiType.Error;
                    if (cast.Operand.Type.IsError || to.IsError) break;

                    // numbers, pointers and method addresses become each other; nothing else does
                    bool from = cast.Operand.Type.IsNumeric || cast.Operand.Type.IsMethod;
                    if (!from || !(to.IsNumeric || to.IsMethod))
                    {
                        Error(cast.Span, $"a {cast.Operand.Type} can't be used as a {to}; only numbers, pointers and methods can");
                        break;
                    }
                    cast.Type = to;
                    break;
                }

                case InExpr input:
                    if (scope.Unit == UnitKind.Program)
                        Error(input.Span, "user programs can't use ports; only the kernel can talk to hardware");
                    BindExpr(input.Port, scope);
                    input.Port = Convert(input.Port, PhiType.U16, "the port");
                    input.Type = input.Size switch { 1 => PhiType.U8, 2 => PhiType.U16, _ => PhiType.U32 };
                    break;

                case UnaryExpr unary:
                    BindExpr(unary.Operand, scope);
                    unary.Operand = Convert(unary.Operand, PhiType.Int, unary.Op switch
                    {
                        UnaryOp.Negate => "'-'",
                        UnaryOp.BitNot => "'~'",
                        _ => "'not'",
                    });
                    unary.Type = unary.Op switch
                    {
                        UnaryOp.Not => PhiType.Bool,
                        UnaryOp.BitNot => unary.Operand.Type.IsUnsigned32 ? PhiType.U32 : PhiType.I32,
                        _ => PhiType.Int,
                    };
                    break;

                case BinaryExpr binary:
                    BindBinary(binary, scope);
                    break;

                default:
                    throw new InvalidOperationException($"unexpected expression {expr.GetType().Name}");
            }
        }

        void BindIndex(IndexExpr index, Scope scope)
        {
            // if Ok: 3  reads as an element of Ok; it was meant as a call
            if (index.Target is NameExpr { InParens: false } callee && Lookup(callee.Name, scope) == null
                && MethodHint(callee.Name, ": " + SourceText(index.Index), scope) is string hint)
            {
                Error(callee.Span, hint);
                index.Type = PhiType.Error;
                return;
            }

            BindExpr(index.Target, scope);
            BindExpr(index.Index, scope);
            PhiType t = index.Target.Type;

            if (t.IsError || index.Index.Type.IsError) { index.Type = PhiType.Error; return; }

            index.Index = Convert(index.Index, PhiType.Int, "an index");

            long? constant = ConstantFolder.Evaluate(index.Index);
            int? limit = null;

            if (t.IsArray)
            {
                index.Type = t.Element;
                limit = t.Length;
            }
            else if (t.IsString)
            {
                index.Type = PhiType.U8; // one character
                limit = CapacityOf(index.Target);
            }
            else if (t.IsPointer)
            {
                index.Type = t.Pointee!; // no limit: a pointer can point anywhere
            }
            else
            {
                Error(index.Span, $"'{Describe(index.Target)}' is a {t}, which can't be indexed");
                index.Type = PhiType.Error;
                return;
            }

            if (limit != null && constant != null)
            {
                // a constant index is checked now instead of when the program runs
                if (constant < 0 || constant >= limit)
                    Error(index.Index.Span, $"index {constant} is outside '{Describe(index.Target)}', which has {limit} {(t.IsString ? "bytes" : "elements")}");
                return;
            }

            if (unsafeDepth == 0) index.CheckLimit = limit;
        }

        /// <summary>Bytes a str expression has room for, when known.</summary>
        static int? CapacityOf(Expr str) => str switch
        {
            NameExpr { Symbol: VariableSymbol v } => v.Capacity,
            IndexExpr { Target: NameExpr { Symbol: VariableSymbol v } } => v.Capacity, // an element of a str array
            _ => null,
        };

        /// <summary>new T / new T[count]: ask the heap (lib/phi/memory/heap.phi) for zeroed memory.</summary>
        void BindNew(NewExpr n, Scope scope)
        {
            n.Type = PhiType.Error;
            PhiType element = ResolveType(n.ElementType, scope, allowStr: false);

            Expr? count = null;
            if (n.Count != null)
            {
                BindExpr(n.Count, scope);
                count = Convert(n.Count, PhiType.U32, "the number of elements");
                n.Count = count;
            }

            n.Allocator = HeapMethod("Heap.Alloc", n.Span, scope, "new");
            if (element.IsError || n.Allocator == null) return;

            var size = new NumberExpr { Value = element.ElementSize, Span = n.Span, Type = PhiType.U32 };
            n.Bytes = count == null
                ? size
                : new BinaryExpr { Op = BinaryOp.Multiply, Left = count, Right = size, Span = n.Span, Type = PhiType.U32 };
            n.Type = PhiType.PointerTo(element);
        }

        MethodSymbol? HeapMethod(string name, Span span, Scope scope, string what)
        {
            if (scope.Unit != UnitKind.Kernel)
            {
                Error(span, $"{what} needs a heap, which only 32-bit kernels (phi.Name:Kernel) have");
                return null;
            }
            if (methodsByFullName.TryGetValue((UnitKind.Kernel, name), out MethodSymbol? m)) return m;
            Error(span, $"{what} needs {name}; add use memory.heap;");
            return null;
        }

        void BindAddr(AddrExpr addr, Scope scope)
        {
            addr.Type = PhiType.U32;

            // addr Method: the address of code, for interrupt handlers
            if (addr.Operand is NameExpr name && Lookup(name.Name, scope) == null)
            {
                object? callee = ResolveCallee(name.Name, scope);
                switch (callee)
                {
                    case MethodSymbol m when m.Unit == scope.Unit:
                        if (ReturnTypeOf(m).IsObject)
                            Error(addr.Span, $"{m.Name} returns an object, so it can't be used as a method value yet");
                        addr.CodeLabel = m.Label;
                        addr.Method = m;
                        name.Type = PhiType.Void;
                        // an interrupt handler ends with iret, so it can't be called like a method
                        if (!m.IsInterruptHandler) addr.Type = PhiType.MethodOf(SignatureOf(m));
                        return;
                    case RawBlockDecl { Language: "asm" } raw:
                        UseRawBlock(units[scope.Unit], raw);
                        addr.CodeLabel = raw.Name;
                        name.Type = PhiType.Void;
                        return;
                }
            }

            BindExpr(addr.Operand, scope);
            if (addr.Operand.Type.IsError) return;

            bool place = addr.Operand switch
            {
                NameExpr { Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol } => true,
                IndexExpr or MemberExpr => true,
                _ => false,
            };
            if (!place)
            {
                Error(addr.Operand.Span, "addr needs a variable, field, element or method");
                return;
            }

            // an object's address is the one it holds: addr pt is a ptr<Point> to pt's memory
            PhiType t = addr.Operand.Type;
            if (t.IsObject)
            {
                addr.OfObject = true;
                addr.Type = PhiType.PointerTo(PhiType.Of(t.Struct!));
                return;
            }

            addr.Type = t.IsString ? PhiType.PointerTo(PhiType.U8)
                      : t.IsArray && t.Kind == TypeKind.Str ? PhiType.PointerTo(PhiType.PointerTo(PhiType.U8)) // its table of pointers
                      : t.IsArray ? PhiType.PointerTo(t.Element)
                      : PhiType.PointerTo(t);
        }

        void BindSizeof(SizeofExpr size, Scope scope)
        {
            size.Type = PhiType.Int;
            PhiType t;
            VariableSymbol? variable = null;

            if (size.TypeName != null)
            {
                t = ResolveType(size.TypeName, scope, allowStr: true);
            }
            else if (size.Operand is NameExpr name && Lookup(name.Name, scope) == null
                     && structs.TryGetValue(name.Name, out StructSymbol? s))
            {
                t = PhiType.Of(s);
            }
            else
            {
                BindExpr(size.Operand!, scope);
                t = size.Operand!.Type;
                if (size.Operand is NameExpr { Symbol: VariableSymbol v }) variable = v;
            }

            if (t.IsError) return;

            if (t.Kind == TypeKind.Str)
            {
                // a str's room is decided where it's declared
                if (variable != null && !t.IsArray) size.Value = variable.Capacity;
                else Error(size.Span, "the size of a str depends on its declaration; use sizeof on a str variable");
                return;
            }

            if (t.Kind is TypeKind.Void) { Error(size.Span, "this has no size"); return; }
            if (t.IsObject) { size.Value = t.Struct!.Size; return; } // the object, not its address
            size.Value = (long)t.ElementSize * (t.IsArray ? t.Length : 1);
        }

        void BindBinary(BinaryExpr b, Scope scope)
        {
            BindExpr(b.Left, scope);
            BindExpr(b.Right, scope);

            if (b.Left.Type.IsError || b.Right.Type.IsError)
            {
                b.Type = PhiType.Error;
                return;
            }

            // two objects (or structs) are compared by their values; empty is no object at all
            if (b.Left.Type.IsObject || b.Left.Type.IsStruct || b.Right.Type.IsObject || b.Right.Type.IsStruct
                || b.Left is EmptyExpr || b.Right is EmptyExpr)
            {
                BindObjectCompare(b, scope);
                return;
            }

            // two strings are compared as text; a one-character string next to a number
            // (like greeting:0 is 'h') is its character code instead
            if (b.IsComparison && b.Left.Type.IsString && b.Right.Type.IsString)
            {
                if (b.Op is not (BinaryOp.Equal or BinaryOp.NotEqual))
                    Error(b.Span, "strings can only be compared with is / == and is not / !=");
                b.Type = PhiType.Bool;
                return;
            }

            b.Left = Convert(b.Left, PhiType.Int, "the left side");
            b.Right = Convert(b.Right, PhiType.Int, "the right side");
            b.Type = b.IsComparison || b.IsLogical ? PhiType.Bool : ArithmeticType(b.Left.Type, b.Right.Type);
        }

        /// <summary>a is b on objects: equal when their values are (following new fields); x is empty: no object.</summary>
        void BindObjectCompare(BinaryExpr b, Scope scope)
        {
            b.Type = PhiType.Bool;
            if (b.Op is not (BinaryOp.Equal or BinaryOp.NotEqual))
            {
                Error(b.Span, "objects can only be compared with is / == and is not / !=");
                return;
            }

            Expr? empty = b.Left is EmptyExpr ? b.Left : b.Right is EmptyExpr ? b.Right : null;
            if (empty != null)
            {
                Expr other = empty == b.Left ? b.Right : b.Left;
                if (other is EmptyExpr || !other.Type.IsObject)
                    Error(other.Span, $"only an object can be empty, and this is a {other.Type}");
                else
                    b.ObjectCompare = other.Type.Struct;
                return;
            }

            static bool IsObjectValue(Expr e) => (e.Type.IsObject || e.Type.IsStruct) && e is NameExpr or IndexExpr or MemberExpr;
            if (!IsObjectValue(b.Left) || !IsObjectValue(b.Right) || b.Left.Type.Struct != b.Right.Type.Struct)
            {
                Error(b.Span, $"a {b.Left.Type} can't be compared with a {b.Right.Type}");
                return;
            }
            if (scope.Unit is UnitKind.Boot or UnitKind.Os)
            {
                Error(b.Span, "comparing whole structs needs 32-bit code (a Kernel or a Program); compare their fields");
                return;
            }
            b.ObjectCompare = b.Left.Type.Struct;
        }

        /// <summary>
        /// Checks that a value fits where it's going. A one-character string like 'w' becomes
        /// its character code wherever a number is expected. Numbers of any size convert to
        /// each other (larger values are cut to fit when stored).
        /// </summary>
        Expr Convert(Expr e, PhiType target, string what)
        {
            if (e.Type.IsError || target.IsError) return e;

            // a struct parameter borrows: an object, a struct in place, or a pointer to one
            if (target.IsObject)
            {
                if (e.Type.IsObject && e.Type.Struct == target.Struct) return e;
                if (e.Type.IsStruct && e.Type.Struct == target.Struct && e is NameExpr or IndexExpr or MemberExpr)
                    return new AddrExpr { Operand = e, Span = e.Span, Type = PhiType.PointerTo(e.Type) };
                if (e.Type.IsPointer && (e.Type.Pointee!.Struct == target.Struct || e.Type.Pointee == PhiType.U8)) return e;
                if (e is EmptyExpr || ConstantFolder.Evaluate(e) == 0) return e; // no object
                Error(e.Span, $"{what} needs a {target}, but this is a {e.Type}");
                return e;
            }

            // an object where a pointer goes is its address: walk is head.next;
            if (target.IsPointer && e.Type.IsObject)
            {
                if (target.Pointee!.Struct != e.Type.Struct && target.Pointee != PhiType.U8)
                    diagnostics.Warning(file, e.Span, $"{what} is a {target}, but this is a {e.Type}; write `addr ... as {target}` if that's meant");
                return e;
            }
            if (e.Type.IsObject)
            {
                Error(e.Span, $"{what} needs a {target}, but this is a {e.Type}; for its address, write addr {Describe(e)}");
                return e;
            }

            if (target.IsMethod)
            {
                if (e.Type.IsMethod)
                {
                    if (!e.Type.Signature!.Equals(target.Signature))
                        Error(e.Span, $"{what} needs a {target}, but this is a {e.Type}");
                    return e;
                }
                if (ConstantFolder.Evaluate(e) == 0) return e; // no method yet
                Error(e.Span, $"{what} needs a {target}, like addr Name");
                return e;
            }

            // the address of a method is a number, for the built-ins that take handlers
            if (target.IsNumeric && e.Type.IsMethod) return e;

            // one kind of pointer silently becoming another is usually a mistake; ptr<u8> (bytes)
            // goes either way, and `as` says it's meant
            if (target.IsPointer && e.Type.IsPointer && target.Pointee != e.Type.Pointee
                && target.Pointee != PhiType.U8 && e.Type.Pointee != PhiType.U8)
                diagnostics.Warning(file, e.Span, $"{what} is a {target}, but this is a {e.Type}; write `as {target}` if that's meant");

            if (target.IsNumeric)
            {
                if (e.Type.IsNumeric) return e;
                if (target.IsPointer && e.Type.IsString) return e; // a str's value is its address

                // a str array is a table of pointers, one per element: where a pointer goes, the table's address
                if (target.IsPointer && e.Type.IsArray && e.Type.Kind == TypeKind.Str && e is NameExpr)
                {
                    PhiType table = PhiType.PointerTo(PhiType.PointerTo(PhiType.U8));
                    if (target != table && target.Pointee != PhiType.U8)
                        diagnostics.Warning(file, e.Span, $"{what} is a {target}, but a str array is a {table}; write `addr {Describe(e)} as {target}` if that's meant");
                    return new AddrExpr { Operand = e, Span = e.Span, Type = table };
                }

                // an array (or array field) passed where a pointer goes is its address
                if (target.IsPointer && e.Type.IsArray && e.Type.Kind != TypeKind.Str && e is NameExpr or IndexExpr or MemberExpr)
                    return new AddrExpr { Operand = e, Span = e.Span, Type = PhiType.PointerTo(e.Type.Element) };
                if (e is StringExpr { Value.Length: 1 } ch)
                    return new NumberExpr { Value = ch.Value[0], Span = ch.Span, Type = PhiType.Int };

                Error(e.Span, $"{what} needs a number, but this is a {e.Type}");
                return e;
            }

            if (target.IsString)
            {
                if (e.Type.IsString || e.Type.IsNumeric) return e; // numbers are written out as text
                Error(e.Span, $"{what} needs a str, but this is a {e.Type}");
                return e;
            }

            Error(e.Span, $"{what} can't hold a {e.Type}");
            return e;
        }
    }

    /// <summary>Evaluates expressions whose value is known at compile time, with 32-bit wraparound.</summary>
    public static class ConstantFolder
    {
        public static long? Evaluate(Expr e)
        {
            switch (e)
            {
                case NumberExpr n: return n.Value;
                case BoolExpr b: return b.Value ? 1 : 0;
                case StringExpr { Value.Length: 1 } c when e.Type.IsNumeric: return c.Value[0];
                case NameExpr { Symbol: ConstantSymbol k }: return k.Value;
                case NameExpr { Symbol: LengthSymbol { Count: int count } }: return count;
                case SizeofExpr s: return s.Value;
                case UnaryExpr u:
                {
                    long? v = Evaluate(u.Operand);
                    if (v == null) return null;
                    return u.Op switch
                    {
                        UnaryOp.Negate => Wrap(-v.Value),
                        UnaryOp.BitNot => Wrap(~v.Value),
                        _ => v.Value == 0 ? 1 : 0,
                    };
                }
                case BinaryExpr b when !b.Left.Type.IsString:
                {
                    long? l = Evaluate(b.Left), r = Evaluate(b.Right);
                    if (l == null || r == null) return null;

                    bool unsigned = b.Left.Type.IsUnsigned32 || b.Right.Type.IsUnsigned32;
                    int x = (int)l.Value, y = (int)r.Value;
                    uint ux = (uint)x, uy = (uint)y;

                    return b.Op switch
                    {
                        BinaryOp.Add => Wrap((long)x + y),
                        BinaryOp.Subtract => Wrap((long)x - y),
                        BinaryOp.Multiply => Wrap((long)x * y),
                        BinaryOp.Divide => y == 0 ? null : unsigned ? Wrap(ux / uy) : Wrap(x / y),
                        BinaryOp.Modulo => y == 0 ? null : unsigned ? Wrap(ux % uy) : Wrap(x % y),
                        BinaryOp.BitAnd => x & y,
                        BinaryOp.BitOr => x | y,
                        BinaryOp.BitXor => x ^ y,
                        BinaryOp.ShiftLeft => Wrap((long)x << (y & 31)),
                        BinaryOp.ShiftRight => unsigned ? Wrap(ux >> (y & 31)) : x >> (y & 31),
                        BinaryOp.Equal => x == y ? 1 : 0,
                        BinaryOp.NotEqual => x != y ? 1 : 0,
                        BinaryOp.Less => (unsigned ? ux < uy : x < y) ? 1 : 0,
                        BinaryOp.LessEqual => (unsigned ? ux <= uy : x <= y) ? 1 : 0,
                        BinaryOp.Greater => (unsigned ? ux > uy : x > y) ? 1 : 0,
                        BinaryOp.GreaterEqual => (unsigned ? ux >= uy : x >= y) ? 1 : 0,
                        BinaryOp.And => x != 0 && y != 0 ? 1 : 0,
                        BinaryOp.Or => x != 0 || y != 0 ? 1 : 0,
                        _ => null,
                    };
                }
            }
            return null;
        }

        /// <summary>32-bit two's complement, like the CPU.</summary>
        static long Wrap(long v) => (int)v;
    }
}

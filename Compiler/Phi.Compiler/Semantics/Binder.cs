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

            foreach (ClassDecl cls in program.Classes)
            {
                file = cls.File;
                UnitKind unit;
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
                    foreach (VarDecl p in m.Parameters) BindVarDecl(p, ms, classLevel: false);
                    BindStatements(m.Body, ms, classLevel: false);
                    if (m.Result != null)
                    {
                        BindExpr(m.Result, ms);
                        if (m.Result.Type.IsString) m.Symbol.ReturnType = PhiType.Str;
                        else if (m.Result.Type.IsNumeric) m.Symbol.ReturnType = m.Result.Type.IsUnsigned32 ? PhiType.U32 : PhiType.Int;
                        else if (!m.Result.Type.IsError)
                            Error(m.Result.Span, $"a method can return a number or a str, not a {m.Result.Type}");
                    }
                }
            }

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

        // ---------------------------------------------------------------- structs

        void DeclareStructs(List<StructDecl> decls)
        {
            foreach (StructDecl d in decls)
            {
                file = d.File;
                if (Parser.ReservedWords.Contains(d.Name))
                {
                    Error(d.Span, $"'{d.Name}' is a keyword and can't be a struct name");
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
        }

        /// <summary>Works out field offsets; fields follow each other with no padding.</summary>
        void Layout(StructSymbol s, HashSet<StructSymbol> inProgress)
        {
            if (s.LayoutDone) return;
            file = s.Decl.File;

            if (!inProgress.Add(s))
            {
                Error(s.Decl.Span, $"struct {s.Name} contains itself; use a ptr<{s.Name}> field instead");
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
                    Error(decl.NameSpan, $"'{decl.Name}' is a keyword and can't be a variable name");
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
                Error(c.NameSpan, $"'{c.Name}' is a keyword and can't be a constant name");
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
                Error(method.Span, $"'{name}' is a keyword and can't be a method name");
                return;
            }

            if (method.IsInterruptHandler)
            {
                if (classScope.Unit == UnitKind.Program) Error(method.Span, "user programs can't handle interrupts");
                if (dotted) Error(method.Span, "an event can't also be an isr");
                if (method.Parameters.Count > 0) Error(method.Parameters[0].Span, "interrupt handlers can't take parameters");
                if (method.Result != null) Error(method.Result.Span, "interrupt handlers can't return a value");
            }

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
            DeclareNames(scope, CollectDeclarations(method.Body), isParameter: false);
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
                    IndexExpr => DefaultStrCapacity, // could be an element of a str array
                    _ => IntTextCapacity,            // a number, written out as text
                };
                return;
            }

            v.Type = d.Values.Count > 1 ? type.ArrayOf(d.Values.Count) : type;
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
                    foreach (VariableSymbol v in scope.Names.Values.OfType<VariableSymbol>())
                    {
                        v.Label = $"VALUE_{method.Symbol!.Label}_{v.Name}";
                        v.IsLocal = true;
                    }
            }
        }

        // ================================================================ lookup

        Symbol? LookupSimple(string name, Scope scope)
        {
            if (scope.Method != null && methodScopes.TryGetValue(scope.Method.Decl, out Scope? ms)
                && ms.Names.TryGetValue(name, out Symbol? local))
                return local;

            if (classScopes[scope.Class].Names.TryGetValue(name, out Symbol? member))
                return member;

            // a name from another class in the same unit, if only one class has it
            var others = classScopes.Values
                .Where(s => s.Unit == scope.Unit && s.Class != scope.Class && s.Names.ContainsKey(name))
                .Select(s => s.Names[name]).ToList();
            if (others.Count == 1) return others[0];

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

        /// <summary>Names, plus x.len and struct fields reached by name (task.id, a.b.c).</summary>
        Symbol? Lookup(string name, Scope scope)
        {
            Symbol? direct = LookupSimple(name, scope);
            if (direct != null) return direct;

            string[] parts = name.Split('.');
            for (int split = parts.Length - 1; split >= 1; split--)
            {
                if (LookupSimple(string.Join('.', parts[..split]), scope) is not VariableSymbol root) continue;
                return ResolveMembers(root, parts[split..]);
            }

            return null;
        }

        Symbol? ResolveMembers(VariableSymbol root, string[] members)
        {
            PhiType type = root.Type;
            int offset = 0;

            for (int i = 0; i < members.Length; i++)
            {
                string m = members[i];
                bool last = i == members.Length - 1;

                if (m == "len" && last)
                {
                    if (type.IsArray) return new LengthSymbol { Name = m, Count = type.Length };
                    if (type.IsString && offset == 0) return new LengthSymbol { Name = m, Str = root };
                    return null;
                }

                FieldSymbol? field = type.IsStruct ? type.Struct!.Find(m) : null;
                if (field == null) return null;
                offset += field.Offset;
                type = field.Type;
            }

            return new FieldPathSymbol { Name = root.Name, Root = root, Offset = offset, Type = type };
        }

        string DescribeMissing(string name, Scope scope)
        {
            UnitKind other = scope.Unit == UnitKind.Boot ? UnitKind.Os : UnitKind.Boot;
            string head = name.Split('.')[0];
            if (scope.Unit == UnitKind.Kernel) return $"unknown name '{name}'";
            bool inOtherUnit = classScopes.Values.Any(s => s.Unit == other && s.Names.ContainsKey(head));
            if (inOtherUnit)
            {
                string where = other == UnitKind.Boot ? $"the {BootBase} class" : $"an {KernelBase} class";
                return $"'{head}' is declared in {where}; the boot sector and the OS classes are built separately and can't share variables";
            }

            if (name.Contains('.') && LookupSimple(head, scope) is VariableSymbol v)
                return v.Type.IsStruct ? $"{v.Type} has no field '{name[(head.Length + 1)..]}'" : $"'{head}' is a {v.Type}, which has no '{name[(head.Length + 1)..]}'";

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
                    return decl;

                case ConstDecl:
                    return stmt; // handled when names were declared

                case LogStmt log:
                    foreach (Expr value in log.Values)
                    {
                        BindExpr(value, scope);
                        if (!value.Type.IsError && !value.Type.IsNumeric && !value.Type.IsString)
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
                    if (!f.Pointer.Type.IsError && !f.Pointer.Type.IsNumeric)
                        Error(f.Pointer.Span, $"free needs a pointer from new, not a {f.Pointer.Type}");
                    f.Releaser = HeapMethod("Heap.Free", f.Span, scope, "free");
                    return f;

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
                    BindStatements(ifs.Then, scope, classLevel: false);
                    if (ifs.Else != null) BindStatements(ifs.Else, scope, classLevel: false);
                    return ifs;

                case WhileStmt loop:
                    if (loop.Init != null) BindVarDecl(loop.Init, scope, classLevel: false);
                    BindCondition(loop.Condition, scope);
                    if (loop.Step != null) loop.Step = BindStatement(loop.Step, scope, classLevel: false);
                    BindStatements(loop.Body, scope, classLevel: false);
                    return loop;

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

            for (int i = 0; i < decl.Values.Count; i++)
            {
                Expr value = decl.Values[i];
                BindExpr(value, scope);
                decl.Values[i] = Convert(value, v.Type.Element, $"'{decl.Name}'");
            }

            var constants = decl.Values.Select(ConstantValue).ToList();
            bool allConstant = constants.All(c => c != null);

            if (allConstant)
                v.StaticValues = constants.Select(c => c!).ToList();

            // a method or loop variable is set every time its declaration runs (to zero if it
            // has no value); a class variable with constant values is simply stored that way
            if (!classLevel || v.IsParameter || !allConstant)
                v.NeedsInitCode = true;
        }

        /// <summary>A long, a string, or null if the value isn't known at compile time.</summary>
        static object? ConstantValue(Expr e) => e switch
        {
            StringExpr s when e.Type.IsString => s.Value,
            _ => ConstantFolder.Evaluate(e),
        };

        static bool IsAssignable(Expr target) => target switch
        {
            NameExpr { Symbol: VariableSymbol or FieldPathSymbol } => true,
            IndexExpr or MemberExpr => true,
            _ => false,
        };

        Stmt BindAssign(AssignStmt assign, Scope scope)
        {
            BindExpr(assign.Target, scope);
            BindExpr(assign.Value, scope);

            Expr target = assign.Target;
            if (target.Type.IsError || assign.Value.Type.IsError) return assign;

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
                value = Convert(value, target.Type, $"'{Describe(target)}'");
            }

            return new AssignStmt { Target = target, Op = AssignOp.Set, Value = value, Span = assign.Span };
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

        void BindCall(CallStmt call, Scope scope)
        {
            BoundUnit unit = units[scope.Unit];
            foreach (Expr arg in call.Arguments) BindExpr(arg, scope);
            if (call.ResultTarget != null) BindExpr(call.ResultTarget, scope);

            object? callee = ResolveCallee(call.Callee, scope);
            call.Resolved = callee;

            switch (callee)
            {
                case MethodSymbol method:
                {
                    if (method.Unit != scope.Unit)
                    {
                        Error(call.CalleeSpan, $"{method.Name} is in the {UnitName(method.Unit)} and can't be called from the {UnitName(scope.Unit)}");
                        return;
                    }

                    if (method.IsInterruptHandler)
                    {
                        Error(call.CalleeSpan, $"{method.Name} is an interrupt handler; install it with call OS.SetInterruptHandler: vector addr {method.Decl.Name};");
                        return;
                    }

                    int count = method.Decl.Parameters.Count;
                    if (call.Arguments.Count > count)
                        Error(call.Arguments[count].Span, $"{method.Name} takes {Plural(count, "argument")} but was given {call.Arguments.Count}");

                    for (int i = 0; i < Math.Min(count, Math.Min(method.Parameters.Count, call.Arguments.Count)); i++)
                        call.Arguments[i] = Convert(call.Arguments[i], method.Parameters[i].Type, $"parameter '{method.Parameters[i].Name}'");

                    if (call.ResultTarget != null)
                    {
                        if (method.Decl.Result == null)
                            Error(call.CalleeSpan, $"{method.Name} doesn't return a value (it would need [end: value])");
                        else
                            CheckResultTarget(call, method.ReturnType);
                    }
                    break;
                }

                case BuiltinFunction builtin:
                {
                    if (builtin.OnlyIn is UnitKind only && only != scope.Unit)
                    {
                        Error(call.CalleeSpan, $"{builtin.Name} can only be used in the {UnitName(only)}");
                        return;
                    }

                    if (!CodeGen.Library.Provides(TargetOf(scope.Unit), builtin.Label))
                    {
                        string why = scope.Unit switch
                        {
                            UnitKind.Kernel => "it needs the BIOS or graphics mode, which a 32-bit kernel doesn't have yet",
                            UnitKind.Program => "user programs reach the hardware only through the kernel's system calls",
                            _ => "it needs the 32-bit kernel's drivers; make the program a phi.Name:Kernel",
                        };
                        Error(call.CalleeSpan, $"{builtin.Name} isn't available in the {UnitName(scope.Unit)} ({why})");
                        return;
                    }

                    unit.UsedBuiltins.Add(builtin);

                    int count = builtin.Parameters.Length;
                    if (call.Arguments.Count != count)
                    {
                        string expected = count == 0 ? "no arguments" : $"{Plural(count, "argument")} ({string.Join(" ", builtin.Parameters)})";
                        Error(call.Span, $"{builtin.Name} takes {expected} but was given {call.Arguments.Count}");
                    }

                    for (int i = 0; i < Math.Min(count, call.Arguments.Count); i++)
                    {
                        PhiType expected = builtin.TextParameters.Contains(i) ? PhiType.PointerTo(PhiType.U8) : PhiType.Int;
                        call.Arguments[i] = Convert(call.Arguments[i], expected, $"'{builtin.Parameters[i]}'");
                    }

                    if (call.ResultTarget != null)
                    {
                        if (!builtin.Returns) Error(call.CalleeSpan, $"{builtin.Name} doesn't return a value");
                        else CheckResultTarget(call, PhiType.Int);
                    }
                    break;
                }

                case RawBlockDecl raw:
                {
                    if (raw.Language != "asm")
                    {
                        Error(call.CalleeSpan, $"{raw.Language}.{raw.Name} can't run on x86; only asm. blocks can be called");
                        return;
                    }
                    if (call.Arguments.Count > 0)
                        Error(call.Arguments[0].Span, "assembly blocks don't take arguments; set variables and use {name} inside the block");
                    UseRawBlock(unit, raw);
                    if (call.ResultTarget != null) CheckResultTarget(call, PhiType.Int); // eax
                    break;
                }

                default:
                    Error(call.CalleeSpan, $"unknown method '{call.Callee}'");
                    break;
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
        static PhiType ArithmeticType(PhiType a, PhiType b) =>
            a.IsUnsigned32 || b.IsUnsigned32 ? PhiType.U32 : PhiType.I32;

        void BindExpr(Expr expr, Scope scope)
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
                    name.Symbol = symbol;
                    name.Type = symbol switch
                    {
                        VariableSymbol v => v.Type,
                        ConstantSymbol k => k.Type,
                        LengthSymbol => PhiType.Int,
                        FieldPathSymbol f => f.Type,
                        _ => PhiType.Error,
                    };
                    if (symbol == null) Error(name.Span, DescribeMissing(name.Name, scope));
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

                    FieldSymbol? field = t.IsStruct ? t.Struct!.Find(member.Member) : null;
                    if (field == null)
                    {
                        Error(member.MemberSpan, t.IsStruct ? $"{t} has no field '{member.Member}'" : $"a {t} has no fields");
                        member.Type = PhiType.Error;
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
                        addr.CodeLabel = m.Label;
                        name.Type = PhiType.Void;
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
                NameExpr { Symbol: VariableSymbol or FieldPathSymbol } => true,
                IndexExpr or MemberExpr => true,
                _ => false,
            };
            if (!place) Error(addr.Operand.Span, "addr needs a variable, field, element or method");
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

        /// <summary>
        /// Checks that a value fits where it's going. A one-character string like 'w' becomes
        /// its character code wherever a number is expected. Numbers of any size convert to
        /// each other (larger values are cut to fit when stored).
        /// </summary>
        Expr Convert(Expr e, PhiType target, string what)
        {
            if (e.Type.IsError || target.IsError) return e;

            if (target.IsNumeric)
            {
                if (e.Type.IsNumeric) return e;
                if (target.IsPointer && e.Type.IsString) return e; // a str's value is its address
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

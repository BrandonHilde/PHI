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
    }

    public sealed class BoundProgram
    {
        public SourceFile File { get; init; } = null!;
        public BoundUnit Boot { get; init; } = null!;
        public BoundUnit? Kernel { get; init; }
    }

    /// <summary>
    /// Resolves names, works out types and storage, and checks the program. It also rewrites
    /// compound assignments (x++ 2) into plain ones (x is x + 2) so code generation stays small.
    /// </summary>
    public sealed class Binder
    {
        public const string BootBase = "Bootloader";
        public const string KernelBase = "OS";

        /// <summary>A str filled from a number needs room for "-2147483648".</summary>
        const int IntTextCapacity = 12;

        /// <summary>Capacity of a str copied from another str whose size isn't known yet.</summary>
        const int DefaultStrCapacity = 64;

        readonly SourceFile file;
        readonly DiagnosticBag diagnostics;

        sealed class Scope
        {
            public ClassDecl Class { get; init; } = null!;
            public UnitKind Unit { get; init; }
            public MethodSymbol? Method { get; init; }
            public Dictionary<string, VariableSymbol> Variables { get; } = new();
        }

        readonly Dictionary<ClassDecl, Scope> classScopes = new();
        readonly Dictionary<MethodDecl, Scope> methodScopes = new();
        readonly Dictionary<UnitKind, BoundUnit> units = new();
        readonly Dictionary<(UnitKind, string), MethodSymbol> methodsByFullName = new();
        readonly Dictionary<string, RawBlockDecl> rawBlocks = new();
        readonly HashSet<VarDecl> loopCounters = new();

        Binder(SourceFile file, DiagnosticBag diagnostics)
        {
            this.file = file;
            this.diagnostics = diagnostics;
        }

        public static BoundProgram? Bind(ProgramNode program, DiagnosticBag diagnostics) =>
            new Binder(program.File, diagnostics).BindProgram(program);

        void Error(Span span, string message) => diagnostics.Error(file, span, message);

        // ================================================================ declarations

        BoundProgram? BindProgram(ProgramNode program)
        {
            var bootClasses = program.Classes.Where(c => c.Base == BootBase).ToList();
            if (bootClasses.Count == 0)
            {
                diagnostics.Error(file, new Span(0, 0), $"a program needs a class that inherits {BootBase}, e.g. phi.Hello:{BootBase} {{ log 'hi'; }}");
                return null;
            }
            foreach (ClassDecl extra in bootClasses.Skip(1))
                Error(extra.BaseSpan, $"only one class can inherit {BootBase}; it becomes the 512-byte boot sector");

            var boot = new BoundUnit { Kind = UnitKind.Boot };
            units[UnitKind.Boot] = boot;
            BoundUnit? kernel = null;

            foreach (ClassDecl cls in program.Classes)
            {
                UnitKind unit;
                if (cls.Base == BootBase) unit = UnitKind.Boot;
                else if (cls.Base == KernelBase) unit = UnitKind.Kernel;
                else
                {
                    string why = cls.Base == "" ? "has no base class" : $"inherits '{cls.Base}', which PHI doesn't know";
                    Error(cls.BaseSpan, $"class {cls.Name} {why}; use :{BootBase} for the boot sector or :{KernelBase} for code loaded after it");
                    continue;
                }

                if (unit == UnitKind.Kernel && kernel == null)
                {
                    kernel = new BoundUnit { Kind = UnitKind.Kernel };
                    units[UnitKind.Kernel] = kernel;
                }

                units[unit].Classes.Add(cls);
                classScopes[cls] = new Scope { Class = cls, Unit = unit };
            }

            foreach (RawBlockDecl raw in program.RawBlocks)
            {
                if (!rawBlocks.TryAdd(raw.Name, raw))
                    Error(raw.Span, $"an assembly block named {raw.Name} already exists");
            }

            foreach (var (cls, scope) in classScopes)
            {
                DeclareVariables(scope, CollectDeclarations(cls.Body), isParameter: false);
                foreach (MethodDecl m in cls.Methods) DeclareMethod(scope, m);
            }

            AssignVariableLabels();

            foreach (var (cls, scope) in classScopes)
            {
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
                        m.Symbol.ReturnType = m.Result.Type.IsString ? PhiType.Str : PhiType.Int;
                        if (!m.Result.Type.IsError && !m.Result.Type.IsNumeric && !m.Result.Type.IsString)
                            Error(m.Result.Span, $"a method can return a number or a str, not a {m.Result.Type}");
                    }
                }
            }

            if (boot.UsedBuiltins.Any(b => b.Name == "Bootloader.JumpToSectorTwo") && kernel == null)
                Error(boot.Classes[0].BaseSpan, $"Bootloader.JumpToSectorTwo needs a class that inherits {KernelBase} to jump to");

            return new BoundProgram { File = file, Boot = boot, Kernel = kernel };
        }

        /// <summary>Every declaration in a body, including those nested in if and while blocks.</summary>
        IEnumerable<VarDecl> CollectDeclarations(IEnumerable<Stmt> body)
        {
            foreach (Stmt s in body)
            {
                switch (s)
                {
                    case VarDecl v:
                        yield return v;
                        break;
                    case IfStmt i:
                        foreach (VarDecl v in CollectDeclarations(i.Then)) yield return v;
                        if (i.Else != null)
                            foreach (VarDecl v in CollectDeclarations(i.Else)) yield return v;
                        break;
                    case WhileStmt w:
                        if (w.Init != null)
                        {
                            loopCounters.Add(w.Init);
                            yield return w.Init;
                        }
                        foreach (VarDecl v in CollectDeclarations(w.Body)) yield return v;
                        break;
                }
            }
        }

        void DeclareVariables(Scope scope, IEnumerable<VarDecl> decls, bool isParameter)
        {
            foreach (VarDecl decl in decls)
            {
                if (Parser.ReservedWords.Contains(decl.Name))
                {
                    Error(decl.NameSpan, $"'{decl.Name}' is a keyword and can't be a variable name");
                    continue;
                }

                if (scope.Variables.TryGetValue(decl.Name, out VariableSymbol? existing))
                {
                    // two loops can each declare the same counter: while int i: 0; ...
                    if (loopCounters.Contains(decl) && existing.Decl.Type == decl.Type && existing.Decl.BufferSize == null)
                    {
                        decl.Symbol = existing;
                        existing.NeedsInitCode = true;
                        continue;
                    }
                    Error(decl.NameSpan, $"'{decl.Name}' is already declared in {ScopeName(scope)}");
                    continue;
                }

                var symbol = new VariableSymbol { Name = decl.Name, Decl = decl, Unit = scope.Unit, IsParameter = isParameter };
                decl.Symbol = symbol;
                scope.Variables[decl.Name] = symbol;
                units[scope.Unit].Variables.Add(symbol);
                DetermineStorage(symbol, scope);

                if (isParameter) scope.Method!.Parameters.Add(symbol);
            }
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

            UnitKind unit = classScope.Unit;
            string fullName = dotted ? name : $"{classScope.Class.Name}.{name}";
            string label = hook?.Label ?? $"M_{classScope.Class.Name}_{name}";

            var symbol = new MethodSymbol { Name = fullName, Label = label, Decl = method, Unit = unit, IsHook = hook != null };

            if (!methodsByFullName.TryAdd((unit, fullName), symbol))
            {
                Error(method.Span, $"method {fullName} is already defined");
                return;
            }

            method.Symbol = symbol;
            units[unit].Methods.Add(symbol);

            var scope = new Scope { Class = classScope.Class, Unit = unit, Method = symbol };
            methodScopes[method] = scope;
            DeclareVariables(scope, method.Parameters, isParameter: true);
            DeclareVariables(scope, CollectDeclarations(method.Body), isParameter: false);
        }

        /// <summary>Decides type, element count and str capacity from the declaration.</summary>
        void DetermineStorage(VariableSymbol v, Scope scope)
        {
            VarDecl d = v.Decl;
            TypeKind kind = d.Type switch
            {
                TypeKeyword.Int => TypeKind.Int,
                TypeKeyword.Byt => TypeKind.Byte,
                TypeKeyword.Bln => TypeKind.Bool,
                TypeKeyword.Str => TypeKind.Str,
                TypeKeyword.Var => d.Values.Count > 0 && d.Values.All(e => e is StringExpr) ? TypeKind.Str : TypeKind.Int,
                _ => TypeKind.Error,
            };

            if (kind == TypeKind.Error)
            {
                Error(d.Span, $"{d.Type.ToString().ToLower()} (decimal numbers) isn't supported yet");
                v.Type = PhiType.Error;
                return;
            }

            if (d.BufferSize is int size)
            {
                if (kind == TypeKind.Str)
                {
                    v.Type = PhiType.Str;
                    v.Capacity = size + 1;
                }
                else
                {
                    v.Type = new PhiType(kind, IsArray: true);
                    v.Count = size;
                }
                return;
            }

            if (kind == TypeKind.Str)
            {
                if (d.Values.Count > 1)
                {
                    // every element gets the same room, so any element can be replaced by index
                    v.Type = PhiType.Str.AsArray;
                    v.Count = d.Values.Count;
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

            if (d.Values.Count > 1)
            {
                v.Type = new PhiType(kind, IsArray: true);
                v.Count = d.Values.Count;
            }
            else
            {
                v.Type = new PhiType(kind);
            }
        }

        /// <summary>
        /// Class variables are VALUE_name (so asm blocks can use {name}); if two classes in the
        /// same unit use a name, both get the class added. Method variables are always qualified.
        /// </summary>
        void AssignVariableLabels()
        {
            foreach (BoundUnit unit in units.Values)
            {
                var classLevel = unit.Variables.Where(v => !IsMethodVariable(v)).ToList();
                var shared = classLevel.GroupBy(v => v.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

                foreach (var (cls, scope) in classScopes.Where(p => p.Value.Unit == unit.Kind))
                    foreach (VariableSymbol v in scope.Variables.Values)
                        v.Label = shared.Contains(v.Name) ? $"VALUE_{cls.Name}_{v.Name}" : $"VALUE_{v.Name}";

                foreach (var (method, scope) in methodScopes.Where(p => p.Value.Unit == unit.Kind))
                    foreach (VariableSymbol v in scope.Variables.Values)
                    {
                        v.Label = $"VALUE_{method.Symbol!.Label}_{v.Name}";
                        v.IsLocal = true;
                    }
            }
        }

        bool IsMethodVariable(VariableSymbol v) => methodScopes.Values.Any(s => s.Variables.ContainsValue(v));

        // ================================================================ lookup

        Symbol? Lookup(string name, Scope scope)
        {
            if (scope.Method != null && methodScopes.TryGetValue(scope.Method.Decl, out Scope? ms)
                && ms.Variables.TryGetValue(name, out VariableSymbol? local))
                return local;

            if (classScopes[scope.Class].Variables.TryGetValue(name, out VariableSymbol? field))
                return field;

            // a variable from another class in the same unit, if only one class has it
            var others = classScopes.Values
                .Where(s => s.Unit == scope.Unit && s.Class != scope.Class && s.Variables.ContainsKey(name))
                .Select(s => s.Variables[name]).ToList();
            if (others.Count == 1) return others[0];

            if (Builtins.Constants.TryGetValue(name, out long value))
                return new ConstantSymbol { Name = name, Value = value };

            int dot = name.LastIndexOf('.');
            if (dot > 0)
            {
                string head = name[..dot];
                string member = name[(dot + 1)..];

                if (member == "len" && Lookup(head, scope) is VariableSymbol target)
                    return new LengthSymbol { Name = name, Of = target };

                // ClassName.variable
                ClassDecl? cls = classScopes.Keys.FirstOrDefault(c => c.Name == head && classScopes[c].Unit == scope.Unit);
                if (cls != null && classScopes[cls].Variables.TryGetValue(member, out VariableSymbol? qualified))
                    return qualified;
            }

            return null;
        }

        string DescribeMissing(string name, Scope scope)
        {
            UnitKind other = scope.Unit == UnitKind.Boot ? UnitKind.Kernel : UnitKind.Boot;
            bool inOtherUnit = classScopes.Values.Any(s => s.Unit == other && s.Variables.ContainsKey(name));
            if (inOtherUnit)
            {
                string where = other == UnitKind.Boot ? $"the {BootBase} class" : $"an {KernelBase} class";
                return $"'{name}' is declared in {where}; the boot sector and the OS classes are built separately and can't share variables";
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
                    return decl;

                case LogStmt log:
                    foreach (Expr value in log.Values)
                    {
                        BindExpr(value, scope);
                        if (!value.Type.IsError && !value.Type.IsNumeric && !value.Type.IsString)
                            Error(value.Span, $"can't print a {value.Type}; print one element at a time, like name:i");
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

            // a method or loop variable is set every time its declaration runs;
            // a class variable with constant values is simply stored that way
            if (decl.Values.Count > 0 && (!allConstant || !classLevel || v.IsParameter))
                v.NeedsInitCode = true;
        }

        /// <summary>A long, a string, or null if the value isn't known at compile time.</summary>
        static object? ConstantValue(Expr e) => e switch
        {
            StringExpr s => s.Value,
            _ => ConstantFolder.Evaluate(e),
        };

        Stmt BindAssign(AssignStmt assign, Scope scope)
        {
            BindExpr(assign.Target, scope);
            BindExpr(assign.Value, scope);

            Expr target = assign.Target;
            if (target.Type.IsError || assign.Value.Type.IsError) return assign;

            bool assignable = target switch
            {
                NameExpr { Symbol: VariableSymbol } => true,
                IndexExpr => true,
                _ => false,
            };

            if (!assignable)
            {
                Error(target.Span, "only variables and array elements can be assigned");
                return assign;
            }

            if (target.Type.IsArray)
            {
                Error(target.Span, $"can't assign a whole array; set one element at a time, like {Describe(target)}:0 is 5;");
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
                value = new BinaryExpr { Op = op, Left = target, Right = value, Span = assign.Span, Type = PhiType.Int };
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

                    int count = method.Decl.Parameters.Count;
                    if (call.Arguments.Count > count)
                        Error(call.Arguments[count].Span, $"{method.Name} takes {Plural(count, "argument")} but was given {call.Arguments.Count}");

                    for (int i = 0; i < Math.Min(count, call.Arguments.Count); i++)
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

                    unit.UsedBuiltins.Add(builtin);

                    int count = builtin.Parameters.Length;
                    if (call.Arguments.Count != count)
                    {
                        string expected = count == 0 ? "no arguments" : $"{Plural(count, "argument")} ({string.Join(" ", builtin.Parameters)})";
                        Error(call.Span, $"{builtin.Name} takes {expected} but was given {call.Arguments.Count}");
                    }

                    for (int i = 0; i < Math.Min(count, call.Arguments.Count); i++)
                        call.Arguments[i] = Convert(call.Arguments[i], PhiType.Int, $"'{builtin.Parameters[i]}'");

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
                    if (!unit.RawBlocks.Contains(raw)) unit.RawBlocks.Add(raw);
                    if (call.ResultTarget != null) CheckResultTarget(call, PhiType.Int); // eax
                    break;
                }

                default:
                    Error(call.CalleeSpan, $"unknown method '{call.Callee}'");
                    break;
            }
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
            UnitKind other = scope.Unit == UnitKind.Boot ? UnitKind.Kernel : UnitKind.Boot;
            if (methodsByFullName.TryGetValue((other, name), out m)) return m;
            var otherMatches = methodsByFullName.Where(p => p.Key.Item1 == other && p.Key.Item2.EndsWith("." + name)).ToList();
            if (otherMatches.Count == 1) return otherMatches[0].Value;

            return null;
        }

        void CheckResultTarget(CallStmt call, PhiType result)
        {
            Expr target = call.ResultTarget!;
            if (target.Type.IsError) return;

            if (target is not (NameExpr { Symbol: VariableSymbol } or IndexExpr) || target.Type.IsArray)
            {
                Error(target.Span, "the result of a call must go into a variable");
                return;
            }

            if (target.Type.IsNumeric && result.IsString)
                Error(target.Span, $"{call.Callee} returns a str, which can't be stored in {target.Type} '{Describe(target)}'");
        }

        static string UnitName(UnitKind unit) => unit == UnitKind.Boot ? "boot sector (Bootloader class)" : "OS classes";
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

        void BindExpr(Expr expr, Scope scope)
        {
            switch (expr)
            {
                case NumberExpr:
                    expr.Type = PhiType.Int;
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
                    switch (symbol)
                    {
                        case VariableSymbol v: name.Type = v.Type; break;
                        case ConstantSymbol: name.Type = PhiType.Int; break;
                        case LengthSymbol: name.Type = PhiType.Int; break;
                        default:
                            Error(name.Span, DescribeMissing(name.Name, scope));
                            name.Type = PhiType.Error;
                            break;
                    }
                    break;
                }

                case IndexExpr index:
                {
                    BindExpr(index.Target, scope);
                    BindExpr(index.Index, scope);
                    PhiType t = index.Target.Type;

                    if (t.IsError) { index.Type = PhiType.Error; break; }

                    if (index.Target is not NameExpr { Symbol: VariableSymbol })
                    {
                        Error(index.Span, "only variables can be indexed");
                        index.Type = PhiType.Error;
                        break;
                    }

                    RequireNumeric(index.Index, "an index");

                    if (t.IsArray) index.Type = t.Element;
                    else if (t.IsString) index.Type = PhiType.Byte; // one character
                    else
                    {
                        Error(index.Span, $"'{Describe(index.Target)}' is a {t}, which can't be indexed");
                        index.Type = PhiType.Error;
                    }
                    break;
                }

                case UnaryExpr unary:
                    BindExpr(unary.Operand, scope);
                    RequireNumeric(unary.Operand, unary.Op == UnaryOp.Negate ? "'-'" : "'not'");
                    unary.Type = unary.Op == UnaryOp.Negate ? PhiType.Int : PhiType.Bool;
                    break;

                case BinaryExpr binary:
                    BindBinary(binary, scope);
                    break;

                default:
                    throw new InvalidOperationException($"unexpected expression {expr.GetType().Name}");
            }
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
            b.Type = b.IsComparison || b.IsLogical ? PhiType.Bool : PhiType.Int;
        }

        /// <summary>
        /// Checks that a value fits where it's going. A one-character string like 'w' becomes
        /// its character code wherever a number is expected.
        /// </summary>
        Expr Convert(Expr e, PhiType target, string what)
        {
            if (e.Type.IsError || target.IsError) return e;

            if (target.IsNumeric)
            {
                if (e.Type.IsNumeric) return e;
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

    /// <summary>Evaluates expressions whose value is known at compile time.</summary>
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
                case NameExpr { Symbol: LengthSymbol { Of.Type.IsArray: true } len }: return len.Of.Count;
                case UnaryExpr u:
                {
                    long? v = Evaluate(u.Operand);
                    if (v == null) return null;
                    return u.Op == UnaryOp.Negate ? Wrap(-v.Value) : (v.Value == 0 ? 1 : 0);
                }
                case BinaryExpr b when !b.Left.Type.IsString:
                {
                    long? l = Evaluate(b.Left), r = Evaluate(b.Right);
                    if (l == null || r == null) return null;
                    int x = (int)l.Value, y = (int)r.Value;
                    return b.Op switch
                    {
                        BinaryOp.Add => Wrap((long)x + y),
                        BinaryOp.Subtract => Wrap((long)x - y),
                        BinaryOp.Multiply => Wrap((long)x * y),
                        BinaryOp.Divide => y == 0 ? null : Wrap(x / y),
                        BinaryOp.Modulo => y == 0 ? null : Wrap(x % y),
                        BinaryOp.Equal => x == y ? 1 : 0,
                        BinaryOp.NotEqual => x != y ? 1 : 0,
                        BinaryOp.Less => x < y ? 1 : 0,
                        BinaryOp.LessEqual => x <= y ? 1 : 0,
                        BinaryOp.Greater => x > y ? 1 : 0,
                        BinaryOp.GreaterEqual => x >= y ? 1 : 0,
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

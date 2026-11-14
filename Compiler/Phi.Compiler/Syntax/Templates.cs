using Phi.Compiler.Semantics;

namespace Phi.Compiler.Syntax
{
    /// <summary>
    /// Structs that take types. struct.Stack&lt;Item&gt; is written once, and a program that says
    /// Stack&lt;int&gt; and Stack&lt;Player&gt; gets two structs, each the template's text read with the
    /// type in Item's place. That's done here, once every file is parsed and before any name is
    /// looked up, so everything after (the binder, the code generator) sees ordinary structs
    /// whose names happen to be "Stack&lt;int&gt;" and "Stack&lt;Player&gt;".
    /// </summary>
    public static class Templates
    {
        /// <summary>The most structs one program's templates may become: one that uses itself with a bigger type each time never ends.</summary>
        const int Most = 2000;

        public static void Expand(ProgramNode program, DiagnosticBag diagnostics)
        {
            if (program.Templates.Count == 0 && program.TemplateUses.Count == 0
                && program.MethodTemplates.Count == 0 && program.MethodTemplateUses.Count == 0) return;

            var templates = new Dictionary<string, StructTemplate>();
            var plain = program.Structs.Select(s => s.Name).Concat(program.Enums.Select(e => e.Name)).ToHashSet();
            foreach (StructTemplate t in program.Templates)
            {
                if (plain.Contains(t.Name))
                    diagnostics.Error(t.File, t.NameSpan, $"{t.Name} is already a struct or an enum");
                else if (!templates.TryAdd(t.Name, t))
                    diagnostics.Error(t.File, t.NameSpan, $"struct {t.Name} is already defined");
            }
            foreach (StructTemplate t in templates.Values)
                foreach (var (name, span) in t.Parameters)
                    if (plain.Contains(name) || templates.ContainsKey(name))
                        diagnostics.Error(t.File, span, $"'{name}' is a type already (a struct or an enum of this program); here it would name whatever type {t.Name} is used with, so give that another name");

            var methods = new MethodTemplates(program, diagnostics);
            var checks = new List<(string Type, (string Name, Span Span) Has, string For, SourceFile File, Span Span, MethodDecl? Hold)>();

            // each use makes its struct, which may use others (a Map<str Stack<int>> has a
            // Stack<int> in it), and each call its method, which may call others and use
            // structs, as a struct's methods may call them: until there are no more
            var made = new HashSet<string>();
            int next = 0, nextCall = 0;
            while (next < program.TemplateUses.Count || nextCall < program.MethodTemplateUses.Count || methods.More)
            {
                if (next >= program.TemplateUses.Count)
                {
                    // (a struct just made may have a method that calls read before it were waiting for)
                    foreach (var (waited, call) in methods.Read())
                        if (!MakeMethod(waited, call, program, made, checks, diagnostics)) return;
                    if (nextCall < program.MethodTemplateUses.Count
                        && !MakeMethods(program.MethodTemplateUses[nextCall++], program, methods, made, checks, diagnostics)) return;
                    continue;
                }
                TemplateUse use = program.TemplateUses[next++];
                if (made.Contains(use.FullName)) continue;
                if (!templates.TryGetValue(use.Template, out StructTemplate? template))
                {
                    if (plain.Contains(use.Template))
                        diagnostics.Error(use.File, use.Span, $"{use.Template} takes no types: it's declared struct.{use.Template}, not struct.{use.Template}<...>");
                    else
                        diagnostics.Error(use.File, use.Span, $"unknown type '{use.Template}' (in {use.FullName})");
                    made.Add(use.FullName);
                    continue;
                }
                if (use.Arguments.Count != template.Parameters.Count)
                {
                    string takes = string.Join(" ", template.Parameters.Select(p => p.Name));
                    diagnostics.Error(use.File, use.Span,
                        $"{template.Name} takes {Count(template.Parameters.Count)} ({template.Name}<{takes}>), but {use.FullName} gives it {Count(use.Arguments.Count)}");
                    made.Add(use.FullName);
                    continue;
                }
                made.Add(use.FullName);
                if (made.Count > Most)
                {
                    diagnostics.Error(use.File, use.Span, $"{use.Template} is used with more than {Most} sets of types, the last {use.FullName}: does it use itself with a bigger type each time?");
                    return;
                }
                // (read from a copy of the template's file: see DiagnosticBag.CopyOf)
                // (a template from a compiled library's interface is there whole, and the struct
                // made from it is this program's own code, not the library's)
                var copy = new SourceFile(template.File.Path, template.File.Text);
                diagnostics.CopyOf(copy, template.File,
                    $" (in {use.FullName}, which line {use.File.GetLocation(use.Span.Start).Line} of {Path.GetFileName(use.File.Path)} uses)");
                for (int i = 0; i < template.Parameters.Count; i++)
                    if (template.MustHave[i] is (string, Span) has) checks.Add((use.Arguments[i].Name, has, use.FullName, use.File, use.Span, null));
                Parser.Instantiate(template, use.Arguments, use.FullName, program, copy);
            }
            methods.SayNoneFound(program, diagnostics);

            // <Item has Ordered>: once every struct there will be is there to ask
            var interfaces = program.Structs.Where(s => s.IsInterface).Select(s => s.Name).ToHashSet();
            var owned = program.Structs.Where(s => !s.IsInterface).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Has.Select(h => h.Name).ToHashSet());
            var said = new HashSet<string>();
            foreach (var check in checks)
            {
                if (!said.Add(check.For)) continue;
                string takes = check.For[(check.For.LastIndexOf('.') + 1)..];
                takes = takes[..takes.IndexOf('<')];
                string? wrong =
                    !interfaces.Contains(check.Has.Name) ? $"{takes} takes a type that has {check.Has.Name}, which isn't an interface of this program"
                    : !owned.TryGetValue(check.Type, out var its) ? $"{takes} takes a type that has {check.Has.Name}, and {check.Type} isn't a struct, so it has no interfaces"
                    : !its.Contains(check.Has.Name) ? $"{takes} takes a type that has {check.Has.Name}, and {check.Type} doesn't: it's declared struct.{check.Type}{(its.Count > 0 ? " has " + string.Join(" ", its) : "")}"
                    : null;
                if (wrong == null) continue;
                // (one made for a call on an object is in every struct with such a method: only the one called says it)
                if (check.Hold != null) check.Hold.HeldErrors.Add((check.File, check.Span, wrong));
                else diagnostics.Error(check.File, check.Span, wrong);
            }
        }

        /// <summary>
        /// A program's methods that take types: a class's by the class and their name, and a
        /// struct's by their name too, for a call on an object (stack.Convert&lt;f64&gt;), whose
        /// struct isn't known where the call is read. Such a call has its method made in every
        /// struct with a method of that name taking that many types, whenever the struct is made;
        /// the ones nothing calls are left out if something's wrong in them, as an unused method
        /// of a struct that takes types is.
        /// </summary>
        sealed class MethodTemplates
        {
            readonly ProgramNode program;
            readonly DiagnosticBag diagnostics;
            readonly Dictionary<(string Class, string Name), MethodTemplate> byOwner = new();
            readonly Dictionary<string, List<MethodTemplate>> ofStructs = new();
            readonly Dictionary<string, List<MethodTemplateUse>> onObjects = new();
            readonly HashSet<MethodTemplateUse> found = new();
            int read;

            public MethodTemplates(ProgramNode program, DiagnosticBag diagnostics)
            {
                this.program = program;
                this.diagnostics = diagnostics;
            }

            /// <summary>Are there templates not yet read in (a struct just made has some)?</summary>
            public bool More => read < program.MethodTemplates.Count;

            /// <summary>Reads in the templates there are by now; gives each new one of a struct with each call on an object it may be for.</summary>
            public List<(MethodTemplate, MethodTemplateUse)> Read()
            {
                var waiting = new List<(MethodTemplate, MethodTemplateUse)>();
                while (read < program.MethodTemplates.Count)
                {
                    MethodTemplate t = program.MethodTemplates[read++];
                    if (t.Owner.Methods.Any(m => m.Name == t.Name))
                        diagnostics.Error(t.File, t.NameSpan, $"{t.Owner.Name} already has a method {t.Name}; one that takes types needs a name of its own");
                    else if (!byOwner.TryAdd((t.Owner.Name, t.Name), t))
                        diagnostics.Error(t.File, t.NameSpan, $"method {t.Name} is already defined in {t.Owner.Name}");
                    else if (t.Owner.ForStruct != null)
                    {
                        if (!ofStructs.TryGetValue(t.Name, out var same)) ofStructs[t.Name] = same = new();
                        same.Add(t);
                        if (onObjects.TryGetValue(t.Name, out var calls))
                            foreach (MethodTemplateUse call in calls)
                                if (call.Arguments.Count == t.Parameters.Count)
                                {
                                    found.Add(call);
                                    waiting.Add((t, call));
                                }
                    }
                }
                return waiting;
            }

            public MethodTemplate? Of(string owner, string name) => byOwner.GetValueOrDefault((owner, name));

            /// <summary>For a call on an object: the structs' methods of its name there are so far (those taking as many types are its).</summary>
            public List<MethodTemplate> OnObject(MethodTemplateUse call)
            {
                if (!onObjects.TryGetValue(call.Template, out var calls)) onObjects[call.Template] = calls = new();
                calls.Add(call);
                var its = ofStructs.GetValueOrDefault(call.Template)?.Where(t => t.Parameters.Count == call.Arguments.Count).ToList() ?? new();
                if (its.Count > 0) found.Add(call);
                return its;
            }

            /// <summary>The calls on objects that no struct turned out to have a method for.</summary>
            public void SayNoneFound(ProgramNode program, DiagnosticBag diagnostics)
            {
                foreach (MethodTemplateUse call in onObjects.Values.SelectMany(c => c).Where(c => !found.Contains(c)))
                {
                    string first = call.Class!.Split('.')[0];
                    if (ofStructs.TryGetValue(call.Template, out var same))
                    {
                        MethodTemplate t = same[0];
                        string takes = string.Join(" ", t.Parameters.Select(p => p.Name));
                        diagnostics.Error(call.File, call.Span,
                            $"{t.Name} takes {Count(t.Parameters.Count)} ({t.Name}<{takes}>), but {call.FullName} gives it {Count(call.Arguments.Count)}");
                    }
                    else if (program.Classes.Any(c => c.ForStruct != null && c.Methods.Any(m => m.Name == call.Template)))
                        diagnostics.Error(call.File, call.Span, $"{call.Template} takes no types: it's declared [{call.Template}], not [{call.Template}<...>]");
                    else
                        diagnostics.Error(call.File, call.Span,
                            $"no struct has a method {call.Template} that takes types (declared [{call.Template}<Item>: ...]), and there's no class {first}");
                }
            }
        }

        /// <summary>The method (or, for a call on an object, the methods) one call's types make; false when there are too many.</summary>
        static bool MakeMethods(MethodTemplateUse use, ProgramNode program, MethodTemplates templates,
                                HashSet<string> made, List<(string, (string, Span), string, SourceFile, Span, MethodDecl?)> checks, DiagnosticBag diagnostics)
        {
            foreach (var (waited, call) in templates.Read())
                if (!MakeMethod(waited, call, program, made, checks, diagnostics)) return false;

            string cls = use.Class ?? use.InClass;
            bool isClass = program.Classes.Any(c => c.Name == cls && c.ForStruct == null);
            // stack.Convert<f64>, on an object: stack's struct isn't known here, so in each struct with one
            if (use.Class != null && !use.Exact && !isClass)
            {
                foreach (MethodTemplate each in templates.OnObject(use))
                    if (!MakeMethod(each, use, program, made, checks, diagnostics)) return false;
                return true;
            }

            // a compiled library's class has the ones it was built with, as methods
            if (program.Classes.Any(c => c.Name == cls && c.Methods.Any(m => m.Name == use.FullName))) return true;
            if (templates.Of(cls, use.Template) is not MethodTemplate template)
            {
                if (!made.Add(" " + cls + "." + use.FullName)) return true;   // (said once)
                bool plain = program.Classes.Any(c => c.Name == cls && c.Methods.Any(m => m.Name == use.Template));
                diagnostics.Error(use.File, use.Span,
                    plain ? $"{use.Template} takes no types: it's declared [{use.Template}], not [{use.Template}<...>]"
                    : $"{cls} has no method {use.Template} that takes types (declared [{use.Template}<Item>: ...])");
                return true;
            }
            if (use.Arguments.Count != template.Parameters.Count)
            {
                if (!made.Add(" " + cls + "." + use.FullName)) return true;
                string takes = string.Join(" ", template.Parameters.Select(p => p.Name));
                diagnostics.Error(use.File, use.Span,
                    $"{template.Name} takes {Count(template.Parameters.Count)} ({template.Name}<{takes}>), but {use.FullName} gives it {Count(use.Arguments.Count)}");
                return true;
            }
            return MakeMethod(template, use, program, made, checks, diagnostics);
        }

        /// <summary>The method a template is for one call's types, added to its class or struct; false when there are too many.</summary>
        static bool MakeMethod(MethodTemplate template, MethodTemplateUse use, ProgramNode program,
                               HashSet<string> made, List<(string, (string, Span), string, SourceFile, Span, MethodDecl?)> checks, DiagnosticBag diagnostics)
        {
            string whole = $"{template.Owner.Name}.{use.FullName}";
            if (!made.Add(" " + whole)) return true;   // (a struct can't be named with a space first)
            // a compiled library's class or struct has the ones it was built with, as methods
            if (template.Owner.Methods.Any(m => m.Name == use.FullName)) return true;
            if (made.Count > Most)
            {
                diagnostics.Error(use.File, use.Span, $"{use.Template} is called with more than {Most} sets of types, the last {use.FullName}: does it call itself with a bigger type each time?");
                return false;
            }
            // (a struct made from a template is itself read from a copy of its file)
            SourceFile real = diagnostics.RealOf(template.File) ?? template.File;
            var copy = new SourceFile(template.File.Path, template.File.Text);
            diagnostics.CopyOf(copy, real,
                $" (in {use.FullName}, which line {use.File.GetLocation(use.Span.Start).Line} of {Path.GetFileName(use.File.Path)} calls)");
            MethodDecl? method = Parser.InstantiateMethod(template, use.Arguments, use.FullName, program, copy);
            // (on an object, what's wrong with the types is the called struct's method's to say)
            bool onObject = use.Class != null && !use.Exact && template.Owner.ForStruct != null;
            for (int i = 0; i < template.Parameters.Count; i++)
                if (template.MustHave[i] is (string, Span) has && (method != null || !onObject))
                    checks.Add((use.Arguments[i].Name, has, onObject ? whole : use.FullName, use.File, use.Span, onObject ? method : null));
            return true;
        }

        static string Count(int n) => n == 1 ? "1 type" : $"{n} types";

        /// <summary>
        /// A struct's (or its methods' class's) name as part of an assembly label: Stack&lt;int&gt; is
        /// Stack_of_int, and Map&lt;str ptr&lt;u8&gt;&gt; is Map_of_str_ptr_of_u8. A name with none of
        /// that in it is itself.
        /// </summary>
        public static string Label(string name)
        {
            if (name.IndexOf('<') < 0) return name;
            var label = new System.Text.StringBuilder();
            foreach (char c in name)
                label.Append(c switch { '<' => "_of_", '>' => "", ' ' => "_", ':' => "_to", _ => c.ToString() });
            return label.ToString();
        }
    }
}

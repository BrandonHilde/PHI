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

            var methods = MethodTemplates(program, diagnostics);
            var checks = new List<(string Type, (string Name, Span Span) Has, string For, SourceFile File, Span Span)>();

            // each use makes its struct, which may use others (a Map<str Stack<int>> has a
            // Stack<int> in it), and each call its method, which may call others and use
            // structs, as a struct's methods may call them: until there are no more
            var made = new HashSet<string>();
            int next = 0, nextCall = 0;
            while (next < program.TemplateUses.Count || nextCall < program.MethodTemplateUses.Count)
            {
                if (next >= program.TemplateUses.Count)
                {
                    if (!MakeMethod(program.MethodTemplateUses[nextCall++], program, methods, made, checks, diagnostics)) return;
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
                    if (template.MustHave[i] is (string, Span) has) checks.Add((use.Arguments[i].Name, has, use.FullName, use.File, use.Span));
                Parser.Instantiate(template, use.Arguments, use.FullName, program, copy);
            }

            // <Item has Ordered>: once every struct there will be is there to ask
            var interfaces = program.Structs.Where(s => s.IsInterface).Select(s => s.Name).ToHashSet();
            var owned = program.Structs.Where(s => !s.IsInterface).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Has.Select(h => h.Name).ToHashSet());
            var said = new HashSet<string>();
            foreach (var check in checks)
            {
                if (!said.Add(check.For)) continue;
                string takes = check.For[..check.For.IndexOf('<')];
                if (!interfaces.Contains(check.Has.Name))
                    diagnostics.Error(check.File, check.Span, $"{takes} takes a type that has {check.Has.Name}, which isn't an interface of this program");
                else if (!owned.TryGetValue(check.Type, out var its))
                    diagnostics.Error(check.File, check.Span, $"{takes} takes a type that has {check.Has.Name}, and {check.Type} isn't a struct, so it has no interfaces");
                else if (!its.Contains(check.Has.Name))
                    diagnostics.Error(check.File, check.Span, $"{takes} takes a type that has {check.Has.Name}, and {check.Type} doesn't: it's declared struct.{check.Type}{(its.Count > 0 ? " has " + string.Join(" ", its) : "")}");
            }
        }

        /// <summary>A program's methods that take types, by their class and name.</summary>
        static Dictionary<(string Class, string Name), MethodTemplate> MethodTemplates(ProgramNode program, DiagnosticBag diagnostics)
        {
            var templates = new Dictionary<(string, string), MethodTemplate>();
            foreach (MethodTemplate t in program.MethodTemplates)
            {
                if (t.Owner.Methods.Any(m => m.Name == t.Name))
                    diagnostics.Error(t.File, t.NameSpan, $"{t.Owner.Name} already has a method {t.Name}; one that takes types needs a name of its own");
                else if (!templates.TryAdd((t.Owner.Name, t.Name), t))
                    diagnostics.Error(t.File, t.NameSpan, $"method {t.Name} is already defined in {t.Owner.Name}");
            }
            return templates;
        }

        /// <summary>The method one call's types make of its template, added to its class; false when there are too many.</summary>
        static bool MakeMethod(MethodTemplateUse use, ProgramNode program, Dictionary<(string Class, string Name), MethodTemplate> templates,
                               HashSet<string> made, List<(string, (string, Span), string, SourceFile, Span)> checks, DiagnosticBag diagnostics)
        {
            string cls = use.Class ?? use.InClass;
            string whole = $"{cls}.{use.FullName}";
            if (!made.Add(" " + whole)) return true;   // (a struct can't be named with a space first)
            if (!templates.TryGetValue((cls, use.Template), out MethodTemplate? template))
            {
                // a compiled library's class has the ones it was built with, as methods
                if (program.Classes.Any(c => c.Name == cls && c.Methods.Any(m => m.Name == use.FullName))) return true;
                bool plain = program.Classes.Any(c => c.Name == cls && c.Methods.Any(m => m.Name == use.Template));
                bool isClass = program.Classes.Any(c => c.Name == cls && c.ForStruct == null);
                diagnostics.Error(use.File, use.Span,
                    plain ? $"{use.Template} takes no types: it's declared [{use.Template}], not [{use.Template}<...>]"
                    : use.Class == null ? $"{cls} has no method {use.Template} that takes types (declared [{use.Template}<Item>: ...])"
                    : isClass ? $"{cls} has no method {use.Template} that takes types (declared [{use.Template}<Item>: ...]); if {cls} is a compiled library, it has only the ones it was built with"
                    : $"there's no class {cls} with a method {use.Template} that takes types; a struct's own methods can't take types yet, only a class's (phi.Name:Library)");
                return true;
            }
            if (use.Arguments.Count != template.Parameters.Count)
            {
                string takes = string.Join(" ", template.Parameters.Select(p => p.Name));
                diagnostics.Error(use.File, use.Span,
                    $"{template.Name} takes {Count(template.Parameters.Count)} ({template.Name}<{takes}>), but {use.FullName} gives it {Count(use.Arguments.Count)}");
                return true;
            }
            if (made.Count > Most)
            {
                diagnostics.Error(use.File, use.Span, $"{use.Template} is called with more than {Most} sets of types, the last {use.FullName}: does it call itself with a bigger type each time?");
                return false;
            }
            for (int i = 0; i < template.Parameters.Count; i++)
                if (template.MustHave[i] is (string, Span) has) checks.Add((use.Arguments[i].Name, has, use.FullName, use.File, use.Span));
            var copy = new SourceFile(template.File.Path, template.File.Text);
            diagnostics.CopyOf(copy, template.File,
                $" (in {use.FullName}, which line {use.File.GetLocation(use.Span.Start).Line} of {Path.GetFileName(use.File.Path)} calls)");
            Parser.InstantiateMethod(template, use.Arguments, use.FullName, program, copy);
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

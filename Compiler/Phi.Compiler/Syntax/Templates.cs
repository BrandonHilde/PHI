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
            if (program.Templates.Count == 0 && program.TemplateUses.Count == 0) return;

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

            // each use makes its struct, which may use others (a Map<str Stack<int>> has a
            // Stack<int> in it): until there are no more
            var made = new HashSet<string>();
            for (int next = 0; next < program.TemplateUses.Count; next++)
            {
                TemplateUse use = program.TemplateUses[next];
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
                var copy = new SourceFile(template.File.Path, template.File.Text) { Compiled = template.File.Compiled };
                diagnostics.CopyOf(copy, template.File,
                    $" (in {use.FullName}, which line {use.File.GetLocation(use.Span.Start).Line} of {Path.GetFileName(use.File.Path)} uses)");
                Parser.Instantiate(template, use.Arguments, use.FullName, program, copy);
            }
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

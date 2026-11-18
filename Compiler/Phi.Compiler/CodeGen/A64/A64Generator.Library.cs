using System.Text;
using System.Text.RegularExpressions;
using Phi.Compiler.Libraries;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.A64
{
    // A compiled library for macOS programs (phi build --library): its own files' code as an
    // object file of Apple's (Builder assembles it, and the .philib keeps it whole), which the
    // linker puts in each program that uses it. What it gives a program is global: its classes'
    // start (phi_lib_NAME_start: their statements, in order), its methods, its variables, and
    // where each of its lines' code starts (for an error's line, in the program's phi_lines).
    // What it leaves to the program (the modules it uses, the runtime's routines, the type
    // tables of its structs) is the program's, whose labels are global when it has libraries.
    // Everything else (its strings, its structs' routines) is its own, so two libraries' never
    // clash.
    public sealed partial class A64Generator
    {
        /// <summary>A library's labels start with this: phi_lib_NAME.</summary>
        string libraryPrefix = "";

        /// <summary>A library's build: the type tables its code uses, which are the program's.</summary>
        readonly List<StructSymbol> libraryTypeTables = new();

        LibraryUnit? libraryUnit;

        string BuildLibrary()
        {
            LibraryBuild build = program.Library!;
            var result = new LibraryUnit();
            libraryUnit = result;
            CompiledTarget t = result.Target;
            t.Name = CompiledTarget.LabelName(build.Name);
            libraryPrefix = t.Prefix;
            if (unit.ForeignEntries.FirstOrDefault(m => !m.IsOutside) is MethodSymbol foreign)
                return NotYet(foreign.Decl.File, foreign.Decl.Span, "addr foreign (a method other code calls)");
            if (unit.RawBlocks.FirstOrDefault(r => !program.IsOutside(r.File) && r.Language != "arm") is RawBlockDecl raw)
                return NotYet(raw.File, raw.Span, $"an x86 assembly block ({raw.Language}.{raw.Name}; ARM's is arm.{raw.Name})");

            var starts = new List<string>();
            foreach (ClassDecl cls in unit.Classes.Where(c => !program.IsOutside(c.File)))
            {
                if (cls.Body.Count == 0) continue;
                file = cls.File;
                string label = $"{libraryPrefix}_class_{starts.Count}";
                var decl = new MethodDecl { Name = cls.Name, Owner = cls, Span = cls.Span };
                decl.Body.AddRange(cls.Body);
                var m = new MethodSymbol { Name = $"{cls.Name} (its own statements)", Label = label, Decl = decl, Unit = unit.Kind };
                if (EmitMethod(m, $"class {cls.Name}'s own statements ({Path.GetFileName(cls.File.Path)})")) starts.Add(label);
            }
            foreach (MethodSymbol m in unit.Methods.Where(m => !m.IsOutside))
            {
                file = m.Decl.File;
                EmitMethod(m, $"{m.Name} ({Path.GetFileName(m.Decl.File.Path)})");
            }
            foreach (RawBlockDecl block in unit.RawBlocks.Where(r => !program.IsOutside(r.File))) EmitRawBlock(block);
            if (failed) return "";
            code.AppendLine($"{libraryPrefix}_line_end:");
            EmitObjectRoutines();
            if (failed) return "";

            var data = new StringBuilder();
            EmitData(data);
            if (failed) return "";

            // what the object file won't say
            needed.ExceptWith(new[] { "phi_heap_alloc", "phi_heap_free" });
            t.RuntimeSymbols.AddRange(needed.OrderBy(n => n, StringComparer.Ordinal));
            t.RawBlocks.AddRange(unit.RawBlocks.Where(r => program.IsOutside(r.File)).Select(r => $"{r.Language}.{r.Name}"));
            t.OutsideFunctions.AddRange(unit.OutsideFunctions.Select(f => $"{f.Library}.{f.Name}"));
            t.TypeTables.AddRange(libraryTypeTables.Select(s => s.Name));
            t.MadeMethods.AddRange(unit.Methods.Where(m => m.IsOutside && m.Decl.OwnFile != null && m.Decl.Name.EndsWith('>'))
                .Select(m => (Owner: m.Decl.Owner.Name, m.Decl.Name, At: m.Decl.Name.IndexOf('<')))
                .Select(m => $"{m.Owner}\t{m.Name[..m.At]}\t{m.Name[(m.At + 1)..^1]}").Distinct());
            t.UsesObjects = needed.Contains("phi_object_line");
            for (int k = 0; k < lineMarks.Count; k++)
            {
                var (_, line, name) = lineMarks[k];
                t.Lines.Add(new LibraryLine(0, k, line, strings.First(p => p.Value == name).Key));
            }

            var exports = new List<string> { t.StartLabel, $"{libraryPrefix}_line_end" };
            exports.AddRange(unit.Methods.Where(m => !m.IsOutside).Select(m => Sym(m.Label)));
            exports.AddRange(unit.Variables.Where(v => !v.IsOutside && !v.IsLocal && !v.Type.IsError).Select(v => Sym(v.Label)).Distinct());
            exports.AddRange(Enumerable.Range(0, lineMarks.Count).Select(k => $"{libraryPrefix}_line_{k}"));
            string text = code.ToString() + data;
            var defined = Regex.Matches(text, @"^([A-Za-z_$][\w$.]*):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();
            defined.Add(t.StartLabel);

            var output = new StringBuilder();
            output.AppendLine($"; The library {build.Name} for macOS programs (Apple Silicon), generated by phi from {Path.GetFileName(program.File.Path)}");
            foreach (string label in exports.Where(defined.Contains)) output.AppendLine($"\t.globl {label}");
            output.AppendLine("\t.section __TEXT,__text,regular,pure_instructions");
            output.AppendLine("\t.p2align 2");
            output.AppendLine($"{t.StartLabel}:");
            output.AppendLine("\tstp x29, x30, [sp, #-16]!");
            output.AppendLine("\tmov x29, sp");
            foreach (string start in starts) output.AppendLine($"\tbl {start}");
            output.AppendLine("\tldp x29, x30, [sp], #16");
            output.AppendLine("\tret");
            output.Append(code);
            output.AppendLine();
            output.Append(data);
            return output.ToString();
        }
    }
}

using System.Text;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.Libraries
{
    /// <summary>
    /// Writes what a program sees of a library: for each of its source files, the same
    /// declarations with no code. It's PHI, read back by the same parser (methods end at their
    /// header, which also says what they return), so a struct is laid out, a method called and a
    /// default worked out as they would be from the source.
    ///
    ///   [Area: int scale; : int]              what it returns, after a second ':'
    ///   [Name: : str room 64]                 the room a caller gives a str result
    ///   [Fill: str target; : void writes target]   the str parameters it writes into
    /// </summary>
    public static class InterfaceWriter
    {
        public static List<InterfaceFile> Write(ProgramNode program, IReadOnlyList<SourceFile> own, IReadOnlyDictionary<UseDecl, (SourceFile From, SourceFile Target)> uses)
        {
            // each by its path from the library's own file, as two folders can each have a parser.phi
            string home = Path.GetDirectoryName(Path.GetFullPath(own[^1].Path))!;
            var files = new List<InterfaceFile>();
            foreach (SourceFile file in own)
                files.Add(new InterfaceFile(Path.GetRelativePath(home, Path.GetFullPath(file.Path)).Replace('\\', '/'), WriteFile(file, program, own, uses)));
            return files;
        }

        static string WriteFile(SourceFile file, ProgramNode program, IReadOnlyList<SourceFile> own, IReadOnlyDictionary<UseDecl, (SourceFile From, SourceFile Target)> uses)
        {
            var sb = new StringBuilder();
            // (a file used twice is read twice, so it's the same file by its key, not as an object)
            var ownKeys = own.Select(f => PhiCompiler.FileKey(f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (use, (from, target)) in uses)
                if (from == file && !ownKeys.Contains(PhiCompiler.FileKey(target.Path))) sb.AppendLine($"use {use.Path};");

            foreach (LibraryUseDecl library in program.Libraries.Where(l => l.File == file))
                sb.AppendLine($"use '{library.Path}' as {library.Name};");
            foreach (OutsideFunctionDecl f in program.OutsideFunctions.Where(f => f.File == file))
            {
                sb.Append($"{f.Library}.{f.Name}:");
                foreach (SysCallParameter p in f.Parameters) sb.Append($" {p.Type} {p.Name}");
                if (f.Result != null) sb.Append($": {f.Result}");
                sb.AppendLine(";");
            }

            foreach (EnumDecl e in program.Enums.Where(e => e.File == file && e.Symbol != null))
            {
                sb.AppendLine($"enum.{e.Name}\n{{");
                foreach (var (name, value) in e.Symbol!.Members) sb.AppendLine($"\t{name}: {value}");
                sb.AppendLine("}");
            }

            foreach (StructDecl s in program.Structs.Where(s => s.File == file))
                if (s.IsInterface) WriteInterface(sb, s);
                else WriteStruct(sb, s, program);

            foreach (ClassDecl c in program.Classes.Where(c => c.File == file && c.ForStruct == null))
            {
                sb.AppendLine($"phi.{c.Name}:{c.Base}\n{{");
                foreach (Stmt statement in c.Body)
                {
                    if (statement is ConstDecl { KnownValue: long value } k)
                        sb.AppendLine($"\tconst {k.Type} {k.Name}: {value};");
                    else if (statement is VarDecl { Symbol: not null, Text: Span text })
                        sb.AppendLine("\t" + Text(file, text));
                }
                foreach (MethodDecl m in c.Methods.Where(m => m.Symbol != null))
                    sb.AppendLine("\t" + MethodHeader(file, m, 0));
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        static void WriteInterface(StringBuilder sb, StructDecl s)
        {
            sb.AppendLine($"interface.{s.Name}\n{{");
            for (int i = 0; i < s.Methods.Count; i++)
            {
                MethodDecl m = s.Methods[i];
                PhiType? result = s.Symbol!.Signatures[i]?.Result;
                string parameters = string.Join(" ", m.Parameters.Select(p => Parameter(s.File, p)));
                string returns = result == null || result.Kind == TypeKind.Void ? "" : $": {TypeName(result)}";
                sb.AppendLine($"\t[{m.Name}: {parameters} {returns}]");
            }
            sb.AppendLine("}");
        }

        static void WriteStruct(StringBuilder sb, StructDecl s, ProgramNode program)
        {
            sb.Append($"struct.{s.Name}");
            if (s.IsForeign) sb.Append(" foreign");
            if (s.Has.Count > 0) sb.Append(" has " + string.Join(" ", s.Has.Select(h => h.Name)));
            sb.AppendLine("\n{");
            foreach (FieldDecl f in s.Fields) sb.AppendLine("\t" + Text(s.File, f.Text));
            ClassDecl? methods = program.Classes.FirstOrDefault(c => c.ForStruct == s);
            if (methods != null)
                foreach (MethodDecl m in methods.Methods.Where(m => m.Symbol != null))
                    sb.AppendLine("\t" + MethodHeader(s.File, m, 1));    // (the parser gives each its self again)
            sb.AppendLine("}");
        }

        static string MethodHeader(SourceFile file, MethodDecl m, int skip)
        {
            MethodSymbol symbol = m.Symbol!;
            var sb = new StringBuilder("[");
            if (m.IsInterruptHandler) sb.Append("isr ");
            sb.Append(m.Name);

            var parameters = m.Parameters.Skip(skip).ToList();
            var written = symbol.Parameters.Skip(skip).Where(p => p.Written).Select(p => p.Name).ToList();
            bool returns = symbol.ReturnType.Kind != TypeKind.Void;
            if (parameters.Count > 0 || returns || written.Count > 0)
            {
                sb.Append(':');
                foreach (VarDecl p in parameters) sb.Append(' ').Append(Parameter(file, p));
                sb.Append(" : ").Append(returns ? TypeName(symbol.ReturnType) : "void");
                if (symbol.ReturnType.IsString) sb.Append($" room {symbol.ResultCapacity}");
                if (written.Count > 0) sb.Append(" writes " + string.Join(" ", written));
            }
            return sb.Append(']').ToString();
        }

        static string Parameter(SourceFile file, VarDecl p)
        {
            string text = Text(file, p.Text!.Value).TrimEnd();
            return text.EndsWith(';') ? text : text + ";";
        }

        /// <summary>The name of what a method returns, as it's written: an object's is its struct's.</summary>
        static string TypeName(PhiType t) => t.Kind switch
        {
            TypeKind.Pointer => $"ptr<{TypeName(t.Pointee!)}>",
            _ => t.ToString(),
        };

        static string Text(SourceFile file, Span span) => file.Text.Substring(span.Start, span.Length);
    }
}

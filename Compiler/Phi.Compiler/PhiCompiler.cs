using Assembly = System.Reflection.Assembly;
using Phi.Compiler.CodeGen;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler
{
    public sealed class CompileResult
    {
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
        public bool Success => !Diagnostics.Any(d => d.Severity == Severity.Error);

        /// <summary>
        /// The program's code: the boot sector and OS classes of a 16-bit program, or the
        /// kernel of a 32-bit one.
        /// </summary>
        public List<AsmUnit> Units { get; } = new();

        /// <summary>For a 32-bit kernel: the standard boot sector (stage1) and loader (stage2).</summary>
        public List<AsmUnit> BootStages { get; } = new();

        public ProgramNode? Syntax { get; init; }
        public BoundProgram? Bound { get; init; }
    }

    /// <summary>source -> Lexer -> Parser -> Binder -> X86Generator -> NASM source</summary>
    public static class PhiCompiler
    {
        /// <summary>Where `use` looks for files that aren't next to the program.</summary>
        const string StandardLibrary = "lib/phi/";

        /// <param name="kernel">For a program in a kernel's rootfs: the kernel's file, whose folder `use` searches too.</param>
        public static CompileResult Compile(SourceFile file, bool generate = true, string? kernel = null)
        {
            var diagnostics = new DiagnosticBag();

            ProgramNode syntax = ParseWithUses(file, kernel, diagnostics);
            if (diagnostics.HasErrors) return new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax };

            BoundProgram? bound = Binder.Bind(syntax, diagnostics);
            if (bound == null || diagnostics.HasErrors || !generate)
                return new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound };

            var units = bound.Units.Select(u => X86Generator.Generate(bound, u, diagnostics)).ToList();

            var result = new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound };
            if (!result.Success) return result;

            result.Units.AddRange(units);
            if (bound.Kernel != null)
            {
                result.BootStages.Add(new AsmUnit { Kind = UnitKind.Boot, Name = "stage1", Text = Library.ReadFile("lib/boot/stage1.asm") });
                result.BootStages.Add(new AsmUnit { Kind = UnitKind.Boot, Name = "stage2", Text = Library.ReadFile("lib/boot/stage2.asm") });
            }
            return result;
        }

        public static CompileResult Compile(string path) => Compile(SourceFile.Load(path));

        /// <summary>
        /// Parses the program and every file it uses (and those files' uses). Used files come
        /// first, so their classes run before the program's own.
        /// </summary>
        static ProgramNode ParseWithUses(SourceFile main, string? kernelFile, DiagnosticBag diagnostics)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(main) };

            void Load(SourceFile file, ProgramNode into)
            {
                ProgramNode node = Parser.Parse(file, diagnostics);

                foreach (UseDecl use in node.Uses)
                {
                    SourceFile? used = Resolve(use.Path, file, main, kernelFile);
                    if (used == null)
                    {
                        string relative = use.Path.Replace('.', '/') + ".phi";
                        var near = new List<string> { Path.GetFileName(file.Path) };
                        if (file != main) near.Add(Path.GetFileName(main.Path));
                        if (kernelFile != null) near.Add($"the kernel {Path.GetFileName(kernelFile)}");
                        string places = string.Join(", ", near.Select(n => "next to " + n)) + (near.Count > 1 ? "," : "");
                        diagnostics.Error(file, use.Span, $"can't find {relative} {places} or in the standard library ({StandardLibrary})");
                        continue;
                    }
                    if (seen.Add(Key(used))) Load(used, into);
                }

                into.Classes.AddRange(node.Classes);
                into.RawBlocks.AddRange(node.RawBlocks);
                into.Structs.AddRange(node.Structs);
                into.Enums.AddRange(node.Enums);
                into.Uses.AddRange(node.Uses);
            }

            var program = new ProgramNode { File = main };
            Load(main, program);

            // a 32-bit kernel or a user program that uses new or free (or objects) gets a heap without
            // having to ask; its classes go first, so it is ready before the program's own code runs
            bool kernel = program.Classes.Any(c => c.Base == Binder.Kernel32Base);
            bool user = program.Classes.Any(c => c.Base == Binder.ProgramBase);
            // (a str without a size grows on the heap too)
            bool usesHeap = SyntaxWalker.Nodes(program).Any(n => n is NewExpr or FreeStmt or TakesStmt or VarDecl { IsNew: true, AtAddress: null }
                                                                 || n is VarDecl { BufferSize: null } d && (d.Type.Name == "str" || d.Type.Name == "var" && d.Values.Any(v => v is StringExpr))
                                                                 || n is VarDecl { Unsized: true })   // an array that grows
                            || program.Structs.Any(s => s.Fields.Any(f => f.IsNew));
            string? heapFile = !usesHeap ? null : user ? "memory.program_heap" : kernel ? "memory.heap" : null;
            SourceFile? heap = heapFile != null ? Resolve(heapFile, main, main, null) : null;
            if (heap == null || !seen.Add(Key(heap))) return program;

            var withHeap = new ProgramNode { File = main };
            Load(heap, withHeap);
            withHeap.Classes.AddRange(program.Classes);
            withHeap.RawBlocks.AddRange(program.RawBlocks);
            withHeap.Structs.AddRange(program.Structs);
            withHeap.Enums.AddRange(program.Enums);
            withHeap.Uses.AddRange(program.Uses);
            return withHeap;
        }

        static string Key(SourceFile f)
        {
            if (f.Path.StartsWith(StandardLibrary)) return f.Path;
            string full = Path.GetFullPath(f.Path);

            // a standard library file opened from the repository's lib/phi/ is the same file as
            // its embedded copy, so library files that use each other aren't loaded twice
            string slashed = full.Replace('\\', '/');
            int at = slashed.LastIndexOf("/" + StandardLibrary, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                string name = StandardLibrary + slashed[(at + 1 + StandardLibrary.Length)..];
                using Stream? embedded = typeof(PhiCompiler).Assembly.GetManifestResourceStream(name) ?? FindResource(name);
                if (embedded != null) return name;
            }
            return full;
        }

        /// <summary>
        /// use a.b; is the file a/b.phi: next to the file that uses it, else next to the main
        /// program, else (for a program in a kernel's rootfs) next to the kernel, else in the
        /// standard library.
        /// </summary>
        static SourceFile? Resolve(string dotted, SourceFile from, SourceFile main, string? kernel)
        {
            string relative = dotted.Replace('.', '/') + ".phi";

            var places = new List<string> { from.Path, main.Path };
            if (kernel != null) places.Add(kernel);
            foreach (string near in places)
            {
                if (near.StartsWith(StandardLibrary)) continue;
                string local = Path.Combine(Path.GetDirectoryName(near) ?? "", relative);
                if (File.Exists(local)) return SourceFile.Load(local);
            }

            string resource = StandardLibrary + relative;
            using Stream? stream = typeof(PhiCompiler).Assembly.GetManifestResourceStream(resource)
                                   ?? FindResource(resource);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            return new SourceFile(resource, reader.ReadToEnd());
        }

        static Stream? FindResource(string name)
        {
            // resource names may use either slash, depending on how they were embedded
            Assembly assembly = typeof(PhiCompiler).Assembly;
            string? match = assembly.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == name);
            return match == null ? null : assembly.GetManifestResourceStream(match);
        }
    }
}

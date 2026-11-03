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

        /// <summary>The Error struct and ErrorKind enum, for a program that catches errors.</summary>
        const string ErrorLibrary = "lang.error";

        /// <param name="kernel">For a program in a kernel's rootfs: the kernel's file, whose folder `use` searches too.</param>
        /// <param name="host">Where the program runs: PHI OS, or (for a program) another system.</param>
        public static CompileResult Compile(SourceFile file, bool generate = true, string? kernel = null, Host host = Host.PhiOS)
        {
            var diagnostics = new DiagnosticBag();
            var usedBy = new Dictionary<string, (SourceFile From, UseDecl Use)>(StringComparer.OrdinalIgnoreCase);

            ProgramNode syntax = ParseWithUses(file, kernel, diagnostics, usedBy, host);
            if (diagnostics.HasErrors) return new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax };

            BoundProgram? bound = Binder.Bind(syntax, diagnostics, host);
            if (diagnostics.HasErrors)
                return new CompileResult { Diagnostics = AtUses(diagnostics.Items, usedBy, syntax, host), Syntax = syntax, Bound = bound };
            if (bound == null || !generate)
            {
                // what 64-bit code can't do yet is found by the generator: a check needs it too
                if (bound != null && host.Is64())
                    foreach (BoundUnit u in bound.Units) X86Generator.Check(bound, u, diagnostics);
                return new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound };
            }

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
        /// The standard library is tested, so an error inside it means a module was used where it
        /// can't work (a driver in a program, a kernel's module on Linux). Its errors become one
        /// at the program's `use` that brought the module in, with the first of them as the reason.
        /// </summary>
        static IReadOnlyList<Diagnostic> AtUses(IReadOnlyList<Diagnostic> diagnostics, Dictionary<string, (SourceFile From, UseDecl Use)> usedBy,
                                                ProgramNode syntax, Host host)
        {
            string where = syntax.Classes.Any(c => c.Base == Binder.ProgramBase) ? host == Host.PhiOS ? "a PHI OS program" : $"a {host.Name()} program"
                         : syntax.Classes.Any(c => c.Base == Binder.Kernel32Base) ? "a 32-bit kernel" : "a 16-bit program";
            var result = new List<Diagnostic>();
            var reported = new HashSet<UseDecl>();

            foreach (Diagnostic d in diagnostics)
            {
                if (d.Severity != Severity.Error || d.File == null || !d.File.Path.StartsWith(StandardLibrary)
                    || !usedBy.TryGetValue(Key(d.File), out var link))
                {
                    result.Add(d);
                    continue;
                }

                // up the uses to the one in the program's own files
                string module = d.File.Path[StandardLibrary.Length..^".phi".Length].Replace('/', '.');
                while (link.From.Path.StartsWith(StandardLibrary) && usedBy.TryGetValue(Key(link.From), out var above))
                    link = above;
                if (link.From.Path.StartsWith(StandardLibrary))
                {
                    result.Add(d);
                    continue;
                }
                if (!reported.Add(link.Use)) continue;

                var (line, _) = d.File.GetLocation(d.Span.Start);
                string what = link.Use.Path == module ? $"{module} doesn't work" : $"{link.Use.Path} needs {module}, which doesn't work";
                result.Add(d with
                {
                    File = link.From,
                    Span = link.Use.Span,
                    Message = $"{what} in {where} ({d.File.Path}:{line}: {d.Message})",
                });
            }
            return result;
        }

        /// <summary>
        /// Parses the program and every file it uses (and those files' uses). Used files come
        /// first, so their classes run before the program's own.
        /// </summary>
        /// <param name="usedBy">gets, for each file loaded by a `use`, that use and the file it's in</param>
        static ProgramNode ParseWithUses(SourceFile main, string? kernelFile, DiagnosticBag diagnostics,
                                         Dictionary<string, (SourceFile From, UseDecl Use)> usedBy, Host host = Host.PhiOS)
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
                    if (seen.Add(Key(used)))
                    {
                        usedBy[Key(used)] = (file, use);
                        Load(used, into);
                    }
                }

                into.Classes.AddRange(node.Classes);
                into.RawBlocks.AddRange(node.RawBlocks);
                into.SysCalls.AddRange(node.SysCalls);
                into.Libraries.AddRange(node.Libraries);
                into.OutsideFunctions.AddRange(node.OutsideFunctions);
                into.Structs.AddRange(node.Structs);
                into.Enums.AddRange(node.Enums);
                into.Uses.AddRange(node.Uses);
            }

            var program = new ProgramNode { File = main };
            Load(main, program);

            // a program that catches errors gets the Error struct they're caught in
            if (SyntaxWalker.Nodes(program).Any(n => n is ThrowsExpr) && Resolve(ErrorLibrary, main, main, null) is SourceFile errors
                && seen.Add(Key(errors)))
                Load(errors, program);

            // a 32-bit kernel or a user program that uses new or free (or objects) gets a heap without
            // having to ask; its classes go first, so it is ready before the program's own code runs
            bool kernel = program.Classes.Any(c => c.Base == Binder.Kernel32Base);
            bool user = program.Classes.Any(c => c.Base == Binder.ProgramBase);
            // (a str without a size grows on the heap too)
            bool usesHeap = SyntaxWalker.Nodes(program).Any(n => n is NewExpr or FreeStmt or TakesStmt or VarDecl { IsNew: true, AtAddress: null }
                                                                 || n is VarDecl { BufferSize: null } d && (d.Type.Name == "str" || d.Type.Name == "var" && d.Values.Any(v => v is StringExpr))
                                                                 // (an array of strs whose texts grow: str names[atmost 8];)
                                                                 || n is VarDecl { BufferSize: not null, SizeAfterColon: false, TextSize: null, Type.Name: "str" }
                                                                 || n is VarDecl { Unsized: true })   // an array that grows
                            || program.Structs.Any(s => s.Fields.Any(f => f.IsNew));
            string? heapFile = !usesHeap ? null : user ? "memory.program_heap" : kernel ? "memory.heap" : null;
            SourceFile? heap = heapFile != null ? Resolve(heapFile, main, main, null) : null;
            if (heap != null && seen.Add(Key(heap)))
            {
                var withHeap = new ProgramNode { File = main };
                Load(heap, withHeap);
                Append(program, withHeap);
                program = withHeap;
            }

            // a program on another system gets the parts of its system's own modules that it
            // uses, which do what the runtime's assembly did (OS.Sleep and the rest: see
            // Builtins' OnHost); first of all, so they're ready before anything else runs.
            // Each system keeps its own.
            if (user && host != Host.PhiOS)
            {
                var used = SyntaxWalker.Nodes(program)
                    .Select(n => n switch { CallStmt c => c.Callee, CallExpr c => c.Callee, NameExpr v => v.Name, _ => null })
                    .Where(name => name != null && name.StartsWith("OS."))
                    .Select(name => Builtins.Find(name!)?.OnHost?.Split('.')[0])
                    .OfType<string>().ToHashSet();
                if (used.Count > 0)
                {
                    var withHost = new ProgramNode { File = main };
                    foreach (string module in HostParts.Where(p => used.Contains(p.Class)).SelectMany(p => p.Modules).Prepend("host.linux64").Prepend("host.linux32"))
                        if (Resolve(module, main, main, null) is SourceFile own && seen.Add(Key(own)))
                            Load(own, withHost);
                    Append(program, withHost);
                    program = withHost;
                }
            }
            return program;
        }

        /// <summary>
        /// The systems' own modules (lib/phi/host/): each part, by the class that answers its
        /// built-ins, and its modules. (linux32 and linux64, Linux's numbers, go with any.)
        /// </summary>
        static readonly (string Class, string[] Modules)[] HostParts =
        {
            ("HostTime", new[] { "host.time_linux32", "host.time_linux64", "host.time_windows" }),
            ("HostFiles", new[] { "host.files_linux", "host.files_windows" }),
            ("HostNet", new[] { "host.net", "host.net_linux", "host.net_windows" }),
            ("HostThreads", new[] { "host.threads", "host.threads_linux", "host.threads_windows" }),
            ("HostWindow", new[] { "host.files_linux", "host.files_windows", "host.window_windows", "host.window_linux" }),
        };

        /// <summary>Adds everything in from after what into has.</summary>
        static void Append(ProgramNode from, ProgramNode into)
        {
            into.Classes.AddRange(from.Classes);
            into.RawBlocks.AddRange(from.RawBlocks);
            into.SysCalls.AddRange(from.SysCalls);
            into.Libraries.AddRange(from.Libraries);
            into.OutsideFunctions.AddRange(from.OutsideFunctions);
            into.Structs.AddRange(from.Structs);
            into.Enums.AddRange(from.Enums);
            into.Uses.AddRange(from.Uses);
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
        /// standard library. A program in a rootfs folder built on its own (for Linux, say)
        /// looks in the folder around it, where its kernel is, as it does built for the kernel.
        /// </summary>
        static SourceFile? Resolve(string dotted, SourceFile from, SourceFile main, string? kernel)
        {
            string relative = dotted.Replace('.', '/') + ".phi";

            var places = new List<string> { from.Path, main.Path };
            if (kernel != null) places.Add(kernel);
            else if (Path.GetDirectoryName(Path.GetFullPath(main.Path)) is string folder
                     && (Path.GetFileName(folder) == "rootfs" || folder.EndsWith(".rootfs")))
                places.Add(Path.Combine(Path.GetDirectoryName(folder) ?? "", Path.GetFileName(main.Path)));
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

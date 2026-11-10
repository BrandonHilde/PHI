using Assembly = System.Reflection.Assembly;
using Phi.Compiler.CodeGen;
using Phi.Compiler.Libraries;
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

        /// <summary>Each `use` in the program and its files: the file it's in, and the file it loaded.</summary>
        public IReadOnlyDictionary<UseDecl, (SourceFile From, SourceFile Target)> Uses { get; init; } = new Dictionary<UseDecl, (SourceFile, SourceFile)>();

        /// <summary>Every file the program is made of, by <see cref="PhiCompiler.FileKey"/>.</summary>
        public IReadOnlySet<string> Files { get; init; } = new HashSet<string>();
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
        /// <param name="debug">For a debugger: see X86Generator's debugInfo.</param>
        /// <param name="open">Text to read in place of files on the disk, by <see cref="FileKey"/>: an editor's unsaved files.</param>
        /// <param name="bindAnyway">
        /// With errors in the parsing, still work out what the names that did parse are: an
        /// editor's going to a definition and completing a name go on while the code is half written.
        /// </param>
        /// <param name="wholeHost">Give a program every part of its system's runtime, used or not, to check them.</param>
        public static CompileResult Compile(SourceFile file, bool generate = true, string? kernel = null, Host host = Host.PhiOS, bool debug = false,
                                            IReadOnlyDictionary<string, string>? open = null, bool bindAnyway = false, bool wholeHost = false)
        {
            var diagnostics = new DiagnosticBag();
            var usedBy = new Dictionary<string, (SourceFile From, UseDecl Use)>(StringComparer.OrdinalIgnoreCase);
            var uses = new Dictionary<UseDecl, (SourceFile From, SourceFile Target)>();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ProgramNode syntax = ParseWithUses(file, kernel, diagnostics, usedBy, uses, files, open, host, wholeHost);
            if (diagnostics.HasErrors)
            {
                BoundProgram? partial = null;
                if (bindAnyway)
                {
                    // half-written code can be what no finished program is, so a failure here only
                    // means fewer names are known (its errors would follow from the parsing's anyway)
                    try { partial = Binder.Bind(syntax, new DiagnosticBag(), host); }
                    catch (Exception e) when (e is not OutOfMemoryException) { }
                    return new CompileResult { Diagnostics = AtUses(diagnostics.Items, usedBy, syntax, host, open), Syntax = syntax, Bound = partial, Uses = uses, Files = files };
                }

                // a build shows the other mistakes too, so one build finds them all: what the
                // rest of the program gets wrong, where that can't be the syntax errors' doing
                var all = diagnostics.Items.ToList();
                bool useFailed = syntax.Uses.Any(u => !uses.ContainsKey(u)) || all.Any(d => d.Message.StartsWith("can't find "));
                if (!useFailed)
                {
                    var later = new DiagnosticBag();
                    try
                    {
                        Binder.Bind(syntax, later, host);
                        all.AddRange(NotFromSyntaxErrors(all, later.Items));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { }
                }
                return new CompileResult { Diagnostics = AtUses(all, usedBy, syntax, host, open), Syntax = syntax, Uses = uses, Files = files };
            }

            BoundProgram? bound = Binder.Bind(syntax, diagnostics, host);
            if (diagnostics.HasErrors)
                return new CompileResult { Diagnostics = AtUses(diagnostics.Items, usedBy, syntax, host, open), Syntax = syntax, Bound = bound, Uses = uses, Files = files };
            if (bound == null || !generate)
            {
                // what 64-bit code can't do yet is found by the generator: a check needs it too
                if (bound != null && host.Is64())
                    foreach (BoundUnit u in bound.Units) X86Generator.Check(bound, u, diagnostics);
                return new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound, Uses = uses, Files = files };
            }

            var units = bound.Units.Select(u => X86Generator.Generate(bound, u, diagnostics, debug)).ToList();

            var result = new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound, Uses = uses, Files = files };
            if (!result.Success) return result;

            result.Units.AddRange(units);
            if (bound.Kernel != null)
            {
                result.BootStages.Add(new AsmUnit { Kind = UnitKind.Boot, Name = "stage1", Text = Library.ReadFile("lib/boot/stage1.asm") });
                result.BootStages.Add(new AsmUnit { Kind = UnitKind.Boot, Name = "stage2", Text = Library.ReadFile("lib/boot/stage2.asm") });
            }
            return result;
        }

        static readonly System.Text.RegularExpressions.Regex QuotedName = new(@"'([A-Za-z_][A-Za-z_0-9]*)(?:[.:][^']*)?'");
        static readonly System.Text.RegularExpressions.Regex Word = new(@"[A-Za-z_][A-Za-z_0-9]*");

        /// <summary>
        /// Of the errors found in what did parse, those worth showing beside the syntax errors: an
        /// error that a line the parser gave up on could have caused is left out, since fixing
        /// that line would make it go away. That's one on such a line; one that names something
        /// such a line names (the line may have declared it); and an "unknown" one whose name is
        /// written anywhere else in its file (what declares it may be what was lost). A use that
        /// didn't parse leaves them all out, as a whole file's names are missing then.
        /// </summary>
        static List<Diagnostic> NotFromSyntaxErrors(IReadOnlyList<Diagnostic> syntaxErrors, IReadOnlyList<Diagnostic> later)
        {
            var kept = new List<Diagnostic>();
            var lostLines = new HashSet<(SourceFile, int)>();
            var lostNames = new HashSet<string>();
            foreach (Diagnostic d in syntaxErrors)
            {
                if (d.File == null) return kept;
                int line = d.File.GetLocation(d.Span.Start).Line;
                // (what was lost may run on from the line before, or onto the next)
                if (!PlainStatement(d.File, line)) return kept;
                for (int near = line - 1; near <= line + 1; near++)
                {
                    lostLines.Add((d.File, near));
                    string text = LineText(d.File, near);
                    foreach (string word in NamesOn(text)) lostNames.Add(word);
                }
            }

            // an error that follows from what was lost can itself make others: the method it's in
            // may give back another type now, and what its line declares may be another type. So
            // what such an error's line names, and its method, are lost too, until nothing more is.
            var errors = later.Where(d => d.Severity == Severity.Error && d.File != null).ToList();
            var follows = new HashSet<Diagnostic>();
            bool FollowsFromLost(Diagnostic d)
            {
                var (line, _) = d.File!.GetLocation(d.Span.Start);
                if (lostLines.Contains((d.File, line))) return true;
                var names = QuotedName.Matches(d.Message).Select(m => m.Groups[1].Value).ToList();
                if (names.Any(lostNames.Contains)) return true;
                // its own line names something lost, or (for an error about a whole method, at its
                // heading) anything in the method does
                if (NamesOn(LineText(d.File, line)).Any(lostNames.Contains)) return true;
                if (LineText(d.File, line).TrimStart().StartsWith('['))
                    for (int below = line + 1; LineText(d.File, below) is { Length: > 0 } text && !text.TrimStart().StartsWith("}"); below++)
                    {
                        if (NamesOn(text).Any(lostNames.Contains)) return true;
                        if (text.TrimStart().StartsWith("[end")) break;
                    }
                if (d.Message.Contains("unknown ") && names.Any(n => WrittenElsewhere(d.File, n, d.Span))) return true;
                // (a type that couldn't be worked out is something lost, too)
                return d.Message.Contains(" ?") || d.Message.Contains("a nothing");
            }
            for (bool more = true; more;)
            {
                more = false;
                foreach (Diagnostic d in errors)
                {
                    if (follows.Contains(d) || !FollowsFromLost(d)) continue;
                    follows.Add(d);
                    more = true;
                    int line = d.File!.GetLocation(d.Span.Start).Line;
                    foreach (string word in NamesOn(LineText(d.File, line))) lostNames.Add(word);
                    foreach (System.Text.RegularExpressions.Match name in QuotedName.Matches(d.Message)) lostNames.Add(name.Groups[1].Value);
                    if (MethodAround(d.File, line) is string method) lostNames.Add(method);
                }
            }

            foreach (Diagnostic d in errors)
            {
                if (follows.Contains(d)) continue;
                if (kept.Any(k => k.File == d.File && k.Span == d.Span && k.Message == d.Message)) continue;
                kept.Add(d);
            }
            return kept;
        }

        static readonly string[] Structure =
        {
            "[", "{", "}", ";;", "phi.", "struct.", "enum.", "interface.", "asm.", "x64.", "sys.", "use ", "const ",
            "if ", "else", "while ", "when ", "is ", "unsafe", "locked ", "new ",
        };

        /// <summary>
        /// True when a line is one statement inside a class or a method: not a heading, a block's
        /// start or end, or a declaration other code is laid out by (a use, a constant, anything
        /// in a struct, an enum or an interface). Losing such a line loses nothing else.
        /// </summary>
        static bool PlainStatement(SourceFile file, int line)
        {
            string text = LineText(file, line).Trim();
            if (text.Length == 0 || Structure.Any(text.StartsWith)) return false;
            // what it's in: the nearest heading above it at the start of a line
            for (int above = line - 1; above >= 1; above--)
            {
                string heading = LineText(file, above);
                if (heading.StartsWith("phi.")) return true;
                if (heading.StartsWith("struct.") || heading.StartsWith("enum.") || heading.StartsWith("interface.")
                    || heading.StartsWith("asm.") || heading.StartsWith("x64.")) return false;
            }
            return false;
        }

        /// <summary>The names written on a line: its words, but for the language's own and what's in quotes or a comment.</summary>
        static IEnumerable<string> NamesOn(string line)
        {
            string code = System.Text.RegularExpressions.Regex.Replace(line, @"'(\\.|[^'\\])*'|#.*$", " ");
            foreach (System.Text.RegularExpressions.Match word in Word.Matches(code))
                if (!Parser.ReservedWords.Contains(word.Value) && !Parser.TypeKeywords.Contains(word.Value))
                    yield return word.Value;
        }

        /// <summary>The name of the method a line is in ([Name: ...] above it, before any [end]), or null in a class's own code.</summary>
        static string? MethodAround(SourceFile file, int line)
        {
            for (int above = line; above >= 1; above--)
            {
                string text = LineText(file, above).Trim();
                if (above < line && text.StartsWith("[end")) return null;
                if (text.StartsWith("phi.") || text.StartsWith("struct.")) return null;
                if (text.StartsWith('[') && !text.StartsWith("[end") && Word.Match(text) is { Success: true } name) return name.Value;
            }
            return null;
        }

        static string LineText(SourceFile file, int line)
        {
            if (line < 1) return "";
            int start = file.GetOffset(line - 1, 0);
            if (start >= file.Text.Length) return "";
            int end = file.Text.IndexOf('\n', start);
            return file.Text[start..(end < 0 ? file.Text.Length : end)];
        }

        static bool WrittenElsewhere(SourceFile file, string name, Span at)
        {
            foreach (System.Text.RegularExpressions.Match word in Word.Matches(file.Text))
                if (word.Value == name && (word.Index < at.Start || word.Index >= at.End)) return true;
            return false;
        }

        public static CompileResult Compile(string path) => Compile(SourceFile.Load(path));

        /// <summary>
        /// A library for one system, up to NASM's part: its unit's assembly and lists, with the
        /// interface and the modules it uses. Its own files are the one given and those it uses
        /// from beside it; the standard library's modules and other compiled libraries stay
        /// outside its code.
        /// </summary>
        public static CompileResult CompileLibrary(SourceFile file, LibraryPlatform platform, string name)
        {
            var diagnostics = new DiagnosticBag();
            var usedBy = new Dictionary<string, (SourceFile From, UseDecl Use)>(StringComparer.OrdinalIgnoreCase);
            var uses = new Dictionary<UseDecl, (SourceFile From, SourceFile Target)>();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var loaded = new List<SourceFile>();

            ProgramNode syntax = ParseWithUses(file, null, diagnostics, usedBy, uses, files, null, platform.Host, false, platform, loaded);
            var own = loaded.Where(f => f.Compiled == null && !f.Path.StartsWith(StandardLibrary)).ToList();
            if (diagnostics.HasErrors)
                return new CompileResult { Diagnostics = AtUses(diagnostics.Items, usedBy, syntax, platform.Host, null), Syntax = syntax, Uses = uses, Files = files };

            var build = new LibraryBuild(platform.Kernel ? UnitKind.Kernel : UnitKind.Program, name, own.ToHashSet());
            BoundProgram? bound = Binder.Bind(syntax, diagnostics, platform.Host, build);
            if (diagnostics.HasErrors || bound == null)
                return new CompileResult { Diagnostics = AtUses(diagnostics.Items, usedBy, syntax, platform.Host, null), Syntax = syntax, Bound = bound, Uses = uses, Files = files };

            BoundUnit unit = bound.Units.Single();
            AsmUnit asm = X86Generator.Generate(bound, unit, diagnostics);
            var result = new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound, Uses = uses, Files = files };
            if (!result.Success || asm.Library == null) return result;

            result.Units.Add(asm);
            CompiledTarget target = asm.Library.Target;
            target.Interface.AddRange(InterfaceWriter.Write(syntax, own, uses));
            target.Modules.AddRange(loaded.Where(f => f.Path.StartsWith(StandardLibrary)).Select(f => f.Path[StandardLibrary.Length..^".phi".Length].Replace('/', '.')));
            return result;
        }

        /// <summary>
        /// The standard library is tested, so an error inside it means a module was used where it
        /// can't work (a driver in a program, a kernel's module on Linux). Its errors become one
        /// at the program's `use` that brought the module in, with the first of them as the reason.
        /// (A library file that's open in an editor is being written, so its errors stay its own.)
        /// </summary>
        static IReadOnlyList<Diagnostic> AtUses(IReadOnlyList<Diagnostic> diagnostics, Dictionary<string, (SourceFile From, UseDecl Use)> usedBy,
                                                ProgramNode syntax, Host host, IReadOnlyDictionary<string, string>? open)
        {
            string where = syntax.Classes.Any(c => c.Base == Binder.ProgramBase) ? host == Host.PhiOS ? "a PHI OS program" : $"a {host.Name()} program"
                         : syntax.Classes.Any(c => c.Base == Binder.Kernel32Base) ? "a 32-bit kernel" : "a 16-bit program";
            var result = new List<Diagnostic>();
            var reported = new HashSet<UseDecl>();

            foreach (Diagnostic d in diagnostics)
            {
                if (d.Severity != Severity.Error || d.File == null || !d.File.Path.StartsWith(StandardLibrary)
                    || open?.ContainsKey(Key(d.File)) == true || !usedBy.TryGetValue(Key(d.File), out var link))
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
        /// <param name="uses">gets each `use`, the file it's in and the file it loaded</param>
        /// <param name="seen">gets every file of the program</param>
        static ProgramNode ParseWithUses(SourceFile main, string? kernelFile, DiagnosticBag diagnostics,
                                         Dictionary<string, (SourceFile From, UseDecl Use)> usedBy,
                                         Dictionary<UseDecl, (SourceFile From, SourceFile Target)> uses, HashSet<string> seen,
                                         IReadOnlyDictionary<string, string>? open, Host host = Host.PhiOS, bool wholeHost = false,
                                         LibraryPlatform? buildingFor = null, List<SourceFile>? loaded = null)
        {
            seen.Add(Key(main));

            // which of a compiled library's versions the program takes, once its kind is known
            LibraryPlatform? platform = buildingFor;

            void Load(SourceFile file, ProgramNode into)
            {
                ProgramNode node = Parser.Parse(file, diagnostics);
                platform ??= LibraryPlatform.Of(host, kernelFile == null && node.Classes.Any(c => c.Base == Binder.Kernel32Base));

                foreach (UseDecl use in node.Uses)
                {
                    SourceFile? used;
                    try
                    {
                        used = Resolve(use.Path, file, main, kernelFile, open, platform);
                    }
                    catch (LibraryFileException e)
                    {
                        diagnostics.Error(file, use.Span, e.Message);
                        continue;
                    }
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
                    uses[use] = (file, used);
                    if (seen.Add(Key(used)))
                    {
                        usedBy[Key(used)] = (file, use);
                        if (used.Compiled != null) LoadCompiled(used, into);
                        else Load(used, into);
                    }
                }

                loaded?.Add(file);    // (after those it uses, as its classes are)
                into.Classes.AddRange(node.Classes);
                into.RawBlocks.AddRange(node.RawBlocks);
                into.SysCalls.AddRange(node.SysCalls);
                into.Libraries.AddRange(node.Libraries);
                into.OutsideFunctions.AddRange(node.OutsideFunctions);
                into.Structs.AddRange(node.Structs);
                into.Enums.AddRange(node.Enums);
                into.Uses.AddRange(node.Uses);
            }

            // a compiled library: the modules its code uses, then each of its own files' interface
            void LoadCompiled(SourceFile library, ProgramNode into)
            {
                CompiledTarget compiled = library.Compiled!;
                into.CompiledLibraries.Add(compiled);
                foreach (string module in compiled.Modules)
                    if (Resolve(module, main, main, null, open) is SourceFile standard && seen.Add(Key(standard)))
                        Load(standard, into);
                foreach (InterfaceFile part in compiled.Interface.SkipLast(1))
                {
                    var file = new SourceFile(Path.Combine(compiled.Path, part.Name), part.Text) { Compiled = compiled };
                    if (seen.Add(Key(file))) Load(file, into);
                }
                Load(library, into);
            }

            var program = new ProgramNode { File = main };
            Load(main, program);

            // a program that catches errors gets the Error struct they're caught in
            if (SyntaxWalker.Nodes(program).Any(n => n is ThrowsExpr) && Resolve(ErrorLibrary, main, main, null, open) is SourceFile errors
                && seen.Add(Key(errors)))
                Load(errors, program);

            // a 32-bit kernel or a user program that uses new or free (or objects) gets a heap without
            // having to ask; its classes go first, so it is ready before the program's own code runs
            bool kernel = buildingFor != null ? buildingFor.Kernel : program.Classes.Any(c => c.Base == Binder.Kernel32Base);
            bool user = buildingFor != null ? !buildingFor.Kernel : program.Classes.Any(c => c.Base == Binder.ProgramBase);
            // (a str without a size grows on the heap too)
            bool usesHeap = SyntaxWalker.Nodes(program).Any(n => n is NewExpr or FreeStmt or TakesStmt or VarDecl { IsNew: true, AtAddress: null }
                                                                 || n is VarDecl { BufferSize: null } d && (d.Type.Name == "str" || d.Type.Name == "var" && d.Values.Any(v => v is StringExpr))
                                                                 // (an array of strs whose texts grow: str names[atmost 8];)
                                                                 || n is VarDecl { BufferSize: not null, SizeAfterColon: false, TextSize: null, Type.Name: "str" }
                                                                 || n is VarDecl { Unsized: true })   // an array that grows
                            || program.Structs.Any(s => s.Fields.Any(f => f.IsNew));
            string? heapFile = !usesHeap ? null : user ? "memory.program_heap" : kernel ? "memory.heap" : null;
            SourceFile? heap = heapFile != null ? Resolve(heapFile, main, main, null, open) : null;
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
                if (wholeHost) used.UnionWith(HostParts.Select(p => p.Class));
                if (used.Count > 0)
                {
                    var withHost = new ProgramNode { File = main };
                    foreach (string module in HostParts.Where(p => used.Contains(p.Class)).SelectMany(p => p.Modules).Prepend("host.linux64").Prepend("host.linux32"))
                        if (Resolve(module, main, main, null, open) is SourceFile own && seen.Add(Key(own)))
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
            ("HostWindow", new[] { "host.files_linux", "host.files_windows", "host.environment_linux", "host.window_windows", "host.window_linux" }),
            ("HostSound", new[] { "host.time_linux32", "host.time_linux64", "host.time_windows", "host.files_linux", "host.files_windows",
                                  "host.environment_linux", "host.sound", "host.sound_windows", "host.sound_linux" }),
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
            into.CompiledLibraries.AddRange(from.CompiledLibraries);
        }

        static string Key(SourceFile f) => FileKey(f.Path);

        /// <summary>What says two paths are one file: the same name for a file on the disk and its embedded copy.</summary>
        public static string FileKey(string path)
        {
            if (path.StartsWith(StandardLibrary)) return path;
            string full = Path.GetFullPath(path);

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
        /// <remarks>At each place a source file comes before a compiled library of the same name.</remarks>
        /// <param name="platform">Which of a compiled library's versions to take; with none, compiled libraries aren't looked for.</param>
        /// <exception cref="LibraryFileException">There's a compiled library, which can't be used.</exception>
        static SourceFile? Resolve(string dotted, SourceFile from, SourceFile main, string? kernel, IReadOnlyDictionary<string, string>? open, LibraryPlatform? platform = null)
        {
            string relative = dotted.Replace('.', '/') + ".phi";

            var places = new List<string> { from.Path, main.Path };
            // (a file of a compiled library is seen as inside it: libraries it uses are next to it)
            if (from.Compiled != null) places.Insert(0, from.Compiled.Path);
            if (kernel != null) places.Add(kernel);
            else if (FolderOfRootfs(main.Path) is string above)
                places.Add(Path.Combine(above, Path.GetFileName(main.Path)));
            foreach (string near in places)
            {
                if (near.StartsWith(StandardLibrary)) continue;
                string local = Path.Combine(Path.GetDirectoryName(near) ?? "", relative);
                if (open != null && open.TryGetValue(FileKey(local), out string? unsaved)) return new SourceFile(local, unsaved);
                if (File.Exists(local)) return SourceFile.Load(local);

                string compiled = Path.ChangeExtension(local, CompiledLibrary.Extension);
                if (platform != null && File.Exists(compiled)) return LoadLibraryInterface(compiled, platform);
            }

            string resource = StandardLibrary + relative;
            if (open != null && open.TryGetValue(resource, out string? unsavedLibrary)) return new SourceFile(resource, unsavedLibrary);
            using Stream? stream = typeof(PhiCompiler).Assembly.GetManifestResourceStream(resource)
                                   ?? FindResource(resource);
            if (stream == null) return null;

            using var reader = new StreamReader(stream);
            return new SourceFile(resource, reader.ReadToEnd());
        }

        /// <summary>A compiled library's main file, as declarations; its version for this system is the one that's kept.</summary>
        static SourceFile LoadLibraryInterface(string path, LibraryPlatform platform)
        {
            CompiledLibrary library = CompiledLibrary.Load(path);
            if (!library.Targets.TryGetValue(platform.Key, out CompiledTarget? target))
            {
                var has = LibraryPlatform.All.Where(p => library.Targets.ContainsKey(p.Key)).Select(p => p.Description);
                throw new LibraryFileException($"{Path.GetFileName(path)} wasn't built for {platform.Description}, only {string.Join(", ", has)}");
            }
            if (target.Interface.Count == 0) throw new LibraryFileException($"{Path.GetFileName(path)} is damaged: it has no interface");
            return new SourceFile(path, target.Interface[^1].Text) { Compiled = target };
        }

        /// <summary>For a file anywhere inside a rootfs or NAME.rootfs folder: the folder that one is in, where its kernel is. Null for any other.</summary>
        public static string? FolderOfRootfs(string path)
        {
            for (string? folder = Path.GetDirectoryName(Path.GetFullPath(path)); folder != null; folder = Path.GetDirectoryName(folder))
                if (Path.GetFileName(folder) == "rootfs" || folder.EndsWith(".rootfs"))
                    return Path.GetDirectoryName(folder);
            return null;
        }

        /// <summary>Whether a `use` of this name finds a module of the standard library.</summary>
        public static bool InStandardLibrary(string dotted)
        {
            string resource = StandardLibrary + dotted.Replace('.', '/') + ".phi";
            using Stream? stream = typeof(PhiCompiler).Assembly.GetManifestResourceStream(resource) ?? FindResource(resource);
            return stream != null;
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

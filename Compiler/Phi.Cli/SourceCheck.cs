using System.Text.RegularExpressions;
using Phi.Compiler;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Cli
{
    /// <summary>
    /// Checks one file as what it is, without building it: what phi check does, and an editor as
    /// the file is written. A program is checked by itself. A module isn't a program, and some
    /// only make sense with what's around them (lib/phi/host's work as a family; net.tlslink
    /// needs the Tcp its program has), so it's checked in the first of these that has no errors
    /// in it: a smallest program around it, its system's runtime, or a program here that uses it.
    /// Only the errors in the file itself are reported. An instance keeps what worked, so a
    /// mistake in a module that's being written is one check and not a search.
    /// </summary>
    public sealed class SourceChecker
    {
        /// <summary>How a file is checked: what program it's in, on which system.</summary>
        /// <param name="Root">The program it's a part of, when that isn't the file with Added after it.</param>
        /// <param name="Added">More source after the program's own: a wrapper, the class Machine.</param>
        /// <param name="WholeHost">The program has every part of the system's runtime.</param>
        sealed record Plan(Host Host, string Added = "", string? Root = null, bool WholeHost = false);

        /// <summary>The longest list of programs tried for a module, as each is a whole compile.</summary>
        const int MostUsers = 8;

        /// <summary>Where the modules are that answer programs' calls on other systems.</summary>
        const string RuntimeFolder = "lib/phi/host/";

        const string KernelWrapper = "\nphi.PhiCheck:Kernel\n{\n}\n";
        const string ProgramWrapper = "\nphi.PhiCheck:Program\n{\n}\n";
        const string BootWrapper = "\nphi.PhiCheck:Bootloader\n{\n}\n";

        readonly Host? target;
        Plan? plan;
        bool planWorked;
        bool planForProgram;

        /// <param name="target">The system asked for; null for what the file itself says, else PHI OS.</param>
        public SourceChecker(Host? target = null) => this.target = target;

        /// <summary>The file was saved: a plan that never worked is worth looking for again.</summary>
        public void Saved()
        {
            if (!planWorked) plan = null;
        }

        /// <param name="open">Text to read in place of files on the disk, by <see cref="PhiCompiler.FileKey"/>.</param>
        /// <param name="bindAnyway">Work out what names are even with parse errors, for an editor.</param>
        public CompileResult Check(string path, string text, IReadOnlyDictionary<string, string>? open = null, bool bindAnyway = false)
        {
            string key = PhiCompiler.FileKey(path);
            var withThis = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = text };
            if (open != null)
                foreach (var (k, v) in open) withThis[k] = v;

            var parsing = new DiagnosticBag();
            ProgramNode own = Parser.Parse(new SourceFile(path, text), parsing);
            Host? ownSystem = SystemOf(own);

            // (what's added would read as part of a class that was never closed, so with parse
            // errors the file goes by itself: only those errors are reported)
            if (parsing.HasErrors)
                return Only(key, PhiCompiler.Compile(new SourceFile(path, text), generate: false, host: target ?? ownSystem ?? plan?.Host ?? Host.PhiOS,
                                                     open: withThis, bindAnyway: bindAnyway));

            CompileResult Run(Plan p)
            {
                CompileResult result = p.Root == null
                    ? PhiCompiler.Compile(new SourceFile(path, text + p.Added), generate: false, host: p.Host, open: withThis, bindAnyway: bindAnyway, wholeHost: p.WholeHost)
                    // (the program around a system's runtime is only what's Added: it's not on the disk)
                    : PhiCompiler.Compile(new SourceFile(p.Root, (p.WholeHost ? "" : TextOf(p.Root, withThis)) + p.Added), generate: false, host: p.Host, open: withThis,
                                          bindAnyway: bindAnyway, wholeHost: p.WholeHost);
                return Only(key, result);
            }

            // (lib/phi/host's are :Program classes, but they're parts of the runtime of every program)
            bool partOfRuntime = key.StartsWith(RuntimeFolder);
            bool isProgram = IsProgram(own) && !partOfRuntime;
            if (plan != null && planForProgram == isProgram)
            {
                CompileResult again = Run(plan);
                planWorked |= IsGood(again, key);
                return again;
            }

            planForProgram = isProgram;
            planWorked = false;
            plan = null;
            CompileResult? best = null;
            foreach (Plan candidate in Plans(path, own, isProgram, partOfRuntime, ownSystem, withThis))
            {
                CompileResult result = Run(candidate);
                if (IsGood(result, key))
                {
                    plan = candidate;
                    planWorked = true;
                    return result;
                }
                // (a program that has another file of this name says nothing about this one)
                if (best == null || result.Files.Contains(key) && Errors(result) < Errors(best))
                {
                    best = result;
                    plan = candidate;
                }
                // what a program's mistakes say tells which systems are worth a try
                if (isProgram)
                    foreach (Plan other in OtherSystems(best, candidate))
                    {
                        CompileResult elsewhere = Run(other);
                        if (Errors(elsewhere) < Errors(best))
                        {
                            best = elsewhere;
                            plan = other;
                        }
                    }
            }
            return best!;
        }

        /// <summary>The ways to check a file, most likely first.</summary>
        IEnumerable<Plan> Plans(string path, ProgramNode own, bool isProgram, bool partOfRuntime, Host? ownSystem, IReadOnlyDictionary<string, string> open)
        {
            string machine = MachineClassFor(path, own);
            Host host = target ?? ownSystem ?? Host.PhiOS;

            if (isProgram)
            {
                yield return new Plan(host, machine);
                yield break;
            }

            // in a program with all of its system's runtime
            if (partOfRuntime)
                foreach (Host system in ownSystem is Host one ? new[] { one } : new[] { Host.Linux, Host.Windows })
                    yield return new Plan(target ?? system, "use memory.program_heap;" + ProgramWrapper, Root: RuntimeRoot(path), WholeHost: true);

            if (ownSystem == null && !partOfRuntime)
            {
                if (host != Host.PhiOS) yield return new Plan(host, machine + ProgramWrapper);
                else
                {
                    // (:OS classes are a 16-bit program's: nothing else takes them)
                    if (!own.Classes.Any(c => c.Base == Binder.KernelBase))
                    {
                        yield return new Plan(host, machine + KernelWrapper);
                        yield return new Plan(host, machine + ProgramWrapper);
                    }
                    yield return new Plan(host, machine + BootWrapper);
                }
            }

            // in a program that uses it, as it's built
            foreach (string user in UsersOf(path, open))
            {
                var parsing = new DiagnosticBag();
                ProgramNode theirs = Parser.Parse(new SourceFile(user, TextOf(user, open)), parsing);
                if (parsing.HasErrors) continue;
                Host system = target ?? SystemOf(theirs) ?? ownSystem ?? Host.PhiOS;
                string theirMachine = MachineClassFor(user, theirs);
                if (IsProgram(theirs))
                {
                    yield return new Plan(system, theirMachine, Root: user);
                    foreach (Host other in SystemsOfUses(user, TextOf(user, open), open).Where(o => o != system))
                        yield return new Plan(other, theirMachine, Root: user);
                }
                else
                {
                    yield return new Plan(system, theirMachine + KernelWrapper, Root: user);
                    yield return new Plan(system, theirMachine + ProgramWrapper, Root: user);
                }
            }
        }

        /// <summary>For a program with errors: the systems its modules are for, if it uses any that aren't this one's.</summary>
        IEnumerable<Plan> OtherSystems(CompileResult result, Plan tried)
        {
            if (target != null || tried.Root != null) yield break;
            var systems = new List<Host>();
            foreach (var (_, usedFile) in result.Uses.Values)
            {
                var ignored = new DiagnosticBag();
                if (SystemOf(Parser.Parse(usedFile, ignored)) is Host system && system != tried.Host && !systems.Contains(system))
                    systems.Add(system);
            }
            foreach (Host system in systems) yield return tried with { Host = system };
        }

        /// <summary>The systems of the modules a program uses.</summary>
        static IEnumerable<Host> SystemsOfUses(string path, string text, IReadOnlyDictionary<string, string> open)
        {
            CompileResult result = PhiCompiler.Compile(new SourceFile(path, text), generate: false, open: open);
            var ignored = new DiagnosticBag();
            return result.Uses.Values.Select(u => SystemOf(Parser.Parse(u.Target, ignored))).OfType<Host>().Distinct().ToList();
        }

        /// <summary>No errors in the file, in a program that really has the file in it.</summary>
        static bool IsGood(CompileResult result, string key) => result.Files.Contains(key) && result.Success;

        /// <summary>The result with only the errors in this file, when it was checked as a part of something else.</summary>
        static CompileResult Only(string key, CompileResult result)
        {
            if (result.Syntax == null || PhiCompiler.FileKey(result.Syntax.File.Path) == key) return result;
            return new CompileResult
            {
                Diagnostics = result.Diagnostics.Where(d => d.File != null && PhiCompiler.FileKey(d.File.Path) == key).ToList(),
                Syntax = result.Syntax,
                Bound = result.Bound,
                Uses = result.Uses,
                Files = result.Files,
            };
        }

        static int Errors(CompileResult result) => result.Diagnostics.Count(d => d.Severity == Severity.Error);

        static string TextOf(string path, IReadOnlyDictionary<string, string> open) =>
            open.TryGetValue(PhiCompiler.FileKey(path), out string? text) ? text : File.ReadAllText(path);

        /// <summary>Whether the file can be a program by itself; otherwise it's a module for one.</summary>
        static bool IsProgram(ProgramNode file) =>
            file.Classes.Any(c => c.Base is Binder.BootBase or Binder.Kernel32Base or Binder.ProgramBase);

        /// <summary>The system a file's classes are for: phi.Sockets:Windows is Windows' alone.</summary>
        static Host? SystemOf(ProgramNode file)
        {
            foreach (ClassDecl c in file.Classes)
                if (Binder.SystemBases.TryGetValue(c.Base, out Host[]? hosts))
                    return hosts[0];
            return null;
        }

        /// <summary>The name of the empty program that holds a system's runtime, which is never on the disk.</summary>
        static string RuntimeRoot(string path) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", "phi_check_runtime.phi");

        /// <summary>
        /// The class Machine, for a file that's a machine of a network: it has a NAME.machines,
        /// or one next to it lists it. Which machine it is doesn't matter to a check.
        /// </summary>
        static string MachineClassFor(string path, ProgramNode own)
        {
            if (own.Classes.Any(c => c.Name == "Machine")) return "";
            string full = Path.GetFullPath(path);
            if (NetworkMachines.ManifestFor(full) != null) return NetworkMachines.MachineClass(1, 1, 0x0A000301, 0x0A000301);

            string? folder = Path.GetDirectoryName(full);
            if (folder == null || !Directory.Exists(folder)) return "";
            foreach (string manifest in Directory.EnumerateFiles(folder, "*.machines"))
                if (NetworkMachines.Read(manifest).Any(m => m.File.Equals(full, StringComparison.OrdinalIgnoreCase)))
                    return NetworkMachines.MachineClass(1, 1, 0x0A000301, 0x0A000301);
            return "";
        }

        /// <summary>The .phi files in this repository (or folder, without one) with a `use` of this file's name.</summary>
        static IEnumerable<string> UsersOf(string path, IReadOnlyDictionary<string, string> open)
        {
            string full = Path.GetFullPath(path);
            string name = Path.GetFileNameWithoutExtension(full);
            var use = new Regex($@"^\s*use\s+([\w]+\.)*{Regex.Escape(name)}\s*;", RegexOptions.Multiline);

            int found = 0;
            foreach (string file in PhiFiles(RepositoryOf(full)))
            {
                if (file.Equals(full, StringComparison.OrdinalIgnoreCase)) continue;
                string text;
                try { text = TextOf(file, open); }
                catch (IOException) { continue; }   // gone since the folder was listed
                if (!use.IsMatch(text)) continue;
                yield return file;
                if (++found == MostUsers) yield break;
            }
        }

        /// <summary>The folder above a file with a .git in it, or else the file's own.</summary>
        static string RepositoryOf(string file)
        {
            string own = Path.GetDirectoryName(file) ?? "";
            for (string? dir = own; dir != null; dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
            return own;
        }

        static IEnumerable<string> PhiFiles(string folder)
        {
            if (!Directory.Exists(folder)) yield break;
            // (.NAME.phi: an editor's copy of unsaved text, here for a moment)
            foreach (string file in Directory.EnumerateFiles(folder, "*.phi").Where(f => !Path.GetFileName(f).StartsWith('.')).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                yield return file;
            foreach (string sub in Directory.EnumerateDirectories(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(sub);
                if (name is "build" or "bin" or "obj" or "node_modules" || name.StartsWith('.')) continue;
                foreach (string file in PhiFiles(sub)) yield return file;
            }
        }
    }
}

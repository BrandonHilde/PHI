using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Phi.Compiler;
using Phi.Compiler.CodeGen;

namespace Phi.Compiler.Tests
{
    /// <summary>
    /// The x86 code the compiler writes, kept still while the compiler around it changes: every
    /// test and sample, built for every system it's built for, hashed. Only when asked for:
    ///
    ///   PHI_ASM_SNAPSHOT=FILE PHI_ASM_SNAPSHOT_MODE=record dotnet test --filter AsmSnapshot
    ///       writes FILE: one line per build, its name and the SHA-256 of its NASM text (or of its
    ///       errors, for one that doesn't build)
    ///   PHI_ASM_SNAPSHOT=FILE dotnet test --filter AsmSnapshot
    ///       builds them all again and fails on any line that differs
    ///
    /// The switches the code generator reads once (PHI_IR=0, PHI_IR32=0) need a run of their own,
    /// each with its own FILE. A change meant to touch only another system's code (a new back
    /// end, the IR made neutral) shows here as no difference at all.
    /// </summary>
    public class AsmSnapshotTests
    {
        // (the wrappers phi test builds a library-only test with: Compiler/Phi.Cli/TestRunner.cs)
        const string Boot16 = "\nphi.PhiTestBoot:Bootloader\n{\n\tcall Bootloader.JumpToSectorTwo;\n}\n";
        const string Kernel32 = "\nphi.PhiTestKernel:Kernel\n{\n}\n";
        const string ProgramWrapper = "\nphi.PhiTestProgram:Program\n{\n}\n";

        static readonly Host[] OtherSystems = { Host.Linux, Host.Windows, Host.Linux64, Host.Windows64 };

        [Fact]
        public void TheCodeWrittenIsTheSameAsTheSnapshot()
        {
            string? file = Environment.GetEnvironmentVariable("PHI_ASM_SNAPSHOT");
            if (string.IsNullOrWhiteSpace(file)) return;   // only when asked for
            bool record = Environment.GetEnvironmentVariable("PHI_ASM_SNAPSHOT_MODE") == "record";

            string root = RepositoryRoot();
            var sources = Directory.GetFiles(Path.Combine(root, "tests"), "*.phi")
                .Concat(Directory.GetFiles(Path.Combine(root, "samples"), "*.phi", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}"))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();

            var builds = new List<(string Source, string Name, Host Host, string Wrapper, bool V2)>();
            foreach (string source in sources)
            {
                string rel = Path.GetRelativePath(root, source).Replace('\\', '/');
                builds.Add((source, $"{rel} phios", Host.PhiOS, "", false));
                builds.Add((source, $"{rel} phios+16", Host.PhiOS, Boot16, false));
                builds.Add((source, $"{rel} phios+32", Host.PhiOS, Kernel32, false));
                bool v2 = File.Exists(Path.ChangeExtension(source, ".build"))
                          && File.ReadAllText(Path.ChangeExtension(source, ".build")).Contains("--x86-64-v2");
                foreach (Host host in OtherSystems)
                {
                    builds.Add((source, $"{rel} {host.FlagName()}", host, "", false));
                    builds.Add((source, $"{rel} {host.FlagName()}+program", host, ProgramWrapper, false));
                    if (v2 && host.Is64()) builds.Add((source, $"{rel} {host.FlagName()}+v2", host, "", true));
                }
            }

            var hashes = new ConcurrentDictionary<string, string>();
            Parallel.ForEach(builds, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, b =>
            {
                // (the settings a build reads are per thread: set each for this one)
                X86Generator.X8664V2 = b.V2;
                X86Generator.MainStack = 0;
                hashes[b.Name] = Hash(b.Source, b.Wrapper, b.Host);
            });

            var lines = hashes.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}\t{h.Value}").ToList();
            if (record)
            {
                File.WriteAllLines(file, lines);
                return;
            }

            Assert.True(File.Exists(file), $"no snapshot {file}: record one first (PHI_ASM_SNAPSHOT_MODE=record)");
            var saved = File.ReadAllLines(file).Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1]);
            var differ = lines.Select(l => l.Split('\t'))
                .Where(p => !saved.TryGetValue(p[0], out string? was) || was != p[1]).Select(p => p[0])
                .Concat(saved.Keys.Where(k => !hashes.ContainsKey(k)).Select(k => k + " (gone)")).ToList();
            Assert.True(differ.Count == 0, $"{differ.Count} of {lines.Count} builds differ from {file}:\n  " + string.Join("\n  ", differ.Take(60)));
        }

        /// <summary>A build's NASM text, or its errors, hashed.</summary>
        static string Hash(string source, string wrapper, Host host)
        {
            string text, kind = "built";
            try
            {
                CompileResult result = PhiCompiler.Compile(new SourceFile(source, File.ReadAllText(source) + wrapper), host: host);
                var all = new StringBuilder();
                if (!result.Success)
                {
                    kind = "errors";
                    all.Append("errors\n");
                    foreach (Diagnostic d in result.Diagnostics) all.Append(d).Append('\n');
                }
                foreach (AsmUnit unit in result.BootStages.Concat(result.Units)) all.Append(unit.Text).Append('\0');
                text = all.ToString();
            }
            catch (Exception e)
            {
                kind = "threw";
                text = e.GetType().Name + ": " + e.Message;
            }
            return kind + " " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }

        static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "PHI.sln"))) return dir.FullName;
            throw new InvalidOperationException("PHI.sln not found above " + AppContext.BaseDirectory);
        }
    }
}

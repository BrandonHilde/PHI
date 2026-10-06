using System.Reflection;

namespace Phi.Compiler.CodeGen
{
    /// <summary>
    /// One file of the standard library. Its header says what it defines and needs:
    ///   ; provides: label label ...
    ///   ; requires: label label ...
    ///   ; hooks: label ...          (events the code calls; a no-op is used if the program has none)
    ///   ; init: label ...           (called once when the program starts)
    ///   ; inherit: target file ...  (use these files of another target's library too)
    /// </summary>
    public sealed class LibraryFile
    {
        public string Name { get; init; } = "";
        public string Text { get; init; } = "";
        public List<string> Provides { get; } = new();
        public List<string> Requires { get; } = new();
        public List<string> Hooks { get; } = new();
        public List<string> Init { get; } = new();
        public List<string> Inherit { get; } = new();
    }

    public static class Library
    {
        static readonly Dictionary<string, List<LibraryFile>> cache = new();

        /// <summary>All library files for a target such as "x86_16".</summary>
        public static IReadOnlyList<LibraryFile> Load(string target)
        {
            lock (cache)
            {
                if (cache.TryGetValue(target, out var files)) return files;

                files = new List<LibraryFile>();
                Assembly assembly = typeof(Library).Assembly;
                string prefix = $"lib/{target}/";

                foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.Replace('\\', '/').StartsWith(prefix)).OrderBy(n => n))
                {
                    using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                    files.Add(Parse(resource.Replace('\\', '/')[prefix.Length..], reader.ReadToEnd()));
                }

                // files borrowed from another target's library (inherit: target file file ...)
                foreach (LibraryFile f in files.ToList().Where(f => f.Inherit.Count > 1))
                {
                    IReadOnlyList<LibraryFile> parent = Load(f.Inherit[0]);
                    foreach (string name in f.Inherit.Skip(1))
                        files.Add(parent.FirstOrDefault(p => p.Name == name)
                                  ?? throw new InvalidOperationException($"lib/{target}/{f.Name} inherits {name}, which lib/{f.Inherit[0]} doesn't have"));
                }

                cache[target] = files;
                return files;
            }
        }

        static LibraryFile Parse(string name, string text)
        {
            var file = new LibraryFile { Name = name, Text = text.Replace("\r\n", "\n").TrimEnd() + "\n" };

            foreach (string line in file.Text.Split('\n'))
            {
                if (!line.StartsWith(';')) break;
                string content = line.TrimStart(';').Trim();

                List<string>? list = content.StartsWith("provides:") ? file.Provides
                    : content.StartsWith("requires:") ? file.Requires
                    : content.StartsWith("hooks:") ? file.Hooks
                    : content.StartsWith("init:") ? file.Init
                    : content.StartsWith("inherit:") ? file.Inherit
                    : null;

                if (list != null)
                    list.AddRange(content[(content.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            return file;
        }

        /// <summary>True when the target's library defines the label.</summary>
        public static bool Provides(string target, string symbol) =>
            Load(target).Any(f => f.Provides.Contains(symbol));

        /// <summary>A file of the library that isn't a routine collection, like lib/boot/stage1.asm.</summary>
        public static string ReadFile(string path)
        {
            Assembly assembly = typeof(Library).Assembly;
            string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == path)
                ?? throw new InvalidOperationException($"the compiler has no {path}");
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// The files needed to define <paramref name="symbols"/>, with their dependencies, each
        /// once, in a stable order.
        /// </summary>
        public static List<LibraryFile> Resolve(string target, IEnumerable<string> symbols)
        {
            IReadOnlyList<LibraryFile> files = Load(target);
            var byProvide = new Dictionary<string, LibraryFile>();
            foreach (LibraryFile f in files)
                foreach (string p in f.Provides)
                    byProvide[p] = f;

            var result = new List<LibraryFile>();
            var seen = new HashSet<LibraryFile>();

            void Visit(string symbol)
            {
                if (!byProvide.TryGetValue(symbol, out LibraryFile? file))
                    throw new InvalidOperationException($"the {target} library has no '{symbol}'");
                if (!seen.Add(file)) return;
                result.Add(file);
                foreach (string r in file.Requires) Visit(r);
            }

            foreach (string s in symbols) Visit(s);
            return result;
        }
    }
}

using System.Text;

namespace Phi.Compiler.Libraries
{
    /// <summary>One of a library's own source files, as declarations with no code (see <see cref="InterfaceWriter"/>).</summary>
    public sealed record InterfaceFile(string Name, string Text);

    /// <summary>
    /// A place in a library's code or data that needs an address the library doesn't know: the
    /// final build writes it as `dd label + addend`, or `dd label + addend - $` when it's relative.
    /// </summary>
    /// <param name="Section">Which section it's in.</param>
    /// <param name="Symbol">A label outside the section's library, or null for a place in it.</param>
    /// <param name="TargetSection">With no <paramref name="Symbol"/>: the section that place is in.</param>
    public sealed record Relocation(int Section, int Offset, int Width, bool Relative, string? Symbol, int TargetSection, long Addend);

    /// <summary>A label the library gives other code: a method or a class variable.</summary>
    public sealed record LibraryExport(string Name, int Section, int Offset);

    /// <summary>Where a source line's code starts, for an error's line.</summary>
    public sealed record LibraryLine(int Section, int Offset, int Line, string File);

    /// <summary>
    /// Part of a library's code or data. Each method is a section of its own, so a program only
    /// gets those it reaches. The first section is the code that's always there (its classes'
    /// code), the second its variables.
    /// </summary>
    public sealed record LibrarySection(byte[] Bytes, int Align);

    /// <summary>What a library is for one system: its interface, its machine code, and what it needs.</summary>
    public sealed class CompiledTarget
    {
        public const int AlwaysCode = 0, Variables = 1;

        /// <summary>The library's name, as its labels start: phi_lib_NAME_text.</summary>
        public string Name { get; set; } = "";

        /// <summary>Where the file is (not kept in it): a library it uses is looked for next to it.</summary>
        public string Path { get; set; } = "";

        public List<InterfaceFile> Interface { get; } = new();

        /// <summary>The standard library's modules its code uses (the final build compiles one copy of each).</summary>
        public List<string> Modules { get; } = new();

        public List<string> RuntimeSymbols { get; } = new();

        /// <summary>The standard library's assembly blocks its code uses, as asm.Name or x64.Name.</summary>
        public List<string> RawBlocks { get; } = new();

        /// <summary>The outside functions its code calls, as Kernel32.CreateThread.</summary>
        public List<string> OutsideFunctions { get; } = new();

        /// <summary>The structs whose type tables its code uses: there's one of each in a program.</summary>
        public List<string> TypeTables { get; } = new();

        /// <summary>The interfaces whose slots its code uses.</summary>
        public List<string> InterfaceSlots { get; } = new();

        /// <summary>Its code uses objects, so it sets phi_object_line.</summary>
        public bool UsesObjects { get; set; }

        public List<LibrarySection> Sections { get; } = new();
        public List<Relocation> Relocations { get; } = new();
        public List<LibraryExport> Exports { get; } = new();
        public List<LibraryLine> Lines { get; } = new();

        public string Prefix => "phi_lib_" + Name;
        public string StartLabel => Prefix + "_start";
        public string SectionLabel(int section) => $"{Prefix}_s{section}";

        /// <summary>A library's labels start with its name, which may be any file's.</summary>
        public static string LabelName(string name) => new(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
    }

    /// <summary>The systems a library can be built for: a program or a kernel on PHI OS, and programs elsewhere.</summary>
    public sealed record LibraryPlatform(string Key, Host Host, bool Kernel, string Description)
    {
        public static readonly LibraryPlatform[] All =
        {
            new("program", Host.PhiOS, false, "PHI OS programs"),
            new("kernel", Host.PhiOS, true, "PHI OS kernels"),
            new("linux", Host.Linux, false, "32-bit Linux"),
            new("linux64", Host.Linux64, false, "64-bit Linux"),
            new("windows", Host.Windows, false, "32-bit Windows"),
            new("windows64", Host.Windows64, false, "64-bit Windows"),
        };

        public static LibraryPlatform Of(Host host, bool kernel) =>
            All.First(p => p.Host == host && p.Kernel == (host == Host.PhiOS && kernel));
    }

    /// <summary>Why a .philib can't be used.</summary>
    public sealed class LibraryFileException : Exception
    {
        public LibraryFileException(string message) : base(message) { }
    }

    /// <summary>
    /// A .philib: a library compiled for one or more systems, which a program includes with
    /// `use` without the library's source. The code goes inside the program, so nothing is
    /// loaded when it runs.
    /// </summary>
    public sealed class CompiledLibrary
    {
        public const string Extension = ".philib";
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("PHILIB\n");

        /// <summary>
        /// Which compiler built it. Until a release the conventions between compiled code can
        /// change with any build of the compiler or its standard library, whose copy of the
        /// modules a library uses is the one that's compiled in. Identical builds of the compiler
        /// get the same value.
        /// </summary>
        public static string CompilerVersion => typeof(CompiledLibrary).Assembly.ManifestModule.ModuleVersionId.ToString();

        public string Name { get; init; } = "";
        public Dictionary<string, CompiledTarget> Targets { get; } = new();

        public void Write(string path)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream, Encoding.UTF8);
            w.Write(Magic);
            w.Write(CompilerVersion);
            w.Write(Name);
            w.Write(Targets.Count);
            foreach (var (key, t) in Targets)
            {
                w.Write(key);
                w.Write(t.Interface.Count);
                foreach (InterfaceFile f in t.Interface) { w.Write(f.Name); w.Write(f.Text); }
                foreach (List<string> list in new[] { t.Modules, t.RuntimeSymbols, t.RawBlocks, t.OutsideFunctions, t.TypeTables, t.InterfaceSlots })
                {
                    w.Write(list.Count);
                    foreach (string s in list) w.Write(s);
                }
                w.Write(t.UsesObjects);
                w.Write(t.Sections.Count);
                foreach (LibrarySection s in t.Sections)
                {
                    w.Write(s.Align);
                    w.Write(s.Bytes.Length);
                    w.Write(s.Bytes);
                }
                w.Write(t.Relocations.Count);
                foreach (Relocation r in t.Relocations)
                {
                    w.Write(r.Section);
                    w.Write(r.Offset);
                    w.Write(r.Width);
                    w.Write(r.Relative);
                    w.Write(r.Symbol ?? "");
                    w.Write(r.TargetSection);
                    w.Write(r.Addend);
                }
                w.Write(t.Exports.Count);
                foreach (LibraryExport e in t.Exports) { w.Write(e.Name); w.Write(e.Section); w.Write(e.Offset); }
                w.Write(t.Lines.Count);
                foreach (LibraryLine l in t.Lines) { w.Write(l.Section); w.Write(l.Offset); w.Write(l.Line); w.Write(l.File); }
            }
            w.Flush();
            File.WriteAllBytes(path, stream.ToArray());
        }

        /// <exception cref="LibraryFileException">It isn't a library, or another version of phi built it.</exception>
        public static CompiledLibrary Load(string path)
        {
            string shown = System.IO.Path.GetFileName(path);
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < Magic.Length || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                throw new LibraryFileException($"{shown} isn't a compiled PHI library");
            try
            {
                using var r = new BinaryReader(new MemoryStream(bytes, Magic.Length, bytes.Length - Magic.Length), Encoding.UTF8);
                if (r.ReadString() != CompilerVersion)
                    throw new LibraryFileException($"{shown} was built by another version of phi: build it again from its source (phi build NAME.phi --library)");
                var library = new CompiledLibrary { Name = r.ReadString() };
                int targets = r.ReadInt32();
                for (int i = 0; i < targets; i++)
                {
                    string key = r.ReadString();
                    var t = new CompiledTarget { Name = CompiledTarget.LabelName(library.Name), Path = path };
                    int files = r.ReadInt32();
                    for (int f = 0; f < files; f++) t.Interface.Add(new InterfaceFile(r.ReadString(), r.ReadString()));
                    foreach (List<string> list in new[] { t.Modules, t.RuntimeSymbols, t.RawBlocks, t.OutsideFunctions, t.TypeTables, t.InterfaceSlots })
                    {
                        int count = r.ReadInt32();
                        for (int s = 0; s < count; s++) list.Add(r.ReadString());
                    }
                    t.UsesObjects = r.ReadBoolean();
                    int sections = r.ReadInt32();
                    for (int n = 0; n < sections; n++)
                    {
                        int align = r.ReadInt32();
                        t.Sections.Add(new LibrarySection(r.ReadBytes(r.ReadInt32()), align));
                    }
                    int relocations = r.ReadInt32();
                    for (int n = 0; n < relocations; n++)
                    {
                        int section = r.ReadInt32(), offset = r.ReadInt32(), width = r.ReadInt32();
                        bool relative = r.ReadBoolean();
                        string symbol = r.ReadString();
                        int target = r.ReadInt32();
                        long addend = r.ReadInt64();
                        t.Relocations.Add(new Relocation(section, offset, width, relative, symbol.Length > 0 ? symbol : null, target, addend));
                    }
                    int exports = r.ReadInt32();
                    for (int n = 0; n < exports; n++) t.Exports.Add(new LibraryExport(r.ReadString(), r.ReadInt32(), r.ReadInt32()));
                    int lines = r.ReadInt32();
                    for (int n = 0; n < lines; n++) t.Lines.Add(new LibraryLine(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadString()));
                    library.Targets[key] = t;
                }
                return library;
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or FormatException)
            {
                throw new LibraryFileException($"{shown} is damaged: build it again from its source (phi build NAME.phi --library)");
            }
        }
    }

    /// <summary>Reads what NASM writes for -f elf32 or -f elf64 into a library's code, data and lists.</summary>
    public static class ElfObject
    {
        const int Bits = 1, SymbolTable = 2, Rela = 4, Rel = 9;

        sealed record Section(string Name, int Type, long Offset, long Size, int Link, int Info, long Align);
        sealed record Symbol(string Name, long Value, int Index, int Binding);

        /// <param name="exports">The labels others use, which NASM was told are global.</param>
        /// <param name="lines">Each line's label (which is only in the object's own list), line and file.</param>
        public static void ReadInto(CompiledTarget target, byte[] d, IReadOnlyCollection<string> exports, IEnumerable<(string Label, int Line, string File)> lines)
        {
            bool is64 = d[4] == 2;
            long shoff = is64 ? BitConverter.ToInt64(d, 0x28) : BitConverter.ToUInt32(d, 0x20);
            int shentsize = BitConverter.ToUInt16(d, is64 ? 0x3A : 0x2E);
            int shnum = BitConverter.ToUInt16(d, is64 ? 0x3C : 0x30);
            int shstrndx = BitConverter.ToUInt16(d, is64 ? 0x3E : 0x32);

            string Text(long at)
            {
                int end = (int)at;
                while (d[end] != 0) end++;
                return Encoding.ASCII.GetString(d, (int)at, end - (int)at);
            }

            var raw = new List<(int Name, Section S)>();
            for (int i = 0; i < shnum; i++)
            {
                int o = (int)shoff + i * shentsize;
                raw.Add(is64
                    ? (BitConverter.ToInt32(d, o), new Section("", BitConverter.ToInt32(d, o + 4), BitConverter.ToInt64(d, o + 24), BitConverter.ToInt64(d, o + 32),
                                                               BitConverter.ToInt32(d, o + 40), BitConverter.ToInt32(d, o + 44), BitConverter.ToInt64(d, o + 48)))
                    : (BitConverter.ToInt32(d, o), new Section("", BitConverter.ToInt32(d, o + 4), BitConverter.ToUInt32(d, o + 16), BitConverter.ToUInt32(d, o + 20),
                                                               BitConverter.ToInt32(d, o + 24), BitConverter.ToInt32(d, o + 28), BitConverter.ToUInt32(d, o + 32))));
            }
            long names = raw[shstrndx].S.Offset;
            List<Section> sections = raw.Select(r => r.S with { Name = Text(names + r.Name) }).ToList();

            // .text and .data first, then each method's (.t0, .t1 ...)
            var parts = new Dictionary<int, int>();
            void AddPart(int index)
            {
                parts[index] = target.Sections.Count;
                Section s = sections[index];
                byte[] bytes = s.Type == Bits ? d[(int)s.Offset..(int)(s.Offset + s.Size)] : Array.Empty<byte>();
                target.Sections.Add(new LibrarySection(bytes, Math.Max((int)s.Align, 1)));
            }
            AddPart(sections.FindIndex(s => s.Name == ".text"));
            AddPart(sections.FindIndex(s => s.Name == ".data"));
            for (int i = 0; i < sections.Count; i++)
                if (sections[i].Type == Bits && !parts.ContainsKey(i) && sections[i].Name.StartsWith(".t"))
                    AddPart(i);
            int PartOf(int index) => parts.TryGetValue(index, out int part) ? part : -1;

            Section table = sections.First(s => s.Type == SymbolTable);
            long strings = sections[table.Link].Offset;
            int entry = is64 ? 24 : 16;
            var symbols = new List<Symbol>();
            for (int i = 0; i < table.Size / entry; i++)
            {
                int o = (int)table.Offset + i * entry;
                symbols.Add(is64
                    ? new Symbol(Text(strings + BitConverter.ToInt32(d, o)), BitConverter.ToInt64(d, o + 8), BitConverter.ToUInt16(d, o + 6), d[o + 4] >> 4)
                    : new Symbol(Text(strings + BitConverter.ToInt32(d, o)), BitConverter.ToUInt32(d, o + 4), BitConverter.ToUInt16(d, o + 14), d[o + 12] >> 4));
            }

            foreach (Section s in sections.Where(s => s.Type is Rela or Rel))
            {
                int section = PartOf(s.Info);
                if (section < 0) continue;
                byte[] bytes = target.Sections[section].Bytes;
                int size = s.Type == Rela ? (is64 ? 24 : 12) : (is64 ? 16 : 8);
                for (int i = 0; i < s.Size / size; i++)
                {
                    int o = (int)s.Offset + i * size;
                    int at, type, sym;
                    long? addend = null;
                    if (is64)
                    {
                        at = (int)BitConverter.ToInt64(d, o);
                        long info = BitConverter.ToInt64(d, o + 8);
                        sym = (int)(info >> 32);
                        type = (int)(info & 0xFFFFFFFF);
                        if (s.Type == Rela) addend = BitConverter.ToInt64(d, o + 16);
                    }
                    else
                    {
                        at = BitConverter.ToInt32(d, o);
                        int info = BitConverter.ToInt32(d, o + 4);
                        sym = (int)((uint)info >> 8);
                        type = info & 0xFF;
                        if (s.Type == Rela) addend = BitConverter.ToInt32(d, o + 8);
                    }

                    (int width, bool relative) = (is64, type) switch
                    {
                        (true, 1) => (8, false),            // R_X86_64_64
                        (true, 2) or (true, 4) => (4, true),    // PC32, PLT32
                        (true, 10) or (true, 11) => (4, false), // 32, 32S
                        (true, 12) => (2, false),
                        (true, 13) => (2, true),
                        (true, 14) => (1, false),
                        (true, 15) => (1, true),
                        (false, 1) => (4, false),           // R_386_32
                        (false, 2) => (4, true),            // R_386_PC32
                        (false, 20) => (2, false),
                        (false, 21) => (2, true),
                        (false, 22) => (1, false),
                        (false, 23) => (1, true),
                        _ => throw new LibraryFileException($"NASM wrote a kind of address phi doesn't know (relocation {type}), which is a bug in phi"),
                    };

                    addend ??= width switch
                    {
                        4 => BitConverter.ToInt32(bytes, at),
                        2 => BitConverter.ToInt16(bytes, at),
                        _ => (sbyte)bytes[at],
                    };

                    Symbol y = symbols[sym];
                    int part = PartOf(y.Index);
                    target.Relocations.Add(part >= 0
                        ? new Relocation(section, at, width, relative, null, part, y.Value + addend.Value)
                        : new Relocation(section, at, width, relative, y.Name, 0, addend.Value));
                }
            }
            target.Relocations.Sort((a, b) => a.Section != b.Section ? a.Section.CompareTo(b.Section) : a.Offset.CompareTo(b.Offset));

            var byName = symbols.Where(y => y.Name.Length > 0).GroupBy(y => y.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (string name in exports)
                if (byName.TryGetValue(name, out Symbol? y) && PartOf(y.Index) is var part and >= 0)
                    target.Exports.Add(new LibraryExport(name, part, (int)y.Value));
            foreach (var (label, line, file) in lines)
                if (byName.TryGetValue(label, out Symbol? y) && PartOf(y.Index) is var part and >= 0)
                    target.Lines.Add(new LibraryLine(part, (int)y.Value, line, file));
            target.Lines.Sort((a, b) => a.Section != b.Section ? a.Section.CompareTo(b.Section) : a.Offset.CompareTo(b.Offset));
        }
    }
}

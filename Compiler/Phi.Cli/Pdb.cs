using System.Security.Cryptography;
using System.Text;
using Phi.Compiler;
using Phi.Compiler.CodeGen;
using Phi.Compiler.Semantics;

namespace Phi.Cli
{
    /// <summary>
    /// Writes the .pdb of a --debug build of a Windows program, which is how a debugger there
    /// knows PHI's lines, methods and variables (H8 step 1). PHI has no linker, so nothing else
    /// would write one.
    ///
    /// A .pdb is a small file system (MSF): numbered streams, each in blocks anywhere in the file.
    /// A program needs these: 1 says which build it goes with; 2 has the types; 3 lists the
    /// modules and sections; /names has the source files' names; the module's stream has its
    /// procedures, their variables and which code each line became (CodeView records). PHI's
    /// whole program is one module, of two sections, code and variables.
    /// </summary>
    public static class Pdb
    {
        const int BlockSize = 4096;
        const ushort NoStream = 0xFFFF;
        internal const uint SectionFlags = 0xE0000060; // code and data, run, read, write

        public static void Write(DebugInfo info, string outDir, string name, Host host, byte[] executable)
        {
            string mapPath = Path.Combine(outDir, "program.map");
            Dictionary<string, uint> labels = ReadMap(mapPath);
            string pdbPath = Path.GetFullPath(Path.Combine(outDir, name + ".pdb"));

            uint imageBase = X86Generator.WindowsAddress;
            uint codeStart = labels["phi_pe_code"];
            uint dataStart = labels["phi_pe_data"];
            uint imageEnd = labels["phi_image_end"];
            var sections = new[] { (Start: codeStart, Size: dataStart - codeStart), (Start: dataStart, Size: imageEnd - dataStart) };
            uint recordAt = labels["phi_debug_record"] - imageBase;

            var symbols = new ModuleWriter(info, labels, codeStart, dataStart, host.Is64(), outDir);
            byte[] module = symbols.Build();

            // this build, and its .pdb, are told apart by a number made from the program and what
            // its .pdb says of it: a comment moves its lines, while its code stays the same, and a
            // debugger keeps the .pdbs it has seen by this number
            byte[] guid = MD5.HashData(executable.Concat(module).ToArray());
            const uint age = 1;
            FillRecord(executable, (int)recordAt, guid, age, pdbPath);

            var streams = new List<byte[]>();
            int Add(byte[] stream)
            {
                streams.Add(stream);
                return streams.Count - 1;
            }

            Add(Array.Empty<byte>());                               // 0: the old directory, not used
            int infoStream = Add(Array.Empty<byte>());              // 1: filled in below
            int typesStream = Add(Array.Empty<byte>());             // 2: filled in below
            int dbiStream = Add(Array.Empty<byte>());               // 3: filled in below
            Add(TypeTable.EmptyStream());                           // 4: type ids, of which there are none
            int namesStream = Add(symbols.Names.Build());
            int moduleStream = Add(module);
            int sectionStream = Add(SectionHeaders(sections.Select(s => (s.Start - imageBase, s.Size)).ToArray()));
            int recordsStream = Add(symbols.Index.Records());
            int globalsStream = Add(symbols.Index.Globals());
            int publicsStream = Add(symbols.Index.Publics());

            streams[typesStream] = symbols.Types.BuildStream(Add(symbols.Types.BuildHashes()));
            streams[infoStream] = InfoStream(guid, age, namesStream);
            streams[dbiStream] = DbiStream(host, age, name, symbols, moduleStream, sectionStream,
                                           sections.Select(s => s.Size).ToArray(), recordsStream, globalsStream, publicsStream);

            File.WriteAllBytes(pdbPath, Msf(streams));
        }

        /// <summary>The label addresses from NASM's map file.</summary>
        static Dictionary<string, uint> ReadMap(string path)
        {
            var labels = new Dictionary<string, uint>();
            foreach (string line in File.ReadLines(path))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out uint address))
                    labels[parts[2]] = address;
            }
            return labels;
        }

        /// <summary>The program's record of which .pdb goes with it: RSDS, the number, the age, the path.</summary>
        static void FillRecord(byte[] executable, int at, byte[] guid, uint age, string pdbPath)
        {
            byte[] path = Encoding.UTF8.GetBytes(pdbPath);
            if (path.Length + 25 > DebugInfo.RecordSize) path = Encoding.UTF8.GetBytes(Path.GetFileName(pdbPath));
            Encoding.ASCII.GetBytes("RSDS").CopyTo(executable, at);
            guid.CopyTo(executable, at + 4);
            BitConverter.GetBytes(age).CopyTo(executable, at + 20);
            path.CopyTo(executable, at + 24);
        }

        // ---------------------------------------------------------------- the file system

        static byte[] Msf(List<byte[]> streams)
        {
            // block 0 is the header, and 1 and 2 of every 4096 say which blocks are free
            int next = 3;
            int Take()
            {
                while (next % BlockSize is 1 or 2) next++;
                return next++;
            }
            static int BlocksOf(int bytes) => (bytes + BlockSize - 1) / BlockSize;

            var placed = new List<int[]>();
            foreach (byte[] stream in streams)
            {
                var blocks = new int[BlocksOf(stream.Length)];
                for (int i = 0; i < blocks.Length; i++) blocks[i] = Take();
                placed.Add(blocks);
            }

            var directory = new ByteWriter();
            directory.U32((uint)streams.Count);
            foreach (byte[] stream in streams) directory.U32((uint)stream.Length);
            foreach (int[] blocks in placed)
                foreach (int block in blocks) directory.U32((uint)block);
            byte[] directoryBytes = directory.ToArray();

            var directoryBlocks = new int[BlocksOf(directoryBytes.Length)];
            for (int i = 0; i < directoryBlocks.Length; i++) directoryBlocks[i] = Take();
            int directoryMap = Take();

            int total = next;
            var file = new byte[(long)total * BlockSize];

            void Place(byte[] data, int[] blocks)
            {
                for (int i = 0; i < blocks.Length; i++)
                    Array.Copy(data, i * BlockSize, file, blocks[i] * BlockSize, Math.Min(BlockSize, data.Length - i * BlockSize));
            }

            for (int s = 0; s < streams.Count; s++) Place(streams[s], placed[s]);
            Place(directoryBytes, directoryBlocks);

            var map = new ByteWriter();
            foreach (int block in directoryBlocks) map.U32((uint)block);
            Place(map.ToArray(), new[] { directoryMap });

            // the header
            var header = new ByteWriter();
            header.Bytes(Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"));
            header.U32(BlockSize);
            header.U32(1);                      // which of the free block maps is in use
            header.U32((uint)total);
            header.U32((uint)directoryBytes.Length);
            header.U32(0);
            header.U32((uint)directoryMap);
            header.ToArray().CopyTo(file, 0);

            // the free block maps: a bit for each block, 1 if it's free. None is, and those past
            // the end are, as far as the map's own block goes.
            for (int b = 1; b < total; b += BlockSize)
            {
                for (int copy = 0; copy < 2; copy++)
                {
                    int at = (b + copy) * BlockSize;
                    if (at + BlockSize > file.Length) continue;
                    for (int i = 0; i < BlockSize; i++) file[at + i] = 0xFF;
                    int first = b - 1;
                    for (int block = first; block < Math.Min(total, first + BlockSize * 8); block++)
                        file[at + (block - first) / 8] &= (byte)~(1 << ((block - first) % 8));
                }
            }
            return file;
        }

        // ---------------------------------------------------------------- the streams

        /// <summary>Stream 1: which build this is, and the numbers of the streams that have names.</summary>
        static byte[] InfoStream(byte[] guid, uint age, int namesStream)
        {
            var w = new ByteWriter();
            w.U32(20000404);                    // the version
            w.U32(0);                           // a time (unused: the number below says which build)
            w.U32(age);
            w.Bytes(guid);

            byte[] name = Encoding.ASCII.GetBytes("/names\0");
            w.U32((uint)name.Length);
            w.Bytes(name);

            // a hash table of one, with room for two
            uint slot = (HashV1("/names") & 0xFFFF) % 2;
            w.U32(1);                           // how many
            w.U32(2);                           // room
            w.U32(1);                           // which are in use: one word
            w.U32(1u << (int)slot);
            w.U32(0);                           // which have been deleted: no words
            w.U32(0);                           // the name's place
            w.U32((uint)namesStream);

            w.U32(0);
            w.U32(20140508);                    // "VC140": it has the type id stream
            return w.ToArray();
        }

        /// <summary>The hash a .pdb uses for names.</summary>
        internal static uint HashV1(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            uint result = 0;
            int i = 0;
            for (; i + 4 <= bytes.Length; i += 4) result ^= BitConverter.ToUInt32(bytes, i);
            if (bytes.Length - i >= 2)
            {
                result ^= BitConverter.ToUInt16(bytes, i);
                i += 2;
            }
            if (bytes.Length - i == 1) result ^= bytes[i];
            result |= 0x20202020;
            result ^= result >> 11;
            return result ^ (result >> 16);
        }

        static byte[] SectionHeaders((uint Address, uint Size)[] sections)
        {
            var w = new ByteWriter();
            string[] names = { ".text\0\0\0", ".data\0\0\0" };
            for (int i = 0; i < sections.Length; i++)
            {
                w.Bytes(Encoding.ASCII.GetBytes(names[i]));
                w.U32(sections[i].Size);
                w.U32(sections[i].Address);
                w.U32(sections[i].Size);
                w.U32(sections[i].Address);     // in the file it's where it is in memory
                w.U32(0);
                w.U32(0);
                w.U16(0);
                w.U16(0);
                w.U32(SectionFlags);
            }
            return w.ToArray();
        }

        /// <summary>Stream 3: the modules (one), the sections and the source files.</summary>
        static byte[] DbiStream(Host host, uint age, string name, ModuleWriter module, int moduleStream,
                                int sectionStream, uint[] sectionSizes, int recordsStream, int globalsStream, int publicsStream)
        {
            void Contribution(ByteWriter w, int section)
            {
                w.U16(section + 1);
                w.U16(0);
                w.U32(0);                       // where in the section
                w.U32(sectionSizes[section]);
                w.U32(SectionFlags);
                w.U16(0);                       // the module
                w.U16(0);
                w.U32(0);
                w.U32(0);
            }

            var modules = new ByteWriter();
            modules.U32(0);
            Contribution(modules, 0);
            modules.U16(0);                     // flags
            modules.U16((ushort)moduleStream);
            modules.U32((uint)module.SymbolBytes);
            modules.U32(0);                     // C11 lines: none
            modules.U32((uint)module.LineBytes);
            modules.U16((ushort)module.Files.Count);
            modules.U16(0);
            modules.U32(0);
            modules.U32(0);
            modules.U32(0);
            modules.Text(name + ".phi");
            modules.Text(name + ".phi");
            modules.Align(4);

            var contributions = new ByteWriter();
            contributions.U32(0xF12EBA2D);      // the version
            for (int i = 0; i < sectionSizes.Length; i++) Contribution(contributions, i);

            var sections = new ByteWriter();
            sections.U16(sectionSizes.Length + 1);
            sections.U16(sectionSizes.Length + 1);
            for (int i = 0; i < sectionSizes.Length; i++)
            {
                sections.U16(0x010F);           // read, write, run, a 32-bit address, a selector
                sections.U16(0);
                sections.U16(0);
                sections.U16(i + 1);
                sections.U16(0xFFFF);
                sections.U16(0xFFFF);
                sections.U32(0);
                sections.U32(sectionSizes[i]);
            }
            // then one that stands for addresses that aren't in any
            sections.U16(0x0208);               // absolute, a 32-bit address
            sections.U16(0);
            sections.U16(0);
            sections.U16(sectionSizes.Length + 1);
            sections.U16(0xFFFF);
            sections.U16(0xFFFF);
            sections.U32(0);
            sections.U32(0xFFFFFFFF);

            var files = new ByteWriter();
            files.U16(1);
            files.U16((ushort)module.Files.Count);
            files.U16(0);
            files.U16((ushort)module.Files.Count);
            var fileNames = new ByteWriter();
            foreach (string f in module.Files)
            {
                files.U32((uint)fileNames.Length);
                fileNames.Text(f);
            }
            files.Bytes(fileNames.ToArray());
            files.Align(4);

            var debugHeaders = new ByteWriter();
            for (int i = 0; i < 11; i++)
                debugHeaders.U16(i == 5 ? (ushort)sectionStream : NoStream); // 5: the section headers

            var w = new ByteWriter();
            w.U32(0xFFFFFFFF);
            w.U32(19990903);                    // the version
            w.U32(age);
            w.U16((ushort)globalsStream);
            w.U16(0x8E00);                      // made in the style of a version 14 tool
            w.U16((ushort)publicsStream);
            w.U16(0);
            w.U16((ushort)recordsStream);
            w.U16(0);
            w.U32((uint)modules.Length);
            w.U32((uint)contributions.Length);
            w.U32((uint)sections.Length);
            w.U32((uint)files.Length);
            w.U32(0);                           // no type servers
            w.U32(0);
            w.U32((uint)debugHeaders.Length);
            w.U32(0);                           // no names of edit-and-continue's
            w.U16(0);
            w.U16((ushort)(host.Is64() ? 0x8664 : 0x014C));
            w.U32(0);
            w.Bytes(modules.ToArray());
            w.Bytes(contributions.ToArray());
            w.Bytes(sections.ToArray());
            w.Bytes(files.ToArray());
            w.Bytes(debugHeaders.ToArray());
            return w.ToArray();
        }
    }

    /// <summary>Little-endian bytes, added to the end.</summary>
    sealed class ByteWriter
    {
        readonly MemoryStream stream = new();
        public int Length => (int)stream.Length;
        public void U8(int v) => stream.WriteByte((byte)v);
        public void U16(int v) { U8(v); U8(v >> 8); }
        public void U32(uint v) { U16((int)(v & 0xFFFF)); U16((int)(v >> 16)); }
        public void Bytes(byte[] b) => stream.Write(b);
        public void Text(string s) { Bytes(Encoding.UTF8.GetBytes(s)); U8(0); }
        public void Align(int to) { while (Length % to != 0) U8(0); }
        public byte[] ToArray() => stream.ToArray();
    }

    /// <summary>The /names stream: strings that are referred to by where they are in it.</summary>
    sealed class NameTable
    {
        readonly ByteWriter text = new();
        readonly List<(string Name, uint At)> names = new();

        public NameTable() => text.U8(0);       // place 0 is no name

        public uint Add(string name)
        {
            foreach (var n in names) if (n.Name == name) return n.At;
            uint at = (uint)text.Length;
            text.Text(name);
            names.Add((name, at));
            return at;
        }

        public byte[] Build()
        {
            var w = new ByteWriter();
            w.U32(0xEFFEEFFE);
            w.U32(1);                           // hash version
            w.U32((uint)text.Length);
            w.Bytes(text.ToArray());
            int count = Math.Max(1, names.Count * 2);
            var buckets = new uint[count];
            foreach (var (name, at) in names)
            {
                int b = (int)(Pdb.HashV1(name) % (uint)count);
                while (buckets[b] != 0) b = (b + 1) % count;
                buckets[b] = at;
            }
            w.U32((uint)count);
            foreach (uint b in buckets) w.U32(b);
            w.U32((uint)names.Count);
            return w.ToArray();
        }
    }

    /// <summary>
    /// The names a debugger looks up without knowing which module to look in: the records, and
    /// a hash table of them for the globals and another for the publics.
    /// </summary>
    sealed class SymbolIndex
    {
        const int S_PUB32 = 0x110E, S_GDATA32 = 0x110D, S_PROCREF = 0x1125, S_UDT = 0x1108;
        const int Buckets = 4096;

        readonly ByteWriter records = new();
        readonly List<(string Name, uint At)> globals = new();
        readonly List<(string Name, uint At, uint Offset)> publics = new();

        uint Add(int kind, ByteWriter body)
        {
            uint at = (uint)records.Length;
            byte[] bytes = body.ToArray();
            int padding = (4 - (bytes.Length + 4) % 4) % 4;
            records.U16(bytes.Length + 2 + padding);
            records.U16(kind);
            records.Bytes(bytes);
            for (int i = 0; i < padding; i++) records.U8(0);
            return at;
        }

        /// <summary>A procedure: found through the place in the module's stream that its record is.</summary>
        public void Procedure(string name, uint moduleOffset, uint codeOffset)
        {
            var reference = new ByteWriter();
            reference.U32(0);
            reference.U32(moduleOffset);
            reference.U16(1);                   // the first module (they're counted from 1 here)
            reference.Text(name);
            globals.Add((name, Add(S_PROCREF, reference)));

            var pub = new ByteWriter();
            pub.U32(2);                         // it's code
            pub.U32(codeOffset);
            pub.U16(1);
            pub.Text(name);
            publics.Add((name, Add(S_PUB32, pub), codeOffset));
        }

        public void Variable(string name, uint type, ushort section, uint offset)
        {
            var w = new ByteWriter();
            w.U32(type);
            w.U32(offset);
            w.U16(section);
            w.Text(name);
            globals.Add((name, Add(S_GDATA32, w)));
        }

        public void Type(string name, uint type)
        {
            var w = new ByteWriter();
            w.U32(type);
            w.Text(name);
            globals.Add((name, Add(S_UDT, w)));
        }

        public byte[] Records() => records.ToArray();

        public byte[] Globals() => HashTable(globals);

        public byte[] Publics()
        {
            byte[] hash = HashTable(publics.Select(p => (p.Name, p.At)).ToList());
            var w = new ByteWriter();
            w.U32((uint)hash.Length);
            w.U32((uint)publics.Count * 4);
            w.U32(0);                           // no thunks
            w.U32(0);
            w.U16(0);
            w.U16(0);
            w.U32(0);
            w.U32(0);
            w.Bytes(hash);
            foreach (var p in publics.OrderBy(p => p.Offset)) w.U32(p.At);
            return w.ToArray();
        }

        /// <summary>Records by the hash of their names, shortest name first within a bucket.</summary>
        static byte[] HashTable(List<(string Name, uint At)> names)
        {
            var buckets = names
                .GroupBy(n => (int)(Pdb.HashV1(n.Name) % Buckets))
                .OrderBy(g => g.Key)
                .Select(g => (Bucket: g.Key, Entries: g.OrderBy(n => n.Name.Length).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();

            var entries = new ByteWriter();
            var bits = new uint[(Buckets + 32) / 32];
            var starts = new ByteWriter();
            int count = 0;
            foreach (var (bucket, list) in buckets)
            {
                bits[bucket / 32] |= 1u << (bucket % 32);
                starts.U32((uint)count * 12);   // as far on as they are in memory, at 12 bytes each
                foreach (var (_, at) in list)
                {
                    entries.U32(at + 1);
                    entries.U32(1);
                    count++;
                }
            }

            var w = new ByteWriter();
            w.U32(0xFFFFFFFF);
            w.U32(0xF12F091A);                  // the version
            w.U32((uint)entries.Length);
            w.U32((uint)(bits.Length * 4 + starts.Length));
            w.Bytes(entries.ToArray());
            foreach (uint word in bits) w.U32(word);
            w.Bytes(starts.ToArray());
            return w.ToArray();
        }
    }

    /// <summary>Stream 2: CodeView's descriptions of PHI's types.</summary>
    sealed class TypeTable
    {
        const uint First = 0x1000;
        const int LF_POINTER = 0x1002, LF_FIELDLIST = 0x1203, LF_ARRAY = 0x1503, LF_STRUCTURE = 0x1505, LF_MEMBER = 0x150D;

        const uint T_VOID = 0x03, T_CHAR = 0x70, T_UINT1 = 0x69, T_INT1 = 0x68, T_INT2 = 0x72, T_UINT2 = 0x73, T_INT4 = 0x74, T_UINT4 = 0x75;
        const uint T_INT8 = 0x76, T_UINT8 = 0x77, T_REAL32 = 0x40, T_REAL64 = 0x41, T_BOOL8 = 0x30, T_BOOL32 = 0x32;

        const uint HashBuckets = 0x3FFFF;

        readonly bool bits64;
        readonly ByteWriter records = new();
        readonly List<(int At, uint Hash)> hashed = new();
        uint nextIndex = First;
        readonly Dictionary<StructSymbol, uint> structs = new();
        readonly Dictionary<StructSymbol, uint> forwards = new();
        readonly HashSet<StructSymbol> building = new();
        readonly Dictionary<uint, uint> pointers = new();
        readonly Dictionary<(uint, int), uint> arrays = new();

        public TypeTable(bool bits64) => this.bits64 = bits64;

        int PointerSize => bits64 ? 8 : 4;

        /// <summary>A pointer to a simple type is a number; a pointer to anything else is a record.</summary>
        uint PointerTo(uint target)
        {
            if (target < First) return target | (bits64 ? 0x600u : 0x400u);
            if (pointers.TryGetValue(target, out uint known)) return known;
            var w = new ByteWriter();
            w.U32(target);
            w.U32((uint)((bits64 ? 0x0C : 0x0A) | (PointerSize << 13)));
            return pointers[target] = Record(LF_POINTER, w);
        }

        uint ArrayOf(uint element, int bytes)
        {
            if (arrays.TryGetValue((element, bytes), out uint known)) return known;
            var w = new ByteWriter();
            w.U32(element);
            w.U32(bits64 ? T_UINT8 : T_UINT4);
            Number(w, bytes);
            w.Text("");
            return arrays[(element, bytes)] = Record(LF_ARRAY, w);
        }

        static uint Simple(PhiType t) => t.Kind switch
        {
            TypeKind.I8 => T_INT1,
            TypeKind.U8 => T_UINT1,
            TypeKind.Chr => T_CHAR,
            TypeKind.I16 => T_INT2,
            TypeKind.U16 => T_UINT2,
            TypeKind.I32 => T_INT4,
            TypeKind.U32 => T_UINT4,
            TypeKind.I64 => T_INT8,
            TypeKind.U64 => T_UINT8,
            TypeKind.F32 => T_REAL32,
            TypeKind.F64 => T_REAL64,
            TypeKind.Bool => t.ElementSize == 1 ? T_BOOL8 : T_BOOL32,
            TypeKind.Enum => t.ElementSize == 1 ? T_UINT1 : t.ElementSize == 2 ? T_UINT2 : T_INT4,
            TypeKind.Lock => T_UINT4,
            _ => T_VOID,
        };

        /// <summary>A variable's type, given how PHI keeps it there.</summary>
        public uint Of(DebugVariable v)
        {
            if (v.Type.Kind == TypeKind.Str && !v.Type.IsArray)
                return v.Grows || v.TextRef || v.Capacity <= 0 ? PointerTo(T_CHAR) : ArrayOf(T_CHAR, v.Capacity);
            return Of(v.Type);
        }

        public uint Of(PhiType t)
        {
            if (t.Open) return PointerTo(Of(t.Element));     // a growing array starts with where its elements are
            if (t.IsArray)
            {
                if (t.TextTable) return ArrayOf(PointerTo(T_CHAR), t.Length * PointerSize);
                uint element = Of(t.Element);
                return ArrayOf(element, t.ElementSize * t.Length);
            }
            switch (t.Kind)
            {
                case TypeKind.Str:
                    return t.IsInlineText ? ArrayOf(T_CHAR, t.Room) : PointerTo(T_CHAR);
                case TypeKind.Pointer:
                    // ptr<u8> is text as often as not: a debugger shows what it points at
                    return t.Pointee is { Kind: TypeKind.U8, IsArray: false } ? PointerTo(T_CHAR) : PointerTo(t.Pointee != null ? Of(t.Pointee) : T_VOID);
                case TypeKind.Method:
                    return PointerTo(T_VOID);
                case TypeKind.Object:
                    return t.Struct != null ? PointerTo(StructOf(t.Struct)) : PointerTo(T_VOID);
                case TypeKind.Struct:
                    return t.Struct != null ? StructOf(t.Struct) : T_VOID;
                default:
                    return Simple(t);
            }
        }

        /// <summary>Each struct by its full description, for a debugger to find by its name.</summary>
        public IEnumerable<(string Name, uint Type)> Structs => structs.Select(s => (s.Key.Name, s.Value));

        uint StructOf(StructSymbol s)
        {
            if (structs.TryGetValue(s, out uint known)) return known;
            if (!building.Add(s))
            {
                // it points back at itself: for now, it's a name to be looked up
                if (forwards.TryGetValue(s, out uint forward)) return forward;
                var f = new ByteWriter();
                f.U16(0);
                f.U16(0x0080);                  // a forward reference
                f.U32(0);
                f.U32(0);
                f.U32(0);
                Number(f, 0);
                f.Text(s.Name);
                return forwards[s] = Record(LF_STRUCTURE, f);
            }

            var fields = new ByteWriter();
            int count = 0;
            foreach (FieldSymbol field in s.Fields)
            {
                uint type = Of(field.Type);
                fields.U16(LF_MEMBER);
                fields.U16(3);                  // public
                fields.U32(type);
                Number(fields, field.Offset);
                fields.Text(field.Name);
                while (fields.Length % 4 != 0) fields.U8(0xF0 + (4 - fields.Length % 4));
                count++;
            }
            uint list = Record(LF_FIELDLIST, fields);

            var w = new ByteWriter();
            w.U16(count);
            w.U16(0);
            w.U32(list);
            w.U32(0);
            w.U32(0);
            Number(w, s.Size);
            w.Text(s.Name);
            building.Remove(s);
            return structs[s] = Record(LF_STRUCTURE, w);
        }

        /// <summary>A size or offset: as it is if it's small, or after what says how it's kept.</summary>
        static void Number(ByteWriter w, int value)
        {
            if (value < 0x8000) w.U16(value);
            else
            {
                w.U16(0x8004);
                w.U32((uint)value);
            }
        }

        uint Record(int kind, ByteWriter body)
        {
            byte[] bytes = body.ToArray();
            int padding = (4 - (bytes.Length + 4) % 4) % 4;
            var record = new ByteWriter();
            record.U16(bytes.Length + 2 + padding);
            record.U16(kind);
            record.Bytes(bytes);
            for (int i = padding; i > 0; i--) record.U8(0xF0 + i);

            // a struct is found by its name (a forward one is what looks; it isn't looked for)
            bool complete = kind == LF_STRUCTURE && (BitConverter.ToUInt16(bytes, 2) & 0x80) == 0;
            uint hash = complete ? Pdb.HashV1(StructName(bytes)) : Crc(record.ToArray());
            hashed.Add((records.Length, hash % HashBuckets));
            records.Bytes(record.ToArray());
            return nextIndex++;
        }

        /// <summary>The name that ends a structure's record, past its size (2 bytes here, as they're small).</summary>
        static string StructName(byte[] body)
        {
            int at = 16;
            if (BitConverter.ToUInt16(body, at) == 0x8004) at += 6;
            else at += 2;
            int end = Array.IndexOf(body, (byte)0, at);
            return Encoding.UTF8.GetString(body, at, end - at);
        }

        /// <summary>The CRC-32 of the bytes, as a .pdb does it: starting from 0, and not inverted at the end.</summary>
        static uint Crc(byte[] bytes)
        {
            uint crc = 0;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc;
        }

        /// <summary>The hash of each record, then where to find a record quickly (the first of every 8 KB).</summary>
        public byte[] BuildHashes()
        {
            var w = new ByteWriter();
            foreach (var (_, hash) in hashed) w.U32(hash);
            uint index = First;
            int next = 0;
            foreach (var (at, _) in hashed)
            {
                if (at >= next)
                {
                    w.U32(index);
                    w.U32((uint)at);
                    next = at + 8192;
                }
                index++;
            }
            return w.ToArray();
        }

        public byte[] BuildStream(int hashStream)
        {
            int hashes = hashed.Count * 4;
            int offsets = BuildHashes().Length - hashes;
            return Stream(records.ToArray(), nextIndex, hashStream, hashes, offsets);
        }

        public static byte[] EmptyStream() => Stream(Array.Empty<byte>(), First, 0xFFFF, 0, 0);

        static byte[] Stream(byte[] records, uint end, int hashStream, int hashes, int offsets)
        {
            var w = new ByteWriter();
            w.U32(20040203);                    // the version
            w.U32(56);                          // this header's size
            w.U32(First);
            w.U32(end);
            w.U32((uint)records.Length);
            w.U16(hashStream);
            w.U16(0xFFFF);                      // no second stream of hashes
            w.U32(4);
            w.U32(HashBuckets);
            w.U32(0);                           // the hashes: where, and how long
            w.U32((uint)hashes);
            w.U32((uint)hashes);                // where to find a record: where, and how long
            w.U32((uint)offsets);
            w.U32(0);                           // no adjustments
            w.U32(0);
            w.Bytes(records);
            return w.ToArray();
        }
    }

    /// <summary>The module's stream: its procedures, their variables, and which code each line became.</summary>
    sealed class ModuleWriter
    {
        const int S_END = 0x0006, S_FRAMEPROC = 0x1012, S_OBJNAME = 0x1101, S_LDATA32 = 0x110C, S_GPROC32 = 0x1110, S_REGREL32 = 0x1111, S_COMPILE3 = 0x113C;
        const int DEBUG_S_LINES = 0xF2, DEBUG_S_FILECHKSMS = 0xF4;
        const int CV_REG_EBP = 22, CV_AMD64_RBP = 334;

        readonly DebugInfo info;
        readonly Dictionary<string, uint> labels;
        readonly uint codeStart;
        readonly uint dataStart;
        readonly bool bits64;
        readonly string outDir;

        public TypeTable Types { get; }
        public SymbolIndex Index { get; } = new();
        public NameTable Names { get; } = new();
        public List<string> Files { get; } = new();
        public int SymbolBytes { get; private set; }
        public int LineBytes { get; private set; }

        readonly Dictionary<Phi.Compiler.SourceFile, uint> fileEntries = new();
        readonly ByteWriter fileTable = new();

        public ModuleWriter(DebugInfo info, Dictionary<string, uint> labels, uint codeStart, uint dataStart, bool bits64, string outDir)
        {
            this.info = info;
            this.labels = labels;
            this.codeStart = codeStart;
            this.dataStart = dataStart;
            this.bits64 = bits64;
            this.outDir = outDir;
            Types = new TypeTable(bits64);
        }

        /// <summary>A label's place in the code's section.</summary>
        uint Offset(string label) => labels[label] - codeStart;

        /// <summary>A variable's section, and its place in it.</summary>
        (ushort Section, uint Offset) DataPlace(string label) =>
            labels[label] >= dataStart ? ((ushort)2, labels[label] - dataStart) : ((ushort)1, labels[label] - codeStart);

        public byte[] Build()
        {
            var symbols = new ByteWriter();
            symbols.U32(4);                     // CodeView's newest kind

            var name = new ByteWriter();
            name.U32(0);
            name.Text("phi");
            Symbol(symbols, S_OBJNAME, name);

            var compile = new ByteWriter();
            compile.U32(1);                     // the language: as C++, for how a debugger reads names
            compile.U16(bits64 ? 0xD0 : 0x07);  // the CPU
            for (int i = 0; i < 8; i++) compile.U16(0);
            compile.Text("phi");
            Symbol(symbols, S_COMPILE3, compile);

            var lines = new ByteWriter();
            var methods = info.Methods.OrderBy(m => Offset(m.Label)).ToList();
            foreach (DebugMethod m in methods) AddMethod(symbols, lines, m);

            foreach (var (structName, type) in Types.Structs) Index.Type(structName, type);

            var checksums = new ByteWriter();
            checksums.U32(DEBUG_S_FILECHKSMS);
            checksums.U32((uint)fileTable.Length);
            checksums.Bytes(fileTable.ToArray());
            checksums.Align(4);

            var stream = new ByteWriter();
            byte[] symbolBytes = symbols.ToArray();
            foreach (var (at, value) in patches) BitConverter.GetBytes(value).CopyTo(symbolBytes, at);
            stream.Bytes(symbolBytes);
            SymbolBytes = stream.Length;
            int beforeLines = stream.Length;
            stream.Bytes(checksums.ToArray());
            stream.Bytes(lines.ToArray());
            LineBytes = stream.Length - beforeLines;
            stream.U32(0);                      // no global references
            return stream.ToArray();
        }

        void AddMethod(ByteWriter symbols, ByteWriter lines, DebugMethod m)
        {
            uint start = Offset(m.Label);
            uint end = Offset(m.EndLabel);
            var marks = info.Lines.Where(l => Offset(l.Label) >= start && Offset(l.Label) < end).ToList();

            // past its start: where it's set up its frame, which is its second line's
            uint bodyAt = marks.Count > 1 ? Offset(marks[1].Label) - start : 0;

            int procAt = symbols.Length;
            var proc = new ByteWriter();
            proc.U32(0);
            int endPatch = proc.Length;
            proc.U32(0);                        // where its S_END is: below
            proc.U32(0);
            proc.U32(end - start);
            proc.U32(bodyAt);
            proc.U32(end - start - 1);
            proc.U32(0);                        // no type of its own
            proc.U32(start);
            proc.U16(1);
            proc.U8(0);
            proc.Text(m.Name);
            Symbol(symbols, S_GPROC32, proc);
            Index.Procedure(m.Name, (uint)procAt, start);

            var frame = new ByteWriter();
            frame.U32(0);
            frame.U32(0);
            frame.U32(0);
            frame.U32(0);
            frame.U32(0);
            frame.U16(0);
            frame.U32((2u << 14) | (2u << 16)); // locals and parameters by the frame pointer
            Symbol(symbols, S_FRAMEPROC, frame);

            foreach (DebugVariable v in m.Variables) AddVariable(symbols, v);

            int endAt = symbols.Length;
            Symbol(symbols, S_END, new ByteWriter());
            patches.Add((procAt + 4 + endPatch, (uint)endAt));
            AddLines(lines, marks, start, end);
        }

        void AddVariable(ByteWriter symbols, DebugVariable v)
        {
            string place = v.Place;
            uint type = Types.Of(v);
            if (place.StartsWith("ebp"))
            {
                int offset = int.Parse(place[3..].Replace(" ", ""));
                var w = new ByteWriter();
                w.U32((uint)offset);
                w.U32(type);
                w.U16(bits64 ? CV_AMD64_RBP : CV_REG_EBP);
                w.Text(v.Name);
                Symbol(symbols, S_REGREL32, w);
            }
            else if (labels.ContainsKey(place))
            {
                var (section, offset) = DataPlace(place);
                Index.Variable(v.Name, type, section, offset);
                if (v.Library) return;
                var w = new ByteWriter();
                w.U32(type);
                w.U32(offset);
                w.U16(section);
                w.Text(v.Name);
                Symbol(symbols, S_LDATA32, w);
            }
        }

        /// <summary>The method's lines, in a block for each run of them from one file.</summary>
        void AddLines(ByteWriter lines, List<DebugLine> marks, uint start, uint end)
        {
            if (marks.Count == 0) return;
            var body = new ByteWriter();
            body.U32(start);
            body.U16(1);
            body.U16(0);
            body.U32(end - start);
            int i = 0;
            while (i < marks.Count)
            {
                int j = i;
                while (j < marks.Count && marks[j].File == marks[i].File) j++;
                body.U32(FileEntry(marks[i].File));
                body.U32((uint)(j - i));
                body.U32((uint)(12 + (j - i) * 8));
                for (; i < j; i++)
                {
                    body.U32(Offset(marks[i].Label) - start);
                    body.U32((uint)marks[i].Line | 0x80000000); // a statement
                }
            }
            lines.U32(DEBUG_S_LINES);
            lines.U32((uint)body.Length);
            lines.Bytes(body.ToArray());
            lines.Align(4);
        }

        /// <summary>
        /// A source file's place in the table of them. A library's own modules are inside phi
        /// itself, so a copy goes next to the .pdb, for a debugger to show.
        /// </summary>
        uint FileEntry(Phi.Compiler.SourceFile file)
        {
            if (fileEntries.TryGetValue(file, out uint known)) return known;
            string path = Path.GetFullPath(file.Path);
            if (!File.Exists(path))
            {
                path = Path.GetFullPath(Path.Combine(outDir, "source", file.Path.TrimStart('/', '\\')));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Text);
            }
            uint entry = (uint)fileTable.Length;
            fileTable.U32(Names.Add(path));
            fileTable.U8(0);                    // no checksum
            fileTable.U8(0);
            fileTable.Align(4);
            Files.Add(path);
            return fileEntries[file] = entry;
        }

        readonly List<(int At, uint Value)> patches = new();

        static void Symbol(ByteWriter symbols, int kind, ByteWriter body)
        {
            byte[] bytes = body.ToArray();
            int padding = (4 - (bytes.Length + 4) % 4) % 4;
            symbols.U16(bytes.Length + 2 + padding);
            symbols.U16(kind);
            symbols.Bytes(bytes);
            for (int i = 0; i < padding; i++) symbols.U8(0);
        }
    }
}

using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// Makes a FAT16 partition from a folder on the host (the program's rootfs), for the disk
    /// image of a 32-bit kernel: 16 MB unless asked for more (phi build --disk-mb). Names must be
    /// 8.3 (up to 8 characters, a dot, up to 3), as PHI's FAT16 driver doesn't read long names.
    /// </summary>
    public static class FatImage
    {
        public const int SectorSize = 512;

        /// <summary>Where the partition starts on the disk (1 MB in), and its size unless asked for another.</summary>
        public const int PartitionStart = 2048;
        public const int DefaultMegabytes = 16;
        public const int PartitionSectors = DefaultMegabytes * 2048;

        /// <summary>
        /// The sizes a partition can be: from 16 MB (FAT16 needs 4,085 clusters, and smaller
        /// would be FAT12's) to 2047 MB (65,524 clusters of 32 KB, FAT16's most).
        /// </summary>
        public const int MinMegabytes = 16, MaxMegabytes = 2047;

        public const int ReservedSectors = 1;     // just the partition's boot sector
        public const int FatCount = 2;
        public const int RootEntries = 512;

        const int EntrySize = 32;
        const int MostClusters = 65524;

        const ushort EndOfChain = 0xFFFF;
        const byte AttrDirectory = 0x10, AttrArchive = 0x20, AttrVolumeLabel = 0x08;

        /// <summary>
        /// A FAT16 partition's shape, as its boot sector says it: phi's own for a size (For), or
        /// what a kept disk has (Read), which may be another size, or made by another tool.
        /// </summary>
        public sealed record Layout(int Sectors, int SectorsPerCluster, int Reserved, int Fats, int SectorsPerFat, int Roots)
        {
            public int ClusterBytes => SectorsPerCluster * SectorSize;
            public int FatStart => Reserved;
            public int RootStart => FatStart + Fats * SectorsPerFat;
            public int RootSectors => (Roots * EntrySize + SectorSize - 1) / SectorSize;
            public int DataStart => RootStart + RootSectors;
            public int ClusterCount => (Sectors - DataStart) / SectorsPerCluster;
            public int Megabytes => (int)((long)Sectors * SectorSize / (1024 * 1024));

            /// <summary>
            /// phi's own for a partition of megabytes: 2 KB clusters (and twice, four times ... as
            /// big where FAT16's 65,524 clusters would be too few), 512 root entries, two FATs.
            /// </summary>
            public static Layout For(int megabytes)
            {
                int sectors = megabytes * 2048;
                int rootSectors = RootEntries * EntrySize / SectorSize;
                int perCluster = 4;
                while ((sectors - ReservedSectors - rootSectors) / perCluster > MostClusters) perCluster *= 2;
                // the FATs' size from the clusters there would be without them (a few more than there are)
                int estimate = (sectors - ReservedSectors - rootSectors) / perCluster;
                int perFat = ((estimate + 2) * 2 + SectorSize - 1) / SectorSize;
                return new Layout(sectors, perCluster, ReservedSectors, FatCount, perFat, RootEntries);
            }

            /// <summary>What partition p's boot sector says, or null (why says why) if it isn't FAT16 as PHI reads it.</summary>
            public static Layout? Read(byte[] p, out string why)
            {
                why = "";
                if (p.Length < SectorSize || p[510] != 0x55 || p[511] != 0xAA) { why = "it has no FAT16 boot sector"; return null; }
                int bytesPerSector = BitConverter.ToUInt16(p, 0x0B);
                int sectors = BitConverter.ToUInt16(p, 0x13);
                if (sectors == 0) sectors = (int)BitConverter.ToUInt32(p, 0x20);
                var layout = new Layout(sectors, p[0x0D], BitConverter.ToUInt16(p, 0x0E), p[0x10], BitConverter.ToUInt16(p, 0x16), BitConverter.ToUInt16(p, 0x11));
                if (bytesPerSector != SectorSize || layout.SectorsPerCluster == 0 || layout.Fats == 0 || layout.SectorsPerFat == 0
                    || (long)sectors * SectorSize > p.Length || layout.ClusterCount < 4085 || layout.ClusterCount > MostClusters
                    || layout.SectorsPerFat * SectorSize < (layout.ClusterCount + 2) * 2)
                {
                    why = "its boot sector doesn't describe a FAT16 partition that fits it";
                    return null;
                }
                return layout;
            }
        }

        sealed class State
        {
            public readonly Layout L;
            public readonly byte[] Partition;
            public readonly ushort[] Fat;
            public int NextCluster = 2;
            public readonly List<string> Errors = new();
            public Func<string, byte[]?>? CompileProgram;

            public State(Layout layout)
            {
                L = layout;
                Partition = new byte[layout.Sectors * SectorSize];
                Fat = new ushort[layout.ClusterCount + 2];
            }
        }

        /// <param name="rootfs">The folder to copy in, or null for an empty file system.</param>
        /// <param name="compileProgram">Turns a .phi file into a user program, stored as NAME.BIN.</param>
        /// <param name="megabytes">The partition's size: MinMegabytes to MaxMegabytes.</param>
        public static byte[] Build(string? rootfs, List<string> errors, Func<string, byte[]?>? compileProgram = null, int megabytes = DefaultMegabytes)
        {
            State state = NewState(Layout.For(megabytes));
            state.CompileProgram = compileProgram;

            var root = new List<byte[]> { VolumeLabel() };
            if (rootfs != null) root.AddRange(DirectoryEntries(state, rootfs, parentCluster: 0));
            return Finish(state, root, rootfs ?? "the root folder", errors);
        }

        static State NewState(Layout layout)
        {
            var state = new State(layout);
            state.Fat[0] = 0xFFF8; // media descriptor: fixed disk
            state.Fat[1] = EndOfChain;
            WriteBootSector(state.Partition, layout);
            return state;
        }

        /// <summary>Writes the root directory and both FATs.</summary>
        static byte[] Finish(State state, List<byte[]> root, string rootName, List<string> errors)
        {
            Layout l = state.L;
            if (root.Count > l.Roots)
                state.Errors.Add($"{rootName} has {root.Count - 1} entries, but the root directory holds at most {l.Roots - 1}");
            else
                for (int i = 0; i < root.Count; i++)
                    root[i].CopyTo(state.Partition, l.RootStart * SectorSize + i * EntrySize);

            for (int copy = 0; copy < l.Fats; copy++)
                for (int i = 0; i < state.Fat.Length; i++)
                    BitConverter.GetBytes(state.Fat[i]).CopyTo(state.Partition, (l.FatStart + copy * l.SectorsPerFat) * SectorSize + i * 2);

            errors.AddRange(state.Errors);
            return state.Partition;
        }

        // ================================================================ keeping a disk

        /// <summary>A file or folder read back from a partition: its 32-byte entry, and its data or children.</summary>
        sealed class Item
        {
            public byte[] Entry = Array.Empty<byte>();
            public byte[] Data = Array.Empty<byte>();
            public List<Item>? Children;
            public string Name => Encoding.ASCII.GetString(Entry, 0, 11);
            public bool IsProgram => Children == null && Name.EndsWith("BIN");
        }

        /// <summary>
        /// The kept partition with every program (NAME.BIN) taken from the new one: changed programs
        /// are replaced and new ones added, and everything else the kernel saved stays as it was.
        /// </summary>
        /// <param name="refreshed">gets the path of each program that was replaced or added</param>
        /// <remarks>
        /// The kept partition keeps its own size and shape (one made bigger by phi build --disk-mb,
        /// or by another tool, is read as its boot sector says), whatever size the new one is.
        /// </remarks>
        public static byte[] RefreshPrograms(byte[] kept, byte[] built, List<string> refreshed, List<string> errors)
        {
            Layout? keptLayout = Layout.Read(kept, out string why);
            Layout? builtLayout = Layout.Read(built, out string builtWhy);
            if (keptLayout == null || builtLayout == null)
            {
                errors.Add(keptLayout == null ? $"the kept disk can't be brought up to date: {why}" : $"the new disk: {builtWhy}");
                return kept;
            }
            List<Item> root = ReadDirectory(kept, keptLayout, RootDirectory(kept, keptLayout));
            Merge(root, ReadDirectory(built, builtLayout, RootDirectory(built, builtLayout)), "", refreshed);

            State state = NewState(keptLayout);
            // (its own boot sector, as another tool may have written it: only phi's numbers matter to PHI)
            kept.AsSpan(0, SectorSize).CopyTo(state.Partition);
            List<byte[]> entries = WriteItems(state, root, parentCluster: 0);
            return Finish(state, entries, "the kept disk", errors);
        }

        static void Merge(List<Item> kept, List<Item> built, string folder, List<string> refreshed)
        {
            foreach (Item item in built)
            {
                Item? old = kept.Find(k => k.Name == item.Name && (k.Children == null) == (item.Children == null));
                string path = folder + ShortToPath(item.Name);
                if (item.Children != null)
                {
                    // a new folder comes across only if it has programs in it
                    var children = old?.Children ?? new List<Item>();
                    int before = refreshed.Count;
                    Merge(children, item.Children, path + "/", refreshed);
                    if (old == null && refreshed.Count > before) kept.Add(new Item { Entry = item.Entry, Children = children });
                }
                else if (item.IsProgram && (old == null || !old.Data.AsSpan().SequenceEqual(item.Data)))
                {
                    if (old != null) kept[kept.IndexOf(old)] = item;
                    else kept.Add(item);
                    refreshed.Add(path);
                }
            }
        }

        static string ShortToPath(string name11)
        {
            string stem = name11[..8].TrimEnd(), ext = name11[8..].TrimEnd();
            return ext.Length == 0 ? stem : stem + "." + ext;
        }

        static byte[] RootDirectory(byte[] p, Layout l) => p.AsSpan(l.RootStart * SectorSize, l.Roots * EntrySize).ToArray();

        /// <summary>The items in a directory's entries (dir), whose data is in the partition p.</summary>
        static List<Item> ReadDirectory(byte[] p, Layout l, byte[] dir)
        {
            var items = new List<Item>();
            for (int at = 0; at + EntrySize <= dir.Length; at += EntrySize)
            {
                if (dir[at] == 0x00) break;                     // the end of the directory
                byte attributes = dir[at + 11];
                if (dir[at] == 0xE5 || dir[at] == (byte)'.' || attributes == 0x0F) continue;  // deleted, . and .., long names

                var item = new Item { Entry = dir.AsSpan(at, EntrySize).ToArray() };
                int first = BitConverter.ToUInt16(dir, at + 26);
                if ((attributes & AttrDirectory) != 0)
                {
                    item.Children = ReadDirectory(p, l, ReadChain(p, l, first, int.MaxValue));
                }
                else if ((attributes & AttrVolumeLabel) == 0)
                {
                    item.Data = ReadChain(p, l, first, (int)BitConverter.ToUInt32(dir, at + 28));
                }
                items.Add(item);
            }
            return items;
        }

        /// <summary>The data of a cluster chain, up to size bytes. p is the whole partition.</summary>
        static byte[] ReadChain(byte[] p, Layout l, int cluster, int size)
        {
            var data = new List<byte>();
            var seen = new HashSet<int>();
            while (cluster >= 2 && cluster < l.ClusterCount + 2 && data.Count < size && seen.Add(cluster))
            {
                long sector = l.DataStart + (long)(cluster - 2) * l.SectorsPerCluster;
                data.AddRange(p.AsSpan((int)(sector * SectorSize), Math.Min(l.ClusterBytes, size - data.Count)).ToArray());
                cluster = BitConverter.ToUInt16(p, l.FatStart * SectorSize + cluster * 2);
            }
            return data.ToArray();
        }

        /// <summary>Entries for items read back from a partition, writing their data and subfolders.</summary>
        static List<byte[]> WriteItems(State state, List<Item> items, int parentCluster)
        {
            var entries = new List<byte[]>();
            foreach (Item item in items)
            {
                byte[] entry = (byte[])item.Entry.Clone();
                int first = 0;
                if (item.Children != null)
                {
                    int clusters = Math.Max(1, ((item.Children.Count + 2) * EntrySize + state.L.ClusterBytes - 1) / state.L.ClusterBytes);
                    first = AllocateChain(state, clusters, ShortToPath(item.Name));
                    if (first == 0) continue;

                    byte[] dot = (byte[])entry.Clone(), dotdot = (byte[])entry.Clone();
                    Encoding.ASCII.GetBytes(".          ").CopyTo(dot, 0);
                    Encoding.ASCII.GetBytes("..         ").CopyTo(dotdot, 0);
                    BitConverter.GetBytes((ushort)first).CopyTo(dot, 26);
                    BitConverter.GetBytes((ushort)parentCluster).CopyTo(dotdot, 26);

                    var children = new List<byte[]> { dot, dotdot };
                    children.AddRange(WriteItems(state, item.Children, first));
                    WriteChain(state, first, children.SelectMany(c => c).ToArray());
                }
                else if (item.Data.Length > 0)
                {
                    first = AllocateChain(state, (item.Data.Length + state.L.ClusterBytes - 1) / state.L.ClusterBytes, ShortToPath(item.Name));
                    if (first == 0) continue;
                    WriteChain(state, first, item.Data);
                }
                BitConverter.GetBytes((ushort)first).CopyTo(entry, 26);
                entries.Add(entry);
            }
            return entries;
        }

        /// <summary>
        /// Partition table entry 1 in the boot sector (offset 446), for a partition of sectors:
        /// type 0x04 (FAT16 under 32 MB) or 0x06 (FAT16 from 32 MB), which PHI's driver both reads.
        /// </summary>
        public static void WritePartitionEntry(byte[] image, int sectors = PartitionSectors)
        {
            byte[] entry = new byte[16];
            entry[0] = 0x00;                            // not the boot partition (we boot from the disk itself)
            entry[1] = 0xFE; entry[2] = 0xFF; entry[3] = 0xFF;  // CHS start: "use LBA"
            entry[4] = (byte)(sectors < 65536 ? 0x04 : 0x06);
            entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;  // CHS end
            BitConverter.GetBytes((uint)PartitionStart).CopyTo(entry, 8);
            BitConverter.GetBytes((uint)sectors).CopyTo(entry, 12);
            entry.CopyTo(image, 446);
        }

        /// <summary>Where a disk image's partition starts and its sectors, from its partition table's first entry.</summary>
        public static (int Start, int Sectors) PartitionOf(byte[] image) =>
            ((int)BitConverter.ToUInt32(image, 446 + 8), (int)BitConverter.ToUInt32(image, 446 + 12));

        static void WriteBootSector(byte[] p, Layout l)
        {
            p[0] = 0xEB; p[1] = 0x3C; p[2] = 0x90;      // jump over the parameters (not bootable)
            Encoding.ASCII.GetBytes("PHI     ").CopyTo(p, 3);
            BitConverter.GetBytes((ushort)SectorSize).CopyTo(p, 0x0B);
            p[0x0D] = (byte)l.SectorsPerCluster;
            BitConverter.GetBytes((ushort)l.Reserved).CopyTo(p, 0x0E);
            p[0x10] = (byte)l.Fats;
            BitConverter.GetBytes((ushort)l.Roots).CopyTo(p, 0x11);
            // (the total in the 16-bit field when it fits, else in the 32-bit one)
            BitConverter.GetBytes((ushort)(l.Sectors < 65536 ? l.Sectors : 0)).CopyTo(p, 0x13);
            p[0x15] = 0xF8;                             // fixed disk
            BitConverter.GetBytes((ushort)l.SectorsPerFat).CopyTo(p, 0x16);
            BitConverter.GetBytes((ushort)63).CopyTo(p, 0x18);   // sectors per track (unused)
            BitConverter.GetBytes((ushort)16).CopyTo(p, 0x1A);   // heads (unused)
            BitConverter.GetBytes((uint)PartitionStart).CopyTo(p, 0x1C);
            BitConverter.GetBytes((uint)(l.Sectors < 65536 ? 0 : l.Sectors)).CopyTo(p, 0x20);
            p[0x24] = 0x80;                             // drive number
            p[0x26] = 0x29;                             // extended boot signature
            BitConverter.GetBytes(0x50484921u).CopyTo(p, 0x27);  // volume serial
            Encoding.ASCII.GetBytes("PHI DISK   ").CopyTo(p, 0x2B);
            Encoding.ASCII.GetBytes("FAT16   ").CopyTo(p, 0x36);
            p[510] = 0x55; p[511] = 0xAA;
        }

        static byte[] VolumeLabel()
        {
            byte[] e = new byte[EntrySize];
            Encoding.ASCII.GetBytes("PHI DISK   ").CopyTo(e, 0);
            e[11] = AttrVolumeLabel;
            return e;
        }

        /// <summary>Entries for everything in a folder, writing file data and subfolders as it goes.</summary>
        static List<byte[]> DirectoryEntries(State state, string folder, int parentCluster)
        {
            var entries = new List<byte[]>();
            var names = new HashSet<string>();

            var items = Directory.EnumerateFileSystemEntries(folder)
                .Where(path => !Path.GetFileName(path).StartsWith('.'))   // .gitkeep and friends
                // (a compiled library's code is in the programs that use it: nothing loads the file)
                .Where(path => !path.EndsWith(Phi.Compiler.Libraries.CompiledLibrary.Extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (string item in items)
            {
                // a program's source is compiled; the disk gets NAME.BIN
                bool program = state.CompileProgram != null && item.EndsWith(".phi", StringComparison.OrdinalIgnoreCase) && File.Exists(item);
                string path = program ? Path.ChangeExtension(item, ".bin") : item;

                string? name = ShortName(path, state.Errors);
                if (name == null) continue;
                if (!names.Add(name))
                {
                    state.Errors.Add($"{path}: another file in this folder has the same 8.3 name");
                    continue;
                }

                if (Directory.Exists(path))
                {
                    // the subfolder's entries go in clusters of their own, starting with . and ..
                    int count = Directory.EnumerateFileSystemEntries(path).Count(p => !Path.GetFileName(p).StartsWith('.')) + 2;
                    int clusters = Math.Max(1, (count * EntrySize + state.L.ClusterBytes - 1) / state.L.ClusterBytes);
                    int first = AllocateChain(state, clusters, path);
                    if (first == 0) continue;

                    var children = new List<byte[]>
                    {
                        Entry(".          ", AttrDirectory, first, 0, Directory.GetLastWriteTime(path)),
                        Entry("..         ", AttrDirectory, parentCluster, 0, Directory.GetLastWriteTime(path)),
                    };
                    children.AddRange(DirectoryEntries(state, path, first));

                    byte[] data = children.SelectMany(c => c).ToArray();
                    WriteChain(state, first, data);
                    entries.Add(Entry(name, AttrDirectory, first, 0, Directory.GetLastWriteTime(path)));
                }
                else
                {
                    byte[]? data = program ? state.CompileProgram!(item) : File.ReadAllBytes(path);
                    if (data == null) continue;
                    int first = 0;
                    if (data.Length > 0)
                    {
                        first = AllocateChain(state, (data.Length + state.L.ClusterBytes - 1) / state.L.ClusterBytes, path);
                        if (first == 0) continue;
                        WriteChain(state, first, data);
                    }
                    // (a program compiled from its source is dated when it was built: its NAME.BIN isn't a
                    // file here, and a missing file's time is 1601, which FAT shows as 1980-12-31)
                    entries.Add(Entry(name, AttrArchive, first, (uint)data.Length, program ? DateTime.Now : File.GetLastWriteTime(path)));
                }
            }

            return entries;
        }

        /// <summary>readme.txt becomes "README  TXT"; anything that doesn't fit 8.3 is an error.</summary>
        public static string? ShortName(string path, List<string> errors)
        {
            string file = Path.GetFileName(path).ToUpperInvariant();
            int dot = file.LastIndexOf('.');
            string stem = dot < 0 ? file : file[..dot];
            string ext = dot < 0 ? "" : file[(dot + 1)..];

            bool valid = stem.Length is >= 1 and <= 8 && ext.Length <= 3
                         && (stem + ext).All(c => char.IsAsciiLetterOrDigit(c) || "$%'-_@~`!(){}^#&".Contains(c));
            if (!valid)
            {
                errors.Add($"{path}: FAT16 names must be 8.3 (up to 8 letters or digits, a dot, up to 3), like README.TXT");
                return null;
            }

            return stem.PadRight(8) + ext.PadRight(3);
        }

        static byte[] Entry(string name11, byte attributes, int cluster, uint size, DateTime time)
        {
            byte[] e = new byte[EntrySize];
            Encoding.ASCII.GetBytes(name11).CopyTo(e, 0);
            e[11] = attributes;
            ushort fatTime = (ushort)((time.Hour << 11) | (time.Minute << 5) | (time.Second / 2));
            ushort fatDate = (ushort)((Math.Max(time.Year - 1980, 0) << 9) | (time.Month << 5) | time.Day);
            BitConverter.GetBytes(fatTime).CopyTo(e, 14);    // created
            BitConverter.GetBytes(fatDate).CopyTo(e, 16);
            BitConverter.GetBytes(fatDate).CopyTo(e, 18);    // last accessed
            BitConverter.GetBytes(fatTime).CopyTo(e, 22);    // written
            BitConverter.GetBytes(fatDate).CopyTo(e, 24);
            BitConverter.GetBytes((ushort)cluster).CopyTo(e, 26);
            BitConverter.GetBytes(size).CopyTo(e, 28);
            return e;
        }

        /// <returns>the first cluster, or 0 if the partition is full</returns>
        static int AllocateChain(State state, int clusters, string forPath)
        {
            if (state.NextCluster + clusters > state.L.ClusterCount + 2)
            {
                state.Errors.Add($"{forPath}: the {state.L.Megabytes} MB file system is full" +
                                 (state.L.Megabytes < MaxMegabytes ? " (phi build --disk-mb makes a bigger one)" : ""));
                return 0;
            }

            int first = state.NextCluster;
            for (int i = 0; i < clusters; i++)
                state.Fat[first + i] = i == clusters - 1 ? EndOfChain : (ushort)(first + i + 1);
            state.NextCluster += clusters;
            return first;
        }

        static void WriteChain(State state, int first, byte[] data)
        {
            int cluster = first;
            Layout l = state.L;
            for (int offset = 0; offset < data.Length; offset += l.ClusterBytes)
            {
                long sector = l.DataStart + (long)(cluster - 2) * l.SectorsPerCluster;
                Array.Copy(data, offset, state.Partition, sector * SectorSize, Math.Min(l.ClusterBytes, data.Length - offset));
                cluster = state.Fat[cluster];
            }
        }
    }
}

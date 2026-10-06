using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// Makes a FAT16 partition from a folder on the host (the program's rootfs), for the disk
    /// image of a 32-bit kernel. Names must be 8.3 (up to 8 characters, a dot, up to 3), as
    /// PHI's FAT16 driver doesn't read long names.
    /// </summary>
    public static class FatImage
    {
        public const int SectorSize = 512;

        /// <summary>Where the partition starts on the disk (1 MB in), and its size (16 MB).</summary>
        public const int PartitionStart = 2048;
        public const int PartitionSectors = 32768;

        public const int SectorsPerCluster = 4;   // 2 KB clusters
        public const int ReservedSectors = 1;     // just the partition's boot sector
        public const int FatCount = 2;
        public const int RootEntries = 512;
        public const int SectorsPerFat = 32;      // room for 8192 cluster entries

        const int EntrySize = 32;
        const int ClusterBytes = SectorsPerCluster * SectorSize;
        const int FatStart = ReservedSectors;
        const int RootStart = FatStart + FatCount * SectorsPerFat;
        const int RootSectors = RootEntries * EntrySize / SectorSize;
        const int DataStart = RootStart + RootSectors;
        public const int ClusterCount = (PartitionSectors - DataStart) / SectorsPerCluster;

        const ushort EndOfChain = 0xFFFF;
        const byte AttrDirectory = 0x10, AttrArchive = 0x20, AttrVolumeLabel = 0x08;

        sealed class State
        {
            public readonly byte[] Partition = new byte[PartitionSectors * SectorSize];
            public readonly ushort[] Fat = new ushort[ClusterCount + 2];
            public int NextCluster = 2;
            public readonly List<string> Errors = new();
            public Func<string, byte[]?>? CompileProgram;
        }

        /// <param name="rootfs">The folder to copy in, or null for an empty file system.</param>
        /// <param name="compileProgram">Turns a .phi file into a user program, stored as NAME.BIN.</param>
        public static byte[] Build(string? rootfs, List<string> errors, Func<string, byte[]?>? compileProgram = null)
        {
            var state = new State { CompileProgram = compileProgram };
            state.Fat[0] = 0xFFF8; // media descriptor: fixed disk
            state.Fat[1] = EndOfChain;

            WriteBootSector(state.Partition);

            var root = new List<byte[]> { VolumeLabel() };
            if (rootfs != null) root.AddRange(DirectoryEntries(state, rootfs, parentCluster: 0));

            if (root.Count > RootEntries)
                state.Errors.Add($"{rootfs} has {root.Count - 1} entries, but the root directory holds at most {RootEntries - 1}");
            else
                for (int i = 0; i < root.Count; i++)
                    root[i].CopyTo(state.Partition, RootStart * SectorSize + i * EntrySize);

            for (int copy = 0; copy < FatCount; copy++)
                for (int i = 0; i < state.Fat.Length; i++)
                    BitConverter.GetBytes(state.Fat[i]).CopyTo(state.Partition, (FatStart + copy * SectorsPerFat) * SectorSize + i * 2);

            errors.AddRange(state.Errors);
            return state.Partition;
        }

        /// <summary>Partition table entry 1 in the boot sector (offset 446): type 0x04, FAT16 under 32 MB.</summary>
        public static void WritePartitionEntry(byte[] image)
        {
            byte[] entry = new byte[16];
            entry[0] = 0x00;                            // not the boot partition (we boot from the disk itself)
            entry[1] = 0xFE; entry[2] = 0xFF; entry[3] = 0xFF;  // CHS start: "use LBA"
            entry[4] = 0x04;                            // FAT16, under 32 MB
            entry[5] = 0xFE; entry[6] = 0xFF; entry[7] = 0xFF;  // CHS end
            BitConverter.GetBytes((uint)PartitionStart).CopyTo(entry, 8);
            BitConverter.GetBytes((uint)PartitionSectors).CopyTo(entry, 12);
            entry.CopyTo(image, 446);
        }

        static void WriteBootSector(byte[] p)
        {
            p[0] = 0xEB; p[1] = 0x3C; p[2] = 0x90;      // jump over the parameters (not bootable)
            Encoding.ASCII.GetBytes("PHI     ").CopyTo(p, 3);
            BitConverter.GetBytes((ushort)SectorSize).CopyTo(p, 0x0B);
            p[0x0D] = SectorsPerCluster;
            BitConverter.GetBytes((ushort)ReservedSectors).CopyTo(p, 0x0E);
            p[0x10] = FatCount;
            BitConverter.GetBytes((ushort)RootEntries).CopyTo(p, 0x11);
            BitConverter.GetBytes((ushort)PartitionSectors).CopyTo(p, 0x13);
            p[0x15] = 0xF8;                             // fixed disk
            BitConverter.GetBytes((ushort)SectorsPerFat).CopyTo(p, 0x16);
            BitConverter.GetBytes((ushort)63).CopyTo(p, 0x18);   // sectors per track (unused)
            BitConverter.GetBytes((ushort)16).CopyTo(p, 0x1A);   // heads (unused)
            BitConverter.GetBytes((uint)PartitionStart).CopyTo(p, 0x1C);
            BitConverter.GetBytes((uint)0).CopyTo(p, 0x20);      // total sectors fit in the 16-bit field
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
                    int clusters = Math.Max(1, (count * EntrySize + ClusterBytes - 1) / ClusterBytes);
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
                        first = AllocateChain(state, (data.Length + ClusterBytes - 1) / ClusterBytes, path);
                        if (first == 0) continue;
                        WriteChain(state, first, data);
                    }
                    entries.Add(Entry(name, AttrArchive, first, (uint)data.Length, File.GetLastWriteTime(path)));
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
            if (state.NextCluster + clusters > ClusterCount + 2)
            {
                state.Errors.Add($"{forPath}: the {PartitionSectors * SectorSize / (1024 * 1024)} MB file system is full");
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
            for (int offset = 0; offset < data.Length; offset += ClusterBytes)
            {
                int sector = DataStart + (cluster - 2) * SectorsPerCluster;
                Array.Copy(data, offset, state.Partition, sector * SectorSize, Math.Min(ClusterBytes, data.Length - offset));
                cluster = state.Fat[cluster];
            }
        }
    }
}

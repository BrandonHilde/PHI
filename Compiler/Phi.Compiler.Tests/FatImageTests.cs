using System.Text;
using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>The FAT16 partition phi build makes from a rootfs folder, read back with a tiny reader.</summary>
    public class FatImageTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "phi-fat-" + Guid.NewGuid().ToString("N"));

        public FatImageTests() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, recursive: true);

        static ushort U16(byte[] b, int at) => BitConverter.ToUInt16(b, at);
        static uint U32(byte[] b, int at) => BitConverter.ToUInt32(b, at);

        // (a partition's shape, read from its boot sector here, apart from FatImage's own reading)
        static int RootOffset(byte[] p) => (U16(p, 0x0E) + p[0x10] * U16(p, 0x16)) * 512;
        static int DataOffset(byte[] p) => RootOffset(p) + U16(p, 0x11) * 32;
        static int ClusterBytes(byte[] p) => p[0x0D] * 512;
        static int TotalSectors(byte[] p) => U16(p, 0x13) != 0 ? U16(p, 0x13) : (int)U32(p, 0x20);
        static int Clusters(byte[] p) => (TotalSectors(p) - DataOffset(p) / 512) / p[0x0D];

        /// <summary>(name, attributes, cluster, size) of every entry in a directory's bytes.</summary>
        static List<(string Name, byte Attributes, int Cluster, uint Size)> Entries(byte[] p, int offset, int count)
        {
            var list = new List<(string, byte, int, uint)>();
            for (int i = 0; i < count && p[offset + i * 32] != 0; i++)
            {
                int e = offset + i * 32;
                list.Add((Encoding.ASCII.GetString(p, e, 11), p[e + 11], U16(p, e + 26), U32(p, e + 28)));
            }
            return list;
        }

        static byte[] ReadChain(byte[] p, int cluster, uint size)
        {
            var data = new List<byte>();
            int fat = U16(p, 0x0E) * 512;
            while (cluster >= 2 && cluster < 0xFFF8)
            {
                data.AddRange(p.Skip(DataOffset(p) + (cluster - 2) * ClusterBytes(p)).Take(ClusterBytes(p)));
                cluster = U16(p, fat + cluster * 2);
            }
            return data.Take((int)size).ToArray();
        }

        [Fact]
        public void TheBootSectorDescribesTheLayout()
        {
            var errors = new List<string>();
            byte[] p = FatImage.Build(null, errors);

            Assert.Empty(errors);
            Assert.Equal(FatImage.PartitionSectors * 512, p.Length);
            Assert.Equal(512, U16(p, 0x0B));
            Assert.Equal(4, p[0x0D]);                           // 2 KB clusters
            Assert.Equal(FatImage.RootEntries, U16(p, 0x11));
            Assert.Equal("FAT16   ", Encoding.ASCII.GetString(p, 0x36, 8));
            Assert.Equal(0xAA55, U16(p, 510));
            Assert.InRange(Clusters(p), 4085, 65524);           // the range that makes it FAT16
        }

        [Theory]
        [InlineData(16, 4, 0x04)]
        [InlineData(64, 4, 0x06)]
        [InlineData(160, 8, 0x06)]       // (2 KB clusters would be too many for FAT16)
        public void ABiggerDiskIsFat16AsBig(int megabytes, int perCluster, byte type)
        {
            var errors = new List<string>();
            byte[] p = FatImage.Build(null, errors, null, megabytes);
            Assert.Empty(errors);
            Assert.Equal(megabytes * 1024 * 1024, p.Length);
            Assert.Equal(megabytes * 2048, TotalSectors(p));
            Assert.Equal(perCluster, p[0x0D]);
            Assert.InRange(Clusters(p), 4085, 65524);
            Assert.True(U16(p, 0x16) * 512 >= (Clusters(p) + 2) * 2, "each FAT has room for every cluster");
            Assert.Equal(FatImage.Layout.For(megabytes), FatImage.Layout.Read(p, out _));
            byte[] image = new byte[512];
            FatImage.WritePartitionEntry(image, p.Length / 512);
            Assert.Equal(type, image[446 + 4]);
            Assert.Equal((uint)(megabytes * 2048), U32(image, 446 + 12));
        }

        [Fact]
        public void FilesOnABiggerDiskReadBack()
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "notes.txt"), "on a 160 MB disk");
            byte[] big = new byte[3 * 1024 * 1024];
            new Random(7).NextBytes(big);
            File.WriteAllBytes(Path.Combine(root, "big.dat"), big);
            var errors = new List<string>();
            byte[] p = FatImage.Build(root, errors, null, 160);
            Assert.Empty(errors);
            Assert.Equal("on a 160 MB disk", Read(p, "DOCS/NOTES.TXT"));
            Assert.Equal(Convert.ToHexString(big), Convert.ToHexString(Encoding.Latin1.GetBytes(Read(p, "BIG.DAT", Encoding.Latin1)!)));
        }

        [Fact]
        public void FilesAndFoldersCanBeReadBack()
        {
            File.WriteAllText(Path.Combine(root, "readme.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            byte[] big = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(Path.Combine(root, "docs", "big.bin"), big);
            File.WriteAllText(Path.Combine(root, ".gitkeep"), "");

            var errors = new List<string>();
            byte[] p = FatImage.Build(root, errors);
            Assert.Empty(errors);

            var rootEntries = Entries(p, RootOffset(p), FatImage.RootEntries);
            Assert.Equal(new[] { "PHI DISK   ", "DOCS       ", "README  TXT" }, rootEntries.Select(e => e.Name));

            var readme = rootEntries.Single(e => e.Name == "README  TXT");
            Assert.Equal("hello", Encoding.ASCII.GetString(ReadChain(p, readme.Cluster, readme.Size)));

            var docs = rootEntries.Single(e => e.Name == "DOCS       ");
            Assert.Equal(0x10, docs.Attributes);
            var docsEntries = Entries(p, DataOffset(p) + (docs.Cluster - 2) * ClusterBytes(p), ClusterBytes(p) / 32);
            Assert.Equal(new[] { ".          ", "..         ", "BIG     BIN" }, docsEntries.Select(e => e.Name));
            Assert.Equal(docs.Cluster, docsEntries[0].Cluster);
            Assert.Equal(0, docsEntries[1].Cluster);   // .. of a top-level folder is the root

            var bigEntry = docsEntries[2];
            Assert.Equal(big, ReadChain(p, bigEntry.Cluster, bigEntry.Size));
        }

        [Theory]
        [InlineData("a long name.txt")]
        [InlineData("toolongname.txt")]
        [InlineData("file.text")]
        public void NamesMustBe8Point3(string name)
        {
            File.WriteAllText(Path.Combine(root, name), "x");
            var errors = new List<string>();
            FatImage.Build(root, errors);
            Assert.Contains(errors, e => e.Contains("8.3"));
        }

        [Fact]
        public void ThePartitionTableEntryPointsAtThePartition()
        {
            byte[] image = new byte[512];
            FatImage.WritePartitionEntry(image);
            Assert.Equal(0x04, image[446 + 4]);
            Assert.Equal((uint)FatImage.PartitionStart, U32(image, 446 + 8));
            Assert.Equal((uint)FatImage.PartitionSectors, U32(image, 446 + 12));
        }

        /// <summary>A disk image: a first byte standing for the kernel, then the partition made from folder.</summary>
        static byte[] Image(byte kernel, string folder, int megabytes = FatImage.DefaultMegabytes)
        {
            var errors = new List<string>();
            byte[] partition = FatImage.Build(folder, errors, null, megabytes);
            Assert.Empty(errors);
            var image = new byte[FatImage.PartitionStart * FatImage.SectorSize + partition.Length];
            image[0] = kernel;
            FatImage.WritePartitionEntry(image, partition.Length / FatImage.SectorSize);
            partition.CopyTo(image, FatImage.PartitionStart * FatImage.SectorSize);
            return image;
        }

        /// <summary>The contents of a file in a partition, by its 8.3 names (DOCS/README.TXT), or null.</summary>
        static string? Read(byte[] p, string path, Encoding? encoding = null)
        {
            var entries = Entries(p, RootOffset(p), U16(p, 0x11));
            string[] parts = path.Split('/');
            for (int i = 0; ; i++)
            {
                string[] nameExt = parts[i].Split('.');
                string name11 = nameExt[0].PadRight(8) + (nameExt.Length > 1 ? nameExt[1] : "").PadRight(3);
                int found = entries.FindIndex(e => e.Name == name11);
                if (found < 0) return null;
                var e = entries[found];
                if (i == parts.Length - 1) return (encoding ?? Encoding.ASCII).GetString(ReadChain(p, e.Cluster, e.Size));
                entries = Entries(ReadChain(p, e.Cluster, (uint)ClusterBytes(p)), 0, ClusterBytes(p) / 32);
            }
        }

        [Fact]
        public void AKeptDiskGetsTheNewKernelAndProgramsAndKeepsItsFiles()
        {
            int partition = FatImage.PartitionStart * FatImage.SectorSize;

            // what the kernel left on the kept disk
            string before = Path.Combine(root, "before");
            Directory.CreateDirectory(Path.Combine(before, "apps"));
            File.WriteAllText(Path.Combine(before, "notes.txt"), "saved by the kernel");
            File.WriteAllText(Path.Combine(before, "apps", "paint.bin"), "paint 1");
            File.WriteAllText(Path.Combine(before, "apps", "mine.bin"), "made in the OS");

            // the new build
            string after = Path.Combine(root, "after");
            Directory.CreateDirectory(Path.Combine(after, "apps"));
            Directory.CreateDirectory(Path.Combine(after, "games"));
            File.WriteAllText(Path.Combine(after, "notes.txt"), "the original");
            File.WriteAllText(Path.Combine(after, "extra.txt"), "new data file");
            File.WriteAllText(Path.Combine(after, "apps", "paint.bin"), "paint 2");
            File.WriteAllText(Path.Combine(after, "apps", "calc.bin"), "calc 1");
            File.WriteAllText(Path.Combine(after, "games", "tetris.bin"), "tetris 1");

            string image = Path.Combine(root, "os.img");
            File.WriteAllBytes(image, Image(1, before));
            var refreshed = new List<string>();
            var errors = new List<string>();
            string kept = Builder.KeepDisk(image, out bool fresh, refreshed, errors);
            Assert.True(fresh);

            File.WriteAllBytes(image, Image(2, after));
            Builder.KeepDisk(image, out fresh, refreshed, errors);
            Assert.False(fresh);
            Assert.Empty(errors);

            byte[] disk = File.ReadAllBytes(kept);
            byte[] p = disk[partition..];
            Assert.Equal(2, disk[0]);
            Assert.Equal("saved by the kernel", Read(p, "NOTES.TXT"));
            Assert.Null(Read(p, "EXTRA.TXT"));
            Assert.Equal("paint 2", Read(p, "APPS/PAINT.BIN"));
            Assert.Equal("calc 1", Read(p, "APPS/CALC.BIN"));
            Assert.Equal("made in the OS", Read(p, "APPS/MINE.BIN"));
            Assert.Equal("tetris 1", Read(p, "GAMES/TETRIS.BIN"));
            Assert.Equal(new[] { "APPS/CALC.BIN", "APPS/PAINT.BIN", "GAMES/TETRIS.BIN" }, refreshed);

            // nothing changed: nothing to update
            refreshed.Clear();
            Builder.KeepDisk(image, out fresh, refreshed, errors);
            Assert.Empty(refreshed);
            Assert.Equal("paint 2", Read(File.ReadAllBytes(kept)[partition..], "APPS/PAINT.BIN"));
        }

        [Fact]
        public void AProgramCompiledFromItsSourceIsDatedWhenItWasBuilt()
        {
            // its NAME.BIN isn't a file in the rootfs: it used to be dated as a missing file is
            // (1601), which FAT showed as 1980-12-31
            File.WriteAllText(Path.Combine(root, "hello.phi"), "phi.Hello:Program\n{\n\texit 0;\n}\n");
            var errors = new List<string>();
            byte[] p = FatImage.Build(root, errors, _ => new byte[] { 1, 2, 3 });
            Assert.Empty(errors);
            int at = RootOffset(p);
            while (System.Text.Encoding.ASCII.GetString(p, at, 11) != "HELLO   BIN") at += 32;
            int date = BitConverter.ToUInt16(p, at + 24);
            Assert.Equal(DateTime.Now.Year, 1980 + (date >> 9));
            Assert.Equal(DateTime.Now.Month, (date >> 5) & 15);
        }

        [Fact]
        public void AKeptDiskOfAnotherSizeKeepsItsSizeAndFiles()
        {
            int partition = FatImage.PartitionStart * FatImage.SectorSize;
            // a 64 MB kept disk (phi build --disk-mb 64, or PHI OS's tools/disk.sh) with a user's file
            string before = Path.Combine(root, "before");
            Directory.CreateDirectory(Path.Combine(before, "apps"));
            File.WriteAllText(Path.Combine(before, "notes.txt"), "saved by the kernel");
            File.WriteAllText(Path.Combine(before, "apps", "paint.bin"), "paint 1");
            string image = Path.Combine(root, "os.img");
            File.WriteAllBytes(image, Image(1, before, 64));
            var refreshed = new List<string>();
            var errors = new List<string>();
            string kept = Builder.KeepDisk(image, out bool fresh, refreshed, errors);
            Assert.True(fresh);

            // the new build is phi's 16 MB
            string after = Path.Combine(root, "after");
            Directory.CreateDirectory(Path.Combine(after, "apps"));
            File.WriteAllText(Path.Combine(after, "apps", "paint.bin"), "paint 2");
            File.WriteAllBytes(image, Image(2, after));
            Builder.KeepDisk(image, out fresh, refreshed, errors);
            Assert.False(fresh);
            Assert.Empty(errors);

            byte[] disk = File.ReadAllBytes(kept);
            Assert.Equal(partition + 64 * 1024 * 1024, disk.Length);
            Assert.Equal(2, disk[0]);                                        // the new kernel
            Assert.Equal((uint)(64 * 2048), U32(disk, 446 + 12));            // its own partition entry
            byte[] p = disk[partition..];
            Assert.Equal(64 * 2048, TotalSectors(p));
            Assert.Equal("saved by the kernel", Read(p, "NOTES.TXT"));
            Assert.Equal("paint 2", Read(p, "APPS/PAINT.BIN"));
        }

        [Fact]
        public void AMistakeInCodeSeveralProgramsUseIsReportedOnce()
        {
            File.WriteAllText(Path.Combine(root, "broken.phi"), "phi.Broken:Library\n{\n\tint x: nothing;\n}\n");
            File.WriteAllText(Path.Combine(root, "os.phi"), "phi.Os:Kernel\n{\n\tlog 'hi';\n}\n");
            string rootfs = Path.Combine(root, "os.rootfs");
            Directory.CreateDirectory(rootfs);
            foreach (string name in new[] { "one", "two", "three" })
                File.WriteAllText(Path.Combine(rootfs, name + ".phi"), $"use broken;\n\nphi.P{name}:Program\n{{\n\tlog Broken.x;\n}}\n");

            BuildResult result = Builder.Build(Path.Combine(root, "os.phi"), Path.Combine(root, "build"));
            Assert.False(result.Success);
            Assert.Single(result.Errors, e => e.Contains("nothing"));
        }

        [Fact]
        public void AWarningInCodeOfTheRootfsReachesTheBuildOnce()
        {
            // (a string that ends with a line break written into it warns)
            File.WriteAllText(Path.Combine(root, "noisy.phi"), "phi.Noisy:Library\n{\n\tlog 'shared\n';\n}\n");
            File.WriteAllText(Path.Combine(root, "os.phi"), "phi.Os:Kernel\n{\n\tlog 'hi';\n}\n");
            string apps = Path.Combine(root, "os.rootfs", "APPS");
            Directory.CreateDirectory(apps);
            foreach (string name in new[] { "one", "two" })
                File.WriteAllText(Path.Combine(apps, name + ".phi"), $"use noisy;\n\nphi.P{name}:Program\n{{\n\tlog '{name}\n';\n}}\n");

            BuildResult result = Builder.Build(Path.Combine(root, "os.phi"), Path.Combine(root, "build"));
            Assert.True(result.Success, string.Join("\n", result.Errors));
            Assert.Single(result.Warnings, w => w.Contains("noisy.phi"));
            Assert.Single(result.Warnings, w => w.Contains("one.phi"));
            Assert.Single(result.Warnings, w => w.Contains("two.phi"));
        }

        [Fact]
        public void AProgramInARootfsBuiltOnItsOwnUsesWhatIsNextToItsKernel()
        {
            File.WriteAllText(Path.Combine(root, "shared.phi"), "phi.Shared:Library\n{\n\tconst int PORT: 4000;\n}\n");
            string rootfs = Path.Combine(root, "server.rootfs");
            Directory.CreateDirectory(rootfs);
            string program = Path.Combine(rootfs, "world.phi");
            File.WriteAllText(program, "use shared;\n\nphi.World:Program\n{\n\tlog Shared.PORT;\n}\n");

            BuildResult result = Builder.Build(program, Path.Combine(root, "build"), host: Host.Linux);
            Assert.True(result.Success, string.Join("\n", result.Errors));
        }
    }
}

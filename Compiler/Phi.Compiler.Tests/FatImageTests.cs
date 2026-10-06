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

        const int RootOffset = (FatImage.ReservedSectors + FatImage.FatCount * FatImage.SectorsPerFat) * FatImage.SectorSize;
        const int DataOffset = RootOffset + FatImage.RootEntries * 32;
        const int ClusterBytes = FatImage.SectorsPerCluster * FatImage.SectorSize;

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
            int fat = FatImage.ReservedSectors * FatImage.SectorSize;
            while (cluster >= 2 && cluster < 0xFFF8)
            {
                data.AddRange(p.Skip(DataOffset + (cluster - 2) * ClusterBytes).Take(ClusterBytes));
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
            Assert.Equal(FatImage.SectorsPerCluster, p[0x0D]);
            Assert.Equal(FatImage.RootEntries, U16(p, 0x11));
            Assert.Equal("FAT16   ", Encoding.ASCII.GetString(p, 0x36, 8));
            Assert.Equal(0xAA55, U16(p, 510));
            Assert.InRange(FatImage.ClusterCount, 4085, 65524); // the range that makes it FAT16
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

            var rootEntries = Entries(p, RootOffset, FatImage.RootEntries);
            Assert.Equal(new[] { "PHI DISK   ", "DOCS       ", "README  TXT" }, rootEntries.Select(e => e.Name));

            var readme = rootEntries.Single(e => e.Name == "README  TXT");
            Assert.Equal("hello", Encoding.ASCII.GetString(ReadChain(p, readme.Cluster, readme.Size)));

            var docs = rootEntries.Single(e => e.Name == "DOCS       ");
            Assert.Equal(0x10, docs.Attributes);
            var docsEntries = Entries(p, DataOffset + (docs.Cluster - 2) * ClusterBytes, ClusterBytes / 32);
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
    }
}

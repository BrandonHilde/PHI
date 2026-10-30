using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>NAME.machines files, and which samples are tests.</summary>
    public class NetworkTests : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "phi-network-" + Guid.NewGuid().ToString("N"));

        public NetworkTests() => Directory.CreateDirectory(root);
        public void Dispose() => Directory.Delete(root, recursive: true);

        [Fact]
        public void AMachinesFileListsFilesNextToItWithHowManyCopies()
        {
            string manifest = Path.Combine(root, "server.machines");
            File.WriteAllText(manifest, "# clients\nclient.phi 3\n\nbot.phi\n");
            var machines = NetworkMachines.Read(manifest);
            Assert.Equal(new[] { (Path.Combine(root, "client.phi"), 3), (Path.Combine(root, "bot.phi"), 1) }, machines);
            Assert.Equal(manifest, NetworkMachines.ManifestFor(Path.Combine(root, "server.phi")));
            Assert.Null(NetworkMachines.ManifestFor(Path.Combine(root, "client.phi")));
            Assert.Equal("client.3", NetworkMachines.Variant(Path.Combine(root, "client.phi"), 3));
            Assert.Equal("server", NetworkMachines.Variant(Path.Combine(root, "server.phi"), 1));
        }

        [Fact]
        public void SamplesWithExpectedOutputAreTestsButTheMachinesTheyListAreNot()
        {
            string game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "build", "old"));
            foreach (string f in new[] { "server.phi", "server.machines", "client.phi", "client.expected", "plain.phi" })
                File.WriteAllText(Path.Combine(game, f), f == "server.machines" ? "client.phi 2\n" : "");
            File.WriteAllText(Path.Combine(root, "demo.phi"), "");
            File.WriteAllText(Path.Combine(root, "demo.contains"), "hi\n");
            File.WriteAllText(Path.Combine(game, "build", "old", "copy.phi"), "");
            File.WriteAllText(Path.Combine(game, "build", "old", "copy.expected"), "");

            var tests = TestRunner.SampleTests(root).Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "demo.phi", "server.phi" }, tests);
            Assert.Empty(TestRunner.SampleTests(Path.Combine(root, "missing")));
        }
    }
}

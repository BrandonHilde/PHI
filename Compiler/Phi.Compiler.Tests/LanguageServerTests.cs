using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Phi.Cli;

namespace Phi.Compiler.Tests
{
    /// <summary>
    /// phi lsp, spoken to as an editor does: messages over a pipe, with files that are only open
    /// and not saved. Each place in a test's text is marked with a | where it's asked about.
    /// </summary>
    public class LanguageServerTests : IDisposable
    {
        /// <summary>How long to wait for an answer: compiling is quick, but tests run side by side.</summary>
        static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        readonly string root = Path.Combine(Path.GetTempPath(), "phi-lsp-" + Guid.NewGuid().ToString("N"));
        readonly Session session;

        public LanguageServerTests()
        {
            Directory.CreateDirectory(root);
            session = new Session(root);
        }

        public void Dispose()
        {
            session.Dispose();
            Directory.Delete(root, recursive: true);
        }

        /// <summary>The server, and the editor's end of the conversation with it.</summary>
        sealed class Session : IDisposable
        {
            readonly AnonymousPipeServerStream toServer = new(PipeDirection.Out);
            readonly AnonymousPipeServerStream fromServer = new(PipeDirection.In);
            readonly Thread server;
            readonly Thread reader;
            readonly BlockingCollection<JsonObject> replies = new();
            readonly ConcurrentDictionary<string, JsonArray> diagnostics = new(StringComparer.OrdinalIgnoreCase);
            int nextId = 1;
            int nextVersion = 1;
            int exitCode = -1;

            public Session(string folder)
            {
                var input = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
                var output = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle);
                var language = new LanguageServer(input, output, TextWriter.Null);
                server = new Thread(() => exitCode = language.Run()) { IsBackground = true };
                server.Start();
                reader = new Thread(ReadAll) { IsBackground = true };
                reader.Start();
                Request("initialize", new JsonObject
                {
                    ["processId"] = null,
                    ["rootUri"] = new Uri(folder).AbsoluteUri,
                    ["capabilities"] = new JsonObject(),
                });
                Notify("initialized", new JsonObject());
            }

            public JsonNode? Request(string method, JsonObject parameters)
            {
                JsonObject reply = RequestWhole(method, parameters);
                if (reply["error"] is JsonNode error) throw new InvalidOperationException(error.ToJsonString());
                return reply["result"];
            }

            public JsonObject RequestWhole(string method, JsonObject parameters)
            {
                int id = nextId++;
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters.DeepClone() });
                Assert.True(replies.TryTake(out JsonObject? reply, Patience), $"no answer to {method}");
                Assert.Equal(id, (int)reply!["id"]!);
                return reply;
            }

            public void Notify(string method, JsonObject parameters) =>
                Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

            public void Open(string uri, string text) =>
                Notify("textDocument/didOpen", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "phi", ["version"] = nextVersion++, ["text"] = text },
                });

            public void Change(string uri, string text) =>
                Notify("textDocument/didChange", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = nextVersion++ },
                    ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
                });

            public void Close(string uri) =>
                Notify("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });

            /// <summary>The file's diagnostics, once the last ones said are ones that pass the check.</summary>
            public JsonArray WaitForDiagnostics(string uri, Func<JsonArray, bool> check)
            {
                DateTime end = DateTime.UtcNow + Patience;
                while (DateTime.UtcNow < end)
                {
                    if (diagnostics.TryGetValue(uri, out JsonArray? found) && check(found)) return found;
                    Thread.Sleep(10);
                }
                diagnostics.TryGetValue(uri, out JsonArray? last);
                throw new Xunit.Sdk.XunitException($"never the expected diagnostics for {uri}; last: {last?.ToJsonString() ?? "none"}");
            }

            public bool HasDiagnostics(string uri) => diagnostics.TryGetValue(uri, out JsonArray? found) && found.Count > 0;

            public int ShutDown()
            {
                Request("shutdown", new JsonObject());
                Notify("exit", new JsonObject());
                Assert.True(server.Join(Patience), "the server didn't exit");
                return exitCode;
            }

            void Send(JsonObject message)
            {
                byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
                toServer.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
                toServer.Write(body);
                toServer.Flush();
            }

            void ReadAll()
            {
                try
                {
                    while (true)
                    {
                        var header = new StringBuilder();
                        while (!header.ToString().EndsWith("\r\n\r\n"))
                        {
                            int b = fromServer.ReadByte();
                            if (b < 0) return;
                            header.Append((char)b);
                        }
                        int length = int.Parse(header.ToString().Split(':')[1].Trim());
                        var body = new byte[length];
                        for (int read = 0; read < length;)
                        {
                            int n = fromServer.Read(body, read, length - read);
                            if (n == 0) return;
                            read += n;
                        }
                        var message = JsonNode.Parse(body)!.AsObject();
                        if ((string?)message["method"] == "textDocument/publishDiagnostics")
                            diagnostics[(string)message["params"]!["uri"]!] = message["params"]!["diagnostics"]!.AsArray();
                        else if (message["id"] != null) replies.Add(message);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }

            public void Dispose()
            {
                toServer.Dispose();
                server.Join(Patience);
                fromServer.Dispose();
            }
        }

        // ------------------------------------------------------------ helpers

        string Uri(string name) => new Uri(Path.Combine(root, name)).AbsoluteUri;

        string Write(string name, string text)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }

        static JsonObject Place(string uri, int line, int character) => new()
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
        };

        /// <summary>The text without its | and where that was.</summary>
        static (string Text, int Line, int Character) Marked(string text)
        {
            int at = text.IndexOf('|');
            Assert.True(at >= 0, "the test's text has no |");
            string before = text[..at];
            int line = before.Count(c => c == '\n');
            return (before + text[(at + 1)..], line, at - (before.LastIndexOf('\n') + 1));
        }

        /// <summary>Opens a file with a | in it and asks where the name there is declared.</summary>
        (string Uri, int Line, string Text)? GoFrom(string name, string marked)
        {
            var (text, line, character) = Marked(marked);
            session.Open(Uri(name), text);
            if (session.Request("textDocument/definition", Place(Uri(name), line, character)) is not JsonObject found) return null;
            int targetLine = (int)found["range"]!["start"]!["line"]!;
            int from = (int)found["range"]!["start"]!["character"]!;
            int to = (int)found["range"]!["end"]!["character"]!;
            string uri = (string)found["uri"]!;
            string[] lines = (uri.Equals(Uri(name), StringComparison.OrdinalIgnoreCase) ? text : File.ReadAllText(new Uri(uri).LocalPath)).Split('\n');
            return (uri, targetLine, lines[targetLine][from..to]);
        }

        /// <summary>Opens a file with a | in it and asks what could go there.</summary>
        List<(string Label, string Detail)> CompleteAt(string name, string marked)
        {
            var (text, line, character) = Marked(marked);
            session.Open(Uri(name), text);
            JsonNode result = session.Request("textDocument/completion", Place(Uri(name), line, character))!;
            return result["items"]!.AsArray().Select(i => ((string)i!["label"]!, (string?)i["detail"] ?? "")).ToList();
        }

        const string Program = """
            struct.Point
            {
            	int x;
            	int y;

            	[Sum]
            	[end: x + y]
            }

            enum.Tool
            {
            	PENCIL
            	BRUSH
            }

            phi.Main:Kernel
            {
            	const int LIMIT: 10;
            	int total: 5;
            	Point spot: 1 2;
            	Point spots[4];
            	str title: 'hello';

            	[Add: int amount;]
            		total++ amount;
            	[end: total]

            	[Run]
            		int kept: 1;
            		call Add: kept;
            		spots:1.x is LIMIT;
            	[end]
            }

            """;

        // ------------------------------------------------------------ errors as you type

        [Fact]
        public void ErrorsComeAndGoAsTheTextChanges()
        {
            string uri = Uri("main.phi");
            session.Open(uri, "phi.Main:Kernel\n{\n\tint total: 5;\n\ttotal++ 1;\n}\n");
            session.WaitForDiagnostics(uri, d => d.Count == 0);

            session.Change(uri, "phi.Main:Kernel\n{\n\tint total: 5;\n\ttotl++ 1;\n}\n");
            JsonArray errors = session.WaitForDiagnostics(uri, d => d.Count > 0);
            JsonNode first = errors[0]!;
            Assert.Equal(1, (int)first["severity"]!);
            Assert.Contains("totl", (string)first["message"]!);
            Assert.Equal(3, (int)first["range"]!["start"]!["line"]!);
            Assert.Equal(1, (int)first["range"]!["start"]!["character"]!);

            session.Change(uri, "phi.Main:Kernel\n{\n\tint total: 5;\n\ttotal++ 1;\n}\n");
            session.WaitForDiagnostics(uri, d => d.Count == 0);
        }

        [Fact]
        public void ParseErrorsAreSaid()
        {
            string uri = Uri("main.phi");
            session.Open(uri, "phi.Main:Kernel\n{\n\tint total: 5;\n\ttotal++ 1\n\ttotal++ 1;\n}\n");
            JsonArray errors = session.WaitForDiagnostics(uri, d => d.Count > 0);
            Assert.Contains("';'", (string)errors[0]!["message"]!);
            Assert.Equal(3, (int)errors[0]!["range"]!["start"]!["line"]!);
        }

        [Fact]
        public void AnErrorBetweenTokensUnderlinesTheOneBeforeIt()
        {
            string uri = Uri("main.phi");
            session.Open(uri, "phi.Main:Kernel\n{\n\tlog 'no end'\n\tlog 'next';\n}\n");
            JsonNode range = session.WaitForDiagnostics(uri, d => d.Count > 0)[0]!["range"]!;
            Assert.Equal(2, (int)range["start"]!["line"]!);
            Assert.Equal(5, (int)range["start"]!["character"]!);
            Assert.Equal(13, (int)range["end"]!["character"]!);
        }

        [Fact]
        public void AQuestionThatCompilesAFileSaysItsErrorsToo()
        {
            string uri = Uri("main.phi");
            session.Open(uri, "phi.Main:Kernel\n{\n}\n");
            session.WaitForDiagnostics(uri, d => d.Count == 0);

            // asked straight after the change, so the question is what compiles it
            session.Change(uri, "phi.Main:Kernel\n{\n\tmissing++ 1;\n}\n");
            session.Request("textDocument/definition", Place(uri, 2, 1));
            JsonArray errors = session.WaitForDiagnostics(uri, d => d.Count > 0);
            Assert.Contains("missing", (string)errors[0]!["message"]!);
        }

        [Fact]
        public void ANameAloneIsUnderlinedItselfAndTheNextLineStillCounts()
        {
            var (text, line, character) = Marked("phi.Main:Kernel\n{\n\tmadeUpName\n\n\tint after: 1;\n\t[Run]\n\t\t|\n\t[end]\n}\n");
            string uri = Uri("main.phi");
            session.Open(uri, text);

            JsonNode error = session.WaitForDiagnostics(uri, d => d.Count > 0)[0]!;
            Assert.Contains("madeUpName", (string)error["message"]!);
            Assert.Equal(2, (int)error["range"]!["start"]!["line"]!);
            Assert.Equal(1, (int)error["range"]!["start"]!["character"]!);
            Assert.Equal(11, (int)error["range"]!["end"]!["character"]!);

            JsonNode result = session.Request("textDocument/completion", Place(uri, line, character))!;
            Assert.Contains(result["items"]!.AsArray(), i => (string)i!["label"]! == "after");
        }

        [Fact]
        public void AWarningIsAWarning()
        {
            Write("other.phi", "phi.Other:Library\n{\n\tint shared: 1;\n}\n");
            string uri = Uri("main.phi");
            session.Open(uri, "use other;\nphi.Main:Kernel\n{\n\tshared++ 1;\n}\n");
            JsonArray found = session.WaitForDiagnostics(uri, d => d.Count > 0);
            Assert.Equal(2, (int)found[0]!["severity"]!);
        }

        [Fact]
        public void AModuleHasNoErrorForNotBeingAProgram()
        {
            string uri = Uri("module.phi");
            session.Open(uri, "phi.Module:Library\n{\n\tint count: 1;\n\t[Next]\n\t\tcount++ 1;\n\t[end: count]\n}\n");
            session.WaitForDiagnostics(uri, d => d.Count == 0);

            session.Change(uri, "phi.Module:Library\n{\n\tint count: 1;\n\t[Next]\n\t\tcont++ 1;\n\t[end: count]\n}\n");
            JsonArray errors = session.WaitForDiagnostics(uri, d => d.Count > 0);
            Assert.Contains("cont", (string)errors[0]!["message"]!);
        }

        [Fact]
        public void AProgramSeesWhatsUnsavedInItsModule()
        {
            Write("helper.phi", "phi.Helper:Library\n{\n}\n");
            string main = Uri("main.phi");
            string helper = Uri("helper.phi");

            session.Open(main, "use helper;\nphi.Main:Kernel\n{\n\tcall Helper.Add;\n}\n");
            session.WaitForDiagnostics(main, d => d.Count > 0);

            // saved nowhere: only the editor has this
            session.Open(helper, "phi.Helper:Library\n{\n\t[Add]\n\t[end]\n}\n");
            session.WaitForDiagnostics(main, d => d.Count == 0);

            // and closing it goes back to what's on the disk
            session.Close(helper);
            session.WaitForDiagnostics(main, d => d.Count > 0);
        }

        [Fact]
        public void AModuleWithAnErrorSaysItOnceInItsOwnFile()
        {
            string main = Uri("main.phi");
            string helper = Uri("helper.phi");
            Write("helper.phi", "phi.Helper:Library\n{\n}\n");

            session.Open(main, "use helper;\nphi.Main:Kernel\n{\n}\n");
            session.Open(helper, "phi.Helper:Library\n{\n\tmissing++ 1;\n}\n");
            session.WaitForDiagnostics(helper, d => d.Count > 0);
            session.WaitForDiagnostics(main, d => d.Count == 0);
        }

        [Fact]
        public void AClosedFileWithAnErrorIsSaidToo()
        {
            Write("helper.phi", "phi.Helper:Library\n{\n\tmissing++ 1;\n}\n");
            session.Open(Uri("main.phi"), "use helper;\nphi.Main:Kernel\n{\n}\n");
            JsonArray errors = session.WaitForDiagnostics(Uri("helper.phi"), d => d.Count > 0);
            Assert.Contains("missing", (string)errors[0]!["message"]!);
        }

        [Fact]
        public void AnOpenLibraryFilesErrorsStayInIt()
        {
            string net = Path.Combine(RepoRoot(), "lib", "phi", "host", "net.phi");
            string uri = new Uri(net).AbsoluteUri;
            string text = File.ReadAllText(net);

            session.Open(uri, text);
            session.WaitForDiagnostics(uri, d => d.Count == 0);

            session.Change(uri, text + "\nphi.Extra:Library\n{\n\tmissing++ 1;\n}\n");
            JsonArray errors = session.WaitForDiagnostics(uri, d => d.Count > 0);
            Assert.Contains("missing", (string)errors[0]!["message"]!);
        }

        /// <summary>The address VS Code has for a file: a small drive letter, and its colon written out.</summary>
        static string AsVsCodeWritesIt(string path)
        {
            string uri = new Uri(path).AbsoluteUri;
            return OperatingSystem.IsWindows() ? "file:///" + char.ToLowerInvariant(uri[8]) + "%3A" + uri[10..] : uri;
        }

        [Fact]
        public void ModulesThatNeedAProgramHaveNoFalseErrorsInAnEditor()
        {
            foreach (string file in new[] { "lib/phi/host/net.phi", "lib/phi/net/tlslink.phi", "lib/phi/drivers/vga_text.phi", "lib/phi/data/keyed.phi" })
            {
                string path = Path.GetFullPath(Path.Combine(RepoRoot(), file));
                string uri = AsVsCodeWritesIt(path);
                session.Open(uri, File.ReadAllText(path));
                JsonArray found = session.WaitForDiagnostics(uri, _ => true);
                Assert.True(found.Count == 0, file + ": " + found.ToJsonString());
            }
        }

        // ------------------------------------------------------------ going to a definition

        [Theory]
        [InlineData("call A|dd: kept;", "Add", 23)]
        [InlineData("call Add: ke|pt;", "kept", 28)]
        [InlineData("spots:1.|x is LIMIT;", "x", 2)]
        [InlineData("spots:1.x is LIM|IT;", "LIMIT", 17)]
        [InlineData("sp|ots:1.x is LIMIT;", "spots", 20)]
        public void GoesToWhatANameIs(string marked, string name, int line)
        {
            string unmarked = marked.Replace("|", "");
            Assert.Contains(unmarked, Program);
            var found = GoFrom("main.phi", Program.Replace(unmarked, marked));
            Assert.NotNull(found);
            Assert.Equal(name, found!.Value.Text);
            Assert.Equal(line, found.Value.Line);
        }

        [Fact]
        public void GoesToAStructsTypeFieldMethodAndAnEnumValue()
        {
            string text = Program.Replace("[Run]", "[Run]\n\t\tTool chosen: Tool.BRUSH;\n\t\tint sum: (spot.Sum);\n\t\tint sx: spot.y;");

            Assert.Equal("Point", GoFrom("a.phi", text.Replace("Point spot:", "Poi|nt spot:"))!.Value.Text);
            Assert.Equal("Tool", GoFrom("b.phi", text.Replace("Tool chosen:", "To|ol chosen:"))!.Value.Text);
            Assert.Equal("BRUSH", GoFrom("c.phi", text.Replace("Tool.BRUSH", "Tool.BR|USH"))!.Value.Text);
            Assert.Equal("Sum", GoFrom("d.phi", text.Replace("spot.Sum", "spot.S|um"))!.Value.Text);
            var field = GoFrom("e.phi", text.Replace("spot.y;", "spot.|y;"))!;
            Assert.Equal("y", field.Value.Text);
            Assert.Equal(3, field.Value.Line);
        }

        [Fact]
        public void AStructsMethodSeesItsFields()
        {
            var found = GoFrom("main.phi", Program.Replace("[end: x + y]", "[end: x| + y]"));
            Assert.Equal("x", found!.Value.Text);
            Assert.Equal(2, found.Value.Line);
        }

        [Fact]
        public void ANameDeclaredInTwoBlocksIsTheOneBeforeIt()
        {
            string text = """
                phi.Main:Kernel
                {
                	[Run: int pick;]
                		if pick is 1
                			int value: 1;
                			log value;
                		;;
                		if pick is 2
                			int value: 2;
                			log val|ue;
                		;;
                	[end]
                }

                """;
            var found = GoFrom("main.phi", text);
            Assert.Equal(8, found!.Value.Line);
        }

        [Fact]
        public void GoesAcrossFilesAndToTheFileOfAUse()
        {
            Write("helper.phi", "phi.Helper:Library\n{\n\tint count: 1;\n\t[Add]\n\t[end]\n}\n");
            string text = "use helper;\nphi.Main:Kernel\n{\n\tcall Helper.Add;\n\tHelper.count++ 1;\n}\n";

            var method = GoFrom("a.phi", text.Replace("Helper.Add", "Helper.A|dd"));
            Assert.Equal(Uri("helper.phi"), method!.Value.Uri, ignoreCase: true);
            Assert.Equal("Add", method.Value.Text);

            var cls = GoFrom("b.phi", text.Replace("Helper.Add", "Hel|per.Add"));
            Assert.Equal("Helper", cls!.Value.Text);

            var variable = GoFrom("c.phi", text.Replace("Helper.count", "Helper.co|unt"));
            Assert.Equal("count", variable!.Value.Text);

            var file = GoFrom("d.phi", text.Replace("use helper", "use hel|per"));
            Assert.Equal(Uri("helper.phi"), file!.Value.Uri, ignoreCase: true);
            Assert.Equal(0, file.Value.Line);
        }

        [Fact]
        public void GoesIntoTheStandardLibrary()
        {
            var found = GoFrom("main.phi", "use memory.mem;\nphi.Main:Kernel\n{\n\tcall Mem.Co|py: 0 0 0;\n}\n");
            Assert.NotNull(found);
            Assert.Equal("Copy", found!.Value.Text);
            Assert.EndsWith("mem.phi", new Uri(found.Value.Uri).LocalPath);
        }

        [Fact]
        public void PhisOwnNamesAndUnknownNamesGoNowhere()
        {
            Assert.Null(GoFrom("a.phi", "phi.Main:Kernel\n{\n\tcall OS.Sle|ep: 1;\n}\n"));
            Assert.Null(GoFrom("b.phi", "phi.Main:Kernel\n{\n\tnobo|dy++ 1;\n}\n"));
            Assert.Null(GoFrom("c.phi", "phi.Main:Kernel\n{\n\t# no|thing here\n}\n"));
        }

        // ------------------------------------------------------------ completing a name

        [Fact]
        public void CompletesWhatsInSight()
        {
            var labels = CompleteAt("main.phi", Program.Replace("call Add: kept;", "call Add: kept;\n\t\t|")).Select(i => i.Label).ToList();
            Assert.Contains("kept", labels);
            Assert.Contains("total", labels);
            Assert.Contains("LIMIT", labels);
            Assert.Contains("Add", labels);
            Assert.Contains("Point", labels);
            Assert.Contains("Tool", labels);
            Assert.Contains("OS", labels);
            Assert.Contains("while", labels);
            Assert.Contains("u32", labels);
            Assert.DoesNotContain(labels, l => l.Contains('#'));
        }

        [Fact]
        public void CompletesAMethodsParametersAndNotAnothers()
        {
            var labels = CompleteAt("main.phi", Program.Replace("total++ amount;", "total++ amount;\n\t\t|")).Select(i => i.Label).ToList();
            Assert.Contains("amount", labels);
            Assert.DoesNotContain("kept", labels);
        }

        [Fact]
        public void CompletesAfterADot()
        {
            string text = Program.Replace("call Add: kept;", "call Add: kept;\n\t\tspot.|\n\t\tint after: 1;");
            var items = CompleteAt("main.phi", text);
            Assert.Contains(("x", "int"), items);
            Assert.Contains(items, i => i.Label == "Sum" && i.Detail.Contains("Point.Sum"));
            Assert.DoesNotContain(items, i => i.Label == "while");
        }

        [Theory]
        [InlineData("spots.|", "len")]
        [InlineData("spots:2.|", "x")]
        [InlineData("spots:kept.|", "y")]
        [InlineData("spots:(kept + 1).|", "x")]
        [InlineData("title.|", "len")]
        [InlineData("Tool.|", "BRUSH")]
        [InlineData("Main.|", "total")]
        [InlineData("OS.|", "Sleep")]
        [InlineData("OS.Sl|", "Sleep")]
        [InlineData("spot.|x", "y")]
        [InlineData("Colors.|", "White")]
        [InlineData("OS.|", "TimerEvent")]
        public void CompletesTheMembersOf(string written, string expected)
        {
            var labels = CompleteAt("main.phi", Program.Replace("call Add: kept;", "call Add: kept;\n\t\t" + written)).Select(i => i.Label);
            Assert.Contains(expected, labels);
        }

        /// <summary>A module with an error isn't bound at all, so all there is to go on is how it's written.</summary>
        [Theory]
        [InlineData("many:1.|", "x")]
        [InlineData("many.|", "len")]
        [InlineData("one.|", "inner")]
        [InlineData("one.inner.|", "y")]
        public void CompletesInAModuleThatsNeverBeenRight(string written, string expected)
        {
            string text = "struct.Point\n{\n\tint x;\n\tint y;\n}\nstruct.Box\n{\n\tPoint inner;\n}\n" +
                          "phi.Module:Library\n{\n\tPoint many[3];\n\tBox one;\n\t" + written + "\n}\n";
            Assert.Contains(expected, CompleteAt("module.phi", text).Select(i => i.Label));
        }

        [Fact]
        public void AnErrorInAModuleKeepsWhatWasKnownAboutIt()
        {
            string uri = Uri("module.phi");
            const string Good = "phi.Module:Library\n{\n\tPoint many[3];\n}\nstruct.Point\n{\n\tint x;\n}\n";
            session.Open(uri, Good);
            session.WaitForDiagnostics(uri, d => d.Count == 0);

            var (text, line, character) = Marked(Good.Replace("many[3];", "many[3];\n\tmany:1.|"));
            session.Change(uri, text);
            JsonNode result = session.Request("textDocument/completion", Place(uri, line, character))!;
            Assert.Contains(result["items"]!.AsArray(), i => (string)i!["label"]! == "x");
        }

        [Fact]
        public void CompletesAMethodsSignature()
        {
            var items = CompleteAt("main.phi", Program.Replace("call Add: kept;", "call Add: kept;\n\t\tMain.|"));
            Assert.Contains(items, i => i.Label == "Add" && i.Detail.Contains("int amount"));
        }

        [Theory]
        [InlineData("log 'spot.|';")]
        [InlineData("# spot.|")]
        public void TextAndCommentsHaveNoCompletions(string written)
        {
            Assert.Empty(CompleteAt("main.phi", Program.Replace("call Add: kept;", "call Add: kept;\n\t\t" + written)));
        }

        [Fact]
        public void BlockCommentsHaveNoCompletions()
        {
            string text = "phi.Main:Kernel\n{\n\t#\n\tin a block |comment\n\t#\n}\n";
            Assert.Empty(CompleteAt("main.phi", text));
            string after = "phi.Main:Kernel\n{\n\t#\n\tin a block comment\n\t#\n\t|\n}\n";
            Assert.NotEmpty(CompleteAt("after.phi", after));
        }

        [Fact]
        public void ACompletionUsesTheNewestText()
        {
            string uri = Uri("main.phi");
            session.Open(uri, Program);
            var (text, line, character) = Marked(Program.Replace("[Run]", "[Run]\n\t\tint fresh: 1;\n\t\t|"));
            session.Change(uri, text);
            // (asked at once: it can't wait for typing to stop)
            JsonNode result = session.Request("textDocument/completion", Place(uri, line, character))!;
            Assert.Contains(result["items"]!.AsArray(), i => (string)i!["label"]! == "fresh");
        }

        [Fact]
        public void AClassNotClosedYetKeepsTheLastNamesKnown()
        {
            string uri = Uri("main.phi");
            session.Open(uri, Program);
            session.WaitForDiagnostics(uri, _ => true);
            session.Request("textDocument/completion", Place(uri, 0, 0));

            int close = Program.LastIndexOf('}');
            var (text, line, character) = Marked(Program[..close].Replace("[Run]", "[Run]\n\t\tspot.|"));
            session.Change(uri, text);
            JsonNode result = session.Request("textDocument/completion", Place(uri, line, character))!;
            Assert.Contains(result["items"]!.AsArray(), i => (string)i!["label"]! == "x");
        }

        // ------------------------------------------------------------ the conversation

        [Fact]
        public void SaysWhatItDoes()
        {
            using var other = new Session(root);
            JsonNode capabilities = other.Request("initialize", new JsonObject { ["rootUri"] = new Uri(root).AbsoluteUri })!["capabilities"]!;
            Assert.True((bool)capabilities["definitionProvider"]!);
            Assert.Equal(1, (int)capabilities["textDocumentSync"]!["change"]!);
            Assert.Contains(capabilities["completionProvider"]!["triggerCharacters"]!.AsArray(), c => (string)c! == ".");
        }

        [Fact]
        public void AnUnknownRequestGetsAnError()
        {
            JsonObject reply = session.RequestWhole("textDocument/nothingLikeThis", new JsonObject());
            Assert.Equal(-32601, (int)reply["error"]!["code"]!);
        }

        [Fact]
        public void AQuestionAboutAFileThatIsntOpenHasNoAnswer()
        {
            Assert.Null(session.Request("textDocument/definition", Place(Uri("nowhere.phi"), 0, 0)));
        }

        [Fact]
        public void ShutsDownAsAsked()
        {
            using var other = new Session(root);
            Assert.Equal(0, other.ShutDown());
        }

        /// <summary>
        /// Every stage of a file being written, as it's typed: questions are answered whatever
        /// state it's in, the server's still there at the end, and it ends with no errors.
        /// </summary>
        [Theory]
        [InlineData("tests/struct_methods.phi")]
        [InlineData("tests/enum_when.phi")]
        [InlineData("tests/interfaces.phi")]
        [InlineData("samples/desktop.rootfs/paddles.phi")]
        public void EveryStageOfWritingAFileIsAnswered(string sample)
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), sample));
            string uri = Uri("written.phi");
            session.Open(uri, "");
            const int Stages = 60;
            for (int stage = 1; stage <= Stages; stage++)
            {
                int length = text.Length * stage / Stages;
                session.Change(uri, text[..length]);
                string[] lines = text[..length].Split('\n');
                JsonObject place = Place(uri, lines.Length - 1, lines[^1].Length);
                Assert.NotNull(session.Request("textDocument/completion", place));
                session.Request("textDocument/definition", place);
            }
            Assert.Equal(0, session.ShutDown());
        }

        // ------------------------------------------------------------ phi check, which is the same check

        [Theory]
        [InlineData("lib/phi/drivers/vga_text.phi")]    // a module for a kernel
        [InlineData("lib/phi/host/net.phi")]            // a part of the runtime, for every system
        [InlineData("lib/phi/host/net_windows.phi")]    // a system's own
        [InlineData("lib/phi/host/time_linux64.phi")]
        [InlineData("lib/phi/net/tlslink.phi")]         // needs the Tcp its program has
        [InlineData("lib/phi/data/keyed.phi")]          // needs a struct that has Keyed
        [InlineData("samples/mmo/server.phi")]          // a machine of a network: it has class Machine
        [InlineData("samples/mmo/client.phi")]
        [InlineData("tests/foreign_program.phi")]       // a program for one system
        [InlineData("tests/sys_calls_program.phi")]
        [InlineData("tests/block_scope.phi")]           // a test of :Library classes
        [InlineData("tests/net_http.phi")]              // phi test gives it class TestWeb
        [InlineData("samples/mmo/load_players.phi")]    // phi load gives it its network, Machine and LoadSettings
        [InlineData("lib/phi/test/load.phi")]
        public void AFileThatsRightHasNoErrors(string file)
        {
            string path = Path.Combine(RepoRoot(), file);
            CompileResult result = new SourceChecker().Check(path, File.ReadAllText(path));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Theory]
        [InlineData("lib/phi/host/net_windows.phi")]
        [InlineData("lib/phi/host/net.phi")]
        [InlineData("lib/phi/net/tlslink.phi")]
        [InlineData("lib/phi/data/keyed.phi")]
        [InlineData("samples/mmo/server.phi")]
        [InlineData("samples/mmo/load_players.phi")]
        public void AMistakeInAFileIsFoundWhereverItsChecked(string file)
        {
            string path = Path.Combine(RepoRoot(), file);
            string own = File.ReadAllText(path);
            // (of the same kind as the file's own, as a system's file has only that system's classes)
            var classes = System.Text.RegularExpressions.Regex.Matches(own, @"^phi\.\w+:(\w+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            string kind = classes.Count > 0 ? classes[^1].Groups[1].Value : "Library";
            string text = own + $"\nphi.Mistaken:{kind}\n{{\n\tnothingCalledThis++ 1;\n}}\n";
            CompileResult result = new SourceChecker().Check(path, text);
            Diagnostic error = Assert.Single(result.Diagnostics.Where(d => d.Severity == Severity.Error));
            Assert.Contains("nothingCalledThis", error.Message);
            Assert.Equal(PhiCompiler.FileKey(path), PhiCompiler.FileKey(error.File!.Path));
        }

        [Fact]
        public void AModuleIsCheckedInAProgramThatUsesItAndNotAnEditorsCopy()
        {
            const string User = "use other;\nuse needs;\nphi.Main:Kernel\n{\n}\n";
            Write("other.phi", "phi.Other:Library\n{\n\tint value: 1;\n}\n");
            string needs = Write("needs.phi", "phi.Needs:Library\n{\n\tOther.value++ 1;\n}\n");

            // only an editor's copy of a program uses it: that's nothing to go by
            Write(".main.phicheck.phi", User);
            Assert.False(new SourceChecker().Check(needs, File.ReadAllText(needs)).Success);

            Write("main.phi", User);
            CompileResult result = new SourceChecker().Check(needs, File.ReadAllText(needs));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void AModuleInASubfolderFindsItsUsesFromTheFolderAboveIt()
        {
            // (no program uses them yet, and there's no repository to look for one in)
            Write("lib/one.phi", "phi.One:Library\n{\n\t[Value]\n\t[end: 1]\n}\n");
            string two = Write("lib/two.phi", "use lib.one;\nphi.Two:Library\n{\n\t[Value]\n\t[end: (One.Value) + 1]\n}\n");
            CompileResult result = new SourceChecker().Check(two, File.ReadAllText(two));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Fact]
        public void AModuleInASubfolderStillHasItsOwnMistakesFound()
        {
            Write("lib/one.phi", "phi.One:Library\n{\n\t[Value]\n\t[end: 1]\n}\n");
            string two = Write("lib/two.phi", "use lib.one;\nphi.Two:Library\n{\n\tnothingCalledThis++ 1;\n}\n");
            CompileResult result = new SourceChecker().Check(two, File.ReadAllText(two));
            Diagnostic error = Assert.Single(result.Diagnostics.Where(d => d.Severity == Severity.Error));
            Assert.Contains("nothingCalledThis", error.Message);
        }

        [Fact]
        public void AModuleInASubfolderIsCheckedWithItsUnsavedText()
        {
            Write("lib/one.phi", "phi.One:Library\n{\n\t[Value]\n\t[end: 1]\n}\n");
            string two = Write("lib/two.phi", "use lib.one;\nphi.Two:Library\n{\n\t[Value]\n\t[end: (One.Value) + 1]\n}\n");
            CompileResult result = new SourceChecker().Check(two, "use lib.one;\nphi.Two:Library\n{\n\tchangedSinceSaved++ 1;\n}\n");
            Assert.Contains(result.Diagnostics, d => d.Message.Contains("changedSinceSaved"));
        }

        [Fact]
        public void AUseThatsNowhereIsStillAnError()
        {
            string lost = Write("lib/lost.phi", "use lib.nothing;\nphi.Lost:Library\n{\n}\n");
            CompileResult result = new SourceChecker().Check(lost, File.ReadAllText(lost));
            Assert.Contains(result.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("can't find lib/nothing.phi"));
        }

        [Fact]
        public void AModuleThatNeedsItsProgramIsCheckedInOneAboveIt()
        {
            // (Shared comes only from the program, so a smallest program around the module isn't enough)
            Write("shared.phi", "phi.Shared:Library\n{\n\tint value: 1;\n}\n");
            Write("lib/one.phi", "phi.One:Library\n{\n\t[Value]\n\t[end: 1]\n}\n");
            string two = Write("lib/two.phi", "use lib.one;\nphi.Two:Library\n{\n\tShared.value++ (One.Value);\n}\n");
            Write("main.phi", "use shared;\nuse lib.two;\nphi.Main:Kernel\n{\n}\n");
            CompileResult result = new SourceChecker().Check(two, File.ReadAllText(two));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Theory]
        [InlineData(Host.PhiOS)]
        [InlineData(Host.Linux)]
        [InlineData(Host.Windows64)]
        public void AU32GoesToAnOSCallTheSameOnEverySystem(Host system)
        {
            // (on another system a module with int parameters does the call, which a program doesn't see)
            string path = Write("append.phi", "phi.Append:Program\n{\n\tu32 size: 1;\n\tcall OS.AppendFile: 'x.txt' 'y' size;\n}\n");
            CompileResult result = new SourceChecker(system).Check(path, File.ReadAllText(path));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }

        [Theory]
        [InlineData(Host.PhiOS)]
        [InlineData(Host.Linux)]
        public void AU32GoingToAMethodsIntIsTheSameErrorOnEverySystem(Host system)
        {
            string path = Write("own.phi", "phi.Own:Program\n{\n\tu32 size: 1;\n\tcall Take: size;\n\t[Take: int n: 0;]\n\t[end]\n}\n");
            CompileResult result = new SourceChecker(system).Check(path, File.ReadAllText(path));
            Assert.Contains(result.Diagnostics, d => d.Severity == Severity.Error && d.Message.Contains("comes out negative"));
        }

        [Fact]
        public void TheSystemAskedForIsTheOneChecked()
        {
            string path = Write("hello.phi", "phi.Hello:Program\n{\n\tlog 'hi';\n}\n");
            Assert.True(new SourceChecker(Host.Windows64).Check(path, File.ReadAllText(path)).Success);
        }

        static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PHI.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("no repository");
        }
    }
}

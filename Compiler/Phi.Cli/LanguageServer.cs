using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phi.Compiler;
using Phi.Compiler.Language;
using Phi.Compiler.Syntax;

namespace Phi.Cli
{
    /// <summary>
    /// phi lsp: the Language Server Protocol over standard input and output, for editors: errors
    /// as a file is written, going to a definition, and completing names. Messages are handled
    /// one at a time on one thread; a file is compiled again once typing has stopped for a
    /// moment, or as soon as something asks about it.
    /// </summary>
    public sealed class LanguageServer
    {
        /// <summary>How long typing must stop before a file is compiled again.</summary>
        static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(150);

        sealed class Document
        {
            public string Uri { get; init; } = "";
            public string Path { get; init; } = "";
            public string Key { get; init; } = "";
            public string Text { get; set; } = "";
            public SourceChecker Checker { get; } = new();

            /// <summary>The text has changed since it was last compiled.</summary>
            public bool Stale { get; set; } = true;
            public CompileResult? Result { get; set; }
            public SourceFile? File { get; set; }
            public ProgramIndex? Index { get; set; }

            /// <summary>The last program that had all this file's classes, for while one doesn't parse.</summary>
            public CompileResult? LastGood { get; set; }
            public int LastGoodClasses { get; set; }
        }

        readonly Stream input;
        readonly Stream output;
        readonly TextWriter log;
        readonly Dictionary<string, Document> documents = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> published = new(StringComparer.OrdinalIgnoreCase);
        readonly List<string> workspaceFolders = new();
        bool shutdownAsked;

        public LanguageServer(Stream input, Stream output, TextWriter log)
        {
            this.input = input;
            this.output = output;
            this.log = log;
        }

        /// <returns>The process's exit code: 0 if the editor asked to shut down first, as the protocol says.</returns>
        public int Run()
        {
            var messages = new BlockingCollection<JsonObject?>();
            var reader = new Thread(() =>
            {
                try
                {
                    while (ReadMessage() is JsonObject message) messages.Add(message);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or JsonException)
                {
                    log.WriteLine("phi lsp: reading: " + e.Message);
                }
                messages.Add(null);
            }) { IsBackground = true };
            reader.Start();

            while (true)
            {
                TimeSpan wait = documents.Values.Any(d => d.Stale) ? Pause : Timeout.InfiniteTimeSpan;
                if (!messages.TryTake(out JsonObject? message, wait))
                {
                    CompileStale();
                    continue;
                }
                if (message == null) return shutdownAsked ? 0 : 1;
                if ((string?)message["method"] == "exit") return shutdownAsked ? 0 : 1;
                Handle(message);
            }
        }

        // ------------------------------------------------------------ messages

        void Handle(JsonObject message)
        {
            string? method = (string?)message["method"];
            JsonNode? id = message["id"];
            JsonObject? parameters = message["params"] as JsonObject;

            // a reply from the editor: this server asks it nothing that needs one
            if (method == null) return;
            try
            {
                JsonNode? answer = Answer(method, parameters, out bool known);
                if (id == null) return;
                if (known) Reply(id, answer);
                else ReplyError(id, -32601, $"phi lsp doesn't do {method}");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // one request that goes wrong mustn't end the editing session
                log.WriteLine($"phi lsp: {method}: {e}");
                if (id != null) ReplyError(id, -32603, e.Message);
            }
        }

        JsonNode? Answer(string method, JsonObject? p, out bool known)
        {
            known = true;
            switch (method)
            {
                case "initialize": return Initialize(p);
                case "shutdown":
                    shutdownAsked = true;
                    return null;
                case "textDocument/didOpen": Open(p!["textDocument"]!); return null;
                case "textDocument/didChange": Change(p!); return null;
                case "textDocument/didClose": Close(p!["textDocument"]!); return null;
                case "textDocument/didSave": Saved(p!["textDocument"]!); return null;
                case "workspace/didChangeWatchedFiles": DiskChanged(); return null;
                case "textDocument/definition": return Definition(p!);
                case "textDocument/formatting": return Formatted(p!);
                case "textDocument/completion": return Completion(p!);
                default:
                    known = false;
                    return null;
            }
        }

        JsonNode Initialize(JsonObject? p)
        {
            if (p?["workspaceFolders"] is JsonArray folders)
                foreach (JsonNode? folder in folders)
                    if ((string?)folder?["uri"] is string uri) workspaceFolders.Add(PathOf(uri));
            if (workspaceFolders.Count == 0 && (string?)p?["rootUri"] is string root) workspaceFolders.Add(PathOf(root));

            return new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    // 1: the whole text with each change
                    ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1, ["save"] = true },
                    ["definitionProvider"] = true,
                    ["documentFormattingProvider"] = true,
                    ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".") },
                },
                ["serverInfo"] = new JsonObject { ["name"] = "phi lsp" },
            };
        }

        // ------------------------------------------------------------ open files

        void Open(JsonNode document)
        {
            string uri = (string)document["uri"]!;
            string path = PathOf(uri);
            string key = PhiCompiler.FileKey(path);
            documents[key] = new Document { Uri = uri, Path = path, Key = key, Text = (string)document["text"]! };
        }

        void Change(JsonObject p)
        {
            if (Find((string)p["textDocument"]!["uri"]!) is not Document document) return;
            // the whole text each time: the last change has it
            if (p["contentChanges"] is JsonArray { Count: > 0 } changes)
            {
                document.Text = (string)changes[changes.Count - 1]!["text"]!;
                document.Stale = true;
            }
        }

        void Close(JsonNode document)
        {
            if (Find((string)document["uri"]!) is not Document closed) return;
            documents.Remove(closed.Key);
            // what used it now reads it from the disk
            MarkUsersStale(closed.Key);
            PublishAll();
        }

        void Saved(JsonNode document)
        {
            if (Find((string)document["uri"]!) is not Document saved) return;
            saved.Checker.Saved();
            saved.Stale = true;
        }

        void DiskChanged()
        {
            foreach (Document d in documents.Values) d.Stale = true;
        }

        Document? Find(string uri) => documents.GetValueOrDefault(PhiCompiler.FileKey(PathOf(uri)));

        void MarkUsersStale(string key)
        {
            foreach (Document d in documents.Values)
                if (d.Result?.Files.Contains(key) == true) d.Stale = true;
        }

        // ------------------------------------------------------------ compiling

        void CompileStale()
        {
            // a change is news to the files that use it, which can have new errors for it
            foreach (Document changed in documents.Values.Where(d => d.Stale).ToList())
                MarkUsersStale(changed.Key);
            foreach (Document d in documents.Values.Where(d => d.Stale).ToList())
                Compile(d);
            PublishAll();
        }

        void Compile(Document d)
        {
            d.Stale = false;
            var open = documents.Values.ToDictionary(o => o.Key, o => o.Text, StringComparer.OrdinalIgnoreCase);
            try
            {
                d.File = new SourceFile(d.Path, d.Text);
                d.Result = d.Checker.Check(d.Path, d.Text, open, bindAnyway: true);
                d.Index = new ProgramIndex(d.Result, d.File);
                if (d.Result.Bound != null && (d.Result.Success || d.Index.ClassesKnown >= d.LastGoodClasses))
                {
                    d.LastGood = d.Result;
                    d.LastGoodClasses = d.Index.ClassesKnown;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // half-written code the compiler can't take: the last errors and names stay
                log.WriteLine($"phi lsp: compiling {d.Path}: {e}");
            }
        }

        /// <summary>
        /// Says each file's errors, when they've changed. An open file's are from its own check;
        /// a closed file's are from the open programs it's a part of.
        /// </summary>
        void PublishAll()
        {
            var byFile = new Dictionary<string, (string Uri, SourceFile File, List<Diagnostic> Items)>(StringComparer.OrdinalIgnoreCase);
            foreach (Document d in documents.Values)
            {
                byFile[d.Key] = (d.Uri, d.File ?? new SourceFile(d.Path, d.Text), new List<Diagnostic>());
                foreach (Diagnostic diagnostic in d.Result?.Diagnostics ?? Array.Empty<Diagnostic>())
                {
                    SourceFile file = diagnostic.File ?? d.File!;
                    string key = PhiCompiler.FileKey(file.Path);
                    if (key != d.Key && documents.ContainsKey(key)) continue;
                    if (!byFile.TryGetValue(key, out var list))
                    {
                        if (UriOfDiskFile(file) is not string uri) continue;
                        list = byFile[key] = (uri, file, new List<Diagnostic>());
                    }
                    if (!list.Items.Contains(diagnostic)) list.Items.Add(diagnostic);
                }
            }

            foreach (string uri in published.Keys.Except(byFile.Values.Select(v => v.Uri), StringComparer.OrdinalIgnoreCase).ToList())
            {
                Publish(uri, new JsonArray());
                published.Remove(uri);
            }

            foreach (var (uri, file, items) in byFile.Values)
            {
                var array = new JsonArray();
                foreach (Diagnostic d in items)
                    array.Add(new JsonObject
                    {
                        ["range"] = RangeOf(file, Visible(file, d.Span)),
                        // 1 is an error, 2 a warning
                        ["severity"] = d.Severity == Severity.Error ? 1 : 2,
                        ["source"] = "phi",
                        ["message"] = d.Message,
                    });
                string text = array.ToJsonString();
                if (published.TryGetValue(uri, out string? before) && before == text) continue;
                published[uri] = text;
                Publish(uri, array);
            }
        }

        void Publish(string uri, JsonArray diagnostics) =>
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/publishDiagnostics",
                ["params"] = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics },
            });

        // ------------------------------------------------------------ questions about a place

        /// <summary>The file, compiled as it is now, and where the question's place is in it.</summary>
        (Document Document, int Offset)? Place(JsonObject p)
        {
            if (Find((string)p["textDocument"]!["uri"]!) is not Document document) return null;
            if (document.Stale || document.Index == null)
            {
                MarkUsersStale(document.Key);
                Compile(document);
                PublishAll();
            }
            if (document.File == null) return null;
            JsonNode position = p["position"]!;
            return (document, document.File.GetOffset((int)position["line"]!, (int)position["character"]!));
        }

        /// <summary>The whole file laid out as phi fmt lays it out; nothing if it doesn't parse, or is laid out already.</summary>
        JsonNode? Formatted(JsonObject p)
        {
            string uri = (string)p["textDocument"]!["uri"]!;
            if (!documents.TryGetValue(PhiCompiler.FileKey(PathOf(uri)), out Document? document)) return null;
            string? laid = Phi.Compiler.Language.Formatter.Format(new SourceFile(document.Path, document.Text), new DiagnosticBag());
            if (laid == null || laid == document.Text) return new JsonArray();
            int lastLine = document.Text.Count(c => c == '\n');
            int lastLineLength = document.Text.Length - (document.Text.LastIndexOf('\n') + 1);
            return new JsonArray(new JsonObject
            {
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                    ["end"] = new JsonObject { ["line"] = lastLine, ["character"] = lastLineLength },
                },
                ["newText"] = laid,
            });
        }

        JsonNode? Definition(JsonObject p)
        {
            if (Place(p) is not var (document, offset) || document.Index == null) return null;

            if (document.Index.UsedFileAt(offset) is SourceFile used)
                return LocationOf(used, new Span(0, 0));

            ProgramIndex index = IndexAround(document, offset);
            NameInfo? name = index.DefinitionAt(offset);
            return name?.File == null ? null : LocationOf(name.File, name.Span);
        }

        JsonNode Completion(JsonObject p)
        {
            var items = new JsonArray();
            if (Place(p) is var (document, offset) && document.Index != null)
                foreach (NameInfo name in IndexAround(document, offset).CompletionsAt(offset))
                {
                    var item = new JsonObject { ["label"] = name.Name, ["kind"] = KindOf(name.Kind) };
                    if (name.Detail != "") item["detail"] = name.Detail;
                    items.Add(item);
                }
            return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
        }

        /// <summary>
        /// The index for a place. Where the file doesn't parse (a class not closed yet is left out
        /// altogether, and a module with an error isn't bound, so it has no types), what was known
        /// about it last time is better than knowing less.
        /// </summary>
        static ProgramIndex IndexAround(Document document, int offset) =>
            document.LastGood == null || document.Index!.Knows(offset) && document.Result?.Bound != null
                ? document.Index!
                : new ProgramIndex(document.LastGood, document.File!, earlier: true);

        /// <summary>The numbers the protocol has for kinds of completion.</summary>
        static int KindOf(NameKind kind) => kind switch
        {
            NameKind.Method => 2,
            NameKind.Function or NameKind.OutsideFunction or NameKind.SystemCall or NameKind.AssemblyBlock => 3,
            NameKind.Field => 5,
            NameKind.Variable or NameKind.Parameter => 6,
            NameKind.Class => 7,
            NameKind.Interface => 8,
            NameKind.Group => 9,
            NameKind.Length => 10,
            NameKind.Enum => 13,
            NameKind.Keyword => 14,
            NameKind.EnumValue => 20,
            NameKind.Constant => 21,
            NameKind.Struct => 22,
            NameKind.Type => 25,
            _ => 1,
        };

        // ------------------------------------------------------------ places in files

        JsonNode? LocationOf(SourceFile file, Span span)
        {
            string? uri = UriOf(file);
            return uri == null ? null : new JsonObject { ["uri"] = uri, ["range"] = RangeOf(file, span) };
        }

        /// <summary>
        /// A span something can be underlined in. "expected ';'" is at the place between two
        /// tokens, with no length: it's about the token that ends there, or else the one that starts there.
        /// </summary>
        static Span Visible(SourceFile file, Span span)
        {
            if (span.Length > 0) return span;
            Token? starting = null;
            foreach (Token t in new Lexer(file, new DiagnosticBag()).Lex())
            {
                if (t.Kind == TokenKind.EndOfFile) break;
                if (t.Span.End == span.Start && t.Span.Length > 0) return t.Span;
                if (t.Span.Start == span.Start && t.Span.Length > 0) starting ??= t;
            }
            return starting?.Span ?? span;
        }

        static JsonObject RangeOf(SourceFile file, Span span)
        {
            int start = Math.Min(span.Start, file.Text.Length);
            int end = Math.Min(span.End, file.Text.Length);
            return new JsonObject { ["start"] = PositionOf(file, start), ["end"] = PositionOf(file, end) };
        }

        static JsonObject PositionOf(SourceFile file, int offset)
        {
            var (line, column) = file.GetLocation(offset);
            return new JsonObject { ["line"] = line - 1, ["character"] = column - 1 };
        }

        /// <summary>A file's address for the editor; an open file's is the one the editor gave.</summary>
        string? UriOf(SourceFile file)
        {
            if (documents.TryGetValue(PhiCompiler.FileKey(file.Path), out Document? open)) return open.Uri;
            return UriOfDiskFile(file);
        }

        /// <summary>
        /// The standard library is inside phi: a file of it has an address only as the copy in
        /// the folder being worked on, when that's the same text, or else one written out for
        /// reading (it's not the same text when lib/ has changed since phi was built).
        /// </summary>
        string? UriOfDiskFile(SourceFile file)
        {
            if (!file.Path.StartsWith("lib/"))
                return System.IO.Path.IsPathFullyQualified(file.Path) ? new Uri(file.Path).AbsoluteUri : new Uri(System.IO.Path.GetFullPath(file.Path)).AbsoluteUri;

            foreach (string folder in workspaceFolders)
            {
                string copy = System.IO.Path.Combine(folder, file.Path);
                if (File.Exists(copy) && File.ReadAllText(copy) == file.Text) return new Uri(copy).AbsoluteUri;
            }

            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(file.Text)))[..16];
            string written = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "phi-lib", hash, file.Path);
            if (!File.Exists(written))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(written)!);
                File.WriteAllText(written, file.Text);
                // it's for reading: changing it wouldn't change anything
                File.SetAttributes(written, FileAttributes.ReadOnly);
            }
            return new Uri(written).AbsoluteUri;
        }

        /// <summary>
        /// A file's path from its address. Uri.LocalPath won't do: editors write a drive's colon
        /// as %3A (file:///c%3A/labs), which it takes for part of a folder's name.
        /// </summary>
        internal static string PathOf(string uri)
        {
            var parsed = new Uri(uri);
            string path = Uri.UnescapeDataString(parsed.AbsolutePath);
            if (!OperatingSystem.IsWindows()) return path;
            if (parsed.Host != "") return $@"\\{parsed.Host}{path.Replace('/', '\\')}";
            return (path.Length > 2 && path[0] == '/' && path[2] == ':' ? path[1..] : path).Replace('/', '\\');
        }

        // ------------------------------------------------------------ the wire

        /// <summary>One message: Content-Length: n, a blank line, then n bytes of JSON. Null at the end of the input.</summary>
        JsonObject? ReadMessage()
        {
            int length = -1;
            while (true)
            {
                string? header = ReadHeaderLine();
                if (header == null) return null;
                if (header.Length == 0) break;
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(header["Content-Length:".Length..].Trim());
            }
            if (length < 0) throw new IOException("a message without a Content-Length");

            var body = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = input.Read(body, read, length - read);
                if (n == 0) return null;
                read += n;
            }
            return JsonNode.Parse(body) as JsonObject;
        }

        string? ReadHeaderLine()
        {
            var line = new StringBuilder();
            while (true)
            {
                int b = input.ReadByte();
                if (b < 0) return null;
                if (b == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)b);
            }
        }

        void Reply(JsonNode id, JsonNode? result) =>
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });

        void ReplyError(JsonNode id, int code, string message) =>
            Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            });

        void Send(JsonObject message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            output.Write(header);
            output.Write(body);
            output.Flush();
        }
    }
}

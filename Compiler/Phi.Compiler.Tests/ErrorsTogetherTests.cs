namespace Phi.Compiler.Tests
{
    /// <summary>
    /// One build shows the syntax errors and the other mistakes together (PhiCompiler's
    /// NotFromSyntaxErrors), but never an error that a line the parser gave up on could have caused.
    /// </summary>
    public class ErrorsTogetherTests
    {
        static List<string> Errors(string text, bool syntaxOnly = false) =>
            PhiCompiler.Compile(new SourceFile("test.phi", text), generate: false, host: Host.Linux, bindAnyway: syntaxOnly)
                .Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList();

        const string Mistakes = """
            phi.Mistakes:Program
            {
            	int count: 5;
            	str name: 'abc';
            	int total: count + ;
            	log total '\n';

            	ptr<u8> p: (Name);
            	missing is 4;
            	log (Twice: 2 3) '\n';

            	[Name]
            	[end: 'text']

            	[Twice: int n: 0;]
            	[end: n * 2]
            }
            """;

        [Fact]
        public void TheOtherMistakesShowBesideASyntaxError()
        {
            List<string> errors = Errors(Mistakes);
            Assert.Contains(errors, e => e.Contains(":5:") && e.Contains("expected a value"));
            Assert.Contains(errors, e => e.Contains(":8:") && e.Contains("can't keep a pointer to it"));
            Assert.Contains(errors, e => e.Contains(":9:") && e.Contains("unknown name 'missing'"));
            Assert.Contains(errors, e => e.Contains(":10:") && e.Contains("takes 1 argument but was given 2"));
            // the line that didn't parse declared total: using it isn't a second mistake
            Assert.DoesNotContain(errors, e => e.Contains("'total'"));
            Assert.Equal(4, errors.Count);
        }

        [Theory]
        [InlineData("\t[Twice: int n: 0;]", "\t[Twice: int n: 0]")]        // a method's heading
        [InlineData("phi.Mistakes:Program", "phi.Mistakes Program")]       // a class's
        public void AnErrorInWhatOtherCodeIsLaidOutByShowsAlone(string line, string broken)
        {
            string text = Mistakes.Replace("\tint total: count + ;", "\tint total: count;").Replace(line, broken);
            Assert.Equal(Errors(text, syntaxOnly: true), Errors(text));
        }

        [Fact]
        public void WhatADeclarationThatDidntParseWouldHaveNamedIsntASecondMistake()
        {
            string text = Mistakes.Replace("\tint total: count + ;", "\tint total: count;").Replace("\tint count: 5;", "\tint count 5;");
            List<string> errors = Errors(text);
            Assert.DoesNotContain(errors.Skip(1), e => e.Contains("'count'") || e.Contains("'total'"));
            Assert.Contains(errors, e => e.Contains("unknown name 'missing'"));
            Assert.Equal(4, errors.Count);
        }

        [Fact]
        public void AnEditorStillGetsTheSyntaxErrorsAlone()
        {
            Assert.Single(Errors(Mistakes, syntaxOnly: true));
        }

        /// <summary>
        /// Programs with nothing wrong, each with one line spoiled at a time: whatever the build
        /// says beyond the syntax errors would be a mistake that isn't there.
        /// </summary>
        [Fact]
        public void ASpoiledLineInAGoodProgramBringsNoOtherErrors()
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "PHI.sln"))) root = Path.GetDirectoryName(root)!;
            var random = new Random(7);
            int spoiled = 0;
            foreach (string path in Directory.GetFiles(Path.Combine(root, "tests"), "*_program.phi").OrderBy(p => p, StringComparer.Ordinal).Take(30))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string text = File.ReadAllText(path).Replace("\r\n", "\n");
                Host host = text.Contains(":Program") ? Host.Linux : Host.PhiOS;
                // (only a program that builds by itself, with nothing wrong)
                if (PhiCompiler.Compile(new SourceFile(path, text), generate: false, host: host).Diagnostics.Any(d => d.Severity == Severity.Error)) continue;
                string[] lines = text.Split('\n');
                for (int round = 0; round < 24; round++)
                {
                    int at = random.Next(lines.Length);
                    string line = lines[at];
                    if (line.Trim().Length < 3 || line.TrimStart().StartsWith('#')) continue;
                    string changed = (round % 4) switch
                    {
                        0 => line.Replace(";", ""),
                        1 => line + " + ;",
                        2 => line.Replace(":", " "),
                        _ => line.Replace("(", ""),
                    };
                    if (changed == line) continue;
                    var copy = (string[])lines.Clone();
                    copy[at] = changed;
                    string mutated = string.Join("\n", copy);
                    var alone = PhiCompiler.Compile(new SourceFile(path, mutated), generate: false, host: host, bindAnyway: true)
                        .Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList();
                    if (alone.Count == 0) continue;   // it still parsed: what it says is its own
                    var both = PhiCompiler.Compile(new SourceFile(path, mutated), generate: false, host: host)
                        .Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList();
                    Assert.True(alone.SequenceEqual(both), $"{name}.phi line {at + 1}: [{line.Trim()}] as [{changed.Trim()}] also says:\n" + string.Join("\n", both.Skip(alone.Count)));
                    spoiled++;
                }
            }
            Assert.True(spoiled > 100, $"only {spoiled} lines were spoiled");
        }
    }
}

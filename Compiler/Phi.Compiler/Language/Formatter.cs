using System.Text;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.Language
{
    /// <summary>
    /// phi fmt: the layout of a file, and nothing that changes what it means. Each line is indented
    /// by what it's inside, with tabs; a line that carries on the one before keeps how much deeper
    /// it is, which is what says it carries on (Parser.AtEndOfValues); comments stay, at the depth
    /// of the code after them; trailing spaces go. In a call with several arguments, one that's an
    /// expression gets parentheses, so a reader doesn't have to count: call Add: x (y + h) w;
    /// What's between the lines of an asm block, a block comment or a long string isn't touched.
    /// </summary>
    public static class Formatter
    {
        /// <summary>The file's text laid out, or null if it doesn't parse (the errors are in diagnostics).</summary>
        public static string? Format(SourceFile file, DiagnosticBag diagnostics)
        {
            var parser = new Parser(file, diagnostics);
            parser.ParseProgram();
            if (diagnostics.HasErrors) return null;

            string text = WithParentheses(file.Text, parser);
            return Layout(text, new Parser(new SourceFile(file.Path, text), new DiagnosticBag()).Tokens);
        }

        // ---------------------------------------------------------------- arguments

        static string WithParentheses(string text, Parser parser)
        {
            IReadOnlyList<Token> tokens = parser.Tokens;
            var edits = new List<(int At, char What)>();
            foreach (var (first, last, argument) in parser.ListedArguments)
            {
                if (argument is not BinaryExpr || Wrapped(tokens, first, last)) continue;
                edits.Add((tokens[first].Span.Start, '('));
                edits.Add((tokens[last].Span.End, ')'));
            }

            var sb = new StringBuilder(text);
            foreach (var (at, what) in edits.Distinct().OrderByDescending(e => e.At).ThenBy(e => e.What == '(' ? 0 : 1))
                sb.Insert(at, what);
            return sb.ToString();
        }

        /// <summary>Do the first and last token make one pair of parentheses? ((a + b) * c isn't: its first closes early.)</summary>
        static bool Wrapped(IReadOnlyList<Token> tokens, int first, int last)
        {
            if (tokens[first].Kind != TokenKind.OpenParen) return false;
            int depth = 0;
            for (int i = first; i <= last; i++)
            {
                if (tokens[i].Kind == TokenKind.OpenParen) depth++;
                else if (tokens[i].Kind == TokenKind.CloseParen && --depth == 0) return i == last;
            }
            return false;
        }

        // ---------------------------------------------------------------- lines

        enum Block { Plain, Interface, Enum }

        static string Layout(string text, IReadOnlyList<Token> tokens)
        {
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            int shift = text.Contains("\r\n") ? 1 : 0;      // (each line before this one is a character longer in text)

            // which tokens start on each line, and which lines start inside one
            var onLine = new List<Token>[lines.Length];
            var inside = new bool[lines.Length];
            for (int i = 0; i < lines.Length; i++) onLine[i] = new();
            var starts = new int[lines.Length];
            for (int i = 1; i < lines.Length; i++) starts[i] = starts[i - 1] + lines[i - 1].Length + 1 + shift;
            int line = 0;
            foreach (Token t in tokens)
            {
                if (t.Kind == TokenKind.EndOfFile) break;
                while (line + 1 < lines.Length && starts[line + 1] <= t.Span.Start) line++;
                onLine[line].Add(t);
                for (int l = line + 1; l < lines.Length && starts[l] < t.Span.End; l++) inside[l] = true;
            }

            var result = new string[lines.Length];
            var waiting = new List<int>();      // comment lines, for the code after them to place
            var blocks = new Stack<Block>();
            int depth = 0, brackets = 0, headWidth = 0, headTabs = 0;
            bool terminated = true, opens = false, guard = false, inBlockComment = false;
            string declaring = "";

            void Place(int tabs)
            {
                foreach (int c in waiting) result[c] = new string('\t', Math.Max(0, tabs)) + lines[c].Trim();
                waiting.Clear();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (inside[i] || inBlockComment)
                {
                    result[i] = lines[i];
                    if (inBlockComment && trimmed.Contains('#')) inBlockComment = false;
                    if (inside[i]) Respond(onLine[i], i);
                    continue;
                }
                if (onLine[i].Count == 0)
                {
                    if (trimmed.Length == 0) result[i] = "";
                    else if (trimmed == "#")
                    {
                        inBlockComment = true;
                        result[i] = lines[i];
                    }
                    else waiting.Add(i);
                    continue;
                }

                Token first = onLine[i][0];
                bool afterRaw = first.Kind == TokenKind.CloseBrace && i > 0 && inside[i];
                bool closer = first.Kind is TokenKind.CloseBrace or TokenKind.DoubleSemicolon || AtMethodEnd(onLine[i], 0);
                int width = WidthOf(lines[i]);

                int tabsHere;
                bool continues = brackets > 0 || !terminated && !closer && first.Kind != TokenKind.OpenBrace && width > headWidth;
                if (continues)
                {
                    tabsHere = headTabs + Math.Max(1, (width - headWidth + 3) / 4);
                    Place(tabsHere);
                }
                else
                {
                    if (opens) depth++;
                    opens = guard = false;
                    if (closer) depth = Math.Max(0, depth - 1);
                    tabsHere = depth;
                    headWidth = width;
                    headTabs = tabsHere;
                    Place(closer ? tabsHere + 1 : tabsHere);

                    if (first.Kind == TokenKind.Identifier)
                    {
                        if (first.Text is "if" or "elif") opens = guard = true;
                        else if (first.Text is "while" or "when" or "unsafe" or "locked" or "else" or "is") opens = true;
                        declaring = first.Text;
                    }
                    else if (first.Kind == TokenKind.OpenBracket && !AtMethodEnd(onLine[i], 0))
                        opens = blocks.Count == 0 || blocks.Peek() != Block.Interface;
                }

                result[i] = new string('\t', tabsHere) + trimmed;
                Respond(onLine[i], i);
            }
            Place(0);

            void Respond(List<Token> on, int index)
            {
                for (int k = 0; k < on.Count; k++)
                {
                    Token t = on[k];
                    switch (t.Kind)
                    {
                        case TokenKind.OpenParen or TokenKind.OpenBracket: brackets++; break;
                        case TokenKind.CloseParen or TokenKind.CloseBracket: brackets = Math.Max(0, brackets - 1); break;
                        case TokenKind.OpenBrace:
                            if (k + 1 < on.Count && on[k + 1].Kind == TokenKind.RawBlock) break;
                            if (Next(tokens, t)?.Kind == TokenKind.RawBlock) break;
                            blocks.Push(declaring == "interface" ? Block.Interface : declaring == "enum" ? Block.Enum : Block.Plain);
                            depth++;
                            break;
                        case TokenKind.CloseBrace:
                            if (Previous(tokens, t)?.Kind == TokenKind.RawBlock) break;
                            if (blocks.Count > 0) blocks.Pop();
                            if (k > 0) depth = Math.Max(0, depth - 1);
                            break;
                        case TokenKind.DoubleSemicolon:
                            if (k > 0)
                            {
                                if (opens) opens = false;
                                else depth = Math.Max(0, depth - 1);
                            }
                            break;
                    }
                }

                Token last = on.Count > 0 ? on[^1] : null!;
                if (last == null) return;
                if (guard && brackets == 0 && last.Kind == TokenKind.Semicolon) opens = guard = false;
                terminated = brackets == 0 && (opens || blocks.Count > 0 && blocks.Peek() == Block.Enum
                    || last.Kind is TokenKind.Semicolon or TokenKind.DoubleSemicolon or TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.RawBlock or TokenKind.CloseBracket);
            }

            // one newline at the end
            int end = result.Length;
            while (end > 0 && result[end - 1].Length == 0) end--;
            return string.Join(newline, result.Take(end)) + newline;
        }

        static Token? Next(IReadOnlyList<Token> tokens, Token t)
        {
            int at = IndexOf(tokens, t);
            return at + 1 < tokens.Count ? tokens[at + 1] : null;
        }

        static Token? Previous(IReadOnlyList<Token> tokens, Token t)
        {
            int at = IndexOf(tokens, t);
            return at > 0 ? tokens[at - 1] : null;
        }

        static int IndexOf(IReadOnlyList<Token> tokens, Token t)
        {
            int low = 0, high = tokens.Count - 1;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (tokens[middle].Span.Start < t.Span.Start) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        static bool AtMethodEnd(List<Token> on, int k) =>
            on[k].Kind == TokenKind.OpenBracket && k + 1 < on.Count && on[k + 1].IsWord("end");

        /// <summary>The width of a line's leading spaces and tabs, as the parser counts them (a tab is 4).</summary>
        static int WidthOf(string line)
        {
            int width = 0;
            foreach (char c in line)
            {
                if (c == '\t') width += 4;
                else if (c == ' ') width++;
                else break;
            }
            return width;
        }
    }
}

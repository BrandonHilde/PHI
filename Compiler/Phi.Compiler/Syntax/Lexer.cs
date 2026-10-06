using System.Globalization;
using System.Text;

namespace Phi.Compiler.Syntax
{
    /// <summary>
    /// Turns PHI source into tokens.
    ///
    /// Comments: '#' starts a comment to the end of the line. A '#' with nothing else on its
    /// line opens a block comment that runs to the next '#'.
    ///
    /// Strings: 'text'. A quote followed directly by a letter or digit does not end the
    /// string, so 'hasn't' works without escaping. Escapes: \r \n \t \0 \\ \'
    ///
    /// Numbers: 42, 0x2A, 101010b (binary), 3.14
    /// </summary>
    public sealed class Lexer
    {
        readonly SourceFile file;
        readonly string text;
        readonly DiagnosticBag diagnostics;
        int pos;
        bool sawNewline;

        public Lexer(SourceFile file, DiagnosticBag diagnostics)
        {
            this.file = file;
            this.diagnostics = diagnostics;
            // a leading byte-order mark is common in files saved by Visual Studio
            text = file.Text;
            if (text.StartsWith('﻿')) pos = 1;
        }

        public List<Token> Lex()
        {
            var tokens = new List<Token>();
            sawNewline = true;

            while (true)
            {
                SkipTrivia();
                Token token = Next();
                tokens.Add(token);

                // asm.Name { ... } and arm.Name { ... } hold raw assembly; capture it untouched
                if (token.Kind == TokenKind.OpenBrace && IsRawBlockHeader(tokens))
                    tokens.Add(ReadRawBlock());

                if (token.Kind == TokenKind.EndOfFile) return tokens;
            }
        }

        static bool IsRawBlockHeader(List<Token> t)
        {
            int n = t.Count;
            return n >= 4
                && t[n - 2].Kind == TokenKind.Identifier
                && t[n - 3].Kind == TokenKind.Dot
                && (t[n - 4].IsWord("asm") || t[n - 4].IsWord("arm"));
        }

        Token ReadRawBlock()
        {
            int start = pos;
            int depth = 1;
            while (pos < text.Length)
            {
                if (text[pos] == '{') depth++;
                else if (text[pos] == '}' && --depth == 0) break;
                pos++;
            }

            if (pos >= text.Length)
                diagnostics.Error(file, new Span(start - 1, 1), "assembly block is missing its closing '}'");

            string raw = text[start..pos];
            return new Token(TokenKind.RawBlock, Span.FromBounds(start, pos), raw, raw, false);
        }

        char Peek(int ahead = 0) => pos + ahead < text.Length ? text[pos + ahead] : '\0';

        void SkipTrivia()
        {
            while (pos < text.Length)
            {
                char c = text[pos];

                if (c == '\n') { sawNewline = true; pos++; }
                else if (char.IsWhiteSpace(c)) pos++;
                else if (c == '#') SkipComment();
                else break;
            }
        }

        void SkipComment()
        {
            int start = pos;
            pos++; // '#'

            // a '#' alone on its line starts a block comment
            int scan = pos;
            while (scan < text.Length && text[scan] != '\n' && char.IsWhiteSpace(text[scan])) scan++;
            bool block = scan >= text.Length || text[scan] == '\n';

            if (block)
            {
                int close = text.IndexOf('#', scan);
                if (close < 0)
                {
                    diagnostics.Error(file, new Span(start, 1), "block comment is never closed (expected a closing '#')");
                    pos = text.Length;
                    return;
                }

                if (text.IndexOf('\n', start, close - start) >= 0) sawNewline = true;
                pos = close + 1;

                // the closing '#' line may carry a trailing remark
                while (pos < text.Length && text[pos] != '\n') pos++;
                return;
            }

            while (pos < text.Length && text[pos] != '\n') pos++;
        }

        Token Make(TokenKind kind, int start, object? value = null)
        {
            var token = new Token(kind, Span.FromBounds(start, pos), text[start..pos], value, sawNewline);
            sawNewline = false;
            return token;
        }

        Token Next()
        {
            int start = pos;
            if (pos >= text.Length) return Make(TokenKind.EndOfFile, start);

            char c = text[pos];

            if (char.IsLetter(c) || c == '_')
            {
                while (char.IsLetterOrDigit(Peek()) || Peek() == '_') pos++;
                return Make(TokenKind.Identifier, start);
            }

            if (char.IsDigit(c)) return ReadNumber();
            if (c == '\'') return ReadString();

            pos++;
            TokenKind kind;
            switch (c)
            {
                case '{': kind = TokenKind.OpenBrace; break;
                case '}': kind = TokenKind.CloseBrace; break;
                case '[': kind = TokenKind.OpenBracket; break;
                case ']': kind = TokenKind.CloseBracket; break;
                case '(': kind = TokenKind.OpenParen; break;
                case ')': kind = TokenKind.CloseParen; break;
                case ':': kind = TokenKind.Colon; break;
                case '.': kind = TokenKind.Dot; break;
                case ';': kind = Match(';') ? TokenKind.DoubleSemicolon : TokenKind.Semicolon; break;
                case '+': kind = Match('+') ? TokenKind.PlusPlus : TokenKind.Plus; break;
                case '-': kind = Match('-') ? TokenKind.MinusMinus : TokenKind.Minus; break;
                case '*': kind = Match('*') ? TokenKind.StarStar : TokenKind.Star; break;
                case '/': kind = Match('/') ? TokenKind.SlashSlash : TokenKind.Slash; break;
                case '%': kind = Match('%') ? TokenKind.PercentPercent : TokenKind.Percent; break;
                case '^': kind = Match('^') ? TokenKind.CaretCaret : TokenKind.Caret; break;
                case '&': kind = TokenKind.Ampersand; break;
                case '|': kind = TokenKind.Pipe; break;
                case '~': kind = TokenKind.Tilde; break;
                case '=': kind = Match('=') ? TokenKind.EqualsEquals : TokenKind.Equals; break;
                case '!': kind = Match('=') ? TokenKind.BangEquals : TokenKind.Bang; break;
                case '<':
                    kind = Match('=') ? TokenKind.LessEquals : Match('<') ? TokenKind.LessLess : TokenKind.Less;
                    break;
                case '>':
                    kind = Match('=') ? TokenKind.GreaterEquals : Match('>') ? TokenKind.GreaterGreater : TokenKind.Greater;
                    break;
                default:
                    diagnostics.Error(file, new Span(start, 1), $"unexpected character '{c}'");
                    kind = TokenKind.Bad;
                    break;
            }

            return Make(kind, start);
        }

        bool Match(char expected)
        {
            if (Peek() != expected) return false;
            pos++;
            return true;
        }

        Token ReadNumber()
        {
            int start = pos;

            if (Peek() == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
            {
                pos += 2;
                while (Uri.IsHexDigit(Peek()) || Peek() == '_') pos++;
                string hex = text[(start + 2)..pos].Replace("_", "");
                return MakeInteger(start, hex, 16);
            }

            while (char.IsDigit(Peek()) || Peek() == '_') pos++;

            // 0101b is binary, as long as the 'b' isn't the start of a longer word
            if (Peek() == 'b' && !char.IsLetterOrDigit(Peek(1)) && Peek(1) != '_')
            {
                string digits = text[start..pos].Replace("_", "");
                pos++;
                if (digits.All(d => d == '0' || d == '1')) return MakeInteger(start, digits, 2);

                diagnostics.Error(file, Span.FromBounds(start, pos), "binary numbers may only contain 0 and 1");
                return Make(TokenKind.Number, start, 0L);
            }

            if (Peek() == '.' && char.IsDigit(Peek(1)))
            {
                pos++;
                while (char.IsDigit(Peek())) pos++;
                double d = double.Parse(text[start..pos], CultureInfo.InvariantCulture);
                return Make(TokenKind.Decimal, start, d);
            }

            return MakeInteger(start, text[start..pos].Replace("_", ""), 10);
        }

        Token MakeInteger(int start, string digits, int radix)
        {
            long value = 0;
            bool overflow = digits.Length == 0;
            foreach (char d in digits)
            {
                value = value * radix + Convert.ToInt32(d.ToString(), 16);
                if (value > uint.MaxValue) overflow = true;
            }

            if (overflow)
                diagnostics.Error(file, Span.FromBounds(start, pos), "number is not a valid 32-bit value");

            return Make(TokenKind.Number, start, value);
        }

        Token ReadString()
        {
            int start = pos;
            pos++; // opening quote
            var sb = new StringBuilder();

            while (true)
            {
                if (pos >= text.Length)
                {
                    diagnostics.Error(file, new Span(start, 1), "string is never closed (expected ')");
                    break;
                }

                char c = text[pos++];

                if (c == '\\' && pos < text.Length)
                {
                    char e = text[pos++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '0': sb.Append('\0'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        default:
                            diagnostics.Error(file, new Span(pos - 2, 2), $"unknown escape '\\{e}'");
                            break;
                    }
                    continue;
                }

                // a quote directly followed by a letter or digit is an apostrophe (hasn't)
                if (c == '\'' && !char.IsLetterOrDigit(Peek())) break;

                if (c == '\r' && Peek() == '\n') continue; // keep multi-line strings as \n only
                sb.Append(c);
            }

            return Make(TokenKind.String, start, sb.ToString());
        }
    }
}

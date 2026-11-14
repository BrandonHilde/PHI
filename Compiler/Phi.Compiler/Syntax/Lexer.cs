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
    /// string, so 'hasn't' works without escaping. Escapes: \r \n \t \0 \\ \' \xNN
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
                && (t[n - 4].IsWord("asm") || t[n - 4].IsWord("x64") || t[n - 4].IsWord("arm"));
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
            if (c == '\'')
            {
                Token read = ReadString();
                if (Peek() == '\'')
                    diagnostics.Error(file, new Span(pos, 1), "a string has just ended here, so this quote starts a second one; for a quote inside a string, write it once ('can't') or as \\' (put a space between them for two strings)");
                return read;
            }

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

        /// <summary>The character after the digits that start `ahead` characters on.</summary>
        char PeekPastDigits(int ahead)
        {
            while (char.IsDigit(Peek(ahead))) ahead++;
            return Peek(ahead);
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

            // 3.14, 6.02e23, 1e-5: a number with a point or an exponent is floating point
            bool point = Peek() == '.' && char.IsDigit(Peek(1));
            if (point)
            {
                pos++;
                while (char.IsDigit(Peek()) || Peek() == '_') pos++;
            }
            int sign = Peek(1) is '+' or '-' ? 1 : 0;
            bool exponent = Peek() is 'e' or 'E' && char.IsDigit(Peek(1 + sign)) && !char.IsLetter(PeekPastDigits(1 + sign));
            if (exponent)
            {
                pos += 1 + sign;
                while (char.IsDigit(Peek())) pos++;
            }
            if (point || exponent)
            {
                double d = double.Parse(text[start..pos].Replace("_", ""), CultureInfo.InvariantCulture);
                return Make(TokenKind.Decimal, start, d);
            }

            return MakeInteger(start, text[start..pos].Replace("_", ""), 10);
        }

        /// <summary>A whole number of up to 64 bits: a long, or a ulong past what a long holds.</summary>
        Token MakeInteger(int start, string digits, int radix)
        {
            ulong value = 0;
            bool overflow = digits.Length == 0;
            foreach (char d in digits)
            {
                ulong digit = (ulong)Convert.ToInt32(d.ToString(), 16);
                if (value > (ulong.MaxValue - digit) / (ulong)radix) overflow = true;
                else value = value * (ulong)radix + digit;
            }

            if (overflow)
                diagnostics.Error(file, Span.FromBounds(start, pos), "number is not a valid 64-bit value");

            return value > long.MaxValue ? Make(TokenKind.Number, start, value) : Make(TokenKind.Number, start, (long)value);
        }

        /// <summary>A character as the bytes of its UTF-8, one per char.</summary>
        static void AppendUtf8(StringBuilder sb, Rune rune)
        {
            Span<byte> bytes = stackalloc byte[4];
            int count = rune.EncodeToUtf8(bytes);
            for (int i = 0; i < count; i++) sb.Append((char)bytes[i]);
        }

        Token ReadString()
        {
            int start = pos;
            bool notText = false;
            pos++; // opening quote
            var sb = new StringBuilder();   // the string's bytes, one per char: text past ASCII as its UTF-8

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
                        case 'x':
                            // any byte by its two hex digits: '\xED' (φ in code page 437)
                            if (pos + 1 < text.Length && Uri.IsHexDigit(text[pos]) && Uri.IsHexDigit(text[pos + 1]))
                            {
                                sb.Append((char)System.Convert.ToInt32(text.Substring(pos, 2), 16));
                                pos += 2;
                            }
                            else
                                diagnostics.Error(file, new Span(pos - 2, 2), "\\x needs two hex digits after it, like '\\x41'");
                            break;
                        case 'u' or 'U':
                        {
                            // any character by its number, as C writes it: '\u00E9' (é), '\U0001F600'
                            int digits = e == 'u' ? 4 : 8;
                            if (pos + digits <= text.Length && text.AsSpan(pos, digits).ToArray().All(Uri.IsHexDigit))
                            {
                                uint code = System.Convert.ToUInt32(text.Substring(pos, digits), 16);
                                if (code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
                                    diagnostics.Error(file, new Span(pos - 2, 2 + digits), $"there's no character {code:X}: they run from 0 to 10FFFF, without D800 to DFFF");
                                else AppendUtf8(sb, new Rune(code));
                                pos += digits;
                            }
                            else
                                diagnostics.Error(file, new Span(pos - 2, 2), e == 'u'
                                    ? "\\u needs four hex digits after it, like '\\u00E9'; a character past FFFF is \\U and eight, like '\\U0001F600'"
                                    : "\\U needs eight hex digits after it, like '\\U0001F600'; \\u takes four, like '\\u00E9'");
                            break;
                        }
                        default:
                            diagnostics.Error(file, new Span(pos - 2, 2), $"unknown escape '\\{e}'");
                            break;
                    }
                    continue;
                }

                // a quote directly followed by a letter or digit is an apostrophe (hasn't)
                if (c == '\'' && !char.IsLetterOrDigit(Peek())) break;

                if (c == '\r' && Peek() == '\n') continue; // keep multi-line strings as \n only

                // a character past ASCII is in the string as the bytes of its UTF-8
                if (c > 127)
                {
                    if (Rune.TryGetRuneAt(text, pos - 1, out Rune rune) && rune.Value != 0xFFFD)
                    {
                        AppendUtf8(sb, rune);
                        pos += rune.Utf16SequenceLength - 1;
                    }
                    else
                    {
                        // (what a file's bytes that aren't UTF-8 are read as)
                        if (!notText)
                            diagnostics.Error(file, new Span(pos - 1, 1),
                                "this isn't a character: the file has to be saved as UTF-8 for text past ASCII (for the character that stands for one that's missing, write \\uFFFD)");
                        notText = true;
                    }
                    continue;
                }
                sb.Append(c);
            }

            // a line break written into the string just before its end is more often a '\n' that
            // an edit split across two lines than a multi-line string
            if (pos >= 2 && text[pos - 1] == '\'' && text[pos - 2] == '\n')
                diagnostics.Warning(file, new Span(start, 1),
                    "this string ends with a line break written into it; for a new line write '\\n' (if the break is meant, end the string on the same line as its text)");

            return Make(TokenKind.String, start, sb.ToString());
        }
    }
}

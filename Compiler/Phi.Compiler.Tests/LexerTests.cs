using Phi.Compiler.Syntax;

namespace Phi.Compiler.Tests
{
    public class LexerTests
    {
        static List<Token> Lex(string text, out DiagnosticBag diagnostics)
        {
            diagnostics = new DiagnosticBag();
            return new Lexer(new SourceFile("test.phi", text), diagnostics).Lex();
        }

        static List<Token> Lex(string text)
        {
            var tokens = Lex(text, out var diagnostics);
            Assert.Empty(diagnostics.Items);
            return tokens;
        }

        [Theory]
        [InlineData("42", 42L)]
        [InlineData("0x2A", 42L)]
        [InlineData("101010b", 42L)]
        [InlineData("1_000", 1000L)]
        public void Numbers(string text, long value)
        {
            Token t = Lex(text)[0];
            Assert.Equal(TokenKind.Number, t.Kind);
            Assert.Equal(value, t.Value);
        }

        [Fact]
        public void AStringEndingInALineBreakWarns()
        {
            // most likely a '\n' that an edit split across two lines
            Lex("log 'done\n';", out var split);
            Assert.Contains(split.Items, d => d.Severity == Severity.Warning && d.Message.Contains("ends with a line break"));

            // text on both lines is a multi-line string, meant as one
            Token t = Lex("'one\ntwo'")[0];
            Assert.Equal("one\ntwo", t.Value);
        }

        /// <summary>A string's value is its bytes, one per char: text past ASCII as its UTF-8.</summary>
        static string Bytes(string text) => string.Concat(System.Text.Encoding.UTF8.GetBytes(text).Select(b => (char)b));

        [Theory]
        [InlineData("'héllo'", "héllo")]
        [InlineData("'日本'", "日本")]
        [InlineData("'ok 😀'", "ok 😀")]
        [InlineData("'caf\\u00E9'", "café")]            // by its number, as C writes it
        [InlineData("'\\u00e9\\u65E5'", "é日")]
        [InlineData("'\\U0001F600!'", "😀!")]
        [InlineData("'\\u0041\\U00000042'", "AB")]
        [InlineData("'\\uFFFD'", "\uFFFD")]              // the character for one that's missing, when it's meant
        public void AStringPastAsciiIsItsUtf8(string text, string expected)
        {
            Token t = Lex(text, out var diagnostics)[0];
            Assert.Empty(diagnostics.Items);
            Assert.Equal(Bytes(expected), t.Value);
        }

        [Theory]
        [InlineData("'\\u12'", "four hex digits")]
        [InlineData("'\\u00G9'", "four hex digits")]
        [InlineData("'\\U1F600'", "eight hex digits")]
        [InlineData("'\\uD800'", "no character D800")]         // half of a UTF-16 pair
        [InlineData("'\\U00110000'", "no character 110000")]   // past the last one
        [InlineData("'a\uFFFDb'", "saved as UTF-8", true)]    // what bytes that aren't UTF-8 are read as
        public void ACharacterThatIsntOneIsAnError(string text, string message, bool typed = false)
        {
            Lex(text, out var diagnostics);
            Diagnostic error = Assert.Single(diagnostics.Items);
            Assert.Equal(Severity.Error, error.Severity);
            Assert.Contains(message, error.Message);
        }

        [Fact]
        public void BytesAboveAsciiAreWrittenAsEscapes()
        {
            // one byte, as it is: not the character with that number
            Assert.Equal("\u00ED", Lex("'\\xED'")[0].Value);
            // and a comment can say anything
            Assert.Equal(TokenKind.Number, Lex("# héllo 日本\n1")[0].Kind);
        }

        [Fact]
        public void BinarySuffixNeedsAWordBoundary()
        {
            // "10bytes" is a number followed by a name, not binary
            var tokens = Lex("10 bytes");
            Assert.Equal(10L, tokens[0].Value);
            Assert.True(tokens[1].IsWord("bytes"));
        }

        [Theory]
        [InlineData("'hello'", "hello")]
        [InlineData("'hasn't'", "hasn't")]
        [InlineData(@"'a\r\n\t\\\''", "a\r\n\t\\'")]
        [InlineData("''", "")]
        [InlineData("'one\r\ntwo'", "one\ntwo")]
        [InlineData(@"'\xED \x41\x7f'", "í A\u007F")]
        public void Strings(string text, string value)
        {
            Token t = Lex(text)[0];
            Assert.Equal(TokenKind.String, t.Kind);
            Assert.Equal(value, t.Value);
        }

        [Fact]
        public void AdjacentStringsAreSeparateTokens()
        {
            var tokens = Lex("'one' 'two'");
            Assert.Equal("one", tokens[0].Value);
            Assert.Equal("two", tokens[1].Value);
        }

        [Fact]
        public void LineAndBlockComments()
        {
            var tokens = Lex("a # line comment\n#\n block\n log 'x';\n#\nb");
            Assert.Equal(new[] { "a", "b" }, tokens.Where(t => t.Kind == TokenKind.Identifier).Select(t => t.Text));
            Assert.True(tokens[1].StartsLine);
        }

        [Fact]
        public void HashInsideAStringIsNotAComment()
        {
            Assert.Equal("#not a comment", Lex("'#not a comment'")[0].Value);
        }

        [Theory]
        [InlineData(";;", TokenKind.DoubleSemicolon)]
        [InlineData("++", TokenKind.PlusPlus)]
        [InlineData("--", TokenKind.MinusMinus)]
        [InlineData("<<", TokenKind.LessLess)]
        [InlineData(">=", TokenKind.GreaterEquals)]
        [InlineData("!=", TokenKind.BangEquals)]
        [InlineData("==", TokenKind.EqualsEquals)]
        public void Operators(string text, TokenKind kind)
        {
            Assert.Equal(kind, Lex(text)[0].Kind);
        }

        [Fact]
        public void AsmBlocksAreCapturedRaw()
        {
            var tokens = Lex("asm.Thing\n{\n    mov ax, [{value}] ; # not a comment\n}");
            Token raw = tokens.Single(t => t.Kind == TokenKind.RawBlock);
            Assert.Contains("mov ax, [{value}] ; # not a comment", raw.Text);
            Assert.Equal(TokenKind.CloseBrace, tokens[tokens.IndexOf(raw) + 1].Kind);
        }

        [Fact]
        public void UnterminatedStringIsReported()
        {
            Lex("'oops", out var diagnostics);
            Assert.Contains(diagnostics.Items, d => d.Message.Contains("never closed"));
        }

        [Theory]
        [InlineData("'can''t'")]
        [InlineData("''''")]
        [InlineData("'one''two' 'three'")]
        public void AStringRightAfterAStringIsAnError(string text)
        {
            Lex(text, out var diagnostics);
            Assert.Contains(diagnostics.Items, d => d.Severity == Severity.Error && d.Message.Contains("write it once ('can't')"));
        }

        [Theory]
        [InlineData("'can't'")]
        [InlineData(@"'can\'t'")]
        [InlineData("'one' 'two'")]
        [InlineData("''")]
        [InlineData("'' ''")]
        public void AQuoteInsideAStringOrAfterASpaceIsFine(string text) => Lex(text);

        [Theory]
        [InlineData(@"'\x4'")]
        [InlineData(@"'\xG1'")]
        [InlineData(@"'\x'")]
        public void HexEscapeNeedsTwoDigits(string text)
        {
            Lex(text, out var diagnostics);
            Assert.Contains(diagnostics.Items, d => d.Message.Contains("\\x needs two hex digits"));
        }

        [Fact]
        public void ByteOrderMarkIsIgnored()
        {
            Assert.True(Lex("﻿phi")[0].IsWord("phi"));
        }
    }
}

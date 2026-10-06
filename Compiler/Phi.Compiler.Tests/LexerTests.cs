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

        [Fact]
        public void ByteOrderMarkIsIgnored()
        {
            Assert.True(Lex("﻿phi")[0].IsWord("phi"));
        }
    }
}

namespace Phi.Compiler.Syntax
{
    public enum TokenKind
    {
        EndOfFile,
        Bad,

        Identifier,
        Number,
        Decimal,
        String,
        RawBlock,       // the text between { } after asm.Name / arm.Name

        // punctuation
        OpenBrace, CloseBrace,
        OpenBracket, CloseBracket,
        OpenParen, CloseParen,
        Colon, Semicolon, DoubleSemicolon, Dot,

        // operators
        Plus, Minus, Star, Slash, Percent,
        PlusPlus, MinusMinus, StarStar, SlashSlash, PercentPercent, CaretCaret,
        Equals, EqualsEquals, BangEquals, Bang,
        Less, Greater, LessEquals, GreaterEquals,
        LessLess, GreaterGreater, // PHI spells <= and >= this way as well
    }

    public sealed class Token
    {
        public TokenKind Kind { get; }
        public Span Span { get; }
        public string Text { get; }

        /// <summary>Decoded value: string contents, or the number for Number tokens.</summary>
        public object? Value { get; }

        /// <summary>True when a line break separates this token from the previous one.</summary>
        public bool StartsLine { get; }

        public Token(TokenKind kind, Span span, string text, object? value, bool startsLine)
        {
            Kind = kind;
            Span = span;
            Text = text;
            Value = value;
            StartsLine = startsLine;
        }

        public bool IsWord(string word) => Kind == TokenKind.Identifier && Text == word;

        public override string ToString() => Kind switch
        {
            TokenKind.EndOfFile => "end of file",
            TokenKind.String => $"'{Value}'",
            _ => $"'{Text}'",
        };
    }
}

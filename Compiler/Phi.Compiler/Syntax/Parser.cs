namespace Phi.Compiler.Syntax
{
    /// <summary>
    /// Recursive-descent parser for PHI. See docs/language.md for the grammar.
    /// </summary>
    public sealed class Parser
    {
        /// <summary>Type names that are keywords. Struct names are ordinary identifiers.</summary>
        public static readonly HashSet<string> TypeKeywords = new()
        {
            "str", "int", "byt", "bln", "chr", "dec", "fin", "var",
            "u8", "u16", "u32", "i8", "i16", "i32", "i64", "u64", "f16", "f32", "f64", "ptr", "method", "lock",
        };

        /// <summary>Words that can't be used as variable, method or type names.</summary>
        public static readonly HashSet<string> ReservedWords = new(TypeKeywords)
        {
            "log", "debug", "ask", "call", "exit", "if", "elif", "else", "while", "each", "when", "unsafe",
            "break", "continue", "return",
            "out", "outw", "outd", "in", "inw", "ind", "addr", "sizeof", "new", "free",
            "is", "not", "and", "or", "has", "true", "false", "end", "isr", "as", "takes", "empty",
            "below", "above", "atmost", "atleast", "at",
            "phi", "asm", "x64", "arm", "struct", "enum", "interface", "use", "const", "volatile", "atomic", "locked", "into",
            "throw", "throws", "sys", "foreign",
        };

        /// <summary>Other names for the keywords people most often reach for as variable names.</summary>
        static readonly Dictionary<string, string> KeywordAlternatives = new()
        {
            ["at"] = "'pos' or 'offset'",
            ["end"] = "'stop' or 'last'",
            ["above"] = "'upper' or 'over'",
            ["below"] = "'lower' or 'under'",
            ["atmost"] = "'most' or 'max'",
            ["atleast"] = "'least' or 'min'",
            ["in"] = "'input'",
            ["out"] = "'output'",
            ["free"] = "'spare' or 'unused'",
            ["empty"] = "'blank' or 'none'",
            ["log"] = "'entry' or 'history'",
            ["sizeof"] = "'size'",
            ["lock"] = "'guard' or 'mutex'",
            ["locked"] = "'held' or 'taken'",
            ["atomic"] = "'shared' or 'whole'",
            ["into"] = "'target'",
            ["return"] = "'result'",
            ["ask"] = "'question' or 'prompt'",
            ["has"] = "'holds' or 'found'",
            ["when"] = "'time' or 'moment'",
            ["each"] = "'item' or 'every'",
        };

        /// <summary>The error for a keyword used as a name, with other names to try when there are common ones.</summary>
        public static string KeywordNameMessage(string word, string what) =>
            KeywordAlternatives.TryGetValue(word, out string? alternatives)
                ? $"'{word}' is a keyword and can't be a {what} name; try {alternatives}"
                : $"'{word}' is a keyword and can't be a {what} name";

        sealed class ParseError : Exception { }

        readonly SourceFile file;
        readonly DiagnosticBag diagnostics;
        readonly List<Token> tokens;
        int pos;

        /// <summary>
        /// True while parsing one value of a space-separated list (a declaration's values, call
        /// arguments, log, out), where a spaced '-1' starts the next value.
        /// </summary>
        bool inList;

        /// <summary>
        /// Keywords this file declared as variable or field names. Each was reported where it was
        /// declared, so later uses read as names instead of causing more errors.
        /// </summary>
        readonly HashSet<string> keywordNames = new();

        /// <summary>The program being read, which the uses of structs that take types are noted in.</summary>
        ProgramNode? reading;

        /// <summary>While a struct that takes types is read as itself: its types' names, which stand for no type yet.</summary>
        HashSet<string>? placeholders;

        /// <summary>While such a struct is read for one use: the type each of its names stands for.</summary>
        Dictionary<string, TypeRef>? given;

        public Parser(SourceFile file, DiagnosticBag diagnostics)
        {
            this.file = file;
            this.diagnostics = diagnostics;
            tokens = new Lexer(file, diagnostics).Lex();
        }

        public static ProgramNode Parse(SourceFile file, DiagnosticBag diagnostics) =>
            new Parser(file, diagnostics).ParseProgram();

        /// <summary>For phi fmt: what the file is made of.</summary>
        public IReadOnlyList<Token> Tokens => tokens;

        /// <summary>For phi fmt: each argument of a call that has several, by its first and last token.</summary>
        public List<(int First, int Last, Expr Argument)> ListedArguments { get; } = new();

        /// <summary>An argument of a call, noted for phi fmt.</summary>
        void ParseArgument(List<Expr> arguments, List<(int, int, Expr)> places)
        {
            int first = pos;
            Expr argument = ParseListElement();
            arguments.Add(argument);
            places.Add((first, pos - 1, argument));
        }

        void NoteArguments(List<(int, int, Expr)> places)
        {
            if (places.Count >= 2) ListedArguments.AddRange(places);
        }

        // ------------------------------------------------------------ token helpers

        Token Current => tokens[pos];
        Token PeekToken(int ahead) => tokens[Math.Min(pos + ahead, tokens.Count - 1)];
        Token Previous => tokens[Math.Max(pos - 1, 0)];
        bool At(TokenKind kind) => Current.Kind == kind;
        bool AtWord(string word) => Current.IsWord(word);
        bool AtEnd => At(TokenKind.EndOfFile);

        Token Advance()
        {
            Token t = Current;
            if (!AtEnd) pos++;
            return t;
        }

        bool Accept(TokenKind kind)
        {
            if (!At(kind)) return false;
            Advance();
            return true;
        }

        bool AcceptWord(string word)
        {
            if (!AtWord(word)) return false;
            Advance();
            return true;
        }

        Token Expect(TokenKind kind, string what)
        {
            if (At(kind)) return Advance();
            throw Error(Current.Span, $"expected {what} but found {Current}");
        }

        Token ExpectIdentifier(string what)
        {
            if (At(TokenKind.Identifier)) return Advance();
            throw Error(Current.Span, $"expected {what} but found {Current}");
        }

        /// <summary>Statements end with ';'. If it's missing, report it where the line ended.</summary>
        void ExpectSemicolon()
        {
            if (Accept(TokenKind.Semicolon)) return;
            Error(new Span(Previous.Span.End, 0), $"expected ';' but found {Current}");
            if (!Current.StartsLine) throw new ParseError();
        }

        ParseError Error(Span span, string message)
        {
            diagnostics.Error(file, span, message);
            return new ParseError();
        }

        static bool IsName(Token t) => t.Kind == TokenKind.Identifier && !ReservedWords.Contains(t.Text);

        /// <summary>A declared name that is a keyword: reported here, so the binder never sees it.</summary>
        void CheckNotKeyword(Token name, string what)
        {
            if (!ReservedWords.Contains(name.Text)) return;
            Error(name.Span, KeywordNameMessage(name.Text, what));
            keywordNames.Add(name.Text);
        }

        /// <summary>A declaration starts with a type keyword, or with a struct name followed by a name.</summary>
        bool AtDeclaration =>
            (Current.Kind == TokenKind.Identifier && TypeKeywords.Contains(Current.Text))
            || (IsName(Current) && IsName(PeekToken(1)) && !PeekToken(1).StartsLine)
            || (IsName(Current) && AtKeywordAsDeclaredName)
            || AtTemplateDeclaration();

        /// <summary>
        /// Button free; or Prompt ask: ...: a struct name and a keyword where the variable name goes,
        /// read as a declaration so the keyword is reported as a name.
        /// </summary>
        bool AtKeywordAsDeclaredName
        {
            get
            {
                Token name = PeekToken(1);
                if (name.Kind != TokenKind.Identifier || name.StartsLine || !ReservedWords.Contains(name.Text)) return false;
                if (name.Text is "is" or "takes") return false;
                return PeekToken(2).Kind is TokenKind.Colon or TokenKind.Semicolon or TokenKind.OpenBracket or TokenKind.Equals;
            }
        }

        /// <summary>new T name ... as a statement declares an object (a new expression is never a statement).</summary>
        /// <summary>
        /// A keyword this file declared as a name, used as one (out++ 4;, free:0 is 1;): read as an
        /// assignment, since the declaration was already reported.
        /// </summary>
        bool AtKeywordNameAssignment
        {
            get
            {
                if (Current.Kind != TokenKind.Identifier || !keywordNames.Contains(Current.Text)) return false;
                Token next = PeekToken(1);
                if (next.StartsLine) return false;
                return next.IsWord("is") || next.IsWord("takes") || next.Kind is TokenKind.Equals or TokenKind.PlusPlus
                    or TokenKind.MinusMinus or TokenKind.StarStar or TokenKind.SlashSlash or TokenKind.PercentPercent
                    or TokenKind.CaretCaret or TokenKind.Colon or TokenKind.Dot;
            }
        }

        bool AtNewDeclaration => AtWord("new") && (PeekToken(1).Kind == TokenKind.Identifier && PeekToken(2).Kind == TokenKind.Identifier || AtTemplateDeclaration(1));

        bool AtTopLevelHeader =>
            Current.StartsLine && (AtWord("phi") || AtWord("asm") || AtWord("x64") || AtWord("arm") || AtWord("struct") || AtWord("enum") || AtWord("interface") || AtWord("sys")) && PeekToken(1).Kind == TokenKind.Dot
            || Current.StartsLine && AtWord("use")
            || Current.StartsLine && libraryNames.Contains(Current.Text) && PeekToken(1).Kind == TokenKind.Dot;

        /// <summary>The names this file gives outside libraries (use 'kernel32.dll' as Kernel32;).</summary>
        readonly HashSet<string> libraryNames = new();

        /// <summary>Skip ahead to a likely statement boundary after an error.</summary>
        void Synchronize()
        {
            int start = pos;
            while (!AtEnd)
            {
                if (pos > start && Current.StartsLine) return;
                if (At(TokenKind.Semicolon)) { Advance(); return; }
                if (At(TokenKind.DoubleSemicolon) || At(TokenKind.CloseBrace) || At(TokenKind.OpenBracket)) return;
                Advance();
            }
        }

        // ------------------------------------------------------------ program

        public ProgramNode ParseProgram()
        {
            var program = new ProgramNode { File = file };
            reading = program;

            while (!AtEnd)
            {
                int before = pos;
                try
                {
                    ParseTopLevel(program);
                }
                catch (ParseError)
                {
                    // skip to the next top-level header
                    Advance();
                    while (!AtEnd && !AtTopLevelHeader) Advance();
                }
                if (pos == before) Advance();
            }

            return program;
        }

        void ParseTopLevel(ProgramNode program)
        {
            Token start = Current;

            if (AtWord("use") && PeekToken(1).Kind == TokenKind.String)
            {
                program.Libraries.Add(ParseLibraryUse(start));
                return;
            }

            if (At(TokenKind.Identifier) && libraryNames.Contains(Current.Text) && PeekToken(1).Kind == TokenKind.Dot)
            {
                program.OutsideFunctions.Add(ParseOutsideFunction(start));
                return;
            }

            if (AtWord("use"))
            {
                Advance();
                string path = ParseQualifiedName(out Span span);
                ExpectSemicolon();
                program.Uses.Add(new UseDecl { Path = path, Span = start.Span.To(span) });
                return;
            }

            if ((AtWord("phi") || AtWord("asm") || AtWord("x64") || AtWord("arm") || AtWord("struct") || AtWord("enum") || AtWord("interface") || AtWord("sys")) && PeekToken(1).Kind == TokenKind.Dot)
            {
                string kind = Advance().Text;
                Advance(); // .
                Token name = ExpectIdentifier("a name");

                switch (kind)
                {
                    case "sys":
                        program.SysCalls.Add(ParseSysCall(start, name));
                        return;
                    case "phi":
                        program.Classes.Add(ParseClass(start, name));
                        return;
                    case "struct":
                        if (At(TokenKind.Less)) ParseStructTemplate(start, name, program);
                        else program.Structs.Add(ParseStruct(start, name, program));
                        return;
                    case "enum":
                        program.Enums.Add(ParseEnum(start, name));
                        return;
                    case "interface":
                        program.Structs.Add(ParseInterface(start, name));
                        return;
                }

                Expect(TokenKind.OpenBrace, "'{'");
                Token raw = At(TokenKind.RawBlock) ? Advance() : new Token(TokenKind.RawBlock, new Span(Current.Span.Start, 0), "", "", false);
                Token close = Expect(TokenKind.CloseBrace, "'}'");

                program.RawBlocks.Add(new RawBlockDecl
                {
                    File = file,
                    Language = kind,
                    Name = name.Text,
                    Text = raw.Text,
                    TextSpan = raw.Span,
                    Span = start.Span.To(close.Span),
                });
                return;
            }

            throw Error(Current.Span, $"expected a class (phi.Name:Base {{ ... }}) but found {Current}");
        }

        /// <summary>sys.Name: number  type name  type name ...;  (the 'sys.Name' read already)</summary>
        SysCallDecl ParseSysCall(Token start, Token name)
        {
            CheckNotKeyword(name, "system call");
            Expect(TokenKind.Colon, $"':' and the system call's number, like sys.{name.Text}: 41 int fd;");
            Token number = Current;
            if (number.Kind != TokenKind.Number)
                throw Error(number.Span, $"expected the system call's number after sys.{name.Text}: but found {number}");
            Advance();
            var decl = new SysCallDecl { File = file, Name = name.Text, Number = number.Value is ulong huge ? unchecked((long)huge) : (long)number.Value!, Span = start.Span.To(number.Span) };
            decl.Result = ParseOutsideParameters($"sys.{name.Text}", decl.Parameters);
            return decl;
        }

        /// <summary>
        /// What a system call or outside function takes, up to its ';': type name pairs, then
        /// ': type' for what it gives (null when it doesn't say), as a method type's result is.
        /// </summary>
        TypeRef? ParseOutsideParameters(string what, List<SysCallParameter> parameters)
        {
            TypeRef? result = null;
            while (!At(TokenKind.Semicolon))
            {
                if (AtEnd || Current.StartsLine)
                    throw Error(new Span(Previous.Span.End, 0), $"expected ';' to end {what}");
                if (Accept(TokenKind.Colon))
                {
                    result = ParseType();
                    if (!At(TokenKind.Semicolon))
                        throw Error(Current.Span, $"expected ';' after what {what} gives ({result.Name}), but found {Current}");
                    break;
                }
                TypeRef type = ParseType();
                Token parameter = ExpectIdentifier($"a name for what {what} takes, after its type ({type.Name})");
                CheckNotKeyword(parameter, "parameter");
                parameters.Add(new SysCallParameter(type, parameter.Text, type.Span.To(parameter.Span)));
            }
            Advance();
            return result;
        }

        /// <summary>use 'kernel32.dll' as Kernel32;</summary>
        LibraryUseDecl ParseLibraryUse(Token start)
        {
            Advance(); // use
            Token path = Advance();
            if (!AcceptWord("as"))
                throw Error(Current.Span, $"expected 'as' and a name for the library, like use '{path.Value}' as Kernel32;");
            Token name = ExpectIdentifier("a name for the library, like Kernel32");
            CheckNotKeyword(name, "library");
            if (!char.IsUpper(name.Text[0]))
                Error(name.Span, $"a library's name reads as a class's does, so it starts with a capital: as {char.ToUpper(name.Text[0])}{name.Text[1..]}");
            ExpectSemicolon();
            libraryNames.Add(name.Text);
            return new LibraryUseDecl { File = file, Path = (string)path.Value!, Name = name.Text, Span = start.Span.To(name.Span) };
        }

        /// <summary>Kernel32.CreateThread: type name ...: result;  or  Kernel32.GetCurrentProcessId;</summary>
        OutsideFunctionDecl ParseOutsideFunction(Token start)
        {
            Token library = Advance();
            Advance(); // .
            Token name = ExpectIdentifier($"the name of a function in {library.Text}");
            var decl = new OutsideFunctionDecl { File = file, Library = library.Text, Name = name.Text, Span = start.Span.To(name.Span) };
            // with nothing to take or give, just its name and ';'
            if (Accept(TokenKind.Semicolon)) return decl;
            Expect(TokenKind.Colon, $"':' and what {library.Text}.{name.Text} takes, or ';'");
            decl.Result = ParseOutsideParameters($"{library.Text}.{name.Text}", decl.Parameters);
            return decl;
        }

        /// <summary>enum.Name { A  B: 5  C }: one name a line, each one more than the last unless it says.</summary>
        EnumDecl ParseEnum(Token start, Token name)
        {
            var decl = new EnumDecl { File = file, Name = name.Text, Span = start.Span.To(name.Span) };
            Expect(TokenKind.OpenBrace, "'{'");
            while (!At(TokenKind.CloseBrace) && !AtEnd)
            {
                Token member = ExpectIdentifier("a name for one of the enum's values");
                CheckNotKeyword(member, "enum value");
                Expr? value = Accept(TokenKind.Colon) ? ParseExpression() : null;
                Accept(TokenKind.Semicolon);
                decl.Members.Add(new EnumMember { Name = member.Text, Value = value, Span = member.Span });
                if (!At(TokenKind.CloseBrace) && !Current.StartsLine)
                    throw Error(Current.Span, $"one value a line in enum {name.Text}, like\n    {member.Text}\n    {Current.Text}");
            }
            Expect(TokenKind.CloseBrace, $"'}}' to close enum {name.Text}");
            return decl;
        }

        ClassDecl ParseClass(Token start, Token name)
        {
            string baseName = "";
            Span baseSpan = name.Span;
            if (Accept(TokenKind.Colon))
            {
                Token b = ExpectIdentifier("a base class name, like Bootloader or OS");
                baseName = b.Text;
                baseSpan = b.Span;
            }

            Expect(TokenKind.OpenBrace, "'{'");

            var cls = new ClassDecl { File = file, Name = name.Text, Base = baseName, BaseSpan = baseSpan, Span = start.Span.To(name.Span) };

            while (!At(TokenKind.CloseBrace) && !AtEnd)
            {
                int before = pos;
                try
                {
                    if (At(TokenKind.OpenBracket))
                    {
                        MethodDecl m = ParseMethod();
                        m.Owner = cls;
                        cls.Methods.Add(m);
                    }
                    else
                    {
                        int from = Current.Span.Start;
                        Stmt statement = ParseStatement();
                        if (statement is VarDecl variable) variable.Text = Span.FromBounds(from, Previous.Span.End);
                        cls.Body.Add(statement);
                    }
                }
                catch (ParseError)
                {
                    Synchronize();
                }
                if (pos == before) Advance();
            }

            Expect(TokenKind.CloseBrace, $"'}}' to close class {name.Text}");
            cls.End = Previous.Span.End;
            return cls;
        }

        /// <summary>interface.Name { [Area] [Draw: int x; int y;] }: method headers, one after another.</summary>
        StructDecl ParseInterface(Token start, Token name)
        {
            var decl = new StructDecl { File = file, Name = name.Text, IsInterface = true, Span = start.Span.To(name.Span) };
            Expect(TokenKind.OpenBrace, "'{'");
            while (!At(TokenKind.CloseBrace) && !AtEnd)
            {
                Token open = Expect(TokenKind.OpenBracket, $"a method header like [Draw: int x;] in interface {name.Text}");
                if (AtWord("end"))
                    throw Error(Current.Span, $"an interface's methods have no body or [end]: each struct that has {name.Text} writes its own");
                Token method = ExpectIdentifier("a method name");
                CheckNotKeyword(method, "method");
                var m = new MethodDecl { Name = method.Text, Span = open.Span.To(method.Span) };
                if (Accept(TokenKind.Colon))
                {
                    while (!At(TokenKind.CloseBracket) && !AtEnd)
                    {
                        // [Area: : int]  [Grow: int by; : int]: what it returns, after the second ':'
                        if (Accept(TokenKind.Colon))
                        {
                            m.DeclaredResult = ParseType();
                            break;
                        }
                        if (!AtDeclaration)
                            throw Error(Current.Span, $"expected a parameter like 'int x;' but found {Current}");
                        m.Parameters.Add(ParseParameter());
                    }
                }
                Expect(TokenKind.CloseBracket, "']' to close the method header");
                decl.Methods.Add(m);
            }
            Expect(TokenKind.CloseBrace, $"'}}' to close interface {name.Text}");
            return decl;
        }

        /// <summary>
        /// True where a method is only its header: in a compiled library's interface. But a
        /// struct that takes types is there whole, code and all, since each program that uses the
        /// library makes its own structs from it.
        /// </summary>
        bool CompiledHeader => file.Compiled != null && placeholders == null && given == null;

        /// <summary>
        /// struct.Stack&lt;Item&gt; { ... }: read once here for its syntax, with Item a type nothing is
        /// known about, and kept as a template. It becomes a struct for each use (Instantiate).
        /// </summary>
        void ParseStructTemplate(Token start, Token name, ProgramNode program)
        {
            var template = new StructTemplate { File = file, Name = name.Text, NameSpan = name.Span, StartToken = tokens.IndexOf(start), Span = start.Span.To(name.Span) };
            foreach (Token parameter in ParseTemplateParameters(name)) template.Parameters.Add((parameter.Text, parameter.Span));
            program.Templates.Add(template);

            // (its fields and methods go nowhere: they're read again for each use)
            placeholders = template.Parameters.Select(p => p.Name).ToHashSet();
            try { ParseStruct(start, name, new ProgramNode { File = file }); }
            finally { placeholders = null; }
            template.Text = Span.FromBounds(start.Span.Start, Previous.Span.End);
        }

        /// <summary>&lt;Key Value&gt; after a struct's name: the names its types go by.</summary>
        List<Token> ParseTemplateParameters(Token name)
        {
            Expect(TokenKind.Less, "'<'");
            var parameters = new List<Token>();
            while (!At(TokenKind.Greater))
            {
                if (AtEnd || Current.StartsLine || !At(TokenKind.Identifier))
                    throw Error(Current.Span, $"expected a name for a type {name.Text} takes, or '>', as in struct.{name.Text}<Item>, but found {Current}");
                Token parameter = Advance();
                if (ReservedWords.Contains(parameter.Text))
                    Error(parameter.Span, TypeKeywords.Contains(parameter.Text)
                        ? $"'{parameter.Text}' is a type already; this is a name for whatever type {name.Text} is used with, like Item (the type itself goes where it's used: {name.Text}<{parameter.Text}>)"
                        : KeywordNameMessage(parameter.Text, "type"));
                else if (parameters.Any(p => p.Text == parameter.Text))
                    Error(parameter.Span, $"{name.Text} already takes a type called '{parameter.Text}'");
                parameters.Add(parameter);
            }
            if (parameters.Count == 0) Error(Current.Span, $"struct.{name.Text}<> takes no types; name one, as in struct.{name.Text}<Item>, or leave the <> out");
            Advance();
            return parameters;
        }

        /// <summary>
        /// The struct a template is for one use: its text read again with each of its types'
        /// names standing for the type given, under the use's whole name (Stack&lt;int&gt;). Its
        /// errors of syntax were reported when the template was read, so they aren't again.
        /// </summary>
        public static StructDecl Instantiate(StructTemplate template, IReadOnlyList<TypeRef> arguments, string fullName, ProgramNode program, SourceFile copy)
        {
            var parser = new Parser(copy, new DiagnosticBag()) { reading = program, pos = template.StartToken };
            parser.given = new Dictionary<string, TypeRef>();
            for (int i = 0; i < template.Parameters.Count; i++) parser.given[template.Parameters[i].Name] = arguments[i];

            Token start = parser.Advance();   // struct
            parser.Advance();                 // .
            Token name = parser.Advance();
            parser.ParseTemplateParameters(name);
            var named = new Token(TokenKind.Identifier, name.Span, fullName, null, name.StartsLine);
            StructDecl decl = parser.ParseStruct(start, named, program);
            program.Structs.Add(decl);
            return decl;
        }

        /// <summary>
        /// At a '&lt;' (ahead tokens on): the token after the '&gt;' that closes it, when what's between
        /// can only be types (names, ptr&lt;...&gt;, method&lt;...: ...&gt;, on one line), or -1. That's how
        /// Stack&lt;int&gt; names; is told from a &lt; b. Inside another type's brackets the close may be
        /// the first half of a '&gt;&gt;': then it's that token.
        /// </summary>
        int TypesEndAhead(int ahead)
        {
            if (PeekToken(ahead).Kind != TokenKind.Less) return -1;
            int depth = 0;
            for (int i = ahead; pos + i < tokens.Count; i++)
            {
                Token t = PeekToken(i);
                if (i > ahead && t.StartsLine) return -1;
                switch (t.Kind)
                {
                    case TokenKind.Less: depth++; break;
                    case TokenKind.Greater: depth--; break;
                    case TokenKind.GreaterGreater:
                        if (depth == 1) return i;
                        depth -= 2;
                        break;
                    case TokenKind.Colon: break;
                    case TokenKind.Identifier:
                        if (ReservedWords.Contains(t.Text) && !TypeKeywords.Contains(t.Text) && t.Text != "into") return -1;
                        break;
                    default: return -1;
                }
                if (depth == 0) return i + 1;
                if (depth < 0) return -1;
            }
            return -1;
        }

        /// <summary>Name&lt;types&gt; then a name: a variable of a struct that takes types (Stack&lt;int&gt; undo;).</summary>
        bool AtTemplateDeclaration(int ahead = 0)
        {
            if (!IsName(PeekToken(ahead))) return false;
            int after = TypesEndAhead(ahead + 1);
            return after > 0 && PeekToken(after).Kind == TokenKind.Identifier && !PeekToken(after).StartsLine;
        }

        StructDecl ParseStruct(Token start, Token name, ProgramNode program)
        {
            var decl = new StructDecl { File = file, Name = name.Text, Span = start.Span.To(name.Span) };
            // struct.Message foreign { ... }: laid out as C lays a struct out
            decl.IsForeign = AcceptWord("foreign");
            // struct.Circle has Shape { ... }: the interfaces it has, whose methods it must have
            if (AcceptWord("has"))
            {
                do
                {
                    Token iface = ExpectIdentifier("the name of an interface it has");
                    decl.Has.Add((iface.Text, iface.Span));
                } while (At(TokenKind.Identifier) && !AtWord("has"));
            }
            Expect(TokenKind.OpenBrace, "'{'");
            ClassDecl? methods = null;

            while (!At(TokenKind.CloseBrace) && !AtEnd)
            {
                int before = pos;
                try
                {
                    if (At(TokenKind.OpenBracket))
                    {
                        // a method: it gets the struct it's called on as self
                        if (methods == null)
                        {
                            methods = new ClassDecl { File = file, Name = name.Text, Base = "struct", BaseSpan = name.Span, ForStruct = decl, Span = start.Span.To(name.Span) };
                            program.Classes.Add(methods);
                        }
                        MethodDecl m = ParseMethod();
                        m.Owner = methods;
                        var self = new VarDecl
                        {
                            Type = new TypeRef { Name = "ptr", Pointee = new TypeRef { Name = name.Text, Span = name.Span }, Span = name.Span },
                            Name = "self",
                            NameSpan = name.Span,
                            Span = name.Span,
                        };
                        self.Values.Add(new NumberExpr { Value = 0, Span = name.Span });
                        m.Parameters.Insert(0, self);
                        methods.Methods.Add(m);
                        continue;
                    }

                    int fieldStart = Current.Span.Start;
                    bool isVolatile = AcceptWord("volatile");
                    bool isNew = AcceptWord("new");
                    TypeRef type = ParseType();
                    Token field = ExpectIdentifier("a field name");
                    CheckNotKeyword(field, "field");
                    Expr? count = null;
                    bool filled = false;
                    bool unsized = false;
                    if (At(TokenKind.OpenBracket))
                        (count, filled, unsized) = ParseBufferSize();

                    // str name: [24]; (or [atmost 24]) is text of up to 24 characters
                    Expr? textSize = null;
                    if (At(TokenKind.Colon) && PeekToken(1).Kind == TokenKind.OpenBracket)
                    {
                        Advance();
                        (textSize, _, _) = ParseBufferSize();
                        if (textSize == null) throw Error(Previous.Span, $"a str field needs a size: str {field.Text}: [24];");
                    }
                    ExpectSemicolon();
                    decl.Fields.Add(new FieldDecl { Type = type, Name = field.Text, Count = count, Filled = filled, Unsized = unsized, IsNew = isNew, Volatile = isVolatile,
                                                    TextSize = textSize, Span = type.Span.To(field.Span), Text = Span.FromBounds(fieldStart, Previous.Span.End) });
                }
                catch (ParseError)
                {
                    Synchronize();
                }
                if (pos == before) Advance();
            }

            Expect(TokenKind.CloseBrace, $"'}}' to close struct {name.Text}");
            if (methods != null) methods.End = Previous.Span.End;
            return decl;
        }

        /// <summary>int, u16, Task, ptr&lt;u8&gt;, ptr&lt;ptr&lt;u8&gt;&gt;</summary>
        TypeRef ParseType()
        {
            Token name = ExpectIdentifier("a type");
            if (name.Text == "method") return ParseMethodType(name);
            if (name.Text != "ptr")
            {
                // one of a struct's own types (Item): the type this use of the struct gave it
                if (given != null && given.TryGetValue(name.Text, out TypeRef? stands))
                {
                    var same = new TypeRef { Name = stands.Name, Pointee = stands.Pointee, Result = stands.Result, Span = name.Span, FromParameter = true };
                    same.Parameters.AddRange(stands.Parameters);
                    same.ParameterInto.AddRange(stands.ParameterInto);
                    return same;
                }
                return TypesEndAhead(0) > 0 ? ParseTemplateUse(name) : new TypeRef { Name = name.Text, Span = name.Span };
            }

            Expect(TokenKind.Less, "'<' after ptr, as in ptr<u8>");
            TypeRef pointee = ParseType();

            // ptr<ptr<u8>> ends in '>>', which the lexer reads as one token
            if (At(TokenKind.GreaterGreater))
            {
                Token both = Current;
                tokens[pos] = new Token(TokenKind.Greater, new Span(both.Span.Start + 1, 1), ">", null, false);
                return new TypeRef { Name = "ptr", Pointee = pointee, Span = name.Span.To(both.Span) };
            }

            Token close = Expect(TokenKind.Greater, "'>' to close ptr<...>");
            return new TypeRef { Name = "ptr", Pointee = pointee, Span = name.Span.To(close.Span) };
        }

        /// <summary>
        /// Stack&lt;int&gt;, Map&lt;str Player&gt;: a struct that takes types, with the types this use gives it.
        /// As a type its name is the whole of it, which is the struct made for these types.
        /// </summary>
        TypeRef ParseTemplateUse(Token name)
        {
            Advance();   // <
            var arguments = new List<TypeRef>();
            while (!At(TokenKind.Greater) && !At(TokenKind.GreaterGreater))
            {
                if (AtEnd || At(TokenKind.Semicolon)) throw Error(Current.Span, $"expected '>' to close {name.Text}<...>");
                arguments.Add(ParseType());
            }
            Token close = Current;
            // (Map<str ptr<u8>> and ptr<Stack<int>> end in '>>', which the lexer reads as one token)
            if (At(TokenKind.GreaterGreater))
                tokens[pos] = new Token(TokenKind.Greater, new Span(close.Span.Start + 1, 1), ">", null, false);
            else Advance();

            var type = new TypeRef { Name = $"{name.Text}<{string.Join(" ", arguments)}>", Span = name.Span.To(close.Span) };
            // (inside a template read as itself, Stack<Item> is no use of anything yet)
            if (placeholders == null)
                reading?.TemplateUses.Add(new TemplateUse { File = file, Template = name.Text, Arguments = arguments, FullName = type.Name, Span = type.Span });
            return type;
        }

        /// <summary>
        /// method&lt;int ptr&lt;u8&gt;: int&gt;: the parameters' types, then ': ' and the result's. A
        /// parameter written str into is the caller's own text (as str target; with no default is).
        /// </summary>
        TypeRef ParseMethodType(Token name)
        {
            Expect(TokenKind.Less, "'<' after method, as in method<int: int>");
            var parameters = new List<TypeRef>();
            var into = new List<bool>();
            while (!At(TokenKind.Greater) && !At(TokenKind.Colon) && !At(TokenKind.GreaterGreater))
            {
                if (AtEnd || At(TokenKind.Semicolon)) throw Error(Current.Span, "expected '>' to close method<...>");
                parameters.Add(ParseType());
                into.Add(AcceptWord("into"));
            }

            TypeRef? result = Accept(TokenKind.Colon) ? ParseType() : null;

            Token close = Current;
            if (At(TokenKind.GreaterGreater))
                tokens[pos] = new Token(TokenKind.Greater, new Span(close.Span.Start + 1, 1), ">", null, false);
            else
                close = Expect(TokenKind.Greater, "'>' to close method<...>");

            var type = new TypeRef { Name = "method", Result = result, Span = name.Span.To(close.Span) };
            type.Parameters.AddRange(parameters);
            type.ParameterInto.AddRange(into);
            return type;
        }

        // ------------------------------------------------------------ methods

        bool AtMethodEnd => At(TokenKind.OpenBracket) && PeekToken(1).IsWord("end");

        MethodDecl ParseMethod()
        {
            Token open = Expect(TokenKind.OpenBracket, "'['");

            if (AtWord("end")) throw Error(Current.Span, "[end] without a matching method header");

            bool isr = AtWord("isr") && PeekToken(1).Kind == TokenKind.Identifier;
            if (isr) Advance();

            string name = ParseQualifiedName(out Span nameSpan);
            var method = new MethodDecl { Name = name, IsInterruptHandler = isr, Span = open.Span.To(nameSpan) };

            if (Accept(TokenKind.Colon))
            {
                while (!At(TokenKind.CloseBracket) && !AtEnd)
                {
                    // what it returns, after a second ':' (a compiled library's says a little more)
                    if (Accept(TokenKind.Colon))
                    {
                        if (CompiledHeader) ParseCompiledResult(method);
                        else method.DeclaredResult = ParseType();
                        break;
                    }
                    // (new Node list[] is an array of objects, which only new says)
                    if (AtNewDeclaration && !(PeekToken(3).Kind == TokenKind.OpenBracket && PeekToken(4).Kind == TokenKind.CloseBracket))
                        throw Error(Current.Span, "a parameter borrows the caller's object, so it's written without new: Point p;");
                    if (!AtDeclaration && !AtNewDeclaration)
                        throw Error(Current.Span, $"expected a parameter like 'int count: 0;' but found {Current}");
                    method.Parameters.Add(ParseParameter());
                }
            }
            Expect(TokenKind.CloseBracket, "']' to close the method header");

            // a compiled library's method is only this header: its code is in the library
            if (CompiledHeader)
            {
                method.IsCompiled = true;
                method.End = Previous.Span.End;
                return method;
            }

            while (!AtMethodEnd && !AtEnd && !At(TokenKind.CloseBrace))
            {
                int before = pos;
                try
                {
                    if (At(TokenKind.OpenBracket))
                        throw Error(Current.Span, $"methods can't be declared inside method {name} (missing [end]?)");
                    method.Body.Add(ParseStatement());
                }
                catch (ParseError)
                {
                    Synchronize();
                    if (At(TokenKind.OpenBracket) && !AtMethodEnd) break;
                }
                if (pos == before) Advance();
            }

            if (!AtMethodEnd) throw Error(method.Span, $"method {name} is missing its [end]");

            Advance(); // [
            Advance(); // end
            if (Accept(TokenKind.Colon)) method.Result = ParseExpression();
            if (method.DeclaredResult != null && method.Result == null)
                throw Error(Previous.Span, $"{name}'s header says what it returns, so it ends with [end: value]");
            Expect(TokenKind.CloseBracket, "']' after [end");
            method.End = Previous.Span.End;

            return method;
        }

        /// <summary>A parameter, keeping its text for a library's interface.</summary>
        VarDecl ParseParameter()
        {
            int start = Current.Span.Start;
            VarDecl parameter = ParseVarDecl(allowBracketEnd: true);
            parameter.Text = Span.FromBounds(start, Previous.Span.End);
            return parameter;
        }

        /// <summary>
        /// In a compiled library's interface, what a method's header says after a second ':':
        /// what it returns (void for nothing), the room for a str result, and the str parameters
        /// it writes into. [Name: : str room 64]  [Fill: str target; : void writes target]
        /// </summary>
        void ParseCompiledResult(MethodDecl method)
        {
            if (!AcceptWord("void")) method.DeclaredResult = ParseType();
            if (AcceptWord("room"))
            {
                Token room = Advance();
                method.DeclaredRoom = room.Value is ulong huge ? (int)huge : (int)(long)room.Value!;
            }
            if (AcceptWord("writes"))
                while (At(TokenKind.Identifier)) method.Writes.Add(Advance().Text);
        }

        string ParseQualifiedName(out Span span)
        {
            Token first = ExpectIdentifier("a name");
            string name = first.Text;
            span = first.Span;

            while (At(TokenKind.Dot) && PeekToken(1).Kind == TokenKind.Identifier)
            {
                Advance();
                Token part = Advance();
                name += "." + part.Text;
                span = span.To(part.Span);
            }

            return name;
        }

        // ------------------------------------------------------------ statements

        List<Stmt> ParseBlock(string owner)
        {
            var body = new List<Stmt>();

            while (!At(TokenKind.DoubleSemicolon))
            {
                if (AtEnd || At(TokenKind.CloseBrace) || AtMethodEnd)
                    throw Error(Current.Span, $"expected ';;' to close the {owner}");

                int before = pos;
                try
                {
                    body.Add(ParseStatement());
                }
                catch (ParseError)
                {
                    Synchronize();
                }
                if (pos == before) Advance();
            }

            Advance(); // ;;
            return body;
        }

        Stmt ParseStatement()
        {
            Token start = Current;

            if (AtWord("const")) return ParseConst();
            if (AtWord("volatile"))
            {
                // memory that changes by itself: every read and write goes to it
                Advance();
                if (!AtDeclaration) throw Error(Current.Span, "volatile goes before a declaration: volatile ptr<u32> status: 0xFEBF0000;");
                VarDecl declared = ParseVarDecl(allowBracketEnd: false);
                declared.Volatile = true;
                return declared;
            }
            if (AtNewDeclaration) return ParseVarDecl(allowBracketEnd: false);
            if (AtDeclaration) return ParseVarDecl(allowBracketEnd: false);
            if (AtKeywordNameAssignment) return ParseAssignment(requireSemicolon: true);

            if (Current.Kind == TokenKind.Identifier)
            {
                switch (Current.Text)
                {
                    case "log":
                    case "debug":
                        return ParseLog();
                    case "throw":
                        return ParseThrow();
                    case "ask":
                    {
                        Advance();
                        string name = ParseQualifiedName(out Span s);
                        ExpectSemicolon();
                        return new AskStmt { Target = new NameExpr { Name = name, Span = s }, Span = start.Span.To(s) };
                    }
                    case "exit":
                    {
                        Advance();
                        Expr? code = At(TokenKind.Semicolon) ? null : ParseExpression();
                        ExpectSemicolon();
                        return new ExitStmt { Code = code, Span = start.Span.To(Previous.Span) };
                    }
                    case "out":
                    case "outw":
                    case "outd":
                    {
                        Advance();
                        Expr port = ParseListElement();
                        Expr value = ParseListElement();
                        ExpectSemicolon();
                        int size = start.Text == "out" ? 1 : start.Text == "outw" ? 2 : 4;
                        return new OutStmt { Size = size, Port = port, Value = value, Span = start.Span.To(Previous.Span) };
                    }
                    case "when":
                        return ParseWhen();
                    case "free":
                    {
                        Advance();
                        Expr pointer = ParseExpression();
                        ExpectSemicolon();
                        return new FreeStmt { Pointer = pointer, Span = start.Span.To(Previous.Span) };
                    }
                    case "unsafe":
                    {
                        Advance();
                        Accept(TokenKind.Semicolon);
                        var stmt = new UnsafeStmt { Span = start.Span };
                        stmt.Body.AddRange(ParseBlock("unsafe block"));
                        return stmt;
                    }
                    case "locked":
                    {
                        Advance();
                        Expr guard = ParsePostfix();
                        Accept(TokenKind.Semicolon);
                        var stmt = new LockedStmt { Lock = guard, Span = start.Span.To(guard.Span) };
                        stmt.Body.AddRange(ParseBlock("locked block"));
                        return stmt;
                    }
                    case "atomic":
                        return ParseAtomic();
                    case "break":
                    case "continue":
                    {
                        Advance();
                        ExpectSemicolon();
                        return start.Text == "break"
                            ? new BreakStmt { Span = start.Span.To(Previous.Span) }
                            : new ContinueStmt { Span = start.Span.To(Previous.Span) };
                    }
                    case "return":
                    {
                        Advance();
                        Expr? value = At(TokenKind.Semicolon) ? null : ParseExpression();
                        ExpectSemicolon();
                        return new ReturnStmt { Value = value, Span = start.Span.To(Previous.Span) };
                    }
                    case "call": return ParseCall();
                    case "if": return ParseIf();
                    case "while": return ParseWhile();
                    case "else":
                    case "elif":
                        throw Error(Current.Span, $"'{Current.Text}' must directly follow the ';;' that closes an if");
                }
            }

            if (Current.Kind == TokenKind.Identifier) return ParseAssignment(requireSemicolon: true);

            throw Error(Current.Span, $"expected a statement but found {Current}");
        }

        // atomic x++ n;  atomic x-- n;  atomic x is v;  atomic x is v when old;
        Stmt ParseAtomic()
        {
            Token start = Advance();
            Expr target = ParsePostfix();
            Token opToken = Current;
            AtomicOp op;
            Expr value;
            Expr? expected = null;
            if (Accept(TokenKind.PlusPlus) || Accept(TokenKind.MinusMinus))
            {
                op = opToken.Kind == TokenKind.PlusPlus ? AtomicOp.Add : AtomicOp.Subtract;
                value = At(TokenKind.Semicolon) ? new NumberExpr { Value = 1, Span = opToken.Span } : ParseBitOr();
            }
            else if (AcceptWord("is"))
            {
                value = ParseBitOr();
                op = AtomicOp.Swap;
                if (AcceptWord("when"))
                {
                    op = AtomicOp.CompareSwap;
                    expected = ParseBitOr();
                }
            }
            else throw Error(Current.Span, $"atomic goes before x++, x--, x is v, or x is v when old, but found {Current}");
            ExpectSemicolon();
            return new AtomicStmt { Target = target, Op = op, Value = value, Expected = expected, Span = start.Span.To(Previous.Span) };
        }

        ConstDecl ParseConst()
        {
            Token keyword = Advance();
            TypeRef type = ParseType();
            Token name = ExpectIdentifier("a constant name");
            if (!Accept(TokenKind.Colon) && !Accept(TokenKind.Equals))
                throw Error(Current.Span, $"expected ':' after constant name '{name.Text}' but found {Current}");
            Expr value = ParseExpression();
            ExpectSemicolon();
            return new ConstDecl { Type = type, Name = name.Text, NameSpan = name.Span, Value = value, Span = keyword.Span.To(Previous.Span) };
        }

        VarDecl ParseVarDecl(bool allowBracketEnd)
        {
            Token start = Current;
            bool isNew = AcceptWord("new");
            TypeRef type = ParseType();
            Token name = ExpectIdentifier("a variable name");
            CheckNotKeyword(name, "variable");

            Expr? buffer = null;
            bool filled = false;
            bool unsized = false;
            bool afterColon = false;
            Expr? textSize = null;
            Expr? at = null;
            var values = new List<Expr>();

            // new T name at address;
            if (isNew && AtWord("at") && !Current.StartsLine)
            {
                Advance();
                at = ParseExpression();
            }
            else if (At(TokenKind.OpenBracket))
            {
                (buffer, filled, unsized) = ParseBufferSize();

                // str lines[8]: [40]; each element holds up to 40 characters
                if (At(TokenKind.Colon) && PeekToken(1).Kind == TokenKind.OpenBracket)
                {
                    Advance();
                    (textSize, _, _) = ParseBufferSize();
                }
            }
            else if (At(TokenKind.Semicolon) || (allowBracketEnd && At(TokenKind.CloseBracket)))
            {
                // no value: starts as zero
            }
            else
            {
                if (!Accept(TokenKind.Colon) && !Accept(TokenKind.Equals))
                    throw Error(Current.Span, $"expected ':' after variable name '{name.Text}' but found {Current}");

                if (At(TokenKind.OpenBracket))
                {
                    (buffer, filled, unsized) = ParseBufferSize();
                    afterColon = true;
                }
                else
                {
                    while (!At(TokenKind.Semicolon) && !(allowBracketEnd && At(TokenKind.CloseBracket)))
                    {
                        if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon))
                            throw Error(new Span(Previous.Span.End, 0), $"expected ';' to end the declaration of '{name.Text}'");
                        values.Add(ParseListElement());
                    }

                    if (values.Count == 0) throw Error(Current.Span, $"variable '{name.Text}' needs a value");
                }
            }

            var decl = new VarDecl
            {
                Type = type,
                Name = name.Text,
                NameSpan = name.Span,
                BufferSize = buffer,
                Filled = filled,
                Unsized = unsized,
                SizeAfterColon = afterColon,
                TextSize = textSize,
                IsNew = isNew,
                AtAddress = at,
                Span = start.Span.To(Previous.Span),
            };
            decl.Values.AddRange(values);

            if (!(allowBracketEnd && At(TokenKind.CloseBracket))) ExpectSemicolon();
            return decl;
        }

        /// <summary>[N], [atmost N] for an array that's filled as it goes, or [] with no size.</summary>
        (Expr? Size, bool Filled, bool Unsized) ParseBufferSize()
        {
            Expect(TokenKind.OpenBracket, "'['");
            if (Accept(TokenKind.CloseBracket)) return (null, false, true);
            bool filled = AcceptWord("atmost");
            Expr size = ParseExpression();
            Expect(TokenKind.CloseBracket, "']'");
            return (size, filled, false);
        }

        Stmt ParseLog()
        {
            Token keyword = Advance();
            var values = new List<Expr>();

            while (!At(TokenKind.Semicolon))
            {
                if (values.Count > 0 && AtEndOfValues(IndentOf(keyword.Span.Start))) break;
                if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon)) break;
                values.Add(ParseListElement());
            }

            if (values.Count == 0) throw Error(keyword.Span, $"'{keyword.Text}' needs something to print");
            ExpectSemicolon();

            var stmt = new LogStmt
            {
                Target = keyword.Text == "debug" ? LogTarget.SerialOnly : LogTarget.ScreenAndSerial,
                Span = keyword.Span.To(Previous.Span),
            };
            stmt.Values.AddRange(values);
            return stmt;
        }

        /// <summary>throw 'no such file: ' path;  the values are the error's text, as log reads them</summary>
        Stmt ParseThrow()
        {
            Token keyword = Advance();
            var values = new List<Expr>();
            while (!At(TokenKind.Semicolon))
            {
                if (values.Count > 0 && AtEndOfValues(IndentOf(keyword.Span.Start))) break;
                if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon)) break;
                values.Add(ParseListElement());
            }
            if (values.Count == 0) throw Error(keyword.Span, "'throw' needs the error's text, like: throw 'no such file: ' path;");
            ExpectSemicolon();
            var stmt = new ThrowStmt { Span = keyword.Span.To(Previous.Span) };
            stmt.Values.AddRange(values);
            return stmt;
        }

        /// <param name="beforeThrows">the call of an if call ... throws err: its arguments end at throws, not ';'</param>
        Stmt ParseCall(bool beforeThrows = false)
        {
            Token keyword = Advance();

            // call target is Name: args;
            Expr? target = null;
            if (At(TokenKind.Identifier) && FindIsBeforeCallee())
            {
                target = ParsePostfix();
                if (!AcceptWord("is")) throw Error(Current.Span, "expected 'is'");
            }

            // call handlers:i: key;  calls a method variable reached through an index
            Expr? calleeTarget = null;
            string callee;
            Span calleeSpan;
            if (CalleeHasIndex())
            {
                calleeTarget = ParsePostfix(stopAtArguments: true);
                callee = file.Text[calleeTarget.Span.Start..calleeTarget.Span.End];
                calleeSpan = calleeTarget.Span;
            }
            else callee = ParseQualifiedName(out calleeSpan);
            var arguments = new List<Expr>();
            var places = new List<(int, int, Expr)>();

            if (Accept(TokenKind.Colon))
            {
                while (!At(TokenKind.Semicolon) && !(beforeThrows && AtWord("throws")))
                {
                    if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon) || beforeThrows && (Current.StartsLine || OneLineIfWords.Any(AtWord)))
                        throw Error(new Span(Previous.Span.End, 0), beforeThrows ? "expected 'throws' and a name for the error, like: if call Load: path throws err" : "expected ';' to end the call");
                    ParseArgument(arguments, places);
                }
            }
            NoteArguments(places);
            if (!beforeThrows) ExpectSemicolon();

            var call = new CallStmt
            {
                ResultTarget = target,
                Callee = callee,
                CalleeSpan = calleeSpan,
                CalleeTarget = calleeTarget,
                Span = keyword.Span.To(Previous.Span),
            };
            call.Arguments.AddRange(arguments);
            return call;
        }

        /// <summary>
        /// After '(': a name, then ':' followed by a space, as in (Str.Len: text). An index has
        /// no space after the ':' (list:i), so (list:i) stays an element.
        /// </summary>
        bool AtCallInParens()
        {
            int i = ScanPostfix(0);
            if (i < 0) return false;
            Token colon = PeekToken(i);
            return colon.Kind == TokenKind.Colon && PeekToken(i + 1).Span.Start > colon.Span.End;
        }

        /// <summary>The callee ahead is more than a name: it has an index, like handlers:i.</summary>
        bool CalleeHasIndex()
        {
            if (!IsName(Current)) return false;
            int name = 1;
            while (PeekToken(name).Kind == TokenKind.Dot && PeekToken(name + 1).Kind == TokenKind.Identifier) name += 2;
            return ScanPostfix(0) > name;
        }

        /// <summary>
        /// Looks over name(.name)*, then any indexes (':' with no space after it, then a name,
        /// number or parenthesized expression) and fields; returns the offset of the token after
        /// them, or -1 if no name is ahead.
        /// </summary>
        int ScanPostfix(int i)
        {
            // (sys.Name: args): a system call, by the name it was declared with
            bool systemCall = PeekToken(i).Text == "sys" && PeekToken(i).Kind == TokenKind.Identifier && PeekToken(i + 1).Kind == TokenKind.Dot;
            if (!IsName(PeekToken(i)) && !systemCall) return -1;
            i++;
            while (true)
            {
                Token t = PeekToken(i);
                if (t.Kind == TokenKind.Dot && PeekToken(i + 1).Kind == TokenKind.Identifier && !PeekToken(i + 1).StartsLine)
                {
                    i += 2;
                    continue;
                }

                Token next = PeekToken(i + 1);
                if (t.Kind != TokenKind.Colon || next.Span.Start != t.Span.End || next.StartsLine) return i;

                if (next.Kind is TokenKind.Identifier or TokenKind.Number)
                {
                    i += 2;
                    continue;
                }
                if (next.Kind != TokenKind.OpenParen) return i;

                int depth = 0;
                int j = i + 1;
                do
                {
                    TokenKind k = PeekToken(j).Kind;
                    if (k == TokenKind.OpenParen) depth++;
                    else if (k == TokenKind.CloseParen) depth--;
                    else if (k == TokenKind.EndOfFile) return -1;
                    j++;
                } while (depth > 0);
                i = j;
            }
        }

        /// <summary>(Name: args) after the '(' has been read.</summary>
        Expr ParseCallExpr(Token open)
        {
            Expr? calleeTarget = null;
            string callee;
            Span calleeSpan;
            if (CalleeHasIndex())
            {
                calleeTarget = ParsePostfix(stopAtArguments: true);
                callee = file.Text[calleeTarget.Span.Start..calleeTarget.Span.End];
                calleeSpan = calleeTarget.Span;
            }
            else callee = ParseQualifiedName(out calleeSpan);
            Expect(TokenKind.Colon, "':'");

            var arguments = new List<Expr>();
            var places = new List<(int, int, Expr)>();
            while (!At(TokenKind.CloseParen))
            {
                if (AtEnd || At(TokenKind.Semicolon) || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon))
                    throw Error(new Span(Previous.Span.End, 0), $"expected ')' to end the call to {callee}");
                ParseArgument(arguments, places);
            }
            NoteArguments(places);
            Token close = Advance();

            var call = new CallExpr { Callee = callee, CalleeSpan = calleeSpan, CalleeTarget = calleeTarget, Span = open.Span.To(close.Span) };
            call.Arguments.AddRange(arguments);
            return call;
        }

        /// <summary>
        /// Looks ahead for `name is` (or `name:index is`) before the callee. A ':' followed by a
        /// space starts the arguments, so the 'is' in `call Show: a is 1;` is a comparison.
        /// However long the name is (`call list:0.next.next.next.next is ...`): the statement's
        /// own end stops the search.
        /// </summary>
        bool FindIsBeforeCallee()
        {
            for (int i = 1; ; i++)
            {
                Token t = PeekToken(i);
                if (t.IsWord("is")) return true;
                if (t.Kind is TokenKind.Semicolon or TokenKind.EndOfFile || t.StartsLine) return false;
                if (t.Kind == TokenKind.Colon && PeekToken(i + 1).Span.Start > t.Span.End) return false;
            }
        }

        /// <summary>The struct an if call ... throws err makes err (lib/phi/error.phi).</summary>
        public const string ErrorStruct = "Error";

        /// <summary>call Load: path throws err  (an if's condition)</summary>
        Expr ParseThrows()
        {
            var call = (CallStmt)ParseCall(beforeThrows: true);
            if (!AcceptWord("throws"))
                throw Error(Current.Span, "after the call, expected 'throws' and a name for the error, like: if call Load: path throws err");
            if (Current.Kind != TokenKind.Identifier || Current.StartsLine)
                throw Error(Current.Span, "expected a name for the error after throws, like: if call Load: path throws err");
            Token name = Advance();
            CheckNotKeyword(name, "variable");
            return new ThrowsExpr { Call = call, Name = name.Text, NameSpan = name.Span, Span = call.Span.To(name.Span) };
        }

        /// <summary>The statements that can follow an if's condition on the same line.</summary>
        static readonly string[] OneLineIfWords = { "return", "break", "continue", "call", "exit" };

        Stmt ParseIf()
        {
            Token keyword = Advance();
            Expr condition = AtWord("call") ? ParseThrows() : ParseExpression();
            Accept(TokenKind.Semicolon);

            var stmt = new IfStmt { Condition = condition, Span = keyword.Span.To(condition.Span) };

            // if call Load: path throws err: inside, err is the error
            if (condition is ThrowsExpr throws)
                stmt.Then.Add(new VarDecl
                {
                    Type = new TypeRef { Name = ErrorStruct, Span = throws.NameSpan },
                    Name = throws.Name,
                    NameSpan = throws.NameSpan,
                    Span = throws.NameSpan,
                });

            // if thing is Player p: inside, p is the Player it holds
            if (condition is IsTypeExpr test)
            {
                test.Declares = true;
                stmt.Then.Add(NarrowedDecl(test.Value, test.TypeName, test.TypeSpan, test.Name, test.NameSpan));
            }

            // a guard on one line, with no ;; :  if key is 27 return true;
            if (!Current.StartsLine && OneLineIfWords.Any(AtWord))
            {
                stmt.Then.Add(ParseStatement());
                if (AtWord("elif") || AtWord("else"))
                    throw Error(Current.Span, $"an if on one line can't have an {Current.Text}; write the if as a block, ending in ;;");
                return stmt;
            }

            // if k is 'p' tool is 1; would otherwise read as a block that never closes
            if (!Current.StartsLine && !AtEnd && !At(TokenKind.DoubleSemicolon))
                throw Error(Current.Span, "an if on one line can only return, break, continue, call or exit; " +
                    "for anything else, put it on the next line and close the if with ;;");

            stmt.Then.AddRange(ParseBlock("if"));

            if (AtWord("elif"))
            {
                stmt.Else = new List<Stmt> { ParseIf() };
            }
            else if (AcceptWord("else"))
            {
                Accept(TokenKind.Semicolon);
                stmt.Else = ParseBlock("else");
            }

            return stmt;
        }

        /// <summary>The name a type test gives the object it found: new Player p, borrowed from value.</summary>
        internal static VarDecl NarrowedDecl(Expr value, string type, Span typeSpan, string name, Span nameSpan) => new()
        {
            Type = new TypeRef { Name = type, Span = typeSpan },
            Name = name,
            NameSpan = nameSpan,
            Span = typeSpan.To(nameSpan),
            IsNew = true,
            Narrows = value,
        };

        /// <summary>when x  /  is a b ... ;;  /  else ... ;;  /  ;;</summary>
        Stmt ParseWhen()
        {
            Token keyword = Advance();
            Expr subject = ParseExpression();
            Accept(TokenKind.Semicolon);
            var stmt = new WhenStmt { Subject = subject, Span = keyword.Span.To(subject.Span) };

            while (true)
            {
                if (AtWord("is"))
                {
                    Token isWord = Advance();
                    var branch = new WhenBranch { Span = isWord.Span };
                    while (!Current.StartsLine && !AtEnd && !At(TokenKind.Semicolon) && !At(TokenKind.DoubleSemicolon))
                        branch.Values.Add(ParseListElement());
                    Accept(TokenKind.Semicolon);
                    if (branch.Values.Count == 0)
                        throw Error(isWord.Span, "'is' needs the values that pick this branch, like is 'q' 27");
                    if (stmt.Else != null)
                        throw Error(isWord.Span, "else comes last in a when, after every is");
                    branch.Body.AddRange(ParseBlock("is"));
                    stmt.Branches.Add(branch);
                }
                else if (AtWord("else"))
                {
                    Token elseWord = Advance();
                    Accept(TokenKind.Semicolon);
                    if (stmt.Else != null) throw Error(elseWord.Span, "a when has one else");
                    stmt.Else = ParseBlock("else");
                }
                else if (Accept(TokenKind.DoubleSemicolon))
                {
                    break;
                }
                else
                {
                    throw Error(Current.Span, "expected 'is' (a branch), 'else' or ';;' (the end of the when)");
                }
            }

            if (stmt.Branches.Count == 0) throw Error(keyword.Span, "a when needs at least one 'is' branch");
            return stmt;
        }

        Stmt ParseWhile()
        {
            Token keyword = Advance();

            // while each x in list: each element in turn
            if (AtWord("each"))
            {
                Advance();
                Token name = ExpectIdentifier("a name for each element");
                CheckNotKeyword(name, "variable");
                if (!AcceptWord("in"))
                    throw Error(Current.Span, $"expected 'in' and an array, like while each {name.Text} in list");
                Expr array = ParseExpression();
                Accept(TokenKind.Semicolon);
                var each = new WhileEachStmt
                {
                    Element = new VarDecl { Type = new TypeRef { Name = "each", Span = name.Span }, Name = name.Text, NameSpan = name.Span, EachOf = array, Span = name.Span },
                    // the count, with a name no program can write
                    Index = new VarDecl { Type = new TypeRef { Name = "int", Span = name.Span }, Name = $"each#{name.Span.Start}", NameSpan = name.Span, Span = name.Span },
                    Array = array,
                    Span = keyword.Span.To(array.Span),
                };
                each.Body.AddRange(ParseBlock("while each loop"));
                return each;
            }

            if (AtDeclaration)
            {
                // while int i: 0; i < 10; i++;
                VarDecl init = ParseVarDecl(allowBracketEnd: false);
                Expr cond = ParseExpression();
                ExpectSemicolon();
                Stmt step = ParseAssignment(requireSemicolon: false);
                Accept(TokenKind.Semicolon);

                var loop = new WhileStmt { Init = init, Condition = cond, Step = step, Span = keyword.Span.To(Previous.Span) };
                loop.Body.AddRange(ParseBlock("while loop"));
                return loop;
            }

            Expr condition = ParseExpression();
            Accept(TokenKind.Semicolon);
            var stmt = new WhileStmt { Condition = condition, Span = keyword.Span.To(condition.Span) };
            stmt.Body.AddRange(ParseBlock("while loop"));
            return stmt;
        }

        Stmt ParseAssignment(bool requireSemicolon)
        {
            Expr target = ParsePostfix();

            if (AtWord("takes") && !Current.StartsLine)
            {
                Advance();
                Expr source = ParsePostfix();
                if (requireSemicolon) ExpectSemicolon();
                return new TakesStmt { Target = target, Source = source, Span = target.Span.To(Previous.Span) };
            }

            AssignOp op;
            Expr? value;
            Token opToken = Current;

            if (AcceptWord("is") || Accept(TokenKind.Equals)) op = AssignOp.Set;
            else if (Accept(TokenKind.PlusPlus)) op = AssignOp.Add;
            else if (Accept(TokenKind.MinusMinus)) op = AssignOp.Subtract;
            else if (Accept(TokenKind.StarStar)) op = AssignOp.Multiply;
            else if (Accept(TokenKind.SlashSlash)) op = AssignOp.Divide;
            else if (Accept(TokenKind.PercentPercent)) op = AssignOp.Modulo;
            else if (Accept(TokenKind.CaretCaret)) op = AssignOp.Power;
            else if (Current.StartsLine || AtEnd)
            {
                // a name alone on its line: it's the name that's wrong, and the next line is its own
                // statement, which is still read (so its mistakes are found and its names known)
                Error(target.Span, $"expected an assignment like 'x is 5;' or 'x++;' after '{file.Text[target.Span.Start..target.Span.End]}'");
                return new UnsafeStmt { Span = target.Span };
            }
            else throw Error(Current.Span, $"expected an assignment like 'x is 5;' or 'x++;' but found {Current}");

            // x++; and x--; step by one
            bool bare = At(TokenKind.Semicolon) || (Current.StartsLine && op is AssignOp.Add or AssignOp.Subtract);
            int valueStart = pos;
            if (bare && op is AssignOp.Add or AssignOp.Subtract)
                value = new NumberExpr { Value = 1, Span = opToken.Span };
            else
                value = ParseExpression();

            if (opToken.IsWord("is") && value is BinaryExpr { Op: BinaryOp.Equal or BinaryOp.NotEqual } compare
                && file.Text[compare.Left.Span.End..compare.Right.Span.Start].Contains("is") && !InParens(compare.Span))
            {
                string text = file.Text[compare.Span.Start..compare.Span.End];
                diagnostics.Warning(file, opToken.Span, $"this stores whether {text} in '{file.Text[target.Span.Start..target.Span.End]}'; " +
                                                        $"write ({text}) to make that clear");
            }

            // the right side as a list builds a str: line is 'Ln ' n ', Col ' col;
            int indent = IndentOf(target.Span.Start);
            bool whole = AtEndOfValues(indent);
            List<Expr>? values = null;
            if (requireSemicolon && !bare && op is AssignOp.Set or AssignOp.Add)
            {
                if (HasListMinus(valueStart, pos))
                {
                    // the first value reads differently as a list ('!' -3 is two values): read it again
                    pos = valueStart;
                    values = new List<Expr>();
                }
                else if (!whole) values = new List<Expr> { value };

                if (values != null)
                    while (!AtEndOfValues(indent)) values.Add(ParseListElement());
            }

            if (requireSemicolon) ExpectSemicolon();

            return new AssignStmt
            {
                Target = target, Op = op, Value = value, Values = values, ValueIsWhole = whole, Bare = bare,
                Span = target.Span.To(Previous.Span),
            };
        }

        /// <summary>
        /// The end of a list of values: ';', or a new line that isn't indented deeper than the
        /// statement's first line (indent), since a deeper line carries on the list.
        /// </summary>
        bool AtEndOfValues(int indent) => At(TokenKind.Semicolon) || AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon)
                                          || Current.StartsLine && IndentOf(Current.Span.Start) <= indent;

        /// <summary>The width of the spaces and tabs that start the line holding offset (a tab counts as 4).</summary>
        int IndentOf(int offset)
        {
            int i = offset;
            while (i > 0 && file.Text[i - 1] != '\n') i--;
            int width = 0;
            for (; i < offset && file.Text[i] is ' ' or '\t'; i++) width += file.Text[i] == '\t' ? 4 : 1;
            return width;
        }

        /// <summary>
        /// Whether tokens from..to have a '-' outside parentheses that a list would read as the
        /// start of a negative value (a space before it, none after: `6 -1`).
        /// </summary>
        bool HasListMinus(int from, int to)
        {
            int depth = 0;
            for (int i = from; i < to; i++)
            {
                Token t = tokens[i];
                if (t.Kind is TokenKind.OpenParen or TokenKind.OpenBracket) depth++;
                else if (t.Kind is TokenKind.CloseParen or TokenKind.CloseBracket) depth--;
                else if (t.Kind == TokenKind.Minus && depth == 0 && i > from && i + 1 < tokens.Count
                         && t.Span.Start > tokens[i - 1].Span.End && tokens[i + 1].Span.Start == t.Span.End)
                    return true;
            }
            return false;
        }

        /// <summary>True when the text just before span (ignoring spaces) is '('.</summary>
        bool InParens(Span span)
        {
            int i = span.Start - 1;
            while (i >= 0 && file.Text[i] is ' ' or '\t') i--;
            return i >= 0 && file.Text[i] == '(';
        }

        // ------------------------------------------------------------ expressions
        //
        // lowest to highest: or, and, comparison, |, ^, &, << >>, + -, * / %, unary, postfix

        public Expr ParseExpression()
        {
            // inside ( ) and [ ] a '-' always subtracts
            bool outer = inList;
            inList = false;
            try { return ParseOr(); }
            finally { inList = outer; }
        }

        /// <summary>One value of a space-separated list: in `3 1 -2`, the '-2' is its own value.</summary>
        Expr ParseListElement()
        {
            bool outer = inList;
            inList = true;
            try { return ParseOr(); }
            finally { inList = outer; }
        }

        /// <summary>
        /// A '-' with a space before it and none after (as in `6 -1`) starts a negative value
        /// in a list instead of subtracting. `6 - 1` and `6-1` still subtract.
        /// </summary>
        bool AtNegativeListValue =>
            inList && At(TokenKind.Minus)
            && Current.Span.Start > Previous.Span.End
            && PeekToken(1).Span.Start == Current.Span.End;

        /// <summary>A binary operator only continues an expression on the same line.</summary>
        /// <summary>
        /// A binary operator goes on the same line as its left side, which is how `if ready` with
        /// a statement on the next line knows where the condition ends. Inside parentheses the
        /// ')' says where it ends, so one can start a line there.
        /// </summary>
        bool OperatorMayFollow => !Current.StartsLine || parenDepth > 0;
        int parenDepth;

        bool AtOperator(TokenKind kind) => At(kind) && OperatorMayFollow;
        bool AtOperatorWord(string word) => AtWord(word) && OperatorMayFollow;

        static Expr Binary(BinaryOp op, Expr left, Expr right) =>
            new BinaryExpr { Op = op, Left = left, Right = right, Span = left.Span.To(right.Span) };

        Expr ParseOr()
        {
            Expr left = ParseAnd();
            while (AtOperatorWord("or"))
            {
                Advance();
                left = Binary(BinaryOp.Or, left, ParseAnd());
            }
            return left;
        }

        Expr ParseAnd()
        {
            Expr left = ParseComparison();
            while (AtOperatorWord("and"))
            {
                Advance();
                left = Binary(BinaryOp.And, left, ParseComparison());
            }
            return left;
        }

        /// <summary>Word spellings of &lt; &gt; &lt;= &gt;=.</summary>
        static readonly Dictionary<string, BinaryOp> ComparisonWords = new()
        {
            ["below"] = BinaryOp.Less,
            ["above"] = BinaryOp.Greater,
            ["atmost"] = BinaryOp.LessEqual,
            ["atleast"] = BinaryOp.GreaterEqual,
        };

        bool AtComparisonWord => OperatorMayFollow && Current.Kind == TokenKind.Identifier && ComparisonWords.ContainsKey(Current.Text);

        Expr ParseComparison()
        {
            Expr left = ParseBitOr();

            BinaryOp? op = null;
            if (AtOperatorWord("is"))
            {
                Advance();
                op = AcceptWord("not") ? BinaryOp.NotEqual : BinaryOp.Equal;

                // thing is Player p: does the interface value hold a Player (then p is it)
                if (op == BinaryOp.Equal && At(TokenKind.Identifier) && PeekToken(1).Kind == TokenKind.Identifier && !PeekToken(1).StartsLine
                    && !ReservedWords.Contains(Current.Text) && !ReservedWords.Contains(PeekToken(1).Text) && PeekToken(2).Kind != TokenKind.Dot)
                {
                    Token type = Advance();
                    Token name = Advance();
                    return new IsTypeExpr { Value = left, TypeName = type.Text, TypeSpan = type.Span, Name = name.Text, NameSpan = name.Span, Span = left.Span.To(name.Span) };
                }
            }
            else if (AtComparisonWord)
            {
                op = ComparisonWords[Advance().Text];
            }
            else if (AtOperatorWord("has"))
            {
                // list has x, text has 'abc'
                Advance();
                Expr value = ParseBitOr();
                return new HasExpr { Collection = left, Value = value, Span = left.Span.To(value.Span) };
            }
            else if (!Current.StartsLine)
            {
                op = Current.Kind switch
                {
                    TokenKind.EqualsEquals => BinaryOp.Equal,
                    TokenKind.BangEquals => BinaryOp.NotEqual,
                    TokenKind.Less => BinaryOp.Less,
                    TokenKind.Greater => BinaryOp.Greater,
                    TokenKind.LessEquals => BinaryOp.LessEqual,
                    TokenKind.GreaterEquals => BinaryOp.GreaterEqual,
                    _ => null,
                };
                if (op != null) Advance();
            }

            if (op == null) return left;
            Expr result = Binary(op.Value, left, ParseBitOr());

            bool another = OperatorMayFollow && (AtWord("is") || AtComparisonWord || Current.Kind is TokenKind.EqualsEquals or TokenKind.BangEquals
                or TokenKind.Less or TokenKind.Greater or TokenKind.LessEquals or TokenKind.GreaterEquals);
            if (another)
                throw Error(Current.Span, "comparisons can't be chained; use parentheses, like (a < b) is false, or 'and'");
            return result;
        }

        Expr ParseBitOr()
        {
            Expr left = ParseBitXor();
            while (AtOperator(TokenKind.Pipe))
            {
                Advance();
                left = Binary(BinaryOp.BitOr, left, ParseBitXor());
            }
            return left;
        }

        Expr ParseBitXor()
        {
            Expr left = ParseBitAnd();
            while (AtOperator(TokenKind.Caret))
            {
                Advance();
                left = Binary(BinaryOp.BitXor, left, ParseBitAnd());
            }
            return left;
        }

        Expr ParseBitAnd()
        {
            Expr left = ParseShift();
            while (AtOperator(TokenKind.Ampersand))
            {
                Advance();
                left = Binary(BinaryOp.BitAnd, left, ParseShift());
            }
            return left;
        }

        Expr ParseShift()
        {
            Expr left = ParseAdditive();
            while (AtOperator(TokenKind.LessLess) || AtOperator(TokenKind.GreaterGreater))
            {
                BinaryOp op = Advance().Kind == TokenKind.LessLess ? BinaryOp.ShiftLeft : BinaryOp.ShiftRight;
                left = Binary(op, left, ParseAdditive());
            }
            return left;
        }

        Expr ParseAdditive()
        {
            Expr left = ParseMultiplicative();
            while (AtOperator(TokenKind.Plus) || (AtOperator(TokenKind.Minus) && !AtNegativeListValue))
            {
                BinaryOp op = Advance().Kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Subtract;
                left = Binary(op, left, ParseMultiplicative());
            }
            return left;
        }

        Expr ParseMultiplicative()
        {
            Expr left = ParseUnary();
            while (AtOperator(TokenKind.Star) || AtOperator(TokenKind.Slash) || AtOperator(TokenKind.Percent))
            {
                BinaryOp op = Advance().Kind switch
                {
                    TokenKind.Star => BinaryOp.Multiply,
                    TokenKind.Slash => BinaryOp.Divide,
                    _ => BinaryOp.Modulo,
                };
                left = Binary(op, left, ParseUnary());
            }
            return left;
        }

        /// <summary>A unary expression, then any number of `as Type`: w:0.data as ptr&lt;Note&gt;, or p as u8[count]</summary>
        Expr ParseUnary()
        {
            Expr expr = ParseUnaryOperand();
            while (AtOperatorWord("as"))
            {
                Advance();
                TypeRef type = ParseType();
                if (At(TokenKind.OpenBracket) && !Current.StartsLine)
                {
                    Advance();
                    Expr count = ParseExpression();
                    Token close = Expect(TokenKind.CloseBracket, "']'");
                    expr = new PointerViewExpr { Pointer = expr, ElementType = type, Count = count, Span = expr.Span.To(close.Span) };
                }
                else expr = new CastExpr { Operand = expr, TargetType = type, Span = expr.Span.To(type.Span) };
            }
            return expr;
        }

        Expr ParseUnaryOperand()
        {
            Token t = Current;

            // atomic x is v when old, as a value: true if x was old (a compare and swap)
            if (AtWord("atomic"))
            {
                Advance();
                Expr target = ParsePostfix();
                if (!AcceptWord("is")) throw Error(Current.Span, "as a value, atomic is a compare and swap: atomic x is v when old");
                Expr value = ParseBitOr();
                if (!AcceptWord("when")) throw Error(Current.Span, "as a value, atomic is a compare and swap: atomic x is v when old");
                Expr expected = ParseBitOr();
                return new AtomicExpr { Target = target, Value = value, Expected = expected, Span = t.Span.To(Previous.Span) };
            }

            if (At(TokenKind.Minus) || At(TokenKind.Bang) || At(TokenKind.Tilde) || AtWord("not"))
            {
                Advance();
                Expr operand = ParseUnaryOperand();

                if (t.Kind == TokenKind.Minus && operand is NumberExpr n)
                    return new NumberExpr { Value = -n.Value, Span = t.Span.To(n.Span) };
                if (t.Kind == TokenKind.Minus && operand is DecimalExpr d)
                    return new DecimalExpr { Value = -d.Value, Span = t.Span.To(d.Span) };

                UnaryOp op = t.Kind switch
                {
                    TokenKind.Minus => UnaryOp.Negate,
                    TokenKind.Tilde => UnaryOp.BitNot,
                    _ => UnaryOp.Not,
                };
                return new UnaryExpr { Op = op, Operand = operand, Span = t.Span.To(operand.Span) };
            }

            if (AtWord("new"))
            {
                Advance();
                TypeRef type = ParseType();
                Expr? count = null;
                Span end = type.Span;
                if (At(TokenKind.OpenBracket) && !Current.StartsLine)
                {
                    Advance();
                    count = ParseExpression();
                    end = Expect(TokenKind.CloseBracket, "']'").Span;
                }
                return new NewExpr { ElementType = type, Count = count, Span = t.Span.To(end) };
            }

            if (AtWord("addr"))
            {
                Advance();
                bool foreign = AcceptWord("foreign");
                Expr operand = ParsePostfix();
                return new AddrExpr { Operand = operand, Foreign = foreign, Span = t.Span.To(operand.Span) };
            }

            if (AtWord("sizeof"))
            {
                Advance();
                // a type keyword or ptr<T> is a type; a name may be a struct or a variable,
                // which the binder decides
                if (Current.Kind == TokenKind.Identifier
                    && (TypeKeywords.Contains(Current.Text) || given != null && given.ContainsKey(Current.Text) || IsName(Current) && TypesEndAhead(1) > 0))
                {
                    TypeRef type = ParseType();
                    return new SizeofExpr { TypeName = type, Span = t.Span.To(type.Span) };
                }
                Expr operand = ParsePostfix();
                return new SizeofExpr { Operand = operand, Span = t.Span.To(operand.Span) };
            }

            if (AtWord("in") || AtWord("inw") || AtWord("ind"))
            {
                Advance();
                Expr port = ParseUnaryOperand();
                int size = t.Text == "in" ? 1 : t.Text == "inw" ? 2 : 4;
                return new InExpr { Size = size, Port = port, Span = t.Span.To(port.Span) };
            }

            return ParsePostfix();
        }

        /// <summary>name, name:index, name:i:j, tasks:i.field, (Find: id).field, (Find: id):0.field</summary>
        /// <param name="stopAtArguments">
        /// For a callee: a ':' followed by a space starts the arguments, not an index.
        /// </param>
        Expr ParsePostfix(bool stopAtArguments = false)
        {
            bool inParens = At(TokenKind.OpenParen);
            Expr expr = ParsePrimary();
            // (a call's result can be used as a variable's can)
            bool result = inParens && expr is CallExpr or NameExpr { InParens: true } or MemberExpr { InParens: true };

            while (!Current.StartsLine)
            {
                if (stopAtArguments && At(TokenKind.Colon) && PeekToken(1).Span.Start > Current.Span.End) break;

                if (At(TokenKind.Colon) && (result || expr is NameExpr or IndexExpr or MemberExpr))
                {
                    Advance();
                    Expr index = ParseIndex();
                    expr = new IndexExpr { Target = expr, Index = index, Span = expr.Span.To(index.Span) };
                }
                else if (At(TokenKind.Dot) && (result || expr is IndexExpr or MemberExpr) && PeekToken(1).Kind == TokenKind.Identifier)
                {
                    result = false;
                    Advance();
                    Token member = Advance();
                    expr = new MemberExpr { Target = expr, Member = member.Text, MemberSpan = member.Span, Span = expr.Span.To(member.Span) };
                }
                else break;
            }

            return expr;
        }

        /// <summary>
        /// The index after ':' is a number, a single name, a class's constant or variable
        /// (list:Wm.NOTEPAD) or a parenthesized expression, so that tasks:i.id means
        /// (tasks:i).id. Write arr:(t.count) for anything longer.
        /// </summary>
        Expr ParseIndex()
        {
            if (IsName(Current))
            {
                Token name = Advance();
                if (At(TokenKind.Dot) && PeekToken(1).Kind == TokenKind.Identifier && !PeekToken(1).StartsLine
                    && IsClassMember(name.Text, PeekToken(1).Text))
                {
                    Advance();
                    Token member = Advance();
                    return new NameExpr { Name = $"{name.Text}.{member.Text}", Span = name.Span.To(member.Span) };
                }
                return new NameExpr { Name = name.Text, Span = name.Span };
            }
            return ParsePrimary();
        }

        /// <summary>
        /// Class names are PascalCase and variables lowerCamelCase, so Wm.NOTEPAD and Wm.count
        /// name a class's member, while i.id and CURRENT.id are a field of the element.
        /// </summary>
        static bool IsClassMember(string first, string member) =>
            char.IsUpper(first[0]) && (first.Any(char.IsLower) || member.All(c => !char.IsLower(c)));

        /// <summary>What's after a '(': a call, (Name), one value, or several that make one text.</summary>
        Expr ParseInParens(Token open)
        {
            if (AtCallInParens()) return ParseCallExpr(open);

            Expr inner = ParseExpression();

            // ('Hello ' name): several values in parentheses are one text built from them
            if (!At(TokenKind.CloseParen) && !AtEnd)
            {
                var parts = new List<Expr> { inner };
                while (!At(TokenKind.CloseParen) && !AtEnd)
                    parts.Add(ParseListElement());
                Token close = Expect(TokenKind.CloseParen, "')'");
                var text = new TextExpr { Span = open.Span.To(close.Span) };
                text.Parts.AddRange(parts);
                return text;
            }
            Expect(TokenKind.CloseParen, "')'");
            // (Name) may be a call with no arguments, which the binder decides
            if (inner is NameExpr named) named.InParens = true;
            if (inner is MemberExpr member) member.InParens = true;
            return inner;
        }

        Expr ParsePrimary()
        {
            Token t = Current;

            switch (t.Kind)
            {
                case TokenKind.Number:
                    Advance();
                    if (t.Value is ulong huge)
                        return new NumberExpr { Value = unchecked((long)huge), Unsigned64 = true, Span = t.Span };
                    return new NumberExpr { Value = (long)t.Value!, Span = t.Span };

                case TokenKind.Decimal:
                    Advance();
                    return new DecimalExpr { Value = (double)t.Value!, Span = t.Span };

                case TokenKind.String:
                    Advance();
                    return new StringExpr { Value = (string)t.Value!, Span = t.Span };

                case TokenKind.OpenParen:
                {
                    Advance();
                    parenDepth++;
                    try { return ParseInParens(t); }
                    finally { parenDepth--; }
                }

                case TokenKind.Identifier:
                    if (t.Text is "true" or "false")
                    {
                        Advance();
                        return new BoolExpr { Value = t.Text == "true", Span = t.Span };
                    }
                    if (t.Text == "empty")
                    {
                        Advance();
                        return new EmptyExpr { Span = t.Span };
                    }
                    if (keywordNames.Contains(t.Text))
                    {
                        Advance();
                        return new NameExpr { Name = t.Text, Span = t.Span };
                    }
                    // asm.Name { ... } (and struct., enum., phi.) written inside a class
                    // (sys.GetPid): a system call that takes nothing, as (Name) calls a method
                    if (t.Text is "sys" && PeekToken(1).Kind == TokenKind.Dot)
                    {
                        string callee = ParseQualifiedName(out Span calleeSpan);
                        return new CallExpr { Callee = callee, CalleeSpan = calleeSpan, Span = calleeSpan };
                    }
                    if (t.Text is "asm" or "x64" or "arm" or "struct" or "enum" or "interface" or "phi" && PeekToken(1).Kind == TokenKind.Dot)
                        throw Error(t.Span, $"{t.Text}. blocks go at the top level of the file, outside any class" +
                                            (t.Text is "asm" or "arm" ? $"; write {t.Text}.Name {{ ... }} there and call Name; from the class" : ""));
                    if (ReservedWords.Contains(t.Text))
                        throw Error(t.Span, $"expected a value but found the keyword '{t.Text}'");

                    string name = ParseQualifiedName(out Span span);
                    return new NameExpr { Name = name, Span = span };
            }

            throw Error(t.Span, $"expected a value but found {t}");
        }
    }
}

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
            "u8", "u16", "u32", "i8", "i16", "i32", "i64", "u64", "f16", "f32", "f64", "ptr", "method",
        };

        /// <summary>Words that can't be used as variable, method or type names.</summary>
        public static readonly HashSet<string> ReservedWords = new(TypeKeywords)
        {
            "log", "debug", "ask", "call", "exit", "if", "elif", "else", "while", "each", "when", "unsafe",
            "break", "continue", "return",
            "out", "outw", "outd", "in", "inw", "ind", "addr", "sizeof", "new", "free",
            "is", "not", "and", "or", "has", "true", "false", "end", "isr", "as", "takes", "empty",
            "below", "above", "atmost", "atleast", "at",
            "phi", "asm", "arm", "struct", "enum", "use", "const",
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

        public Parser(SourceFile file, DiagnosticBag diagnostics)
        {
            this.file = file;
            this.diagnostics = diagnostics;
            tokens = new Lexer(file, diagnostics).Lex();
        }

        public static ProgramNode Parse(SourceFile file, DiagnosticBag diagnostics) =>
            new Parser(file, diagnostics).ParseProgram();

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
            || (IsName(Current) && AtKeywordAsDeclaredName);

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

        bool AtNewDeclaration => AtWord("new") && PeekToken(1).Kind == TokenKind.Identifier && PeekToken(2).Kind == TokenKind.Identifier;

        bool AtTopLevelHeader =>
            Current.StartsLine && (AtWord("phi") || AtWord("asm") || AtWord("arm") || AtWord("struct") || AtWord("enum")) && PeekToken(1).Kind == TokenKind.Dot
            || Current.StartsLine && AtWord("use");

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

        ProgramNode ParseProgram()
        {
            var program = new ProgramNode { File = file };

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

            if (AtWord("use"))
            {
                Advance();
                string path = ParseQualifiedName(out Span span);
                ExpectSemicolon();
                program.Uses.Add(new UseDecl { Path = path, Span = start.Span.To(span) });
                return;
            }

            if ((AtWord("phi") || AtWord("asm") || AtWord("arm") || AtWord("struct") || AtWord("enum")) && PeekToken(1).Kind == TokenKind.Dot)
            {
                string kind = Advance().Text;
                Advance(); // .
                Token name = ExpectIdentifier("a name");

                switch (kind)
                {
                    case "phi":
                        program.Classes.Add(ParseClass(start, name));
                        return;
                    case "struct":
                        program.Structs.Add(ParseStruct(start, name, program));
                        return;
                    case "enum":
                        program.Enums.Add(ParseEnum(start, name));
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
                        cls.Body.Add(ParseStatement());
                    }
                }
                catch (ParseError)
                {
                    Synchronize();
                }
                if (pos == before) Advance();
            }

            Expect(TokenKind.CloseBrace, $"'}}' to close class {name.Text}");
            return cls;
        }

        StructDecl ParseStruct(Token start, Token name, ProgramNode program)
        {
            Expect(TokenKind.OpenBrace, "'{'");
            var decl = new StructDecl { File = file, Name = name.Text, Span = start.Span.To(name.Span) };
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

                    bool isNew = AcceptWord("new");
                    TypeRef type = ParseType();
                    Token field = ExpectIdentifier("a field name");
                    CheckNotKeyword(field, "field");
                    Expr? count = null;
                    bool filled = false;
                    bool unsized = false;
                    if (At(TokenKind.OpenBracket))
                        (count, filled, unsized) = ParseBufferSize();
                    ExpectSemicolon();
                    decl.Fields.Add(new FieldDecl { Type = type, Name = field.Text, Count = count, Filled = filled, Unsized = unsized, IsNew = isNew, Span = type.Span.To(field.Span) });
                }
                catch (ParseError)
                {
                    Synchronize();
                }
                if (pos == before) Advance();
            }

            Expect(TokenKind.CloseBrace, $"'}}' to close struct {name.Text}");
            return decl;
        }

        /// <summary>int, u16, Task, ptr&lt;u8&gt;, ptr&lt;ptr&lt;u8&gt;&gt;</summary>
        TypeRef ParseType()
        {
            Token name = ExpectIdentifier("a type");
            if (name.Text == "method") return ParseMethodType(name);
            if (name.Text != "ptr") return new TypeRef { Name = name.Text, Span = name.Span };

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

        /// <summary>method&lt;int ptr&lt;u8&gt;: int&gt;: the parameters' types, then ': ' and the result's.</summary>
        TypeRef ParseMethodType(Token name)
        {
            Expect(TokenKind.Less, "'<' after method, as in method<int: int>");
            var parameters = new List<TypeRef>();
            while (!At(TokenKind.Greater) && !At(TokenKind.Colon) && !At(TokenKind.GreaterGreater))
            {
                if (AtEnd || At(TokenKind.Semicolon)) throw Error(Current.Span, "expected '>' to close method<...>");
                parameters.Add(ParseType());
            }

            TypeRef? result = Accept(TokenKind.Colon) ? ParseType() : null;

            Token close = Current;
            if (At(TokenKind.GreaterGreater))
                tokens[pos] = new Token(TokenKind.Greater, new Span(close.Span.Start + 1, 1), ">", null, false);
            else
                close = Expect(TokenKind.Greater, "'>' to close method<...>");

            var type = new TypeRef { Name = "method", Result = result, Span = name.Span.To(close.Span) };
            type.Parameters.AddRange(parameters);
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
                    // (new Node list[] is an array of objects, which only new says)
                    if (AtNewDeclaration && !(PeekToken(3).Kind == TokenKind.OpenBracket && PeekToken(4).Kind == TokenKind.CloseBracket))
                        throw Error(Current.Span, "a parameter borrows the caller's object, so it's written without new: Point p;");
                    if (!AtDeclaration && !AtNewDeclaration)
                        throw Error(Current.Span, $"expected a parameter like 'int count: 0;' but found {Current}");
                    method.Parameters.Add(ParseVarDecl(allowBracketEnd: true));
                }
            }
            Expect(TokenKind.CloseBracket, "']' to close the method header");

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
            Expect(TokenKind.CloseBracket, "']' after [end");

            return method;
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
                if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon) || (Current.StartsLine && values.Count > 0))
                    break;
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

        Stmt ParseCall()
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

            if (Accept(TokenKind.Colon))
            {
                while (!At(TokenKind.Semicolon))
                {
                    if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon))
                        throw Error(new Span(Previous.Span.End, 0), "expected ';' to end the call");
                    arguments.Add(ParseListElement());
                }
            }
            ExpectSemicolon();

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
            if (!IsName(PeekToken(i))) return -1;
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
            while (!At(TokenKind.CloseParen))
            {
                if (AtEnd || At(TokenKind.Semicolon) || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon))
                    throw Error(new Span(Previous.Span.End, 0), $"expected ')' to end the call to {callee}");
                arguments.Add(ParseListElement());
            }
            Token close = Advance();

            var call = new CallExpr { Callee = callee, CalleeSpan = calleeSpan, CalleeTarget = calleeTarget, Span = open.Span.To(close.Span) };
            call.Arguments.AddRange(arguments);
            return call;
        }

        /// <summary>
        /// Looks ahead for `name is` (or `name:index is`) before the callee. A ':' followed by a
        /// space starts the arguments, so the 'is' in `call Show: a is 1;` is a comparison.
        /// </summary>
        bool FindIsBeforeCallee()
        {
            for (int i = 1; i < 8; i++)
            {
                Token t = PeekToken(i);
                if (t.IsWord("is")) return true;
                if (t.Kind is TokenKind.Semicolon or TokenKind.EndOfFile || t.StartsLine) return false;
                if (t.Kind == TokenKind.Colon && PeekToken(i + 1).Span.Start > t.Span.End) return false;
            }
            return false;
        }

        /// <summary>The statements that can follow an if's condition on the same line.</summary>
        static readonly string[] OneLineIfWords = { "return", "break", "continue", "call", "exit" };

        Stmt ParseIf()
        {
            Token keyword = Advance();
            Expr condition = ParseExpression();
            Accept(TokenKind.Semicolon);

            var stmt = new IfStmt { Condition = condition, Span = keyword.Span.To(condition.Span) };

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
            bool whole = AtEndOfValues;
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
                    while (!AtEndOfValues) values.Add(ParseListElement());
            }

            if (requireSemicolon) ExpectSemicolon();

            return new AssignStmt
            {
                Target = target, Op = op, Value = value, Values = values, ValueIsWhole = whole, Bare = bare,
                Span = target.Span.To(Previous.Span),
            };
        }

        bool AtEndOfValues => At(TokenKind.Semicolon) || AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon)
                              || Current.StartsLine;

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
        bool AtOperator(TokenKind kind) => At(kind) && !Current.StartsLine;
        bool AtOperatorWord(string word) => AtWord(word) && !Current.StartsLine;

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

        bool AtComparisonWord => !Current.StartsLine && Current.Kind == TokenKind.Identifier && ComparisonWords.ContainsKey(Current.Text);

        Expr ParseComparison()
        {
            Expr left = ParseBitOr();

            BinaryOp? op = null;
            if (AtOperatorWord("is"))
            {
                Advance();
                op = AcceptWord("not") ? BinaryOp.NotEqual : BinaryOp.Equal;
            }
            else if (AtComparisonWord)
            {
                op = ComparisonWords[Advance().Text];
            }
            else if (AtOperatorWord("has"))
            {
                throw Error(Current.Span, "'has' is not supported yet");
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

            bool another = !Current.StartsLine && (AtWord("is") || AtComparisonWord || Current.Kind is TokenKind.EqualsEquals or TokenKind.BangEquals
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

        /// <summary>A unary expression, then any number of `as Type`: w:0.data as ptr&lt;Note&gt;</summary>
        Expr ParseUnary()
        {
            Expr expr = ParseUnaryOperand();
            while (AtOperatorWord("as"))
            {
                Advance();
                TypeRef type = ParseType();
                expr = new CastExpr { Operand = expr, TargetType = type, Span = expr.Span.To(type.Span) };
            }
            return expr;
        }

        Expr ParseUnaryOperand()
        {
            Token t = Current;

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
                Expr operand = ParsePostfix();
                return new AddrExpr { Operand = operand, Span = t.Span.To(operand.Span) };
            }

            if (AtWord("sizeof"))
            {
                Advance();
                // a type keyword or ptr<T> is a type; a name may be a struct or a variable,
                // which the binder decides
                if (Current.Kind == TokenKind.Identifier && TypeKeywords.Contains(Current.Text))
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

        /// <summary>name, name:index, name:i:j, tasks:i.field</summary>
        /// <param name="stopAtArguments">
        /// For a callee: a ':' followed by a space starts the arguments, not an index.
        /// </param>
        Expr ParsePostfix(bool stopAtArguments = false)
        {
            Expr expr = ParsePrimary();

            while (!Current.StartsLine)
            {
                if (stopAtArguments && At(TokenKind.Colon) && PeekToken(1).Span.Start > Current.Span.End) break;

                if (At(TokenKind.Colon) && expr is NameExpr or IndexExpr or MemberExpr)
                {
                    Advance();
                    Expr index = ParseIndex();
                    expr = new IndexExpr { Target = expr, Index = index, Span = expr.Span.To(index.Span) };
                }
                else if (At(TokenKind.Dot) && expr is IndexExpr or MemberExpr && PeekToken(1).Kind == TokenKind.Identifier)
                {
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
                    if (AtCallInParens()) return ParseCallExpr(t);

                    Expr inner = ParseExpression();
                    Expect(TokenKind.CloseParen, "')'");
                    // (Name) may be a call with no arguments, which the binder decides
                    if (inner is NameExpr named) named.InParens = true;
                    if (inner is MemberExpr member) member.InParens = true;
                    return inner;
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
                    if (t.Text is "asm" or "arm" or "struct" or "enum" or "phi" && PeekToken(1).Kind == TokenKind.Dot)
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

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
            "str", "int", "byt", "bln", "dec", "fin", "var",
            "u8", "u16", "u32", "i8", "i16", "i32", "ptr",
        };

        /// <summary>Words that can't be used as variable, method or type names.</summary>
        public static readonly HashSet<string> ReservedWords = new(TypeKeywords)
        {
            "log", "debug", "ask", "call", "exit", "if", "elif", "else", "while", "unsafe",
            "out", "outw", "outd", "in", "inw", "ind", "addr", "new", "free",
            "is", "not", "and", "or", "has", "true", "false", "end", "isr",
            "phi", "asm", "arm", "struct", "use", "const",
        };

        sealed class ParseError : Exception { }

        readonly SourceFile file;
        readonly DiagnosticBag diagnostics;
        readonly List<Token> tokens;
        int pos;

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

        /// <summary>A declaration starts with a type keyword, or with a struct name followed by a name.</summary>
        bool AtDeclaration =>
            (Current.Kind == TokenKind.Identifier && TypeKeywords.Contains(Current.Text))
            || (IsName(Current) && IsName(PeekToken(1)) && !PeekToken(1).StartsLine);

        bool AtTopLevelHeader =>
            Current.StartsLine && (AtWord("phi") || AtWord("asm") || AtWord("arm") || AtWord("struct")) && PeekToken(1).Kind == TokenKind.Dot
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

            if ((AtWord("phi") || AtWord("asm") || AtWord("arm") || AtWord("struct")) && PeekToken(1).Kind == TokenKind.Dot)
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
                        program.Structs.Add(ParseStruct(start, name));
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

        StructDecl ParseStruct(Token start, Token name)
        {
            Expect(TokenKind.OpenBrace, "'{'");
            var decl = new StructDecl { File = file, Name = name.Text, Span = start.Span.To(name.Span) };

            while (!At(TokenKind.CloseBrace) && !AtEnd)
            {
                int before = pos;
                try
                {
                    TypeRef type = ParseType();
                    Token field = ExpectIdentifier("a field name");
                    Expr? count = null;
                    if (Accept(TokenKind.OpenBracket))
                    {
                        count = ParseExpression();
                        Expect(TokenKind.CloseBracket, "']'");
                    }
                    ExpectSemicolon();
                    decl.Fields.Add(new FieldDecl { Type = type, Name = field.Text, Count = count, Span = type.Span.To(field.Span) });
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
                    if (!AtDeclaration)
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
            if (AtDeclaration) return ParseVarDecl(allowBracketEnd: false);

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
                        Expr port = ParseExpression();
                        Expr value = ParseExpression();
                        ExpectSemicolon();
                        int size = start.Text == "out" ? 1 : start.Text == "outw" ? 2 : 4;
                        return new OutStmt { Size = size, Port = port, Value = value, Span = start.Span.To(Previous.Span) };
                    }
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
            TypeRef type = ParseType();
            Token name = ExpectIdentifier("a variable name");

            Expr? buffer = null;
            var values = new List<Expr>();

            if (At(TokenKind.OpenBracket))
            {
                buffer = ParseBufferSize();
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
                    buffer = ParseBufferSize();
                }
                else
                {
                    while (!At(TokenKind.Semicolon) && !(allowBracketEnd && At(TokenKind.CloseBracket)))
                    {
                        if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon))
                            throw Error(new Span(Previous.Span.End, 0), $"expected ';' to end the declaration of '{name.Text}'");
                        values.Add(ParseExpression());
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
                Span = type.Span.To(Previous.Span),
            };
            decl.Values.AddRange(values);

            if (!(allowBracketEnd && At(TokenKind.CloseBracket))) ExpectSemicolon();
            return decl;
        }

        Expr ParseBufferSize()
        {
            Expect(TokenKind.OpenBracket, "'['");
            Expr size = ParseExpression();
            Expect(TokenKind.CloseBracket, "']'");
            return size;
        }

        Stmt ParseLog()
        {
            Token keyword = Advance();
            var values = new List<Expr>();

            while (!At(TokenKind.Semicolon))
            {
                if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon) || (Current.StartsLine && values.Count > 0))
                    break;
                values.Add(ParseExpression());
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

            string callee = ParseQualifiedName(out Span calleeSpan);
            var arguments = new List<Expr>();

            if (Accept(TokenKind.Colon))
            {
                while (!At(TokenKind.Semicolon))
                {
                    if (AtEnd || At(TokenKind.CloseBrace) || At(TokenKind.DoubleSemicolon))
                        throw Error(new Span(Previous.Span.End, 0), "expected ';' to end the call");
                    arguments.Add(ParseExpression());
                }
            }
            ExpectSemicolon();

            var call = new CallStmt
            {
                ResultTarget = target,
                Callee = callee,
                CalleeSpan = calleeSpan,
                Span = keyword.Span.To(Previous.Span),
            };
            call.Arguments.AddRange(arguments);
            return call;
        }

        /// <summary>Looks ahead for `name is` (or `name:index is`) before the callee.</summary>
        bool FindIsBeforeCallee()
        {
            for (int i = 1; i < 8; i++)
            {
                Token t = PeekToken(i);
                if (t.IsWord("is")) return true;
                if (t.Kind is TokenKind.Semicolon or TokenKind.EndOfFile || t.StartsLine) return false;
            }
            return false;
        }

        Stmt ParseIf()
        {
            Token keyword = Advance();
            Expr condition = ParseExpression();
            Accept(TokenKind.Semicolon);

            var stmt = new IfStmt { Condition = condition, Span = keyword.Span.To(condition.Span) };
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

        Stmt ParseWhile()
        {
            Token keyword = Advance();

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
            if (bare && op is AssignOp.Add or AssignOp.Subtract)
                value = new NumberExpr { Value = 1, Span = opToken.Span };
            else
                value = ParseExpression();

            if (requireSemicolon) ExpectSemicolon();

            return new AssignStmt { Target = target, Op = op, Value = value, Span = target.Span.To(Previous.Span) };
        }

        // ------------------------------------------------------------ expressions
        //
        // lowest to highest: or, and, comparison, |, ^, &, << >>, + -, * / %, unary, postfix

        public Expr ParseExpression() => ParseOr();

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

        Expr ParseComparison()
        {
            Expr left = ParseBitOr();

            BinaryOp? op = null;
            if (AtOperatorWord("is"))
            {
                Advance();
                op = AcceptWord("not") ? BinaryOp.NotEqual : BinaryOp.Equal;
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

            bool another = !Current.StartsLine && (AtWord("is") || Current.Kind is TokenKind.EqualsEquals or TokenKind.BangEquals
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
            while (AtOperator(TokenKind.Plus) || AtOperator(TokenKind.Minus))
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

        Expr ParseUnary()
        {
            Token t = Current;

            if (At(TokenKind.Minus) || At(TokenKind.Bang) || At(TokenKind.Tilde) || AtWord("not"))
            {
                Advance();
                Expr operand = ParseUnary();

                if (t.Kind == TokenKind.Minus && operand is NumberExpr n)
                    return new NumberExpr { Value = -n.Value, Span = t.Span.To(n.Span) };

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

            if (AtWord("in") || AtWord("inw") || AtWord("ind"))
            {
                Advance();
                Expr port = ParseUnary();
                int size = t.Text == "in" ? 1 : t.Text == "inw" ? 2 : 4;
                return new InExpr { Size = size, Port = port, Span = t.Span.To(port.Span) };
            }

            return ParsePostfix();
        }

        /// <summary>name, name:index, name:i:j, tasks:i.field</summary>
        Expr ParsePostfix()
        {
            Expr expr = ParsePrimary();

            while (!Current.StartsLine)
            {
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
        /// The index after ':' is a number, a single name or a parenthesized expression, so that
        /// tasks:i.id means (tasks:i).id. Write arr:(t.count) for anything longer.
        /// </summary>
        Expr ParseIndex()
        {
            if (IsName(Current))
            {
                Token name = Advance();
                return new NameExpr { Name = name.Text, Span = name.Span };
            }
            return ParsePrimary();
        }

        Expr ParsePrimary()
        {
            Token t = Current;

            switch (t.Kind)
            {
                case TokenKind.Number:
                    Advance();
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
                    Expr inner = ParseExpression();
                    Expect(TokenKind.CloseParen, "')'");
                    return inner;
                }

                case TokenKind.Identifier:
                    if (t.Text is "true" or "false")
                    {
                        Advance();
                        return new BoolExpr { Value = t.Text == "true", Span = t.Span };
                    }
                    if (ReservedWords.Contains(t.Text))
                        throw Error(t.Span, $"expected a value but found the keyword '{t.Text}'");

                    string name = ParseQualifiedName(out Span span);
                    return new NameExpr { Name = name, Span = span };
            }

            throw Error(t.Span, $"expected a value but found {t}");
        }
    }
}

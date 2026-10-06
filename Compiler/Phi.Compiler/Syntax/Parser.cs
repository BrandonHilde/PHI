namespace Phi.Compiler.Syntax
{
    /// <summary>
    /// Recursive-descent parser for PHI. See docs/language.md for the grammar.
    /// </summary>
    public sealed class Parser
    {
        static readonly Dictionary<string, TypeKeyword> TypeKeywords = new()
        {
            ["str"] = TypeKeyword.Str,
            ["int"] = TypeKeyword.Int,
            ["byt"] = TypeKeyword.Byt,
            ["bln"] = TypeKeyword.Bln,
            ["dec"] = TypeKeyword.Dec,
            ["fin"] = TypeKeyword.Fin,
            ["var"] = TypeKeyword.Var,
        };

        /// <summary>Words that can't be used as variable or method names.</summary>
        public static readonly HashSet<string> ReservedWords = new()
        {
            "str", "int", "byt", "bln", "dec", "fin", "var",
            "log", "debug", "ask", "call", "exit", "if", "elif", "else", "while",
            "is", "not", "and", "or", "has", "true", "false", "end", "phi", "asm", "arm",
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

        bool IsTypeKeyword(Token t) => t.Kind == TokenKind.Identifier && TypeKeywords.ContainsKey(t.Text);

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
                    // skip to the next class header
                    while (!AtEnd && !(Current.StartsLine && (AtWord("phi") || AtWord("asm") || AtWord("arm")) && PeekToken(1).Kind == TokenKind.Dot))
                        Advance();
                }
                if (pos == before) Advance();
            }

            return program;
        }

        void ParseTopLevel(ProgramNode program)
        {
            Token start = Current;

            if ((AtWord("phi") || AtWord("asm") || AtWord("arm")) && PeekToken(1).Kind == TokenKind.Dot)
            {
                string kind = Advance().Text;
                Advance(); // .
                Token name = ExpectIdentifier("a class name");

                if (kind == "phi")
                {
                    program.Classes.Add(ParseClass(start, name));
                    return;
                }

                Expect(TokenKind.OpenBrace, "'{'");
                Token raw = At(TokenKind.RawBlock) ? Advance() : new Token(TokenKind.RawBlock, new Span(Current.Span.Start, 0), "", "", false);
                Token close = Expect(TokenKind.CloseBrace, "'}'");

                program.RawBlocks.Add(new RawBlockDecl
                {
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

            var cls = new ClassDecl { Name = name.Text, Base = baseName, BaseSpan = baseSpan, Span = start.Span.To(name.Span) };

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
                        Stmt s = ParseStatement();
                        if (s is VarDecl v) cls.Variables.Add(v);
                        cls.Body.Add(s);
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

        // ------------------------------------------------------------ methods

        bool AtMethodEnd => At(TokenKind.OpenBracket) && PeekToken(1).IsWord("end");

        MethodDecl ParseMethod()
        {
            Token open = Expect(TokenKind.OpenBracket, "'['");

            if (AtWord("end")) throw Error(Current.Span, "[end] without a matching method header");

            Span nameSpan;
            string name = ParseQualifiedName(out nameSpan);
            var method = new MethodDecl { Name = name, Span = open.Span.To(nameSpan) };

            if (Accept(TokenKind.Colon))
            {
                while (!At(TokenKind.CloseBracket) && !AtEnd)
                {
                    if (!IsTypeKeyword(Current))
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

            if (IsTypeKeyword(Current))
                return ParseVarDecl(allowBracketEnd: false);

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
                        Span s;
                        string name = ParseQualifiedName(out s);
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

        VarDecl ParseVarDecl(bool allowBracketEnd)
        {
            Token typeToken = Advance();
            Token name = ExpectIdentifier("a variable name");
            TypeKeyword type = TypeKeywords[typeToken.Text];

            int? buffer = null;
            var values = new List<Expr>();

            if (At(TokenKind.OpenBracket))
            {
                buffer = ParseBufferSize();
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
                Span = typeToken.Span.To(Previous.Span),
            };
            decl.Values.AddRange(values);

            if (!(allowBracketEnd && At(TokenKind.CloseBracket))) ExpectSemicolon();
            return decl;
        }

        int ParseBufferSize()
        {
            Expect(TokenKind.OpenBracket, "'['");
            Token size = Expect(TokenKind.Number, "a buffer size");
            Expect(TokenKind.CloseBracket, "']'");

            long n = (long)size.Value!;
            if (n < 1 || n > 0x4000) throw Error(size.Span, "buffer size must be between 1 and 16384");
            return (int)n;
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

            Span calleeSpan;
            string callee = ParseQualifiedName(out calleeSpan);
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
            for (int i = 1; i < 6; i++)
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

            if (IsTypeKeyword(Current) && PeekToken(1).Kind == TokenKind.Identifier)
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
            Expr? value = null;
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

        public Expr ParseExpression() => ParseOr();

        /// <summary>A binary operator only continues an expression on the same line.</summary>
        bool AtOperator(TokenKind kind) => At(kind) && !Current.StartsLine;
        bool AtOperatorWord(string word) => AtWord(word) && !Current.StartsLine;

        Expr ParseOr()
        {
            Expr left = ParseAnd();
            while (AtOperatorWord("or"))
            {
                Advance();
                Expr right = ParseAnd();
                left = new BinaryExpr { Op = BinaryOp.Or, Left = left, Right = right, Span = left.Span.To(right.Span) };
            }
            return left;
        }

        Expr ParseAnd()
        {
            Expr left = ParseComparison();
            while (AtOperatorWord("and"))
            {
                Advance();
                Expr right = ParseComparison();
                left = new BinaryExpr { Op = BinaryOp.And, Left = left, Right = right, Span = left.Span.To(right.Span) };
            }
            return left;
        }

        Expr ParseComparison()
        {
            Expr left = ParseAdditive();

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
                    TokenKind.LessEquals or TokenKind.LessLess => BinaryOp.LessEqual,
                    TokenKind.GreaterEquals or TokenKind.GreaterGreater => BinaryOp.GreaterEqual,
                    _ => null,
                };
                if (op != null) Advance();
            }

            if (op == null) return left;

            Expr right = ParseAdditive();
            return new BinaryExpr { Op = op.Value, Left = left, Right = right, Span = left.Span.To(right.Span) };
        }

        Expr ParseAdditive()
        {
            Expr left = ParseMultiplicative();
            while (AtOperator(TokenKind.Plus) || AtOperator(TokenKind.Minus))
            {
                BinaryOp op = Advance().Kind == TokenKind.Plus ? BinaryOp.Add : BinaryOp.Subtract;
                Expr right = ParseMultiplicative();
                left = new BinaryExpr { Op = op, Left = left, Right = right, Span = left.Span.To(right.Span) };
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
                Expr right = ParseUnary();
                left = new BinaryExpr { Op = op, Left = left, Right = right, Span = left.Span.To(right.Span) };
            }
            return left;
        }

        Expr ParseUnary()
        {
            if (At(TokenKind.Minus) || At(TokenKind.Bang) || AtWord("not"))
            {
                Token op = Advance();
                Expr operand = ParseUnary();

                if (op.Kind == TokenKind.Minus && operand is NumberExpr n)
                    return new NumberExpr { Value = -n.Value, Span = op.Span.To(n.Span) };

                return new UnaryExpr
                {
                    Op = op.Kind == TokenKind.Minus ? UnaryOp.Negate : UnaryOp.Not,
                    Operand = operand,
                    Span = op.Span.To(operand.Span),
                };
            }

            return ParsePostfix();
        }

        /// <summary>name, name:index, name:i:j</summary>
        Expr ParsePostfix()
        {
            Expr expr = ParsePrimary();

            while (At(TokenKind.Colon) && !Current.StartsLine && expr is NameExpr or IndexExpr)
            {
                Advance();
                Expr index = ParsePrimary();
                expr = new IndexExpr { Target = expr, Index = index, Span = expr.Span.To(index.Span) };
            }

            return expr;
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

                    Span span;
                    string name = ParseQualifiedName(out span);
                    return new NameExpr { Name = name, Span = span };
            }

            throw Error(t.Span, $"expected a value but found {t}");
        }
    }
}

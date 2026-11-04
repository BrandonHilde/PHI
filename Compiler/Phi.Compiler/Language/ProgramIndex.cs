using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.Language
{
    public enum NameKind
    {
        Variable, Parameter, Field, Constant, Method, Class, Struct, Interface, Enum, EnumValue,
        Function, Length, AssemblyBlock, SystemCall, OutsideFunction, Group, Keyword, Type,
    }

    /// <summary>A name in a program: what it is, and where it's declared (no file: PHI's own).</summary>
    public sealed record NameInfo(NameKind Kind, string Name, string Detail = "", SourceFile? File = null, Span Span = default,
                                  PhiType? Value = null, object? Declaration = null);

    /// <summary>
    /// What an editor asks of one file of a compiled program: what the name here means, and which
    /// names could go here. It reads the file's tokens around the place, not the binder's result
    /// for that one spot, so that it goes on working in code that's half written, where a.b. and
    /// the like don't parse: the syntax says what's declared and the binder (when it got that
    /// far) what type it is.
    /// </summary>
    public sealed class ProgramIndex
    {
        /// <summary>Words that say what kind of thing the name after their dot is: struct.Point, asm.Copy.</summary>
        static readonly Dictionary<string, NameKind> GroupWords = new()
        {
            ["phi"] = NameKind.Class, ["struct"] = NameKind.Struct, ["interface"] = NameKind.Interface, ["enum"] = NameKind.Enum,
            ["asm"] = NameKind.AssemblyBlock, ["x64"] = NameKind.AssemblyBlock, ["arm"] = NameKind.AssemblyBlock, ["sys"] = NameKind.SystemCall,
        };

        readonly CompileResult result;
        readonly ProgramNode program;
        readonly string fileKey;
        readonly string text;
        readonly IReadOnlyList<Token> tokens;

        /// <summary>The declarations are from an earlier version of the text.</summary>
        readonly bool earlier;

        /// <param name="result">The program the file was compiled in.</param>
        /// <param name="file">The file being asked about, as it is now.</param>
        /// <param name="earlier">
        /// The program is from before the latest changes. What's before the place being edited is
        /// still where it was, so what's around a place is what starts last before it.
        /// </param>
        public ProgramIndex(CompileResult result, SourceFile file, bool earlier = false)
        {
            this.earlier = earlier;
            this.result = result;
            program = result.Syntax ?? new ProgramNode { File = file };
            fileKey = PhiCompiler.FileKey(file.Path);
            text = file.Text;
            tokens = new Lexer(file, new DiagnosticBag()).Lex();
        }

        bool InThisFile(SourceFile? other) => other != null && PhiCompiler.FileKey(other.Path) == fileKey;

        /// <summary>Whether a method or class of this file is around the place: if not, the file didn't parse there.</summary>
        public bool Knows(int offset) => EnclosingClass(offset) != null;

        /// <summary>How many classes of this file parsed: fewer than before means one isn't closed for now.</summary>
        public int ClassesKnown => program.Classes.Count(c => InThisFile(c.File));

        // ------------------------------------------------------------ going to a definition

        /// <summary>The file that the `use` here loads, if there's one here.</summary>
        public SourceFile? UsedFileAt(int offset)
        {
            foreach (var (use, (from, target)) in result.Uses)
                if (use.Span.Start <= offset && offset <= use.Span.End && InThisFile(from))
                    return target;
            return null;
        }

        /// <summary>What the name at this place is, or null if there's no name or it's unknown.</summary>
        public NameInfo? DefinitionAt(int offset)
        {
            int at = NameTokenAt(offset);
            if (at < 0) return null;
            List<Segment> chain = ChainEndingAt(at);
            return Follow(chain, offset, IsCallAt(at));
        }

        int NameTokenAt(int offset)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                Token t = tokens[i];
                if (t.Span.Start > offset) break;
                if (t.Kind == TokenKind.Identifier && offset <= t.Span.End) return i;
            }
            return -1;
        }

        /// <summary>call Name, or (Name: 1): a method is meant, even with a variable of that name.</summary>
        bool IsCallAt(int at) =>
            at > 0 && tokens[at - 1].IsWord("call") || at + 1 < tokens.Count && tokens[at + 1].Kind == TokenKind.Colon && tokens[at + 1].Span.End < tokens[Math.Min(at + 2, tokens.Count - 1)].Span.Start;

        // ------------------------------------------------------------ a.b:i.c

        /// <param name="Indexes">How many :i come after this name, before the next dot.</param>
        readonly record struct Segment(string Name, int Indexes);

        List<Segment> ChainEndingAt(int at)
        {
            var chain = new List<Segment>();
            if (at > 0 && tokens[at - 1].Kind == TokenKind.Dot) chain = BaseBeforeDot(at - 1);
            chain.Add(new Segment(tokens[at].Text, 0));
            return chain;
        }

        /// <summary>What's before the dot at this token: tasks:i.id's tasks, indexed once.</summary>
        List<Segment> BaseBeforeDot(int dot)
        {
            var reversed = new List<Segment>();
            int j = dot - 1;
            while (j >= 0)
            {
                int indexes = 0;
                while (IndexStartingBefore(j) is int before)
                {
                    indexes++;
                    j = before;
                }
                if (j < 0 || tokens[j].Kind != TokenKind.Identifier) break;
                reversed.Add(new Segment(tokens[j].Text, indexes));
                if (j == 0 || tokens[j - 1].Kind != TokenKind.Dot) break;
                j -= 2;
            }
            reversed.Reverse();
            return reversed;
        }

        /// <summary>
        /// When the tokens ending at j are one index (:i, :0 or :(i + 1), with no space around the
        /// colon, which is what makes it an index), the token before that index.
        /// </summary>
        int? IndexStartingBefore(int j)
        {
            if (j < 0) return null;
            int start = j;
            if (tokens[j].Kind == TokenKind.CloseParen)
            {
                int depth = 0;
                for (; start >= 0; start--)
                {
                    if (tokens[start].Kind == TokenKind.CloseParen) depth++;
                    else if (tokens[start].Kind == TokenKind.OpenParen && --depth == 0) break;
                }
                if (start < 0) return null;
            }
            else if (tokens[j].Kind is not (TokenKind.Identifier or TokenKind.Number)) return null;

            int colon = start - 1;
            if (colon < 1 || tokens[colon].Kind != TokenKind.Colon) return null;
            if (tokens[colon].Span.End != tokens[start].Span.Start || tokens[colon - 1].Span.End != tokens[colon].Span.Start) return null;
            return colon - 1;
        }

        /// <summary>The name a whole chain comes to.</summary>
        NameInfo? Follow(List<Segment> chain, int offset, bool preferMethod = false)
        {
            if (chain.Count == 0) return null;
            NameInfo? current = chain.Count > 1 && GroupWords.ContainsKey(chain[0].Name)
                ? new NameInfo(NameKind.Group, chain[0].Name)
                : Head(chain[0].Name, offset, preferMethod && chain.Count == 1);
            for (int i = 1; i < chain.Count && current != null; i++)
            {
                string wanted = chain[i].Name;
                bool wantMethod = preferMethod && i == chain.Count - 1;
                IEnumerable<NameInfo> members = MembersOf(current, chain[i - 1].Indexes).Where(m => m.Name == wanted);
                current = (wantMethod ? members.FirstOrDefault(m => m.Kind == NameKind.Method) : null) ?? members.FirstOrDefault();
            }
            return current;
        }

        /// <summary>A plain name at this place, in the order the binder looks: here, this class, then the rest of the program.</summary>
        NameInfo? Head(string name, int offset, bool preferMethod)
        {
            ClassDecl? own = EnclosingClass(offset);
            MethodDecl? method = EnclosingMethod(offset);

            if (preferMethod && own != null && MethodsOf(own).FirstOrDefault(m => m.Name == name) is NameInfo ownMethod) return ownMethod;

            if (method != null && Local(method, name, offset) is NameInfo local) return local;
            if (own != null)
            {
                if (method == null && ClassVariable(own, name, offset) is NameInfo variable) return variable;
                if (own.ForStruct != null && FieldsOf(own.ForStruct).FirstOrDefault(f => f.Name == name) is NameInfo field) return field;
                if (MembersOfClass(own).FirstOrDefault(m => m.Name == name) is NameInfo member) return member;
            }

            foreach (ClassDecl other in program.Classes.Where(c => c != own && c.ForStruct == null))
                if (MembersOfClass(other).FirstOrDefault(m => m.Name == name && m.Kind != NameKind.Method) is NameInfo shared) return shared;
            foreach (ClassDecl other in program.Classes.Where(c => c != own))
                if (MethodsOf(other).FirstOrDefault(m => m.Name == name) is NameInfo anyMethod) return anyMethod;

            return TopLevel().FirstOrDefault(t => t.Name == name);
        }

        ClassDecl? EnclosingClass(int offset) =>
            EnclosingMethod(offset)?.Owner
            ?? program.Classes.Where(c => InThisFile(c.File)).LastOrDefault(c => c.Span.Start <= offset && (earlier || offset <= c.End));

        MethodDecl? EnclosingMethod(int offset) =>
            program.Classes.Where(c => InThisFile(c.File)).SelectMany(c => c.Methods)
                .Where(m => m.Span.Start <= offset && (earlier || offset <= m.End))
                .MaxBy(m => m.Span.Start);

        // ------------------------------------------------------------ what's declared

        /// <summary>
        /// The variables of a method, or a class's own code. A name declared twice is in separate
        /// blocks, and code that compiles can only mean the last one before it.
        /// </summary>
        NameInfo? Local(MethodDecl method, string name, int offset) => Nearest(DeclaredIn(method), name, offset, method.Owner.File);

        NameInfo? ClassVariable(ClassDecl cls, string name, int offset) => Nearest(cls.Body.SelectMany(SyntaxWalker.Nodes).OfType<Stmt>(), name, offset, cls.File);

        static IEnumerable<Stmt> DeclaredIn(MethodDecl method) =>
            method.Parameters.Cast<Stmt>().Concat(method.Body.SelectMany(SyntaxWalker.Nodes).OfType<Stmt>());

        NameInfo? Nearest(IEnumerable<Stmt> statements, string name, int offset, SourceFile file)
        {
            NameInfo? first = null, nearest = null;
            foreach (Stmt s in statements)
            {
                NameInfo? info = s switch
                {
                    VarDecl v when v.Name == name => Describe(v, file),
                    ConstDecl c when c.Name == name => Describe(c, file),
                    _ => null,
                };
                if (info == null) continue;
                first ??= info;
                if (info.Span.Start <= offset) nearest = info;
            }
            return nearest ?? first;
        }

        NameInfo Describe(VarDecl v, SourceFile file, NameKind kind = NameKind.Variable)
        {
            PhiType? type = v.Symbol?.Type is { IsError: false } known ? known : FromSyntax(v);
            return new NameInfo(kind, v.Name, type?.ToString() ?? v.Type.ToString(), file, v.NameSpan, type, v);
        }

        static NameInfo Describe(ConstDecl c, SourceFile file) =>
            new(NameKind.Constant, c.Name, c.Type.ToString(), file, c.NameSpan, null, c);

        /// <summary>A variable's type from how it's declared. (Several values are an array's, but a struct's fields in one.)</summary>
        PhiType? FromSyntax(VarDecl v)
        {
            PhiType? type = FromSyntax(v.Type);
            bool array = v.BufferSize != null && !v.SizeAfterColon || v.Filled || v.Unsized || v.Values.Count > 1 && type is { IsStruct: false };
            return array ? type?.ArrayOf(1) : type;
        }

        /// <summary>A type as far as can be told from how it's written, for code the binder didn't get to.</summary>
        PhiType? FromSyntax(TypeRef type)
        {
            if (type.Name == "ptr" && type.Pointee != null) return FromSyntax(type.Pointee) is PhiType p ? PhiType.PointerTo(p) : null;
            if (type.Name == "str") return PhiType.Str;
            return program.Structs.FirstOrDefault(s => s.Name == type.Name)?.Symbol is StructSymbol symbol ? PhiType.Of(symbol) : null;
        }

        static Span NameAtEndOf(Span span, string name) => new(span.End - name.Length, name.Length);

        IEnumerable<NameInfo> MethodsOf(ClassDecl cls) =>
            cls.Methods.Select(m => DescribeMethod(m, cls.File, cls.Name));

        static NameInfo DescribeMethod(MethodDecl m, SourceFile file, string owner)
        {
            // (a struct's methods have self first, which the caller doesn't write)
            var written = m.Parameters.Where(p => p.Name != "self").Select(p => $"{p.Type} {p.Name}").ToList();
            string result = m.Symbol is { ResultBound: true, ReturnType.Kind: not TypeKind.Void } s ? $": {s.ReturnType}" : "";
            string parameters = written.Count > 0 ? ": " + string.Join("; ", written) : "";
            return new NameInfo(NameKind.Method, m.Name.Split('.')[^1], $"[{owner}.{m.Name}{parameters}]{result}", file, NameAtEndOf(m.Span, m.Name), null, m);
        }

        /// <summary>A class's variables and constants at its top level (and its blocks'), and its methods.</summary>
        IEnumerable<NameInfo> MembersOfClass(ClassDecl cls)
        {
            var seen = new HashSet<string>();
            foreach (Node node in cls.Body.SelectMany(SyntaxWalker.Nodes))
            {
                if (node is VarDecl v && IsWritten(v) && seen.Add(v.Name)) yield return Describe(v, cls.File);
                else if (node is ConstDecl c && seen.Add(c.Name)) yield return Describe(c, cls.File);
            }
            foreach (NameInfo m in MethodsOf(cls)) yield return m;
        }

        /// <summary>Not one the parser makes up for a loop's counter (each#123).</summary>
        static bool IsWritten(VarDecl v) => !v.Name.Contains('#');

        IEnumerable<NameInfo> FieldsOf(StructDecl s) =>
            s.Fields.Select(f =>
            {
                PhiType? type = s.Symbol?.Find(f.Name)?.Type is { IsError: false } known ? known
                    : f.Count != null || f.Filled || f.Unsized ? FromSyntax(f.Type)?.ArrayOf(1)
                    : FromSyntax(f.Type);
                return new NameInfo(NameKind.Field, f.Name, type?.ToString() ?? f.Type.ToString(), s.File, NameAtEndOf(f.Span, f.Name), type, f);
            });

        IEnumerable<NameInfo> MethodsOfStruct(StructDecl s)
        {
            foreach (MethodDecl m in s.Methods) yield return DescribeMethod(m, s.File, s.Name);
            foreach (ClassDecl c in program.Classes.Where(c => c.ForStruct == s))
                foreach (NameInfo m in MethodsOf(c)) yield return m;
        }

        static NameInfo Length(string name) => new(NameKind.Length, name, name == "len" ? "how many are in use" : "the most it can hold", Value: PhiType.U32);

        /// <summary>What's after the dot on this.</summary>
        /// <param name="indexes">How many times it was indexed before the dot.</param>
        IEnumerable<NameInfo> MembersOf(NameInfo owner, int indexes)
        {
            switch (owner.Kind)
            {
                case NameKind.Group when GroupWords.TryGetValue(owner.Name, out NameKind kind):
                    return kind switch
                    {
                        NameKind.AssemblyBlock => program.RawBlocks.Where(b => b.Language == owner.Name).Select(DescribeBlock),
                        NameKind.SystemCall => program.SysCalls.Select(DescribeSystemCall),
                        NameKind.Struct or NameKind.Interface => program.Structs.Select(DescribeStruct),
                        NameKind.Enum => program.Enums.Select(DescribeEnum),
                        _ => program.Classes.Where(c => c.ForStruct == null).Select(DescribeClass),
                    };
                case NameKind.Group:
                    return BuiltinsIn(owner.Name).Concat(OutsideFunctionsOf(owner.Name));
                case NameKind.Class:
                    return program.Classes.Where(c => c.Name == owner.Name && c.ForStruct == null).SelectMany(MembersOfClass).Concat(BuiltinsIn(owner.Name));
                case NameKind.Struct or NameKind.Interface:
                    return owner.Declaration is StructDecl s ? MethodsOfStruct(s) : Array.Empty<NameInfo>();
                case NameKind.Enum:
                    return owner.Declaration is EnumDecl e
                        ? e.Members.Select(m => new NameInfo(NameKind.EnumValue, m.Name, e.Name, e.File, m.Span))
                        : Array.Empty<NameInfo>();
            }
            return owner.Value is PhiType value ? MembersOfValue(value, indexes) : Array.Empty<NameInfo>();
        }

        IEnumerable<NameInfo> MembersOfValue(PhiType type, int indexes)
        {
            for (int i = 0; i < indexes; i++)
                type = type.IsArray ? type.Element
                     : type.IsPointer ? type.Pointee ?? PhiType.Error
                     : type.IsString ? PhiType.Chr
                     : PhiType.Error;

            if (type.IsArray || type.IsString) return new[] { Length("len"), Length("max") };
            while (type.IsPointer && type.Pointee != null) type = type.Pointee;

            StructDecl? decl = type.Fields?.Decl;
            return decl == null ? Array.Empty<NameInfo>() : FieldsOf(decl).Concat(MethodsOfStruct(decl));
        }

        IEnumerable<NameInfo> BuiltinsIn(string group)
        {
            string prefix = group + ".";
            foreach (BuiltinFunction f in Builtins.Functions)
                foreach (string name in f.Aliases.Prepend(f.Name).Where(n => n.StartsWith(prefix)))
                    yield return new NameInfo(NameKind.Function, name[prefix.Length..],
                                              $"{name}{(f.Parameters.Length > 0 ? ": " + string.Join(" ", f.Parameters) : "")}");
            foreach (HookInfo hook in Builtins.Hooks.Where(h => h.Name.StartsWith(prefix)))
                yield return new NameInfo(NameKind.Function, hook.Name[prefix.Length..], $"[{hook.Name}] event");
            foreach (var (name, value) in Builtins.Constants.Where(c => c.Key.StartsWith(prefix)))
                yield return new NameInfo(NameKind.Constant, name[prefix.Length..], value.ToString());
        }

        IEnumerable<NameInfo> OutsideFunctionsOf(string library) =>
            program.OutsideFunctions.Where(f => f.Library == library)
                .Select(f => new NameInfo(NameKind.OutsideFunction, f.Name, $"{f.Library}.{f.Name}", f.File, NameAtEndOf(f.Span, f.Name)));

        static NameInfo DescribeClass(ClassDecl c) => new(NameKind.Class, c.Name, "class", c.File, NameAtEndOf(c.Span, c.Name), Declaration: c);

        static NameInfo DescribeStruct(StructDecl s) =>
            new(s.IsInterface ? NameKind.Interface : NameKind.Struct, s.Name, s.IsInterface ? "interface" : "struct", s.File, NameAtEndOf(s.Span, s.Name),
                s.Symbol != null ? PhiType.Of(s.Symbol) : null, s);

        static NameInfo DescribeEnum(EnumDecl e) => new(NameKind.Enum, e.Name, "enum", e.File, NameAtEndOf(e.Span, e.Name), Declaration: e);

        static NameInfo DescribeBlock(RawBlockDecl b) =>
            new(NameKind.AssemblyBlock, b.Name, $"{b.Language} block", b.File, new Span(b.Span.Start + b.Language.Length + 1, b.Name.Length));

        static NameInfo DescribeSystemCall(SysCallDecl s) =>
            new(NameKind.SystemCall, s.Name, $"system call {s.Number}", s.File, new Span(s.Span.Start + "sys.".Length, s.Name.Length));

        /// <summary>Every name that's known anywhere in the program, without a class before it.</summary>
        IEnumerable<NameInfo> TopLevel()
        {
            foreach (StructDecl s in program.Structs) yield return DescribeStruct(s);
            foreach (EnumDecl e in program.Enums) yield return DescribeEnum(e);
            foreach (ClassDecl c in program.Classes.Where(c => c.ForStruct == null)) yield return DescribeClass(c);
            foreach (RawBlockDecl b in program.RawBlocks) yield return DescribeBlock(b);
            foreach (LibraryUseDecl l in program.Libraries)
                yield return new NameInfo(NameKind.Group, l.Name, $"'{l.Path}'", l.File, NameAtEndOf(l.Span, l.Name));
            foreach (string group in Builtins.Functions.Select(f => f.Name).Concat(Builtins.Constants.Keys).Select(n => n.Split('.')[0]).Distinct())
                if (!program.Classes.Any(c => c.Name == group))
                    yield return new NameInfo(NameKind.Group, group, "PHI's own");
        }

        // ------------------------------------------------------------ completing a name

        /// <summary>The names that could go at this place: after a dot, what the thing before it has; else what's in sight.</summary>
        public IReadOnlyList<NameInfo> CompletionsAt(int offset)
        {
            if (InTextOrComment(offset)) return Array.Empty<NameInfo>();

            int last = -1;
            for (int i = 0; i < tokens.Count && tokens[i].Span.Start < offset && tokens[i].Kind != TokenKind.EndOfFile; i++) last = i;

            // the name being written so far isn't part of what's before it
            int beforeName = last >= 0 && tokens[last].Kind == TokenKind.Identifier && offset <= tokens[last].Span.End ? last - 1 : last;
            if (beforeName >= 0 && tokens[beforeName].Kind == TokenKind.Dot && tokens[beforeName].Span.End <= offset)
            {
                List<Segment> chain = BaseBeforeDot(beforeName);
                if (chain.Count == 0) return Array.Empty<NameInfo>();
                NameInfo? owner = chain.Count == 1 && GroupWords.ContainsKey(chain[0].Name)
                    ? new NameInfo(NameKind.Group, chain[0].Name)
                    : Follow(chain, offset);
                return owner == null ? Array.Empty<NameInfo>() : Distinct(MembersOf(owner, chain[^1].Indexes));
            }
            return Distinct(InSight(offset));
        }

        IEnumerable<NameInfo> InSight(int offset)
        {
            MethodDecl? method = EnclosingMethod(offset);
            ClassDecl? own = EnclosingClass(offset);

            if (method != null)
                foreach (VarDecl p in method.Parameters) yield return Describe(p, own!.File, NameKind.Parameter);
            if (method != null)
                foreach (Stmt s in method.Body.SelectMany(SyntaxWalker.Nodes).OfType<Stmt>())
                {
                    if (s is VarDecl v && IsWritten(v)) yield return Describe(v, own!.File);
                    else if (s is ConstDecl c) yield return Describe(c, own!.File);
                }
            if (own?.ForStruct != null)
                foreach (NameInfo field in FieldsOf(own.ForStruct)) yield return field;
            if (own != null)
                foreach (NameInfo member in MembersOfClass(own)) yield return member;

            foreach (NameInfo top in TopLevel()) yield return top;
            foreach (string type in Parser.TypeKeywords) yield return new NameInfo(NameKind.Type, type);
            foreach (string word in Parser.ReservedWords.Where(w => !Parser.TypeKeywords.Contains(w))) yield return new NameInfo(NameKind.Keyword, word);
        }

        /// <summary>The first of each name: what's nearest hides what's further away, as it does in the code.</summary>
        static IReadOnlyList<NameInfo> Distinct(IEnumerable<NameInfo> names)
        {
            var seen = new HashSet<string>();
            return names.Where(n => seen.Add(n.Name)).ToList();
        }

        bool InTextOrComment(int offset)
        {
            int previousEnd = 0;
            foreach (Token t in tokens)
            {
                if (t.Span.Start >= offset) break;
                if (t.Kind is TokenKind.String or TokenKind.RawBlock && offset < t.Span.End) return true;
                previousEnd = t.Span.End;
            }

            // (inside a word)
            if (previousEnd >= offset) return false;

            // the tokens skip comments, so one would be in the gap before this place
            bool inBlock = false;
            string[] lines = text[previousEnd..Math.Min(offset, text.Length)].Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line == "#") inBlock = !inBlock;
                else if (!inBlock && i == lines.Length - 1 && line.Contains('#')) return true;
            }
            return inBlock;
        }
    }
}

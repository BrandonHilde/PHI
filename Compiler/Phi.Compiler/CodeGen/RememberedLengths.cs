using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen
{
    /// <summary>
    /// Which of a program's methods' own strs that grow remember their length (see "a str that
    /// remembers its length" in X86Generator): what both generators use.
    /// </summary>
    public sealed class RememberedLengths
    {
        readonly BoundUnit unit;
        Dictionary<VariableSymbol, bool>? remembers;
        HashSet<VariableSymbol>? onlyRead;

        public RememberedLengths(BoundUnit unit) => this.unit = unit;


        static IEnumerable<Node> NodesOf(MethodSymbol m) =>
            m.Decl.Body.SelectMany(SyntaxWalker.Nodes).Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>());

        static (MethodSymbol? Method, List<Expr> Arguments) CallOf(Node n) => n switch
        {
            CallStmt { Resolved: MethodSymbol m } c => (m, c.Arguments),
            CallExpr { Resolved: MethodSymbol m } c => (m, c.Arguments),
            _ => (null, new List<Expr>()),
        };

        /// <summary>
        /// The ptr&lt;u8&gt; parameters their methods only read through: every use is an element
        /// read (text:i), a value of a log or of a str, or giving it to another such parameter. A method with an assembly
        /// block, or one in a compiled library (no code to look at), has none.
        /// </summary>
        public HashSet<VariableSymbol> OnlyReadPointers()
        {
            if (onlyRead != null) return onlyRead;
            var bodies = unit.Methods.Where(m => !m.Decl.IsCompiled).ToDictionary(m => m, m => NodesOf(m).ToList());
            onlyRead = bodies.Where(b => !b.Value.Any(n => n is CallStmt { Resolved: RawBlockDecl } or CallExpr { Resolved: RawBlockDecl }))
                             .SelectMany(b => b.Key.Parameters.Where(p => p.Type.IsTextPointer && !p.Type.IsArray)).ToHashSet();
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var (m, nodes) in bodies)
                    foreach (VariableSymbol p in m.Parameters.Where(onlyRead.Contains).ToList())
                    {
                        // an element that's written, or whose address is taken, isn't a read
                        var written = new HashSet<Node>();
                        foreach (Node n in nodes)
                        {
                            Expr? target = n switch
                            {
                                AssignStmt a => a.Target,
                                AtomicStmt a => a.Target,
                                AppendStmt a => a.Target,
                                StringBuildStmt b => b.Target,
                                ArrayAssignStmt c => c.Target,
                                ObjectAssignStmt o => o.Target,
                                SetLengthStmt l => l.Array,
                                AskStmt a => a.Target,
                                CallStmt c => c.ResultTarget,
                                AddrExpr a => a.Operand,
                                _ => null,
                            };
                            if (target != null) written.Add(target);
                        }
                        int uses = nodes.Count(n => n is NameExpr name && name.Symbol == p), reads = 0;
                        foreach (Node n in nodes)
                        {
                            if (n is IndexExpr { Target: NameExpr element } index && element.Symbol == p && !written.Contains(index)) reads++;
                            // written out as text: a value of a log, or of a str's
                            bool Is(Expr e) => e is NameExpr name && name.Symbol == p;
                            reads += n switch
                            {
                                LogStmt log => log.Values.Count(Is),
                                StringBuildStmt build => build.Parts.Count(Is),
                                TextExpr text => text.Parts.Count(Is),
                                _ => 0,
                            };
                            (MethodSymbol? callee, List<Expr> arguments) = CallOf(n);
                            if (callee == null) continue;
                            for (int i = 0; i < Math.Min(arguments.Count, callee.Parameters.Count); i++)
                                if (arguments[i] is NameExpr given && given.Symbol == p && onlyRead.Contains(callee.Parameters[i])) reads++;
                        }
                        if (reads != uses)
                        {
                            onlyRead.Remove(p);
                            changed = true;
                        }
                    }
            }
            return onlyRead;
        }

        /// <summary>True for a method's own str that grows and that only the method's statements change.</summary>
        public bool Remembers(VariableSymbol v)
        {
            if (!v.Grows || !v.IsLocal || v.IsParameter || v.TextRef || !v.Type.IsString || v.Type.IsArray) return false;
            if (remembers == null)
            {
                remembers = new();
                HashSet<VariableSymbol> pointers = OnlyReadPointers();
                foreach (MethodSymbol m in unit.Methods.Where(m => !m.Decl.IsCompiled))
                {
                    List<Node> nodes = NodesOf(m).ToList();
                    foreach (VariableSymbol local in m.Locals.Where(l => l.Grows && !l.IsParameter))
                    {
                        int uses = nodes.Count(n => n is NameExpr name && name.Symbol == local), known = 0;
                        bool Is(Expr? e) => e is NameExpr name && name.Symbol == local;
                        foreach (Node n in nodes)
                        {
                            switch (n)
                            {
                                case StringBuildStmt build:
                                    known += (Is(build.Target) ? 1 : 0) + build.Parts.Count(Is);
                                    break;
                                case TextExpr text:
                                    known += text.Parts.Count(Is);
                                    break;
                                case LogStmt log:
                                    known += log.Values.Count(Is);
                                    break;
                                // s is value; and other is s; between strs
                                case AssignStmt { Op: AssignOp.Set, Target.Type: { IsString: true, IsArray: false } } assign:
                                    known += (Is(assign.Target) ? 1 : 0) + (Is(assign.Value) ? 1 : 0);
                                    break;
                            }
                            (MethodSymbol? callee, List<Expr> arguments) = CallOf(n);
                            if (callee == null) continue;
                            for (int i = 0; i < Math.Min(arguments.Count, callee.Parameters.Count); i++)
                                if (Is(arguments[i]) && pointers.Contains(callee.Parameters[i])) known++;
                        }
                        if (Is(m.Decl.Result)) known++;
                        remembers[local] = known == uses;
                    }
                }
            }
            return remembers.GetValueOrDefault(v);
        }
    }
}

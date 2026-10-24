namespace Phi.Compiler.Syntax
{
    /// <summary>Every statement and expression in a program, for questions like "does it use new?".</summary>
    public static class SyntaxWalker
    {
        public static IEnumerable<Node> Nodes(ProgramNode program) =>
            program.Classes.SelectMany(c => c.Body.SelectMany(Nodes)
                .Concat(c.Methods.SelectMany(m => m.Parameters.Cast<Stmt>().Concat(m.Body).SelectMany(Nodes)
                    .Concat(m.Result != null ? Nodes(m.Result) : Enumerable.Empty<Node>()))));

        public static IEnumerable<Node> Nodes(Stmt stmt)
        {
            yield return stmt;

            IEnumerable<Node> children = stmt switch
            {
                VarDecl v => v.Values.Concat(v.BufferSize != null ? new[] { v.BufferSize } : Array.Empty<Expr>())
                    .Concat(v.AtAddress != null ? new[] { v.AtAddress } : Array.Empty<Expr>()).SelectMany(Nodes),
                TakesStmt t => Nodes(t.Target).Concat(Nodes(t.Source)),
                ObjectAssignStmt o => Nodes(o.Target).Concat(Nodes(o.Value)),
                ConstDecl c => Nodes(c.Value),
                LogStmt l => l.Values.SelectMany(Nodes),
                AskStmt a => Nodes(a.Target),
                ExitStmt e => e.Code != null ? Nodes(e.Code) : Enumerable.Empty<Node>(),
                OutStmt o => Nodes(o.Port).Concat(Nodes(o.Value)),
                FreeStmt f => Nodes(f.Pointer),
                UnsafeStmt u => u.Body.SelectMany(Nodes),
                CallStmt c => c.Arguments.SelectMany(Nodes)
                    .Concat(c.ResultTarget != null ? Nodes(c.ResultTarget) : Enumerable.Empty<Node>())
                    .Concat(c.CalleeTarget != null ? Nodes(c.CalleeTarget) : Enumerable.Empty<Node>()),
                IfStmt i => Nodes(i.Condition).Concat(i.Then.SelectMany(Nodes)).Concat((i.Else ?? new()).SelectMany(Nodes)),
                WhileStmt w => (w.Init != null ? Nodes(w.Init) : Enumerable.Empty<Node>())
                    .Concat(Nodes(w.Condition))
                    .Concat(w.Step != null ? Nodes(w.Step) : Enumerable.Empty<Node>())
                    .Concat(w.Body.SelectMany(Nodes)),
                AssignStmt a => Nodes(a.Target).Concat(Nodes(a.Value)).Concat((a.Values ?? new()).SelectMany(Nodes)),
                StringBuildStmt b => Nodes(b.Target).Concat(b.Parts.SelectMany(Nodes)),
                AppendStmt a => Nodes(a.Target).Concat(a.Values.SelectMany(Nodes)),
                WhenStmt w => Nodes(w.Subject)
                    .Concat(w.Branches.SelectMany(b => b.Values.SelectMany(Nodes).Concat(b.Body.SelectMany(Nodes))))
                    .Concat((w.Else ?? new()).SelectMany(Nodes)),
                SetLengthStmt l => Nodes(l.Array).Concat(Nodes(l.Value)),
                ReturnStmt r => r.Value != null ? Nodes(r.Value) : Enumerable.Empty<Node>(),
                _ => Enumerable.Empty<Node>(),
            };

            foreach (Node n in children) yield return n;
        }

        public static IEnumerable<Node> Nodes(Expr expr)
        {
            yield return expr;

            IEnumerable<Expr> children = expr switch
            {
                IndexExpr i => new[] { i.Target, i.Index },
                MemberExpr m => new[] { m.Target },
                AddrExpr a => new[] { a.Operand },
                CastExpr c => new[] { c.Operand },
                SizeofExpr s => s.Operand != null ? new[] { s.Operand } : Array.Empty<Expr>(),
                InExpr i => new[] { i.Port },
                NewExpr n => n.Count != null ? new[] { n.Count } : Array.Empty<Expr>(),
                UnaryExpr u => new[] { u.Operand },
                BinaryExpr b => new[] { b.Left, b.Right },
                CallExpr c => c.Arguments.Concat(c.CalleeTarget != null ? new[] { c.CalleeTarget } : Array.Empty<Expr>()).ToArray(),
                _ => Array.Empty<Expr>(),
            };

            foreach (Expr child in children)
                foreach (Node n in Nodes(child)) yield return n;
        }
    }
}

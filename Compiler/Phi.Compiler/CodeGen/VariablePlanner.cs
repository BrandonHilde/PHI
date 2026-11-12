using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen
{
    /// <summary>
    /// Picks the variables of a method worth keeping in registers. Nothing here depends on the
    /// CPU: the generator says how many registers it has, which variables can go in one, and
    /// which statements must run with the variables in memory (a "fence", which costs a store
    /// and a load for each register around it).
    ///
    /// A use counts once outside loops and 8 times more for each loop around it, since that's
    /// roughly how often it runs; what's inside an if counts half, and each branch of a when
    /// its share of the when (a branch of sixteen runs a sixteenth of the time, near enough).
    /// A variable gets a register when its uses are worth more than what the register costs:
    /// saving and restoring it (2), setting it up (1), and the fences it has to cross, each
    /// counted the same way.
    /// </summary>
    public static class VariablePlanner
    {
        const int LoopWeight = 8;
        const int DeepestLoop = 5;
        const long One = 1 << 16;       // what one use outside every loop and branch counts: shares of it are whole numbers
        const int MostBranches = 64;

        public static List<VariableSymbol> Choose(MethodSymbol m, int registers,
            Func<VariableSymbol, bool> fits, Func<Stmt, bool> runsInRegisters)
        {
            var candidates = new HashSet<VariableSymbol>(m.Parameters.Concat(m.Locals).Where(v => fits(v) && !v.Decl.Volatile));

            // a variable whose address is taken has to stay in memory, and so does a while each's
            // count and element, which its loop reaches by address
            foreach (Node n in m.Decl.Parameters.Cast<Stmt>().Concat(m.Decl.Body).SelectMany(SyntaxWalker.Nodes)
                         .Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>()))
            {
                if (n is AddrExpr { Operand: NameExpr { Symbol: VariableSymbol taken } }) candidates.Remove(taken);
                // an atomic step is on memory (other cores see it there)
                if (n is AtomicStmt { Target: NameExpr { Symbol: VariableSymbol shared } }) candidates.Remove(shared);
                if (n is AtomicExpr { Target: NameExpr { Symbol: VariableSymbol swapped } }) candidates.Remove(swapped);
                if (n is WhileEachStmt each)
                {
                    if (each.Index.Symbol != null) candidates.Remove(each.Index.Symbol);
                    if (each.Element.Symbol != null) candidates.Remove(each.Element.Symbol);
                }
            }
            return Choose(candidates, m.Decl.Body, m.Decl.Result, registers, runsInRegisters);
        }

        /// <summary>
        /// The same choice for any run of statements (a loop in a class's own code, say), from
        /// variables the caller knows can be in registers while it runs.
        /// </summary>
        public static List<VariableSymbol> Choose(HashSet<VariableSymbol> candidates, List<Stmt> body, Expr? result,
            int registers, Func<Stmt, bool> runsInRegisters)
        {
            if (candidates.Count == 0) return new List<VariableSymbol>();

            var uses = new Dictionary<VariableSymbol, long>();
            long fences = 0;

            void Count(Node node, long weight)
            {
                foreach (Node n in node is Stmt s ? SyntaxWalker.Nodes(s) : SyntaxWalker.Nodes((Expr)node))
                {
                    if (n is NameExpr { Symbol: VariableSymbol v } && candidates.Contains(v))
                        uses[v] = uses.GetValueOrDefault(v) + weight;
                    // a field reached through a pointer (c.mark), or through the struct a method was
                    // called on, reads the pointer too
                    VariableSymbol? through = n switch
                    {
                        NameExpr { Symbol: PathSymbol path } => path.Root,
                        NameExpr { Symbol: SelfFieldSymbol field } => field.Self,
                        _ => null,
                    };
                    if (through != null && candidates.Contains(through))
                        uses[through] = uses.GetValueOrDefault(through) + weight;
                    // a declaration with a value sets it, which is a use too
                    if (n is VarDecl { Symbol: VariableSymbol declared } d && d.Values.Count > 0 && candidates.Contains(declared))
                        uses[declared] = uses.GetValueOrDefault(declared) + weight;
                }
            }

            void Walk(List<Stmt> body, int depth, long weight)
            {
                if (weight < 1) weight = 1;
                long inLoop = depth < DeepestLoop ? weight * LoopWeight : weight;

                foreach (Stmt s in body)
                {
                    if (!runsInRegisters(s))
                    {
                        fences += weight;
                        continue;
                    }
                    switch (s)
                    {
                        case UnsafeStmt u:
                            Walk(u.Body, depth, weight);
                            break;
                        case LockedStmt l:
                            Count(l.Lock, weight);
                            Walk(l.Body, depth, weight);
                            break;
                        case IfStmt i:
                            Count(i.Condition, weight);
                            Walk(i.Then, depth, weight / 2);
                            if (i.Else != null) Walk(i.Else, depth, weight / 2);
                            break;
                        case WhileStmt w:
                            if (w.Init != null) Count(w.Init, weight);
                            Count(w.Condition, inLoop);
                            if (w.Step != null) Count(w.Step, inLoop);
                            Walk(w.Body, depth + 1, inLoop);
                            break;
                        case WhenStmt w:
                        {
                            Count(w.Subject, weight);
                            long share = weight / Math.Clamp(w.Branches.Count + (w.Else != null ? 1 : 0), 1, MostBranches);
                            foreach (WhenBranch b in w.Branches)
                            {
                                foreach (Expr value in b.Values) Count(value, share);
                                Walk(b.Body, depth, share);
                            }
                            if (w.Else != null) Walk(w.Else, depth, share);
                            break;
                        }
                        default:
                            Count(s, weight);
                            break;
                    }
                }
            }

            Walk(body, 0, One);
            if (result != null) Count(result, One);

            const long setUp = 3 * One;   // push and pop, and loading or zeroing it
            return uses
                .Select(u => (Variable: u.Key, Worth: u.Value - setUp - 2 * fences))
                .Where(u => u.Worth > 0)
                .OrderByDescending(u => u.Worth)
                .ThenBy(u => u.Variable.Name, StringComparer.Ordinal)   // the same choice every build
                .Take(registers)
                .Select(u => u.Variable)
                .ToList();
        }
    }
}

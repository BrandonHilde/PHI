using System.Runtime.CompilerServices;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen
{
    /// <summary>
    /// PHI works an operator's left side out before its right, and a call's arguments first to
    /// last (a store works its value out first, then where it goes). Code that works the right
    /// side out first, because that's quicker or needs fewer registers, is only used where no
    /// one can tell: <see cref="LeftFirst"/> says when someone could.
    /// </summary>
    public static class EvaluationOrder
    {
        /// <summary>
        /// True when working out the right side first could be seen: one side calls something
        /// (which may print, change what the other side reads, or stop the program) and the other
        /// isn't one that nothing can change, or stop.
        /// </summary>
        public static bool LeftFirst(Expr left, Expr right, MethodSymbol? method) =>
            HasEffect(right) && !Steady(left, method) || HasEffect(left) && !Steady(right, method);

        /// <summary>True when working the expression out may do something: call a method, make an object, swap in one step, or read a port.</summary>
        public static bool HasEffect(Expr e) => SyntaxWalker.Nodes(e).Any(n =>
            n is CallExpr or NewExpr or AtomicExpr or InExpr or NameExpr { Call: not null } or MemberExpr { Call: not null });

        /// <summary>
        /// True when nothing done in between can change the value, or make working it out stop
        /// the program: a constant, or a method's own number, character, bln, enum, pointer or
        /// float that it never takes the address of, and arithmetic on those (but not division,
        /// which stops the program on a zero).
        /// </summary>
        public static bool Steady(Expr e, MethodSymbol? method)
        {
            if (e is BinaryExpr or UnaryExpr or CastExpr && ConstantFolder.Evaluate(e) != null) return true;
            return e switch
            {
                NumberExpr or BoolExpr or DecimalExpr or StringExpr or EmptyExpr or SizeofExpr => true,
                NameExpr { Call: null, Symbol: ConstantSymbol or FloatConstantSymbol } => true,
                NameExpr { Call: null, Symbol: VariableSymbol { IsLocal: true } v } =>
                    method != null && Plain(v.Type) && !Addressed(method).Contains(v),
                UnaryExpr u => Steady(u.Operand, method),
                CastExpr c => Steady(c.Operand, method),
                BinaryExpr { ObjectCompare: null, ArrayCompare: false, Op: not (BinaryOp.Divide or BinaryOp.Modulo) } b =>
                    Plain(b.Left.Type) && Plain(b.Right.Type) && Steady(b.Left, method) && Steady(b.Right, method),
                _ => false,
            };
        }

        /// <summary>A value held whole in the variable: a number, chr, bln, enum, pointer, float or method.</summary>
        static bool Plain(PhiType t) =>
            t.IsNumeric || t.IsWide || t.IsFloat || t.IsMethod;

        static readonly ConditionalWeakTable<MethodSymbol, HashSet<VariableSymbol>> addressed = new();

        /// <summary>The method's variables whose address it takes (so a call could change them).</summary>
        static HashSet<VariableSymbol> Addressed(MethodSymbol method) => addressed.GetValue(method, m =>
        {
            var found = new HashSet<VariableSymbol>();
            IEnumerable<Node> nodes = m.Decl.Parameters.Cast<Stmt>().Concat(m.Decl.Body).SelectMany(SyntaxWalker.Nodes);
            if (m.Decl.Result != null) nodes = nodes.Concat(SyntaxWalker.Nodes(m.Decl.Result));
            foreach (Node n in nodes)
                if (n is AddrExpr a) Names(a.Operand, found);
                else if (n is PointerViewExpr view) Names(view.Pointer, found);
            return found;
        });

        static void Names(Expr e, HashSet<VariableSymbol> found)
        {
            foreach (Node n in SyntaxWalker.Nodes(e))
                if (n is NameExpr { Symbol: VariableSymbol v }) found.Add(v);
        }
    }
}

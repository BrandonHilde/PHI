using System.Text.RegularExpressions;
using Phi.Compiler.CodeGen.Ir;
using Phi.Compiler.Semantics;

namespace Phi.Compiler.CodeGen.A64
{
    // A small method put in where it's called: its intermediate form built again for the call,
    // its blocks set between the call's block's two halves, its parameters the call's arguments
    // and its return a jump on. A call costs a frame, the registers kept and the arguments
    // through memory; Map's Get was four of them (Get, Hash.Of, FindHashed, and what called it).
    //
    // Only a method that keeps nothing in its frame: no objects, strs or arrays of its own,
    // nothing whose address is taken. Its lines are marked as its own file's (LineMark.File),
    // and the emitter names that file while it writes them, so an error or a fault in it names
    // the line and file it's on, as when it's called.
    //
    // Never a compiled library's method in the program that uses it (the program has the text
    // of some, for the ones that take types): its code is the library's, and so are the class
    // variables it works on.
    public sealed partial class A64Generator
    {
        /// <summary>The most instructions a method put in where it's called may have, and how deep (a call in one put in, put in too).</summary>
        const int InlineMost = 48, InlineDepth = 3;

        Dictionary<string, MethodSymbol>? methodsByLabel;
        readonly HashSet<string> neverInline = new();

        static readonly Regex ArgumentPlace = new(@"^rbp \+ (\d+)$");

        /// <summary>Each call in fn of a method small and simple enough, replaced by the method's own code.</summary>
        void Inline(IrFunction fn, MethodSymbol into)
        {
            methodsByLabel ??= unit.Methods.Where(m => m.Decl != null && !m.IsOutside && !m.IsHook && !m.IsInterruptHandler)
                .GroupBy(m => m.Label).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            var inside = new List<string> { into.Label };
            foreach (Block b in fn.Blocks.ToList()) InlineIn(fn, b, inside);
        }

        void InlineIn(IrFunction fn, Block b, List<string> inside)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                if (b.Instrs[k] is not CallI call || Inlined(call, inside) is not IrFunction body) continue;
                // the block's two halves, the method's blocks between them; the call's line again after them
                var rest = new Block { Id = fn.Blocks.Count };
                if (LineBefore(fn, b, k) is LineMark line) rest.Instrs.Add(new LineMark { Node = line.Node, File = line.File });
                rest.Instrs.AddRange(b.Instrs.Skip(k + 1));
                rest.End = b.End;
                b.Instrs.RemoveRange(k, b.Instrs.Count - k);
                b.End = new JumpT { Target = body.Blocks[0] };
                foreach (Block cb in body.Blocks)
                    if (cb.End is RetT ret)
                    {
                        if (call.Dst != null) cb.Instrs.Add(Given(call, ret.Value!.Value));
                        cb.End = new JumpT { Target = rest };
                    }
                int at = fn.Blocks.IndexOf(b);
                fn.Blocks.InsertRange(at + 1, body.Blocks);
                fn.Blocks.Insert(at + 1 + body.Blocks.Count, rest);
                foreach (StructSymbol tested in body.TypeTables) fn.TypeTables.Add(tested);
                inside.Add(call.Label);
                foreach (Block cb in body.Blocks.ToList()) InlineIn(fn, cb, inside);
                inside.RemoveAt(inside.Count - 1);
                InlineIn(fn, rest, inside);
                return;
            }
        }

        /// <summary>The line the code at b's instruction k is for: the mark before it, in the order the blocks are written.</summary>
        static LineMark? LineBefore(IrFunction fn, Block b, int k)
        {
            for (int at = fn.Blocks.IndexOf(b); at >= 0; at--)
            {
                Block from = fn.Blocks[at];
                for (int j = (from == b ? k : from.Instrs.Count) - 1; j >= 0; j--)
                    if (from.Instrs[j] is LineMark mark) return mark;
            }
            return null;
        }

        /// <summary>What the method gives back, into the call's result (as the call's own code takes it from x0).</summary>
        static Instr Given(CallI call, Opnd value)
        {
            VReg dst = call.Dst!;
            if (value.IsImm) return new ConstI { Dst = dst, Value = value.Imm };
            if (!dst.Float && dst.Wide && (call.Result == CallResult.Eax || !value.Reg!.Wide)) return new ExtendI { Dst = dst, Src = value.Reg!, Signed = false };
            return new CopyI { Dst = dst, Src = value };
        }

        /// <summary>The called method's form, made for this call (its parameters the arguments), or null when it isn't to be put in.</summary>
        IrFunction? Inlined(CallI call, List<string> inside)
        {
            if (call.Outside || call.Routine || call.Target != null || inside.Count > InlineDepth || inside.Contains(call.Label)) return null;
            if (neverInline.Contains(call.Label) || !methodsByLabel!.TryGetValue(call.Label, out MethodSymbol? m)) return null;
            if (call.Result is CallResult.Xmm0 or CallResult.EdxEax) return null;
            if (call.ArgKinds.Any(a => a is MemKind.F32 or MemKind.F16 or MemKind.V128)) return null;
            IrFunction? body = IrBuilder.Build(m, Holdable, Catches, GrowResults, true, (lengths ??= new RememberedLengths(unit)).Remembers, out _, arm: true);
            // (what's wrong with the method itself is wrong at every call of it)
            bool itself = body != null && body.Homes == 0 && body.Scratch == null && body.UnwindRecord == 0 && body.Owned.Count == 0
                && body.OwnedTexts.Count == 0 && body.OwnedViews.Count == 0 && body.OwnedTableTexts.Count == 0 && !body.Values.Any(v => v.Pinned)
                && body.Blocks.Sum(b => b.Instrs.Count(i => i is not LineMark)) <= InlineMost
                && body.Blocks.All(b => b.End is JumpT or BranchT or SwitchT or RetT { Vec: false } && b.Instrs.All(Simple));
            if (!itself)
            {
                neverInline.Add(call.Label);
                return null;
            }
            if (body!.Blocks.Any(b => b.End is RetT { Value: null }) && call.Dst != null) return null;
            if (body.Blocks.Any(b => b.End is RetT { Value: { IsImm: true } }) && call.Dst is { Float: true }) return null;
            // each read of a parameter (where the call would have put it: the last argument at
            // rbp + 16, the one before 8 above) is the argument itself
            int n = call.Args.Count;
            foreach (Block b in body.Blocks)
            {
                // (its lines are its own file's: an error in it names the line it's on, as when it's called)
                for (int k = 0; k < b.Instrs.Count; k++)
                    if (b.Instrs[k] is LineMark { File: null } mark) b.Instrs[k] = new LineMark { Node = mark.Node, File = m.Decl!.File };
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    if (b.Instrs[k] is not LoadI { Place: var place } load || !place.TrimStart().StartsWith("rbp", StringComparison.Ordinal)) continue;
                    if (ArgumentPlace.Match(place.Trim()) is not { Success: true } found) return null;
                    long off = long.Parse(found.Groups[1].Value) - 16;
                    if (off < 0 || off % 8 != 0 || off / 8 >= n) return null;
                    if (Argument(load, call.Args[(int)(n - 1 - off / 8)]) is not Instr read) return null;
                    b.Instrs[k] = read;
                }
            }
            if (Environment.GetEnvironmentVariable("PHI_INLINE_TRACE") == "1") Console.Error.WriteLine($"inline: {m.Name} in {inside[^1]}\n{body.Dump()}");
            return body;
        }

        /// <summary>A parameter's read, from the argument: as reading its 8 bytes of the frame as Kind would give it.</summary>
        static Instr? Argument(LoadI load, Opnd a)
        {
            VReg dst = load.Dst;
            if (load.Kind == MemKind.F64) return !a.IsImm && a.Reg!.Float && dst.Float ? new CopyI { Dst = dst, Src = a } : null;
            if (dst.Float || dst.Vec || !a.IsImm && (a.Reg!.Float || a.Reg.Vec)) return null;
            if (a.IsImm)
            {
                long? value = load.Kind switch
                {
                    MemKind.U8 => a.Imm & 0xFF, MemKind.S8 => (sbyte)a.Imm, MemKind.U16 => a.Imm & 0xFFFF, MemKind.S16 => (short)a.Imm,
                    MemKind.D32 => dst.Wide ? a.Imm & 0xFFFFFFFF : (int)a.Imm, MemKind.Q64 => a.Imm, _ => null,
                };
                return value is long n ? new ConstI { Dst = dst, Value = n } : null;
            }
            switch (load.Kind)
            {
                case MemKind.U8 or MemKind.S8 or MemKind.U16 or MemKind.S16:
                    return dst.Wide ? null : new NarrowI { Dst = dst, Src = a.Reg!, Kind = load.Kind };
                case MemKind.D32:
                    return dst.Wide ? new ExtendI { Dst = dst, Src = a.Reg!, Signed = false } : new CopyI { Dst = dst, Src = a };
                case MemKind.Q64:
                    return a.Reg!.Wide || !dst.Wide ? new CopyI { Dst = dst, Src = a } : new ExtendI { Dst = dst, Src = a.Reg, Signed = false };
                default:
                    return null;
            }
        }

        /// <summary>An instruction a method put in where it's called may have: nothing in its frame but its parameters, read.</summary>
        static bool Simple(Instr i) => i switch
        {
            LineMark or ConstI or CopyI or UnI or ExtendI or NarrowI or SetCcI or LoadIdxI or StoreIdxI or LeaI or FConstI or FBinI or FNegI or ToFloatI => true,
            BinI or CheckI or SignCheckI or StopI or ThrowI => true,
            LoadI => true,   // (a parameter's, or a class's variable's: Inlined looks at which)
            StoreI s => !s.Place.TrimStart().StartsWith("rbp", StringComparison.Ordinal),
            AddrI a => !a.Label.TrimStart().StartsWith("rbp", StringComparison.Ordinal),
            CallI c => !c.Outside && !c.Routine,
            _ => false,
        };
    }
}

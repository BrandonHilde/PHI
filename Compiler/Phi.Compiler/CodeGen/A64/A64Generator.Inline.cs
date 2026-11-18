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

        /// <summary>
        /// A short if and else made a choice with no jump (csel): if c then x is a; else x is b,
        /// each side up to three sums that can't fail, both worked out and one kept. A branch the
        /// CPU can't guess (is n odd?) costs it a dozen instructions' time each wrong guess; a few
        /// more instructions always cost less. One variable chosen; nothing read from memory.
        /// </summary>
        static void Selects(IrFunction fn)
        {
            Liveness live = Liveness.Of(fn);
            var into = new Dictionary<Block, int>();
            foreach (Block b in fn.Blocks)
                foreach (Block to in IrBuilder.Successors(b)) into[to] = into.GetValueOrDefault(to) + 1;
            static bool Sum(Instr i) => i switch
            {
                BinI b => b.Op is not (BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU) && !b.Dst.Float && !b.Dst.Vec,
                CopyI c => !c.Dst.Float && !c.Dst.Vec,
                ConstI c => !c.Dst.Float && !c.Dst.Vec,
                UnI or ExtendI or NarrowI or LeaI or LineMark => true,
                _ => false,
            };
            // an arm's instructions on values of its own: what each variable it sets ends as
            (List<Instr> Code, Dictionary<VReg, VReg> Ends)? Arm(Block? arm)
            {
                var code = new List<Instr>();
                var ends = new Dictionary<VReg, VReg>();
                if (arm == null) return (code, ends);
                if (!arm.Instrs.All(Sum) || arm.Instrs.Count(i => i is not LineMark) > 3) return null;
                VReg Now(VReg v) => ends.GetValueOrDefault(v, v);
                Opnd NowOf(Opnd o) => o.IsImm ? o : Now(o.Reg!);
                foreach (Instr i in arm.Instrs)
                {
                    if (i is LineMark) continue;
                    VReg dst = RegisterAllocator.Def(i)!;
                    VReg mine = fn.NewValue(dst.Wide, dst.Name);
                    code.Add(i switch
                    {
                        BinI b => new BinI { Op = b.Op, Dst = mine, A = NowOf(b.A), B = NowOf(b.B) },
                        CopyI c => new CopyI { Dst = mine, Src = NowOf(c.Src) },
                        ConstI c => new ConstI { Dst = mine, Value = c.Value },
                        UnI u => new UnI { Op = u.Op, Dst = mine, A = Now(u.A) },
                        ExtendI e => new ExtendI { Dst = mine, Src = Now(e.Src), Signed = e.Signed },
                        NarrowI n => new NarrowI { Dst = mine, Src = Now(n.Src), Kind = n.Kind },
                        LeaI l => new LeaI { Dst = mine, A = Now(l.A), Factor = l.Factor, Add = l.Add },
                        _ => throw new InvalidOperationException("a sum that isn't one"),
                    });
                    ends[dst] = mine;
                }
                return (code, ends);
            }
            foreach (Block top in fn.Blocks.ToList())
            {
                if (top.End is not BranchT { Float: false } br || br.Then == br.Else) continue;
                // if and else that meet again, or an if alone (the other way goes straight on)
                Block? then = null, otherwise = null;
                Block join;
                bool Alone(Block b) => into.GetValueOrDefault(b) == 1 && b != top && b != fn.Blocks[0];
                if (br.Then.End is JumpT tj && br.Else.End is JumpT ej && tj.Target == ej.Target && Alone(br.Then) && Alone(br.Else))
                    (then, otherwise, join) = (br.Then, br.Else, tj.Target);
                // (not an if alone: it's most often one the CPU guesses, a new best found now and
                // then, and the choice made every time was slower: collatz's, by a tenth)
                else continue;
                if (join == then || join == otherwise || join == top) continue;
                if (Arm(then) is not var (thenCode, thenEnds) || Arm(otherwise) is not var (elseCode, elseEnds)) continue;
                var chosen = thenEnds.Keys.Union(elseEnds.Keys).Where(v => live.In[join].Contains(v)).OrderBy(v => v.Id).ToList();
                if (chosen.Count != 1 || chosen.Any(v => v.Float || v.Vec || v.Pinned)) continue;
                VReg kept = chosen[0];
                // (x + 1 as a side's last step is the choice's own: csinc)
                (Opnd Value, int Plus) End(List<Instr> code, Dictionary<VReg, VReg> ends)
                {
                    if (!ends.TryGetValue(kept, out VReg? end)) return (kept, 0);
                    // (a copy as its last step: what's copied is the side's value)
                    while (code.Count > 0 && code[^1] is CopyI { Src: { IsImm: false } copied } copy && copy.Dst == end)
                    {
                        code.RemoveAt(code.Count - 1);
                        end = copied.Reg!;
                    }
                    if (code.Count > 0 && code[^1] is BinI { Op: BinOp.Add, A: { IsImm: false } from, B: { IsImm: true, Imm: 1 } } last && last.Dst == end)
                    {
                        code.RemoveAt(code.Count - 1);
                        return (from, 1);
                    }
                    // (x * 3 + 1 is one instruction of the form's, two of ARM's: its + 1 too)
                    if (code.Count > 0 && code[^1] is LeaI { Add: 1 } times && times.Dst == end)
                    {
                        code[^1] = new LeaI { Dst = end, A = times.A, Factor = times.Factor, Add = 0 };
                        return (end, 1);
                    }
                    return (end, 0);
                }
                var (no, plusNo) = End(elseCode, elseEnds);
                var (yes, plusYes) = plusNo == 0 ? End(thenCode, thenEnds) : (thenEnds.GetValueOrDefault(kept, kept), 0);
                top.Instrs.AddRange(thenCode);
                top.Instrs.AddRange(elseCode);
                top.Instrs.Add(new SelectI
                {
                    Dst = kept, Cond = br.Cond, A = br.A, B = br.B, TestMask = br.TestMask,
                    IfTrue = yes, IfFalse = no, PlusTrue = plusYes, PlusFalse = plusNo,
                });
                top.End = new JumpT { Target = join };
                if (then != null) fn.Blocks.Remove(then);
                if (otherwise != null) fn.Blocks.Remove(otherwise);
                // (join had two ways in, or top's and an arm's: one now)
                into[join] = into.GetValueOrDefault(join) - 1;
            }
        }

        /// <summary>
        /// A method that starts with a test and returns at once one way (if n &lt; 2 return n): its
        /// first block, the block that returns, and the rest's first block. Only where nothing
        /// comes back to the first block, and each of the other two has the one way in.
        /// </summary>
        internal static (Block Entry, Block Return, Block Later)? EarlyReturn(IrFunction fn)
        {
            if (fn.Blocks.Count < 3 || fn.Blocks[0].End is not BranchT br || br.Then == br.Else) return null;
            if (fn.UnwindRecord != 0 || fn.Blocks.Any(b => b.End is TryT)) return null;
            var into = new Dictionary<Block, int>();
            foreach (Block b in fn.Blocks)
                foreach (Block to in IrBuilder.Successors(b)) into[to] = into.GetValueOrDefault(to) + 1;
            Block entry = fn.Blocks[0];
            if (into.GetValueOrDefault(entry) != 0) return null;
            foreach (var (back, rest) in new[] { (br.Then, br.Else), (br.Else, br.Then) })
                if (back.End is RetT && back != entry && rest != entry && into[back] == 1 && into[rest] == 1
                    && !entry.Instrs.Concat(back.Instrs).Any(i => i is RawCallI))
                    return (entry, back, rest);
            return null;
        }

        /// <summary>
        /// For such a method: the first block and the one that returns work on values of their
        /// own, copied to the method's as the rest begins. A value that lives across the rest's
        /// calls is in a register every method keeps, which has to be saved first; the copies
        /// don't live that long, so the way that returns at once touches none, and saves none
        /// (the emitter saves them where the rest begins). fib returns at once half the time.
        /// </summary>
        static void SplitEntry(IrFunction fn)
        {
            if (EarlyReturn(fn) is not var (entry, back, rest)) return;
            IEnumerable<VReg> Named(Block b) => b.Instrs.SelectMany(i => RegisterAllocator.Uses(i).Concat(RegisterAllocator.Def(i) is VReg d ? new[] { d } : Array.Empty<VReg>()))
                .Concat(RegisterAllocator.Uses(b.End));
            var before = Named(entry).Concat(Named(back)).ToHashSet();
            if (before.Any(v => v.Vec || v.Pinned)) return;
            var own = new Dictionary<VReg, VReg>();
            VReg Own(VReg v)
            {
                if (!own.TryGetValue(v, out VReg? mine)) own[v] = mine = v.Float ? fn.NewFloat(v.Name) : fn.NewValue(v.Wide, v.Name);
                return mine;
            }
            void Each(Func<VReg, VReg> to)
            {
                foreach (Block b in new[] { entry, back })
                {
                    foreach (Instr i in b.Instrs) Rename(i, to);
                    Rename(b.End!, to);
                }
            }
            Each(Own);
            if (Named(entry).Concat(Named(back)).Any(before.Contains))
            {
                // (an instruction that holds a value some way Rename doesn't know: as it was)
                var was = own.ToDictionary(o => o.Value, o => o.Key);
                Each(v => was.GetValueOrDefault(v, v));
                return;
            }
            Liveness live = Liveness.Of(fn);
            int at = 0;
            foreach (var (v, mine) in own.OrderBy(o => o.Key.Id))
                if (live.In[rest].Contains(v)) rest.Instrs.Insert(at++, new CopyI { Dst = v, Src = mine });
        }

        /// <summary>Each value an instruction (or a block's end) names, replaced: in its own properties, its lists of operands, and what it holds to write back.</summary>
        static void Rename(object node, Func<VReg, VReg> to)
        {
            foreach (System.Reflection.PropertyInfo p in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                switch (p.GetValue(node))
                {
                    case VReg v when p.CanWrite:
                        p.SetValue(node, to(v));
                        break;
                    case Opnd o when p.CanWrite:
                        if (o.Reg != null) p.SetValue(node, Opnd.Of(to(o.Reg)));
                        break;
                    case List<Opnd> operands:
                        for (int k = 0; k < operands.Count; k++)
                            if (operands[k].Reg is VReg r) operands[k] = Opnd.Of(to(r));
                        break;
                    case List<VReg> values:
                        for (int k = 0; k < values.Count; k++) values[k] = to(values[k]);
                        break;
                    case List<StoreI> held:
                        foreach (StoreI h in held) Rename(h, to);
                        break;
                }
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

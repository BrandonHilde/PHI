namespace Phi.Compiler.CodeGen.Ir
{
    /// <summary>
    /// Small tidyings of the form before registers are given out. For now one: a value
    /// worked out into a temporary and copied straight into a variable (i is i + 1 is
    /// t = i + 1; i = t) is worked out into the variable, when nothing else reads the
    /// temporary. That's most of a loop's counting.
    /// </summary>
    public static class IrPeephole
    {
        public static void Run(IrFunction fn)
        {
            ThreadJumps(fn);
            Lea(fn);
            RemoveDead(fn);
            ZeroWhatsReadFirst(fn);
            Fold(fn);
            TestBits(fn);
        }

        /// <summary>
        /// x * 3, 5 or 9, and with a number added after (n * 3 + 1): one lea, which takes a
        /// cycle where a multiply takes three. The multiply's value has to be read only by the add.
        /// </summary>
        static void Lea(IrFunction fn)
        {
            var reads = new Dictionary<VReg, int>();
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs) foreach (VReg v in RegisterAllocator.Uses(i)) reads[v] = reads.GetValueOrDefault(v) + 1;
                foreach (VReg v in RegisterAllocator.Uses(b.End)) reads[v] = reads.GetValueOrDefault(v) + 1;
            }
            foreach (Block b in fn.Blocks)
            {
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    if (b.Instrs[k] is not BinI { Op: BinOp.Mul, B.IsImm: true, B.Imm: 3 or 5 or 9, A.Reg: VReg a } mul || a.Float) continue;
                    if (k + 1 < b.Instrs.Count && b.Instrs[k + 1] is BinI { Op: BinOp.Add or BinOp.Sub, B.IsImm: true, A.Reg: VReg t } add
                        && t == mul.Dst && reads.GetValueOrDefault(t) == 1 && add.Dst.Wide == mul.Dst.Wide)
                    {
                        long c = add.Op == BinOp.Add ? add.B.Imm : -add.B.Imm;
                        if (c >= int.MinValue && c <= int.MaxValue)
                        {
                            b.Instrs[k] = new LeaI { Dst = add.Dst, A = a, Factor = (int)mul.B.Imm, Add = c };
                            b.Instrs.RemoveAt(k + 1);
                            continue;
                        }
                    }
                    b.Instrs[k] = new LeaI { Dst = mul.Dst, A = a, Factor = (int)mul.B.Imm, Add = 0 };
                }
            }
        }

        /// <summary>
        /// if (n &amp; 1) is 0: the and, whose value nothing else reads, and the comparison with 0
        /// are one test of the bits (test n, 1), when the mask fits an instruction.
        /// </summary>
        static void TestBits(IrFunction fn)
        {
            var reads = new Dictionary<VReg, int>();
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs) foreach (VReg v in RegisterAllocator.Uses(i)) reads[v] = reads.GetValueOrDefault(v) + 1;
                foreach (VReg v in RegisterAllocator.Uses(b.End)) reads[v] = reads.GetValueOrDefault(v) + 1;
            }
            foreach (Block b in fn.Blocks)
            {
                if (b.End is not BranchT { Float: false, TestMask: null, B.IsImm: true, B.Imm: 0, Cond: Cond.Eq or Cond.Ne } br || b.Instrs.Count == 0) continue;
                if (b.Instrs[^1] is not BinI { Op: BinOp.And, B.IsImm: true, A.Reg: VReg x } and || and.Dst != br.A) continue;
                if (reads.GetValueOrDefault(and.Dst) != 1 || and.B.Imm < int.MinValue || and.B.Imm > int.MaxValue) continue;
                b.Instrs.RemoveAt(b.Instrs.Count - 1);
                br.A = x;
                br.TestMask = and.B.Imm;
            }
        }

        /// <summary>
        /// A block with nothing in it but a jump is gone: what jumped to it jumps where it does.
        /// (Loops and ifs leave a few, where one block only leads on to the next.)
        /// </summary>
        static void ThreadJumps(IrFunction fn)
        {
            Block Final(Block b)
            {
                var seen = new HashSet<Block>();
                while (b.Instrs.Count == 0 && b.End is JumpT j && seen.Add(b)) b = j.Target;
                return b;
            }
            foreach (Block b in fn.Blocks)
            {
                switch (b.End)
                {
                    case JumpT j: j.Target = Final(j.Target); break;
                    case BranchT br:
                        br.Then = Final(br.Then);
                        br.Else = Final(br.Else);
                        break;
                    case SwitchT sw:
                        for (int k = 0; k < sw.Cases.Count; k++) sw.Cases[k] = (sw.Cases[k].Value, Final(sw.Cases[k].Target));
                        sw.Default = Final(sw.Default);
                        break;
                    case TryT t:
                        // (Caught starts with CaughtI, so it's never passed over: CatchI's address for it stays right)
                        t.Body = Final(t.Body);
                        break;
                }
            }
            // the first block stays: it's where the method starts
            var reached = new HashSet<Block>();
            var work = new Stack<Block>();
            work.Push(fn.Blocks[0]);
            while (work.Count > 0)
            {
                Block b = work.Pop();
                if (!reached.Add(b)) continue;
                foreach (Block n in IrBuilder.Successors(b)) work.Push(n);
            }
            fn.Blocks.RemoveAll(b => !reached.Contains(b));
        }

        /// <summary>
        /// What works out a value nothing reads goes, when working it out does nothing else: not
        /// a division (which stops on 0), a call, a store, a check, or a read through a pointer.
        /// </summary>
        static void RemoveDead(IrFunction fn)
        {
            for (bool removed = true; removed;)
            {
                removed = false;
                Liveness live = Liveness.Of(fn);
                foreach (Block b in fn.Blocks)
                {
                    var alive = new HashSet<VReg>(live.Out[b]);
                    foreach (VReg v in RegisterAllocator.Uses(b.End)) alive.Add(v);
                    for (int k = b.Instrs.Count - 1; k >= 0; k--)
                    {
                        Instr i = b.Instrs[k];
                        VReg? def = RegisterAllocator.Def(i);
                        if (def != null && !alive.Contains(def) && Pure(i))
                        {
                            b.Instrs.RemoveAt(k);
                            removed = true;
                            continue;
                        }
                        if (def != null) alive.Remove(def);
                        foreach (VReg v in RegisterAllocator.Uses(i)) alive.Add(v);
                    }
                }
            }
        }

        static bool Pure(Instr i) => i switch
        {
            ConstI or CopyI or UnI or ExtendI or NarrowI or SetCcI or LoadI or AddrI or LeaI or FConstI or FBinI or FNegI or ToFloatI or ToWholeI => true,
            VecI or VecMakeI or VecGetI or VecSetI or VecReduceI or RoundF32I => true,
            BinI b => b.Op is not (BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU),
            _ => false,
        };

        /// <summary>
        /// A variable that could be read before anything sets it starts at 0, as every variable
        /// does: it's live where the method starts, so it's given 0 there (and nothing else is).
        /// </summary>
        static void ZeroWhatsReadFirst(IrFunction fn)
        {
            Liveness live = Liveness.Of(fn);
            Block entry = fn.Blocks[0];
            foreach (VReg v in live.In[entry].OrderBy(v => v.Id))
                entry.Instrs.Insert(0, new ConstI { Dst = v, Value = 0 });
        }

        static void Fold(IrFunction fn)
        {
            var reads = new Dictionary<VReg, int>();
            void Count(VReg v) => reads[v] = reads.GetValueOrDefault(v) + 1;
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs) foreach (VReg v in RegisterAllocator.Uses(i)) Count(v);
                foreach (VReg v in RegisterAllocator.Uses(b.End)) Count(v);
            }
            var defined = new Dictionary<VReg, int>();
            foreach (Block b in fn.Blocks)
                foreach (Instr i in b.Instrs)
                    if (RegisterAllocator.Def(i) is VReg d) defined[d] = defined.GetValueOrDefault(d) + 1;

            foreach (Block b in fn.Blocks)
            {
                for (int k = 0; k + 1 < b.Instrs.Count; k++)
                {
                    if (b.Instrs[k + 1] is not CopyI { Src.Reg: VReg temp } copy) continue;
                    if (reads.GetValueOrDefault(temp) != 1 || defined.GetValueOrDefault(temp) != 1 || copy.Dst.Wide != temp.Wide || copy.Dst.Float != temp.Float || copy.Dst.Vec != temp.Vec) continue;
                    Instr? into = Retarget(b.Instrs[k], temp, copy.Dst);
                    if (into == null) continue;
                    b.Instrs[k] = into;
                    b.Instrs.RemoveAt(k + 1);
                }
            }
        }

        /// <summary>i with its destination dst in place of temp, or null when it doesn't write temp.</summary>
        static Instr? Retarget(Instr i, VReg temp, VReg dst) => i switch
        {
            BinI b when b.Dst == temp => new BinI { Op = b.Op, Dst = dst, A = b.A, B = b.B },
            UnI u when u.Dst == temp => new UnI { Op = u.Op, Dst = dst, A = u.A },
            ExtendI e when e.Dst == temp => new ExtendI { Dst = dst, Src = e.Src, Signed = e.Signed },
            NarrowI n when n.Dst == temp => new NarrowI { Dst = dst, Src = n.Src, Kind = n.Kind },
            SetCcI s when s.Dst == temp => new SetCcI { Float = s.Float, Cond = s.Cond, Dst = dst, A = s.A, B = s.B },
            LoadI l when l.Dst == temp => new LoadI { Dst = dst, Place = l.Place, Kind = l.Kind },
            ConstI c when c.Dst == temp => new ConstI { Dst = dst, Value = c.Value },
            FBinI f when f.Dst == temp => new FBinI { Op = f.Op, Dst = dst, A = f.A, B = f.B },
            FNegI f when f.Dst == temp => new FNegI { Dst = dst, A = f.A },
            ToFloatI f when f.Dst == temp => new ToFloatI { Dst = dst, Src = f.Src, Signed = f.Signed },
            ToWholeI f when f.Dst == temp => new ToWholeI { Dst = dst, Src = f.Src },
            FConstI f when f.Dst == temp => new FConstI { Dst = dst, Value = f.Value },
            LeaI l when l.Dst == temp => new LeaI { Dst = dst, A = l.A, Factor = l.Factor, Add = l.Add },
            LoadIdxI l when l.Dst == temp => new LoadIdxI { Dst = dst, Base = l.Base, Index = l.Index, Scale = l.Scale, Kind = l.Kind },
            VecI v when v.Dst == temp => new VecI { Op = v.Op, Dst = dst, A = v.A, B = v.B, C = v.C, Count = v.Count, Lane = v.Lane, To = v.To },
            VecGetI g when g.Dst == temp => new VecGetI { Dst = dst, A = g.A, Index = g.Index, Lane = g.Lane },
            VecReduceI r when r.Dst == temp => new VecReduceI { Op = r.Op, Dst = dst, A = r.A, B = r.B, Lane = r.Lane },
            _ => null,
        };
    }
}

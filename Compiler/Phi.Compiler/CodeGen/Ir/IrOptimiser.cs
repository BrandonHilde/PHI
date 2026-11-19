namespace Phi.Compiler.CodeGen.Ir
{
    /// <summary>
    /// What the form can be spared before registers are given out, each step one that only
    /// takes instructions away (or moves them to where they run less often), and none that
    /// changes what a program does: its results, its errors and their lines, its floats to the
    /// last bit. Each can be left out (IrOptions) to find which one a wrong program comes from.
    ///
    /// Consts: a test of a number written in the program (while true) is no test.
    /// Same: what was worked out, read or checked a moment ago isn't worked out, read or
    /// checked again while nothing it's made from has changed.
    /// Hoist: arithmetic that comes to the same every time round a loop is done once, before it.
    /// </summary>
    public static class IrOptimiser
    {
        /// <param name="loads">
        /// Reads of memory may be used again (never in a program with a volatile anywhere: every
        /// read of one goes to the memory, and the form doesn't say which reads those are).
        /// </param>
        /// <param name="hoistMost">How many values one loop may have worked out before it (each holds a register all the way round).</param>
        public static void Run(IrFunction fn, bool loads, int hoistMost, bool arm)
        {
            // (PHI_OPT_FN=a,b: only the methods whose names have one of these in them, to find the one that goes wrong)
            if (Only.Length > 0 && !Only.Any(fn.Name.Contains)) return;
            if (IrOptions.On("same"))
            {
                if (IrOptions.On("copies")) Copies(fn);
                Same(fn, loads, arm);
            }
            if (IrOptions.On("hoist") && hoistMost > 0) Hoist(fn, hoistMost);
        }

        static readonly string[] Only = (Environment.GetEnvironmentVariable("PHI_OPT_FN") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        // ---------------------------------------------------------------- consts

        /// <summary>
        /// A branch on a value given a number in the same block, and nowhere else, against a
        /// number: it always goes one way, so it's a jump (while true's test, and what a
        /// constant leaves of an if). The blocks nothing reaches then go with ThreadJumps.
        /// </summary>
        public static void Consts(IrFunction fn)
        {
            if (!IrOptions.On("consts")) return;
            var defs = new Dictionary<VReg, int>();
            foreach (Block b in fn.Blocks)
                foreach (Instr i in b.Instrs)
                    if (RegisterAllocator.Def(i) is VReg d) defs[d] = defs.GetValueOrDefault(d) + 1;
            foreach (Block b in fn.Blocks)
            {
                if (b.End is not BranchT { Float: false, B.IsImm: true } br || br.A.Pinned || br.A.Float || br.A.Vec) continue;
                if (defs.GetValueOrDefault(br.A) != 1) continue;
                if (b.Instrs.LastOrDefault(i => RegisterAllocator.Def(i) == br.A) is not ConstI known) continue;
                long a = known.Value, n = br.B.Imm;
                bool holds;
                if (br.TestMask is long mask)
                {
                    if (br.Cond is not (Cond.Eq or Cond.Ne)) continue;
                    long bits = br.A.Wide ? a & mask : (uint)a & (uint)mask;
                    holds = br.Cond == Cond.Ne ? bits != 0 : bits == 0;
                }
                else
                {
                    long sa = br.A.Wide ? a : (int)a, sb = br.A.Wide ? n : (int)n;
                    ulong ua = br.A.Wide ? (ulong)a : (uint)a, ub = br.A.Wide ? (ulong)n : (uint)n;
                    holds = br.Cond switch
                    {
                        Cond.Eq => ua == ub, Cond.Ne => ua != ub,
                        Cond.LtS => sa < sb, Cond.LeS => sa <= sb, Cond.GtS => sa > sb, Cond.GeS => sa >= sb,
                        Cond.LtU => ua < ub, Cond.LeU => ua <= ub, Cond.GtU => ua > ub, _ => ua >= ub,
                    };
                }
                b.End = new JumpT { Target = holds ? br.Then : br.Else };
            }
        }

        // ---------------------------------------------------------------- same

        /// <summary>
        /// x = y, where each is given a value in one place only and neither is ever read before
        /// it's given one: x is y wherever it's read (every read of x comes after the copy, with
        /// y not given again between: if it were, there'd be a way to that read past no copy at
        /// all). So what reads x reads y, and the copy goes, and the register x had. A method put
        /// in where it's called leaves these: its parameters, copied from the caller's values.
        /// </summary>
        static void Copies(IrFunction fn)
        {
            var made = new Dictionary<VReg, int>();
            foreach (Block b in fn.Blocks)
                foreach (Instr i in b.Instrs)
                    if (RegisterAllocator.Def(i) is VReg d) made[d] = made.GetValueOrDefault(d) + 1;
            HashSet<VReg> unset = Liveness.Of(fn).In[fn.Blocks[0]];
            bool Once(VReg v) => !v.Pinned && made.GetValueOrDefault(v) == 1 && !unset.Contains(v);
            var is_ = new Dictionary<VReg, VReg>();
            VReg Is(VReg v)
            {
                while (is_.TryGetValue(v, out VReg? y)) v = y;
                return v;
            }
            foreach (Block b in fn.Blocks)
                b.Instrs.RemoveAll(i =>
                {
                    if (i is not CopyI { Src.Reg: VReg y } c || !Once(c.Dst) || !Once(y)) return false;
                    if (c.Dst.Wide != y.Wide || c.Dst.Float != y.Float || c.Dst.Vec != y.Vec || Is(y) == c.Dst) return false;
                    is_[c.Dst] = y;
                    return true;
                });
            if (is_.Count == 0) return;
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs) Uses(i, Is);
                if (b.End != null) Uses(b.End, Is);
            }
        }

        /// <summary>What's known where a block ends (or so far in it): a value that holds what a key works out.</summary>
        sealed class Known
        {
            /// <summary>The value that holds it (null: a check, which has none).</summary>
            public VReg? Holder;
            public VReg[] Operands = Array.Empty<VReg>();
            public bool Memory;

            /// <summary>The instruction in this block that worked it out; null when it came from the blocks before.</summary>
            public Instr? Source;
        }

        sealed class State
        {
            public readonly Dictionary<string, Known> Table = new();

            /// <summary>x holds what Leader[x] holds (x was copied from it, and neither has changed): what reads x reads that.</summary>
            public readonly Dictionary<VReg, VReg> Leader = new();

            /// <summary>
            /// The same, for an x that was worked out again by something too small to be worth
            /// keeping a value for (i + 1, an index made 64 bits): x is still made and still read,
            /// but what's worked out from it is known to be what's worked out from the other.
            /// </summary>
            public readonly Dictionary<VReg, VReg> Alike = new();

            public VReg L(VReg v)
            {
                v = Read(v);
                return Alike.TryGetValue(v, out VReg? a) ? a : v;
            }

            public VReg Read(VReg v) => Leader.TryGetValue(v, out VReg? l) ? l : v;

            public void Changed(VReg d)
            {
                foreach (string key in Table.Where(e => e.Value.Holder == d || e.Value.Operands.Contains(d)).Select(e => e.Key).ToList()) Table.Remove(key);
                foreach (var same in new[] { Leader, Alike })
                {
                    same.Remove(d);
                    foreach (VReg x in same.Where(e => e.Value == d).Select(e => e.Key).ToList()) same.Remove(x);
                }
            }

            public void MemoryChanged()
            {
                foreach (string key in Table.Where(e => e.Value.Memory).Select(e => e.Key).ToList()) Table.Remove(key);
            }

            public void Clear()
            {
                Table.Clear();
                Leader.Clear();
                Alike.Clear();
            }
        }

        /// <summary>
        /// One instruction, as cheap as the copy that would stand in for it (and on ARM an index
        /// made 64 bits or a base with a number added goes into the read itself, when nothing
        /// else reads it): never taken out, only known to be the same as the one before.
        /// </summary>
        static bool Small(Instr i, bool arm) => i switch
        {
            BinI b => b.Op is not (BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU)
                      && !(b.Op == BinOp.Mul && !(b.B.IsImm && b.B.Imm is 3 or 5 or 9)),
            UnI or NarrowI or LeaI or ExtendI => true,
            SetCcI c => !c.Float,
            AddrI => !arm,
            _ => false,
        };

        /// <summary>Instructions that write no memory and call nothing: what's known stays known past them.</summary>
        static bool Quiet(Instr i) => i is LineMark or ConstI or CopyI or BinI or UnI or ExtendI or NarrowI or SetCcI or SelectI or LoadI or LoadIdxI
            or AddrI or LeaI or FConstI or FBinI or FNegI or ToFloatI or ToWholeI or RoundF32I or CheckI or SignCheckI or AddrOfI
            or VecI or VecMakeI or VecGetI or VecSetI or VecReduceI;

        static string Kind(VReg v) => v.Vec ? "v" : v.Float ? "f" : v.Wide ? "q" : "d";

        /// <summary>
        /// What an instruction works out, as text that's the same for two that work out the same
        /// from the same values (each named by the value it's a copy of): null for one that's
        /// always done. Memory: it reads memory, so a write between two of them parts them.
        /// </summary>
        static string? Key(Instr i, State s, bool loads, out VReg[] operands, out bool memory)
        {
            var named = new List<VReg>();
            bool pinned = false;
            string V(VReg v)
            {
                v = s.L(v);
                if (v.Pinned) pinned = true;
                named.Add(v);
                // (not its Id: a method put in where it's called brings values numbered from 0 again)
                if (!names!.TryGetValue(v, out int name)) names[v] = name = names.Count;
                return $"v{name}";
            }
            string O(Opnd o) => o.IsImm ? $"#{o.Imm}" : V(o.Reg!);
            memory = false;
            string? key = i switch
            {
                BinI b => $"bin {b.Op} {Kind(b.Dst)} {O(b.A)} {O(b.B)}",
                UnI u => $"un {u.Op} {Kind(u.Dst)} {V(u.A)}",
                ExtendI e => $"ext {e.Signed} {Kind(e.Dst)} {V(e.Src)}",
                NarrowI n => $"narrow {n.Kind} {Kind(n.Dst)} {V(n.Src)}",
                SetCcI c => $"set {c.Float} {c.Cond} {Kind(c.Dst)} {V(c.A)} {O(c.B)}",
                LeaI l => $"lea {l.Factor} {l.Add} {Kind(l.Dst)} {V(l.A)}",
                FBinI f => $"fbin {f.Op} {V(f.A)} {V(f.B)}",
                FNegI f => $"fneg {V(f.A)}",
                ToFloatI f => $"tofloat {f.Signed} {V(f.Src)}",
                ToWholeI f => $"towhole {Kind(f.Dst)} {V(f.Src)}",
                AddrI a => $"addr {Kind(a.Dst)} {a.Label}",
                LoadI l when loads => $"load {l.Kind} {Kind(l.Dst)} {l.Place}",
                LoadIdxI l when loads => $"loadx {l.Kind} {Kind(l.Dst)} {V(l.Base)} {O(l.Index)} {l.Scale} {l.IndexSigned}",
                CheckI c => $"check {V(c.Index)} {c.Limit} {(c.LimitValue != null ? V(c.LimitValue) : "")}",
                SignCheckI c => $"sign {V(c.Value)}",
                _ => null,
            };
            memory = i is LoadI or LoadIdxI;
            operands = named.ToArray();
            if (RegisterAllocator.Def(i) is VReg d && d.Pinned) return null;
            return pinned ? null : key;
        }

        /// <summary>A number of its own for each value Same names in a key.</summary>
        [ThreadStatic] static Dictionary<VReg, int>? names;

        static void Same(IrFunction fn, bool loads, bool arm)
        {
            names = new Dictionary<VReg, int>();
            var preds = new Dictionary<Block, List<Block>>();
            foreach (Block b in fn.Blocks) preds[b] = new List<Block>();
            foreach (Block b in fn.Blocks)
                foreach (Block to in IrBuilder.Successors(b))
                    if (preds.TryGetValue(to, out List<Block>? into) && !into.Contains(b)) into.Add(b);
            var caught = fn.Blocks.Where(b => b.End is TryT).Select(b => ((TryT)b.End!).Caught).ToHashSet();

            // first each block by itself; then each again with what all the blocks before it know
            // (those laid out after it, which come round a loop to it, as the first time left them)
            var ends = new Dictionary<Block, State>();
            var kept = new HashSet<Instr>();
            var gives = new List<(Block In, CopyI Copy)>();
            foreach (Block b in fn.Blocks) ends[b] = Block(fn, b, loads, arm, null, ends, kept, gives);
            foreach (Block b in fn.Blocks.ToList())
            {
                List<Block> before = preds[b];
                bool from = b != fn.Blocks[0] && before.Count > 0 && !before.Contains(b) && !caught.Contains(b) && before.All(p => p.End is not TryT);
                ends[b] = Block(fn, b, loads, arm, from ? before : null, ends, kept, gives);
            }

            // a block that gives its value on (h = t as it ends), where t is made once and read
            // only in that block: it's made in h there, and the copy goes
            var made = new Dictionary<VReg, int>();
            var read = new Dictionary<VReg, HashSet<Block>>();
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    if (RegisterAllocator.Def(i) is VReg d) made[d] = made.GetValueOrDefault(d) + 1;
                    foreach (VReg u in RegisterAllocator.Uses(i)) (read.TryGetValue(u, out var where) ? where : read[u] = new HashSet<Block>()).Add(b);
                }
                foreach (VReg u in RegisterAllocator.Uses(b.End)) (read.TryGetValue(u, out var where) ? where : read[u] = new HashSet<Block>()).Add(b);
            }
            if (IrOptions.On("gives"))
            foreach (var (p, copy) in gives)
            {
                VReg t = copy.Src.Reg!, h = copy.Dst;
                if (t.Pinned || made.GetValueOrDefault(t) != 1 || !read.TryGetValue(t, out var readers) || readers.Count != 1 || !readers.Contains(p)) continue;
                int at = p.Instrs.FindIndex(i => RegisterAllocator.Def(i) == t);
                // (t isn't read before it's made, which would be last time round's; and nothing
                // between reads h, or gives it: there it still holds what came round)
                if (at < 0 || p.Instrs.Take(at).Any(i => RegisterAllocator.Uses(i).Contains(t)) || !p.Instrs.Remove(copy)) continue;
                if (p.Instrs.Skip(at).Any(i => RegisterAllocator.Def(i) == h || RegisterAllocator.Uses(i).Contains(h)) || RegisterAllocator.Uses(p.End).Contains(h))
                {
                    p.Instrs.Add(copy);
                    continue;
                }
                foreach (Instr i in p.Instrs.Skip(at)) Uses(i, v => v == t ? h : v);
                Uses(p.End!, v => v == t ? h : v);
                made.Remove(t);
            }
        }

        /// <summary>
        /// One block, start to end. With before (every block that comes to this one): what all
        /// of them know where they end is known here too, in a value each of them gives
        /// (h = its own, where they differ) as it ends.
        /// </summary>
        static State Block(IrFunction fn, Block b, bool loads, bool arm, List<Block>? before, Dictionary<Block, State> ends, HashSet<Instr> kept, List<(Block In, CopyI Copy)> gives)
        {
            var s = new State();
            // what's happened in this block so far, which parts it from what the blocks before know
            var changed = new HashSet<VReg>();
            bool wrote = false, called = false;
            // a value made for what the blocks before know: what each of them calls it
            var joined = new Dictionary<VReg, VReg[]>();

            void Made(VReg d)
            {
                s.Changed(d);
                changed.Add(d);
                joined.Remove(d);
                // (a variable whose address is taken is memory too)
                if (d.Pinned)
                {
                    s.MemoryChanged();
                    wrote = true;
                }
            }

            // what every block before this one knows i's value as, where each ends; null when one doesn't
            Known[]? Before(Instr i, VReg[] operands, bool memory)
            {
                if (before == null || called || memory && wrote || kept.Contains(i) || !IrOptions.On("before")) return null;
                if (operands.Any(o => changed.Contains(o) && !joined.ContainsKey(o))) return null;
                var theirs = new Known[before.Count];
                for (int p = 0; p < before.Count; p++)
                {
                    State end = ends[before[p]];
                    // the same instruction as that block would name it: its own values for the joined ones
                    var there = new State();
                    foreach (VReg o in operands)
                    {
                        VReg its = joined.TryGetValue(o, out VReg[]? each) ? each[p] : end.L(o);
                        if (its != o) there.Leader[o] = its;
                    }
                    string? key = Key(i, there, loads, out _, out _);
                    if (key == null || !end.Table.TryGetValue(key, out Known? known) || memory && known.Source == null) return null;
                    theirs[p] = known;
                }
                return theirs;
            }

            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                VReg? def = RegisterAllocator.Def(i);
                Uses(i, v => v == def ? v : s.Read(v));
                if (!Quiet(i))
                {
                    if (i is StoreI or StoreIdxI)
                    {
                        s.MemoryChanged();
                        wrote = true;
                    }
                    else
                    {
                        s.Clear();
                        called = true;
                    }
                    if (def != null) Made(def);
                    continue;
                }
                if (i is CopyI { Src.Reg: VReg src } copy && !copy.Dst.Pinned && !src.Pinned
                    && copy.Dst.Wide == src.Wide && copy.Dst.Float == src.Float && copy.Dst.Vec == src.Vec)
                {
                    // (the program's own copy, i is t: what reads i still reads i, or t would have to
                    // be kept as long as i is; but what's worked out from i is what's worked out from t)
                    VReg l = s.L(src);
                    if (l == copy.Dst)
                    {
                        // (it holds that already)
                        b.Instrs.RemoveAt(k--);
                        continue;
                    }
                    Made(copy.Dst);
                    s.Alike[copy.Dst] = l;
                    continue;
                }
                string? key = Key(i, s, loads, out VReg[] operands, out bool memory);
                if (key == null || kept.Contains(i))
                {
                    if (def != null) Made(def);
                    if (key != null && !operands.Contains(def!)) s.Table[key] = new Known { Holder = def, Operands = operands, Memory = memory, Source = i };
                    continue;
                }
                s.Table.TryGetValue(key, out Known? known);
                if (def == null)
                {
                    // a check: one that's been made, of the same index against the same size, isn't made again
                    if (known != null || Before(i, operands, false) != null)
                    {
                        s.Table[key] = known ?? new Known { Operands = operands };
                        b.Instrs.RemoveAt(k--);
                    }
                    else s.Table[key] = new Known { Operands = operands, Source = i };
                    continue;
                }
                if (Small(i, arm))
                {
                    if (known != null)
                    {
                        if (known.Holder == def) continue;
                        VReg same = known.Holder!;
                        Made(def);
                        s.Alike[def] = same;
                        continue;
                    }
                    Known[]? each = Before(i, operands, false);
                    Made(def);
                    if (!operands.Contains(def)) s.Table[key] = new Known { Holder = def, Operands = operands, Source = i };
                    if (each != null) joined[def] = each.Select(e => e.Holder!).ToArray();
                    continue;
                }
                VReg? holder = known?.Holder;
                if (holder == null && Before(i, operands, memory) is Known[] theirs)
                {
                    if (memory) foreach (Known t in theirs) kept.Add(t.Source!);
                    VReg first = theirs[0].Holder!;
                    if (theirs.All(t => t.Holder == first) && !changed.Contains(first)) holder = first;
                    else
                    {
                        holder = def.Vec ? fn.NewVec() : def.Float ? fn.NewFloat() : fn.NewValue(def.Wide);
                        for (int p = 0; p < before!.Count; p++)
                        {
                            var give = new CopyI { Dst = holder, Src = theirs[p].Holder! };
                            before[p].Instrs.Add(give);
                            gives.Add((before[p], give));
                        }
                        joined[holder] = theirs.Select(t => t.Holder!).ToArray();
                    }
                }
                if (holder == null)
                {
                    Made(def);
                    if (!operands.Contains(def)) s.Table[key] = new Known { Holder = def, Operands = operands, Memory = memory, Source = i };
                    continue;
                }
                if (known == null) s.Table[key] = new Known { Holder = holder, Operands = operands, Memory = memory };
                if (holder == def)
                {
                    b.Instrs.RemoveAt(k--);
                    continue;
                }
                b.Instrs[k] = new CopyI { Dst = def, Src = holder };
                Known keep = s.Table[key];
                Made(def);
                // (what's known of the holder is still so, unless it was made from def itself)
                if (!keep.Operands.Contains(def)) s.Table[key] = keep;
                s.Leader[def] = holder;
            }
            if (b.End != null) Uses(b.End, s.Read);
            return s;
        }

        /// <summary>
        /// Each value an instruction (or a block's end) reads, replaced: in its own properties,
        /// its lists of operands, and what it holds to write back. What it makes is left (to
        /// gives it back as it is).
        /// </summary>
        static void Uses(object node, Func<VReg, VReg> to)
        {
            if (node is LineMark or ConstI or FConstI) return;
            foreach (System.Reflection.PropertyInfo p in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                switch (p.GetValue(node))
                {
                    case VReg v when p.CanWrite:
                        if (to(v) is VReg now && now != v) p.SetValue(node, now);
                        break;
                    case Opnd o when p.CanWrite:
                        if (o.Reg != null && to(o.Reg) is VReg other && other != o.Reg) p.SetValue(node, Opnd.Of(other));
                        break;
                    case List<Opnd> operands:
                        for (int k = 0; k < operands.Count; k++)
                            if (operands[k].Reg is VReg r) operands[k] = Opnd.Of(to(r));
                        break;
                    case List<VReg> values:
                        for (int k = 0; k < values.Count; k++) values[k] = to(values[k]);
                        break;
                    case List<StoreI> held:
                        foreach (StoreI h in held) Uses(h, to);
                        break;
                }
            }
        }

        // ---------------------------------------------------------------- hoist

        /// <summary>Arithmetic that can't stop the program and reads no memory: the same from the same values, wherever it's done.</summary>
        static bool Movable(Instr i) => i switch
        {
            // (a multiply by 3, 5 or 9 is one quick instruction, and one by a number may be an address's scale)
            BinI b => b.Op == BinOp.Mul && !b.B.IsImm && !b.A.IsImm,
            FBinI or FNegI or ToFloatI or ToWholeI => true,
            _ => false,
        };

        /// <summary>
        /// A loop is the blocks from the one a jump goes back to (its first) to the one that jumps,
        /// as they're laid out, when its first block is the only way in. What one of its
        /// instructions works out from values nothing in the loop changes, into a value nothing
        /// else gives and nothing reads before, is worked out in a block of its own just before
        /// the loop: once, not every time round. The innermost loops first, so what's the same
        /// all the way round an outer one too moves out of both.
        /// </summary>
        static void Hoist(IrFunction fn, int most)
        {
            var done = new HashSet<Block>();
            while (true)
            {
                var index = new Dictionary<Block, int>();
                for (int k = 0; k < fn.Blocks.Count; k++) index[fn.Blocks[k]] = k;
                // each loop's first block, and its last
                var last = new Dictionary<Block, int>();
                for (int k = 0; k < fn.Blocks.Count; k++)
                    foreach (Block to in IrBuilder.Successors(fn.Blocks[k]))
                        if (index[to] <= k) last[to] = Math.Max(last.GetValueOrDefault(to, -1), k);
                Block? head = last.Keys.Where(h => !done.Contains(h)).OrderBy(h => last[h] - index[h]).ThenBy(h => index[h]).FirstOrDefault();
                if (head == null) return;
                done.Add(head);
                HoistLoop(fn, head, index[head], last[head], most);
            }
        }

        static void HoistLoop(IrFunction fn, Block head, int from, int to, int most)
        {
            if (from == 0) return;
            var inside = new HashSet<Block>(fn.Blocks.Skip(from).Take(to - from + 1));
            var outside = fn.Blocks.Where(b => !inside.Contains(b)).ToList();
            // the first block is the only way in, and not where a throw comes back to
            if (outside.Any(b => IrBuilder.Successors(b).Any(n => n != head && inside.Contains(n)))) return;
            var ways = outside.Where(b => IrBuilder.Successors(b).Contains(head)).ToList();
            if (ways.Count == 0 || ways.Any(b => b.End is TryT or SwitchT)) return;
            // (a value moved out is kept all the way round: across a call that's a register the
            // method has to save, or for a float the frame, so a loop that calls is left as it is)
            if (inside.Any(b => b.Instrs.Any(i => RegisterAllocator.IsCall(i) || i is RawCallI or X87I))) return;

            var made = new Dictionary<VReg, int>();
            var madeInside = new HashSet<VReg>();
            foreach (Block b in fn.Blocks)
                foreach (Instr i in b.Instrs)
                    if (RegisterAllocator.Def(i) is VReg d)
                    {
                        made[d] = made.GetValueOrDefault(d) + 1;
                        if (inside.Contains(b)) madeInside.Add(d);
                    }
            Liveness live = Liveness.Of(fn);
            var moved = new List<Instr>();
            var movedValues = new HashSet<VReg>();
            foreach (Block b in fn.Blocks.Skip(from).Take(to - from + 1))
                for (int k = 0; k < b.Instrs.Count && moved.Count < most; k++)
                {
                    Instr i = b.Instrs[k];
                    if (!Movable(i) || RegisterAllocator.Def(i) is not VReg d || d.Pinned || d.Vec) continue;
                    if (made.GetValueOrDefault(d) != 1 || live.In[head].Contains(d)) continue;
                    if (RegisterAllocator.Uses(i).Any(u => u.Pinned || madeInside.Contains(u) && !movedValues.Contains(u))) continue;
                    b.Instrs.RemoveAt(k--);
                    moved.Add(i);
                    movedValues.Add(d);
                }
            if (moved.Count == 0) return;

            var before = new Block { Id = fn.Blocks.Max(b => b.Id) + 1 };
            before.Instrs.AddRange(moved);
            before.End = new JumpT { Target = head };
            foreach (Block b in ways)
                switch (b.End)
                {
                    case JumpT j: j.Target = before; break;
                    case BranchT br:
                        if (br.Then == head) br.Then = before;
                        if (br.Else == head) br.Else = before;
                        break;
                }
            fn.Blocks.Insert(from, before);
        }
    }
}

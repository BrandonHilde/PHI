namespace Phi.Compiler.CodeGen.Ir
{
    /// <summary>
    /// Registers for a method's values, by linear scan (Poletto and Sarkar): each value lives
    /// from where it's first given to where it's last needed, in the order the blocks are
    /// written, made longer over the blocks it's live through (found by liveness, so a loop's
    /// variables live round the whole loop). A value whose life spans a call gets one of r12
    /// to r15, which every method keeps; any other prefers rbx, rsi, rdi and r8 to r10, which a
    /// call may change. rax, rcx, rdx and r11 are the emitter's own, never given out. A value
    /// with no register free keeps its place in the frame (the one that's needed furthest
    /// ahead gives its register up).
    /// </summary>
    public sealed class RegisterAllocator
    {
        public static readonly string[] CallerSaved = { "rbx", "rsi", "rdi", "r8", "r9", "r10" };
        public static readonly string[] CalleeSaved = { "r12", "r13", "r14", "r15" };

        /// <summary>
        /// The f64s' and vecs' registers. A call may change every one of them (no method keeps
        /// them), so one needed after a call is kept in the frame; xmm12 to xmm15 are the
        /// emitter's own (a vec's work takes up to four at once).
        /// </summary>
        public static readonly string[] FloatRegisters = Enumerable.Range(0, 12).Select(i => $"xmm{i}").ToArray();

        /// <summary>Each value's register (its 64-bit name), or none: then its slot in the frame.</summary>
        public Dictionary<VReg, string> Registers { get; } = new();

        /// <summary>Each value that has no register: its place below rbp (8, 16, ...).</summary>
        public Dictionary<VReg, int> Slots { get; } = new();

        /// <summary>The registers of r12 to r15 used, which the method saves and puts back.</summary>
        public List<string> UsedCalleeSaved { get; } = new();

        sealed class Interval
        {
            public VReg Value = null!;
            public int Start = int.MaxValue, End = -1;
            public bool CrossesCall;
            public string? Register;

            /// <summary>How much keeping it in memory would cost: its uses, ten times as many for each loop they're in.</summary>
            public double Weight;
        }

        /// <summary>
        /// 32-bit code's (named as their 64-bit registers, as the emitter writes them): ebx anyone's;
        /// esi and edi the caller's, as the generator keeps its variables in them; eax, ecx and edx
        /// the emitter's own.
        /// </summary>
        public static readonly string[] CallerSaved32 = { "rbx" };
        public static readonly string[] CalleeSaved32 = { "rsi", "rdi" };

        string[] callerSaved = CallerSaved, calleeSaved = CalleeSaved, floatRegisters = FloatRegisters;
        bool x32, reuseSaved;

        /// <summary>What else is a call on a target (an instruction its emitter writes as one), or null.</summary>
        Func<Instr, bool>? alsoCall, sharesResult;

        /// <summary>
        /// Another CPU's registers for values, in place of x64's (for ARM: CodeGen/A64): those a
        /// call may change, those every method keeps, the floats' (none kept across a call), and
        /// what else its emitter writes as a call. The scan is the same.
        /// </summary>
        /// <param name="SharesResult">
        /// The instructions whose result may have the register of an operand read there for the
        /// last time (its emitter reads every operand before it writes the result).
        /// </param>
        public sealed record Target(string[] CallerSaved, string[] CalleeSaved, string[] Float, Func<Instr, bool>? AlsoCall = null,
                                    Func<Instr, bool>? SharesResult = null);

        public static RegisterAllocator Allocate(IrFunction fn, bool x32 = false, Target? target = null)
        {
            var allocator = new RegisterAllocator();
            if (x32)
            {
                allocator.x32 = true;
                allocator.callerSaved = CallerSaved32;
                allocator.calleeSaved = CalleeSaved32;
            }
            if (target != null)
            {
                allocator.callerSaved = target.CallerSaved;
                allocator.calleeSaved = target.CalleeSaved;
                allocator.floatRegisters = target.Float;
                allocator.alsoCall = target.AlsoCall;
                allocator.reuseSaved = true;
                allocator.sharesResult = target.SharesResult;
            }
            allocator.Run(fn);
            return allocator;
        }

        // ---------------------------------------------------------------- what each instruction reads and writes

        public static IEnumerable<VReg> Uses(Instr i)
        {
            switch (i)
            {
                case CopyI c: if (c.Src.Reg != null) yield return c.Src.Reg; break;
                case BinI b:
                    if (b.A.Reg != null) yield return b.A.Reg;
                    if (b.B.Reg != null) yield return b.B.Reg;
                    break;
                case UnI u: yield return u.A; break;
                case ExtendI e: yield return e.Src; break;
                case NarrowI n: yield return n.Src; break;
                case SetCcI s:
                    yield return s.A;
                    if (s.B.Reg != null) yield return s.B.Reg;
                    break;
                case StoreI st: if (st.Src.Reg != null) yield return st.Src.Reg; break;
                case CallI call:
                    foreach (Opnd a in call.Args) if (a.Reg != null) yield return a.Reg;
                    if (call.Target != null) yield return call.Target;
                    break;
                case LoadIdxI l:
                    yield return l.Base;
                    if (l.Index.Reg != null) yield return l.Index.Reg;
                    break;
                case StoreIdxI st:
                    yield return st.Base;
                    if (st.Index.Reg != null) yield return st.Index.Reg;
                    if (st.Src.Reg != null) yield return st.Src.Reg;
                    break;
                case SignCheckI c:
                    yield return c.Value;
                    foreach (StoreI h in c.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
                case FBinI f:
                    yield return f.A;
                    yield return f.B;
                    break;
                case CheckI c:
                    yield return c.Index;
                    if (c.LimitValue != null) yield return c.LimitValue;
                    foreach (StoreI h in c.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
                case FNegI f: yield return f.A; break;
                case LeaI l: yield return l.A; break;
                case UnlockI u:
                    yield return u.Saved;
                    if (u.At != null) yield return u.At;
                    break;
                case LockI { At: VReg lockAt }: yield return lockAt; break;
                case ObjCallI oc:
                    if (oc.Arg != null) yield return oc.Arg;
                    if (oc.Second != null) yield return oc.Second;
                    break;
                case ToFloatI f: yield return f.Src; break;
                case ToWholeI f: yield return f.Src; break;
                case RoundF32I r: yield return r.Src; break;
                case VecI v:
                    yield return v.A;
                    if (v.B != null) yield return v.B;
                    if (v.C != null) yield return v.C;
                    if (v.Count.Reg != null) yield return v.Count.Reg;
                    break;
                case VecMakeI m:
                    foreach (Opnd o in m.Lanes) if (o.Reg != null) yield return o.Reg;
                    break;
                case VecGetI g:
                    yield return g.A;
                    if (g.Index.Reg != null) yield return g.Index.Reg;
                    break;
                case VecSetI st:
                    yield return st.A;
                    if (st.Index.Reg != null) yield return st.Index.Reg;
                    if (st.Value.Reg != null) yield return st.Value.Reg;
                    break;
                case VecReduceI r:
                    yield return r.A;
                    if (r.B != null) yield return r.B;
                    break;
                case PrintI p: if (p.Value.Reg != null) yield return p.Value.Reg; break;
                case AtomicI at:
                    yield return at.Address;
                    if (at.Value.Reg != null) yield return at.Value.Reg;
                    if (at.Expected.Reg != null) yield return at.Expected.Reg;
                    break;
                case SysCallI sc:
                    foreach (Opnd a in sc.Args) if (a.Reg != null) yield return a.Reg;
                    break;
                case GrowStoreI gs:
                    yield return gs.Slot;
                    if (gs.Value.Reg != null) yield return gs.Value.Reg;
                    break;
                case NumberTextI nt: if (nt.Value.Reg != null) yield return nt.Value.Reg; break;
                case AddrOfI ao: yield return ao.Of; break;
                case X87I x87:
                    yield return x87.A;
                    if (x87.B != null) yield return x87.B;
                    break;
                case ArrayRoutineI ar:
                    yield return ar.View;
                    if (ar.Made != null) yield return ar.Made;
                    break;
                case StopI st:
                    foreach (StoreI h in st.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
                case HasTextI ht:
                    yield return ht.Text;
                    if (ht.Value.Reg != null) yield return ht.Value.Reg;
                    break;
                case InterfaceMethodI im: yield return im.Receiver; break;
                case ExitI ex:
                    if (ex.Code.Reg != null) yield return ex.Code.Reg;
                    foreach (StoreI h in ex.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
                case FreeTextI { Value: not null } ftv: yield return ftv.Value; break;
                case CompareBytesI cmb:
                    yield return cmb.A;
                    yield return cmb.B;
                    yield return cmb.Count;
                    break;
                case CopyBytesI cb:
                    yield return cb.Dst;
                    yield return cb.Src;
                    yield return cb.Count;
                    break;
                case ResultTextI rt:
                    yield return rt.Value;
                    foreach (StoreI h in rt.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
                case FixedStoreI fs:
                    yield return fs.Dst;
                    if (fs.Value.Reg != null) yield return fs.Value.Reg;
                    foreach (StoreI h in fs.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
                case ThrowI th:
                    yield return th.Text;
                    foreach (StoreI h in th.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
                case ArrayGrowI g:
                    yield return g.View;
                    yield return g.Count;
                    break;
                case TextI t:
                    if (t.A != null) yield return t.A;
                    if (t.B != null) yield return t.B;
                    foreach (StoreI h in t.Held) if (h.Src.Reg != null) yield return h.Src.Reg;
                    break;
            }
        }

        public static VReg? Def(Instr i) => i switch
        {
            ConstI c => c.Dst,
            CopyI c => c.Dst,
            BinI b => b.Dst,
            UnI u => u.Dst,
            ExtendI e => e.Dst,
            NarrowI n => n.Dst,
            SetCcI s => s.Dst,
            LoadI l => l.Dst,
            CallI c => c.Dst,
            LoadIdxI l => l.Dst,
            AddrI a => a.Dst,
            LeaI l => l.Dst,
            LockI l => l.Saved,
            FConstI f => f.Dst,
            FBinI f => f.Dst,
            FNegI f => f.Dst,
            ToFloatI f => f.Dst,
            ToWholeI f => f.Dst,
            RoundF32I r => r.Dst,
            VecI v => v.Dst,
            VecMakeI m => m.Dst,
            VecGetI g => g.Dst,
            VecSetI st => st.Dst,
            VecReduceI r => r.Dst,
            TextI t => t.Dst,
            SysCallI sc => sc.Dst,
            NumberTextI nt => nt.Dst,
            AddrOfI ao => ao.Dst,
            X87I x87 => x87.Dst,
            CompareBytesI cmb => cmb.Dst,
            RawCallI { Dst: not null } rc => rc.Dst,
            HasTextI ht => ht.Dst,
            InterfaceMethodI im => im.Dst,
            ResultTextI rt => rt.Dst,
            ObjCallI oc => oc.Dst,
            AtomicI at => at.Dst,
            _ => null,
        };

        /// <summary>An instruction that calls something, which may change what a call changes.</summary>
        public static bool IsCall(Instr i) => i is CallI or ObjCallI or PrintI or RoutineI or CatchI or ArrayGrowI or SysCallI
            or GrowStoreI or NumberTextI or FreeTextI or FixedStoreI or ResultTextI or CopyBytesI or HasTextI or CompareBytesI or ArrayRoutineI
            || i is TextI { Op: not (TextOp.Literal or TextOp.OrEmpty) };

        public static IEnumerable<VReg> Uses(Terminator? t)
        {
            switch (t)
            {
                case BranchT br:
                    yield return br.A;
                    if (br.B.Reg != null) yield return br.B.Reg;
                    break;
                case RetT { Value: Opnd v } when v.Reg != null:
                    yield return v.Reg;
                    break;
                case SwitchT sw:
                    yield return sw.Value;
                    break;
            }
        }

        // ---------------------------------------------------------------- the scan

        void Run(IrFunction fn)
        {
            Liveness live = Liveness.Of(fn);
            var liveIn = live.In;
            var liveOut = live.Out;

            // each value's life, over the blocks in the order they're written
            var intervals = new Dictionary<VReg, Interval>();
            Interval Of(VReg v)
            {
                if (!intervals.TryGetValue(v, out Interval? iv)) intervals[v] = iv = new Interval { Value = v };
                return iv;
            }
            void Touch(VReg v, int at)
            {
                Interval iv = Of(v);
                if (at < iv.Start) iv.Start = at;
                if (at > iv.End) iv.End = at;
            }
            // how deep in loops each block is: a jump back to a block laid out before (or at) it
            // is a loop, and the blocks from there to here are in it (blocks are laid out in the
            // order of the source, so a loop's are together)
            var index = new Dictionary<Block, int>();
            for (int k = 0; k < fn.Blocks.Count; k++) index[fn.Blocks[k]] = k;
            var depth = new int[fn.Blocks.Count];
            for (int k = 0; k < fn.Blocks.Count; k++)
                foreach (Block next in IrBuilder.Successors(fn.Blocks[k]))
                    if (index[next] <= k)
                        for (int d = index[next]; d <= k; d++) depth[d]++;
            void Weigh(VReg v, int k) => Of(v).Weight += Math.Pow(10, Math.Min(depth[k], 6));

            var calls = new List<int>();
            // (an assembly block may change any register, r12 to r15 and the xmms too)
            var rawCalls = new List<int>();
            int pos = 0;
            for (int k = 0; k < fn.Blocks.Count; k++)
            {
                Block b = fn.Blocks[k];
                int start = pos;
                foreach (VReg v in liveIn[b]) Touch(v, start);
                foreach (Instr i in b.Instrs)
                {
                    pos += 2;
                    foreach (VReg v in Uses(i))
                    {
                        Touch(v, pos);
                        Weigh(v, k);
                    }
                    if (Def(i) is VReg def)
                    {
                        // (in 32-bit code, with three registers, an operand read here for the last
                        // time gives its register to what's made here: the result starts after it)
                        Touch(def, x32 || sharesResult?.Invoke(i) == true ? pos + 1 : pos);
                        Weigh(def, k);
                    }
                    if (IsCall(i) || alsoCall?.Invoke(i) == true) calls.Add(pos);
                    // (in 32-bit code a library routine takes its arguments in esi and edi, the
                    // registers values are kept in across calls, and may change them: it's as an
                    // assembly block is, and so is anything else that isn't a method's call)
                    if (i is RawCallI || x32 && IsCall(i) && i is not CallI { Routine: false }) rawCalls.Add(pos);
                    // (and a 64-bit multiply, divide or remainder is the library's, in edx:eax and ebx:ecx)
                    else if (x32 && i is BinI { Dst.Wide: true, Op: BinOp.Mul or BinOp.DivS or BinOp.DivU or BinOp.RemS or BinOp.RemU }) rawCalls.Add(pos);
                }
                pos += 2;
                foreach (VReg v in Uses(b.End))
                {
                    Touch(v, pos);
                    Weigh(v, k);
                }
                foreach (VReg v in liveOut[b]) Touch(v, pos + 1);
                pos += 2;
            }
            foreach (Interval iv in intervals.Values)
                iv.CrossesCall = calls.Any(c => c > iv.Start && c < iv.End);
            // (the method's homes are first, at the top of the frame)
            int slotsForRaw = fn.Homes;
            // where a throw comes back to, nothing is in a register: what's needed there is in the frame
            var caught = new HashSet<VReg>(fn.Blocks.Where(b => b.End is TryT).SelectMany(b => liveIn[((TryT)b.End!).Caught]));
            foreach (Interval iv in intervals.Values.OrderBy(i => i.Value.Id))
                // (in 32-bit code an i64 is two halves: the emitter works on them where they are in the frame)
                if (rawCalls.Any(c => c > iv.Start && c < iv.End) || caught.Contains(iv.Value) || iv.Value.Pinned || x32 && iv.Value.Int64)
                {
                    slotsForRaw += iv.Value.Vec ? 2 : 1;
                    Slots[iv.Value] = 8 * slotsForRaw;
                    iv.Weight = -1;   // (taken out of the scan below)
                }

            var free = new List<string>(callerSaved.Concat(calleeSaved));
            // (32-bit code has xmm0 to xmm7: the emitter's own are xmm4 to xmm7 there, as its xmm12 to xmm15)
            var freeFloat = new List<string>(x32 ? floatRegisters.Take(4) : floatRegisters);
            var active = new List<Interval>();
            var saved = new HashSet<string>();
            bool reuse = reuseSaved;
            var activeFloat = new List<Interval>();
            int slots = slotsForRaw;
            foreach (Interval iv in intervals.Values.Where(i => i.Weight >= 0).OrderBy(i => i.Start).ThenBy(i => i.Value.Id))
            {
                if (iv.Value.Float || iv.Value.Vec)
                {
                    foreach (Interval done in activeFloat.Where(a => a.End < iv.Start).ToList())
                    {
                        activeFloat.Remove(done);
                        freeFloat.Add(done.Register!);
                    }
                    if (iv.CrossesCall || freeFloat.Count == 0)
                    {
                        // (no register keeps an f64 across a call; with none free, whichever costs least goes)
                        Interval? cheapest = iv.CrossesCall ? null : activeFloat.OrderBy(a => a.Weight).FirstOrDefault();
                        if (cheapest != null && cheapest.Weight < iv.Weight)
                        {
                            iv.Register = cheapest.Register;
                            cheapest.Register = null;
                            activeFloat.Remove(cheapest);
                            slots += cheapest.Value.Vec ? 2 : 1;
                            Slots[cheapest.Value] = 8 * slots;
                            activeFloat.Add(iv);
                        }
                        else
                        {
                            // (a vec's 16 bytes take two slots: its place is the lower one's)
                            slots += iv.Value.Vec ? 2 : 1;
                            Slots[iv.Value] = 8 * slots;
                        }
                        continue;
                    }
                    iv.Register = freeFloat[0];
                    freeFloat.RemoveAt(0);
                    activeFloat.Add(iv);
                    continue;
                }
                // those that ended give their registers back
                foreach (Interval done in active.Where(a => a.End < iv.Start).ToList())
                {
                    active.Remove(done);
                    free.Add(done.Register!);
                }
                // (reuse: of the registers every method keeps, one this method already saves comes
                // before one it doesn't yet, so a short method saves few; x86's order is as it was)
                string? register = iv.CrossesCall
                    ? (reuse ? free.FirstOrDefault(r => saved.Contains(r)) : null) ?? free.FirstOrDefault(r => calleeSaved.Contains(r))
                    : free.FirstOrDefault(r => callerSaved.Contains(r)) ?? (reuse ? free.FirstOrDefault(r => saved.Contains(r)) : null) ?? free.FirstOrDefault();
                if (register == null)
                {
                    // none free: whichever costs least in memory (of those whose register this
                    // one can have, and this one) goes to the frame
                    Interval? furthest = active
                        .Where(a => !iv.CrossesCall || calleeSaved.Contains(a.Register!))
                        .OrderBy(a => a.Weight).ThenByDescending(a => a.End).FirstOrDefault();
                    if (furthest != null && furthest.Weight < iv.Weight)
                    {
                        register = furthest.Register!;
                        furthest.Register = null;
                        active.Remove(furthest);
                        Slots[furthest.Value] = 8 * ++slots;
                    }
                    else
                    {
                        Slots[iv.Value] = 8 * ++slots;
                        continue;
                    }
                }
                else free.Remove(register);
                if (calleeSaved.Contains(register)) saved.Add(register);
                iv.Register = register;
                active.Add(iv);
            }
            // (PHI_IR_REGS=1: each value's interval and where it went, for finding why one wasn't in a register)
            if (Environment.GetEnvironmentVariable("PHI_IR_REGS") == "1")
                foreach (Interval iv in intervals.Values.OrderBy(i => i.Start))
                    Console.Error.WriteLine($"  {iv.Value}: {iv.Start}..{iv.End} weight {iv.Weight} {(iv.CrossesCall ? "crosses a call " : "")}{iv.Register ?? (Slots.TryGetValue(iv.Value, out int at) ? $"[{at}]" : "-")}");
            foreach (Interval iv in intervals.Values)
            {
                if (iv.Register == null) continue;
                Registers[iv.Value] = iv.Register;
                if (calleeSaved.Contains(iv.Register) && !UsedCalleeSaved.Contains(iv.Register)) UsedCalleeSaved.Add(iv.Register);
            }
            // a method that catches keeps all four for its caller: a throw comes back past methods
            // that would have put them back, so they hold whatever was in them down there; and
            // one that calls an assembly block, which may change any of them
            if (fn.Blocks.Any(b => b.End is TryT) || rawCalls.Count > 0)
                foreach (string r in calleeSaved.Where(r => !UsedCalleeSaved.Contains(r))) UsedCalleeSaved.Add(r);
            UsedCalleeSaved.Sort(string.CompareOrdinal);
            SlotCount = slots;
        }

        /// <summary>How many 8-byte slots below rbp the method's homes and the values without registers take.</summary>
        public int SlotCount { get; private set; }
    }
}

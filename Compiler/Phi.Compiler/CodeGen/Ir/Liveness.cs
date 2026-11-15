namespace Phi.Compiler.CodeGen.Ir
{
    /// <summary>Which values each block needs from before it (live in) and passes on (live out).</summary>
    public sealed class Liveness
    {
        public Dictionary<Block, HashSet<VReg>> In { get; } = new();
        public Dictionary<Block, HashSet<VReg>> Out { get; } = new();

        public static Liveness Of(IrFunction fn)
        {
            var live = new Liveness();
            var defs = new Dictionary<Block, HashSet<VReg>>();
            foreach (Block b in fn.Blocks)
            {
                var u = new HashSet<VReg>();
                var d = new HashSet<VReg>();
                foreach (Instr i in b.Instrs)
                {
                    foreach (VReg v in RegisterAllocator.Uses(i)) if (!d.Contains(v)) u.Add(v);
                    if (RegisterAllocator.Def(i) is VReg def) d.Add(def);
                }
                foreach (VReg v in RegisterAllocator.Uses(b.End)) if (!d.Contains(v)) u.Add(v);
                defs[b] = d;
                live.In[b] = u;
                live.Out[b] = new HashSet<VReg>();
            }
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int k = fn.Blocks.Count - 1; k >= 0; k--)
                {
                    Block b = fn.Blocks[k];
                    var o = live.Out[b];
                    foreach (Block s in IrBuilder.Successors(b))
                        foreach (VReg v in live.In[s])
                            if (o.Add(v)) changed = true;
                    foreach (VReg v in o)
                        if (!defs[b].Contains(v) && live.In[b].Add(v)) changed = true;
                }
            }
            return live;
        }
    }
}

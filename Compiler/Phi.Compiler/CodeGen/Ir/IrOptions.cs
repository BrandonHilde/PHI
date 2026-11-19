namespace Phi.Compiler.CodeGen.Ir
{
    /// <summary>
    /// The optimiser's steps, each of which can be left out to find which one a wrong program
    /// comes from: PHI_OPT_OFF=share,entry (or all). share: a result made in the register of an
    /// operand read for the last time; entry: registers saved after a method's first test, when
    /// one way returns at once (x64; ARM always does); consts: a test of a number known when
    /// it's built; same: what was worked out or read a moment ago used again (its parts: copies,
    /// before, gives); hoist: what a loop works out the same every time, worked out before it;
    /// tidy: ARM's test of a value against 0 as one instruction; rax: x64's value read at once
    /// kept in rax; spare: ARM's x4 and x5 for values no call comes near. PHI_OPT_FN=Heap.Free,Map does
    /// same and hoist only in the methods whose names have one of those in them.
    /// </summary>
    public static class IrOptions
    {
        static readonly HashSet<string> off = (Environment.GetEnvironmentVariable("PHI_OPT_OFF") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

        public static bool On(string step) => !off.Contains(step) && !off.Contains("all");
    }
}

using Phi.Compiler.CodeGen.Ir;

namespace Phi.Compiler.CodeGen.A64
{
    public sealed partial class A64Generator
    {
        /// <summary>ErrorKind (lib/phi/lang/error.phi): what a throw; is, and a number that doesn't fit the other kind.</summary>
        const int ErrorThrown = 0;
        const int ErrorConvert = 7;

        /// <summary>An Error's bytes (lib/phi/lang/error.phi), as x86's: what a catch copies into err.</summary>
        static int ErrorBytes => X86Generator.ErrorBytes;

        sealed partial class Emitter
        {
            /// <summary>The instructions of objects, catches and vecs (written as their milestones come); false for one that isn't.</summary>
            bool More(Instr i)
            {
                switch (i)
                {
                    case CatchI c:
                    {
                        // the generator's EmitIfThrows' record (throw.s has its fields): the catch
                        // around it, sp, x29, the methods' records, where a throw goes, how deep in
                        // the log's lock; then it's the innermost catch
                        g.Need("phi_throw");
                        g.Need("phi_log_held");
                        PlaceAddress("x9", c.Record);
                        E("ldr x10, [x28, #8]");
                        E("str x10, [x9]");
                        E("mov x10, sp");
                        E("str x10, [x9, #8]");
                        E("str x29, [x9, #16]");
                        E("ldr x10, [x28, #16]");
                        E("str x10, [x9, #24]");
                        E($"adr x10, {blockLabels[c.Caught]}");
                        E("str x10, [x9, #32]");
                        E("bl phi_log_held");
                        PlaceAddress("x9", c.Record);
                        E("str x0, [x9, #40]");
                        E("str x9, [x28, #8]");
                        return true;
                    }
                    case UncatchI u:
                        // the call came back: the catch around this one is the innermost again
                        PlaceAddress("x9", u.Record);
                        E("ldr x10, [x9]");
                        E("str x10, [x28, #8]");
                        return true;
                    case CaughtI caught:
                    {
                        // the thread's error, copied into err (nothing is in a register here)
                        PlaceAddress("x9", caught.Place);
                        E("add x10, x28, #24");
                        E($"mov x11, #{ErrorBytes}");
                        string copy = g.NewLabel("caught");
                        Label(copy);
                        E("ldrb w12, [x10], #1");
                        E("strb w12, [x9], #1");
                        E("subs x11, x11, #1");
                        E($"b.ne {copy}");
                        return true;
                    }
                }
                return false;
            }

            void VecArgument(Opnd a, int at) =>
                throw new InvalidOperationException($"{methodLabel}: a vec given to a call, which the ARM code doesn't write yet");

            void VecResult(VReg dst) =>
                throw new InvalidOperationException($"{methodLabel}: a vec given back, which the ARM code doesn't write yet");

            void VecReturn(Opnd v) =>
                throw new InvalidOperationException($"{methodLabel}: a vec given back, which the ARM code doesn't write yet");
        }
    }
}

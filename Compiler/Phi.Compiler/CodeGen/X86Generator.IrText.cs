using Phi.Compiler.CodeGen.Ir;

namespace Phi.Compiler.CodeGen
{
    // Text in the intermediate form's code: the address of zero-terminated bytes, measured,
    // compared and copied by the library's routines, as the generator does it.
    public sealed partial class X86Generator
    {
        sealed partial class IrEmitter
        {
            void Text(TextI t)
            {
                switch (t.Op)
                {
                    case TextOp.Literal:
                    {
                        string r = InRegister(t.Dst!) ? Reg(t.Dst!)! : "rax";
                        E($"lea {r}, [rel {g.StringLabel(t.Literal!)}]");
                        Store(t.Dst!, r);
                        return;
                    }
                    case TextOp.OrEmpty:
                    {
                        // (no text yet is the text with nothing in it)
                        string r = InRegister(t.Dst!) ? Reg(t.Dst!)! : "rax";
                        string has = g.NewLabel("hastext");
                        Load(r, t.A!, 64);
                        E($"test {r}, {r}");
                        E($"jnz {has}");
                        E($"lea {r}, [rel {g.StringLabel("")}]");
                        g.Label(has);
                        Store(t.Dst!, r);
                        return;
                    }
                    case TextOp.Copy:
                    {
                        g.Need("phi_strcpy");
                        Load("rsi", t.A!, 64);
                        E($"lea rdi, {Place(t.Place!)}");
                        E($"mov ecx, {t.Room}");
                        E("call phi_strcpy");
                        // (cut short: the error the generator's EmitTextFits gives)
                        string ok = g.NewLabel("textfits");
                        E($"jnc {ok}");
                        WriteBack(t.Held);
                        MarkNow();
                        g.EmitTextError();
                        g.Label(ok);
                        return;
                    }
                    case TextOp.Length:
                        g.Need("phi_strlen");
                        Load("rsi", t.A!, 64);
                        E("call phi_strlen");
                        Store(t.Dst!, "rax");
                        return;
                    case TextOp.Equal or TextOp.Order:
                    {
                        string routine = t.Op == TextOp.Equal ? "phi_strcmp" : "phi_strorder";
                        g.Need(routine);
                        // (rsi and rdi may hold the other's text: through the stack)
                        E($"push {Operand(t.B!, 64)}");
                        Load("rsi", t.A!, 64);
                        E("pop rdi");
                        E($"call {routine}");
                        Store(t.Dst!, "rax");
                        return;
                    }
                }
            }
        }
    }
}

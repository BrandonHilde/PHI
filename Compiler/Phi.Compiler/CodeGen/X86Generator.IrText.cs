using Phi.Compiler.CodeGen.Ir;

namespace Phi.Compiler.CodeGen
{
    // Text in the intermediate form's code: the address of zero-terminated bytes, measured,
    // compared and copied by the library's routines, as the generator does it.
    public sealed partial class X86Generator
    {
        sealed partial class IrEmitter
        {
            /// <summary>A number written out as text into the scratch bytes, as Print does it.</summary>
            void NumberText(NumberTextI nt)
            {
                switch (nt.Kind)
                {
                    case PrintKind.Char:
                        Load("rax", nt.Value, 32);
                        E($"mov byte {S(0)}, al");
                        E($"mov byte {S(1)}, 0");
                        break;
                    case PrintKind.Int64 or PrintKind.Uint64:
                    {
                        string convert = nt.Kind == PrintKind.Uint64 ? "phi_uint64_to_str" : "phi_int64_to_str";
                        g.Need(convert);
                        Load("rax", nt.Value, 64);
                        E("mov rdx, rax");
                        E("shr rdx, 32");
                        E($"lea rdi, {S(0)}");
                        E("mov ecx, 24");
                        E($"call {convert}");
                        break;
                    }
                    default:
                    {
                        g.Need("phi_float_to_str");
                        VReg f = nt.Value.Reg!;
                        if (InRegister(f))
                        {
                            E("sub rsp, 8");
                            E($"movsd qword [rsp], {Reg(f)}");
                            E("fld qword [rsp]");
                            E("add rsp, 8");
                        }
                        else E($"fld {F(f)}");
                        E($"lea rdi, {S(0)}");
                        E("mov ecx, 48");
                        E("call phi_float_to_str");
                        break;
                    }
                }
                string r = InRegister(nt.Dst) ? Reg(nt.Dst)! : "rax";
                E($"lea {r}, {S(0)}");
                Store(nt.Dst, r);
            }

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

using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.Ir
{
    // Text in the intermediate form, as the generator has it: a str's value is where its text
    // is (zero-terminated bytes). A method's own str with a size (a parameter, which the method
    // copies what it's given into when it starts, or a variable) is a buffer in its frame; a
    // class's is at its label; a struct's is where the field is (or, for one that grows, where
    // its slot says). Text that grows on the heap, and building text, are still the generator's.
    public sealed partial class IrBuilder
    {
        /// <summary>A method's own str with a size, kept in its frame: how far below rbp its bytes start.</summary>
        readonly Dictionary<VariableSymbol, int> frameTexts = new();

        /// <summary>A str this form keeps in the method's frame: a parameter (not the caller's own, str into) or a variable, with a size.</summary>
        static bool FrameText(VariableSymbol v) =>
            v.Type.IsString && !v.Type.IsArray && !v.Grows && !v.TextRef && v.Capacity > 0 && !v.Type.IsInlineText;

        string TextPlace(VariableSymbol v) => $"rbp - {frameTexts[v]}";

        /// <summary>
        /// A str parameter with no default (str target;) that the method only reads: the caller's
        /// own text, through the address of its slot (where the text is, then its room).
        /// </summary>
        static bool CallersText(VariableSymbol p) => p.IsParameter && p.TextRef && !p.Written && p.Type.IsString && !p.Type.IsArray;

        /// <summary>Each such parameter's value: the address of the caller's slot.</summary>
        readonly Dictionary<VariableSymbol, VReg> callersTexts = new();

        /// <summary>Its buffer in the frame (the parameters' and variables' together, before anything else is built).</summary>
        void PlaceFrameTexts()
        {
            foreach (VariableSymbol v in method.Parameters.Concat(method.Locals).Where(FrameText))
            {
                for (int k = 0; k < (v.Capacity + 7) / 8; k++) fn.NewHome();
                frameTexts[v] = 8 * fn.Homes;
            }
        }

        /// <summary>True when <see cref="TextAddress"/> takes the text (asking only, with nothing built).</summary>
        bool CanText(Expr e) => e switch
        {
            StringExpr => true,
            NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v) || callersTexts.ContainsKey(v) => true,
            NameExpr { Symbol: VariableSymbol { TextRef: false } g } when !Ours(g) && !g.Type.IsArray && SimpleLabel(g.Label) => true,
            _ when e.Type.IsInlineText || e.Type.IsHeapText => CanAddress(e),
            _ => false,
        };

        /// <summary>Where a text is: written in the program, a method's own, a class's, a struct's field ('' for one that grows and has none yet).</summary>
        VReg TextAddress(Expr e)
        {
            VReg d = fn.NewValue(true);
            switch (e)
            {
                case StringExpr s:
                    Emit(new TextI { Op = TextOp.Literal, Dst = d, Literal = s.Value });
                    return d;
                case NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v):
                    Emit(new AddrI { Dst = d, Label = TextPlace(v) });
                    return d;
                case NameExpr { Symbol: VariableSymbol caller } when callersTexts.TryGetValue(caller, out VReg? slot):
                {
                    // (the caller's text: where its slot says, '' while it has none)
                    VReg at = LoadAt(slot, 0, PhiType.PointerTo(PhiType.U8));
                    Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = at });
                    return d;
                }
                case NameExpr { Symbol: VariableSymbol g } when g.Grows:
                {
                    VReg at = fn.NewValue(true);
                    Emit(new LoadI { Dst = at, Place = g.Label, Kind = MemKind.Q64 });
                    Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = at });
                    return d;
                }
                case NameExpr { Symbol: VariableSymbol g }:
                    Emit(new AddrI { Dst = d, Label = g.Label });
                    return d;
            }
            if (e.Type.IsInlineText) return AddressOf(e);
            if (e.Type.IsHeapText)
            {
                VReg slot = LoadAt(AddressOf(e), 0, PhiType.PointerTo(PhiType.U8));
                Emit(new TextI { Op = TextOp.OrEmpty, Dst = d, A = slot });
                return d;
            }
            throw new NotYet($"text that's {Unknown(e)}");
        }

        /// <summary>A text's value, for where text goes: its address (a number written out as text is the generator's).</summary>
        Opnd TextValue(Expr e)
        {
            Need(e.Type.IsString, $"a {e.Type} written out as text");
            Need(CanText(e), $"text that's {Unknown(e)}");
            return TextAddress(e);
        }

        /// <summary>
        /// Two texts compared: the right side's address first, unless the left calls something
        /// (as the generator's EmitTextPair); 0 when they're the same text (is, is not), or -1, 0
        /// or 1 for their order.
        /// </summary>
        (VReg Result, Cond Cond) CompareTexts(BinaryExpr cmp)
        {
            VReg left, right;
            if (EvaluationOrder.HasEffect(cmp.Left))
            {
                left = (VReg)TextValue(cmp.Left).Reg!;
                right = (VReg)TextValue(cmp.Right).Reg!;
            }
            else
            {
                right = (VReg)TextValue(cmp.Right).Reg!;
                left = (VReg)TextValue(cmp.Left).Reg!;
            }
            bool order = cmp.Op is not (BinaryOp.Equal or BinaryOp.NotEqual);
            VReg d = fn.NewValue(false);
            Emit(new TextI { Op = order ? TextOp.Order : TextOp.Equal, Dst = d, A = left, B = right });
            Cond c = cmp.Op switch
            {
                BinaryOp.Equal => Cond.Eq,
                BinaryOp.NotEqual => Cond.Ne,
                BinaryOp.Less => Cond.LtS,
                BinaryOp.LessEqual => Cond.LeS,
                BinaryOp.Greater => Cond.GtS,
                _ => Cond.GeS,
            };
            return (d, c);
        }

        static bool TextCompare(BinaryExpr b) => IsComparison(b.Op) && b.Left.Type.IsString && b.Right.Type.IsString && !b.Left.Type.IsArray && !b.Right.Type.IsArray;

        /// <summary>How long a text is, measured.</summary>
        VReg TextLength(Expr text)
        {
            VReg at = (VReg)TextValue(text).Reg!;
            VReg d = fn.NewValue(false);
            Emit(new TextI { Op = TextOp.Length, Dst = d, A = at });
            return d;
        }

        /// <summary>A str of the method's own, or a class's with a size, given a text: copied in, too long an error.</summary>
        bool SetText(Expr target, Expr value)
        {
            string? place = target switch
            {
                NameExpr { Symbol: VariableSymbol v } when frameTexts.ContainsKey(v) => TextPlace(v),
                NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false } g } when !Ours(g) && !g.Type.IsArray && g.Capacity > 0 && SimpleLabel(g.Label) => g.Label,
                _ => null,
            };
            if (place == null || !value.Type.IsString || !CanText(value)) return false;
            int room = target is NameExpr { Symbol: VariableSymbol t } ? t.Capacity : 0;
            VReg from = TextAddress(value);
            Emit(new TextI { Op = TextOp.Copy, A = from, Place = place, Room = room, Held = HeldStores() });
            return true;
        }
    }
}

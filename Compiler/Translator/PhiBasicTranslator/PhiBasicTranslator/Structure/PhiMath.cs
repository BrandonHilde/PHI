using PhiBasicTranslator.ParseEngine;

namespace PhiBasicTranslator.Structure
{
    public class PhiMath
    {
        public enum Opperation
        {
            None,
            PlusEquals,
            MinusEquals,
            Plus,
            Minus,
            MultiplyEquals,
            Multiply,
            DivideEquals,
            Divide,
            ModEquals,
            Mod
        }
        public string RawValue { get; set; } = string.Empty;
        public long Order { get; set; } = 0;
        public MathPair Math { get; set; }

        public PhiMath Copy()
        {
            return new PhiMath
            {
                Math = Math.Copy(),
                RawValue = RawValue,
                Order = Order
            };
        }

        public static MathPair Parse(string value, long depth = -1)
        {
            MathPair mth = new MathPair();

            string val1 = "";
            string val2 = "";
            string math = "";

            for (int i = 0; i < value.Length; i++)
            {
                string cut = value.Substring(i);

                string match = ParseUtilities.MatchesMath(cut);

                if(match != string.Empty)
                {
                    math = match;

                    val1 = value.Substring(0, i).Trim();
                    val2 = value.Substring(i + match.Length).Trim();

                    Defs.RawNumberCut.ToList().ForEach(
                        x => val1 = val1.Replace(x.ToString(), "")
                        );

                    Defs.RawNumberCut.ToList().ForEach(
                     x => val2 = val2.Replace(x.ToString(), "")
                     );

                    mth = new MathPair(val1, val2, math, depth);   

                    break;
                }
            }

            return mth;
        }
    }

    public class MathPair
    {
        public string ValueLeft { get; set; } = string.Empty;
        public string ValueRight { get; set; } = string.Empty;
        public string MathOp { get; set; } = string.Empty;
        public long Depth { get; set; } = -1;

        public MathPair()
        {

        }

        public MathPair Copy()
        {
            return new MathPair(ValueLeft, ValueRight, MathOp, Depth);
        }
        public MathPair(string valueLeft, string valueRight, string mathOp)
        {
            ValueRight = valueRight;
            ValueLeft = valueLeft;
            MathOp = mathOp;
        }

        public MathPair(string valueLeft, string valueRight, string mathOp, long depth)
        {
            ValueRight = valueRight;
            ValueLeft = valueLeft;
            MathOp = mathOp;
            Depth = depth;
        }
    }
}

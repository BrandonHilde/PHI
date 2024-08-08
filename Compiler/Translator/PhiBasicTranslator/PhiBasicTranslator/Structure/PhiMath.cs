using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class PhiMath
    {
        public string RawValue { get; set; } = string.Empty;

        public MathPair Math { get; set; }  

        public static MathPair Parse(string value)
        {
            MathPair mth = new MathPair();

            string val1 = "";
            string val2 = "";
            string math = "";

            for (int i = 0; i < value.Length; i++)
            {
                string cut = value.Substring(i);

                string match = MatchesMath(cut);

                if(match != string.Empty)
                {
                    math = match;

                    val1 = value.Substring(0, i).Trim();
                    val2 = value.Substring(i + match.Length).Trim();

                    mth = new MathPair(val1, val2, math);   

                    break;
                }
            }

            return mth;
        }

        private static string MatchesMath(string value)
        {
            string str = string.Empty;

            //later matches will override previous ones
            foreach (string m in Defs.MathSmallOpsList)
            {
                if(value.StartsWith(m))
                {
                    str = m;
                }
            }

            foreach (string m in Defs.MathOpsList)
            {
                if (value.StartsWith(m))
                {
                    str = m;
                }
            }

            return str;
        }
    }

    public class MathPair
    {
        public string ValueOne { get; set; } = string.Empty;
        public string ValueTwo { get; set; } = string.Empty;
        public string MathOp { get; set; } = string.Empty;

        public MathPair()
        {

        }
        public MathPair(string valueOne, string valueTwo, string mathOp)
        {
            ValueOne = valueOne;
            ValueTwo = valueTwo;
            MathOp = mathOp;
        }
    }
}

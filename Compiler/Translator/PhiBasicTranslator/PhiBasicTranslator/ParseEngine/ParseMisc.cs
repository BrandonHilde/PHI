using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.ParseEngine
{
    public class ParseMisc
    {

        public static bool IsNumber(string str)
        {
            double d = 0;

            return double.TryParse(str, out d);
        }
        public static bool StartsWithAppendedValue(string content, string value, List<string> appendedValues)
        {
            bool result = false;

            foreach (string appendedValue in appendedValues)
            {
                if(content.StartsWith(value + appendedValue)) return true;
            }

            return result;
        }

        public static bool StartsWithAppendedValue(string content, string value, List<char> appendedValues)
        {
            bool result = false;

            foreach (char append in appendedValues)
            {
                if (content.StartsWith(value + append)) return true;
            }

            return result;
        }

        public static bool ContainsAny(string content, List<string> values)
        {
            foreach (string value in values)
            {
                if(content.Contains(value)) return true;
            }

            return false;   
        }

        public static bool IsElsePair(PhiClass cls, PhiInstruct instruct)
        {
            PhiInstruct? inst = cls.Instructs.LastOrDefault();

            if (inst != null)
            {
                if(inst.Name == Defs.instIf && instruct.Name == Defs.instElse)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsElsePair(PhiMethod mthd, PhiInstruct instruct)
        {
            PhiInstruct? inst = mthd.Instructs.LastOrDefault();

            if (inst != null)
            {
                if (inst.Name == Defs.instIf && instruct.Name == Defs.instElse)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsElsePair(PhiInstruct parentInst, PhiInstruct instruct)
        {
            PhiInstruct? inst = parentInst.Instructs.LastOrDefault();

            if (inst != null)
            {
                if (inst.Name == Defs.instIf && instruct.Name == Defs.instElse)
                {
                    return true;
                }
            }

            return false;
        }

        public static int GetPrevIndexOfAny(string Content, string Match, int Start)
        {
            for (int i = Start; i >= 0; i--)
            {
                if (Match.Contains(Content[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int GetNextIndexOfAny(string Content, string Match, int Start)
        {
            for (int i = Start; i < Content.Length; i++)
            {
                if (Match.Contains(Content[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public static string ClearMethodName(string methodName)
        {
            methodName = methodName.Replace('.', '_');
            methodName = ParseUtilities.ClearLabel(methodName, Defs.Alphabet);

            return methodName;
        }
    }
}

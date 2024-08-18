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

        public static string ClearMethodName(string methodName)
        {
            methodName = methodName.Replace('.', '_');
            methodName = ParseUtilities.ClearLabel(methodName, Defs.Alphabet);

            return methodName;
        }
    }
}

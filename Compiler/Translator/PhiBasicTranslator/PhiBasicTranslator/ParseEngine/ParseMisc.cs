using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.ParseEngine
{
    public class ParseMisc
    {
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
    }
}

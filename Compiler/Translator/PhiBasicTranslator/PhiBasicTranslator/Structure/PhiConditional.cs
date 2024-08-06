using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class PhiConditional
    {
        public string RawValue { get; set; } = string.Empty;
        public List<ConditionalPairs> PhiConditionals { get; set; } = new List<ConditionalPairs>();
    }
}

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
        public List<ConditionalPairs> PhiConditionalPairs { get; set; } = new List<ConditionalPairs>();

        public PhiConditional Copy()
        {
            List<ConditionalPairs> conds = new List<ConditionalPairs>();

            foreach (ConditionalPairs pair in PhiConditionalPairs) 
                conds.Add(pair.Copy());

            return new PhiConditional
            {
                PhiConditionalPairs = conds,
                RawValue = RawValue,
            };
        }
    }
}

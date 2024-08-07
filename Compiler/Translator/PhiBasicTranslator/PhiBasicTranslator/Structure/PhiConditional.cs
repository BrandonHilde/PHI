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

        public PhiConditional Copy()
        {
            List<ConditionalPairs> conds = new List<ConditionalPairs>();

            foreach (ConditionalPairs pair in PhiConditionals) 
                conds.Add(pair.Copy());

            return new PhiConditional
            {
                PhiConditionals = conds,
                RawValue = RawValue,
            };
        }
    }
}

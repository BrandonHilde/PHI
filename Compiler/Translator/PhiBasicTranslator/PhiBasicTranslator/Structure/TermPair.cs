using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class TermPair
    {
        public string Name { get; set; }
        public string Value { get; set; }

        public bool PreviouslySet = false;
        public List<string> Equivalent { get; set; }

        public List<TermPair> SubTerms { get; set; } = new List<TermPair>();
    }

    public class BuildPair
    {
        public string Label { get; set; } = string.Empty;
        public List<string> CodeBase { get; set; } = new List<string>();
        public List<string> CoreCode { get; set; } = new List<string>();
        public List<string> SubCode { get; set; } = new List<string>();

        public BuildPair Copy()
        {
            return new BuildPair      
            { 
                Label = Label,
                CoreCode = CoreCode, 
                SubCode = SubCode,
                CodeBase = CodeBase
            };
        }
    }
}

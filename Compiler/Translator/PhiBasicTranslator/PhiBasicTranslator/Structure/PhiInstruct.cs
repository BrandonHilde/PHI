using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class PhiInstruct
    {
        public string Name = string.Empty;
        public string Value = string.Empty;
        public string Content = string.Empty;
        public List<Inside> ContentLabels { get; set; } = new List<Inside>();
        public Inside InType = Inside.VariableTypeMixed;

        public List<PhiVariable> Variables = new List<PhiVariable>();
        public List<PhiInstruct> Instructs = new List<PhiInstruct>();
        public List<PhiConditional> Conditionals = new List<PhiConditional>();
        public List<PhiMath> Maths = new List<PhiMath>();

        public List<BuildPair> BuildPairs = new List<BuildPair>();

        public PhiInstruct()
        {

        }

        public List<PhiInstruct> GetSubInstructs()
        {
            List<PhiInstruct> insts = Instructs;

            foreach(PhiInstruct inst in Instructs)
            {
                insts.AddRange(inst.GetSubInstructs());
            }

            return insts;
        }

        public PhiInstruct Copy()
        {
            List<PhiVariable> vars = new List<PhiVariable>();

            foreach (PhiVariable v in Variables) vars.Add(v.Copy());

            List<PhiInstruct> insts = new List<PhiInstruct>();

            foreach (PhiInstruct nst in Instructs) insts.Add(nst.Copy());

            List<PhiConditional> cond = new List<PhiConditional>();

            foreach (PhiConditional cnd in Conditionals) cond.Add(cnd.Copy());

            List<PhiMath> maths = new List<PhiMath>();

            foreach (PhiMath mt in Maths) maths.Add(mt.Copy());

            List<BuildPair> build = new List<BuildPair>();

            foreach (BuildPair bp in BuildPairs) build.Add(bp.Copy());
            //

            //maybe unecessary 
            List<Inside> labels = new List<Inside>();
            foreach (Inside i in ContentLabels) labels.Add(i);

            return new PhiInstruct
            {
                Name = Name,
                Value = Value,
                Content = Content,
                InType = InType,
                Variables = vars,
                Instructs = insts,
                ContentLabels = labels,
                Conditionals = cond,
                Maths = maths,
                BuildPairs = build
            };
        }
    }
}

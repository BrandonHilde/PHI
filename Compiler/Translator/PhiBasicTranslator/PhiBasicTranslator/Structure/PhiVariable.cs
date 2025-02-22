using PhiBasicTranslator.GeneralUtilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class PhiVariable
    {
        public PhiVariable() { }

        public Inside varType = Inside.VariableTypeVar;
        public string Name = string.Empty;
        public string ValueRaw = string.Empty;
        public bool preExisting = true;
        public bool pointer = false;
        public List<string> Values { get; set; } = new List<string>();

        public List<PhiVariable> SubVariables { get; set; } = new List<PhiVariable>();

        public PhiVariable Copy()
        {
            List<PhiVariable> vars = new List<PhiVariable>();

            foreach (var v in SubVariables) vars.Add(v.Copy());

            return new PhiVariable
            {
                Name = Name,
                varType = varType,
                ValueRaw = ValueRaw,
                Values = FileManager.CopyList(Values),
                SubVariables = vars,
                preExisting = preExisting
            };
        }
    }
}

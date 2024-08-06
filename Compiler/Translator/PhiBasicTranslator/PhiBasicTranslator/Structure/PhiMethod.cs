using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PhiBasicTranslator.Structure
{
    public class PhiMethod
    {
        public string Name = string.Empty;
        public string End = string.Empty;
        public string Content = string.Empty;
        [JsonIgnore]
        public string Remainer = string.Empty;

        public PhiMethod() { }

        public List<PhiVariable> Variables = new List<PhiVariable>();
        public List<PhiInstruct> Instructs { get; set; } = new List<PhiInstruct>();

        public PhiMethod Copy()
        {
            List<PhiVariable> vars = new List<PhiVariable>();

            foreach (var v in Variables) vars.Add(v.Copy());

            List<PhiInstruct> insts = new List<PhiInstruct>();

            foreach (PhiInstruct nst in Instructs) insts.Add(nst.Copy());

            return new PhiMethod
            {
                Name = Name,
                End = End,
                Content = Content,
                Remainer = Remainer,
                Variables = vars,
                Instructs = insts
            };
        }
    }
}

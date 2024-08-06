using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Newtonsoft;
using PhiBasicTranslator.GeneralUtilities;

namespace PhiBasicTranslator.Structure
{

    public class PhiCodebase
    {
        public PhiCodebase() { }

        public List<PhiClass> ClassList { get; set; } = new List<PhiClass>();

        public void Save(string File)
        {
            FileManager.SaveToFile(File, this);
        }

        public static PhiCodebase Load(string File)
        {
            return FileManager.LoadFromFile<PhiCodebase>(File);
        }

        public PhiCodebase Copy()
        {
            List<PhiClass> clst = new List<PhiClass>();

            foreach (PhiClass cl in ClassList) clst.Add(cl.Copy());

            return new PhiCodebase
            {
                ClassList = clst
            };
        }
    }

    public enum PhiType { ASM, ARM, PHI }
    public class PhiClass
    {
        public PhiType Type { get; set; } = PhiType.PHI;
        public string Name { get; set; } = string.Empty;
        public string Inherit { get; set; } = string.Empty;
       
        public string RawContent { get; set; } = string.Empty;

        public PhiClass() { }
        public List<PhiMethod> Methods { get; set; } = new List<PhiMethod>();
        public List<PhiVariable> Variables { get; set; } = new List<PhiVariable>();
        public List<PhiInstruct> Instructs { get; set; } = new List<PhiInstruct>();

        public PhiClass Copy()
        {
            List<PhiVariable> vars = new List<PhiVariable>();

            foreach (PhiVariable v in Variables) vars.Add(v.Copy());

            List<PhiMethod> mthds = new List<PhiMethod>();

            foreach (PhiMethod mth in Methods) mthds.Add(mth.Copy());

            List<PhiInstruct> insts = new List<PhiInstruct>();

            foreach (PhiInstruct nst in Instructs) insts.Add(nst.Copy());

            return new PhiClass
            {
                Type = Type,
                Name = Name,
                Inherit = Inherit,
                RawContent = RawContent,
                Methods = mthds,
                Variables = vars,
                Instructs = insts
            };
        }
    }



   
}

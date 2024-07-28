using System;
using System.Collections.Generic;
using System.Linq;
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
        public List<PhiVariables> Variables { get; set; } = new List<PhiVariables>();
        public List<PhiInstruct> Instructs { get; set; } = new List<PhiInstruct>();

        public PhiClass Copy()
        {
            List<PhiVariables> vars = new List<PhiVariables>();

            foreach (PhiVariables v in Variables) vars.Add(v.Copy());

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

    public class PhiMethod
    {
        public string Name = string.Empty;
        public string End = string.Empty;
        public string Content = string.Empty;
        [JsonIgnore]
        public string Remainer = string.Empty;

        public PhiMethod() { }

        public List<PhiVariables> Variables = new List<PhiVariables>();

        public PhiMethod Copy()
        {
            List<PhiVariables> vars = new List<PhiVariables>();

            foreach (var v in Variables) vars.Add(v.Copy());

            return new PhiMethod
            {
                Name = Name,
                End = End,
                Content = Content,
                Remainer = Remainer,
                Variables = vars
            };
        }
    }

    public class PhiVariables
    {
        public PhiVariables() { }

        public Inside varType = Inside.VariableTypeVar;
        public string Name = string.Empty;
        public string ValueRaw = string.Empty;
        public List<string> Values { get; set; } = new List<string>();

        public List<PhiVariables> SubVariables { get; set; } = new List<PhiVariables>();

        public PhiVariables Copy()
        {
            List<PhiVariables> vars = new List<PhiVariables>();

            foreach (var v in SubVariables) vars.Add(v.Copy());

            return new PhiVariables
            {
                Name = Name,
                varType = varType,
                ValueRaw = ValueRaw,
                Values = FileManager.CopyList(Values),
                SubVariables = vars
            };
        }
    }

    public class PhiInstruct
    {
        public string Name = string.Empty;
        public string Value = string.Empty;
        public string Content = string.Empty;
        public Inside InType = Inside.VariableTypeMixed;

        public List<PhiVariables> SubVariables = new List<PhiVariables>();  

        public PhiInstruct()
        {

        }

        public PhiInstruct Copy()
        {
            List<PhiVariables> vars = new List<PhiVariables>();

            foreach (var v in SubVariables) vars.Add(v.Copy());

            return new PhiInstruct
            {
                Name = Name,
                Value = Value,
                Content = Content,
                InType = InType,
                SubVariables = vars
            };
        }
    }
}

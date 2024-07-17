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

        public List<PhiClass> ClassList { get; set; }

        public void Save(string File)
        {
            FileManager.SaveToFile<PhiCodebase>(File, this);
        }

        public static PhiCodebase Load(string File)
        {
            return FileManager.LoadFromFile<PhiCodebase>(File);
        }
    }
    public class PhiClass
    {
        public PhiClass() { }   
        public string Name { get; set; } = string.Empty;
        public string Inherit { get; set; } = string.Empty;

        public string RawContent { get; set; } = string.Empty;
        public List<PhiMethod> Methods { get; set; }
        public List<PhiVariables> Variables { get; set; }
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
    }

    public class PhiVariables
    {
        public PhiVariables() { }

        public string Name = string.Empty;
        public string ValueRaw = string.Empty;
        public List<string> Values { get; set; } = new List<string>();

        public List<PhiVariables> SubVariables { get; set; }
    }
}

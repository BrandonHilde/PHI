using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class PhiClass
    {
        public string Name { get; set; } = string.Empty;
        public string Inherit { get; set; } = string.Empty;
        public List<PhiMethod> Methods { get; set; }
        public List<PhiVariables> Variables { get; set; }
    }

    public class PhiMethod
    {
        public string Name = string.Empty;
        public string End = string.Empty;
        public string Content = string.Empty;

        public List<PhiVariables> Variables = new List<PhiVariables>();
    }

    public class PhiVariables
    {
        public string Name = string.Empty;
        public string ValueRaw = string.Empty;
        public List<string> Values { get; set; } = new List<string>();

        public List<PhiVariables> SubVariables { get; set; }
    }
}

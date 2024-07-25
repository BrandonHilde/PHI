using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PhiBasicTranslator;
using PhiBasicTranslator.Structure;

namespace PhiBasicTranslator.TranslateUtilities
{
    public class TranslateToX86
    {
        public static List<string> ToX86(PhiCodebase code)
        {
            List<string> ASM = new List<string>();

            for(int i = 0; i < code.ClassList.Count; i++)
            {
                PhiClass cls = code.ClassList[i];

                if(cls.Inherit == Defs.os16bit)
                {
                    ASM.AddRange(ASMx86_16BIT.GetInheritance(ASMx86_16BIT.InheritType.BITS16));
                }

                List<string> values = new List<string>();

                foreach(PhiInstruct inst in cls.Instructs)
                {
                    if(inst.Name == Defs.instLog)
                    {
                        // remember to add includes check to prevent duplicates
                        ASM = ASMx86_16BIT.InsertCodeLines(ASM, ASMx86_16BIT.InstructLogBITS16, 0);

                        values.Clear();

                        PhiVariables val = ConvertValue(inst.Value, cls);

                        values.Add(val.Name + val.ValueRaw);

                        ASM = ASMx86_16BIT.InsertVars(ASM, values);
                        ASM = ASMx86_16BIT.InsertValues(ASM, new List<string> { val.Name });
                    }
                }
            }

            return ASM;
        }

        public static PhiVariables ConvertValue(string value, PhiClass cls, Inside ValueType = Inside.VariableTypeStr, int vcount = 0)
        {
            if(ValueType == Inside.VariableTypeStr)
            {
                if (value[value.Length - 1].ToString() == Defs.VariableSetClosure)
                {
                    value = value.Substring(0, value.Length - 1);
                }
                else
                {

                }

                return new PhiVariables 
                { 
                    Name = "VALUE_" + vcount, 
                    ValueRaw = " db " + value + ",0" 
                };
            }

            return new PhiVariables();
        }
    }
}

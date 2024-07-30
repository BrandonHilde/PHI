using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PhiBasicTranslator;
using PhiBasicTranslator.ParseEngine;
using PhiBasicTranslator.Structure;

namespace PhiBasicTranslator.TranslateUtilities
{
    public class TranslateToX86
    {
        public static List<string> ToX86(PhiCodebase code)
        {
            List<string> ASM = new List<string>();

            int varStart = 0;

            for(int i = 0; i < code.ClassList.Count; i++)
            {
                PhiClass cls = code.ClassList[i];

                if(cls.Inherit == Defs.os16bit)
                {
                    ASM.AddRange(ASMx86_16BIT.GetInheritance(ASMx86_16BIT.InheritType.BITS16));
                    ASM = AutoInclude_BITS16(ASM);
                }

                List<string> values = ConvertVarsToASM(cls.Variables);

                ASM = ASMx86_16BIT.MergeValues(ASM, values, Defs.replaceVarStart);

                foreach (PhiInstruct inst in cls.Instructs)
                {
                    if(inst.Name == Defs.instLog)
                    {
                        BuildInstructLog(inst, cls, ASM, varStart);
                    }
                    else if(inst.Name == Defs.instWhile)
                    {
                        BuildInstructWhile(inst, cls, ASM, varStart);
                    }
                }
            }

            return ASM;
        }

        public static List<string> BuildInstructWhile(PhiInstruct instruct, PhiClass cls, List<string> Code, int varStart = 0)
        {

            return Code;
        }

        public static List<string> BuildInstructLog(PhiInstruct instruct, PhiClass cls, List<string> Code, int varStart = 0)
        {
            // remember to add includes check to prevent duplicates

            List<string> values = new List<string>();

            //List<PhiVariables> val = ConvertValue(inst.Value, cls, inst.InType);

            List<PhiVariables> vrs = ParseVariables.GetInstructSubVariables(instruct, cls.Variables, varStart);

            varStart += vrs.Count;

            foreach (PhiVariables v in vrs)
            {
                if (v.varType == Inside.VariableTypeStr)
                {
                    // adds function call code
                    Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.InstructLogString_BITS16, Defs.replaceCodeStart);
                }
                else if (v.varType == Inside.VariableTypeInt)
                {
                    // adds function call code
                    Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.InstructLogInt_BITS16, Defs.replaceCodeStart);
                }

                if (!v.preExisting) values.Add(ASMx86_16BIT.VarTypeConvert(v));

                Code = ASMx86_16BIT.ReplaceValue(Code, Defs.replaceValueStart, v.Name);
            }

            Code = ASMx86_16BIT.MergeValues(Code, values, Defs.replaceVarStart);

            return Code;
        }

        public static List<string> AutoInclude_BITS16(List<string> Code)
        {
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.PrintInt_x86BITS16, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.PrintLog_x86BITS16, Defs.replaceIncludes);

            return Code;
        }

        public static List<string> ConvertVarsToASM(List<PhiVariables> phiVariables)
        {
            List<string> vals = new List<string>();

            foreach (PhiVariables vbl in phiVariables)
            {
                string build = vbl.Name;

                if(vbl.varType == Inside.VariableTypeStr)
                {
                    build = ASMx86_16BIT.VarTypeConvert(vbl);
                }
                else if(vbl.varType == Inside.VariableTypeInt)
                {
                    build += " dd ";
                    build += vbl.ValueRaw;
                }

                vals.Add(build);
            }

            return vals;
        }
        public static List<PhiVariables> ConvertValue(string value, PhiClass cls, Inside ValueType = Inside.VariableTypeStr, int vcount = 0)
        {
            List<PhiVariables> varbls = new List<PhiVariables>();

            if(ValueType == Inside.String)
            {
                if (value[value.Length - 1].ToString() == Defs.VariableSetClosure)
                {
                    value = value.Substring(0, value.Length - 1);
                }
                else
                {

                }

                varbls.Add(new PhiVariables 
                { 
                    Name = "VALUE_" + vcount, 
                    ValueRaw = " db " + value + ",0" ,
                    varType = Inside.VariableTypeStr
                });
            }
            else if(ValueType == Inside.VariableTypeInt)
            {

            }
            else if(ValueType == Inside.VariableTypeMixed)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    string cut = value.Substring(i);

                    for (int j = 0; j < cls.Variables.Count; j++)
                    {
                        // " " or ";" determines if its actually a
                        // name or just a part of another variable
                        if (cut.StartsWith(cls.Variables[j].Name + " ")
                         || cut.StartsWith(cls.Variables[j].Name + Defs.VariableSetClosure)) 
                        {
                            varbls.Add(new PhiVariables
                            {
                                Name = cls.Variables[j].Name,
                                varType= cls.Variables[j].varType
                            });
                        }
                    }
                }
            }

            return varbls;
        }
    }
}

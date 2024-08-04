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
        public static int VarCount = 0;
        public static int LoopCount = 0;
        public static List<string> ToX86(PhiCodebase code)
        {
            List<string> ASM = new List<string>();

            for(int i = 0; i < code.ClassList.Count; i++)
            {
                PhiClass cls = code.ClassList[i];

                if(cls.Inherit == Defs.os16bit)
                {
                    ASM.AddRange(ASMx86_16BIT.GetInheritance(ASMx86_16BIT.InheritType.BITS16));
                    ASM = AutoInclude_BITS16(ASM);
                }

                List<string> values = ConvertAllVariables(cls);

                ASM = ASMx86_16BIT.MergeValues(ASM, values, Defs.replaceVarStart);

                ASM = BuildAllInstructs(ASM, cls);
            }

            return ASM;
        }

        public static List<string> BuildAllInstructs(List<string> Code, PhiClass cls)
        {
            foreach (PhiInstruct inst in cls.Instructs)
            { 
                Code = BuildSubInstructs(Code, inst, cls);
            }

            return Code;
        }

        public static List<string> BuildSubInstructs(List<string> Code, PhiInstruct instrct, PhiClass cls)
        {
            if (instrct.Name == Defs.instLog)
            {
                Code = BuildInstructLog(instrct, cls, Code);
            }
            else if (instrct.Name == Defs.instWhile)
            {
                List<string> values = ConvertVarsToASM(instrct.Variables);

                Code = ASMx86_16BIT.MergeValues(Code, values, Defs.replaceVarStart);

                List<string> buildcode = BuildInstructWhile(instrct, cls);

                Code = ASMx86_16BIT.MergeValues(Code, buildcode, Defs.replaceIncludes);
            }
            else if(instrct.Name == Defs.instIf)
            {

            }
            else if(instrct.Name == Defs.instElse)
            {

            }

            foreach(PhiInstruct inst in instrct.Instructs)
            {
                Code = BuildSubInstructs(Code, inst, cls);
            }

            return Code;
        }

        public static List<string> ConvertAllVariables(PhiClass cls)
        {
            List<string> values = ConvertVarsToASM(UpdateUnsetVars(cls.Variables));

            foreach (PhiMethod methods in cls.Methods)
            {
                values.AddRange(ConvertVarsToASM(UpdateUnsetVars(methods.Variables)));
            }

            foreach(PhiInstruct inst in cls.Instructs)
            {
                values.AddRange(ConvertAllSubVariables(inst));
            }

            return values;
        }

        public static List<string> ConvertAllSubVariables(PhiInstruct inst)
        {
            List<string> values = ConvertVarsToASM(UpdateUnsetVars(inst.Variables));

            foreach(PhiInstruct instruct in inst.Instructs)
            {
                values.AddRange(ConvertAllSubVariables(instruct));
            }

            return values;
        }

        public static List<PhiVariables> UpdateUnsetVars(List<PhiVariables> varbles)
        {
            foreach (PhiVariables vbl in varbles)
            {
                if (vbl.Name == Defs.replaceUnsetName)
                {
                    vbl.Name = ASMx86_16BIT.UpdateName((VarCount++).ToString());
                    
                    if(vbl.varType == Inside.StandAloneInt)
                    {
                        vbl.varType = Inside.VariableTypeInt;
                    }
                }
            }

            return varbles;
        }

        public static List<string> BuildInstructWhile(PhiInstruct instruct, PhiClass cls)
        {            
            List<string> loopStart = new List<string>();

            List<string> values = instruct.Variables.Select(x => x.Name).ToList();

            for (int i = 0; i < values.Count; i++)
            {
                values[i] = ASMx86_16BIT.UpdateName(values[i]);
            }

            //remember to check for different while loop formats and math example: (6 + (4/i))
            if (values.Count == 2)
            {
                string name = "WHILE_" + (LoopCount++);

                string vStart = values.FirstOrDefault() ?? string.Empty;
                string vLimit = values.LastOrDefault() ?? string.Empty;

                #region Loop Construction
                loopStart = ASMx86_16BIT.ReplaceValue(
                    ASMx86_16BIT.InstructWhileStart_BITS16,
                    Defs.replaceValueStart,
                    vStart
                );

                loopStart.AddRange(ASMx86_16BIT.InstructWhileCheck_BITS16);

                #region LOOP CHECK

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    Defs.replaceValueStart,
                    vStart
                );

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    ASMx86_16BIT.replaceLoopLimit,
                    vLimit
                );

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    ASMx86_16BIT.replaceLoopCondition,
                    ASMx86_16BIT.jumpIfGreaterThan
                );

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    ASMx86_16BIT.replaceLoopIncrement,
                    ASMx86_16BIT.loopSubIncrementByOne
                );

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    ASMx86_16BIT.replaceLoopContentName,
                    name
                );

                #endregion

                loopStart.AddRange(ASMx86_16BIT.InstructWhileContent_BITS16);

                loopStart.AddRange(ASMx86_16BIT.InstructWhileDone_BITS16);

                #region LOOP END

                loopStart = ASMx86_16BIT.ReplaceValue(
                   loopStart,
                   ASMx86_16BIT.replaceLoopContentName,
                   name
                );

                // loop content gets added after sub-instructs are built

                loopStart = ASMx86_16BIT.ReplaceValue(
                   loopStart,
                   Defs.replaceValueStart,
                   vStart
                );

                #endregion
                //Code = ASMx86_16BIT.MergeValues(
                //    Code,
                //    ASMx86_16BIT.InstructWhileStart_BITS16,
                //    Defs.replaceIncludes
                //);

                #endregion
            }

            return loopStart;
        }

        public static List<string> BuildInstructLog(PhiInstruct instruct, PhiClass cls, List<string> Code)
        {
            // remember to add includes check to prevent duplicates

            List<string> values = new List<string>();

            List<PhiVariables> vrs = ParseVariables.GetInstructSubVariables(instruct, cls.Variables);

            VarCount += vrs.Count;

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

                Code = ASMx86_16BIT.ReplaceValue(Code, Defs.replaceValueStart, ASMx86_16BIT.UpdateName(v.Name));
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
                string build = ASMx86_16BIT.UpdateName(vbl.Name);

                if(vbl.varType == Inside.VariableTypeStr)
                {
                    build = ASMx86_16BIT.VarTypeConvert(vbl);
                }
                else if(vbl.varType == Inside.VariableTypeInt)
                {
                    build += ASMx86_16BIT.varIntTyp;
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
                    Name = ASMx86_16BIT.prefixVariable + vcount, 
                    ValueRaw = ASMx86_16BIT.varStrTyp + value + ",0" , // db 'value',0
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
                                Name = ASMx86_16BIT.prefixVariable + cls.Variables[j].Name,
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

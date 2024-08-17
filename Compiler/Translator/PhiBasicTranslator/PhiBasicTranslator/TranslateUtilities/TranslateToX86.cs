﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
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
        public static int IfCount = 0;
        public static List<string> ToX86(PhiCodebase code)
        {
            List<string> ASM = new List<string>();

            List<PhiVariable> allVars = new List<PhiVariable>();

            for (int i = 0; i < code.ClassList.Count; i++)
            {
                PhiClass cls = code.ClassList[i];

                if (cls.Type == PhiType.PHI)
                {

                    if (cls.Inherit == Defs.OS16BIT)
                    {
                        ASM.AddRange(ASMx86_16BIT.GetInheritance(ASMx86_16BIT.InheritType.BITS16));
                        ASM = AutoInclude_BITS16(ASM);

                        allVars.AddRange(ConvertAllVariables(cls));

                        List<string> values = ConvertVarsToASM(allVars);

                        List<string> SubCode = new List<string>
                        {
                            "",
                            "  " + Defs.replaceCodeStart,
                            ""
                        };

                        ASM = ASMx86_16BIT.MergeValues(ASM, values, Defs.replaceVarStart);

                        BuildPair pair = BuildAllInstructs(ASM, SubCode, cls, allVars);

                        ASM = pair.CodeBase;
                    }
                    else if (cls.Inherit == Defs.OS16BitVideo)
                    {
                        ASM.AddRange(ASMx86_16BIT.GetInheritance(ASMx86_16BIT.InheritType.BITS16));
                        ASM = AutoInclude_BITS16(ASM);

                        allVars.AddRange(ConvertAllVariables(cls));

                        List<string> values = ConvertVarsToASM(allVars);

                        List<string> SubCode = new List<string>
                        {
                            "",
                            "  " + Defs.replaceCodeStart,
                            ""
                        };

                        ASM = ASMx86_16BIT.MergeValues(ASM, values, Defs.replaceVarStart);

                        BuildPair pair = BuildAllInstructs(ASM, SubCode, cls, allVars);

                        ASM = pair.CodeBase;
                    }
                }
                else if(cls.Type == PhiType.ASM)
                {
                    List<string> values = new List<string>()
                    {
                        cls.Name + ":",
                    };

                    string[] splt = cls.RawContent.Split("\n");

                    values.AddRange(splt);

                    for(int v = 0; v < values.Count; v++) 
                        values[v] = ParseVariables.ReplaceASMVar(values[v], allVars);

                    ASM = ASMx86_16BIT.MergeValues(ASM, values, Defs.replaceIncludes);
                }
            }

            //reset after complete
            VarCount = 0;
            LoopCount = 0; 
            IfCount = 0;

            return ASM;
        }

        public static BuildPair BuildAllInstructs(List<string> Code, List<string> SubCode, PhiClass cls, List<PhiVariable> predefined)
        {
            BuildPair build = new BuildPair();

            build.CodeBase = Code;

            foreach (PhiInstruct inst in cls.Instructs)
            { 
                BuildPair pair = BuildSubInstructs(build.CodeBase, SubCode, inst, cls, predefined, false);

                build.CodeBase = pair.CodeBase;

                build.CodeBase = ASMx86_16BIT.MergeSubCode(build.CodeBase, pair.SubCode, Defs.replaceCodeStart);
            }

            return build;
        }

        public static BuildPair BuildSubInstructs(List<string> Code, List<string> SubCode, PhiInstruct instrct, PhiClass cls, List<PhiVariable> predefined, bool sub = true)
        {
            List<string> cde = Code;
            List<string> scd = SubCode;

            foreach (PhiInstruct inst in instrct.Instructs)
            {
                BuildPair pair = BuildSubInstructs(cde, scd, inst, cls, predefined);

                Code = pair.CodeBase;

                instrct.BuildPairs.Add(pair);
            }

            if (instrct.Name == Defs.instLog)
            {
                BuildPair pair = BuildInstructLog(instrct, cls, predefined, Code, SubCode);

                return pair;
            }
            else if (instrct.Name == Defs.instWhile)
            {
                string name = "WHILE_" + (LoopCount++);

                BuildPair pair = BuildInstructWhile(name, instrct, cls);

                if (instrct.BuildPairs.Count > 0)
                {
                    foreach (BuildPair bp in instrct.BuildPairs)
                    {
                        pair.CoreCode = ASMx86_16BIT.MergeSubCode(pair.CoreCode, bp.SubCode, ASMx86_16BIT.replaceLoopContent);
                    }
                }

                if (sub)
                {
                    //remove because sub
                    pair.CoreCode = ASMx86_16BIT.ReplaceValue(pair.CoreCode, Defs.replaceCodeStart, "");
                }

                Code = ASMx86_16BIT.MergeValues(Code, pair.CoreCode, Defs.replaceIncludes);

                pair.CodeBase = Code;

                return pair;
            }
            else if (instrct.Name == Defs.instIf)
            {
                string name = "IF_" + (IfCount++);

                BuildPair pair = BuildInstructIf(name, instrct, cls);

                if (instrct.BuildPairs.Count > 0)
                {
                    foreach (BuildPair bp in instrct.BuildPairs)
                    {
                        pair.CoreCode = ASMx86_16BIT.MergeSubCode(pair.CoreCode, bp.SubCode, ASMx86_16BIT.replaceIfContent);
                    }
                }

                if (sub)
                {
                    //remove because sub
                    pair.CoreCode = ASMx86_16BIT.ReplaceValue(pair.CoreCode, Defs.replaceCodeStart, "");
                }

                if (sub)
                {
                    Code = ASMx86_16BIT.MergeValues(Code, pair.CoreCode, Defs.replaceIncludes);
                    // SubCode = ASMx86_16BIT.MergeValues(SubCode, pair.SubCode, Defs.replaceCodeStart);

                }
                else
                {
                    // add methods into the includes
                    Code = ASMx86_16BIT.MergeValues(Code, pair.CoreCode, Defs.replaceIncludes);
                    pair.CoreCode = ASMx86_16BIT.MergeValues(pair.CoreCode, pair.SubCode, Defs.replaceCodeStart);
                }

                pair.CodeBase = Code;

                return pair;
            }
            else if (instrct.Name == Defs.instElse)
            {
                Console.WriteLine(instrct.Name + " Unimplemented");
            }
            else if (instrct.Name == Defs.instAsk)
            {
                BuildPair pair = BuildInstructAsk(instrct, cls, predefined, Code, SubCode);

                return pair;
            }
            else if (instrct.Name == Defs.instCall)
            {
                BuildPair pair = new BuildPair();

                string callname = "";

                for(int i = 0; i < instrct.Value.Length; i++)
                {
                    if (instrct.Value[i].ToString() == Defs.VariableSet)
                    {
                        break;
                    }

                    if (instrct.ContentLabels[i] == Inside.None)
                    {
                        callname += instrct.Value[i];
                    }
                }

                callname = callname.Replace('.', '_').Trim();

                pair.SubCode = new List<string>();

                if (instrct.Variables.Count > 0)
                {
                    if(callname == ASMx86_16BIT.incDrawRectangle)
                    {
                        for(int i = 0; i < instrct.Variables.Count; i++)
                        {
                            Inside vtyp = ASMx86_16BIT.VarType_DrawRectangle[i];

                            List<string> setcode = new List<string>();

                            if (vtyp == Inside.VariableTypeByt)
                            {
                                setcode.AddRange(ASMx86_16BIT.BIT8x86_SetVariable);
                            }
                            else
                            {
                                setcode.AddRange(ASMx86_16BIT.BIT32x86_SetVariable);
                            }

                            setcode = ASMx86_16BIT.ReplaceValue(
                                setcode,
                                Defs.replaceValueStart,
                                instrct.Variables[i].Name
                                );

                            setcode = ASMx86_16BIT.ReplaceValue(
                                setcode,
                                ASMx86_16BIT.replaceVarName,
                                ASMx86_16BIT.VarList_DrawRectangle[i]
                             );

                            pair.SubCode.AddRange(setcode);
                        }
                    }
                }

                pair.SubCode.Add(
                   "    " 
                   + ASMx86_16BIT.callLabel 
                   + " " 
                   + callname.Trim()
                   );

                pair.CodeBase = Code;

                return pair;
            }

            return new BuildPair 
            { 
                CoreCode = Code, 
                SubCode = SubCode 
            };
        }

        public static List<PhiVariable> ConvertAllVariables(PhiClass cls)
        {
            List<PhiVariable> allv = UpdateUnsetVars(cls.Variables);

            foreach (PhiMethod methods in cls.Methods)
            {
                allv.AddRange(UpdateUnsetVars(methods.Variables));
            }

            foreach(PhiInstruct inst in cls.Instructs)
            {
                allv.AddRange(ConvertAllSubVariables(inst));
            }

            return allv;
        }

        public static List<PhiVariable> ConvertAllSubVariables(PhiInstruct inst)
        {
            List<PhiVariable> values = new List<PhiVariable>();
                
            List<PhiVariable> up = UpdateUnsetVars(inst.Variables);

            foreach (PhiVariable v in up)
            {
                if(v.varType != Inside.VariableTypeInsert)
                    values.Add(v.Copy());
            }

            foreach (PhiInstruct instruct in inst.Instructs)
            {
                List<PhiVariable> sub = ConvertAllSubVariables(instruct);

                foreach(PhiVariable v in sub)
                {
                    values.Add(v.Copy());
                }
            }

            return values;
        }

        public static List<PhiVariable> UpdateUnsetVars(List<PhiVariable> varbles)
        {
            foreach (PhiVariable vbl in varbles)
            {
                if (vbl.Name == Defs.replaceUnsetName)
                {
                    vbl.Name = (VarCount++).ToString();
                    
                    if(vbl.varType == Inside.StandAloneInt)
                    {
                        vbl.varType = Inside.VariableTypeInt;
                    }
                }
            }

            return varbles;
        }

        public static BuildPair BuildInstructIf(string Name, PhiInstruct instruct, PhiClass cls)
        {
            List<string> buildcode = new List<string>();
            List<string> buildsub = new List<string>();


            List<string> values = instruct.Variables.Select(x => x.Name).ToList();

            for (int i = 0; i < values.Count; i++)
            {
                values[i] = ASMx86_16BIT.UpdateName(values[i]);
            }

            string jmpCon = ASMx86_16BIT.jumpIfGreaterThan;

            PhiConditional? condit = instruct.Conditionals.FirstOrDefault();

            if (condit != null)
            {
                jmpCon = ConvertConditional(condit);

                ConditionalPairs? pair = condit.PhiConditionalPairs.FirstOrDefault();

                if (pair != null)
                {
                   

                    string left = pair.LeftValue;
                    string right = pair.RightValue;

                    PhiVariable? vl = cls.Variables.Where(c => c.Name == left).FirstOrDefault();
                    if (vl != null)
                    {
                        left = ASMx86_16BIT.UpdateName(left);
                        left = "[" + left + "]";
                    }

                    PhiVariable? vr = cls.Variables.Where(c => c.Name == right).FirstOrDefault();
                    if (vr != null)
                    {
                        right = ASMx86_16BIT.UpdateName(right);

                        if(vr.varType == Inside.VariableTypeInt)
                        {
                            right = "[" + right + "]";  
                        }
                    }
                    #region BUILD VALUES
                    buildcode.AddRange(ASMx86_16BIT.InstructIfCheck_BITS16);

                    buildcode = ASMx86_16BIT.ReplaceValue(
                      buildcode,
                      ASMx86_16BIT.replaceIfName,
                      Name
                   );
                    buildcode = ASMx86_16BIT.ReplaceValue(
                      buildcode,
                      ASMx86_16BIT.replaceIfJump,
                      jmpCon
                   );
                    buildcode = ASMx86_16BIT.ReplaceValue(
                       buildcode,
                       ASMx86_16BIT.replaceIfLeftCompare,
                       left
                    );

                    buildcode = ASMx86_16BIT.ReplaceValue(
                      buildcode,
                      ASMx86_16BIT.replaceIfRightCompare,
                      right
                    );

                    // build content

                    buildcode.AddRange(ASMx86_16BIT.InstructIfContent_BITS16);

                    buildcode = ASMx86_16BIT.ReplaceValue(
                      buildcode,
                      ASMx86_16BIT.replaceIfName,
                      Name
                    );

                    #endregion

                    buildsub.AddRange(ASMx86_16BIT.InstructIfCall_BITS16);

                    buildsub = ASMx86_16BIT.ReplaceValue(
                        buildsub,
                        ASMx86_16BIT.replaceIfName,
                        Name
                        );
                }
            }

            return new BuildPair
            {
                CoreCode = buildcode,
                SubCode = buildsub
            };
        }
        public static BuildPair BuildInstructWhile(string name, PhiInstruct instruct, PhiClass cls)
        {            
            List<string> loopStart = new List<string>();

            List<string> callWhile = ASMx86_16BIT.ReplaceValue(
                   ASMx86_16BIT.InstructWhileCall_BITS16,
                   ASMx86_16BIT.replaceLoopName,
                   name);

            List<string> values = instruct.Variables.Select(x => x.Name).ToList();

            for (int i = 0; i < values.Count; i++)
            {
                values[i] = ASMx86_16BIT.UpdateName(values[i]);
            }

            //remember to check for different while loop formats and math example: (6 + (4/i))
            if (values.Count >= 2)
            {
                string nameCont = name;

                string vStart = values.FirstOrDefault() ?? string.Empty;
                string vLimit = values.LastOrDefault() ?? string.Empty;

                string jmpCon = ASMx86_16BIT.jumpIfGreaterThan;
                string incVal = ASMx86_16BIT.loopSubIncrementByOne;

                PhiConditional? condit = instruct.Conditionals.FirstOrDefault();
                PhiMath? mths = instruct.Maths.FirstOrDefault();

                if (condit != null)
                {
                    jmpCon = ConvertConditional(condit);
                }

                if (mths != null)
                {
                    incVal = ConvertIncrement(mths);
                }

                #region Loop Construction

                loopStart.AddRange(ASMx86_16BIT.InstructWhileContent_BITS16);

                loopStart = ASMx86_16BIT.ReplaceValue(
                   loopStart,
                   ASMx86_16BIT.replaceLoopContentName,
                   nameCont
                );

                loopStart.AddRange(ASMx86_16BIT.ReplaceValue(
                    ASMx86_16BIT.InstructWhileStart_BITS16,
                    Defs.replaceValueStart,
                    vStart
                ));

                PhiVariable? startValue = instruct.Variables.Where(x => ASMx86_16BIT.UpdateName(x.Name) == vStart).FirstOrDefault();

                if (startValue != null)
                {
                    loopStart = ASMx86_16BIT.ReplaceValue(
                       loopStart,
                       ASMx86_16BIT.replaceLoopStart,
                       startValue.ValueRaw
                   );
                }

                loopStart.AddRange(ASMx86_16BIT.InstructWhileCheck_BITS16);

                #region LOOP CHECK

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    ASMx86_16BIT.replaceLoopName,
                    name
                );

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
                    jmpCon
                );

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    ASMx86_16BIT.replaceLoopIncrement,
                    incVal
                );

                loopStart = ASMx86_16BIT.ReplaceValue(
                    loopStart,
                    ASMx86_16BIT.replaceLoopContentName,
                    nameCont
                );

                #endregion

                loopStart.AddRange(ASMx86_16BIT.InstructWhileDone_BITS16);



                #region LOOP END

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

            return new BuildPair
            {
                CoreCode = loopStart,
                SubCode = callWhile
            };
        }

        public static BuildPair BuildInstructLog(PhiInstruct instruct, PhiClass cls, List<PhiVariable> predefined, List<string> Code, List<string> SubCode)
        {
            List<string> subCde = new List<string>() { Defs.replaceCodeStart };
            // remember to add includes check to prevent duplicates

            List<string> values = new List<string>();

            List<PhiVariable> vrs = ParseVariables.GetInstructSubVariables(instruct, predefined);

            //VarCount += vrs.Count;

            foreach (PhiVariable v in vrs)
            {
                if (v.varType == Inside.VariableTypeStr)
                {
                    // adds function call code
                    subCde = ASMx86_16BIT.MergeValues(subCde, ASMx86_16BIT.InstructLogString_BITS16, Defs.replaceCodeStart);
                }
                else if (v.varType == Inside.VariableTypeInt)
                {
                    // adds function call code
                    subCde = ASMx86_16BIT.MergeValues(subCde, ASMx86_16BIT.InstructLogInt_BITS16, Defs.replaceCodeStart);
                }

                if (!v.preExisting) values.Add(ASMx86_16BIT.VarTypeConvert(v));

                subCde = ASMx86_16BIT.ReplaceValue(subCde, Defs.replaceValueStart, ASMx86_16BIT.UpdateName(v.Name));
            }

            Code = ASMx86_16BIT.MergeValues(Code, values, Defs.replaceVarStart);

            return new BuildPair
            {
                CodeBase = Code,
                CoreCode = new List<string>(),
                SubCode = subCde
            };
        }

        public static BuildPair BuildInstructAsk(PhiInstruct instruct, PhiClass cls, List<PhiVariable> predefined, List<string> Code, List<string> SubCode)
        {
            List<string> subCde = new List<string>() { Defs.replaceCodeStart };
            // remember to add includes check to prevent duplicates

            List<string> values = new List<string>();

            List<PhiVariable> vrs = ParseVariables.GetInstructSubVariables(instruct, predefined);

            //VarCount += vrs.Count;

            foreach (PhiVariable v in vrs)
            {
                if (v.varType == Inside.VariableTypeStr)
                {
                    // adds function call code
                    subCde = ASMx86_16BIT.MergeValues(subCde, ASMx86_16BIT.InstructASK_BITS16, Defs.replaceCodeStart);
                }

                if (!v.preExisting) values.Add(ASMx86_16BIT.VarTypeConvert(v));

                subCde = ASMx86_16BIT.ReplaceValue(subCde, Defs.replaceValueStart, ASMx86_16BIT.UpdateName(v.Name));
            }

            Code = ASMx86_16BIT.MergeValues(Code, values, Defs.replaceVarStart);

            return new BuildPair
            {
                CodeBase = Code,
                CoreCode = new List<string>(),
                SubCode = subCde
            };
        }

        public static List<string> AutoInclude_BITS16(List<string> Code)
        {
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.PrintInt_x86BITS16, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.PrintLog_x86BITS16, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.AskInput_x86BITS16, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_VideoMode, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_SectorPrep, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_JumpSectorTwo, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_WaitForKeyPress, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_Interupt, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_InteruptEvent, Defs.replaceIncludes);
            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_DrawRectangle, Defs.replaceIncludes);

            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_DrawConstants, Defs.replaceConstStart);

            Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_DrawVariables, Defs.replaceVarStart);

            return Code;
        }

        public static List<string> ConvertVarsToASM(List<PhiVariable> phiVariables)
        {
            List<string> vals = new List<string>();

            foreach (PhiVariable vbl in phiVariables)
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
        public static List<PhiVariable> ConvertValue(string value, PhiClass cls, Inside ValueType = Inside.VariableTypeStr, int vcount = 0)
        {
            List<PhiVariable> varbls = new List<PhiVariable>();

            if(ValueType == Inside.String)
            {
                if (value[value.Length - 1].ToString() == Defs.VariableSetClosure)
                {
                    value = value.Substring(0, value.Length - 1);
                }
                else
                {

                }

                varbls.Add(new PhiVariable 
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
                        // checks if name or just a part of another variable
                        if (ParseMisc.StartsWithAppendedValue(
                            cut, 
                            cls.Variables[j].Name, 
                            Defs.ConditionalClosureCharacters.ToList()
                            ))
                        {
                            varbls.Add(new PhiVariable
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
        public static string ConvertIncrement(PhiMath math)
        {
            if(math.Math.MathOp == Defs.MathInc)
            {
                return ASMx86_16BIT.loopSubIncrementByOne;
            }
            else if (math.Math.MathOp == Defs.MathDec)
            {
                return ASMx86_16BIT.loopSubDecrementByOne;
            }

            return string.Empty;
        }

        public static string ConvertConditional(PhiConditional condition)
        {
            string cond = string.Empty;

            if(condition != null)
            {
                ConditionalPairs.ConditionType tp = condition.PhiConditionalPairs.First().type;

                if(tp == ConditionalPairs.ConditionType.JumpIfLessEqual)
                {
                    cond = ASMx86_16BIT.jumpIfLessThanEqual; // <=
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfEqual)
                {
                    cond = ASMx86_16BIT.jumpIfEqual; // ==
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfLess)
                {
                    cond = ASMx86_16BIT.jumpIfLessThan; // <
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfGreater)
                {
                    cond = ASMx86_16BIT.jumpIfGreaterThan; // >
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfNotEqual)
                {
                    cond = ASMx86_16BIT.jumpIfNotEqual; // != 
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfGreaterEqual)
                {
                    cond = ASMx86_16BIT.jumpIfGreaterThanEqual;
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfCarry)
                {
                    cond = ASMx86_16BIT.jumpIfCarry;
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfNoCarry)
                {
                    cond = ASMx86_16BIT.jumpIfNoCarry;
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfOverflow)
                {
                    cond = ASMx86_16BIT.jumpIfOverflow;
                }
                else if (tp == ConditionalPairs.ConditionType.JumpIfNoOverflow)
                {
                    cond = ASMx86_16BIT.jumpIfNoOverflow;
                }
            }

            return cond;
        }
    }
}

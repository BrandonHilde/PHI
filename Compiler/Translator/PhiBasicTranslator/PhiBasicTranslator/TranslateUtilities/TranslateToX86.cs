using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection.Metadata;
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
        public static PhiCodebase ToX86(PhiCodebase code)
        {
            PhiCodebase codebase = new PhiCodebase();

            List<string> ASM = new List<string>();

            List<PhiVariable> allVars = new List<PhiVariable>();

            for (int i = 0; i < code.ClassList.Count; i++)
            {
                PhiClass cls = code.ClassList[i];

                ASM.Clear();

                if (cls.Type == PhiType.PHI)
                {
                    if (cls.Inherit == Defs.OS16BIT)
                    {
                        ASM.AddRange(ASMx86_16BIT.GetInheritance(ASMx86_16BIT.InheritType.BITS16));
                        ASM = AutoInclude_BITS16(ASM, cls);

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

                        //TODO: ADD RETURN VALUES INTO Method

                        BuildPair mpar = BuildAllMethods(ASM, SubCode, cls, allVars);

                        ASM = mpar.CodeBase;
                    }
                    else if (cls.Inherit == Defs.OS16BitVideo)
                    {
                        ASM.AddRange(ASMx86_16BIT.GetInheritance(ASMx86_16BIT.InheritType.BITS16));
                        ASM = AutoInclude_BITS16(ASM, cls);

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

                        BuildPair mpar = BuildAllMethods(ASM, SubCode, cls, allVars);

                        ASM = mpar.CodeBase;
                    }
                    else if(cls.Inherit == Defs.OS16BitSectorTwo)
                    {
                        ASM.AddRange(
                            ASMx86_16BIT.GetInheritance(
                                ASMx86_16BIT.InheritType.BITS16SectorTwo));

                        ASM = AutoInclude_BITS16(ASM, cls);

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

                        BuildPair mpar = BuildAllMethods(ASM, SubCode, cls, allVars);

                        ASM = mpar.CodeBase;
                    }
                }
                else if(cls.Type == PhiType.ASM)
                {
                    ASM = new List<string>()
                    {
                        Defs.replaceIncludes
                    };

                    List<string> values = new List<string>()
                    {
                        cls.Name + ":",
                    };

                    string[] splt = cls.RawContent.Split("\n");
                    
                    values.AddRange(splt);

                    for(int v = 0; v < values.Count; v++) 
                        values[v] = ParseVariables.ReplaceASMVar(values[v], allVars);

                    ASM = ASMx86_16BIT.MergeValues(ASM, values, Defs.replaceIncludes);
                    ASM = ASMx86_16BIT.ReplaceValue(ASM, Defs.replaceIncludes, string.Empty);
                }

                cls.translatedASM = ASM;
                codebase.ClassList.Add(cls.Copy());
            }

            //reset after complete
            VarCount = 0;
            LoopCount = 0; 
            IfCount = 0;

            List<PhiClass> asmcls = codebase.ClassList.Where(x=> x.Type == PhiType.ASM).ToList();

            if (asmcls.Count > 0)
            {
                for (int c = 0; c < codebase.ClassList.Count; c++)
                {
                    if (codebase.ClassList[c].Type != PhiType.ASM)
                    {
                        List<PhiInstruct> subsinst = codebase.ClassList[c].GetAllInstructs();

                        foreach( PhiClass asm in asmcls)
                        {
                            PhiInstruct? isCalled = subsinst.Where(
                                x=>x.Value.Contains(asm.Name)).FirstOrDefault();

                            if(isCalled != null)
                            {
                                codebase.ClassList[c].translatedASM = ASMx86_16BIT.MergeValues(
                                    codebase.ClassList[c].translatedASM, 
                                    asm.translatedASM, 
                                    Defs.replaceIncludes
                                );
                            }
                        }
                    }
                }
            }

            return codebase;
        }

        public static BuildPair BuildAllMethods(List<string> Code, List<string> SubCode, PhiClass cls, List<PhiVariable> predefined)
        {
            BuildPair build = new BuildPair();

            build.CodeBase = Code;

            foreach (PhiMethod method in cls.Methods)
            {
                BuildPair pair = BuildAllMethodInstructs(Code, SubCode, method, cls, predefined);

                build.CodeBase = pair.CodeBase;

                Code = build.CodeBase; //reqired line because Code is used in buildallmethodintructs

                build.CodeBase = ASMx86_16BIT.MergeSubCode(build.CodeBase, pair.CoreCode, Defs.replaceIncludes);
            }

            return build;
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

        public static BuildPair BuildAllMethodInstructs(List<string> Code, List<string> SubCode, PhiMethod mthd, PhiClass cls, List<PhiVariable> predefined)
        {
            BuildPair build = new BuildPair();

            build.CodeBase = Code;

            //List<string> mthdCode = new List<string>();

            //mthdCode.AddRange(ASMx86_16BIT.BIT16x86_TimerEvent);

            List<string> mthdCode = new List<string>()
            {
                mthd.Name + ":",
                Defs.replaceCodeStart,
                "   ret"
            };

            foreach (PhiInstruct inst in mthd.Instructs)
            {
                BuildPair pair = BuildSubInstructs(build.CodeBase, SubCode, inst, cls, predefined, false);

                build.CodeBase = pair.CodeBase;
                build.CoreCode = pair.CoreCode;

                mthdCode = ASMx86_16BIT.MergeSubCode(mthdCode, pair.SubCode, Defs.replaceCodeStart);              
            }

            mthdCode = ASMx86_16BIT.ReplaceValue(mthdCode, Defs.replaceCodeStart, string.Empty);

            build.CodeBase = ASMx86_16BIT.MergeSubCode(build.CodeBase, mthdCode, Defs.replaceIncludes);

            return build;
        }

        public static BuildPair BuildSubInstructs(List<string> Code, List<string> SubCode, PhiInstruct instrct, PhiClass cls, List<PhiVariable> predefined, bool sub = true)
        {

            for (int i = 0; i < instrct.Instructs.Count; i++)
            {
                PhiInstruct inst = instrct.Instructs[i];

                BuildPair pair = BuildSubInstructs(Code, SubCode, inst, cls, predefined);

                Code = pair.CodeBase;

                instrct.BuildPairs.Add(pair);
            }



            //foreach are the bane of my existance because they dont allow modification
            //foreach (PhiInstruct inst in instrct.Instructs)
            //{
              
            //}

            if (instrct.Name == Defs.instLog)
            {
                BuildPair pair = BuildInstructLog(instrct, cls, predefined, Code, SubCode);

                return pair;
            }
            else if (instrct.Name == Defs.instWhile)
            {
                string name = Defs.WhileNameStart + (LoopCount++);

                BuildPair pair = BuildInstructWhile(name, instrct, cls);

                pair.CoreCode = new List<string>
                {
                    name + Defs.VariableSet,
                    ASMx86_16BIT.replaceLoopContent
                };

                if (instrct.BuildPairs.Count > 0)
                {
                    foreach (BuildPair bp in instrct.BuildPairs)
                    {
                        pair.CoreCode = ASMx86_16BIT.MergeSubCode(
                            pair.CoreCode, 
                            bp.SubCode, 
                            ASMx86_16BIT.replaceLoopContent);
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

                BuildPair pair = BuildInstructIf(Code, SubCode, name, instrct, cls, predefined);

                if (instrct.BuildPairs.Count > 0)
                {
                    foreach (BuildPair bp in instrct.BuildPairs)
                    {
                        if (bp.Label == Defs.instElse)
                        {
                            pair.CoreCode = ASMx86_16BIT.MergeSubCode(pair.CoreCode, bp.SubCode, ASMx86_16BIT.replaceElseContent);
                        }
                        else
                        {
                            pair.CoreCode = ASMx86_16BIT.MergeSubCode(pair.CoreCode, bp.SubCode, ASMx86_16BIT.replaceIfContent);
                        }
                    }
                }

                if (sub)
                {
                    //remove because sub
                    pair.CoreCode = ASMx86_16BIT.ReplaceValue(pair.CoreCode, Defs.replaceCodeStart, "");
                }

                if (sub)
                {
                    pair.CodeBase = ASMx86_16BIT.MergeValues(Code, pair.CoreCode, Defs.replaceIncludes);
                    // SubCode = ASMx86_16BIT.MergeValues(SubCode, pair.SubCode, Defs.replaceCodeStart);

                }
                else
                {
                    // add methods into the includes
                    pair.CodeBase = ASMx86_16BIT.MergeValues(Code, pair.CoreCode, Defs.replaceIncludes);
                    pair.CoreCode = ASMx86_16BIT.MergeValues(pair.CoreCode, pair.SubCode, Defs.replaceCodeStart);
                }

                return pair;
            }
            else if (instrct.Name == Defs.instElse)
            {
                BuildPair pair = new BuildPair();

                pair.CodeBase = Code;

                pair.Label = Defs.instElse;

                pair.SubCode = new List<string>()
                {
                    ASMx86_16BIT.replaceElseContent
                };

                if (instrct.BuildPairs.Count > 0)
                {
                    foreach (BuildPair bp in instrct.BuildPairs)
                    {
                        pair.SubCode = ASMx86_16BIT.MergeSubCode(pair.SubCode, bp.SubCode, ASMx86_16BIT.replaceElseContent);
                    }
                }

                pair.SubCode = ASMx86_16BIT.ReplaceValue(
                    pair.SubCode, 
                    ASMx86_16BIT.replaceElseContent, 
                    string.Empty
                    );

                pair.SubCode = ASMx86_16BIT.ReplaceValue(
                   pair.SubCode,
                   Defs.replaceCodeStart,
                   string.Empty
                   );

                pair.SubCode.RemoveAll(x=> x == string.Empty);

                //if (sub)
                //{
                //    pair.CodeBase = ASMx86_16BIT.MergeValues(Code, pair.CoreCode, Defs.replaceIncludes);
                //    // SubCode = ASMx86_16BIT.MergeValues(SubCode, pair.SubCode, Defs.replaceCodeStart);

                //}
                //else
                //{
                //    // add methods into the includes
                //    pair.CodeBase = ASMx86_16BIT.MergeValues(Code, pair.CoreCode, Defs.replaceIncludes);
                //    pair.CoreCode = ASMx86_16BIT.MergeValues(pair.CoreCode, pair.SubCode, Defs.replaceCodeStart);
                //}

                return pair;
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

                string callSetTo = "";

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

                if(callname.Contains(ASMx86_16BIT.replaceLoopContentName))
                {
                    string loopname = Defs.WhileNameStart + LoopCount;
                    callname = callname.Replace(ASMx86_16BIT.replaceLoopContentName, loopname);
                }

                callname = callname.Replace('.', '_').Trim(); // trim prevents false split

                if(callname.Contains(' '))
                {
                    callname = callname.Replace("\t", " ");

                    string[] splt = callname.Split(' ');

                    for(int i = 0; i < splt.Length; i++)
                    {
                        if (splt[i] != string.Empty)
                        {
                            if(callSetTo == string.Empty) 
                                callSetTo = splt[i];
                            else 
                                callname = splt[i];
                        }
                    }
                }
                else if(callname.Contains('\t'))
                {
                    callname = callname.Replace(" ", "\t");

                    string[] splt = callname.Split('\t');

                    for (int i = 0; i < splt.Length; i++)
                    {
                        if (splt[i] != string.Empty)
                        {
                            if (callSetTo == string.Empty)
                                callSetTo = splt[i];
                            else
                                callname = splt[i];
                        }
                    }
                }

                pair.SubCode = new List<string>();

                #region GetKey

                if (callname == ASMx86_16BIT.incGetKey)
                {
                    List<string> setcode = new List<string>();

                    if (callSetTo != string.Empty && callname != string.Empty)
                    {
                        setcode.AddRange(ASMx86_16BIT.BIT16x86_GetKey);

                        setcode = ASMx86_16BIT.ReplaceValue(
                            setcode, 
                            ASMx86_16BIT.replaceVarName, 
                            ASMx86_16BIT.UpdateName(callSetTo)
                            );
                    }

                    pair.CodeBase = Code;
                    pair.SubCode.AddRange(setcode);

                    return pair;
                }
                #endregion


                PhiMethod? method = cls.Methods.Where(x => x.Name == callname).FirstOrDefault();

                if (instrct.Variables.Count > 0 && method == null)
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

                            string mname = instrct.Variables[i].Name;

                            if(mname.StartsWith("'") && mname.EndsWith("'"))
                            {
                                // do nothing for now
                            }
                            else
                            {
                                if(!ParseMisc.IsNumber(mname))
                                {
                                    PhiVariable? vbr = predefined.Where(x => x.Name == mname).FirstOrDefault();

                                    mname = ASMx86_16BIT.UpdateName(mname);

                                    if (vbr != null)
                                    {
                                        mname = "[" + mname + "]";
                                    }
                                }
                            }

                            string vlistName = "[" + ASMx86_16BIT.VarList_DrawRectangle[i] + "]";

                            setcode = ASMx86_16BIT.ReplaceValue(
                                setcode,
                                Defs.replaceValueStart,
                                mname
                                );

                            setcode = ASMx86_16BIT.ReplaceValue(
                                setcode,
                                ASMx86_16BIT.replaceVarName,
                                vlistName
                             );

                            pair.SubCode.AddRange(setcode);
                        }
                    }
                    else if (callname == ASMx86_16BIT.incIsKeyDown)
                    {
                        string nme = instrct.Variables.First().Name;    

                        List<string> setcode = new List<string>();

                        if (callSetTo != string.Empty && callname != string.Empty)
                        {
                            setcode.AddRange(ASMx86_16BIT.BIT16x86_IsKeyDown);
                            setcode = ASMx86_16BIT.ReplaceValue(
                                setcode,
                                Defs.replaceValueStart,
                                nme);

                            setcode = ASMx86_16BIT.ReplaceValue(
                                setcode,
                                ASMx86_16BIT.replaceVarName,
                                ASMx86_16BIT.UpdateName(callSetTo)
                                );
                        }

                        pair.SubCode.AddRange(setcode);
                    }
                    else
                    {

                    }
                }
               
                if(method != null)
                { 
                    if(instrct.Variables.Count > 0)
                    {
                        for(int i = 0; i < instrct.Variables.Count; i++)
                        {
                            if (i < method.Variables.Count)
                            {
                                List<string> sbcode = new List<string>();

                                sbcode.AddRange(ASMx86_16BIT.BIT32x86_SetVariable);

                                string nme = instrct.Variables[i].Name;

                                PhiVariable? vbr = predefined.Where(x => x.Name == nme).FirstOrDefault();

                                if(vbr != null)
                                {
                                    nme = "[" + ASMx86_16BIT.UpdateName(nme) + "]";
                                }

                                sbcode = ASMx86_16BIT.ReplaceValue(
                                    sbcode,
                                    Defs.replaceValueStart,
                                    nme
                                );

                                sbcode = ASMx86_16BIT.ReplaceValue(
                                    sbcode,
                                    ASMx86_16BIT.replaceVarName,
                                    "[" + ASMx86_16BIT.UpdateName(method.Variables[i].Name) + "]"
                                );

                                pair.SubCode.AddRange(sbcode);
                            }
                        }
                    }

                    // pair.SubCode.AddRange(setcode);

                    //check if method is a coded method instead of built-in

                    if (method != null)
                    {
                        if (method.End != string.Empty && callSetTo != string.Empty)
                        {
                            PhiVariable? vrbl = predefined.Where(x => x.Name == method.End).FirstOrDefault();
                            PhiVariable? vrst = predefined.Where(x => x.Name == callSetTo).FirstOrDefault();

                            if (vrbl != null && vrst != null)
                            {
                                List<string> setcode = new List<string>();

                                setcode.AddRange(ASMx86_16BIT.BIT32x86_SetVariable);

                                setcode = ASMx86_16BIT.ReplaceValue(
                                    setcode,
                                    Defs.replaceValueStart, 
                                    "[" + ASMx86_16BIT.UpdateName(vrbl.Name) + "]"
                                    );

                                setcode = ASMx86_16BIT.ReplaceValue(
                                   setcode,
                                   ASMx86_16BIT.replaceVarName,
                                   "[" + ASMx86_16BIT.UpdateName(vrst.Name) + "]"
                                );

                                if (!ASMx86_16BIT.excludeMethodCall.Contains(callname))
                                {
                                    pair.SubCode.Add(
                                        "    "
                                        + ASMx86_16BIT.callLabel
                                        + " "
                                        + callname.Trim()
                                        );
                                }

                                pair.SubCode.AddRange(setcode);

                                pair.CodeBase = Code;

                                return pair;
                            }
                        }
                    }
                }

                pair.CodeBase = Code;

                if (!ASMx86_16BIT.excludeMethodCall.Contains(callname))
                {
                    pair.SubCode.Add(
                        "    "
                        + ASMx86_16BIT.callLabel
                        + " "
                        + callname.Trim()
                        );
                }

                return pair;
            }
            else if (instrct.Name == Defs.instMath)
            {
                BuildPair pair = new BuildPair();

                foreach (PhiMath math in instrct.Maths)
                {
                    pair.SubCode.AddRange(BuildMathInstruct(math, predefined));
                }

                pair.CodeBase = Code;   

                return pair;
            }

            return new BuildPair 
            { 
                CodeBase = Code,
                CoreCode = new List<string>(), 
                SubCode = SubCode 
            };
        }

        public static List<string> BuildMathInstruct(PhiMath math, List<PhiVariable> predefined)
        {
            List<string> list = new List<string>();

            PhiMath.Opperation op = ParseUtilities.MatchesOpperation(math.Math.MathOp);

            string left = math.Math.ValueLeft.Trim();
            string right = math.Math.ValueRight.Trim();

            if(left.Contains(Defs.squareOpen) && left.Contains(Defs.squareClose))
            {
                left = ConvertArrayToASM(left, predefined);
            }

            if (right.Contains(Defs.squareOpen) && right.Contains(Defs.squareClose))
            {
                right = ConvertArrayToASM(right, predefined);
            }

            bool hasOpperation = false;

            //need to add constants to predefined

            PhiVariable? varbleL = predefined.Where(x => x.Name == math.Math.ValueLeft).FirstOrDefault();

            PhiVariable? varbleR = predefined.Where(x => x.Name == math.Math.ValueRight).FirstOrDefault();

            if (varbleL != null)
            {
                left = ASMx86_16BIT.UpdateName(varbleL.Name);
            }

            if (varbleR != null)
            {
                right = ASMx86_16BIT.UpdateName(varbleR.Name);
            }

            if (op != PhiMath.Opperation.ArrayIndex)
            {
                if (varbleL != null)
                {
                    left = "[" + left + "]";
                }

                if (varbleR != null)
                {
                    right = "[" + right + "]";
                }
            }
            
            if(varbleR == null)
            {
                if (right == string.Empty)
                {
                    if (math.Math.MathOp == Defs.MathInc
                    || math.Math.MathOp == Defs.MathDec)
                    {
                        right = "1"; // for i++; and i--;
                    }
                }
                if (ParseMisc.HasMathOpperation(right))
                {
                    List<string> lines = new List<string>();

                    List<PhiMath> maths = ParseMisc.ParseMath(right);

                    if (maths.Count > 0)
                    {
                        hasOpperation = true;

                        foreach (PhiMath mth in maths)
                        {
                            Console.WriteLine(mth.RawValue);
                            lines.AddRange(BuildMathInstruct(mth, predefined));
                        }

                        Console.WriteLine("-------------------");
                        list.AddRange(lines);
                    }
                }
                else if (!ParseMisc.IsNumber(right))
                {
                    if (!right.Contains(Defs.curlyOpen))
                        right = ASMx86_16BIT.UpdateName(right);
                }
            }

            ConditionalPairs.ConditionType typ = ParseUtilities.MatchesCondition(math.Math.MathOp);
            string val = ConvertConditional(typ);

            if (typ == ConditionalPairs.ConditionType.None)
            {
                if(op != PhiMath.Opperation.None)
                {
                    if(op == PhiMath.Opperation.Plus)
                    {
                        /*
                        * 
                        *   START OF PLUS
                        * 
                        */
                        if (left == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEaxMath32 // pop into eax
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                              list,
                              Defs.replaceValueStart,
                              left // mov left into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEaxMath32 // mov left into ebx
                            );
                        }

                        if (right == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEbxMath32 // pop into ebx
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               right // mov right into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEbxMath32 // mov right into ebx
                            );
                        }

                        list.AddRange(ASMx86_16BIT.BIT32x86_AddMath);
                        list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               ASMx86_16BIT.registerEbxMath32 // push into eax
                        );

                        list.AddRange(ASMx86_16BIT.BIT32x86_PUSH);
                        list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               ASMx86_16BIT.registerEaxMath32 // push into eax
                        );
                        /*
                        * 
                        *   END OF PLUS
                        * 
                        */
                    }
                    else if(op == PhiMath.Opperation.Minus)
                    {
                        /*
                         * 
                         *   START OF MINUS
                         * 
                         */
                        if (left == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEaxMath32 // pop into eax
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                              list,
                              Defs.replaceValueStart,
                              left// mov left into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEaxMath32 // mov left into ebx
                            );
                        }

                        if (right == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEbxMath32 // pop into ebx
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               right // mov right into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEbxMath32 // mov right into ebx
                            );
                        }

                        list.AddRange(ASMx86_16BIT.BIT32x86_SubMath);
                        list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               ASMx86_16BIT.registerEbxMath32 // push into eax
                        );

                        list.AddRange(ASMx86_16BIT.BIT32x86_PUSH);
                        list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               ASMx86_16BIT.registerEaxMath32 // push into eax
                        );

                        /*
                        * 
                        *   END OF MINUS
                        * 
                        */
                    }
                    else if (op == PhiMath.Opperation.Multiply)
                    {
                        /*
                        * 
                        *   START OF MULTIPLY
                        * 
                        */
                        if (left == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEaxMath32 // pop into eax
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                              list,
                              Defs.replaceValueStart,
                              left// mov left into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEaxMath32 // mov left into ebx
                            );
                        }

                        if (right == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEcxMath32 // pop into ebx
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               right // mov right into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEcxMath32 // mov right into ebx
                            );
                        }

                        list.AddRange(ASMx86_16BIT.BIT32x86_MulMath);

                        list.AddRange(ASMx86_16BIT.BIT32x86_PUSH);
                        list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               ASMx86_16BIT.registerEaxMath32 // push into eax
                        );


                        /*
                        * 
                        *   END OF MULTIPLY
                        * 
                        */
                    }
                    else if (op == PhiMath.Opperation.Divide)
                    {

                        /*
                        * 
                        *   START OF DIVIDE
                        * 
                        */
                        if (left == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEbxMath32 // pop into eax
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                              list,
                              Defs.replaceValueStart,
                              left// mov left into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEaxMath32 // mov left into ebx
                            );
                        }

                        if (right == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEaxMath32 // pop into ebx
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               right // mov right into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEbxMath32 // mov right into ebx
                            );
                        }

                        list.AddRange(ASMx86_16BIT.BIT32x86_DivMath);

                        list.AddRange(ASMx86_16BIT.BIT32x86_PUSH);
                        list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               ASMx86_16BIT.registerEaxMath32 // push into eax
                        );

                         /*
                          * 
                          *   END OF DIVIDE
                          * 
                          */
                    }
                    else if (op == PhiMath.Opperation.Mod)
                    {
                       /*
                        * 
                        *   START OF MOD
                        * 
                        */
                        if (left == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEbxMath32 // pop into eax
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                              list,
                              Defs.replaceValueStart,
                              left// mov left into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEaxMath32 // mov left into ebx
                            );
                        }

                        if (right == Defs.replaceLinkedValue)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEaxMath32 // pop into ebx
                            );
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               right // mov right into ebx
                            );

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               ASMx86_16BIT.replaceVarName,
                               ASMx86_16BIT.registerEbxMath32 // mov right into ebx
                            );
                        }

                        list.AddRange(ASMx86_16BIT.BIT32x86_DivMath);

                        list.AddRange(ASMx86_16BIT.BIT32x86_PUSH);
                        list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               ASMx86_16BIT.registerEdxMath32 // push into eax
                        );

                       /*
                        * 
                        *   END OF MOD
                        * 
                        */
                    }

                    #region Double Sign

                    if (op == PhiMath.Opperation.PlusEquals)
                    {
                        list.AddRange(ASMx86_16BIT.BIT32x86_AddVariable);

                        list = ASMx86_16BIT.ReplaceValue(
                            list,
                            ASMx86_16BIT.replaceVarName,
                            left);

                        list = ASMx86_16BIT.ReplaceValue(
                           list,
                           Defs.replaceValueStart,
                           right);
                    }
                    else if(op == PhiMath.Opperation.MinusEquals)
                    {
                        list.AddRange(ASMx86_16BIT.BIT32x86_SubVariable);

                        list = ASMx86_16BIT.ReplaceValue(
                            list,
                            ASMx86_16BIT.replaceVarName,
                            left);

                        list = ASMx86_16BIT.ReplaceValue(
                           list,
                           Defs.replaceValueStart,
                           right);
                    }
                    else if (op == PhiMath.Opperation.DivideEquals)
                    {
                        list.AddRange(ASMx86_16BIT.BIT32x86_DivVariable);

                        list = ASMx86_16BIT.ReplaceValue(
                            list,
                            ASMx86_16BIT.replaceVarName,
                            left);

                        list = ASMx86_16BIT.ReplaceValue(
                           list,
                           Defs.replaceValueStart,
                           right);
                    }
                    else if (op == PhiMath.Opperation.MultiplyEquals)
                    {
                        list.AddRange(ASMx86_16BIT.BIT32x86_MulVariable);

                        list = ASMx86_16BIT.ReplaceValue(
                            list,
                            ASMx86_16BIT.replaceVarName,
                            left);

                        list = ASMx86_16BIT.ReplaceValue(
                           list,
                           Defs.replaceValueStart,
                           right);
                    }
                    else if (op == PhiMath.Opperation.ModEquals)
                    {
                        list.AddRange(ASMx86_16BIT.BIT32x86_ModVariable);

                        list = ASMx86_16BIT.ReplaceValue(
                            list,
                            ASMx86_16BIT.replaceVarName,
                            left);

                        list = ASMx86_16BIT.ReplaceValue(
                           list,
                           Defs.replaceValueStart,
                           right);
                    }

                    #endregion

                    if(op == PhiMath.Opperation.ArrayIndex)
                    {


                        if (varbleL != null)
                        {
                            if (varbleL.varType == Inside.VariableTypeInt)
                            {
                                list.AddRange(ASMx86_16BIT.BIT32x86_POP);
                                list = ASMx86_16BIT.ReplaceValue(list, Defs.replaceValueStart, ASMx86_16BIT.registerEaxMath32);

                                int num = NumberOfBytesInType(varbleL.varType);

                                list.AddRange(ASMx86_16BIT.ASMx86_MOV);
                                list = ASMx86_16BIT.ReplaceValue(list, ASMx86_16BIT.replaceVarName, ASMx86_16BIT.registerEcxMath32);

                                list = ASMx86_16BIT.ReplaceValue(list, Defs.replaceValueStart, num.ToString());

                                list.AddRange(ASMx86_16BIT.BIT32x86_MulMath);

                                list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                                list = ASMx86_16BIT.ReplaceValue(list, ASMx86_16BIT.replaceVarName, ASMx86_16BIT.registerEbxMath32);

                                list = ASMx86_16BIT.ReplaceValue(list, Defs.replaceValueStart, ASMx86_16BIT.registerEaxMath32);
                            }
                            else
                            {
                                list.AddRange(ASMx86_16BIT.BIT32x86_POP);
                                list = ASMx86_16BIT.ReplaceValue(list, Defs.replaceValueStart, ASMx86_16BIT.registerEbxMath32);
                            }
                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);
                            list = ASMx86_16BIT.ReplaceValue(list, Defs.replaceValueStart, ASMx86_16BIT.registerEbxMath32);
                        }

                        list.AddRange(ASMx86_16BIT.BIT32x86_RetrieveArrayValue);
                        list = ASMx86_16BIT.ReplaceValue(list, Defs.replaceValueStart, left);

                        list.AddRange(ASMx86_16BIT.BIT32x86_PUSH);
                        list = ASMx86_16BIT.ReplaceValue(list, Defs.replaceValueStart, ASMx86_16BIT.registerEaxMath32);

                        //needs to push value to stack and then pull into array 
                    }
                }
            }
            else
            {
                if (typ == ConditionalPairs.ConditionType.JumpIfEqual)
                {
                    if (hasOpperation)
                    {
                        list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                        list = ASMx86_16BIT.ReplaceValue(
                            list, 
                            Defs.replaceValueStart, 
                            ASMx86_16BIT.registerEaxMath32
                        );

                        list.AddRange(ASMx86_16BIT.ASMx86_MOV);

                        list = ASMx86_16BIT.ReplaceValue(
                            list,
                            ASMx86_16BIT.replaceVarName,
                            left);

                        list = ASMx86_16BIT.ReplaceValue(
                          list,
                          Defs.replaceValueStart,
                          ASMx86_16BIT.registerEaxMath32);
                    }
                    else
                    {
                        // if right is empty dont try to set it because its already in eax
                        if (math.Math.ValueRight == string.Empty)
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_POP);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                Defs.replaceValueStart,
                                ASMx86_16BIT.registerEaxMath32
                            );

                            list.AddRange(ASMx86_16BIT.BIT32x86_SetVariableFromStack);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                ASMx86_16BIT.replaceVarName,
                                left);

                        }
                        else
                        {
                            list.AddRange(ASMx86_16BIT.BIT32x86_SetVariable);

                            list = ASMx86_16BIT.ReplaceValue(
                                list,
                                ASMx86_16BIT.replaceVarName,
                                left);

                            list = ASMx86_16BIT.ReplaceValue(
                               list,
                               Defs.replaceValueStart,
                               right);
                        }
                    }
                }
            }

            return list;
        }


        public static BuildPair BuildMath(List<string> Code, List<string> SubCode, List<PhiMath> maths, PhiClass cls, List<PhiVariable> predefined)
        {
            BuildPair pair = new BuildPair();

            foreach (PhiMath math in maths)
            {

            }

            return pair;
        }

        //public static 
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

        public static BuildPair BuildInstructIf(List<string> Code, List<string> SubCode, string Name, PhiInstruct instruct, PhiClass cls, List<PhiVariable> predefined)
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

            if (condit == null)
            {
                string vnme = ParseMisc.ExtractStandAloneVarName(
                    instruct.Value, 
                    instruct.ContentLabels, 
                    Defs.ConditionalClosureCharacters);

                string leftv = vnme.Trim();

                condit = new PhiConditional
                {
                    PhiConditionalPairs = new List<ConditionalPairs>
                    {
                        new ConditionalPairs
                        {
                            LeftValue = leftv,
                            RightValue = Defs.True, // maybe change to 1 later for true
                            opperation = ConditionalPairs.ConditionOpperation.Comparision,
                            type = ConditionalPairs.ConditionType.JumpIfEqual
                        }
                    }
                    
                };
            }

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

                    Inside vtypeComp = Inside.None;

                    if(vl != null)
                    {
                        if(vl.varType == Inside.VariableTypeBln)
                        {
                            vtypeComp = vl.varType;
                        }
                    }

                    if (vtypeComp == Inside.VariableTypeBln || vtypeComp == Inside.VariableTypeByt)
                    {
                        buildcode.AddRange(ASMx86_16BIT.InstructIfCheck_BITS8);
                    }
                    else
                    {

                        buildcode.AddRange(ASMx86_16BIT.InstructIfCheck_BITS16);
                    }

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
                CodeBase = Code,
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

                //if (condit != null)
                //{
                //    jmpCon = ConvertConditional(condit);
                //}

                //if (mths != null)
                //{
                //    incVal = ConvertIncrement(mths);
                //}

                //loopStart.AddRange(ASMx86_16BIT.InstructWhileContent_BITS16);

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //   loopStart,
                //   ASMx86_16BIT.replaceLoopContentName,
                //   nameCont
                //);

                //loopStart.AddRange(ASMx86_16BIT.ReplaceValue(
                //    ASMx86_16BIT.InstructWhileStart_BITS16,
                //    Defs.replaceValueStart,
                //    vStart
                //));

                //PhiVariable? startValue = instruct.Variables.Where(x => ASMx86_16BIT.UpdateName(x.Name) == vStart).FirstOrDefault();

                //if (startValue != null)
                //{
                //    loopStart = ASMx86_16BIT.ReplaceValue(
                //       loopStart,
                //       ASMx86_16BIT.replaceLoopStart,
                //       startValue.ValueRaw
                //   );
                //}

                //loopStart.AddRange(ASMx86_16BIT.InstructWhileCheck_BITS16);

                //#region LOOP CHECK

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //    loopStart,
                //    ASMx86_16BIT.replaceLoopName,
                //    name
                //);

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //    loopStart,
                //    Defs.replaceValueStart,
                //    vStart
                //);

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //    loopStart,
                //    ASMx86_16BIT.replaceLoopLimit,
                //    vLimit
                //);

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //    loopStart,
                //    ASMx86_16BIT.replaceLoopCondition,
                //    jmpCon
                //);

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //    loopStart,
                //    ASMx86_16BIT.replaceLoopIncrement,
                //    incVal
                //);

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //    loopStart,
                //    ASMx86_16BIT.replaceLoopContentName,
                //    nameCont
                //);

                //#endregion

                //loopStart.AddRange(ASMx86_16BIT.InstructWhileDone_BITS16);



                //#region LOOP END

                //// loop content gets added after sub-instructs are built

                //loopStart = ASMx86_16BIT.ReplaceValue(
                //   loopStart,
                //   Defs.replaceValueStart,
                //   vStart
                //);

                //#endregion
                //Code = ASMx86_16BIT.MergeValues(
                //    Code,
                //    ASMx86_16BIT.InstructWhileStart_BITS16,
                //    Defs.replaceIncludes
                //);
   

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
                string vname = ASMx86_16BIT.UpdateName(v.Name);

                if (v.varType == Inside.VariableTypeStr)
                {
                    // adds function call code
                    subCde = ASMx86_16BIT.MergeValues(subCde, ASMx86_16BIT.InstructLogString_BITS16, Defs.replaceCodeStart);
                }
                else if(v.varType == Inside.VariableTypeInt)
                {
                    vname = "[" + vname + "]";
                    // adds function call code
                    subCde = ASMx86_16BIT.MergeValues(subCde, ASMx86_16BIT.InstructLogInt_BITS16, Defs.replaceCodeStart);
                }
                else if (v.varType == Inside.VariableTypeBln)
                {
                    vname = "[" + vname + "]";
                    // adds function call code
                    subCde = ASMx86_16BIT.MergeValues(subCde, ASMx86_16BIT.InstructLogByte_BITS16, Defs.replaceCodeStart);
                }
                else if (v.varType == Inside.VariableTypeByt)
                {
                    vname = "[" + vname + "]";
                    // adds function call code
                    subCde = ASMx86_16BIT.MergeValues(subCde, ASMx86_16BIT.InstructLogByte_BITS16, Defs.replaceCodeStart);
                }

                if (!v.preExisting) values.Add(ASMx86_16BIT.VarTypeConvert(v));

                List<string> parts = ParseMisc.ExtractArrayParts(v.ValueRaw);

                if (parts.Count > 1)
                {
                    string arr = ConvertArrayToASM(v.ValueRaw, predefined);

                    subCde = ASMx86_16BIT.ReplaceValue(subCde, Defs.replaceValueStart, arr);
                }
                else
                {
                    subCde = ASMx86_16BIT.ReplaceValue(subCde, Defs.replaceValueStart, vname);
                }
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

        public static List<string> AutoInclude_BITS16(List<string> Code, PhiClass cls)
        {
            if (cls.Includes.Contains(PhiInclude.Sectors))
            {
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_SectorPrep, Defs.replaceIncludes);
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_JumpSectorTwo, Defs.replaceIncludes);
            }

            if (cls.Includes.Contains(PhiInclude.Text))
            {
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.PrintInt_x86BITS16, Defs.replaceIncludes);
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.PrintLog_x86BITS16, Defs.replaceIncludes);
            }

            if (cls.Includes.Contains(PhiInclude.Keyboard))
            {
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.AskInput_x86BITS16, Defs.replaceIncludes);
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_WaitForKeyPress, Defs.replaceIncludes);
                if (cls.Inherit == Defs.OS16BitSectorTwo)
                {
                    Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.OS_KeyboardHandler, Defs.replaceIncludes);
                    Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_KeyboardSetup, Defs.replaceIncludes);

                    bool overwriteEvent = cls.Methods.Where(
                       x => x.Name == ASMx86_16BIT.incKeyboardEvent
                       ).FirstOrDefault() != null ? true : false;

                    if (!overwriteEvent)
                    {
                        Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_KeyboardEvent, Defs.replaceIncludes);
                    }

                    Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_ScanKeyTable, Defs.replaceVarStart);
                    Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.KeyCodeValue, Defs.replaceVarStart);
                }
            }

            if (cls.Includes.Contains(PhiInclude.Timer))
            {
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_TimerConstants, Defs.replaceConstStart);

                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_Interupt, Defs.replaceIncludes);
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_ProgramableInteruptTimer, Defs.replaceIncludes);
                
                bool overwriteEvent = cls.Methods.Where(
                    x => x.Name == ASMx86_16BIT.incTimerEvent
                    ).FirstOrDefault() != null ? true : false;

                if (!overwriteEvent)
                {
                    Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_TimerEvent, Defs.replaceIncludes);
                }
            }           
            
            if (cls.Includes.Contains(PhiInclude.Graphics))
            {            
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_VideoMode, Defs.replaceIncludes);
                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_DrawRectangle, Defs.replaceIncludes);

                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_DrawConstants, Defs.replaceConstStart);

                Code = ASMx86_16BIT.MergeValues(Code, ASMx86_16BIT.BIT16x86_DrawVariables, Defs.replaceVarStart);
            }

            return Code;
        }

        public static List<string> ConvertVarsToASM(List<PhiVariable> phiVariables)
        {
            List<string> vals = new List<string>();

            foreach (PhiVariable vbl in phiVariables)
            {
                string build = ASMx86_16BIT.UpdateName(vbl.Name);

                build = ASMx86_16BIT.VarTypeConvert(vbl);

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

                return ConvertConditional(tp);
            }

            return cond;
        }

        public static string ConvertArrayToASM(string value, List<PhiVariable> predefined)
        {
            List<string> parts = ParseMisc.ExtractArrayParts(value);
            
            string result = string.Empty;

            string orgn = parts.First();
            string add = parts.Last();

            int mult = 1;

            PhiVariable? varOrg = predefined.Where(x => x.Name == orgn).FirstOrDefault();

            PhiVariable? varAdd = predefined.Where(x => x.Name == add).FirstOrDefault();

            if (varOrg != null)
            {
                orgn = ASMx86_16BIT.UpdateName(orgn);

                mult = NumberOfBytesInType(varOrg.varType);
            }

            if (varAdd != null)
            {
                add = ASMx86_16BIT.UpdateName(add);

                result = "[" + orgn + " + " + add + "]";
            }
            
            if(mult != 1)
            {
                int ad = 0;

                if (int.TryParse(add, out ad))
                {
                    result = "[" + orgn + " + " + (ad * mult) + "]";
                }
                else
                {
                    result = "[" + orgn + " + " + add + "]";
                }
            }

            return result;
        }

        public static int NumberOfBytesInType(Inside vartype)
        {
            if (vartype == Inside.VariableTypeInt) return 4;

            return 1;
        }

        public static string ConvertConditional(ConditionalPairs.ConditionType tp)
        {
            string cond = string.Empty;

            if (tp == ConditionalPairs.ConditionType.JumpIfLessEqual)
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

            return cond;
        }
    }
}

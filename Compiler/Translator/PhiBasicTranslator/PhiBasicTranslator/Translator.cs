using PhiBasicTranslator.Structure;
using System.ComponentModel.Design;
using System.Diagnostics.Metrics;
using PhiBasicTranslator.ParseEngine;
using System.ComponentModel;
using System;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Reflection.Metadata;
using System.Net.Mime;
using PhiBasicTranslator.TranslateUtilities;
using Newtonsoft.Json.Linq;
using static PhiBasicTranslator.Structure.ConditionalPairs;

namespace PhiBasicTranslator
{
    public class Translator
    {
        public string FileContent = string.Empty;
        public Translator() { }
        public Translator(string file)
        {
            if (File.Exists(file))
            {
                string content = File.ReadAllText(file);

                FileContent = content;

                TranslateCode(content);
            }
            else
            {
                Console.WriteLine("File Doesn't Exist");
            }
        }

        public void Highlight(string content)
        {

        }

        public PhiCodebase TranslateFile(string file)
        {
            PhiCodebase code = new PhiCodebase();

            if (File.Exists(file))
            {
                string content = File.ReadAllText(file);


                code.ClassList = TranslateCode(content);
            }

            return TranslateToX86.ToX86(code);
        }

        public List<PhiClass> TranslateCode(string content)
        {
            string name = "";
            string vnme = "";
            string mnme = "";
            string vval = "";
            string inherit = "";

            ContentProfile profile = ParseUtilities.ProfileContent(content);

            List<PhiClass> classes = new List<PhiClass>();

            PhiClass current = new PhiClass();
            PhiMethod method = new PhiMethod();
            PhiVariable varble = new PhiVariable();
            //PhiVariable varble = new PhiVariable();

            Inside last = Inside.None;
            Inside inside = Inside.None;
            
            int typeCount = 0;

            bool insideMethod = false;

            for (int i = 0; i < content.Length; i++)
            {
                string letter = content[i].ToString();
                string prev = string.Empty;

                if(i > 0) prev = content[i - 1].ToString();

                inside = profile.ContentInside[i];

                bool match = last == inside;

                if (!match)
                {
                    #region Class Type Set
                    if (inside == Inside.PhiClassStart)
                        current.Type = PhiType.PHI;
                    else if (inside == Inside.ArmClassStart)
                        current.Type = PhiType.ARM;
                    else if (inside == Inside.AsmClassStart)
                        current.Type = PhiType.ASM;
                    #endregion


                    if(inside == Inside.Opperation || inside == Inside.Conditonal)
                    {
                        int ic = ParseMisc.GetPrevIndexOfAny(
                            content, 
                            Defs.ConditionalStartCharacters, 
                            i);

                        int nx = ParseMisc.GetNextIndexOfAny(
                            content,
                            Defs.ConditionalStartCharacters,
                            i);

                        if (ic > 0 && nx > 0)
                        {
                            string cut = content.Substring(ic, nx - ic + 1);
                            Console.WriteLine(cut);

                            i = nx + 1; //set to instead of add

                            //if (i < content.Length)
                            //{
                            //    string next = content.Substring(i);
                            //}

                            List<Inside> contentL = new List<Inside>();

                            for (int m = ic; m <= nx; m++) 
                                contentL.Add(profile.ContentInside[m]);

                            PhiInstruct temp = new PhiInstruct
                            {
                                Name = "Raw",
                                Value = cut,
                                ContentLabels = contentL
                            };

                            PhiInstruct maths = TranslateSubInstructs(temp, current);

                            foreach (PhiInstruct mth in maths.Instructs)
                            {
                                if (method.Name != string.Empty)
                                {
                                    method.Instructs.Add(mth.Copy());
                                }
                                else
                                {
                                    current.Instructs.Add(mth.Copy());
                                }
                            }
                        }
                    }

                    if (inside == Inside.MethodOpen)
                    {
                        insideMethod = true;

                        /*
                         * eventually change over to getnextphimethod
                         * 
                        string cut = content.Substring(i);

                        PhiMethod mthd = GetNextPhiMethod(cut);
                        */
                    }

                    if (last == Inside.MethodEnd && insideMethod)
                    {
                        insideMethod = false;

                        int en = content.IndexOf(Defs.squareClose, i);

                        if (en > 0)
                        {
                            string str = string.Empty;

                            for (int j = i + 1; j < en; j++)
                            {
                                str += content[j];
                            }

                            method.End = str.Trim();
                        }

                        current.Methods.Add(method.Copy());

                        method = new PhiMethod();
                    }
                    #region Instructs

                    PhiInstruct instr = ExtractInstruct(inside, content, prev, profile.ContentInside, i, current);

                    if (instr.Name != string.Empty)
                    {
                        i += instr.Value.Length; // check for accuracy

                        current.AddInclude(instr);

                        if (method.Name != string.Empty)
                        {
                            if (ParseMisc.IsElsePair(method, instr))
                            {
                                method.Instructs.Last().Instructs.Add(instr.Copy());
                            }
                            else
                            {
                                method.Instructs.Add(instr.Copy());
                            }
                        }
                        else
                        {

                            if (ParseMisc.IsElsePair(current, instr))
                            {
                                current.Instructs.Last().Instructs.Add(instr.Copy());
                            }
                            else
                            {
                                current.Instructs.Add(instr.Copy());
                            }
                        }
                    }
                }

                if (inside == Inside.ClassName)
                {
                    name += letter;
                }

                if (inside == Inside.ClassInherit)
                {
                    if (current.Name == string.Empty)
                    {
                        current.Name = ParseUtilities.ClearLabel(name, Defs.Alphabet);
                        name = string.Empty;
                    }
                    
                    inherit += letter;
                }

                if(inside == Inside.CurlyOpen)
                {
                    if (current.Name == string.Empty)
                    {
                        current.Name = ParseUtilities.ClearLabel(name, Defs.Alphabet);
                        name = string.Empty;
                    }

                    if (current.Inherit == string.Empty)
                    {
                        current.Inherit = ParseUtilities.ClearLabel(inherit, Defs.Alphabet);    
                        inherit = string.Empty;
                    }

                    if(current.Type == PhiType.ASM || current.Type== PhiType.ARM)
                    {
                        for(int j = i + 1; j < content.Length; j++)
                        {
                            if (profile.ContentInside[j] == Inside.ClassClose)
                            {
                                break;
                            }
                            current.RawContent += content[j];
                        }

                        i += current.RawContent.Length + 2; // +2 is for the {} curly brackets

                        classes.Add(current.Copy());
                        current = new PhiClass();
                    }
                }
                #endregion

                #region Variables

                if (!match)
                {
                    #region var type set
                    if (inside == Inside.VariableTypeBln)
                    {
                        varble.varType = Inside.VariableTypeBln;
                    }
                    else if (inside == Inside.VariableTypeByt)
                    {
                        varble.varType = Inside.VariableTypeByt;
                    }
                    else if (inside == Inside.VariableTypeDec)
                    {
                        varble.varType = Inside.VariableTypeDec;
                    }
                    else if (inside == Inside.VariableTypeFin)
                    {
                        varble.varType = Inside.VariableTypeFin;
                    }
                    else if (inside == Inside.VariableTypeInt)
                    {
                        varble.varType = Inside.VariableTypeInt;
                    }
                    else if (inside == Inside.VariableTypeStr)
                    {
                        varble.varType = Inside.VariableTypeStr;
                    }
                    else if (inside == Inside.VariableTypeVar)
                    {
                        varble.varType = Inside.VariableTypeVar;
                    }
                    #endregion

                    if (last == Inside.VariableName)
                    {
                        varble.Name = ParseUtilities.ClearLabel(vnme, Defs.Alphabet);
                        vnme = string.Empty;
                    }

                    if (last == Inside.VariableEnd)
                    {
                        varble.ValueRaw = vval;

                        List<string> vals = ParseMisc.ExtractSubValues(varble.ValueRaw);

                        if(vals.Count > 0)
                        {
                            varble.Values = vals;
                        }

                        vval = string.Empty;

                        if (insideMethod)
                        {
                            method.Variables.Add(varble.Copy());
                        }
                        else
                        {
                            current.Variables.Add(varble.Copy());
                        }

                        varble = new PhiVariable();
                    }

                    if (last == Inside.MethodName)
                    {
                        method.Name = ParseMisc.ClearMethodName(mnme);
                        mnme = string.Empty;
                    }


                    if (inside == Inside.ClassClose)
                    {
                        classes.Add(current.Copy());

                        current = new PhiClass();
                    }
                }

 
                    if (inside == Inside.VariableName)
                    {
                        vnme += letter;
                    }

                    if (inside == Inside.VariableValue || inside == Inside.String)
                    {
                        vval += letter;
                    }


                    #endregion

                    #region Methods

                    if (inside == Inside.MethodName)
                    {
                        mnme += letter;
                    }

                    #endregion
                

                last = inside;
            }

            return classes;
        }

        public List<PhiClass> PostProcess(List<PhiClass> classes)
        {
            List<PhiClass> cls = new List<PhiClass>();

            for (int i = 0; i < classes.Count; i++)
            {
                PhiClass cs = classes[i];


            }

            return cls;
        }
        public PhiInstruct ExtractInstruct(Inside inside, string content, string prev, Inside[] profile, int i, PhiClass current)
        {
            PhiInstruct instruct = new PhiInstruct();
            //you will need to do subinstruct parsing
            if (inside == Inside.Instruct && instruct.Name == string.Empty)
            {
                if (i > 1)
                {
                    string cut = content.Substring(i);
                    string nme = ParseUtilities.MatchesInstruct(cut, prev);

                    if (nme != string.Empty)
                    {
                        instruct.Name = nme;

                        if (Defs.instructContainers.Contains(instruct.Name))
                        {
                            instruct.InType = Inside.InstructContainer;
                            int len = MeasureContainerInstruct(content, profile, i);

                            string instructValue = content.Substring(i, len);

                            instruct.Value = instructValue;
                        }
                        else
                        {
                            instruct.InType = Inside.VariableTypeMixed;
                            int len = MeasureInstruct(content, profile, i);

                            string instructValue = content.Substring(i, len);

                            instruct.Value = instructValue;
                        }

                        for (int j = i; j < i + instruct.Value.Length; j++)
                        {
                            instruct.ContentLabels.Add(profile[j]);
                        }

                        instruct = TranslateSubInstructs(instruct, current);
                    }
                }
            }

            return instruct;
        }
        public PhiInstruct TranslateSubInstructs(PhiInstruct instr, PhiClass current)
        {
            if (instr.Name != string.Empty)
            {
                bool isLoop = instr.Name == Defs.instWhile; // true if while

                Inside last = instr.ContentLabels.First();

                PhiVariable varble = new PhiVariable();
                PhiInstruct subInstr = new PhiInstruct();

                bool startParams = false;

                string prev = string.Empty;
                string cond = string.Empty;
                string cRaw = string.Empty;

                ConditionalPairs.ConditionOpperation conditionOpperation 
                    = ConditionalPairs.ConditionOpperation.Comparision;

                for (int i = 0; i < instr.Value.Length; i++)
                {
                    Inside inside = instr.ContentLabels[i];
                    string letter = instr.Value[i].ToString();

                    bool match = inside == last;

                    if (!match)
                    {
                        if (inside == Inside.VariableTypeInt)
                        {
                            varble.varType = inside;
                        }
                        else if (inside == Inside.VariableName)
                        {
                            varble.Name += letter;
                        }
                        else if (inside == Inside.Colon && instr.Name != Defs.instWhile)
                        {
                            startParams = true;
                        }
                        else if (inside == Inside.VariableEnd)
                        {
                            List<string> vals = ParseMisc.ExtractSubValues(varble.ValueRaw);

                            if (vals.Count > 0)
                            {
                                varble.Values = vals;
                            }

                            instr.Variables.Add(varble);
                            //Preexisting?
                            varble = new PhiVariable();
                        }
                        else if (inside == Inside.StandAloneInt)
                        {
                            varble.varType = Inside.StandAloneInt;
                            varble.Name = Defs.replaceUnsetName;

                            for (int j = i; j < instr.Value.Length; j++)
                            {
                                if (instr.ContentLabels[j] != Inside.StandAloneInt)
                                {
                                    break;
                                }
                                else
                                {
                                    varble.ValueRaw += instr.Value[j];
                                }
                            }


                            List<string> vals = ParseMisc.ExtractSubValues(varble.ValueRaw);

                            if (vals.Count > 0)
                            {
                                varble.Values = vals;
                            }

                            instr.Variables.Add(varble);
                        }
                        else if (inside == Inside.Instruct)
                        {
                            string cut = instr.Value.Substring(i);
                            string instName = ParseUtilities.MatchesInstruct(cut, prev);

                            if (instName != string.Empty)
                            {
                                // instructContainers remember to work on unified parsing
                                if (Defs.instructContainers.Contains(instName))
                                {
                                    PhiInstruct subInstruct = ExtractInstruct(inside, instr.Value, prev, instr.ContentLabels.ToArray(), i, current);

                                    current.AddInclude(subInstruct);

                                    if (ParseMisc.IsElsePair(instr, subInstruct))
                                    {
                                        instr.Instructs.Last().Instructs.Add(subInstruct.Copy());
                                    }
                                    else
                                    {
                                        instr.Instructs.Add(subInstruct.Copy());
                                    }

                                    i += subInstruct.Value.Length;
                                }
                                else
                                {
                                    PhiInstruct subInstruct = ExtractInstruct(inside, instr.Value, prev, instr.ContentLabels.ToArray(), i, current);
                                    
                                    current.AddInclude(subInstruct);

                                    if (ParseMisc.IsElsePair(instr, subInstruct))
                                    {
                                        instr.Instructs.Last().Instructs.Add(subInstruct.Copy());
                                    }
                                    else
                                    {
                                        instr.Instructs.Add(subInstruct.Copy());
                                    }

                                    i += subInstruct.Value.Length;
                                }
                            }
                        }
                    }
                    else if (varble.Name != string.Empty)
                    {
                        if (inside == Inside.VariableName)
                            varble.Name += letter;
                    }

                    if (startParams)
                    {
                        string parms = instr.Value.Substring(i + 1).Trim();

                        ContentProfile subprf = ParseUtilities.ProfilePrepare(parms);

                        List<PhiVariable> vars = new List<PhiVariable>();

                        string vName = "";

                        for (int j = 0; j < parms.Length; j++)
                        {
                            if (subprf.ContentInside[j] != Inside.InstructClose)
                            {
                                if (Defs.TabSpaceClosureCharacters.Contains(parms[j])
                                    && subprf.ContentInside[j] != Inside.String)
                                {
                                    vName = vName.Replace(";", "");

                                    if (vName != string.Empty)
                                    {
                                        vars.Add(new PhiVariable
                                        {
                                            varType = Inside.VariableTypeInsert,
                                            Name = vName
                                        });

                                        vName = "";
                                    }
                                }
                                else
                                {
                                    vName += parms[j];
                                }

                            }


                        }

                        vName = vName.Replace(";", "");

                        if (vName.Replace(" ", string.Empty) != string.Empty)
                        {
                            vars.Add(new PhiVariable
                            {
                                varType = Inside.VariableTypeInsert,
                                Name = vName.Replace(Defs.VariableSetClosure, string.Empty)
                            });
                        }

                        instr.Variables.AddRange(vars);

                        startParams = false;
                    }

                    if (inside == Inside.Opperation)
                    {
                        string cr = string.Empty;

                        int bgn = 0;
                        int end = 0;

                        #region clip

                        for (int j = i; j < instr.Value.Length; j++)
                        {
                            if (Defs.ConditionalClosureCharacters.Contains(instr.Value[j]))
                            {
                                end = j;
                                break;
                            }
                        }

                        for (int j = i; j >= 0; j--)
                        {
                            string ltr = instr.Value[j].ToString();

                            if (Defs.ConditionalClosureCharacters.Contains(ltr)
                                || instr.ContentLabels[j] == Inside.Instruct)
                            {
                                bgn = j;
                                break;
                            }
                        }
                        #endregion

                        cr = instr.Value.Substring(bgn + 1, end - bgn - 1).Trim();

                        i = end - 1;

                        List<PhiMath> maths = ExtractArraySubMaths(cr);

                        if (maths.Count > 0)
                        {
                            instr.Instructs.Add(new PhiInstruct
                            {
                                Name = Defs.instMath,
                                Maths = maths,
                                Value = cr
                            });
                        }
                        else
                        {

                            MathPair mpar = PhiMath.Parse(cr);

                            instr.Instructs.Add(new PhiInstruct
                            {
                                Name = Defs.instMath,
                                Value = cr,
                                Maths = new List<PhiMath>
                            {
                                new PhiMath
                                {
                                    Math = mpar,
                                    RawValue = cr
                                }
                            }
                            });
                        }

                    }

                    if (inside == Inside.Conditonal)
                    {
                        cond += letter;

                        string cr = string.Empty;

                        int bgn = 0;
                        int end = 0;

                        #region clip

                        for (int j = i; j < instr.Value.Length; j++)
                        {
                            string ltr = instr.Value[j].ToString();
                            Inside chek = instr.ContentLabels[j];

                            if (chek == Inside.Conditonal && j > i)
                            {
                                cond += ltr;
                            }

                            if (Defs.ConditionalClosureCharacters.Contains(ltr))
                            {
                                end = j;
                                break;
                            }
                        }

                        for (int j = i; j >= 0; j--)
                        {
                            string ltr = instr.Value[j].ToString();

                            Inside chek = instr.ContentLabels[j];

                            if (chek == Inside.Instruct)
                            {
                                conditionOpperation = ConditionalPairs.ConditionOpperation.Comparision;

                                bgn = j;
                                break;
                            }

                            if (Defs.ConditionalClosureCharacters.Contains(ltr))
                            {
                                if (ltr != "\t")
                                {
                                    conditionOpperation = ConditionalPairs.ConditionOpperation.AssignValue;

                                    bgn = j;
                                    break;
                                }
                            }

                        }
                        #endregion

                        cr = instr.Value.Substring(bgn + 1, end - bgn - 1).Trim();

                        if (cRaw == string.Empty)
                        {
                            cRaw = cr;
                        }

                        i = end - 1;
                    }

                    if (inside == Inside.VariableValue)
                    {
                        varble.ValueRaw += letter;
                    }

                    if (i > 0)
                    {
                        last = inside;
                        prev = letter;
                    }


                    if (cond != string.Empty)
                    {
                        string? select = Defs.OpperCompareList.Where(x => x == cond).FirstOrDefault();

                        if (select != null)
                        {
                            instr = AssessConditionals(instr, conditionOpperation, cRaw, cond);
                        }

                        cond = string.Empty;
                        cRaw = string.Empty;
                    }
                }
            }

            if (instr.Name == Defs.instWhile)
            {
                instr = ReorganizeLoop(instr);
            }

            return instr;
        }

        public PhiInstruct AssessConditionals(PhiInstruct instruct, ConditionOpperation opper, string contentRaw, string condition)
        {
            string left = string.Empty;
            string right = string.Empty;

            string[] two = contentRaw.Split(condition);

            if (two.Length == 2)
            {
                left = two[0].Trim();
                right = two[1].Trim();
            }

            ConditionalPairs.ConditionType ctyp = ConditionalPairs.ConditionType.None;

            if (instruct.Name == Defs.instWhile)
            {
                ctyp = ParseUtilities.MatchesInvertedCondition(condition);
            }
            else
            {
                ctyp = ParseUtilities.MatchesCondition(condition);
            }

            if (opper == ConditionalPairs.ConditionOpperation.Comparision)
            {
                instruct.Conditionals.Add(new PhiConditional
                {
                    PhiConditionalPairs = new List<ConditionalPairs>
                    {
                        new ConditionalPairs
                        {
                            type = ctyp,
                            LeftValue = left,
                            RightValue = right,
                            opperation = opper
                        }
                    },
                    RawValue = contentRaw
                });
            }
            else
            {
                if (right.Contains(Defs.squareClose) && right.Contains(Defs.squareOpen))
                {
                    List<PhiMath> maths = ExtractArraySubMaths(right);

                    instruct.Instructs.Add(new PhiInstruct
                    {
                        Name = Defs.instMath,
                        Maths = maths,
                        Value = contentRaw
                    });

                    instruct.Instructs.Add(new PhiInstruct
                    {
                        Name = Defs.instMath,
                        Maths = new List<PhiMath>
                        {
                            new PhiMath
                            {
                                Math = new MathPair
                                {
                                    MathOp = condition,
                                    ValueRight = string.Empty,
                                    ValueLeft = left,
                                },
                                RawValue = contentRaw
                            }
                        },
                        Value = contentRaw
                    });
                }
                else
                {
                    //instr.name for while
                    instruct.Instructs.Add(new PhiInstruct
                    {
                        Name = Defs.instMath,
                        Maths = new List<PhiMath>
                        {
                            new PhiMath
                            {
                                Math = new MathPair
                                {
                                    MathOp = condition,
                                    ValueRight = right,
                                    ValueLeft = left,
                                },
                                RawValue = contentRaw
                            }
                        },
                        Value = contentRaw
                    });
                }
            }

            return instruct;
        }

        public PhiInstruct ReorganizeLoop(PhiInstruct instruct)
        {
            if (instruct != null)
            {
                if (instruct.Name == Defs.instWhile)
                {
                    PhiInstruct finalInstruct = new PhiInstruct();
                    PhiInstruct finalInIF = new PhiInstruct(); 
                    PhiInstruct finalIter = new PhiInstruct();
                    PhiInstruct finalCall = new PhiInstruct();

                    for (int i = 0; i < instruct.Instructs.Count; i++)
                    {
                        PhiInstruct? mathComp = instruct.Instructs[i];

                        if (mathComp != null)
                        {
                            if(i == 0)
                            {
                                finalInIF = AssessConditionals(
                                    instruct.Instructs[i], 
                                    ConditionOpperation.Comparision, 
                                    instruct.Instructs[i].Value, 
                                    instruct.Instructs[i].Maths.First().Math.MathOp);

                                finalInIF = new PhiInstruct
                                {
                                    Name = Defs.instIf,
                                    Conditionals = finalInIF.Conditionals,
                                    Content = finalInIF.Content,
                                    ContentLabels = finalInIF.ContentLabels,
                                    Value = finalInIF.Value,
                                    InType = Inside.InstructContainer
                                };
                            }
                            else if (i == 1)
                            {
                                finalIter = instruct.Instructs[i];
                            }
                            else
                            {
                                finalInIF.Instructs.Add(instruct.Instructs[i]);
                            }
                        }
                    }

                    finalInIF.Instructs.Add(finalIter);

                    string val = "call " + ASMx86_16BIT.replaceLoopContentName + Defs.VariableSetClosure;

                    //ContentProfile cnt = ParseUtilities.ProfileContent(val);

                    List<Inside> cinside = new List<Inside>();

                    foreach(char ci in val)
                    {
                        cinside.Add(Inside.None);
                    }

                    finalInIF.Instructs.Add(new PhiInstruct
                    {
                        Name = Defs.instCall,
                        InType = Inside.Instruct,
                        Value = val,
                        ContentLabels = cinside
                    });

                    finalInstruct.Instructs.Add(finalInIF);
                    finalInstruct.Variables = instruct.Variables;

                    finalInstruct.InType = Inside.InstructContainer;
                    finalInstruct.Name = Defs.instWhile;
                    finalInstruct.Value = instruct.Value;

                    return finalInstruct;
                }
            }

            return instruct;
        }
        public int MeasureInstruct(string content, Inside[] contentLabels, int begin)
        {
            int len = 0;

            string prev = string.Empty;

            if (content.Length == contentLabels.Length)
            {
                for (int i = begin; i < content.Length; i++)
                {
                    Inside inside = contentLabels[i];

                    len++;

                    if (inside == Inside.InstructClose || inside == Inside.SemiColon)
                    {
                        return len;
                    }

                    if (i > 0) { prev = content[i].ToString(); }
                }
            }

            return len;
        }
        public int MeasureContainerInstruct(string content, Inside[] contentLabels, int begin)
        {
            int len = 0;

            int depth = 0;

            string prev = string.Empty;

            if (content.Length == contentLabels.Length)
            {
                for (int i = begin; i < content.Length; i++)
                {
                    Inside inside = contentLabels[i];

                    len++;

                    string cut = content.Substring(i);

                    if (inside == Inside.Instruct)
                    {
                        string instName = ParseUtilities.MatchesInstruct(cut, prev);

                        if (instName != string.Empty)
                        {
                            // instructContainers remember to work on unified parsing
                            if (Defs.instructContainers.Contains(instName))
                            {
                                depth++;
                            }
                        }
                    }

                    if (inside == Inside.InstructClose)
                    {
                        depth--;

                        if (depth <= 0)
                        {
                            return len;
                        }
                    }

                    if(i > 0) { prev = content[i].ToString(); }
                }
            }

            return len;
        }

        public List<PhiMath> ExtractArraySubMaths(string content)
        {
            List<PhiMath> subMaths = new List<PhiMath>();

            List<int> squareorder = ParseUtilities.GetEnclosingDepth(content, Defs.squareOpen, Defs.squareClose);

            List<string> innerValues = new List<string>();

            List<string> names = new List<string>();

            string inner = string.Empty;
            string name = string.Empty;

            for (int i = 0; i < content.Length; i++)
            {
                if (squareorder[i] == 0 && content[i].ToString() != Defs.squareClose)
                {
                    name += content[i];
                }

                if (squareorder[i] != 0)
                {
                    if (name != string.Empty)
                    {
                        names.Add(name);
                        name = string.Empty;
                    }
     
                    if(content[i].ToString() != Defs.squareOpen)
                        inner += content[i];
                }

                if (content[i].ToString() == Defs.squareClose) 
                { 
                    innerValues.Add(inner);
                    inner = string.Empty;
                }
            }

            for(int i = 0; i < innerValues.Count; i++)
            {
                string nme = string.Empty;

                if(i < names.Count)
                    nme = names[i].Trim();

                subMaths.Add(new PhiMath
                {
                    Math = new MathPair
                    {
                        MathOp = Defs.oppSetIndex,
                        ValueLeft = nme,
                        ValueRight = innerValues[i]
                    },
                    RawValue = nme + " " + innerValues[i],
                });
            }


            return subMaths;
        }

        public string ExtractContent(string content, string begin)
        {
            string ASM = "";

            bool insideString = false;

            int insideCount = 0;

            int start = 0;

            bool run = false;

            string sub = content.Substring(begin.Length);

            for (int i = 0; i < sub.Length; i++)
            {
                if (sub[i].ToString() == Defs.ValueStringDelcare)
                {
                    if (!ParseUtilities.IsIgnorable(sub, i))
                    {
                        insideString = !insideString;
                    }
                }

                if (!insideString)
                {
                    if (sub[i].ToString() == Defs.curlyClose)
                    {
                        if (insideCount == 0)
                        {
                            return ASM;
                        }

                        insideCount--;
                    }

                    if (sub[i].ToString() == Defs.curlyOpen)
                    {
                        if (insideCount == 0)
                        {
                            start = i;
                            run = true;
                        }
                        else
                        {
                            insideCount++;
                        }
                    }
                }

                if (run)
                {
                    ASM += sub[i];
                }
            }

            return string.Empty;
        }
        public List<string> extractIntContent(string content)
        {
            List<string> value = new List<string>();

            string build = string.Empty;

            for (int i = 0; i < content.Length; i++)
            {
                if (Defs.VariableSetClosure.Contains(content[i]))
                {
                    value.Add(build);

                    break;
                }
                else if(Defs.Numeric.Contains(content[i])) 
                {
                    build += content[i];    
                }

                if (content[i] == ' ' && build != string.Empty)
                {
                    if (build.Length > 0)
                    {
                        value.Add(build);

                        build = string.Empty;
                    }
                }
            }

            return value;
        }
        public List<string> ExtractStrContent(string content)
        {
            List<string> value = new List<string>();

            string build = string.Empty;

            bool inside = false;

            for (int i = 0; i < content.Length; i++)
            {

                if (content[i].ToString() == Defs.ValueStringDelcare)
                {
                    if (!ParseUtilities.IsIgnorable(content, i))
                    {
                        inside = !inside;

                        if (!inside)
                        {
                            value.Add(build);
                            build = string.Empty;

                        }

                        continue;
                    }
                }

                if (inside)
                {
                    build += content[i];
                }
                else
                {
                    if (Defs.VariableSetClosure.Contains(content[i]))
                    {
                        break;
                    }
                }
            }

            return value;
        }
        public PhiClass ExtractClassContent(string content)
        {
            PhiClass phiClass = new PhiClass();

            string rawContent = string.Empty;

            #region Class Internals

            int cutfrom = 0;

            bool startContent = false;

            string sub = content.Substring(Defs.classStartPHI.Length);

            for (int i = 0; i < sub.Length; i++)
            {
                if (Defs.NameClosureCharacters.Contains(sub[i])
                    && phiClass.Name == string.Empty)
                {
                    string nm = sub.Substring(cutfrom, i - cutfrom);

                    if (Defs.Alphabet.Contains(nm.First().ToString().ToLower()))
                    {
                        phiClass.Name = nm;
                    }
                    else
                    {
                        phiClass.Name = "MAIN";
                    }
                }

                if (sub[i].ToString() == Defs.ClassInherit)
                {
                    cutfrom = i;
                }

                if (Defs.NameClosureCharacters.Contains(sub[i])
                   && phiClass.Name != string.Empty
                   && phiClass.Inherit == string.Empty)
                {
                    string nm = sub.Substring(cutfrom, i - cutfrom);
                    if(nm != null)
                    {
                        if(nm != string.Empty)
                        {
                            if (Defs.Alphabet.Contains(nm.First().ToString()))
                            {
                                phiClass.Inherit = nm;
                            }
                        }
                    }                    
                }

                if (sub[i].ToString() == Defs.curlyOpen)
                {
                    string cont = sub.Substring(i);
                    string value = ExtractContent(sub, Defs.curlyOpen);
                    phiClass.Variables = GetPhiVariables(value);

                    int inx = value.IndexOf(Defs.curlyOpen, 0);

                    rawContent = value.Substring(inx + 1);
                }
            }

            #endregion

            #region MethodInternals

            phiClass.RawContent = rawContent;

           phiClass.Methods = GetPhiMethods(phiClass.RawContent);

            #endregion

            return phiClass;
        }

        public List<PhiMethod> GetPhiMethods(string content)
        {
            List<PhiMethod> methods = new List<PhiMethod>();

            string raw = content;

            while (true)
            {
                PhiMethod mthd =  GetNextPhiMethod(raw);

                if(mthd.Name != string.Empty)
                {
                    methods.Add(mthd);

                    // cut last part off
                    raw = mthd.Remainder;
                }
                else
                {
                    break;
                }
            }

            return methods;
        }
        public PhiMethod GetNextPhiMethod(string content)
        {
            PhiMethod phiMethod = new PhiMethod();

            bool startName = false;
            bool startArgs = false;
            bool endMethod = false;
            bool startContent = false;

            string name = string.Empty;
            string end = string.Empty;

            string value = string.Empty;

            int start = 0;

            for(int i = 0; i < content.Length; i++)
            {
                if (content[i].ToString() == Defs.squareOpen)
                {
                    if (name == string.Empty)
                    { 
                        // if name is empty then startName

                        // check for array pattern and ignore if array "str testArray[3];" vs "[Method]"
                        if (!Defs.Alphabet.Contains(content[i].ToString()))
                        {
                            startName = true;
                            start = i;
                        }    
                    }
                    else
                    {
                        endMethod = true;
                    }
                }

                if(startName || startArgs)
                {
                    if (content[i].ToString() == Defs.squareClose)
                    {
                        startContent = true;
                    }
                    else if (content[i].ToString() == Defs.VariableSet)
                    {
                        startName = false;
                        startArgs = true;
                    }
                    else
                    {
                        if (startName && content[i].ToString() != Defs.squareOpen)
                        {
                            name += content[i];
                        }
                    }
                }

                if(startContent)
                {
                    value += content[i];
                }

                if(startArgs)
                {

                    if (content[i].ToString() == Defs.squareClose)
                    {
                        startContent = true;
                        startArgs = false;
                    }
                }

                if(endMethod)
                {
                    if (content[i].ToString() == Defs.squareClose)
                    {
                        end = end.Replace(Defs.squareOpen, string.Empty);
                        end = end.Replace(Defs.VariableSet, string.Empty);
                        end = end.Replace(Defs.squareClose, string.Empty);
                        end = end.Replace(Defs.MethodEndDeclare, string.Empty);

                        phiMethod.Name = name;
                        phiMethod.End = end;
                        phiMethod.Content = content.Substring(start, i - start + 1);
                        phiMethod.Remainder = content.Substring(i + 1);
                        break;
                    }
                    else if (content[i].ToString() == Defs.VariableSet)
                    {

                    }
                    else
                    {
                        end += content[i];
                    }
                }
            }

            return phiMethod;
        }

        public List<PhiVariable> GetPhiVariables(string content)
        {
            List<PhiVariable> phiVariables = new List<PhiVariable>();

            for (int i = 0; i < content.Length; i++)
            {
                string cut = content.Substring(i);

                string prev = string.Empty;

                if (i > 0)
                {
                    prev = content[i - 1].ToString();
                }

                Inside val = ParseUtilities.MatchesVariable(cut, prev);

                if (val != Inside.None)
                {
                    if (val == Inside.VariableTypeStr)
                    {
                        int inx = cut.IndexOf(Defs.VariableSet, val.ToString().Length + 1);
                        int len = inx - val.ToString().Length - 1;

                        string nme = cut.Substring(val.ToString().Length + 1, len);
                        string vle = cut.Substring(inx);

                        nme = ParseUtilities.ClearLabel(nme, Defs.Alphabet);

                        List<string> raw = ExtractStrContent(vle);

                        phiVariables.Add(new PhiVariable
                        {
                            Name = nme,
                            ValueRaw = "",
                            Values = raw
                        });
                    }
                    else if (val == Inside.VariableTypeInt)
                    {
                        int inx = cut.IndexOf(Defs.VariableSet, val.ToString().Length + 1);
                        int len = inx - val.ToString().Length - 1;

                        string nme = cut.Substring(val.ToString().Length + 1, len);
                        string vle = cut.Substring(inx);

                        nme = ParseUtilities.ClearLabel(nme, Defs.Alphabet);

                        List<string> raw = extractIntContent(vle);

                        phiVariables.Add(new PhiVariable
                        {
                            Name = nme,
                            ValueRaw = "",
                            Values = raw
                        });
                    }
                }

            }

            return phiVariables;
        }



    }
}

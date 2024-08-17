using PhiBasicTranslator.Structure;
using System.ComponentModel.Design;
using System.Diagnostics.Metrics;
using PhiBasicTranslator.ParseEngine;
using System.ComponentModel;
using System;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Reflection.Metadata;

namespace PhiBasicTranslator
{
    public class Translator
    {
        public string FileContent = string.Empty;
        public Translator() { }
        public Translator(string file) 
        {
            string content = File.ReadAllText(file);

            FileContent = content;

            TranslateCode(content);  
        }

        public void Highlight(string content)
        {

        }

        public List<string> TranslateFile(string file)
        {
            string content = File.ReadAllText(file);

            PhiCodebase code = new PhiCodebase();

            code.ClassList = TranslateCode(content);

            return TranslateUtilities.TranslateToX86.ToX86(code);
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


                    if (inside == Inside.MethodOpen)
                    {
                        insideMethod = true;
                    }

                    if (last == Inside.MethodEnd && insideMethod)
                    {
                        insideMethod = false;

                        current.Methods.Add(method.Copy());

                        method = new PhiMethod();
                    }
                    #region Instructs

                    PhiInstruct instr = ExtractInstruct(inside, content, prev, profile.ContentInside, i);

                    if (instr.Name != string.Empty)
                    {
                        i += instr.Value.Length; // check for accuracy

                        current.Instructs.Add(instr.Copy());
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
                        method.Name = ParseUtilities.ClearLabel(mnme, Defs.Alphabet);
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


        public PhiInstruct ExtractInstruct(Inside inside, string content, string prev, Inside[] profile, int i)
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

                        instruct = TranslateSubInstructs(instruct);
                    }
                }
            }

            return instruct;
        }
        public PhiInstruct TranslateSubInstructs(PhiInstruct instr)
        {
            if (instr.Name != string.Empty)
            {
                Inside last = instr.ContentLabels.First();

                PhiVariable varble = new PhiVariable();
                PhiInstruct subInstr = new PhiInstruct();

                bool startParams = false;

                string prev = string.Empty;
                string cond = string.Empty;
                string cRaw = string.Empty;

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
                        else if(inside == Inside.Colon)
                        {
                            startParams = true;
                        }
                        else if (inside == Inside.VariableEnd)
                        {
                            instr.Variables.Add(varble);
                            //Preexisting?
                            varble = new PhiVariable();
                        }
                        else if (inside == Inside.StandAloneInt)
                        {
                            varble.varType = Inside.StandAloneInt;
                            varble.Name = Defs.replaceUnsetName;

                            for(int j = i; j < instr.Value.Length; j++)
                            {
                                if(instr.ContentLabels[j] != Inside.StandAloneInt)
                                {
                                    break;
                                }
                                else
                                {
                                    varble.ValueRaw += instr.Value[j];
                                }
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
                                    PhiInstruct subInstruct = ExtractInstruct(inside, instr.Value, prev, instr.ContentLabels.ToArray(), i);
                                    instr.Instructs.Add(subInstruct);

                                    i += subInstruct.Value.Length;
                                }
                                else
                                {
                                    PhiInstruct subInstruct = ExtractInstruct(inside, instr.Value, prev, instr.ContentLabels.ToArray(), i);
                                    instr.Instructs.Add(subInstruct);

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

                    if(startParams)
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

                    if(inside == Inside.Opperation)
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

                            if (Defs.ConditionalClosureCharacters.Contains(ltr))
                            {
                                bgn = j;
                                break;
                            }
                        }
                        #endregion

                        cr = instr.Value.Substring(bgn + 1, end - bgn - 1).Trim();

                        i = end;

                        MathPair mpar = PhiMath.Parse(cr);

                        instr.Maths.Add(new PhiMath
                        {
                            Math = mpar,
                            RawValue = cr
                        });
                        
                    }

                    if(inside == Inside.Conditonal)
                    {
                        cond += letter;

                        string cr = string.Empty;

                        int bgn = 0;
                        int end = 0;

                        #region clip

                        for (int j = i; j < instr.Value.Length; j++)
                        {
                            string ltr = instr.Value[j].ToString();

                            if (Defs.ConditionalClosureCharacters.Contains(ltr))
                            {
                                end = j;
                                break;
                            }
                        }

                        for (int j = i; j >= 0; j--)
                        {
                            string ltr = instr.Value[j].ToString();

                            if (Defs.ConditionalClosureCharacters.Contains(ltr))
                            {
                                bgn = j;
                                break;
                            }
                        }
                        #endregion
                    
                        cr = instr.Value.Substring(bgn + 1, end - bgn - 1).Trim();

                        if(cRaw == string.Empty) 
                        { 
                            cRaw = cr; 
                        }
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
                }

                if(cond != string.Empty)
                {
                    Console.WriteLine(cond);

                    string? select = Defs.OpperCompareList.Where(x => x == cond).FirstOrDefault();

                    if (select != null)
                    {
                        string left = string.Empty;
                        string right = string.Empty;

                        string[] two = cRaw.Split(cond);

                        if (two.Length == 2)
                        {
                            left = two[0].Trim();
                            right = two[1].Trim();
                        }

                        ConditionalPairs.ConditionType ctyp = ConditionalPairs.ConditionType.None;

                        if (instr.Name == Defs.instWhile)
                        {
                            ctyp = ParseUtilities.MatchesInvertedCondition(cond);
                        }
                        else
                        {
                            ctyp = ParseUtilities.MatchesCondition(cond);
                        }

                        instr.Conditionals.Add(new PhiConditional
                        {
                            PhiConditionalPairs = new List<ConditionalPairs>
                            {
                                new ConditionalPairs 
                                { 
                                    type = ctyp,
                                    LeftValue = left,
                                    RightValue = right
                                }
                            },
                            RawValue = cRaw
                        });
                    }
                }
            }

            return instr;
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
                    raw = mthd.Remainer;
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
                    { // if name is empty then startName
                        if (i > 0)
                        {
                            // check for array pattern and ignore if array "str testArray[3];" vs "[Method]"
                            if (!Defs.Alphabet.Contains(content[i - 1].ToString()))
                            {
                                startName = true;
                                start = i;
                            }
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
                        phiMethod.Remainer = content.Substring(i + 1);
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

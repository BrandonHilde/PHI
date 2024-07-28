using PhiBasicTranslator.Structure;
using System.ComponentModel.Design;
using System.Diagnostics.Metrics;
using PhiBasicTranslator.ParseEngine;

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
            PhiVariables varble = new PhiVariables();
            PhiInstruct instruct = new PhiInstruct();

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

                    if(inside == Inside.MethodOpen)
                    {
                        insideMethod = true;
                    }
                    
                    if(last == Inside.MethodEnd && insideMethod)
                    {
                        insideMethod = false;

                        current.Methods.Add(method.Copy());
                        
                        method = new PhiMethod();
                    }

                    if(inside == Inside.Instruct)
                    {
                        if (i > 1)
                        {
                            string cut = content.Substring(i);
                            string nme = ParseUtilities.MatchesInstruct(cut, prev);

                            if (nme != string.Empty)
                            {
                                instruct.Name = nme;

                                #region Instructs

                                Inside lastj = Inside.None;

                                for (int j = i + nme.Length; j < content.Length; j++)
                                {
                                    Inside sidej = profile.ContentInside[j];

                                    string letterj = content[j].ToString();

                                    if (sidej != Inside.InstructClose)
                                    {
                                        instruct.Value += letterj;

                                        if(sidej != Inside.String && letterj == " ")
                                        {
                                            typeCount++;
                                        }
                                    }
                                    else
                                    {

                                        if(Defs.instructContainers.Contains(instruct.Name))
                                        {
                                            instruct.InType = Inside.InstructContainer;
                                        }
                                        else if(typeCount > 1)
                                        {
                                            instruct.InType = Inside.VariableTypeMixed;
                                        }
                                        else
                                        {
                                            instruct.InType = lastj;
                                        }

                                        current.Instructs.Add(instruct.Copy());
                                        instruct = new PhiInstruct();

                                        typeCount = 0;

                                        break;
                                    }
                                    lastj = sidej;
                                }

                                #endregion
                            }
                        }
                    }
                }

                #region Class Values

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
                    if(current.Inherit == string.Empty)
                    {
                        current.Inherit = ParseUtilities.ClearLabel(inherit, Defs.Alphabet);    
                        inherit = string.Empty;
                    }
                }
                #endregion

                #region Variables

                if(!match)
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

                    if(last == Inside.VariableEnd)
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

                        varble = new PhiVariables();
                    }

                    if(last == Inside.MethodName)
                    {
                        method.Name = ParseUtilities.ClearLabel(mnme, Defs.Alphabet);
                        mnme = string.Empty;
                    }

                    if(inside == Inside.CurlyClose)
                    {
                        classes.Add(current.Copy());

                        current = new PhiClass();
                    }
                }

                if (inside == Inside.VariableName)
                {
                    vnme += letter;
                }

                if(inside == Inside.VariableValue || inside == Inside.String)
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
                else if(Defs.Numbers.Contains(content[i])) 
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

        public List<PhiVariables> GetPhiVariables(string content)
        {
            List<PhiVariables> phiVariables = new List<PhiVariables>();

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

                        phiVariables.Add(new PhiVariables
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

                        phiVariables.Add(new PhiVariables
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

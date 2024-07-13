using PhiBasicTranslator.Structure;

namespace PhiBasicTranslator
{
    public class Translator
    {

        public string TranslateFile(string file)
        {
            string content = File.ReadAllText(file);

            return TranslateCode(content);
        }

        public string TranslateCode(string content)
        {
            string ASM = "";

            for (int i = 0; i < content.Length; i++)
            {
                string sub = content.Substring(i);

                if (sub.StartsWith(Defs.classStartPHI)) //.phi:name {}
                {
                    PhiClass ph = extractClassContent(sub);
                }
                else if (sub.StartsWith(Defs.classStartx86ASM)) //.asm {}
                {
                    ASM += ExtractContent(sub, Defs.classStartx86ASM);
                }
            }

            return ASM;
        }

        public bool IsIgnorable(string content, int index)
        {
            if (index > 1 && content.Length > index)
            {
                if (content[index - 1].ToString() == Defs.IgnoreCharacter
                    && content[index - 2].ToString() == Defs.IgnoreCharacter)
                {
                    return false;
                }
                if (content[index - 1].ToString() == Defs.IgnoreCharacter)
                {
                    return true;
                }
            }

            return false;
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
                    if (!IsIgnorable(sub, i))
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
        public List<string> extractStrContent(string content)
        {
            List<string> value = new List<string>();

            string build = string.Empty;

            bool inside = false;

            for (int i = 0; i < content.Length; i++)
            {

                if (content[i].ToString() == Defs.ValueStringDelcare)
                {
                    if (!IsIgnorable(content, i))
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
        public PhiClass extractClassContent(string content)
        {
            PhiClass phiClass = new PhiClass();

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
                }
            }

            return phiClass;
        }

        public List<PhiVariables> GetPhiVariables(string content)
        {
            List<PhiVariables> phiVariables = new List<PhiVariables>();

            for (int i = 0; i < content.Length; i++)
            {
                string cut = content.Substring(i);

                string val = MatchesVariable(cut);

                if (val != string.Empty)
                {
                    if (val == Defs.varSTR)
                    {
                        int inx = cut.IndexOf(Defs.VariableSet, val.Length + 1);
                        int len = inx - val.Length - 1;

                        string nme = cut.Substring(val.Length + 1, len);
                        string vle = cut.Substring(inx);

                        nme = ClearLabel(nme, Defs.Alphabet);

                        List<string> raw = extractStrContent(vle);

                        phiVariables.Add(new PhiVariables
                        {
                            Name = nme,
                            ValueRaw = "",
                            Values = raw
                        });
                    }
                    else if (val == Defs.varINT)
                    {
                        int inx = cut.IndexOf(Defs.VariableSet, val.Length + 1);
                        int len = inx - val.Length - 1;

                        string nme = cut.Substring(val.Length + 1, len);
                        string vle = cut.Substring(inx);

                        nme = ClearLabel(nme, Defs.Alphabet);

                        List<string> raw = extractIntContent(vle);

                        phiVariables.Add(new PhiVariables
                        {
                            Name = nme,
                            ValueRaw = "",
                            Values = raw
                        });
                    }
                    else if (val == Defs.varDEC)
                    {

                    }
                    else if (val == Defs.varVAR)
                    {

                    }
                }

            }

            return phiVariables;
        }

        public string ClearLabel(string label, string allowed)
        {
            string clear = string.Empty;

            for (int i = 0; i < label.Length; i++)
            {
                if (allowed.Contains(label[i]))
                {
                    clear += label[i];
                }
            }

            return clear;
        }
        public string MatchesVariable(string content)
        {
            string vname = string.Empty;

            foreach (string val in Defs.VariableTypes)
            {
                if (content.StartsWith(val))
                {
                    vname = val;
                }
            }

            return vname;
        }
    }
}

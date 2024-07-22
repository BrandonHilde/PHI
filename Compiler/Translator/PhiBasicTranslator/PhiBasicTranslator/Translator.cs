using PhiBasicTranslator.Structure;
using System.ComponentModel.Design;
using System.Diagnostics.Metrics;
using PhiBasicTranslator.ParseEngine;

namespace PhiBasicTranslator
{
    public class Translator
    {
        public Translator() { }
        public Translator(string file) 
        {
            string content = File.ReadAllText(file);

            ContentProfile prf = ParseUtilities.ProfileContent(content);

            ConsoleColor[] clrs =
            {
                ConsoleColor.White,  // None                                        None, 
                ConsoleColor.Green,                     // MultiComment,            MultiComment, 
                ConsoleColor.Green,                     // Comment,                 Comment, 
                ConsoleColor.Red, // String,                                        String, 
                ConsoleColor.Gray,                     // AsmClassStart,            AsmClassStart, 
                ConsoleColor.DarkGray,                  // ArmClassStart,           ArmClassStart,
                ConsoleColor.DarkBlue,                 // PhiClassStart,            PhiClassStart, 
                ConsoleColor.DarkBlue,// Curly,                                     Curly, 
                ConsoleColor.DarkBlue,// Square,                                    Square, 
                ConsoleColor.DarkBlue,                     // Parenthesis,          Parenthesis, 
                ConsoleColor.DarkMagenta,                     // Colon,             Colon, 
                ConsoleColor.DarkMagenta,                         //                SemiColon,
                ConsoleColor.DarkCyan,                     // ClassName,            ClassName, 
                ConsoleColor.DarkCyan,// ClassInherit,                              ClassInherit, 
                ConsoleColor.DarkCyan,// VariableName                               VariableName,
                ConsoleColor.DarkBlue, // VariableType                              VariableType,
                ConsoleColor.Yellow,                                 //             VariableValue,
                ConsoleColor.DarkBlue,                               //             MethodOpen,
                ConsoleColor.DarkBlue,                               //             MethodClose,
                ConsoleColor.DarkMagenta,                            //             MethodSet,
                ConsoleColor.DarkBlue,                               //             MethodEnd,
                ConsoleColor.DarkMagenta,                            //             MethodReturn,
                ConsoleColor.DarkCyan,                               //             MethodName
                ConsoleColor.Cyan,                                   //             Instruct
                ConsoleColor.Red

            };

            for (int i = 0; i < content.Length; i++)
            {
                Console.ForegroundColor = clrs[(int)prf.ContentInside[i]];

                Console.Write(content[i]);  
            }

            Console.ForegroundColor = ConsoleColor.White;   
        }

        public string TranslateFile(string file)
        {
            string content = File.ReadAllText(file);

            return TranslateCode(content);
        }

        public string TranslateCode(string content)
        {
            string ASM = "";

            // remember to check for string ignore
            string rawContent = ParseUtilities.ClearComments(content);

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
        public List<string> extractStrContent(string content)
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
        public PhiClass extractClassContent(string content)
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

                string val = ParseUtilities.MatchesVariable(cut, prev);

                if (val != string.Empty)
                {
                    if (val == Defs.varSTR)
                    {
                        int inx = cut.IndexOf(Defs.VariableSet, val.Length + 1);
                        int len = inx - val.Length - 1;

                        string nme = cut.Substring(val.Length + 1, len);
                        string vle = cut.Substring(inx);

                        nme = ParseUtilities.ClearLabel(nme, Defs.Alphabet);

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

                        nme = ParseUtilities.ClearLabel(nme, Defs.Alphabet);

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



    }
}

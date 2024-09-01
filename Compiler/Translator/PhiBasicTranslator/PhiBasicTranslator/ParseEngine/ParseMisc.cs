using PhiBasicTranslator.Structure;
using PhiBasicTranslator.TranslateUtilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.ParseEngine
{
    public class ParseMisc
    {
        public static bool IsNumber(string str)
        {
            double d = 0;

            return double.TryParse(str, out d);
        }

        public static bool HasMathOpperation(string content)
        {
            if(ContainsAny(content, Defs.MathOpsList) 
               || ContainsAny(content, Defs.MathSmallOpsList))
            {
                return true;
            }

            return false;
        }
        public static bool StartsWithAppendedValue(string content, string value, List<string> appendedValues)
        {
            bool result = false;

            foreach (string appendedValue in appendedValues)
            {
                if(content.StartsWith(value + appendedValue)) return true;
            }

            return result;
        }

        public static bool StartsWithAppendedValue(string content, string value, List<char> appendedValues)
        {
            bool result = false;

            foreach (char append in appendedValues)
            {
                if (content.StartsWith(value + append)) return true;
            }

            return result;
        }

        public static bool ContainsAny(string content, List<string> values)
        {
            foreach (string value in values)
            {
                if(content.Contains(value)) return true;
            }

            return false;   
        }

        public static bool IsElsePair(PhiClass cls, PhiInstruct instruct)
        {
            PhiInstruct? inst = cls.Instructs.LastOrDefault();

            if (inst != null)
            {
                if(inst.Name == Defs.instIf && instruct.Name == Defs.instElse)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsElsePair(PhiMethod mthd, PhiInstruct instruct)
        {
            PhiInstruct? inst = mthd.Instructs.LastOrDefault();

            if (inst != null)
            {
                if (inst.Name == Defs.instIf && instruct.Name == Defs.instElse)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsElsePair(PhiInstruct parentInst, PhiInstruct instruct)
        {
            PhiInstruct? inst = parentInst.Instructs.LastOrDefault();

            if (inst != null)
            {
                if (inst.Name == Defs.instIf && instruct.Name == Defs.instElse)
                {
                    return true;
                }
            }

            return false;
        }

        public static List<string> ExtractSubValues(string value)
        {
            List<string> result = new List<string>();

            ContentProfile profile = ParseUtilities.ProfilePrepare(value);

            string val = "";

            bool prevIsSlash = false;

            for (int i = 0; i < value.Length; i++)
            {
                string letter = value[i].ToString();

                if (profile.ContentInside[i] != Inside.String)
                {
                    if (profile.ContentInside[i] != Inside.Comment
                    && profile.ContentInside[i] != Inside.MultiComment)
                    {
                        if(Defs.TabSpaceClosureCharacters.Contains(letter))
                        {
                            if (val != string.Empty) result.Add(val);
                            val = string.Empty;
                        }
                        else
                        {
                            val += letter;
                        }
                    }
                }
                else
                {
                    if (prevIsSlash)
                    {
                        if (letter == "r")
                        {
                            if (val != string.Empty && val != "'") result.Add(val + "'");
                            result.Add(ASMx86_16BIT.varStrReturn);

                            val = string.Empty;
                        }
                        else if (letter == "n")
                        {
                            if (val != string.Empty && val != "'") result.Add(val + "'");
                            result.Add(ASMx86_16BIT.varStrNewLine);

                            val = string.Empty;
                        }
                        else if (letter == "t")
                        {
                            if (val != string.Empty && val != "'") result.Add(val + "'");
                            result.Add(ASMx86_16BIT.varStrTab);

                            val = string.Empty;
                        }
                    }
                    
                    
                    if (letter == "\\")
                    {
                        prevIsSlash = true;
                    }
                    else
                    {
                        if(!prevIsSlash) val += letter;

                        prevIsSlash = false;
                    }
                }
            }

            if(val != string.Empty && val != "'") result.Add(val);

            return result;
        }
        public static string ExtractStandAloneVarName(string content, List<Inside> labels, string endMatch)
        {
            string nme = string.Empty;

            bool start = false;

            Inside last = Inside.None;

            int nx = -1;

            for (int i = 0; i < content.Length; i++)
            {
                Inside inside = labels[i];

                bool match = last == inside;

                if(!match)
                {
                    if(last == Inside.Instruct)
                    {
                        start = true;
                        nx = GetNextIndexOfAny(content, endMatch, i);   
                    }
                }

                if (i < nx)
                {
                    if (start) nme += content[i];
                }
                else
                {
                   if(start) break;
                }


                last = inside;
            }

            return nme;
        }

        public static int GetPrevIndexOfAny(string Content, string Match, int Start)
        {
            for (int i = Start; i >= 0; i--)
            {
                if (Match.Contains(Content[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int GetNextIndexOfAny(string Content, string Match, int Start)
        {
            for (int i = Start; i < Content.Length; i++)
            {
                if (Match.Contains(Content[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public static string ClearMethodName(string methodName)
        {
            methodName = methodName.Replace('.', '_');
            methodName = ParseUtilities.ClearLabel(methodName, Defs.Alphabet);

            return methodName;
        }
    }
}

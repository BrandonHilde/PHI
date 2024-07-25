using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.ParseEngine
{
    public class ParseUtilities
    {
        public static ContentProfile ProfileContent(string content)
        {
            ContentProfile prf = ProfilePrepare(content);

            prf = ProfileClasses(content, prf);
            prf = ProfileVariables(content, prf);
            prf = ProfileMethods(content, prf);
            prf = ProfileInstructs(content, prf);
            prf = ProfileBasics(content, prf);

            return prf;
        }
        public static bool IsIgnorable(string content, int index)
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

        public static string ClearComments(string content)
        {
            ContentProfile prf = ProfilePrepare(content);

            string raw = "";

            for (int i = 0; i < content.Length; i++)
            {
                if (prf.ContentInside[i] == Inside.None || prf.ContentInside[i] == Inside.String)
                {
                    raw += content[i];
                }
            }

            return raw;
        }

        public static ContentProfile ProfileInstructs(string content, ContentProfile previous)
        {
            bool start = false;

            for (int i = 0; i < content.Length; i++)
            {
                if (previous.ContentInside[i] == Inside.None)
                {
                    string cut = content.Substring(i);

                    string letter = content[i].ToString();

                    string prev = string.Empty;

                    if (i > 0)
                    {
                        prev = content[i - 1].ToString();
                    }

                    string inst = MatchesInstruct(cut, prev);

                    if (inst != string.Empty)
                    {
                        start = true;

                        for (int j = i; j < inst.Length + i; j++)
                        {
                            previous.ContentInside[j] = Inside.Instruct;
                        }
                    } 
                    
                    if(start && letter + prev == Defs.InstructSetClosure)
                    {
                        previous.ContentInside[i] = Inside.InstructClose;
                        start = false;
                    }

                    if (start && letter != Defs.VariableSetClosure)
                    {
                        //previous.ContentInside[i] = Inside.InstructValue;
                    }
                }
            }
                
            return previous;
        }

        public static ContentProfile ProfileBasics(string content, ContentProfile previous)
        {
            for (int i = 0; i < content.Length; i++)
            {
                string letter = content[i].ToString();

                if (previous.ContentInside[i] == Inside.None)
                {
                    if(letter == Defs.VariableSetClosure) //;
                    {
                        previous.ContentInside[i] = Inside.SemiColon;
                    }
                }
            }

            return previous;
        }

        public static ContentProfile ProfileMethods(string content, ContentProfile previous)
        {
            Inside inside = Inside.None;

            for (int i = 0; i < content.Length; i++)
            {
                if (previous.ContentInside[i] == Inside.None)
                {
                    string cut = content.Substring(i);

                    string letter = content[i].ToString();

                    string prev = string.Empty;

                    if (i > 0)
                    {
                        prev = content[i - 1].ToString();
                    }

                    if (inside == Inside.MethodName)
                    {
                        if (letter == Defs.VariableSet) //:
                        {
                            inside = Inside.MethodSet;
                        }
                        else if (letter == Defs.squareClose)
                        {
                            inside = Inside.MethodClose;
                            previous.ContentInside[i] = inside;
                        }

                        previous.ContentInside[i] = inside;
                    }

                    if (!Defs.Alphabet.Contains(prev))
                    {
                        if (letter == Defs.squareOpen)
                        {
                            previous.ContentInside[i] = Inside.MethodOpen;

                            if (cut.StartsWith(Defs.MethodEndDeclare))
                            {
                                inside = Inside.MethodEnd;
                            }
                            else
                            {
                                inside = Inside.MethodName;
                            }
                        }
                    }

                    if (inside == Inside.MethodSet)
                    {
                        if (letter == Defs.squareClose)
                        {
                            inside = Inside.MethodClose;
                            previous.ContentInside[i] = inside;
                        }
                    }

                    //if (inside == Inside.MethodOpen)
                    //{
                    //    if (cut.StartsWith(Defs.MethodEndDeclare))
                    //    {
                    //        inside = Inside.MethodEnd;
                    //    }
                    //}

                    if (inside == Inside.MethodEnd)
                    {
                        if (letter == Defs.VariableSet) //:
                        {
                            inside = Inside.MethodReturn;
                        }

                        previous.ContentInside[i] = inside;
                    }

                    if (inside == Inside.MethodReturn || inside == Inside.MethodEnd)
                    {
                        if (letter == Defs.squareClose)
                        {
                            previous.ContentInside[i] = Inside.MethodEnd;
                            inside = Inside.None;
                        }
                    }
                }
            }

            return previous;
        }
        public static ContentProfile ProfileVariables(string content, ContentProfile previous)
        {
            for (int i = 0; i < content.Length; i++)
            {
                string cut = content.Substring(i);

                string prev = string.Empty;

                if (i > 0)
                {
                    prev = content[i - 1].ToString();
                }

                Inside val = MatchesVariable(cut, prev);

                if (val != Inside.None && previous.ContentInside[i] == Inside.None)
                {
                    previous = ParseVariables.ProfileVAR(content, i, val, previous);
                }
            }

            return previous;
        }
        public static ContentProfile ProfileClasses(string content, ContentProfile previous)
        {
            Inside inside = Inside.None;

            bool startName = false;

            if(previous.ContentInside.Length == content.Length)
            {
                for (int i = 0; i < content.Length; i++)
                {
                    string remaining = content.Substring(i);

                    if(remaining.StartsWith(Defs.classStartPHI))
                    {
                        inside = Inside.PhiClassStart;

                        for (int m = i; m < Defs.classStartPHI.Length + i; m++)
                        {
                            previous.ContentInside[m] = inside;
                        }

                        i += Defs.classStartPHI.Length;

                        startName = true;
                    }
                    else if (remaining.StartsWith(Defs.classStartx86ASM))
                    {
                        inside = Inside.AsmClassStart;

                        for (int m = i; m < Defs.classStartPHI.Length + i; m++)
                        {
                            previous.ContentInside[m] = inside;
                        }

                        i += Defs.classStartx86ASM.Length;

                        startName = true;
                    }
                    else if (remaining.StartsWith(Defs.classStartArmASM))
                    {
                        inside = Inside.ArmClassStart;

                        for (int m = i; m < Defs.classStartPHI.Length + i; m++)
                        {
                            previous.ContentInside[m] = inside;
                        }

                        i += Defs.classStartArmASM.Length;

                        startName = true;
                    }

                    if (startName)
                    {
                        inside = Inside.ClassName;

                        for (int m = i; m < content.Length; m++)
                        {
                            string letter = content[m].ToString();

                            if (inside == Inside.Colon)
                            {
                                inside = Inside.ClassInherit;
                            }

                            if (letter == Defs.ClassInherit) //":"
                            {
                                inside = Inside.Colon;
                            }

                            previous.ContentInside[m] = inside;

                            if (letter == Defs.curlyOpen)
                            {
                                inside = Inside.CurlyOpen;
                                previous.ContentInside[m] = inside;
                                break;
                            }
                        }

                        startName = false;
                    }

                    if (content[i].ToString() == Defs.curlyOpen)
                    {
                        inside = Inside.CurlyOpen;
                        previous.ContentInside[i] = inside;
                    }

                    if (content[i].ToString() == Defs.curlyClose)
                    {
                        inside = Inside.CurlyClose;
                        previous.ContentInside[i] = inside;
                    }

                    if (inside != Inside.None)
                    {
                        inside = Inside.None;
                    }
                }
            }

            return previous;
        }
        public static ContentProfile ProfilePrepare(string content)
        {
            ContentProfile profile = new ContentProfile(content.Length);

            string letter = string.Empty;
            Inside inside = Inside.None;
            Inside exitType = Inside.None;

            int lastExit = -1;

            for (int i = 0; i < content.Length; i++)
            {
                letter = content[i].ToString();

                if (inside == Inside.String || inside == Inside.None)
                {
                    if (letter == Defs.ValueStringDelcare)
                    {
                        if (!IsIgnorable(content, i))
                        {
                            if (inside == Inside.String)
                            {
                                inside = Inside.None;
                                exitType = Inside.String;
                                lastExit = i;
                            }
                            else
                            {
                                inside = Inside.String;
                            }
                        }
                    }
                }

                if (inside != Inside.None)
                {
                    if (Defs.CommentLine.Contains(letter))
                    {
                        if (inside == Inside.Comment)
                        {
                            inside = Inside.None;
                            exitType = Inside.Comment;
                            lastExit = i;
                        }
                    }
                    else if (letter == Defs.Comment)
                    {
                        if (inside == Inside.MultiComment)
                        {
                            inside = Inside.None;
                            exitType = Inside.MultiComment;
                            lastExit = i;
                        }
                    }
                }
                else if (i < content.Length - 1)
                {
                    bool lineEnd = Defs.CommentLine.Contains(content[i + 1]);

                    if (letter == Defs.Comment)
                    {
                        if(lineEnd) inside = Inside.MultiComment;
                        else inside = Inside.Comment;
                    }
                }

                if (lastExit != i)
                    profile.ContentInside[i] = inside;
                else profile.ContentInside[i] = exitType;
            }

            return profile;
        }
        /*
        public static string ClearMiltiLineComments(string content)
        {
            bool inside = false;
            bool lineEnd = false;

            bool[] insideString = ProfileStringContent(content);

            string rawContent = string.Empty;

            for (int i = 0; i < content.Length - 1; i++)
            {
                string letter = content[i].ToString();

                if (!insideString[i])
                {
                    lineEnd = Defs.CommentLine.Contains(content[i + 1]);

                    if (letter == Defs.Comment && lineEnd)
                    {
                        // exit comment if line has ended
                        inside = !inside;
                    }
                }

                if (letter == Defs.Comment && lineEnd)
                {
                }
                else
                {
                    if (!inside)
                    {
                        rawContent += letter;
                    }
                }
            }

            return rawContent;
        }

        public static string ClearComments(string content)
        {
            bool inside = false;
            bool lineEnd = false;

            bool[] insideString = ProfileStringContent(content);


            string rawContent = string.Empty;

            for (int i = 0; i < content.Length - 2; i++)
            {
                string letter = content[i].ToString();

                if (!insideString[i])
                {
                    lineEnd = Defs.CommentLine.Contains(content[i + 1]);

                    if (letter == Defs.Comment && !lineEnd)
                    {
                        // exit comment if line has ended
                        inside = true;
                    }

                    if (Defs.CommentLine.Contains(letter))
                    {
                        if (inside)
                        {
                            //only end if inside comment
                            inside = false;
                        }
                    }
                }

                if (!inside)
                {
                    rawContent += letter;
                }
            }

            return rawContent;
        }
        */
        public static string ClearLabel(string label, string allowed)
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
        public static Inside MatchesVariable(string content, string prev)
        {
            if (Defs.Alphabet.Contains(prev))
            {
                return Inside.None;
            }

            foreach (string val in Defs.VariableTypes)
            {
                if (content.StartsWith(val))
                {
                    if (val.ToString() == Defs.varBLN) return Inside.VariableTypeBln;
                    if (val.ToString() == Defs.varBYT) return Inside.VariableTypeByt;
                    if (val.ToString() == Defs.varDEC) return Inside.VariableTypeDec;
                    if (val.ToString() == Defs.varFIN) return Inside.VariableTypeFin;
                    if (val.ToString() == Defs.varINT) return Inside.VariableTypeInt;
                    if (val.ToString() == Defs.varSTR) return Inside.VariableTypeStr;
                    if (val.ToString() == Defs.varVAR) return Inside.VariableTypeVar;
                }
            }

            return Inside.None;
        }

        public static string MatchesInstruct(string content, string prev)
        {
            string vname = string.Empty;

            foreach (string val in Defs.instructModifyList)
            {
                if (content.StartsWith(val))
                {
                    return val;
                }
            }

            if (Defs.Alphabet.Contains(prev))
            {
                return vname;
            }

            foreach (string val in Defs.instructCommandList)
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

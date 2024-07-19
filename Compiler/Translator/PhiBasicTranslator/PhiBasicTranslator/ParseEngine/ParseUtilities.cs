using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.ParseEngine
{
    public class ParseUtilities
    {

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

        public static ContentProfile ProfileVariables(string content, ContentProfile previous)
        {
            for (int i = 0; i < content.Length; i++)
            {
                string cut = content.Substring(i);

                string val = MatchesVariable(cut);

                if (val != string.Empty && previous.ContentInside[i] == Inside.None)
                {
                    if (val == Defs.varSTR)
                    {
                        previous = ParseVariables.ProfileSTR(content, i, previous);
                    }
                    else if (val == Defs.varINT)
                    {
                        previous = ParseVariables.ProfileINT(content, i, previous);
                    }
                    else if (val == Defs.varBYT)
                    {

                    }
                    else if (val == Defs.varDEC)
                    {

                    }
                    else if (val == Defs.varFIN)
                    {

                    }
                    else if (val == Defs.varVAR)
                    {

                    }
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
                for (int i = 0; i < content.Length; ++i)
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
                                inside = Inside.Curly;
                                previous.ContentInside[m] = inside;
                                break;
                            }
                        }

                        startName = false;
                    }

                    if (content[i].ToString() == Defs.curlyClose
                        || content[i].ToString() == Defs.curlyOpen)
                    {
                        inside = Inside.Curly;
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
        public static string MatchesVariable(string content)
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

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
            ContentProfile prf = ProfileContent(content);

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
        public static ContentProfile ProfileContent(string content)
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

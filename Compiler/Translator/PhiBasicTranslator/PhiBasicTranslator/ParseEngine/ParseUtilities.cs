using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Security.Cryptography;
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
            prf = ProfileParenthesis(content, prf);
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

            bool container = false;

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

                    if(Defs.instructContainers.Contains(inst))
                    {
                        container = true;
                    }

                    if (inst != string.Empty)
                    {
                        start = true;

                        for (int j = i; j < inst.Length + i; j++)
                        {
                            previous.ContentInside[j] = Inside.Instruct;
                        }
                    }

                    if (letter == Defs.VariableSet)// && !container)
                    {
                        previous.ContentInside[i] = Inside.Colon;
                    }

                    if (letter + prev == Defs.InstructSetClosure)
                    {
                        previous.ContentInside[i] = Inside.InstructClose;
                        container = false;
                        start = false;
                    }
                    else if(letter == Defs.VariableSetClosure && !container && start)
                    {
                        previous.ContentInside[i] = Inside.InstructClose;
                        start = false;
                    }

                    if (start)
                    {
                        if(container)
                            previous.Labels[i] = RegionLabel.InstructContent;
                        else
                            previous.Labels[i] = RegionLabel.InstructValue;
                    }
                }
            }
                
            return previous;
        }

        public static ContentProfile ProfileBasics(string content, ContentProfile previous)
        {
            string prev = string.Empty;

            for (int i = 0; i < content.Length; i++)
            {
                string letter = content[i].ToString();

                string cut = content.Substring(i);

                Inside inside = previous.ContentInside[i];

                if (inside == Inside.None)
                {
                    string? opr = Defs.OpperCompareList.Where(
                        x => cut.StartsWith(x)
                        ).FirstOrDefault();

                    if(opr != null)
                    {
                        for(int j = i; j < i + opr.Length; j++)
                        {
                            previous.ContentInside[j] = Inside.Conditonal;
                        }
                    }

                    string? math = MatchesMath(cut);

                    if(math != string.Empty)
                    {
                        for (int j = i; j < i + math.Length; j++)
                        {
                            previous.ContentInside[j] = Inside.Opperation;
                        }
                    }

                    if(letter == Defs.VariableSetClosure) //;
                    {
                        previous.ContentInside[i] = Inside.SemiColon;
                    }

                    if(Defs.Numeric.Contains(letter))
                    {
                        if(!Defs.Alphabet.Contains(prev))
                        {
                            string num = string.Empty;
                            bool valid = false;

                            for(int j = i; j < content.Length;  j++)
                            {
                                string add = content[j].ToString();

                                if (Defs.Numeric.Contains(add))
                                {
                                    num += add;
                                }
                                else
                                {
                                    valid = Defs.RawNumberCut.Contains(add);

                                    break;
                                }
                            }

                            if (valid)
                            {
                                for (int k = i; k < i + num.Length; k++)
                                {
                                    previous.ContentInside[k] = Inside.StandAloneInt;
                                }

                                i += num.Length - 1;
                            }
                        }
                    }
                    
                }

                prev = letter;
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
                    if (previous.Labels[i] != RegionLabel.Conditional)
                    {
                        previous = ParseVariables.ProfileVAR(content, i, val, previous);
                    }
                }
            }

            return previous;
        }
        public static ContentProfile ProfileClasses(string content, ContentProfile previous)
        {
            Inside inside = Inside.None;

            bool startName = false;

            int curlycount = 0;

            if(previous.ContentInside.Length == content.Length)
            {
                for (int i = 0; i < content.Length; i++)
                {
                    string remaining = content.Substring(i);

                    if (previous.ContentInside[i] == Inside.None)
                    {
                        if (remaining.StartsWith(Defs.classStartPHI))
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
                            curlycount = 0;

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
                            curlycount++;
                            inside = Inside.CurlyOpen;
                            previous.ContentInside[i] = inside;
                        }

                        if (content[i].ToString() == Defs.curlyClose)
                        {
                            curlycount--;

                            if (curlycount >= 0)
                            {
                                inside = Inside.CurlyClose;
                            }
                            else
                            {
                                inside = Inside.ClassClose;
                            }
                            previous.ContentInside[i] = inside;
                        }

                        if (inside != Inside.None)
                        {
                            inside = Inside.None;
                        }
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
      
        public static ContentProfile ProfileParenthesis(string content, ContentProfile previous)
        {
            bool parenthesisStart = false;

            int condStart = 0;

            for (int i = 0; i < content.Length; i++)
            {
                string letter = content[i].ToString();

                if (previous.ContentInside[i] == Inside.None)
                {
                    if (letter == Defs.paraOpen)
                    {
                        parenthesisStart = true;
                        condStart = i;
                    }

                    if (parenthesisStart)
                    {
                        if (letter == Defs.paraClose)
                        {
                            parenthesisStart = false;

                            RegionLabel regionLabel = RegionLabel.ConditionalBoolean;
                            string rvalue = content.Substring(condStart, i - condStart + 1);

                            if (rvalue.Contains(Defs.VariableSet))
                            {
                            }

                            for (int j = condStart; j <= i; j++)
                            {
                                previous.Labels[j] = regionLabel;
                            }
                        }
                    }
                }
            }

            return previous;
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

        public static List<int> ProfileEnclosingDepth(string Content, string EncloseType, string EncloseTypeEnd)
        {
            int[] depth = new int[Content.Length];

            ContentProfile prf = ProfilePrepare(Content);

            int dp = 0;

            for (int i = 0; i < Content.Length; i++)
            {
                if (prf.ContentInside[i] == Inside.None)
                {
                    if (Content[i].ToString() == EncloseType)
                    {
                        dp++;
                    }
                    else if (Content[i].ToString() == EncloseTypeEnd)
                    {
                        dp--;
                    }
                }

                depth[i] = dp;
            }

            return depth.ToList();
        }

        public static PhiMath.Opperation MatchesOpperation(string condition)
        {
            PhiMath.Opperation typ = PhiMath.Opperation.None;

            string? val = Defs.MathOpsList.Where(x => x == condition).FirstOrDefault();

            if (val != null)
            {
                if (val == Defs.MathInc) typ = PhiMath.Opperation.PlusEquals;
                if (val == Defs.MathDec) typ = PhiMath.Opperation.MinusEquals;
            }

            return typ;
        }
        public static ConditionalPairs.ConditionType MatchesCondition(string condition)
        {
            ConditionalPairs.ConditionType typ = ConditionalPairs.ConditionType.None;

            for (int i = 0; i < condition.Length; i++)
            {
                string? match = Defs.OpperCompareList.Where(
                    x => condition.Contains(x)
                    ).FirstOrDefault();

                bool cont = (match != null);

                if (cont)
                {
                    //remember it only jumps if it fails so use opposites
                    if (match == Defs.opperIs || match == Defs.opperIsAlt)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfEqual;
                    }
                    else if (match == Defs.opperNot || match == Defs.opperNotAlt)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfNotEqual;
                    }
                    else if (match == Defs.opperLesser)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfLess;
                    }
                    else if (match == Defs.opperLesserEqu)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfLessEqual;
                    }
                    else if (match == Defs.opperGreater)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfGreater;
                    }
                    else if (match == Defs.opperGreaterEqu)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfGreaterEqual;
                    }
                }
            }

            return typ;
        }
        public static ConditionalPairs.ConditionType MatchesInvertedCondition(string condition)
        {
            ConditionalPairs.ConditionType typ = ConditionalPairs.ConditionType.None;

            for (int i = 0; i < condition.Length; i++)
            {
                string? match = Defs.OpperCompareList.Where(
                    x => condition.Contains(x)
                    ).FirstOrDefault();

                bool cont = (match != null);

                if(cont)
                {
                    //remember it only jumps if it fails so use opposites
                    if (match == Defs.opperIs || match == Defs.opperIsAlt)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfNotEqual;
                    }
                    else if (match == Defs.opperNot || match == Defs.opperNotAlt)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfEqual;
                    }
                    else if (match == Defs.opperLesser)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfGreaterEqual;
                    }
                    else if (match == Defs.opperLesserEqu)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfGreater;
                    }
                    else if (match == Defs.opperGreater)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfLessEqual;
                    }
                    else if (match == Defs.opperGreaterEqu)
                    {
                        typ = ConditionalPairs.ConditionType.JumpIfLess;
                    }
                }
            }

            return typ;
        }
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
        public static string MatchesMath(string value)
        {
            string str = string.Empty;

            //later matches will override previous ones
            foreach (string m in Defs.MathSmallOpsList)
            {
                if (value.StartsWith(m))
                {
                    str = m;
                }
            }

            foreach (string m in Defs.MathOpsList)
            {
                if (value.StartsWith(m))
                {
                    str = m;
                }
            }

            return str;
        }
        public static string MatchesInstruct(string content, string prev)
        {
            string vname = string.Empty;

            foreach (string val in Defs.instructContainers)
            {
                if (ParseMisc.StartsWithAppendedValue(
                    content, 
                    val, 
                    Defs.TabSpaceClosureCharacters.ToList()))
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
                if (ParseMisc.StartsWithAppendedValue(
                    content,
                    val,
                    Defs.TabSpaceClosureCharacters.ToList()))
                {
                    vname = val;
                }
            }

            return vname;
        }
    }
}

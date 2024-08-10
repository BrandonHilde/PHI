using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using PhiBasicTranslator.Structure;
using System.Runtime.ConstrainedExecution;
using PhiBasicTranslator.ParseEngine;

namespace PhiBasicTranslator.TranslateUtilities
{
    internal class ASMx86_16BIT
    {
        public static readonly string varStrTyp = " db ";
        public static readonly string varIntTyp = " dd ";
        public static readonly string varStrNewLine = "10";
        public static readonly string varStrReturn = "13";
        public static readonly string varStrTab = "9";
        public static readonly string varStrEnd = "0";
        public static readonly string replaceLoopCondition = ";{LOOP CONDITION}";
        public static readonly string replaceLoopLimit = ";{LOOP LIMIT}";
        public static readonly string replaceLoopName = ";{LOOP NAME}";
        public static readonly string replaceLoopContent = ";{LOOP CONTENT}";
        public static readonly string replaceLoopContentName = ";{LOOP CONTENT NAME}";
        public static readonly string replaceLoopIncrement = ";{LOOP INCREMENT}";

        public static readonly string replaceIfCondition = ";{IF CONDITION}";
        public static readonly string replaceIfRightCompare = ";{IF COMPARE RIGHT}";
        public static readonly string replaceIfLeftCompare = ";{IF COMPARE LEFT}";
        public static readonly string replaceIfSkip = ";{IF SKIP}";


        public static readonly string loopSubIncrementByOne = "inc cx";
        public static readonly string loopSubDecrementByOne = "dec cx";

        public static readonly string jumpIfGreaterThan = "jg";
        public static readonly string jumpIfLessThan = "jl";
        public static readonly string jumpIfGreaterThanEqual = "jge";
        public static readonly string jumpIfLessThanEqual = "jle";
        public static readonly string jumpIfEqual = "je";
        public static readonly string jumpIfNotEqual = "jne";

        public static readonly string jumpIfCarry = "jc";
        public static readonly string jumpIfNoCarry = "jnc";
        public static readonly string jumpIfOverflow = "jo";
        public static readonly string jumpIfNoOverflow = "jno";

        public static readonly string prefixVariable = "VALUE_";
        public enum InheritType { External, BITS16 }
        public static List<string> GetInheritance(InheritType type)
        {
            if (type == InheritType.External)
            {
                // find code
            }
            else if (type == InheritType.BITS16)
            {
                return BIT16x86;
            }

            return new List<string>();
        }

        public static string FormatStringConvert(string value)
        {
            string vl = string.Empty;

            bool lastExit = false;

            for (int i = 0; i < value.Length; i++)
            {
                if(ParseUtilities.IsIgnorable(value, i))
                {
                    if (!lastExit) vl += "'";

                    if (value[i] == 'n')
                    {
                        vl += "," + varStrNewLine;
                        lastExit = true;
                    }
                    else if (value[i] == 'r')
                    {
                        vl += "," + varStrReturn;
                        lastExit = true;
                    }
                    else if (value[i] == 't')
                    {
                        vl += "," + varStrTab;
                        lastExit = true;
                    }
                    else if (value[i] == 'u')
                    {
                        //unimplemented
                    }
                }
                else if(value[i] != '\\')
                {
                    if (lastExit)
                    {
                        vl += ",'";
                        lastExit = false;
                    }

                    vl += value[i];
                }
                
            }

            if (vl.Length > 0)
            {

                if (vl.Last() == ',') vl += varStrEnd;
                else vl += "," + varStrEnd;
            }

            return vl;
        }
        public static string VarTypeConvert(PhiVariable varble, bool update = true)
        {
            string vr = string.Empty;

            if (varble != null)
            {
                string name = varble.Name;

                if(update) name = UpdateName(varble.Name);

                if (varble.varType == Inside.VariableTypeStr)
                {
                    vr = name + varStrTyp + FormatStringConvert(varble.ValueRaw);
                }
                else if (varble.varType == Inside.VariableTypeInt)
                {
                    vr = name + varIntTyp + varble.ValueRaw;
                }
            }

            return vr;
        }

        public static string UpdateName(string name)
        {
            return ASMx86_16BIT.prefixVariable + name;
        }

        public static List<string> InsertVars(List<string> Code, List<string> Values)
        {
            for (int v = 0; v < Values.Count; v++)
            {
                for (int i = 0; i < Code.Count; i++)
                {
                    string val = Defs.replaceVarStart;

                    if (Code[i].Contains(val))
                    {
                        Code[i] = Code[i].Replace(val, Values[v]);
                    }
                }
            }

            return Code;
        }

        public static List<string> MergeValues(List<string> Code, List<string> Values, string key)
        {
            List<string> result = new List<string>();

            int c = 0;
            int v = 0;

            int t = 0;

            while (t++ < Values.Count + Code.Count)
            {
                NextLinePair pair = NextLine(Code, Values, key, c, v);

                c = pair.IndexOne;
                v = pair.IndexTwo;

                result.Add(pair.Value);
            }

            return result;
        }

        private static NextLinePair NextLine(List<string> Code, List<string> Values, string key, int c, int v)
        {
            NextLinePair next = new NextLinePair();

            if (c < Code.Count)
            {
                if (Code[c].Contains(key))
                {
                    if (v < Values.Count)
                    {
                        next.Value = Values[v++];
                    }
                    else
                    {
                        next.Value = key;
                        c++;
                    }
                }
                else
                {
                    next.Value = Code[c++];
                }
            }

            next.IndexOne = c;
            next.IndexTwo = v;

            return next;
        }


        public static List<string> MergeSubCode(List<string> Code, List<string> SubCode, string Key)
        {
            List<string> sub = SubCode.Where(x=> x != Key).ToList();

            List<string> result = MergeValues(Code, sub, Key);

            return result;
        }
        public static List<string> ReplaceValue(List<string> Code, string Key, string Value)
        {
            for (int i = 0; i < Code.Count; i++)
            {
                if (Code[i].Contains(Key)) 
                { 
                    Code[i] = Code[i].Replace(Key, Value); 
                }
            }

            return Code;
        }
        public static List<string> InsertValues(List<string> Code, List<string> Values)
        {
            for (int v = 0; v < Values.Count; v++)
            {
                for (int i = 0; i < Code.Count; i++)
                {
                    string val = Defs.replaceValueStart;

                    if (Code[i].Contains(val))
                    {
                        Code[i] = Code[i].Replace(val, Values[v]);
                    }
                }
            }

            return Code;
        }

        public static List<string> InsertCodeLines(List<string> Code, List<string> Lines, int index)
        {
            List<string> CodeLines = new List<string>();

            for (int i = 0; i < Code.Count; i++)
            {
                string val = Defs.replaceCodeStart; //;{CODE:0}

                if (Code[i].Contains(val))
                {
                    for(var n = 0; n < Lines.Count; n++)
                    {
                        CodeLines.Add(Lines[n]);
                    }
                }
                else
                {
                    CodeLines.Add(Code[i]);
                }
            }

            return CodeLines;
        }

        #region IF ELSE

        public static List<string> InstructIfContent_BITS16 = new List<string>()
        {
            "   cmp " + replaceIfLeftCompare + ", " + replaceIfRightCompare,
            "   " + replaceIfCondition + ", " + replaceIfSkip
        };

        #endregion

        #region WHILE LOOPS

        public static List<string> InstructWhileContent_BITS16 = new List<string>()
        {
            replaceLoopContentName + ":",
            replaceLoopContent,
        };

        public static List<string> InstructWhileCall_BITS16 = new List<string>()
        {
            "   call " + replaceLoopName,
        };

        public static List<string> InstructWhileStart_BITS16 = new List<string>()
        {
            replaceLoopName + ":",
            "   mov cx, [" + Defs.replaceValueStart +"]"
        };

        public static List<string> InstructWhileCheck_BITS16 = new List<string>()
        {
            ".loop_check:",
            "   cmp cx, [" + replaceLoopLimit + "]",
            "   " + replaceLoopCondition + " .loop_done", //jge jg je jl jle etc...
            "   " + replaceLoopIncrement, // inc cx
            "   mov [" + Defs.replaceValueStart +"], cx",
            "   call " + replaceLoopContentName,
            "   jmp .loop_check"
        };

        public static List<string> InstructWhileDone_BITS16 = new List<string>()
        {
            ".loop_done:",
            "   mov [" + Defs.replaceValueStart + "], cx",
            "   ret"
        };

        #endregion
        #region LOGS
        public static List<string> InstructLogString_BITS16 = new List<string>()
        {
            "   mov si, " + Defs.replaceValueStart,
            "   call print_log"
        };

        public static List<string> InstructLogInt_BITS16 = new List<string>()
        {
            "   mov ax, [" + Defs.replaceValueStart + "]",
            "   call print_int"
        };
        #endregion

        public static List<string> BIT16x86 = new List<string>()
        {
            //"[BITS 16]",
            "ORG 0x7c00",
            "",
            "start:",
            "   xor ax, ax",
            "   mov ds, ax",
            "   mov es, ax",
            "   mov ss, ax",
            "   mov sp, 0x7c00",
            "",
            Defs.replaceCodeStart, //;{CODE}
            "",
            "   jmp $",
            "",
            Defs.replaceIncludes, // ;{INCLUDES}
            "",
            Defs.replaceVarStart,//;{VALUES}
            "times 510-($-$$) db 0",
            "dw 0xaa55"
        };

        public static List<string> PrintLog_x86BITS16 = new List<string>()
        {
             "print_log:",
            "   mov ah, 0x0E",
            ".loop:",
            "   lodsb",
            "   cmp al, 0",
            "   je .done",
            "   int 0x10",
            "   jmp .loop",
            ".done:",
            "   ret",
            "",
        };

        public static List<string> PrintInt_x86BITS16 = new List<string>()
        {
            "print_int:",
            "    push bp",
            "    mov bp, sp",
            "    push dx",
            "",
            "    cmp ax, 10",
            "    jge .div_num",
            "",
            "    add al, '0'",
            "    mov ah, 0x0E",
            "    int 0x10",
            "    jmp .done",
            "",
            ".div_num:",
            "    xor dx, dx",
            "    mov bx, 10",
            "    div bx",
            "    push dx",
            "    call print_int",
            "    pop dx",
            "    add dl, '0'",
            "    mov ah, 0x0E",
            "    mov al, dl",
            "    int 0x10",
            "",
            ".done:",
            "    pop dx",
            "    mov sp, bp",
            "    pop bp",
            "    ret",
            ""
        };
    }
}

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
using static System.Formats.Asn1.AsnWriter;
using System.Diagnostics.Metrics;

namespace PhiBasicTranslator.TranslateUtilities
{
    internal class ASMx86_16BIT
    {
        public static readonly string varStrTyp = " db ";
        public static readonly string varIntTyp = " dd ";
        public static readonly string varTimes = " times ";
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
        public static readonly string replaceLoopStart = ";{LOOP Start}";

        public static readonly string replaceIfCondition = ";{IF CONDITION}";
        public static readonly string replaceIfRightCompare = ";{IF COMPARE RIGHT}";
        public static readonly string replaceIfLeftCompare = ";{IF COMPARE LEFT}";
        public static readonly string replaceIfName = ";{IF NAME}";
        public static readonly string replaceIfJump = ";{IF JUMP}";
        public static readonly string replaceIfContent = ";{IF CONTENT}";


        public static readonly string loopSubIncrementByOne = "inc cx";
        public static readonly string loopSubDecrementByOne = "dec cx";

        public static readonly string callLabel = "call ";

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
        public static readonly string suffixContent = "_CONTENT";
        public enum InheritType { External, BITS16, BITS16Video }
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
            else if(type == InheritType.BITS16Video)
            {
                return BIT16x86_Bootloader;
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

                string arr = ParseVariables.GetRawValueArrayLength(varble.ValueRaw);

                if (varble.varType == Inside.VariableTypeStr)
                {
                    if (arr != string.Empty)
                    {
                        vr = name + ":" + varTimes + arr + varStrTyp + varStrEnd;
                    }
                    else
                    {
                        vr = name + varStrTyp + FormatStringConvert(varble.ValueRaw);
                    }
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
            List<string> cde = new List<string>();

            cde.AddRange(Code);

            for (int i = 0; i < cde.Count; i++)
            {
                if (cde[i].Contains(Key)) 
                {
                    cde[i] = cde[i].Replace(Key, Value); 
                }
            }

            return cde;
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

        public static List<string> InstructIfCheck_BITS16 = new List<string>()
        {
            replaceIfName + ":",
            "   mov ax, " + replaceIfLeftCompare,
            "   cmp ax, " +  replaceIfRightCompare,
            "   " + replaceIfJump + " " + replaceIfName + suffixContent,
            "   ret"
        };

        public static List<string> InstructIfContent_BITS16 = new List<string>()
        {
            replaceIfName + suffixContent+ ":",
            "   " + replaceIfContent,
            "   ret"
        };

        public static List<string> InstructIfCall_BITS16 = new List<string>()
        {
            "   call " + replaceIfName,
        };

        #endregion

        #region WHILE LOOPS

        public static List<string> InstructWhileContent_BITS16 = new List<string>()
        {
            replaceLoopContentName + suffixContent+ ":",
            replaceLoopContent,
            "   ret"
        };

        public static List<string> InstructWhileCall_BITS16 = new List<string>()
        {
            "   call " + replaceLoopName,
        };

        public static List<string> InstructWhileStart_BITS16 = new List<string>()
        {
            replaceLoopName + ":",
            "   mov cx, " + replaceLoopStart,
            "   mov [" + Defs.replaceValueStart +"], cx"
        };

        public static List<string> InstructWhileCheck_BITS16 = new List<string>()
        {
            ".loop_check:",
            "   mov cx, [" + Defs.replaceValueStart +"]",
            "   cmp cx, [" + replaceLoopLimit + "]",
            "   " + replaceLoopCondition + " .loop_done", //jge jg je jl jle etc...
            "   call " + replaceLoopContentName + suffixContent,
            "   mov cx, [" + Defs.replaceValueStart +"]",
            "   " + replaceLoopIncrement, // inc cx
            "   mov [" + Defs.replaceValueStart +"], cx",
            "   jmp .loop_check"
        };

        public static List<string> InstructWhileDone_BITS16 = new List<string>()
        {
            ".loop_done:",
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


        #region ASK
        public static List<string> InstructASK_BITS16 = new List<string>()
        {
            "   mov di, " + Defs.replaceValueStart,
            "   call get_input"
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

        public static List<string> BIT16x86_Bootloader = new List<string>()
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
            Defs.replaceIncludes, // ;{INCLUDES}
            "",
            Defs.replaceVarStart,//;{VALUES}
            "times 510-($-$$) db 0",
            "dw 0xaa55"
        };

        /// <summary>
        ///  remember to add additional options
        /// </summary>
        public static List<string> BIT16x86_SectorPrep = new List<string>()
        {
            "OS16BITVideo_PrepSectorTwo:",
            "   mov ah, 0x02    ; BIOS read sector",
            "   mov al, 1       ; Number of sectors",
            "   mov ch, 0       ; Cylinder number",
            "   mov dh, 0       ; Head number",
            "   mov cl, 2       ; Sector number",
            "   mov bx, 0x7E00  ; Load address",
            "   int 0x13",
            "   ret"
        };

        public static List<string> BIT16x86_ProgramableInteruptTimer = new List<string>()
        {
            "OS16BITVideo_PrepInteruptTimer:",
            "   cli    ; Set up the PIT",
            "   mov al, 00110100b    ; Channel 0, lobyte/hibyte, rate generator",
            "   out PIT_COMMAND, al",
            "       ; Set the divisor",
            "   mov ax, DIVISOR",
            "   out PIT_CHANNEL_0, al    ; Low byte",
            "   mov al, ah",
            "   out PIT_CHANNEL_0, al    ; High byte",
            "   ; Set up the timer ISR",
            "   mov word [0x0020], timer_interrupt",
            "   mov word [0x0022], 0x0000    ; Enable interrupts",
            "   sti",
            "   ret"
        };

        public static List<string> BIT16x86_MainLoop = new List<string>()
        {
            "OS16BITVideo_main_loop:",
            "   hlt                     ; Halt until next interrupt",
            "   jmp main_loop"
        };

        public static List<string> BIT16x86_InteruptEvent = new List<string>()
        {
            "OS16BITVideo_timer_interrupt:",
            Defs.replaceCodeStart,
            "   mov al, 0x20",
            "   out 0x20, al",
            "   iret"
        };


        public static List<string> BIT16x86_JumpSectorTwo = new List<string>()
        {
            "OS16BITVideo_JumpToSectorTwo:",
            "   call OS16BITVideo_PrepSectorTwo",
            "   jmp 0x7E00 ; jump to sector two", // jump to sector two"
             "   ret"
        };

        public static List<string> BIT16x86_VideoMode = new List<string>()
        {
            "OS16BITVideo_EnableVideoMode:",
            "   mov ax, 0x13",
            "   int 0x10",
            "   ret"
        };

        public static List<string> BIT16x86_WaitForKeyPress = new List<string>()
        {
            "OS16BITVideo_WaitForKeyPress:",
            "   mov ah, 0x00",
            "   int 0x16",
            "   ret"
        };

        public static List<string> AskInput_x86BITS16 = new List<string>()
        {
            "get_input:",
            "   xor cx, cx",
            ".loop:",
            "    mov ah, 0",
            "    int 0x16",
            "    cmp al, 0x0D",
            "    je .done",
            "    stosb   ",
            "    inc cx  ",
            "    mov ah, 0x0E",
            "    int 0x10",
            "    jmp .loop",
            "",
            ".done:",
            "    mov byte [di], 0 ",
            "    ret"
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

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
using System.Diagnostics.Metrics;

namespace PhiBasicTranslator.TranslateUtilities
{
    internal class ASMx86_16BIT
    {
        public static readonly string varStrTyp = " db ";
        public static readonly string varWrdTyp = " dw ";
        public static readonly string varIntTyp = " dd ";
        public static readonly string varBytTyp = " db ";
        public static readonly string varTimes = " times ";
        public static readonly string varStrNewLine = "10";
        public static readonly string varStrReturn = "13";
        public static readonly string varStrTab = "9";
        public static readonly string varStrEnd = "0";

        public static readonly string registerEaxMath32 = "eax";
        public static readonly string registerEbxMath32 = "ebx";
        public static readonly string registerEcxMath32 = "ecx";
        public static readonly string registerEdxMath32 = "edx";

        public static readonly string incJumpToSectorTwo = "OS16BIT_JumpToSectorTwo";
        public static readonly string incPrepSectorTwo = "OS16BIT_PrepToSectorTwo";

        public static readonly List<string> incSectorsList = new List<string>()
        {
            incJumpToSectorTwo.Replace('_', '.'),
            incPrepSectorTwo.Replace('_', '.'),
        };

        public static readonly string incWaitForKey = "OS16BIT_WaitForKeyPress";
        public static readonly string incKeyboardInterupt = "OS16BIT_SetupKeyboardInterupt";
        public static readonly string incKeyboardEvent = "OS16BIT_KeyboardEvent";

        public static readonly string incGetKey = "OS16BIT_GetKey";
        public static readonly string incIsKeyDown = "OS16BIT_IsKeyDown";

        public static readonly List<string> incKeyboardList = new List<string>()
        {
            incWaitForKey.Replace('_', '.'),
            incKeyboardInterupt.Replace('_', '.'),
            incKeyboardEvent.Replace('_', '.')
        };

        public static readonly string incTimerEvent = "OS16BIT_TimerEvent";
        public static readonly string incSetupTimerInterupt = "OS16BIT_SetupInteruptTimer";
        public static readonly string incTimerInterupt = "OS16BIT_timer_interrupt";

        public static readonly List<string> incTimerList = new List<string>()
        {
            incTimerEvent.Replace('_', '.'),
            incSetupTimerInterupt.Replace('_', '.'),
            incTimerInterupt.Replace('_', '.'),
        };

        public static readonly string incEnableVideo = "OS16BITVideo_EnableVideoMode";
        public static readonly string incDrawRectangle = "OS16BITVideo_DrawRectangle";
        public static readonly string incDrawPixel = "OS16BITVideo_DrawPixel";

        public static readonly List<string> incDrawingList = new List<string>()
        {
            incEnableVideo.Replace('_', '.'),
            incDrawRectangle.Replace('_', '.'),
            incDrawPixel.Replace('_', '.'),
        };

        public static readonly List<string> excludeMethodCall = new List<string>()
        {
            incGetKey,
            incIsKeyDown
        };

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
        public static readonly string replaceElseContent = ";{ELSE CONTENT}";

        public static readonly string replaceColorset = ";{COLOR SET}";
        public static readonly string replaceVarName = ";{VAR NAME}";

        public static readonly string KeyCodeVar = "KeyCodeValue";

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
        public enum InheritType { External, BITS16, BITS16Video, BITS16SectorTwo }
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
            else if(type== InheritType.BITS16SectorTwo)
            {
                return BIT16x86_SectorTwo;
            }

            return new List<string>();
        }

        public static string FormatVarToString(PhiVariable varble)
        {
            string vl = string.Empty;

            foreach (string v in varble.Values)
            {
                vl += v + ",";
            }

            if (varble.varType == Inside.VariableTypeStr)
            {
                vl += "0";
            }

            if(vl.EndsWith(","))
            {
                vl = vl.Substring(0, vl.Length - 1);
            }

            return vl;
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
                        //vr = name + varStrTyp + FormatStringConvert(varble.ValueRaw);

                        vr = name + varStrTyp + FormatVarToString(varble);
                    }
                }
                else if (varble.varType == Inside.VariableTypeInt)
                {
                    vr = name + varIntTyp + FormatVarToString(varble);
                }
                else if (varble.varType == Inside.VariableTypeBln)
                {
                    vr = name + varStrTyp;

                    if (varble.ValueRaw.ToLower() == "true")
                    {
                        vr += "1";
                    }
                    else if (varble.ValueRaw.ToLower() == "false")
                    {
                        vr += "0";
                    }
                    else
                    {
                        //remember to resolve later
                        vr += ASMx86_16BIT.UpdateName(varble.ValueRaw);
                    }

                }
                else if (varble.varType == Inside.VariableTypeByt)
                {
                    vr = name + varStrTyp;

                    if(ParseMisc.IsNumber(varble.ValueRaw))
                    {
                        vr += varble.ValueRaw; //add limits later
                    }
                    else
                    {
                        vr += ASMx86_16BIT.UpdateName(varble.ValueRaw);
                    }
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
            "   mov eax, " + replaceIfLeftCompare,
            "   cmp eax, " +  replaceIfRightCompare,
            "   " + replaceIfJump + " " + replaceIfName + suffixContent,
            "",
            replaceElseContent,
            "",
            "   ret"
        };

        public static List<string> InstructIfCheck_BITS8 = new List<string>()
        {
            replaceIfName + ":",
            "   mov al, " + replaceIfLeftCompare,
            "   cmp al, " +  replaceIfRightCompare,
            "   " + replaceIfJump + " " + replaceIfName + suffixContent,
            "",
            replaceElseContent,
            "",
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
            "   mov ax, word " + Defs.replaceValueStart,
            "   call print_int"
        };
        public static List<string> InstructLogByte_BITS16 = new List<string>()
        {
            "   mov ax, word " + Defs.replaceValueStart,
            "   xor ah, ah",
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

        #region Bootloaders

        public static List<string> BIT16x86 = new List<string>()
        {
            //"[BITS 16]",
            "ORG 0x7c00",
            "",
            Defs.replaceConstStart,
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
            Defs.replaceConstStart,
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

        public static List<string> BIT16x86_SectorTwo = new List<string>()
        {
            "ORG 0x7E00",
            "",
            Defs.replaceConstStart,
            "",
            "start:",
            "",
            Defs.replaceCodeStart, //;{CODE}
            "",
            "   jmp $",
            "",
            Defs.replaceIncludes, // ;{INCLUDES}
            "",
            Defs.replaceVarStart,//;{VALUES}
            ""
        };
        #endregion

        #region Sectors
        /// <summary>
        ///  remember to add additional options
        /// </summary>
        public static List<string> BIT16x86_SectorPrep = new List<string>()
        {
            "OS16BIT_PrepSectorTwo:",
            "   mov ah, 0x02    ; BIOS read sector",
            "   mov al, 6       ; Number of sectors", // may need to change number later
            "   mov ch, 0       ; Cylinder number",
            "   mov dh, 0       ; Head number",
            "   mov cl, 2       ; Sector number",
            "   mov bx, 0x7E00  ; Load address",
            "   int 0x13",
            "   ret"
        };

        public static List<string> BIT16x86_JumpSectorTwo = new List<string>()
        {
            "OS16BIT_JumpToSectorTwo:",
            "   call OS16BIT_PrepSectorTwo",
            "   jmp 0x7E00 ; jump to sector two", // jump to sector two"
             "   ret"
        };

        #endregion

        #region Timers


        public static List<string> BIT16x86_TimerConstants = new List<string>()
        {
            "; interupt timer constants",
            "PIT_COMMAND    equ 0x43",
            "PIT_CHANNEL_0  equ 0x40",
            "PIT_FREQUENCY  equ 1193180  ; Base frequency",
            "DESIRED_FREQ   equ 60      ; Desired interrupt frequency ",
            "DIVISOR        equ PIT_FREQUENCY / DESIRED_FREQ"
        };

        public static List<string> BIT16x86_ProgramableInteruptTimer = new List<string>()
        {
            "OS16BIT_SetupInteruptTimer:",
            "   cli    ; Set up the PIT",
            "   mov al, 00110100b    ; Channel 0, lobyte/hibyte, rate generator",
            "   out PIT_COMMAND, al",
            "       ; Set the divisor",
            "   mov ax, DIVISOR",
            "   out PIT_CHANNEL_0, al    ; Low byte",
            "   mov al, ah",
            "   out PIT_CHANNEL_0, al    ; High byte",
            "   ; Set up the timer ISR",
            "   mov word [0x0020], OS16BIT_timer_interrupt",
            "   mov word [0x0022], 0x0000    ; Enable interrupts",
            "   sti",
            "   ret"
        };

        public static List<string> BIT16x86_Interupt = new List<string>()
        {
            "OS16BIT_timer_interrupt:",
            "   call OS16BIT_TimerEvent",
            "   mov al, 0x20",
            "   out 0x20, al",
            "   iret"
        };

        public static List<string> BIT16x86_TimerEvent = new List<string>()
        {
            incTimerEvent + ":",
            Defs.replaceCodeStart,
            "   ret"
        };

        #endregion

        #region Graphics
        public static List<string> BIT16x86_VideoMode = new List<string>()
        {
            "OS16BITVideo_EnableVideoMode:",
            "   mov ax, 0x13",
            "   int 0x10",
            "   ret"
        };

        public static List<string> VarList_DrawRectangle = new List<string>()
        {
            "DrawRectX",
            "DrawRectY",
            "DrawRectW",
            "DrawRectH",
            "DrawRectColor"
        };

        public static List<Inside> VarType_DrawRectangle = new List<Inside>()
        {
            Inside.VariableTypeInt,
            Inside.VariableTypeInt,
            Inside.VariableTypeInt,
            Inside.VariableTypeInt,
            Inside.VariableTypeByt
        };

        public static List<string> BIT16x86_DrawVariables = new List<string>()
        {
             "; drawing variables",
            VarList_DrawRectangle[0] + varIntTyp + "0",
            VarList_DrawRectangle[1] + varIntTyp + "0",
            VarList_DrawRectangle[2] + varIntTyp + "10",
            VarList_DrawRectangle[3] + varIntTyp + "10",
            VarList_DrawRectangle[4] + varBytTyp + "0xA"
        };

        public static List<string> BIT16x86_DrawConstants = new List<string>()
        {
           
            "; drawing constants",
            "DRAW_START equ 0xA0000",
            "SCREEN_WIDTH equ 320",
            "SCREEN_HEIGHT equ 200",
            "BUFFER_SIZE equ DRAW_START + (SCREEN_WIDTH * SCREEN_HEIGHT)",
            ";color array",
            Defs.VariableOpenDeclare + "Colors.Black equ 0x0  ;Black",
            Defs.VariableOpenDeclare + "Colors.Blue equ 0x1  ;Blue",
            Defs.VariableOpenDeclare + "Colors.Green equ 0x2  ;Green",
            Defs.VariableOpenDeclare + "Colors.Cyan equ 0x3  ;Cyan",
            Defs.VariableOpenDeclare + "Colors.Red equ 0x4  ;Red",
            Defs.VariableOpenDeclare + "Colors.Magenta equ  0x5  ;Magenta",
            Defs.VariableOpenDeclare + "Colors.Brown equ  0x6  ;Brown",
            Defs.VariableOpenDeclare + "Colors.LightGray equ  0x7  ;Light Gray",
            Defs.VariableOpenDeclare + "Colors.Gray equ  0x8  ;Gray",
            Defs.VariableOpenDeclare + "Colors.LightBlue equ  0x9  ;Light Blue",
            Defs.VariableOpenDeclare + "Colors.LightGreen equ 0xA  ;Light Green",
            Defs.VariableOpenDeclare + "Colors.LightCyan   equ 0xB  ;Light Cyan",
            Defs.VariableOpenDeclare + "Colors.LightRed   equ 0xC  ;Light Red",
            Defs.VariableOpenDeclare + "Colors.LightMagenta   equ 0xD  ;Light Magenta",
            Defs.VariableOpenDeclare + "Colors.Yellow equ 0xE  ;Yellow ",
            Defs.VariableOpenDeclare + "Colors.White equ 0xF  ;White",
            ""
        };

        public static List<string> BIT16x86_DrawRectangle = new List<string>()
        {
            incDrawRectangle + ":",
            "   mov edi, DRAW_START; Start of VGA memory",
            "   mov eax, [" + VarList_DrawRectangle[1] + "]", //rx
            "   mov ecx, SCREEN_WIDTH",
            "   mul ecx",
            "   add eax, [" + VarList_DrawRectangle[0] + "]", //ry
            "   add edi, eax",
            "   mov edx, 0",
            ".draw_row:",
            "   mov ecx, 0",
            ".draw_pixel:",
            "   cmp edi, BUFFER_SIZE",
            "   jl .continue_draw",
            "   mov edi, DRAW_START",
            ".continue_draw:",
            "   mov al, [" + VarList_DrawRectangle[4] + "]",
            "   mov byte [edi], al",
            "   inc edi",
            "   inc ecx",
            "   cmp ecx, [" + VarList_DrawRectangle[2] + "]", //rw
            "   jl .draw_pixel",
            "   add edi, SCREEN_WIDTH",
            "   sub edi, [" + VarList_DrawRectangle[2] + "]", //rw
            "   inc edx",
            "   cmp edx, [" + VarList_DrawRectangle[3] + "]", //rh
            "   jl .draw_row",
            "   ret"
        };

        #endregion


        #region Stack
        
        public static List<string> BIT32x86_PUSH = new List<string>()
        {
            "",
            "   push " + Defs.replaceValueStart,
            ""
        };

        public static List<string> BIT32x86_POP = new List<string>()
        {
            "",
            "   pop " + Defs.replaceValueStart,
            ""
        };

        public static List<string> ASMx86_MOV = new List<string>()
        {
            "",
            "   mov " + replaceVarName + ", " + Defs.replaceValueStart,
            ""
        };


        #endregion

        #region MATH

        public static List<string> BIT32x86_AddMath = new List<string>()
        {
            "",
            "   add eax," + Defs.replaceValueStart,
            ""
        };


        public static List<string> BIT32x86_SubMath = new List<string>()
        {
            "",
            "   sub eax," + Defs.replaceValueStart,
            ""
        };

        public static List<string> BIT32x86_MulMath = new List<string>()
        {
            "",
            "   mul ecx",
            ""
        };

        public static List<string> BIT32x86_DivMath = new List<string>()
        {
            "",
            "   div ebx",
            ""
        };

        public static List<string> BIT32x86_AddVariable = new List<string>()
        {
            "",
            "   mov eax, " + replaceVarName,
            "   add eax," + Defs.replaceValueStart,
            "   mov " + replaceVarName + ", eax",
            ""
        };

        public static List<string> BIT32x86_SubVariable = new List<string>()
        {
            "",
            "   mov eax, " + replaceVarName,
            "   sub eax," + Defs.replaceValueStart,
            "   mov " + replaceVarName + ", eax",
            ""
        };

        public static List<string> BIT32x86_DivVariable = new List<string>()
        {
            "",
            "   xor edx, edx",
            "   mov eax, " + replaceVarName,
            "   mov ebx," + Defs.replaceValueStart,
            "   div ebx",
            "   mov " + replaceVarName + ", eax",
            ""
        };

        public static List<string> BIT32x86_MulVariable = new List<string>()
        {
            "",
            "   mov eax, " + replaceVarName,
            "   mov ecx," + Defs.replaceValueStart,
            "   mul ecx",
            "   mov " + replaceVarName + ", eax",
            ""
        };

        public static List<string> BIT32x86_ModVariable = new List<string>()
        {
            "",
            "   xor edx, edx",
            "   mov eax, " + replaceVarName,
            "   mov ebx," + Defs.replaceValueStart,
            "   div ebx",
            "   mov " + replaceVarName + ", edx",
            ""
        };


        public static List<string> BIT32x86_SetVariable = new List<string>()
        {
            "",
            "   mov eax, " + Defs.replaceValueStart,
            "   mov " + replaceVarName + ", eax",
            ""
        };

        public static List<string> BIT32x86_SetVariableFromStack = new List<string>()
        {
            "",
            "   mov " + replaceVarName + ", eax",
            ""
        };

        public static List<string> BIT16x86_SetVariable = new List<string>()
        {
            "",
            "   mov ax, " + Defs.replaceValueStart,
            "   mov " + replaceVarName + ", ax",
            ""
        };

        public static List<string> BIT8x86_SetVariable = new List<string>()
        {
            "",
            "   mov al, " + Defs.replaceValueStart,
            "   mov " + replaceVarName + ", al",
            ""
        };
        #endregion

        #region Arrays

        public static List<string> BIT32x86_RetrieveArrayValue = new List<string>()
        {
            "   mov eax, [" + Defs.replaceValueStart + " + ebx]"
        };

        #endregion

        #region Keyboard

        public static List<string> KeyCodeValue = new List<string>()
        {
            KeyCodeVar + varStrTyp + "0"
        };

        public static List<string> BIT16x86_ScanKeyTable = new List<string>()
        {
            "key_down_table:",
            "   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            "   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            "   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            "   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            "   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            "   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
            "",
            "scan_code_table:",
            "   db 0, 0, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 0, 0",
            "   db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 0, 0",
            "   db 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', \"'\", '`', 0, '\\'",
            "   db 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '"
        };

        public static List<string> BIT16x86_KeyboardSetup = new List<string>()
        {
            incKeyboardInterupt + ":",
            "   ; Set up the interrupt handler",
            "   cli                     ; Disable interrupts",
            "   mov ax, 0",
            "   mov es, ax",
            "   mov word [es:9*4], keyboard_handler",
            "   mov [es:9*4+2], cs",
            "   sti                     ; Enable interrupts",
            "   ret"
        };

        public static List<string> OLD_KeyboardInterupt = new List<string>()
        {
            "keyboard_handler:",
            "   push ax",
            "   push bx",
            "   in al, 0x60             ; Read scan code ",
            "   xor bh, bh",
            "   mov bl, al",
            "   ; Convert scan code to ASCII (simplified)",
            "   mov al, [scan_code_table + bx]",
            "   cmp al, 0",
            "   je .key_up",
            "   mov bx, key_down_table",
            "   add bx, ax",
            "   mov byte [bx], 1",
            "   jmp .done",
            ".key_up:",
            "   mov bx, key_down_table",
            "   add bx, ax",
            "   mov byte [bx], 0",
            "   jmp .done",
            ".done:",
            "   call " + incKeyboardEvent,
            "   mov al, 0x20            ; Send End of Interrupt",
            "   out 0x20, al",
            "   pop bx",
            "   pop ax",
            "   iret"
        };

        public static List<string> OS_KeyboardHandler = new List<string>()
        {
            "",
            "keyboard_handler:",
            "   push ax",
            "   push bx",
            "   in al, 0x60             ; Read scan code",
            "   test al, 0x80",
            "   jz .key_down",
            "   ;key up event",
            "   and al, 0x7F            ; Clear the key release bit",
            "   xor bx, bx",
            "   mov bl, al",
            "   mov al, [scan_code_table + bx]  ; Convert scan code to ASCII",
            "",
            "   mov [" + KeyCodeVar + "], al",
            "",
            "   xor bx, bx",
            "   mov bl, byte [" + KeyCodeVar + "]",
            "   ;add bx, key_down_table" ,
            "   mov byte [key_down_table + bx], 0x00",
            "",
            "   jmp .done",
            "   ",
            "   cmp al, 0               ; Check if it's a valid key",
            "   je .done",
            "",
            ".key_down:",
            "   xor bx, bx",
            "   mov bl, al",
            "   ; Convert scan code to ASCII (simplified)",
            "   mov al, [scan_code_table + bx]",
            "",
            "   mov [" + KeyCodeVar + "], al",
            "",
            "   xor bx, bx",
            "   mov bl, byte [" + KeyCodeVar + "]",
            "   ;add bx, key_down_table" ,
            "   mov byte [key_down_table + bx], 0x01",
            ".done:",
            "   call " + incKeyboardEvent,
            "   mov al, 0x20            ; Send End of Interrupt",
            "   out 0x20, al",
            "   pop bx",
            "   pop ax",
            "   iret"
        };

        public static List<string> BIT16x86_GetKey = new List<string>()
        {
            "   mov al, " + "[" + KeyCodeVar + "]",
            "   mov byte [" + replaceVarName + "], al"
        };

        public static List<string> BIT16x86_IsKeyDown = new List<string>()
        {
            "   xor bx, bx",
            "   mov bl, byte " + Defs.replaceValueStart,
            "   add bx, key_down_table",
            "   mov ax, [bx]",
            "   xor ah, ah",
            "   mov byte [" + replaceVarName + "], al"
        };

        public static List<string> BIT16x86_KeyboardEvent = new List<string>()
        {
            incKeyboardEvent + ":",
            Defs.replaceCodeStart,
            "   ret"
        };

        public static List<string> BIT16x86_WaitForKeyPress = new List<string>()
        {
            "OS16BIT_WaitForKeyPress:", 
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

        #endregion

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

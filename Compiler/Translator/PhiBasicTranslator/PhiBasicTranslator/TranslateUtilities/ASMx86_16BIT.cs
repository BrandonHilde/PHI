﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using PhiBasicTranslator.Structure;
using System.Runtime.ConstrainedExecution;

namespace PhiBasicTranslator.TranslateUtilities
{
    internal class ASMx86_16BIT
    {
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

        public static List<string> InstructLogString_BITS16 = new List<string>()
        {
            "   mov si, " + Defs.replaceValueStart,
            "   call print_log"
        };

        public static List<string> InstructLogInt_BITS16 = new List<string>()
        {
            "   mov ax, " + Defs.replaceValueStart,
            "   call print_int"
        };

        public static List<string> BIT16x86 = new List<string>()
        {
            "BITS 16",
            "org 0x7c00",
            "",
            "start:",
            "   xor ax, ax",
            "   mov ds, ax",
            "   mov es, ax",
            "   mov ss, ax",
            "   mov sp, 0x7c00",
            "",
            Defs.replaceCodeStart, //;{CODE:0}
            "",
            Defs.replaceIncludes,
            "",
            Defs.replaceVarStart,//;{VALUES:0}
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
            "    push ebp",
            "    mov ebp, esp",
            "    push edx",
            "",
            "    cmp eax, 10",
            "    jge .div_num",
            "",
            "    add al, '0'",
            "    mov ah, 0x0E",
            "    int 0x10",
            "    jmp .done",
            "",
            ".div_num:",
            "    xor edx, edx",
            "    mov ebx, 10",
            "    div ebx",
            "    push edx",
            "    call print_int",
            "    pop edx",
            "    add dl, '0'",
            "    mov ah, 0x0E",
            "    mov al, dl",
            "    int 0x10",
            "",
            ".done:",
            "    pop edx",
            "    mov esp, ebp",
            "    pop ebp",
            "    ret",
            ""
        };
    }
}

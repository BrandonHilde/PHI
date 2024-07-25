﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using PhiBasicTranslator.Structure;

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
                    string val = Defs.replaceVarStart + v + Defs.curlyClose;

                    if (Code[i].Contains(val))
                    {
                        Code[i] = Code[i].Replace(val, Values[v]);
                    }
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
                    string val = Defs.replaceValueStart + v + Defs.curlyClose;

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
                string val = Defs.replaceCodeStart + index + Defs.curlyClose; //;{CODE:0}

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

        public static List<string> InstructLogBITS16 = new List<string>()
        {
            "   mov si, " + Defs.replaceValueStart + "0" + Defs.curlyClose,
            "   call print_log"
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
            Defs.replaceCodeStart + "0" + Defs.curlyClose,
            "",
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
            "",
            Defs.replaceVarStart + "0" + Defs.curlyClose,
            "times 510-($-$$) db 0",
            "dw 0xaa55"
        };
    }
}

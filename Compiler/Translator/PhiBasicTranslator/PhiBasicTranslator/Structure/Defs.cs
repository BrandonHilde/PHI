using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public static class Defs
    {
        public static readonly string classStartPHI = "phi.";
        public static readonly string classStartx86ASM = "asm.";
        public static readonly string classStartArmASM = "arm.";
        public static readonly string curlyOpen = "{";
        public static readonly string curlyClose = "}";
        public static readonly string squareOpen = "[";
        public static readonly string squareClose = "]";
        public static readonly string Sys = "sys.";
        public static readonly string SysVideoMode = "videoMode";
        public static readonly string SysTextMode = "textMode";
        public static readonly string AccessDeclarePublic = "public";
        //public static readonly string AccessDeclarePrivate = "private";
        public static readonly string AccessDeclareSafe = "safe";
        public static readonly string MethodEndDeclare = "end";
        public static readonly string ValueStringDelcare = "'";
        public static readonly string TypeRawDeclare = "~";
        public static readonly string ClassInherit = ":";
        public static readonly string VariableSet = ":";
        public static readonly string Comment = "#";
        public static readonly string CommentLine = "\r\n";
        public static readonly string IgnoreCharacter = "\\";

        public static readonly string NameClosureCharacters = " \t\r\n{:";
        public static readonly string VariableSetClosure = ";";
        public static readonly string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_1234567890";
        public static readonly string Numbers = "1234567890.-";

        public static readonly string varSTR = "str";
        public static readonly string varINT = "int";
        public static readonly string varDEC = "dec";
        public static readonly string varFIN = "fin";
        public static readonly string varVAR = "var";
        public static readonly string varBYT = "byt";


        public static readonly List<string> VariableTypes = new List<string>()
        {
            varSTR,
            varINT,
            varDEC,
            varVAR,
            varBYT,
            varFIN
        };

        public static readonly List<TermPair> SysList = new List<TermPair>()
        {
            new TermPair
            {
                Name = "SYSTEM VIDEO MODE",
                Value = SysVideoMode,
                Equivalent = new List<string>()
                {
                    "mov ax, {0}",
                    "int 0x10"
                }
            }
        };
    }
    public static class DefsVideoMode
    {
        public static readonly string Color256r320x200 = "0x13";
    }
}

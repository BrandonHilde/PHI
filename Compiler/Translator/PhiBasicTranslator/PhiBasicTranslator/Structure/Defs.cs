using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public static class Defs
    {
        public static readonly string os16bit = "OS16BIT";
        public static readonly string replaceValueStart = ";{VALUES}";
        public static readonly string replaceCodeStart = ";{CODE}";
        public static readonly string replaceVarStart = ";{VARIABLE}";
        public static readonly string replaceIncludes = ";{Include}";

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
        public static readonly string MethodEndDeclare = "[end";
        public static readonly string ValueStringDelcare = "'";
        public static readonly string TypeRawDeclare = "~";
        public static readonly string ClassInherit = ":";
        public static readonly string VariableSet = ":";
        public static readonly string Comment = "#";
        public static readonly string CommentLine = "\r\n";
        public static readonly string IgnoreCharacter = "\\";

        public static readonly string NameClosureCharacters = " \t\r\n{:";
        public static readonly string VariableSetClosure = ";";
        public static readonly string VariableOpenDeclare = "VALUE_";
        public static readonly string InstructSetClosure = ";;";
        public static readonly string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_1234567890";
        public static readonly string Numbers = "1234567890.-";

        public static readonly string varSTR = "str";
        public static readonly string varINT = "int";
        public static readonly string varDEC = "dec"; // decimal
        public static readonly string varFIN = "fin"; // float int
        public static readonly string varVAR = "var";
        public static readonly string varBYT = "byt"; // byte
        public static readonly string varBLN = "bln"; // boolean

        public static readonly List<string> VariableTypes = new List<string>()
        {
            varSTR,
            varINT,
            varDEC,
            varVAR,
            varBYT,
            varFIN,
            varBLN
        };

        public static readonly string instWhile = "while";
        public static readonly string instOrder = "order";
        public static readonly string instIf = "if";
        public static readonly string instElse = "else";
        public static readonly string instIfElse = instIf + " " + instElse;
        public static readonly string instLog = "log";
        public static readonly string instIs = "is";      // ==
        public static readonly string instIsAlt = "==";   // ==
        public static readonly string instHas = "has";    // contians()
        public static readonly string instNot = "not";    // !=
        public static readonly string instNotAlt = "!=";  // !=
        public static readonly string instGreater = ">";    // >
        public static readonly string instLesser = "<";     // <
        public static readonly string instGreaterEqu = ">=";    // >=
        public static readonly string instLesserEqu = "<=";     // <=
        public static readonly string instAnd = "and";    // &&
        public static readonly string instOr = "or";      // ||
        public static readonly string instPlus = "+";
        public static readonly string instMinus = "-";
        public static readonly string instMult = "*";
        public static readonly string instDivide = "/";
        public static readonly string instMod = "%";

        public static readonly List<string> instructContainers = new List<string>()
        {
            instWhile,
            instIf,
            instElse,
            instIfElse,
        };

        public static readonly List<string> instructCommandList = new List<string>()
        {
            instOrder,
            instLog,
            instIs,
            instHas,
            instNot,
            instAnd,
            instOr
        };

        public static readonly List<string> instructModifyList = new List<string>()
        {
            instIsAlt,
            instNotAlt,
            instGreater,
            instLesser,
            instGreaterEqu,
            instLesserEqu,
            instPlus,
            instMinus,
            instMult,
            instDivide,
            instMod
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

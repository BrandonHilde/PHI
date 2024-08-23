using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public static class Defs
    {
        public static readonly string OS16BIT = "OS16BIT";
        public static readonly string OS16BitVideo = "OS16BITVideo";
        public static readonly string OS16BitSectorTwo = "OS16BITSectorTwo";

        public static readonly string replaceValueStart = ";{VALUES}";
        public static readonly string replaceCodeStart = ";{CODE}";
        public static readonly string replaceVarStart = ";{VARIABLE}";
        public static readonly string replaceConstStart = ";{CONSTANTS}";
        public static readonly string replaceIncludes = ";{INCLUDE}";

        public static readonly string replaceUnsetName = ";{UNSET}";

        public static readonly string True = "1";

        public static readonly string classStartPHI = "phi.";
        public static readonly string classStartx86ASM = "asm.";
        public static readonly string classStartArmASM = "arm.";
        public static readonly string paraOpen = "(";
        public static readonly string paraClose = ")";
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
        public static readonly string ConditionalClosureCharacters = "\r\n\t;:";
        public static readonly string ConditionalStartCharacters = "\r\n;:";
        public static readonly string TabSpaceClosureCharacters = "\r\n\t ";
        public static readonly string VariableSetClosure = ";";
        public static readonly string VariableOpenDeclare = "VALUE_";
        public static readonly string InstructSetClosure = ";;";
        public static readonly string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_1234567890";
        public static readonly string Numeric = "1234567890.-";
        public static readonly string RawNumberCut = ":;,=*+-!&|$/%^()[]{}";

        public static readonly string MathInc = "++";
        public static readonly string MathDec = "--";
        public static readonly string MathMultEq = "**";
        public static readonly string MathDivEq = "//";
        public static readonly string MathModEq = "%%";
        public static readonly string MathPowEq = "^^";
        public static readonly string MathPlus = "+";
        public static readonly string MathMinus = "-";
        public static readonly string MathMult = "*";
        public static readonly string MathDiv = "/";
        public static readonly string MathMod = "%";
        public static readonly string MathPow = "^";

        public static readonly string BoolAnd = "&";
        public static readonly string BoolOr = "?";
        public static readonly string BoolXor = "|";
        public static readonly string BoolNot = "!";

        public static readonly List<string> MathSmallOpsList = new List<string>()
        {
            MathPlus,
            MathMinus,
            MathMult,
            MathDiv,
            MathMod,
            MathPow,
            BoolAnd,
            BoolOr,
            BoolXor,
            BoolNot
        };

        public static readonly List<string> MathOpsList = new List<string>()
        {
            MathInc,
            MathDec,
            MathMultEq,
            MathDivEq,
            MathModEq,
            MathPowEq
        };

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

        public static readonly string instMath = "{Math}";
        public static readonly string instWhile = "while";
        public static readonly string instOrder = "order";
        public static readonly string instIf = "if";
        public static readonly string instElse = "else";
        public static readonly string instIfElse = "elif";
        public static readonly string instLog = "log";
        public static readonly string instAsk = "ask";
        public static readonly string instCall = "call";
        public static readonly string opperIs = "is";      // ==
        public static readonly string opperIsAlt = "==";   // ==
        public static readonly string opperHas = "has";    // contians()
        public static readonly string opperNot = "not";    // !=
        public static readonly string opperNotAlt = "!=";  // !=
        public static readonly string opperGreater = ">";    // >
        public static readonly string opperLesser = "<";     // <
        public static readonly string opperGreaterEqu = ">=";    // >=
        public static readonly string opperLesserEqu = "<=";     // <=
        public static readonly string opperAnd = "and";    // &&
        public static readonly string opperOr = "or";      // ||
        public static readonly string opperPlus = "+";
        public static readonly string opperMinus = "-";
        public static readonly string opperMult = "*";
        public static readonly string opperDivide = "/";
        public static readonly string opperMod = "%";

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
            instAsk,
            instCall
        };

        public static readonly List<string> OpperCompareList = new List<string>()
        {
            opperIs,
            opperIsAlt,
            opperHas,
            opperGreater,
            opperLesser,
            opperGreaterEqu,
            opperLesserEqu,
            opperNot,
            opperNotAlt
        };

        public static readonly List<string> OpperatorModifyList = new List<string>()
        {
            opperIsAlt,
            opperNotAlt,
            opperGreater,
            opperLesser,
            opperGreaterEqu,
            opperLesserEqu,
            opperPlus,
            opperMinus,
            opperMult,
            opperDivide,
            opperMod
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

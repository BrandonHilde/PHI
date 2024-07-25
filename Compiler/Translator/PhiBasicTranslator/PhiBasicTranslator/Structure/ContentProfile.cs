using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public enum Inside 
    { 
        None, 
        MultiComment, 
        Comment, 
        String, 
        AsmClassStart, 
        ArmClassStart,
        PhiClassStart, 
        CurlyOpen, 
        CurlyClose,
        SquareOpen,
        SquareClose,
        ParenthesisOpen,
        ParenthesisClose, 
        Colon, 
        SemiColon,
        ClassName, 
        ClassInherit, 
        VariableName,
        VariableTypeStr,
        VariableTypeInt,
        VariableTypeVar,
        VariableTypeByt,
        VariableTypeDec,
        VariableTypeBln,
        VariableTypeFin,
        VariableValue,
        VariableEnd,
        MethodOpen,
        MethodClose,
        MethodSet,
        MethodEnd,
        MethodReturn,
        MethodName,
        Instruct,
        InstructValue,
        InstructClose
    };
    public class ContentProfile
    {
        public Inside[] ContentInside = { Inside.None };
        public ContentProfile(int len) 
        { 
            ContentInside = new Inside[len];
        }
    }
}

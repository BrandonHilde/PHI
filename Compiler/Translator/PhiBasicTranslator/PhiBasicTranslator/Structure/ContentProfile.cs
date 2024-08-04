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
        VariableTypeMixed,
        VariableValue,
        VariableEnd,
        MethodOpen,
        MethodClose,
        MethodSet,
        MethodEnd,
        MethodReturn,
        MethodName,
        Instruct,
        InstructContainer,
        InstructClose,
        StandAloneInt
    };

    public enum RegionLabel
    {
        None,
        Declaration,
        InstructValue,
        InstructContent
    }
    public class ContentProfile
    {
        public Inside[] ContentInside = { Inside.None };
        public RegionLabel[] Labels = { RegionLabel.None };
        public ContentProfile(int len) 
        { 
            ContentInside = new Inside[len];
            Labels = new RegionLabel[len];
        }
    }
}

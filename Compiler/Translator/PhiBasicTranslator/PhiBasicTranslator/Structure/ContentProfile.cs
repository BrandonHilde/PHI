﻿using System;
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
        ClassClose,
        VariableName,
        VariableTypeStr,
        VariableTypeInt,
        VariableTypeVar,
        VariableTypeByt,
        VariableTypeDec,
        VariableTypeBln,
        VariableTypeFin,
        VariableTypeMixed,
        VariableTypeInsert,
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
        StandAloneInt,
        Conditonal,
        Opperation
    };

    public enum RegionLabel
    {
        None,
        Declaration,
        InstructValue,
        InstructContent,
        Conditional,
        ConditionalBoolean,
        ConditionalValueSet,
        ConditionalOperation,
        ConditionalAssessMath,
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.Structure
{
    public class ConditionalPairs
    {
        public enum ConditionOpperation
        {
            AssignValue,
            Comparision
        };
        public enum ConditionType 
        { 
            None,
            JumpIfGreater, 
            JumpIfLess, 
            JumpIfEqual, 
            JumpIfNotEqual,
            JumpIfGreaterEqual,
            JumpIfLessEqual,
            JumpIfCarry,
            JumpIfNoCarry,
            JumpIfOverflow,
            JumpIfNoOverflow
        }
        public string LeftValue { get; set; }
        public string RightValue { get;set; }
        public ConditionType type = ConditionType.None;
        public ConditionOpperation opperation = ConditionOpperation.Comparision;

        public ConditionalPairs Copy()
        {
            return new ConditionalPairs 
            { 
                LeftValue = LeftValue, 
                RightValue = RightValue,
                type = type,
                opperation = opperation
            };
        }
    }
}

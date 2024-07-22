using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PhiBasicTranslator.ParseEngine
{
    public class ParseVariables
    {
        public static ContentProfile ProfileVAR(string content, int index, string varType, ContentProfile previous)
        {
            previous = ProfileVarType(content, varType, index, previous);

            Inside inside = Inside.VariableName;

            for (int i = index + varType.Length; i < content.Length; i++)
            {
                if (content[i] != ' ' && previous.ContentInside[i] == Inside.None)
                {
                    previous.ContentInside[i] = inside;

                    if (content[i].ToString() == Defs.VariableSet)
                    {
                        inside = Inside.Colon;
                        previous.ContentInside[i] = inside;

                        inside = Inside.VariableValue;
                    }

                    if (content[i].ToString() == Defs.VariableSetClosure) // ;
                    {
                        previous.ContentInside[i] = Inside.SemiColon;
                        break;
                    }
                }
            }

            return previous;
        }

        public static ContentProfile ProfileINT(string content, int index, ContentProfile previous)
        {
            previous = ProfileVarType(content, Defs.varINT, index, previous);

            Inside inside = Inside.VariableName;

            for (int i = index + Defs.varINT.Length; i < content.Length; i++)
            {
                if (content[i] != ' ' && previous.ContentInside[i] == Inside.None)
                {
                    previous.ContentInside[i] = inside;

                    if (content[i].ToString() == Defs.VariableSet)
                    {
                        inside = Inside.Colon;
                        previous.ContentInside[i] = inside;

                        inside = Inside.VariableValue;
                    }

                    if (content[i].ToString() == Defs.VariableSetClosure) // ;
                    {
                        previous.ContentInside[i] = Inside.SemiColon;
                        break;
                    }
                }
            }

            return previous;
        }

        public static ContentProfile ProfileVarType(string content, string vType, int index, ContentProfile previous)
        {
            string cut = content.Substring(index);

            if (cut.StartsWith(vType + " "))
            {
                for (int i = index; i < index + vType.Length && i < content.Length; i++)
                {
                    previous.ContentInside[i] = Inside.VariableType;
                }
            }

            return previous;
        }

    }
}

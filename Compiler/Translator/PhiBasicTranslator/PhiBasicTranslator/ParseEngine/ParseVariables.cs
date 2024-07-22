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
        public static ContentProfile ProfileVAR(string content, int index, Inside varType, ContentProfile previous)
        {
            previous = ProfileVarType(content, varType, index, previous);

            Inside inside = Inside.VariableName;

            int vlen = 3;

            for (int i = index + vlen; i < content.Length; i++)
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

        //public static ContentProfile ProfileINT(string content, int index, ContentProfile previous)
        //{
        //    previous = ProfileVarType(content, Inside.VariableTypeInt, index, previous);

        //    Inside inside = Inside.VariableName;

        //    for (int i = index + Defs.varINT.Length; i < content.Length; i++)
        //    {
        //        if (content[i] != ' ' && previous.ContentInside[i] == Inside.None)
        //        {
        //            previous.ContentInside[i] = inside;

        //            if (content[i].ToString() == Defs.VariableSet)
        //            {
        //                inside = Inside.Colon;
        //                previous.ContentInside[i] = inside;

        //                inside = Inside.VariableValue;
        //            }

        //            if (content[i].ToString() == Defs.VariableSetClosure) // ;
        //            {
        //                previous.ContentInside[i] = Inside.SemiColon;
        //                break;
        //            }
        //        }
        //    }

        //    return previous;
        //}

        public static ContentProfile ProfileVarType(string content, Inside vType, int index, ContentProfile previous)
        {
            string cut = content.Substring(index);

            string vstr = GetVarEquivilent(vType);

            if (vstr != string.Empty)
            {

                if (cut.StartsWith(vstr + " "))
                {
                    for (int i = index; i < index + vstr.Length && i < content.Length; i++)
                    {
                        previous.ContentInside[i] = vType;
                    }
                }
            }

            return previous;
        }

        public static string GetVarEquivilent(Inside vType)
        {
            string str = string.Empty;

            if (vType == Inside.VariableTypeBln) return Defs.varBLN;
            if (vType == Inside.VariableTypeByt) return Defs.varBYT;
            if (vType == Inside.VariableTypeDec) return Defs.varDEC;
            if (vType == Inside.VariableTypeStr) return Defs.varSTR;
            if (vType == Inside.VariableTypeInt) return Defs.varINT;
            if (vType == Inside.VariableTypeFin) return Defs.varFIN;
            if (vType == Inside.VariableTypeVar) return Defs.varVAR;


            return str;
        }

    }
}

using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PhiBasicTranslator.TranslateUtilities;

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
                        previous.ContentInside[i] = Inside.VariableEnd;
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
                if (ParseMisc.StartsWithAppendedValue(cut, vstr, Defs.TabSpaceClosureCharacters.ToList()))
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

        public static List<PhiVariable> GetInstructSubVariables(PhiInstruct instruct, List<PhiVariable> predefinedVars)
        {
            List<PhiVariable> varbles = new List<PhiVariable>();

            string content = instruct.Value;

            bool singleline = false;

            if(Defs.instructCommandList.Contains(instruct.Name)) 
            {
                singleline = true;
            }

            List<string> vals = new List<string>();
            string vl = string.Empty;

            for (int i = 0; i < content.Length; i++)
            {
                Inside inside = instruct.ContentLabels[i];

                if (inside != Inside.Comment
                    && inside != Inside.MultiComment
                    && inside != Inside.Instruct)
                {
                    if (content[i] != ' ' || inside == Inside.String)
                    {
                        vl += content[i];
                    }
                    else
                    {
                        if (vl != string.Empty)
                        { 
                            vals.Add(vl);
                            vl = string.Empty;
                        }
                    }

                    if (content[i].ToString() == Defs.VariableSetClosure)
                    {
                        if (vl != string.Empty)
                        {
                            vl = vl.Replace(Defs.VariableSetClosure, "");

                            vals.Add(vl);

                            vl = string.Empty;

                            //comes after the clear because it will double add otherwise
                            if (singleline) break;
                        }
                    }
                }
            }

            if (vl != string.Empty)
            {
                vals.Add(vl);
                vl = string.Empty;
            }

            foreach (string v in vals)
            {
                PhiVariable? vbl = predefinedVars.Where(x => x.Name == v).FirstOrDefault();

                if(vbl != null)
                {
                    varbles.Add(vbl);
                }
                else if(v.StartsWith(Defs.ValueStringDelcare))
                {
                    varbles.Add(new PhiVariable
                    {
                        Name = (TranslateToX86.VarCount++).ToString(),
                        ValueRaw = v,
                        varType = Inside.VariableTypeStr,
                        preExisting = false
                    });
                }
                else if(v.Contains('.')) // defs.decimaldeclare?
                {
                    varbles.Add(new PhiVariable
                    {
                        Name = (TranslateToX86.VarCount++).ToString(),
                        ValueRaw = v,
                        varType = Inside.VariableTypeDec,
                        preExisting = false
                    });
                }
                else
                {
                    varbles.Add(new PhiVariable
                    {
                        Name = (TranslateToX86.VarCount++).ToString(),
                        ValueRaw = v,
                        varType = Inside.VariableTypeInt,
                        preExisting = false
                    });
                }
            }

            return varbles;
        }
    }
}

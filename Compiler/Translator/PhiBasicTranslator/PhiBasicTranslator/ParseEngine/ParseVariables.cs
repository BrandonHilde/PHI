﻿using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PhiBasicTranslator.TranslateUtilities;
using Newtonsoft.Json.Linq;

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

        public static string GetRawValueArrayLength(string rawValue)
        {
            bool start = false;

            string val = string.Empty;

            ContentProfile prf = ParseUtilities.ProfilePrepare(rawValue);

            for (int i = 0; i < rawValue.Length; i++)
            {
                Inside inside = prf.ContentInside[i];

                if (inside != Inside.String && 
                    inside != Inside.Comment && 
                    inside != Inside.MultiComment)
                {
                    if (rawValue[i].ToString() == Defs.squareOpen)
                    {
                        start = true;
                    }
                    else if (rawValue[i].ToString() == Defs.squareClose && start)
                    {
                        start = false;
                    }
                    else
                    {
                        if (start) val += rawValue[i];
                    }
                }
            }

            if (!start)
            {
                return val;
            }

            return string.Empty;
        }

        public static string ReplaceASMVar(string content, List<PhiVariable> preexisting)
        {
            bool startname = false;

            string cont = string.Empty;
            string name = string.Empty;

            
            ContentProfile profile = ParseUtilities.ProfilePrepare(content);

            for (int i = 0; i < content.Length; i++)
            {
                if (profile.ContentInside[i] == Inside.None)
                {
                    if (content[i].ToString() == Defs.curlyOpen)
                    {
                        startname = true;
                    }

                    if (content[i].ToString() == Defs.curlyClose)
                    {
                        if (startname)
                        {
                            if (name.Length > 0)
                            {
                                name = name.Replace("{", "");
                                name = name.Replace("}", "");

                                cont += ASMx86_16BIT.UpdateName(name);

                                continue;
                            }
                            startname = false;
                        }
                    }
                }

                if(!startname)
                {
                    cont += content[i];
                }
                else
                {
                    name += content[i];
                }
            }
            return cont;
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
                    if (!Defs.TabSpaceClosureCharacters.Contains(content[i]) || inside == Inside.String)
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
                string vname = v.Trim();
                string index = string.Empty;

                bool array = false;

                if(vname.Contains(Defs.squareOpen) && vname.Contains(Defs.squareClose))
                {
                    List<string> vls = ParseMisc.ExtractArrayParts(vname);

                    vname = vls.First();
                    index = vls.Last();

                    array = true;
                }

                PhiVariable? vbl = predefinedVars.Where(x => x.Name == vname).FirstOrDefault();

                List<string> sbvals = ParseMisc.ExtractSubValues(v);

                if(vbl != null)
                {
                    if (!array)
                    {
                        varbles.Add(vbl);
                    }
                    else
                    {
                        varbles.Add(new PhiVariable
                        {
                            Name = vbl.Name,
                            ValueRaw = v,
                            varType = vbl.varType,
                            preExisting = true,
                            Values = sbvals
                        });
                    }
                }
                else if(v.StartsWith(Defs.ValueStringDelcare))
                {
                    varbles.Add(new PhiVariable
                    {
                        Name = (TranslateToX86.VarCount++).ToString(),
                        ValueRaw = v,
                        varType = Inside.VariableTypeStr,
                        preExisting = false,
                        Values = sbvals
                    });
                }
                else if(v.Contains('.')) // defs.decimaldeclare?
                {
                    varbles.Add(new PhiVariable
                    {
                        Name = (TranslateToX86.VarCount++).ToString(),
                        ValueRaw = v,
                        varType = Inside.VariableTypeDec,
                        preExisting = false,
                        Values = sbvals
                    });
                }
                else
                {
                    varbles.Add(new PhiVariable
                    {
                        Name = (TranslateToX86.VarCount++).ToString(),
                        ValueRaw = v,
                        varType = Inside.VariableTypeInt,
                        preExisting = false,
                        Values = sbvals
                    });
                }
            }

            return varbles;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Newtonsoft;
using PhiBasicTranslator.GeneralUtilities;
using PhiBasicTranslator.ParseEngine;
using PhiBasicTranslator.TranslateUtilities;

namespace PhiBasicTranslator.Structure
{
    public enum PhiType { ASM, ARM, PHI }
    public enum PhiInclude { Graphics, Text, Timer, Keyboard, Mouse, Sectors };
    public class PhiClass
    {
        public PhiType Type { get; set; } = PhiType.PHI;
        public List<PhiInclude> Includes { get; set; } = new List<PhiInclude>();
        public string Name { get; set; } = string.Empty;
        public string Inherit { get; set; } = string.Empty;
        public string RawContent { get; set; } = string.Empty;

        public List<string> translatedASM = new List<string>();
        public List<PhiMethod> Methods { get; set; } = new List<PhiMethod>();
        public List<PhiVariable> Variables { get; set; } = new List<PhiVariable>();
        public List<PhiInstruct> Instructs { get; set; } = new List<PhiInstruct>();

        public PhiClass() { }

        public void AddInclude(PhiInstruct instruct)
        {
            if (instruct.Name == Defs.instLog) Includes.Add(PhiInclude.Text);
            if (instruct.Name == Defs.instAsk) Includes.Add(PhiInclude.Keyboard);

            if (instruct.Name == Defs.instCall)
            {
                if (ParseMisc.ContainsAny(instruct.Value, ASMx86_16BIT.incDrawingList))
                {
                    Includes.Add(PhiInclude.Graphics);
                }

                if (ParseMisc.ContainsAny(instruct.Value, ASMx86_16BIT.incTimerList))
                {
                    Includes.Add(PhiInclude.Timer);
                }

                if (ParseMisc.ContainsAny(instruct.Value, ASMx86_16BIT.incSectorsList))
                {
                    Includes.Add(PhiInclude.Sectors);
                }

                if (ParseMisc.ContainsAny(instruct.Value, ASMx86_16BIT.incKeyboardList))
                {
                    Includes.Add(PhiInclude.Keyboard);
                }
            }
        }

        public void CheckForOverrides()
        {

        }

        public PhiClass Copy()
        {
            List<PhiVariable> vars = new List<PhiVariable>();

            foreach (PhiVariable v in Variables) vars.Add(v.Copy());

            List<PhiMethod> mthds = new List<PhiMethod>();

            foreach (PhiMethod mth in Methods) mthds.Add(mth.Copy());

            List<PhiInstruct> insts = new List<PhiInstruct>();

            foreach (PhiInstruct nst in Instructs) insts.Add(nst.Copy());

            List<string> asm = new List<string>();
            asm.AddRange(translatedASM);

            List<PhiInclude> inc = new List<PhiInclude>();
            inc.AddRange(Includes);

            return new PhiClass
            {
                Type = Type,
                Name = Name,
                Inherit = Inherit,
                RawContent = RawContent,
                Methods = mthds,
                Variables = vars,
                Instructs = insts,
                translatedASM = asm,
                Includes = inc,
            };
        }
    }



   
}

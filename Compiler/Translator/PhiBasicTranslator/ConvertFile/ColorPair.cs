using PhiBasicTranslator.Structure;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleIDE
{
    public class ColorPair
    {
        public ConsoleColor color = ConsoleColor.White;
        public Inside label = Inside.None;
        public ColorPair(Inside Label, ConsoleColor Color) 
        { 
            label = Label;
            color = Color;
        }
    }
}

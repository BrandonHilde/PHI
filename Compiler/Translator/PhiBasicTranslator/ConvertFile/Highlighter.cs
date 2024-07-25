using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;
using ConsoleIDE;
using PhiBasicTranslator.ParseEngine;
using PhiBasicTranslator.Structure;

namespace ConvertFile
{
    public class Highlighter
    {
        public List<ColorPair> Highlighting = new List<ColorPair>();
        public Highlighter()
        {
            SetUpDefaults();
        }

        public void Add(ColorPair colorPair)
        {
            Highlighting.Add(colorPair);
        }

        public void ColorCode(string content)
        {
            ContentProfile prf = ParseUtilities.ProfileContent(content);

            for (int i = 0; i < content.Length; i++)
            {
                ColorPair? clr = Highlighting.Where(
                    x=> x.label == prf.ContentInside[i]
                    ).FirstOrDefault();

                if (clr != null)
                {
                    Console.ForegroundColor = clr.color;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.White;
                }

                Console.Write(content[i]);
            }

            Console.ForegroundColor = ConsoleColor.White;
        }

        public void SetUpDefaults()
        {
            Highlighting.Add(new ColorPair(Inside.ClassName, ConsoleColor.Blue));
            Highlighting.Add(new ColorPair(Inside.MethodName, ConsoleColor.Blue));
            Highlighting.Add(new ColorPair(Inside.Instruct, ConsoleColor.Blue));
            Highlighting.Add(new ColorPair(Inside.CurlyClose, ConsoleColor.Blue));
            Highlighting.Add(new ColorPair(Inside.CurlyOpen, ConsoleColor.Blue));
            Highlighting.Add(new ColorPair(Inside.ClassInherit, ConsoleColor.Blue));
            Highlighting.Add(new ColorPair(Inside.PhiClassStart, ConsoleColor.Blue));

            Highlighting.Add(new ColorPair(Inside.Comment, ConsoleColor.Green));
            Highlighting.Add(new ColorPair(Inside.MultiComment, ConsoleColor.Green));

            Highlighting.Add(new ColorPair(Inside.String, ConsoleColor.Magenta));
        }
    }
}

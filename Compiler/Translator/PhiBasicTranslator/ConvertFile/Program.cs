// See https://aka.ms/new-console-template for more information
using ConvertFile;
using PhiBasicTranslator;
using PhiBasicTranslator.Structure;

Console.WriteLine("Hello, World!");

string file = string.Empty;


while (file == string.Empty)
{
    Console.Write("File: ");
    file = Console.ReadLine();  
}

if (file == ".") file = "../../../hello.phi";

Translator translator = new Translator(file);

Highlighter highlighter = new Highlighter(); 

highlighter.ColorCode(translator.FileContent);

List<string> lines = new List<string>();

lines = translator.TranslateFile(file);

Console.WriteLine();

PhiMath math = new PhiMath();

MathPair pr = PhiMath.Parse(";i++;");


Console.WriteLine("\r\n;ASMx86 CODE\r\n");

foreach (string line in lines) Console.WriteLine(line);

File.WriteAllLines("phi.ASM", lines);

//string code = "";

//string asm = translator.TranslateFile("../../../code.phi");

//Console.WriteLine(asm);
// See https://aka.ms/new-console-template for more information
using PhiBasicTranslator;

Console.WriteLine("Hello, World!");

string file = string.Empty;


while (file == string.Empty)
{
    Console.Write("File: ");
    file = Console.ReadLine();  
}

if (file == ".") file = "../../../code.phi";

Translator translator = new Translator(file);

//string code = "";

//string asm = translator.TranslateFile("../../../code.phi");

//Console.WriteLine(asm);
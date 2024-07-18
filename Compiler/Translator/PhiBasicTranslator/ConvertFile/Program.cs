// See https://aka.ms/new-console-template for more information
using PhiBasicTranslator;

Console.WriteLine("Hello, World!");


Translator translator = new Translator("../../../code.phi");

//string code = "";

string asm = translator.TranslateFile("../../../code.phi");

Console.WriteLine(asm);
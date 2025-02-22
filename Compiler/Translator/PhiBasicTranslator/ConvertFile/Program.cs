// See https://aka.ms/new-console-template for more information
using ConvertFile;
using PhiBasicTranslator;
using PhiBasicTranslator.ParseEngine;
using PhiBasicTranslator.Structure;

Console.WriteLine("Hello, World!");

string file = string.Empty;


while (file == string.Empty)
{
    Console.Write("File: ");
    file = Console.ReadLine();  
}

if (file == ".") file = "../../../hello.phi";

/*
 * 
 *              (6 + {a}) 
 *      {a}:    ({b} / {c})
 *      {b}:    (i * 7)
 *      {c}:    (w * 2)
 *              ({a} - 5)   
 *              
 *                 ;{CODE} (6 + (i * 7 / (w * 2)) - 5)

   mov ax, [VALUE_w]
   mov cx, 2
   mul cx      ; ax is 16

   push ax

   mov ax, [VALUE_i]
   mov cx, 7
   mul cx      ; ax is 16

   pop bx

   div bx 

   ;mov si, VALUE_newline
   ;call print_log                 ;{CODE} (6 + (i * 7 / (w * 2)) - 5)

   ;mov ax, dx       ; remainder (mod)
   ;call print_int


   add ax, 6
   sub ax, 5

   call print_int
 * 
 */

//string test = "int y: (6 + ((x * 7) / (y * 2)) - 5)";
//List<int> stuff = ParseUtilities.GetEnclosingDepth(test, Defs.paraOpen, Defs.paraClose);

//for (int i = 0; i < stuff.Count; i++)
//{
//    Console.Write(stuff[i]);
//}
//Console.WriteLine();
//Console.WriteLine(test);

Translator translator = new Translator(file);

Highlighter highlighter = new Highlighter(); 

highlighter.ColorCode(translator.FileContent);

List<string> lines = new List<string>();

PhiCodebase codebase = translator.TranslateFile(file);

foreach (PhiClass codeclass in codebase.ClassList)
{
    lines = codeclass.translatedASM;

    Console.WriteLine("\r\n;ASMx86 CODE\r\n");

    foreach (string line in lines) Console.WriteLine(line);

    File.WriteAllLines("phi.ASM", lines);
}

//string code = "";

//string asm = translator.TranslateFile("../../../code.phi");

//Console.WriteLine(asm);
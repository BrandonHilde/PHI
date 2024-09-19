<img src="/resources/phialt.png" width="155">
<h1>PHI Language</h1>
<h3>Soon operating systems will be as easy to code as desktop apps</h3>

<h3>Example Program:</h3>

```phi

phi.Hello:Bootloader
{
	str hello: 'Hello, World!\r\n';
    	str newline: '\r\n';
	str name:[40];
	
	log 'What is your name: ';
	ask name;
	log newline;
	log 'hello: ' name;
	log newline;
	log 'Press any key to continue...';
	
	call Bootloader.WaitForKeyPress;
	call Bootloader.JumpToSectorTwo;
}

phi.SectorTwo:OS
{
	str Greetings: 'Welcome to PHI language!';
	
	log '\r\n' Greetings;
}

```
<h4>To Build:</h4>

    1. Install NASM (Netwide Assembler) (can also use FASM Flat Assembler)
    2. Install QEMU (qemu-system-x86_64)
    3. Run the ConvertFile program
    4. Specify the file (hello.phi)
    5. After it runs it will produce a phi.ASM file
    6. cd into the folder
    7. & [insert path]\buildSingleASM.bat phi

[More Build Info](./Compiler/x86ASM/buildSingleASM.bat)

<h4>Goals:</h4>
    
    1. Direct access to ASM
    2. Optional memory safety 
    3. Syntax Efficiency 
    4. Compile to ASM and then to Binary
    5. Compatibility with C

<h4>Timeline:</h4>
<li>Write Assembly equivilents for PHI functionality</li>
<li>Write a PHI to Assembly converter in C#</li>
<li>Write a Assembly Intel x86(and AT&T eventually) to Arm converter in C#</li>
<li>Expand PHI to be a full language</li>
<li>Rewrite the converters in PHI so the language is self dependent</li>
<li>Basic set of drivers as built-in functions</li>

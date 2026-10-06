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

    1. Install the .NET 8 SDK, NASM and QEMU, and put nasm and qemu-system-i386 on PATH
       (or set PHI_NASM / PHI_QEMU to their full paths)
    2. dotnet build PHI.sln
    3. Use the phi command (Compiler/Phi.Cli/bin/Debug/net8.0/phi.exe):

       phi build tests/hello.phi         compile to tests/build/hello/hello.img (a bootable disk image)
       phi run tests/hello.phi           build and boot it in QEMU; log output also prints in the terminal
       phi run tests/hello.phi --debug   start paused, waiting for gdb on localhost:1234
       phi test                          boot every test in tests/ headless and check its output

The generated assembly for each class is saved next to the image (for example `tests/build/hello/0_Hello.asm`).
See [tests/README.md](./tests/README.md) for how the tests work, and [Plan.md](./Plan.md) for the roadmap.

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

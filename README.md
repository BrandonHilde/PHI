<img src="/resources/phialt.png" width="155">
<h1>PHI Language</h1>
<h3>Soon operating systems will be as easy to code as desktop apps</h3>

<h3>PHI OS</h3>

`phi run samples/os.phi` boots a small operating system written in PHI: a 32-bit kernel
with interrupt handling, drivers, paging, a heap, a FAT16 file system and processes, and a
shell with commands and a game, all running as user programs. Only the lowest layer (the
interrupt stubs, task switching and the console) is assembly; the memory manager, file
system, process loader, drivers like the disk and clock, and the whole userland are PHI.

![The PHI OS shell](docs/images/phi-os-shell.png)

```
phi> ls
  CAT.BIN  17461
  ...
phi> echo hello
hello
phi> count &
[started program 2]
phi> ps
  PID  STATE         NAME
  1    ready         SHELL.BIN
  2    sleeping      COUNT.BIN
  3    running       PS.BIN
phi> pong
```

<h3>Example Programs:</h3>

A 32-bit kernel (`phi run hello.phi`):

```phi
phi.Hello:Kernel
{
	str name: [40];

	log 'What is your name: ';
	ask name;
	log '\nhello ' name '\n';
}
```

A user program, which a kernel runs from its disk (put `count.phi` in the kernel's
`rootfs` folder and `phi build` compiles it into `COUNT.BIN`):

```phi
phi.Count:Program
{
	while int i: 1; i <= 5; i++;
		log 'count ' i '\n';
		call OS.Sleep: 300;
	;;
}
```

<h3>Learn PHI</h3>

- **[Getting started](docs/getting-started.md):** install the tools and boot PHI OS in ten minutes
- **[The tutorial](docs/README.md#learn-phi):** eight parts, from a boot sector to adding a
  command to PHI OS, each with a tested example
- **[Language reference](docs/language.md)**, **[memory map](docs/memory-map.md)** and
  **[architecture](docs/architecture.md)**
- **[Contributing](CONTRIBUTING.md)**

<h4>To Build:</h4>

    1. Install the .NET 8 SDK, NASM and QEMU, and put nasm and qemu-system-i386 on PATH
       (or set PHI_NASM / PHI_QEMU to their full paths)
    2. dotnet build PHI.sln
    3. Use the phi command (Compiler/Phi.Cli/bin/Debug/net8.0/phi.exe):

       phi build samples/arcade.phi      compile to samples/build/arcade/arcade.img (a bootable disk image)
       phi run samples/arcade.phi        build and boot it in QEMU (Pong: w/s and o/l move the paddles)
       phi run samples/kernel.phi        a 32-bit protected-mode kernel
       phi run samples/os.phi            PHI OS: boots into a shell (help lists the commands)
       phi run samples/terminal.phi      a simpler kernel with built-in commands (ls, cat, run hello.bin ...)
       phi run tests/hello.phi --debug   start paused, waiting for gdb on localhost:1234
       phi check file.phi                report errors without building
       phi test                          boot every test in tests/ headless and check its output

The generated assembly is saved next to the image (`boot.asm`, `os.asm` or `kernel.asm`), with each
PHI source line shown above the code it produced.

- [docs/](./docs/README.md): all the documentation
- [samples/](./samples): example programs
- [tests/README.md](./tests/README.md): how the tests work
- [Plan.md](./Plan.md): the roadmap

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

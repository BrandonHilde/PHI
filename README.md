<img src="/resources/phialt.png" width="155">
<h1>PHI Language</h1>
<h3>Soon operating systems will be as easy to code as desktop apps</h3>

<h3>PHI OS</h3>

`phi run samples/os.phi` boots a small operating system written in PHI: a 32-bit kernel
with interrupt handling, drivers, paging, a heap, a FAT16 file system and processes, and a
shell with commands and a game, all running as user programs. Only the lowest layer (the
interrupt stubs, task switching and the console) is assembly; the memory manager, file
system, process loader, drivers like the disk and clock, and the whole userland are PHI.

<h3>The network</h3>

`phi run samples/web.phi` boots a kernel that fetches web pages, `https://` ones too. The
network card driver (RTL8139), ARP, IP, UDP, DHCP, DNS, TCP, HTTP, and TLS 1.3 with its
cryptography (X25519, AES-GCM, ChaCha20-Poly1305, SHA-256) are all written in PHI:

```phi
use net.http;

phi.Fetch:Kernel
{
	ptr<u8> page: new u8[65536];
	bln ok: false;
	call ok is Net.Start;

	int size: 0;
	call size is Http.Get: 'https://example.com/' page 65536;
	log page;     # HTTP/1.1 200 OK ... <!doctype html><html> ...
}
```

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

A 32-bit kernel (save it as `hello.phi` anywhere, then `phi run hello.phi`):

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
       phi run samples/web.phi           fetch web pages: type a URL, see the raw HTML
       phi run tests/hello.phi --debug   start paused, waiting for gdb on localhost:1234
       phi check file.phi                report errors without building
       phi test                          boot every test in tests/ headless and check its output
       phi serve folder [--https]        serve a folder to kernels in QEMU (at 10.0.2.2)

The generated assembly is saved next to the image (`boot.asm`, `os.asm` or `kernel.asm`), with each
PHI source line shown above the code it produced.

- [docs/](./docs/README.md): all the documentation
- [samples/](./samples): example programs
- [tests/README.md](./tests/README.md): how the tests work
- [Plan.md](./Plan.md): the roadmap

<h4>Goals:</h4>

1. Direct access to ASM
2. Optional memory safety
3. Syntax efficiency
4. Compile to ASM and then to binary
5. Compatibility with C (planned)

<h4>What's Next:</h4>

All eight phases of the [roadmap](./Plan.md) are done: PHI compiles boot sectors, 32-bit
kernels and user programs, and PHI OS ties them together. Next, roughly in order:

1. Variables on the stack for methods (recursion, kernel threads)
2. A heap for user programs, so `new` works there too
3. Growable strings
4. Framebuffer graphics in 32-bit kernels
5. Later: checking TLS certificates, 64-bit, an ARM backend, C compatibility, and
   rewriting the compiler in PHI (see [Plan.md](./Plan.md#later))

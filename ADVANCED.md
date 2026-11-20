# PHI: the advanced guide

The [README](README.md) says what PHI is and how to start. This page is for what comes
after: building PHI from its source, everything the `phi` command does, debugging and
measuring, how it works inside, and where it's going.

- [Build PHI from source](#build-phi-from-source)
- [The phi command](#the-phi-command)
- [Targets](#targets)
- [Debugging](#debugging)
- [Speed, and measuring it](#speed-and-measuring-it)
- [Calling C libraries](#calling-c-libraries)
- [Compiled libraries](#compiled-libraries)
- [How it works inside](#how-it-works-inside)
- [Tests](#tests)
- [Settings](#settings)
- [Goals and roadmap](#goals-and-roadmap)

## Build PHI from source

A release needs none of this: its `phi` is one file with everything inside. To build the
compiler yourself:

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), NASM and
   QEMU, and put `nasm` and `qemu-system-i386` on `PATH` (or set `PHI_NASM` and `PHI_QEMU`
   to their full paths). On a Mac, Apple's Command Line Tools as well, for native macOS
   programs.
2. `dotnet build PHI.sln`
3. The command is `Compiler/Phi.Cli/bin/Debug/net10.0/phi` (`phi.exe` on Windows).
4. For the vendor folder's C libraries, which aren't in the repository:
   `python3 vendor/fetch.py` once. It downloads each library's own builds, and compiles the
   others where there's a compiler for that system (clang on a Mac, MinGW for Windows, gcc
   on Linux). [vendor/MAINTAINING.md](vendor/MAINTAINING.md) has the details.

`python3 release/pack.py 1.0.0` packs a release: one zip for each system.

## The phi command

```
phi build samples/arcade.phi      compile to samples/build/arcade/arcade.img (a bootable disk image)
phi run samples/arcade.phi        build and boot it in QEMU (Pong: w/s and o/l move the paddles)
phi run samples/kernel.phi        a 32-bit protected-mode kernel
phi run samples/os.phi            PHI OS: boots into a shell (help lists the commands)
phi run samples/terminal.phi      a simpler kernel with built-in commands (ls, cat, run hello.bin ...)
phi run samples/web.phi           fetch web pages: type a URL, see the raw HTML
phi run samples/music.phi         a jukebox: songs and a WAV file, on the sound card
phi run samples/desktop.phi       a desktop: apps as programs, each in a window
phi run samples/mmo/server.phi    a game server and three clients on one network, each in a
                                  window: move with the arrow keys, see everyone move; the
                                  world and the lobby are programs the kernel restarts if they
                                  crash (press K in the server's window to see it)

phi build x.phi --target windows64        an ordinary program: x.exe (or linux64, linux, windows, macos)
phi run x.phi --target macos              build it and run it here
phi build x.phi --target macos --app      on a Mac: a native app, x.app, that the Finder opens
phi build x.phi --library                 a compiled library: x.philib, which another program
                                          includes with use x; without any of its source
phi vendor                                the C libraries that come with PHI

phi check file.phi                report errors without building
phi fmt file.phi                  lay out a file, or a folder of them
phi lsp                           the language server, for an editor (PHI-VSCODE is the extension for VS Code)

phi run tests/hello.phi --debug   start paused, waiting for gdb on localhost:1234
phi build x.phi --target windows64 --debug   with a .pdb: a debugger steps through its lines
phi profile samples/web.phi       boot it, then list the PHI lines that took the most time
phi profile x.phi --target linux64   the same for a Linux program, run here to its end

phi test                          boot every test in tests/ headless and check its output
phi test --bench                  run the benchmarks and compare their counts with the saved ones
phi compare                       time the programs in bench/ beside the same ones in C and C#
phi fuzz code --count 1000        write random programs, each with what it must print, and check
                                  the compiler's code for them on the systems this computer runs
phi fuzz numbers --count 1000     the same, with programs of 64-bit and floating point numbers
phi fuzz objects --count 1000     the same, with programs of objects, text and arrays that grow:
                                  copied, handed over and freed, on a heap that checks every use

phi load samples/mmo/server.phi --connections 500 --seconds 60 --root samples/mmo/certs/root.der --host lobby.mmo.phi.test
                                  boot the game server with load-client kernels that log 500
                                  TLS connections in and keep them talking, checking every answer
phi load samples/mmo/server.phi --client samples/mmo/load_players.phi --connections 300 --seconds 60
                                  300 players in the game world at once, checking that it keeps
                                  its 20 ticks a second
phi serve folder [--https]        serve a folder to kernels in QEMU (at 10.0.2.2)
phi --version                     which PHI this is
```

`phi` with nothing after it lists every command with its options.

## Targets

A program says what it is by its class (`phi.Name:Kernel`, `:Program`, `:Bootloader`), and
`--target` says which system a program is for.

| `--target` | What's built |
|---|---|
| none | a bootable disk image for a kernel or a 16-bit program; for a `:Program`, one that runs on PHI OS |
| `windows64`, `windows` | a 64-bit or 32-bit `.exe` |
| `linux64`, `linux` | a 64-bit or 32-bit Linux program |
| `macos` | a native program for Apple Silicon, built on a Mac (`--app` makes a `.app`) |

Windows and Linux programs can be built from any system: `phi build x.phi --target windows64`
works on a Mac or on Linux. A macOS program is assembled and linked by Apple's tools, so it's
built on a Mac. A program that calls a C library is built with a target, since PHI OS has
none to call.

A program's build goes in a `build` folder beside its source: the program, the libraries it
needs beside it, and `obj` with the generated assembly, where each PHI line is shown above
the code it produced.

## Debugging

- **A kernel:** `phi run x.phi --debug` starts QEMU paused, waiting for gdb on
  `localhost:1234`.
- **A Windows program:** `--debug` writes a `.pdb`, and Visual Studio, WinDbg or cdb step
  through PHI's lines.
- **A macOS program:** `--debug` writes a `.dSYM`, and lldb stops at PHI's lines and shows
  the methods called.
- **A program that stops:** an index out of range, a null pointer, division by zero and a
  stack that's run out each say which line. A kernel shows a panic screen with the same.
- **A wrong result that might be the optimiser's:** `PHI_IR=0` builds without it, and
  `PHI_OPT_OFF=all` (or `same`, `hoist`, `share` one at a time) leaves steps of it out.

[Getting started](docs/getting-started.md#when-something-goes-wrong) has the walk-through,
and the [codebase map](docs/codebase-map.html) says where to look for each kind of error.

## Speed, and measuring it

Programs are built through PHI's own optimiser: values kept in registers, short methods put
in place, nothing worked out twice, and simple loops over arrays made into vector code.

Over the twelve benchmarks in `bench/`, on x86 Linux, a 64-bit PHI program is 1.35 times
faster than C# and 2 times faster than C built without optimisation; C built with `gcc -O2`
is 1.14 times faster than PHI ([bench/RESULTS.md](bench/RESULTS.md) has every time; the
[release notes](docs/releases/1.0.0.md) say where PHI is ahead and behind).

- `phi compare` times each benchmark as PHI, C and C# on your own computer.
- `phi test --bench` counts instructions for a set of larger workloads and fails if one got
  slower.
- `phi profile` lists the PHI lines a kernel or a program spent its time in.
- `vec<T>` is for when you want vector instructions by hand: as many numbers as a vector
  register holds, worked on by one instruction each.

## Calling C libraries

The [vendor folder](vendor/README.md) has nine libraries ready. Any other C library is
called the same way they are: a `use` that names its file on each system, and a line for
each function you call.

```phi
use 'mylib.dll' 'libmylib.dylib' 'libmylib.so.1' as My;

My.add: int a int b: int;
My.name: : ptr<u8>;
```

Floats, 64-bit numbers, structs by value, functions that take any number of values, and PHI
methods that C calls back (`addr foreign Name`) all work, from Windows, macOS and 64-bit
Linux programs. `lng`, `ulng`, `isz` and `usz` are C's `long` and `size_t` as each system has
them. [Outside libraries](docs/language.md#outside-libraries) in the reference has the rules,
and [PHI for C programmers](docs/phi-for-c-programmers.md#calling-c) a table of C beside PHI.

## Compiled libraries

`phi build NAME.phi --library` makes `NAME.philib`: a library another program includes with
`use NAME;` without any of its source. One file holds the library for every system it builds
for. [Compiled libraries](docs/language.md#compiled-libraries) has the details.

## How it works inside

PHI's compiler is a .NET program that writes assembly, which NASM turns into the finished
file: a boot image, a `.exe`, a Linux program. There is no linker. For macOS it writes ARM
assembly, which Apple's clang assembles and links.

- **[Architecture](docs/architecture.md):** the compiler, the standard library and the build,
  step by step
- **[Codebase map](docs/codebase-map.html):** the same as a clickable diagram, with where to
  look first for each kind of error
- **[Memory map](docs/memory-map.md):** where everything lives in a kernel's memory, and what
  happens between power-on and your code
- **[The standard library](lib/phi):** 135 modules written in PHI: drivers, memory, a file
  system, processes, the network up to TLS 1.3, graphics, windows, sound and cryptography

## Tests

```
dotnet test PHI.sln               the compiler's own tests
phi test                          every program in tests/, on each system this computer runs
phi test docs/examples            the tutorial's examples
phi test tests/vendor --target macos      each vendor library (or linux64, windows64, windows)
```

[tests/README.md](tests/README.md) says how a test is written and how they're run on each
system, and [CONTRIBUTING.md](CONTRIBUTING.md) what to run before sending a change.

## Settings

Environment variables `phi` reads:

| Variable | What it does |
|---|---|
| `PHI_NASM`, `PHI_QEMU`, `PHI_CLANG` | the full path of a tool that isn't on `PATH` |
| `PHI_VENDOR` | another folder to use as the vendor folder |
| `NASMENV=-O1` | much faster builds of big programs while developing, a few percent larger |
| `PHI_IR=0`, `PHI_IR32=0` | build without the optimiser (every program, or 32-bit ones) |
| `PHI_OPT_OFF` | leave out steps of the optimiser, to find one a wrong result comes from |
| `PHI_LINUX_RUN`, `PHI_WINDOWS_RUN` | how to run another system's programs for `phi test` (a virtual machine, say) |

[Architecture](docs/architecture.md) lists the rest, which are for working on the compiler.

## Goals and roadmap

What PHI is for:

1. Direct access to the machine: assembly, ports, interrupts and memory when you ask
2. Safety by default, which you can turn off where it matters
3. Little to type, and easy to read back
4. Machine code with nothing between: PHI to assembly to a finished file
5. C's libraries, called as they are

What comes next, roughly in order:

1. More tools: hover, finding uses and renaming in the language server, debug information
   on Linux, and packages
2. More places to run: C libraries from 32-bit Linux, 64-bit kernels, ARM beyond macOS
3. Later: PHI's own assembler, and the compiler rewritten in PHI

[Plan.md](Plan.md) is the full roadmap, with what each phase added and why.

# Getting started

This guide installs the tools, gets the PHI compiler, and boots a PHI operating system
in an emulator. It takes about ten minutes.

There are two ways to get `phi`. **A release** is a zip for Windows, Linux or macOS: unzip
it anywhere and `phi` is in it, as one file that needs no .NET, with the vendor folder of C
libraries beside it. NASM and QEMU are still to be installed (below), and then
[Boot PHI OS](#boot-phi-os) is the next step. Run `phi` from that folder, or add the folder to
your `PATH` to run it from anywhere; in PowerShell a program in the current folder is run as
`.\phi`. **From the source**, it's built with the .NET
SDK, as [Build PHI](#build-phi) describes.

## What you need

| Tool | What it's for | Get it |
|---|---|---|
| .NET 10 SDK | builds the PHI compiler from its source (a release doesn't need it) | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download/dotnet/10.0) |
| NASM | turns the assembly PHI generates into machine code | [nasm.us](https://www.nasm.us/) |
| QEMU | an emulated PC to boot PHI programs on | [qemu.org/download](https://www.qemu.org/download/) |
| git | to get the code | [git-scm.com](https://git-scm.com/) |
| Xcode's Command Line Tools | only on a Mac, and only for native macOS programs (`--target macos`): Apple's clang assembles and links them | `xcode-select --install` |

On Linux, your package manager has all of them, for example on Ubuntu:

```
sudo apt install dotnet-sdk-10.0 nasm qemu-system-x86 git
```

On macOS with Homebrew: `brew install --cask dotnet-sdk` and `brew install nasm qemu git`.

PHI needs `nasm` and `qemu-system-i386` on your `PATH`. Check with:

```
nasm -v
qemu-system-i386 --version
```

If they're installed somewhere else, set `PHI_NASM` and `PHI_QEMU` to their full paths
instead.

A big program spends most of its build in NASM, making each jump as short as it can. While
developing one, set the environment variable `NASMENV=-O1`: NASM reads it itself, and builds
many times faster for a program a few percent larger. (`phi build` suggests it when NASM takes
more than 30 seconds.) Leave it off for a kernel's boot code, which has to fit its sector.

> PHI is developed and tested on Linux and on a Mac with Apple Silicon; on Windows it's
> checked by hand, less often. The compiler is plain .NET and runs on all three; if
> something doesn't work, please open an issue.

On a Mac, everything for PHI OS runs as it does elsewhere (QEMU emulates the PC), and a
program built with `--target macos` runs natively. A Linux program (`--target linux` or
`linux64`) is x86 code, which a Mac runs in a Linux machine: set `PHI_LINUX_RUN` to the
command that runs one there (`lima`, say, with [Lima](https://lima-vm.io/) installed), and
`phi run` and `phi test` use it. Without it those runs are set aside and named. A Windows
program runs in a Windows machine the same way, by `PHI_WINDOWS_RUN`: a command given the
program's path and then its arguments, which takes it to the machine and runs it there (a
script that copies it over ssh, say; `HostRunner.cs` says what it's asked).

## Build PHI

```
git clone <this repository>
cd PHI
dotnet build PHI.sln
```

This builds the `phi` command into `Compiler/Phi.Cli/bin/Debug/net10.0/` (`phi.exe` on
Windows). You can run it from there, add that folder to your `PATH`, or run it through
dotnet from anywhere in the repository:

```
dotnet run --project Compiler/Phi.Cli -- run samples/os.phi
```

The rest of the documentation writes it simply as `phi`.

## Boot PHI OS

```
phi run samples/os.phi
```

A QEMU window opens and boots into the PHI shell. Try a few commands:

```
phi> help
phi> ls
phi> cat readme.txt
phi> count &
phi> ps
phi> pong
```

In Pong, `w`/`s` and `o`/`l` move the paddles and `q` quits back to the shell. Close the
QEMU window to stop.

Everything you see was compiled from PHI just now: the kernel in `samples/os.phi`, and the
shell, commands and game from the `.phi` files in `samples/os.rootfs/`.

## Fetch a web page

```
phi run samples/web.phi
```

At the `url:` prompt, type an address such as `http://example.com/` and press Enter. The
kernel prints the raw response: the headers, then the page's HTML. The kernel reaches the
internet through QEMU, so your computer needs a connection. `https://` works too, over TLS
1.3, though PHI doesn't check the server's certificate yet.

## Play music

```
phi run samples/music.phi
```

Type a number to play a song or a WAV file; any key stops it. In PHI OS, `play music/ode.sng`
does the same from the shell (`play music/chip.sng &` keeps the shell free while it plays,
Escape stops it), and `volume 50` turns it down. The sound comes out of your speakers
through QEMU's emulated AC'97 card; `phi run` picks QEMU's sound output for your system
(DirectSound on Windows), and the environment variable `PHI_AUDIO` chooses another (such as
`sdl`, or `none` for silence). Songs are text files, one voice a line: see
`samples/os.rootfs/MUSIC/` and [the language reference](language.md#sound).

## The phi command

| Command | What it does |
|---|---|
| `phi build file.phi` | compile to a bootable disk image, in `build/file/` next to the program |
| `phi build file.phi --library` | compile a library (`phi.Name:Library`) to `file.philib`, which another program includes with `use file;` without its source: see [Compiled libraries](language.md#compiled-libraries) |
| `phi check file.phi` | report errors without building: a program as it is, a module (or a program's class that's part of another program) in a program around it |
| `phi fmt file.phi` | lay out a file, or every `.phi` file in a folder, in place: tabs for what each line is inside, no trailing spaces, and parentheses around an expression in a call with several arguments (`call Add: x (y + h) w;`). It only changes the layout: what the file compiles to stays the same, and a file with errors is left alone. `--check` changes nothing, and fails if a file would change |
| `phi lsp` | the language server, for an editor: errors as you type, going to a definition, completing names. In VS Code, the [PHI extension](https://github.com/BrandonHilde/PHI-VSCODE) runs it |
| `phi vendor` | the C libraries that come with PHI, each used from one line (`use vendor.raylib;`): see [Use a C library](#use-a-c-library) |
| `phi run file.phi` | build, then boot it in QEMU; serial output (`log` and `debug`) also prints in your terminal |
| `phi run file.phi --debug` | start paused, waiting for a debugger (see below) |
| `phi run file.phi --keep-disk` | boot a disk that keeps what the kernel saved (`build/NAME/NAME.kept.img`): each run puts the new kernel and any changed or new programs (`NAME.BIN`) on it but leaves its other files alone; delete it to start again |
| `phi build file.phi --keep-disk` | the same disk made or brought up to date without booting it, for a script that starts QEMU itself (with its own display or devices) |
| `phi run file.phi --disk-mb 64` | a kernel's disk bigger than the 16 MB it has otherwise, up to 2047 (FAT16's most; on `phi build` too). A kept disk keeps the size it has, whatever size the new one is: its programs are brought up to date in it, as with any other kept disk |
| `phi run file.phi --memory 512` | give the machine more memory than QEMU's 128 MB (up to 1024); the kernel's heap is about a quarter of it. `phi test` takes it too |
| `phi check file.phi` | report errors without building |
| `phi build file.phi --target linux` | build a program (`phi.Name:Program`) as an ordinary Linux executable instead (`--target windows`: a `.exe`); see [Programs on Linux and Windows](language.md#programs-on-linux-and-windows) |
| `phi build file.phi --target linux64 --x86-64-v2` | a 64-bit program for x86-64-v2 CPUs (since about 2009 to 2011), whose `vec` code is shorter; an older CPU says so and stops |
| `phi build file.phi --target windows --stack large` | a Windows program reserving 1 MB for its main stack (`normal`, `large`, `huge`, or bytes; 1 MB unless it says); Linux uses the system's |
| `phi run file.phi --target linux -- args` | build it and run it here, with `args` as its command line (a Linux program runs under WSL on Windows) |
| `phi build file.phi --target macos` | on a Mac with Apple Silicon and Xcode's Command Line Tools: a native macOS program; with `--app`, `NAME.app` for the Finder; with `--debug`, a `NAME.dSYM` for lldb; see [Programs on macOS](language.md#programs-on-macos) |
| `phi test` | boot every test in `tests/` without a window and check what each one prints; the tests that can also run as Linux and Windows programs do |
| `phi test --target linux` | run only the tests that can, as Linux programs, without QEMU (much faster); `--target windows` and `--target macos` likewise |
| `phi serve folder` | serve a folder over HTTP (`--https` for HTTPS) on your computer; a kernel in QEMU reaches it at `10.0.2.2` |

The build folder keeps the generated assembly (`boot.asm`, `os.asm` or `kernel.asm`), with
each line of your PHI program shown above the instructions it became. Reading it is a good
way to learn what the compiler does.

## Write your own program

Create `hello.phi` anywhere:

```phi
phi.Hello:Kernel
{
	log 'Hello from my own kernel!\n';
}
```

and run it:

```
phi run hello.phi
```

That's a complete 32-bit operating system kernel. PHI adds the boot sector and the loader
that switches the CPU into 32-bit mode, and your code runs right after. The
[tutorial](README.md#learn-phi) builds from here.

PHI is written with words (`x is 5`, `if a above b`, `n atmost 10`), and every one of
them is reserved, so a few names you might reach for aren't free: `at`, `in`, `out`,
`end`, `has`, `empty`, `below`, `above`, `each`, `when` and `ask` among them. The error
suggests another name (`try 'pos' or 'offset'`), and the
[full list](language.md#reserved-words) is in the reference.

A kernel that saves files (accounts, settings, documents) gets a fresh disk from every
`phi run`, and `phi run` says so when the kernel changed its disk. Run it with
`phi run os.phi --keep-disk` to keep what it saved between runs.

The clock (`drivers.rtc`) is in UTC, as QEMU keeps it, but `phi run` and `phi test` tell the
kernel your computer's time zone, so `Rtc.ReadLocal` gives your local time.

## Use a C library

A program for Windows, macOS or Linux can call C libraries, and nine come with PHI in the
[vendor folder](../vendor/README.md). Save this as `window.phi`:

```phi
use vendor.raylib;

phi.Window:Program
{
	call Ray.SetTraceLogLevel: Ray.LOG_WARNING;       # (raylib says less on the console)
	call Ray.InitWindow: 800 450 'Hello from PHI';
	while not (Ray.WindowShouldClose)
		call Ray.BeginDrawing;
		call Ray.ClearBackground: Ray.RAYWHITE;
		call Ray.DrawText: 'It works' 320 200 30 Ray.DARKGRAY;
		call Ray.EndDrawing;
	;;
	call Ray.CloseWindow;
	exit 0;
}
```

and run it for the system you're on: `phi run window.phi --target windows64` (or `macos`,
`linux64`). `phi build` puts `raylib.dll` (or its macOS or Linux file) beside the program,
so the folder it builds into is what you'd give someone else. With PHI built from its
source, fetch the libraries once first: `python3 vendor/fetch.py`. `phi vendor` lists the
libraries there are.

## When something goes wrong

**Errors in your program** show the file, line and column:

```
hello.phi:3:6: error: unknown name 'nmae'
```

**`Could not find 'nasm'`** (or QEMU): the tool isn't on your `PATH`. Install it, or set
`PHI_NASM` / `PHI_QEMU`.

**The boot sector is too big**: a `Bootloader` class must fit in 512 bytes. Move code into
an `OS` class, or write a 32-bit kernel (`phi.Name:Kernel`), which can be almost 1 MB.

**A red screen that says PHI KERNEL PANIC**: your kernel did something the CPU refused,
like dividing by zero or using a null pointer. The screen names the exception and shows
the instruction address (`eip`) and the registers. Find the address in the generated
`kernel.asm` to see which line of your program it was.

**Nothing happens, or the screen stays black**: add `debug 'got here\n';` lines. `debug`
writes only to the serial port, which `phi run` shows in your terminal, so it works even
when the screen doesn't.

### Debugging with gdb

`phi run file.phi --debug` starts QEMU paused, with a gdb server on port 1234, and logs
interrupts and CPU resets to `build/file/file.qemu.log` (useful for crashes that reboot the
machine). Connect with:

```
gdb -ex "target remote localhost:1234" -ex "set architecture i386" -ex "break *0x100000" -ex continue
```

`0x100000` is where a 32-bit kernel starts. For a 16-bit program, use `set architecture
i8086` and `break *0x7c00`.

### Debugging a Windows program

`--debug` with `--target windows` or `windows64` also writes `NAME.pdb` next to `NAME.exe`,
so a Windows debugger stops on PHI's lines, follows them through methods and files, and shows
the call stack and each variable by its name and type:

```
phi build file.phi --target windows64 --debug
cdb -y build\file build\file\file.exe
```

In `cdb` (from the Windows SDK's Debugging Tools; 32-bit programs use its `x86` one):

| Command | What it does |
|---|---|
| `.lines -e`, then `l+t` and `l+s` | step by PHI's lines, and show each one |
| ``bp `file!file.phi:12` `` | stop at line 12 of file.phi |
| `g` | carry on |
| `p`, `t`, `gu` | the next line; the next line, going into a method; out of this method |
| `k` | the call stack |
| `dv /t` | this method's variables (at the start of a program, its classes' own) |
| `dx place`, `dx point` | a variable, and through a pointer, the struct it points at |

A `--debug` build keeps every variable in memory and writes every method out, so what the
debugger shows is what's there. It runs slower for that, but does the same as any other
build. A `ptr<u8>` shows as text, since it usually is. The standard library's sources go in
`build\file\source`, for stepping into them.

Other debuggers that read `.pdb` files should work too (WinDbg and Visual Studio open the
`.exe`), though `cdb` is the one that's been tried. In VS Code, the C/C++ extension's
`cppvsdbg` debugger needs `"debug.allowBreakpointsEverywhere": true` to set a breakpoint in a
`.phi` file. A Linux program has no debug information yet.

## Next

[Tutorial 1: your first boot sector](tutorial/01-boot-sector.md).

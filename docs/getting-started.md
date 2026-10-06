# Getting started

This guide installs the tools, builds the PHI compiler, and boots a PHI operating system
in an emulator. It takes about ten minutes.

## What you need

| Tool | What it's for | Get it |
|---|---|---|
| .NET 8 SDK | builds and runs the PHI compiler | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| NASM | turns the assembly PHI generates into machine code | [nasm.us](https://www.nasm.us/) |
| QEMU | an emulated PC to boot PHI programs on | [qemu.org/download](https://www.qemu.org/download/) |
| git | to get the code | [git-scm.com](https://git-scm.com/) |

On Linux, your package manager has all of them, for example on Ubuntu:

```
sudo apt install dotnet-sdk-8.0 nasm qemu-system-x86 git
```

On macOS with Homebrew: `brew install --cask dotnet-sdk` and `brew install nasm qemu git`.

PHI needs `nasm` and `qemu-system-i386` on your `PATH`. Check with:

```
nasm -v
qemu-system-i386 --version
```

If they're installed somewhere else, set `PHI_NASM` and `PHI_QEMU` to their full paths
instead.

> PHI is developed and tested on Windows. The compiler is plain .NET and should work on
> Linux and macOS too; if something doesn't, please open an issue.

## Build PHI

```
git clone <this repository>
cd PHI
dotnet build PHI.sln
```

This builds the `phi` command into `Compiler/Phi.Cli/bin/Debug/net8.0/` (`phi.exe` on
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

## The phi command

| Command | What it does |
|---|---|
| `phi build file.phi` | compile to a bootable disk image, in `build/file/` next to the program |
| `phi run file.phi` | build, then boot it in QEMU; serial output (`log` and `debug`) also prints in your terminal |
| `phi run file.phi --debug` | start paused, waiting for a debugger (see below) |
| `phi check file.phi` | report errors without building |
| `phi test` | boot every test in `tests/` without a window and check what each one prints |

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

## When something goes wrong

**Errors in your program** show the file, line and column:

```
hello.phi:3:6: error: unknown name 'nmae'
```

**`Could not find 'nasm'`** (or QEMU): the tool isn't on your `PATH`. Install it, or set
`PHI_NASM` / `PHI_QEMU`.

**The boot sector is too big**: a `Bootloader` class must fit in 512 bytes. Move code into
an `OS` class, or write a 32-bit kernel (`phi.Name:Kernel`), which can be 512 KB.

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
gdb -ex "target remote localhost:1234" -ex "set architecture i386" -ex "break *0x10000" -ex continue
```

`0x10000` is where a 32-bit kernel starts. For a 16-bit program, use `set architecture
i8086` and `break *0x7c00`.

## Next

[Tutorial 1: your first boot sector](tutorial/01-boot-sector.md).

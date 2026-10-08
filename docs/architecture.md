# Architecture

This is a map of how PHI works inside, for anyone changing the compiler, the standard
library or the build. For the language itself, see [language.md](language.md).

## The repository

| Folder | What's in it |
|---|---|
| `Compiler/Phi.Compiler/` | the compiler, a .NET library: PHI source in, NASM assembly out |
| `Compiler/Phi.Cli/` | the `phi` command: runs the compiler and NASM, builds disk images, runs QEMU and the tests |
| `Compiler/Phi.Compiler.Tests/` | xUnit tests for the compiler and the image builder (no QEMU needed) |
| `lib/boot/` | the boot sector and loader PHI adds to 32-bit kernels |
| `lib/x86_16/` | assembly library for 16-bit programs (uses the BIOS) |
| `lib/x86_32/` | assembly library for 32-bit kernels: console, interrupts, panic screen, timer, keyboard, mouse, tasks |
| `lib/x86_32_user/` | assembly library for user programs: system calls |
| `lib/phi/` | the standard library written in PHI: drivers, memory, file system, processes, the network, text |
| `tests/` | programs that boot in QEMU, with the output they must produce |
| `samples/` | example programs, including PHI OS |
| `docs/` | this documentation, and the tutorial's tested examples |

Everything in `lib/` is embedded in the compiler when it's built, so `phi` needs no files
next to it at run time.

## From source to disk image

```
file.phi ─▶ Lexer ─▶ Parser ─▶ (use: more files) ─▶ Binder ─▶ X86Generator ─▶ .asm ─▶ NASM ─▶ Builder ─▶ .img
```

[PhiCompiler.cs](../Compiler/Phi.Compiler/PhiCompiler.cs) runs the stages; the `phi` command's
[Builder.cs](../Compiler/Phi.Cli/Builder.cs) runs NASM and assembles the image.

### Lexer and parser (`Syntax/`)

[Lexer.cs](../Compiler/Phi.Compiler/Syntax/Lexer.cs) turns text into tokens. Two PHI-specific
details live here: a quote followed by a letter doesn't end a string (so `'hasn't'` works),
and a `#` alone on a line starts a block comment. Each token records whether it starts a
line, which the parser uses to know where an `if` condition ends.

[Parser.cs](../Compiler/Phi.Compiler/Syntax/Parser.cs) is a recursive-descent parser that
builds the tree in [Ast.cs](../Compiler/Phi.Compiler/Syntax/Ast.cs). It recovers from errors
(skipping to the next line or statement), so one mistake doesn't hide the rest. Expression
precedence, lowest first, is: `or`, `and`, comparison, `|`, `^`, `&`, shifts, `+ -`,
`* / %`, unary, postfix (`:index`, `.field`).

`use path;` lines are followed in `PhiCompiler.ParseWithUses`: each file is parsed once, and
used files' classes come before the classes that use them. A kernel that uses `new` or
`free` gets `memory.heap` added automatically.

### Binder (`Semantics/`)

[Binder.cs](../Compiler/Phi.Compiler/Semantics/Binder.cs) checks the program and works out
everything the generator needs:

- **Units.** Every class goes into a unit, by its base: `Bootloader` → Boot (512 bytes at
  `0x7C00`), `OS` → Os (16-bit, `0x7E00`), `Kernel` → Kernel (32-bit, `0x10000`), `Program` →
  Program (32-bit user mode, `0x40000000`). `Library` classes join whichever unit the program
  has. A program is 16-bit (Boot + Os), a kernel, or a user program; they don't mix.
- **Names and storage.** Every variable, including method parameters and locals, gets a fixed
  label: `VALUE_name` for class variables (so `asm.` blocks can use `{name}`), and the method's
  label added in front of the name for parameters and locals. There is no stack frame for PHI
  methods; this is why there's no recursion, and why the kernel is never preempted (see
  [Concurrency](#concurrency)).
- **Types** ([Symbols.cs](../Compiler/Phi.Compiler/Semantics/Symbols.cs)): sized integers,
  `bln`, `str` (fixed capacity), `ptr<T>`, packed structs, and arrays of all of them. Math is
  32-bit, unsigned when either side is `u32` or a pointer.
- **Rewrites** that keep the generator small: `x++ 2` becomes `x is x + 2`, a one-character
  string next to a number becomes its character code, and `new T[n]` becomes a call to
  `Heap.Alloc` with `n * size`.
- **Built-ins** ([Builtins.cs](../Compiler/Phi.Compiler/Semantics/Builtins.cs)): each built-in
  function names an assembly label. It's available in a unit only if that unit's library
  defines the label, so `OS.ReadFile` exists in programs (a system call) but not in kernels
  (which use `Fat`), and the binder's error says so.
- **Bounds checks.** Indexes of arrays and `str`s are checked at run time (at build time when
  the index is a constant), except inside `unsafe`.

### Code generator (`CodeGen/`)

[X86Generator.cs](../Compiler/Phi.Compiler/CodeGen/X86Generator.cs) writes one NASM file per
unit, in 16-bit or 32-bit code. It is deliberately simple:

- An expression's value ends up in `eax`; the right side of a binary operation in `ecx`.
- Nothing is kept in registers between statements, so library routines may change any
  general-purpose register, and every read and write really happens (which device memory
  needs).
- Memory is reached through a **place**: a fixed label, a label plus a computed offset in
  `ebx`, or a computed address in `ebx` (through a pointer). In 16-bit code, a pointer above
  64 KB goes through the `fs` segment; in 32-bit code memory is flat.
- Each PHI source line is written as a comment above its instructions.

Built-in functions use a C-like convention: arguments pushed right to left as 32-bit values,
the caller removes them, the result comes back in `eax`. PHI methods instead take their
parameters in their fixed variables.

### The assembly library

[Library.cs](../Compiler/Phi.Compiler/CodeGen/Library.cs) loads `lib/<target>/*.asm`. Each
file starts with a header:

```nasm
; provides: OS_SetupKeyboardInterupt OS_GetKey OS_IsKeyDown ...
; requires: phi_irq_register
; hooks: OS_KeyboardEvent
; init: phi_console_init
; inherit: x86_32 int_to_str.asm strcpy.asm     (lib/x86_32_user/_inherit.asm)
```

The generator records which labels the program uses; the library adds the files that provide
them, and their requirements, once each. `hooks` are events the file calls (`[OS.TimerEvent]`):
if the program doesn't define one, an empty one is added. `init` routines run before the
program's code. `inherit` borrows files from another target, so user programs share the
kernel's number formatting without duplicating it.

### The build (`Phi.Cli/`)

[Builder.cs](../Compiler/Phi.Cli/Builder.cs) assembles the units and lays out the disk:

| Program | Disk image |
|---|---|
| 16-bit | sector 0: boot sector; sector 1 on: the OS classes |
| 32-bit kernel | sector 0: stage 1; sectors 1-8: the loader; sector 9 on: the kernel; sector 2048 on: a 16 MB FAT16 partition |
| user program | not an image: `NAME.bin`, to put in a kernel's rootfs |

The kernel is assembled first, so the boot code can be given its exact size in sectors.
[FatImage.cs](../Compiler/Phi.Cli/FatImage.cs) formats the partition from the program's
`NAME.rootfs/` folder, compiling any `.phi` file there into a `.BIN` user program.

## Inside a 32-bit kernel

The boot process and memory layout are in [memory-map.md](memory-map.md). The layers, from
the bottom:

| Layer | Where | Language |
|---|---|---|
| boot sector and loader | `lib/boot/` | assembly |
| console, interrupt table, panic screen | `lib/x86_32/console.asm`, `interrupts.asm`, `panic.asm` | assembly |
| timer, keyboard, mouse drivers | `lib/x86_32/timer.asm`, `keyboard.asm`, `mouse.asm` | assembly |
| tasks, scheduler, system calls | `lib/x86_32/tasks.asm` | assembly |
| VGA, disk, clock drivers | `lib/phi/drivers/` | PHI |
| frames, paging, heap, lists | `lib/phi/memory/` | PHI |
| FAT16 | `lib/phi/fs/fat16.phi` | PHI |
| loading and running programs, system calls 16-30 | `lib/phi/proc/process.phi` | PHI |
| PCI, the RTL8139 network card | `lib/phi/drivers/pci.phi`, `rtl8139.phi` | PHI |
| ARP, IPv4, ICMP, UDP, DHCP, DNS, TCP, HTTP | `lib/phi/net/` | PHI |
| shell and commands | `samples/os.rootfs/` | PHI, as user programs |

Interrupts all go through one stub in `interrupts.asm`, which saves the registers and
segment registers, then dispatches: exceptions to the panic screen (or, for a user program,
to stopping it), IRQs to their registered handler, `int 0x80` to the system-call dispatcher,
and vector 48 to the scheduler. Multitasking plugs into that stub through three hooks.

### Concurrency

Because method variables have fixed addresses, two pieces of code running the same method at
once would overwrite each other's variables. PHI avoids this by design:

- **The kernel is never preempted.** Its code runs until it waits (`OS.Yield`, `Process.Wait`)
  or its main code ends. System calls run with interrupts off.
- **User programs are preempted** by the timer, but each has its own memory, so its variables
  are its own.

Interrupt handlers and events do interrupt kernel code, so they should only touch their own
variables. Giving methods stack frames (and so recursion and kernel threads) is the first item
in the plan's "what's next".

## Tests

| Kind | Where | Runs with |
|---|---|---|
| compiler unit tests | `Compiler/Phi.Compiler.Tests/` | `dotnet test PHI.sln` |
| programs in QEMU | `tests/*.phi` | `phi test` |
| the tutorial's examples | `docs/examples/*.phi` | `phi test docs/examples` |

A QEMU test is `NAME.phi` plus one of:

- `NAME.expected`: the exact serial output (line endings and trailing spaces don't matter)
- `NAME.contains`: lines that must appear (for output that changes, like panic addresses)
- `NAME.errors`: the program must fail to compile with these messages

and optionally `NAME.input` (keys and mouse movements, see [tests/README.md](../tests/README.md)),
`NAME.rootfs/` (the disk) and `NAME.web/` (files served over HTTP while the test runs). A test made only of `Library` classes runs twice, as a 16-bit
program and as a 32-bit kernel, against the same expected output, which keeps the two code
generators in step.

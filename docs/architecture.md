# Architecture

This is a map of how PHI works inside, for anyone changing the compiler, the standard
library or the build. For the language itself, see [language.md](language.md).

For a picture of the same thing, open [codebase-map.html](codebase-map.html) in a browser: every
part as a box, with arrows for what passes between them. Click a box for its files and when to
suspect it, or pick a symptom ("nasm failed", "wrong output only with the new path", "stopped
by exception 6") to see where to look first, in order.

## The repository

| Folder | What's in it |
|---|---|
| `Compiler/Phi.Compiler/` | the compiler, a .NET library: PHI source in, NASM assembly out (ARM assembly for clang, for a macOS program: `CodeGen/A64/`) |
| `Compiler/Phi.Cli/` | the `phi` command: runs the compiler and NASM, builds disk images, runs QEMU and the tests |
| `Compiler/Phi.Compiler.Tests/` | xUnit tests for the compiler and the image builder (no QEMU needed) |
| `lib/boot/` | the boot sector and loader PHI adds to 32-bit kernels |
| `lib/x86_16/` | assembly library for 16-bit programs (uses the BIOS) |
| `lib/x86_32/` | assembly library for 32-bit kernels: console, interrupts, panic screen, timer, keyboard, mouse, tasks |
| `lib/x86_32_user/` | assembly library for user programs: system calls |
| `lib/x86_32_linux/` | assembly library for programs built with `--target linux`: Linux system calls |
| `lib/x86_32_windows/` | assembly library for programs built with `--target windows`: the `kernel32.dll` import table and calls |
| `lib/x86_64_linux/`, `lib/x86_64_windows/` | the same for 64-bit programs (`--target linux64`, `windows64`) |
| `lib/arm64_macos/` | assembly library (ARM, for Apple's clang) for programs built with `--target macos`: the console, errors and faults, threads, text and numbers, AppKit and audio |
| `lib/phi/` | the standard library written in PHI: drivers, memory, file system, processes, the network, cryptography, sound, text |
| `tests/` | programs that boot in QEMU, with the output they must produce |
| `samples/` | example programs, including PHI OS |
| `docs/` | this documentation, and the tutorial's tested examples |

Everything in `lib/` is embedded in the compiler when it's built, so `phi` needs no files
next to it at run time.

## From source to disk image

```
file.phi ─▶ Lexer ─▶ Parser ─▶ (use: more files) ─▶ Binder ─┬▶ new path: IrBuilder ─▶ IrPeephole ─▶ RegisterAllocator ─▶ IR emitter ─┬▶ .asm ─▶ NASM ─▶ Builder ─▶ .img, ELF, .exe
                                                            └▶ X86Generator (everything else) ─────────────────────────────────────┘

a macOS program:                                    Binder ─▶ IrBuilder ─▶ (methods put in) ─▶ IrPeephole ─▶ RegisterAllocator ─▶ ARM back end ─▶ program.s ─▶ clang ─▶ NAME, NAME.app
```

On x86 each method goes one of two ways (see [The new path](#the-new-path-codegenir) below);
both read the binder's checked program and write into the same .asm file. A macOS program
goes through the new path whole, to [the ARM back end](#the-arm-back-end-codegena64).

[PhiCompiler.cs](../Compiler/Phi.Compiler/PhiCompiler.cs) runs the stages; the `phi` command's
[Builder.cs](../Compiler/Phi.Cli/Builder.cs) runs NASM (or clang) and assembles the image.

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

A struct that takes types (`struct.Stack<Item>`) is kept by the parser as a template, and
each `Stack<int>` where a type goes is noted as a use. Once every file is read,
[Templates.cs](../Compiler/Phi.Compiler/Syntax/Templates.cs) makes each use its struct: the
parser reads the template's tokens again with `Item` standing for the type given, and the
struct that comes out is named `Stack<int>`. Everything after sees ordinary structs. Each
is read from a copy of the template's file, which is how an error in one says which use it
was for (`DiagnosticBag.CopyOf`).

### Binder (`Semantics/`)

[Binder.cs](../Compiler/Phi.Compiler/Semantics/Binder.cs) checks the program and works out
everything the generator needs:

- **Units.** Every class goes into a unit, by its base: `Bootloader` → Boot (512 bytes at
  `0x7C00`), `OS` → Os (16-bit, `0x7E00`), `Kernel` → Kernel (32-bit, `0x100000`), `Program` →
  Program (32-bit user mode, `0x40000000`). `Library` classes join whichever unit the program
  has. A program is 16-bit (Boot + Os), a kernel, or a user program; they don't mix.
- **Names and storage.** Class variables get a fixed label, `VALUE_name` (so `asm.` blocks can
  use `{name}`). A method's parameters and variables live in its stack frame: the caller
  pushes the arguments (a `str` argument as the address of its text, which the method copies
  into a buffer of its own), and the method reserves and zeroes room for its variables below
  `ebp`. A method that returns a `str` copies it into a buffer the caller passes, so two
  results can be used side by side. The layout is described in `LayOutFrame` in
  [X86Generator.cs](../Compiler/Phi.Compiler/CodeGen/X86Generator.cs).
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
unit, in 16-, 32- or 64-bit code. In a 32- or 64-bit program, most methods now come from the
new path (next section); the generator writes everything else: 16-bit code, kernels, debug
builds, a class's own statements, calls into Windows libraries, any method the new path
refuses, and everything around methods (variables, tables, executable headers, compiled
libraries' code, and the runtime routines the new path calls). Its own method code is
deliberately simple:

- An expression's value ends up in `eax`; the right side of a binary operation in `ecx`.
- In 32-bit code, a method keeps up to two of its most used variables in `esi` and `edi`
  (32-bit numbers and pointers that aren't `volatile` and whose address isn't taken; class
  variables stay in memory, since interrupt handlers share them, but see the next point). They're chosen
  by [VariablePlanner.cs](../Compiler/Phi.Compiler/CodeGen/VariablePlanner.cs): a use inside
  a loop counts 8 times one outside it. A method that changes `esi` or `edi` saves them
  first, so they're kept across calls to methods. Library routines and `asm.` blocks may
  change any register, so a statement that needs one (or copies text, or works with
  floats, 64-bit numbers or objects) is *fenced*: the variables go to their places in the
  frame before it and come back after it. The generator checks its own output for a stray
  `esi` or `edi` outside a fence.
- In 64-bit code it's up to four, in `r12` to `r15`, by the same rules: a 32-bit number in
  the low half (`r12d`) and a pointer in the whole register. As an index, a number that
  can't be negative (unsigned, or just checked) is used as it is, and any other goes
  through `r11` with its sign (`IndexRegister`).
- A `while` in a class's own code does the same for the class variables it uses most
  (`EmitClassStatement`), but only those nothing else can reach while it runs: not one a
  method or an asm block names (an interrupt handler, another thread or another core may be
  running it), not a `volatile` one, and not one whose address is taken. They're loaded
  before the loop and stored after it, so memory is right whenever other code runs.
- `x / d` and `x % d` with `d` a constant of 2 or more don't use the CPU's divide: a power
  of two is a shift or a mask, and any other, in 32- and 64-bit code, a multiply by a number
  near 2^32 / d and a shift (`TryDivideByConstant`, with the numbers from
  [DivisionMagic.cs](../Compiler/Phi.Compiler/CodeGen/DivisionMagic.cs)). Times a power of
  two is a shift.
- A `when` on a number whose values are constants close together jumps through a table
  of its branches (`DenseWhen`); any other compares its values one at a time, in order.
- Everything else is read and written in memory every time, which device memory needs.
- A call to a small method (no loops, everything in it one register) is written out where
  it's called instead (32-bit code only: in 64-bit code it made programs bigger and no faster), with its parameters and variables in the caller's frame
  (`CanInline` and `EmitInline`).
- Memory is reached through a **place**: a fixed label, a label plus a computed offset in
  `ebx`, or a computed address in `ebx` (through a pointer). In 16-bit code, a pointer above
  64 KB goes through the `fs` segment; in 32-bit code memory is flat.
- Each PHI source line is written as a comment above its instructions.
- In 32-bit code the variables start on a 4 KB page of their own. When code and variables it
  writes share a page, QEMU discards its translation of that code on every write, and hot
  loops ran up to ~200 times slower.

Built-in functions use a C-like convention: arguments pushed right to left as 32-bit values,
the caller removes them, the result comes back in `eax`. PHI methods instead take their
parameters in their fixed variables.

### The new path (`CodeGen/Ir/`)

The optimiser: a method's checked tree becomes a small intermediate form, which is tidied,
given registers and written out as x86, or as ARM for a macOS program. It is on by default
for 64-bit and 32-bit programs, it is all a macOS program has, and it is meant to become
PHI's only code generator.

1. [IrBuilder](../Compiler/Phi.Compiler/CodeGen/Ir/IrBuilder.cs) (with `.Arrays`, `.Text`,
   `.Loops`, `.Vec`, `.Outer` and `.Objects`) turns a method into the form defined in
   [Ir.cs](../Compiler/Phi.Compiler/CodeGen/Ir/Ir.cs): simple instructions over numbered values,
   in blocks. It takes only what it knows exactly; anything else refuses the method with a
   reason, and the generator writes that method instead. Loops that sum or work on arrays
   can become vector code here.
2. [IrPeephole](../Compiler/Phi.Compiler/CodeGen/Ir/IrPeephole.cs) makes small rewrites: jumps to
   jumps, `x * 3, 5 or 9` as one `lea`, dead values, folded constants, bit tests and, in 32-bit
   code, constant offsets folded into addresses. It runs
   [IrOptimiser](../Compiler/Phi.Compiler/CodeGen/Ir/IrOptimiser.cs) first, whose steps only
   take instructions away or move them to where they run less often:
   - a test of a number written in the program (`while true`) is a jump;
   - a copy `x = y`, where each is given a value in one place, goes, and what read `x` reads `y`;
   - what was worked out, read from memory or checked a moment ago isn't done again while
     nothing it's made from has changed, in one block or in all the blocks that lead to it.
     A call forgets everything, a write forgets every read, and a program with a `volatile`
     anywhere (or a variable at an address of its own) has no read used twice. Every time
     round a loop still reads what it tests from memory at least once;
   - a multiply or float arithmetic that comes to the same every time round a loop that
     calls nothing is done once before it (at most 2 values a loop on x64, 6 on ARM, which
     has more registers).

   The last two are for 64-bit and ARM code only: a value kept to use again holds a
   register, and 32-bit code has three.
3. [RegisterAllocator](../Compiler/Phi.Compiler/CodeGen/Ir/RegisterAllocator.cs) gives out
   registers by linear scan, with [Liveness](../Compiler/Phi.Compiler/CodeGen/Ir/Liveness.cs).
   What doesn't get one gets a slot in the frame. 32-bit code has three registers for values
   (ebx, esi, edi) and keeps every 64-bit number in the frame, as a pair of halves. A value
   made from one read there for the last time takes its register (`i + 1` is worked out
   where `i` was, and a copy is no instruction), and on x64 a call's result or a sum that
   only the next instruction reads stays in `rax` (ARM's `x0`). A method that may return at
   once saves its kept registers only on the way that goes on.
4. The emitter, [X86Generator.Ir.cs](../Compiler/Phi.Compiler/CodeGen/X86Generator.Ir.cs) with
   `IrVec.cs` (vector code) and `IrText.cs` (numbers as text), writes each instruction. 32-bit
   code is written as 64-bit text and translated as it goes out (`To32`); `Not32` throws on
   anything 32-bit code doesn't have. `Fits32` refuses a 32-bit method this back end can't do
   yet, and on PHI OS any method with floats or `vec`, since PHI OS doesn't turn SSE on.

To find which path is at fault, and where:

| Setting | What it does |
|---|---|
| `PHI_IR=0` | every method goes to the generator |
| `PHI_IR32=0` | the same, for 32-bit programs only |
| `PHI_IR_SKIP=Class.Method` | one method goes to the generator |
| `PHI_IR_TRACE=1` | which methods took the new path, and why the others didn't; `2` also prints each method's form |
| `PHI_IR_REGS=1` | each value's live range, and whether it got a register or a frame slot |
| `PHI_IR_VERIFY=1` | where a value is made in a register another live value has (the allocator checking itself) |
| `PHI_INLINE_TRACE=1` | macOS programs: each method put in where it's called, with its form |
| `PHI_OPT_OFF=same,hoist` | leaves out steps of the optimiser, to find the one a wrong program comes from: `consts`, `same` (its parts `copies`, `before`, `gives`), `hoist`, `share` (a result in an operand's register), `entry` (x64's late saving of kept registers), `rax`, `tidy` (ARM's `cbz`), `spare` (ARM's `x4` and `x5`), or `all` |
| `PHI_OPT_FN=Heap.Free,Map` | `same` and `hoist` only in the methods whose names have one of these in them |

A wrong form is the builder's; a right form with wrong registers the allocator's; right
registers with wrong instructions the emitter's. Every change here is checked against the
generator: the tests run both ways ([tests/README.md](../tests/README.md)), and `phi fuzz`
compares random programs' output with what they must print.

### The ARM back end (`CodeGen/A64/`)

A macOS program (`--target macos`) is ARM code for Apple Silicon, and all of it comes from
the form: [A64Generator](../Compiler/Phi.Compiler/CodeGen/A64/A64Generator.cs) has no
generator behind it. A class's own statements are built as a method, the routines a struct
or an array needs (free, clone, compare, assign) are built as the form too
(`A64Generator.Objects.cs`), and what the form doesn't take is an error when the program is
built ("… which macOS programs can't do yet"), so `phi test --target macos` runs exactly the
tests that build.

- **One form, two back ends.** The IR builder is shared. What only ARM's builds do is behind
  its `arm` switch, so x86's code stays as it was: a test, `AsmSnapshotTests`, hashes the
  x86 code of every test and sample on every system (asked for with `PHI_ASM_SNAPSHOT`), and
  a change meant for ARM shows there as no difference at all.
- **The same answers.** What x86's instructions do and ARM's don't is made the same, so the
  tests' expected output is shared: dividing by zero (and the smallest number by -1) is the
  same error with its line, a float turned into a whole number out of range gives x86's
  answer, `sin` and `cos` of a huge number, an `f16`'s rounding, and what a `vec` does lane
  for lane (NEON for SSE2).
- **Calls** are PHI's own between PHI methods, as on x86 (arguments in the frame, the first
  highest; the result in `x0`, `d0` or `q0`), and C's for the runtime's routines and outside
  libraries. Values are in `x10` to `x15` and `x19` to `x27` (and `v16` to `v27`); `x0` to
  `x9`, `x16` and `x17` are the emitter's own, and `x28` is the thread's block. `x4` and
  `x5`, which the emitter writes only as it sets up a call, also hold a value that no call
  is made in the life of, so a method that calls nothing has fewer kept registers to save.
- **What it does for speed**, each before or after registers are given out
  (`A64Generator.Inline.cs`, `A64Generator.cs`): a small method is put in where it's called,
  up to three deep, its lines still its own file's; a short if and else that set one variable
  become a choice with no jump (`csel`, `csinc`); a loop keeps the class variables it changes
  in registers, by the generator's rules; an index made 64 bits goes into the read itself
  (`[x1, w2, uxtw #2]`); a value made and read at once is made in `x0`; a test against 0
  is one `cbz` or `cbnz`; and a method that may return at once saves its kept registers
  only on the way that goes on.
- **Assembly for clang.** Its text is `program.s`, with the runtime from `lib/arm64_macos/`
  pasted in as NASM's is. [Builder.cs](../Compiler/Phi.Cli/Builder.cs) (`AssembleMacOS`) has
  Apple's clang assemble it and link it with `libSystem` (and `libobjc` and frameworks when
  the program names them with `use '…'`), and the linker signs it. Faults (a bad address, a
  stack run out) arrive as signals, and `faults.s` turns each into PHI's error with its line,
  found in a table of line marks the program carries.

When a macOS program is wrong and the same program for `linux64` is right, the fault is
here or behind `arm` in the builder; when both are wrong, it's before them.

### The assembly library

[Library.cs](../Compiler/Phi.Compiler/CodeGen/Library.cs) loads `lib/<target>/*.asm` (for a
macOS program, `lib/arm64_macos/*.s`, with the same headers). Each file starts with a header:

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
| program, `--target linux` | an ELF executable, `NAME`: its header and one segment, loaded at `0x08048000` |
| program, `--target windows` | a PE executable, `NAME.exe`: its headers and one section at `0x400000`, with the import table in it |
| program, `--target macos` | a Mach-O executable for arm64, `NAME`: PHI writes `program.s`, Apple's clang assembles and links it with libSystem (and libobjc and frameworks, for a window or sound) and signs it; `--app` puts it in `NAME.app` |

The kernel is assembled first, so the boot code can be given its exact size in sectors.
[FatImage.cs](../Compiler/Phi.Cli/FatImage.cs) formats the partition from the program's
`NAME.rootfs/` folder, compiling any `.phi` file there into a `.BIN` user program.

An executable for another system needs no linker either: the generator writes its headers
in front of the code ([X86Generator.cs](../Compiler/Phi.Compiler/CodeGen/X86Generator.cs),
`EmitElfHeader` and `EmitPeHeader`), and NASM's flat output is the whole file. The program's
library is `lib/x86_32_linux/` or `lib/x86_32_windows/` instead of `lib/x86_32_user/`: the
same labels (`OS_ReadFile`, `phi_exit` ...), answered by that system.

A macOS program is the one that is linked: Apple requires its programs to be signed and to
reach the system through its libraries, so the builder hands `program.s` to clang
(`PHI_CLANG`, or `/usr/bin/clang` from Xcode's Command Line Tools), which assembles it,
links it and signs it. `--app` then makes `NAME.app` round it (an `Info.plist`, the program
in `Contents/MacOS`, signed again as a bundle), and `--debug` a `NAME.dSYM` for lldb.

### Compiled libraries

`phi build NAME.phi --library` ([Builder.cs](../Compiler/Phi.Cli/Builder.cs), `BuildLibrary`)
makes `NAME.philib` ([CompiledLibrary.cs](../Compiler/Phi.Compiler/Libraries/CompiledLibrary.cs))
with no linker, for each system in turn:

1. `PhiCompiler.CompileLibrary` binds the library as a program would be, but the files that
   aren't its own are only used by name (`LibraryBuild`, `IsOutside`), and the generator's
   `BuildLibrary` writes assembly for an object file: its classes' code as a routine, its
   methods each in a section, its variables in `.data`, `extern` for the labels that are the
   program's to give, and `global` for its methods and class variables.
2. NASM makes an ELF object of it (`-f elf32` or `-f elf64`, on every system), and `ElfObject`
   reads out the sections and every address that's still to fill in. Names that aren't exported
   stay inside, so no two libraries' can clash.
3. `InterfaceWriter` writes what a program sees: each of its files as PHI with no code, read back
   by the same parser, whose methods end at their header. That says what they return, what room
   a `str` result needs, and which `str` parameters they write into, which would otherwise come from
   their bodies.

```phi
[Greet: str who; : str room 256]
[Fill: str target; : void writes target]
```

Then a program's `use` finds it (`PhiCompiler.Resolve`), its interface binds like any other file,
and the generator writes what it reaches as source in the program's usual `-f bin` run (`db`
lines, with `dd label + n` at each address, or `dd label + n - $` where it's relative), so NASM
fills in every address and the ELF and PE headers stay where they are. Only the final build
knows the whole program, so what depends on that is a label in the library's code and is written
then: each interface's place in a type table (`phi_slot_Shape`), the type tables, and an outside
function's import slot. (For macOS, built on a Mac, the library's code is clang's object file,
kept whole in the `.philib` and handed to the program's link: `A64Generator.Library.cs`.) A class variable's label is always `VALUE_Class_name` so nothing else can
change it, and the library's list of lines joins the program's. Any compiled library makes the
program one that catches errors (`Catches`), since its code was compiled to allow for that.

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
| PCI, the RTL8139 network card, the AC'97 sound card | `lib/phi/drivers/pci.phi`, `rtl8139.phi`, `ac97.phi` | PHI |
| graphics mode, the keyboard and mouse on their interrupts | `lib/phi/drivers/vbe.phi`, `input.phi` | PHI |
| drawing, the VGA font | `lib/phi/gfx/` | PHI |
| ARP, IPv4, ICMP, UDP, DHCP, DNS, TCP, TLS 1.3, HTTP | `lib/phi/net/` | PHI |
| SHA-256, HMAC, HKDF, AES-128-GCM, ChaCha20-Poly1305, X25519, random numbers | `lib/phi/crypto/` | PHI |
| a synthesizer for songs written as text, WAV files, playing them | `lib/phi/audio/` | PHI |
| shell and commands | `samples/os.rootfs/` | PHI, as user programs |

Interrupts all go through one stub in `interrupts.asm`, which saves the registers and
segment registers, then dispatches: exceptions to the panic screen (or, for a user program,
to stopping it), IRQs to their registered handler, `int 0x80` to the system-call dispatcher,
and vector 48 to the scheduler. Multitasking plugs into that stub through three hooks.

### Concurrency

A method's variables are on the stack, so two pieces of code can run the same method at once.
Class variables, though, are shared, and PHI keeps code from changing them at the same time
by design:

- **The kernel is never preempted.** Its code runs until it waits (`OS.Yield`, `Process.Wait`)
  or its main code ends. System calls run with interrupts off.
- **User programs are preempted** by the timer, but each has its own memory, so its variables
  are its own.

Interrupt handlers and events do interrupt kernel code. They can call any method, but should
be careful with class variables the interrupted code may be using. With stack frames in
place, kernel threads are now possible; they would need a stack each and locking for shared
class variables.

## Tests

| Kind | Where | Runs with |
|---|---|---|
| compiler unit tests | `Compiler/Phi.Compiler.Tests/` | `dotnet test PHI.sln` |
| programs in QEMU, and as Linux, Windows and macOS programs | `tests/*.phi` | `phi test` (`--target linux`, `windows` or `macos` for those alone) |
| the tutorial's examples | `docs/examples/*.phi` | `phi test docs/examples` |
| random programs with known output | written as it runs; wrong ones kept in `fuzz-failures/` | `phi fuzz code`, `numbers`, `objects` |
| speed against C and C# | `bench/` (results in `bench/RESULTS.md`, from x86 Linux) | `phi compare` |
| x86's code, unchanged by a change meant for something else | every test and sample, hashed | `PHI_ASM_SNAPSHOT=FILE dotnet test --filter AsmSnapshot` |
| the code libraries, PHI OS | `../PHI-Code-Library`, `../PHI-OS` | `pwsh ./build.ps1 -Test`, `./test.sh` |

A QEMU test is `NAME.phi` plus one of:

- `NAME.expected`: the exact serial output (line endings and trailing spaces don't matter)
- `NAME.contains`: lines that must appear (for output that changes, like panic addresses)
- `NAME.errors`: the program must fail to compile with these messages

and optionally `NAME.input` (keys and mouse movements, see [tests/README.md](../tests/README.md)),
`NAME.rootfs/` (the disk), `NAME.web/` (files served over HTTP while the test runs) and
`NAME.sound` (checks on what the sound card played: QEMU records it to a WAV file, and
[SoundCheck.cs](../Compiler/Phi.Cli/SoundCheck.cs) measures how long it sounds and its pitches). A test
made only of `Library` classes runs twice, as a 16-bit program and as a 32-bit kernel, against
the same expected output, which keeps the two code generators in step.

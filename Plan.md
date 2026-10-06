# PHI Roadmap: From Bootloader to Operating System

PHI's goal is to be a language with everything needed to build an operating system:
booting, drivers, memory, storage, and processes. These are built into the language and
its standard library, so writing an OS feels like writing an app.

This plan covers the **basics**: a 32-bit x86 OS that boots, handles interrupts, manages
memory, reads files from a disk, runs several programs, and has a shell. It is all written
in PHI and runs in QEMU.

---

## Ground rules

- **QEMU is the only target for now.** We only support the hardware QEMU emulates by
  default (i440FX machine, PS/2 keyboard and mouse, VGA, PIT timer, IDE disk, COM1 serial).
  Real hardware, UEFI and USB come later.
- **x86 first.** ARM and 64-bit are later work (see [Later](#later)).
- **Every phase ends with a demo that boots in QEMU** and an automated test that checks it.
- **Existing programs keep working.** `hello.phi`, `code.phi` and `arcade.phi` become
  regression tests. If a change breaks Pong, the change isn't done.

---

## Where we are today

| Area | Status |
|---|---|
| Translator | Hand-written character-by-character parser (`Translator.cs`) → `PhiClass` model → assembly pasted from string templates (`ASMx86_16BIT.cs`, `TranslateToX86.cs`) |
| CPU mode | 16-bit real mode only |
| Boot | Boot sector (`Bootloader`) loads a fixed **6 sectors** (hard-coded) and jumps to sector two |
| Built-ins | `log`, `ask`, wait for key, keyboard and timer interrupts, PS/2 mouse, VGA mode 13h pixels and rectangles |
| Language | Classes with a base (`phi.Name:Base`), typed variables (`str int bln byt dec var`), `call`, `call x is Y`, loops, `if`, math expressions, `asm.` blocks |
| Build | `ConvertFile` prompts for a file → `phi.ASM` → `buildSingleASM.bat` (nasm + qemu) |
| Tests | None |

**The main limitation:** `log`, `ask` and the disk read all call BIOS interrupts
(`int 0x10`, `int 0x13`, `int 0x16`). BIOS calls only work in 16-bit real mode, which
also limits us to about 1 MB of memory and has no memory protection. A real OS has to
run in 32-bit protected mode, and every built-in has to be rewritten to talk to the
hardware directly. Most of this plan follows from that.

---

## Overview of phases

| # | Phase | Demo at the end |
|---|---|---|
| 0 | Foundations: tooling, tests, serial debugging | `phi run hello.phi` boots, and a test checks the output automatically |
| 1 | Compiler rebuild: lexer → AST → code generator | All current samples produce working output from the new compiler |
| 2 | Systems-language features | A PHI-only VGA text driver, with no `asm.` blocks |
| 3 | Boot into 32-bit protected mode | "Hello from 32-bit PHI" printed by a PHI kernel |
| 4 | Interrupts and core drivers | Typing on screen, a ticking clock, a mouse cursor, panic screens |
| 5 | Memory management | `new`/`free` work, paging is on, and a page fault shows a clean error |
| 6 | Storage and file system | `cat readme.txt` reads a file from the disk image |
| 7 | Processes and multitasking | Two programs run at once; a crash in one doesn't take down the kernel |
| 8 | Shell and userland | An interactive shell that runs programs from disk |

Phases 0 and 1 are the most important. Everything after them is much easier with a real
compiler and an automated test loop.

---

## Phase 0: Foundations

Goal: one command to build and run, and a way to test automatically.

1. **Repo hygiene.** Add a `.gitignore` for `bin/`, `obj/` and `.vs/`, and remove those
   from git (`git rm -r --cached`). Remove the unused root `package.json`.
2. **A real CLI.** Turn `ConvertFile` into a `phi` command:
   - `phi build file.phi -o os.img` compiles, runs nasm, and builds the disk image
   - `phi run file.phi` builds and then starts QEMU
   - `phi test` runs the test suite

   The CLI calls `nasm` and `qemu-system-i386` itself, which replaces the `.bat` files.
3. **Disk image instead of a floppy.** Boot with `-drive file=os.img,format=raw` (an IDE
   hard disk). Phase 6 needs this anyway.
4. **Serial debug output.** Add a built-in `debug 'text';` that writes to COM1 (port
   `0x3F8`). Run QEMU with `-serial stdio`. This is the most useful debugging tool in OS
   development: the screen can be broken and you still see output.
5. **Automated QEMU tests.**
   - Add QEMU's `isa-debug-exit` device (`-device isa-debug-exit,iobase=0xf4,iosize=0x04`),
     so PHI code can end the VM with a pass/fail exit code.
   - Each test is a `.phi` file plus an `.expected` file containing the serial output.
     `phi test` boots each one headless (`-display none`), with a timeout, and compares
     the output.
6. **Debugger support.** Document `phi run --debug`, which starts QEMU with `-s -S` so
   you can attach `gdb` and step through the generated assembly. Also document
   `-d int,cpu_reset` for tracking down triple faults.

**Done when:** `phi test` boots `hello.phi` headless, checks its serial output, and passes.

**Status: done (2026-10-06).**
- [x] `.gitignore`; `bin/`, `obj/` and `.vs/` untracked; root `package.json` removed
- [x] `phi build | run | test` CLI in `Compiler/Phi.Cli` (assembles each class separately and joins them into a raw disk image)
- [x] Boots as an IDE hard disk (`-drive ...,if=ide`) with `isa-debug-exit` and `-no-reboot`
- [x] `log` also writes to COM1, so every program's output is testable
- [x] Test runner: `tests/*.phi` + `.expected`
- [x] `phi run --debug`: QEMU waits for gdb on `:1234` and logs interrupts and resets
- [x] The `debug` (serial only) and `exit` statements were added in Phase 1, with the new parser
- Fixed along the way: `while` loops were missing a `ret` and fell through into the data after them.

---

## Phase 1: Compiler rebuild

Goal: replace the character-by-character parser with a standard compiler pipeline that
can grow. Do this **before** adding more features, because every feature after this
point would otherwise have to be written twice.

```
source.phi → Lexer → tokens → Parser → AST → Checker → Code generator → .asm
```

1. **Lexer.** Converts source into tokens (`Identifier`, `Number`, `String`, `Colon`,
   `Semicolon`, `LBrace`, keywords like `log`, `call`, `if`, `while`, ...). Each token
   records its line and column, so errors can say `hello.phi:12:5: expected ';'`.
2. **Parser → AST.** A recursive-descent parser that produces syntax-tree nodes:
   `ClassDecl`, `VarDecl`, `MethodDecl`, `CallStmt`, `IfStmt`, `WhileStmt`, `AsmBlock`,
   `BinaryExpr`, and so on. Use precedence climbing for math expressions; this replaces
   the current `GetEnclosingDepth` approach.
3. **Checker.** Resolves names and types, and reports undefined variables, type
   mismatches and calls to unknown built-ins.
4. **Code generator.** Walks the AST and emits assembly. Keep the backend behind an
   interface so we can have more than one:
   - `X86_16Backend`: the existing real-mode output (used for boot stage 1 and the old demos)
   - `X86_32Backend`: the main target from Phase 3 onward
5. **Built-in library as PHI and `.asm` files.** Move the routines out of C# string
   lists (`ASMx86_16BIT.cs`) into real files under `lib/`. Include a file only when the
   program uses it, which the "Fixed Includes" work already started.
6. **Compiler unit tests.** Use xUnit tests for the lexer, parser and checker, plus
   "golden file" tests that compare generated `.asm` against a known-good copy.

**Done when:** `hello.phi`, `code.phi` and `arcade.phi` compile with the new pipeline and
pass their QEMU tests, and the old `Translator.cs` is deleted.

**Status: done (2026-10-06).** The language as implemented is documented in
[docs/language.md](docs/language.md).
- [x] Lexer, parser (with error recovery), binder and x86-16 code generator in `Compiler/Phi.Compiler`;
  errors have file, line and column
- [x] Accepts the current syntax: classes, all variable forms and arrays, `log`/`ask`/`call`,
  `if`/`elif`/`else`, both `while` forms, `is`/`++`/`--`/`**`/`//`/`%%`, methods with defaults and
  `[end: value]`, events, `asm.` blocks with `{var}`
- [x] `if` and `while` compile to jumps (the old ones recursed once per loop iteration), math runs
  at run time, strings compare and copy, numbers convert to text, `x.len`
- [x] New statements from Phase 0: `debug` (serial only) and `exit` (QEMU `isa-debug-exit`)
- [x] Library moved to `lib/x86_16/*.asm`, embedded in the compiler; each file says what it provides
  and requires, and only what a program uses is included
- [x] The build assembles the kernel first and passes its size to the boot sector, which loads it with
  an LBA disk read (replaces the hard-coded 6 sectors)
- [x] 55 xUnit tests (lexer, parser, binder, errors, samples) and 23 QEMU tests, including scripted
  keyboard and mouse input (`NAME.input`) and expected compile errors (`NAME.errors`)
- [x] Old translator and `ConvertFile` deleted; samples moved to `samples/`, `arcade.phi` ported to
  the `Bootloader`/`OS` names
- Differs from the plan:
  - There is one generator class, not a backend interface. The interface will be easier to get right
    once the 32-bit backend (Phase 3) exists to compare against.
  - Behavior tests in QEMU instead of golden `.asm` files. Golden files break on every harmless change
    to the output; checking what the program does catches the bugs that matter.
  - `code.phi` is a syntax showcase with no `Bootloader` class, so the test only checks that it parses.
  - Pong (`arcade.phi`) was checked by hand (screenshots with scripted keys); it has no automated test.
- Bugs fixed in the ported library: the mouse misread movements over 127 pixels (9-bit values),
  drawing wrote past the 64 KB segment limit (it only worked because QEMU doesn't enforce it), and
  interrupt handlers didn't save registers.

---

## Phase 2: Systems-language features

Goal: give PHI what it needs to write drivers and kernel code **in PHI**, not only in
`asm.` blocks. Syntax below is a proposal in the style of `TheoryTwo.md`; adjust freely.

| Feature | Why an OS needs it | Possible syntax |
|---|---|---|
| Functions with parameters and return values | Everything | `[Add: int a, int b] ... [end: a + b]` |
| Fixed-size integer types | Hardware registers have exact sizes | `u8 u16 u32 i32` (existing `byt`/`int` become aliases) |
| Constants | Port numbers, magic values | `const u16 COM1: 0x3F8;` |
| Pointers and addresses | Video memory, page tables, buffers | `ptr<u16> vga: 0xB8000;` then `vga[i] = c;` |
| Structs | Page entries, file headers, task records | `struct Task { u32 esp; u32 id; }` |
| Arrays and indexing | Buffers, tables | `u8 buf[512];` |
| Port I/O intrinsics | Talking to devices | `out COM1, b;` / `u8 s: in 0x64;` |
| Interrupt handler functions | Keyboard, timer, faults | `[isr KeyboardHandler] ... [end]` (saves registers, sends EOI, uses `iret`) |
| Volatile memory access | Device memory must not be cached in registers | `volatile ptr<u8> ...` |
| Bitwise operators | Flags, masks | `& \| ^ ~ << >>` |
| Multiple files and imports | A kernel is many files | `use drivers.keyboard;` |
| Sections and placement | Put code and data at the right address | `@section boot`, `@org 0x7C00` |

Keep `asm.` blocks for anything PHI can't express yet. For the planned "optional memory
safety", start simple: bounds-checked arrays by default, with an `unsafe` block for raw
pointer work. Unsafe code is what most drivers need, so design it in now instead of
adding it later.

**Done when:** a VGA text-mode driver (`print`, `clear`, scrolling) is written entirely
in PHI and passes a test.

**Status: done (2026-10-06).** See [docs/language.md](docs/language.md) for the details.
- [x] Sized integers `u8 u16 u32 i8 i16 i32` (`int` = `i32`, `byt` = `u8`); math is unsigned when
  either side is `u32` or a pointer; literals above `int` range are `u32`
- [x] `const`, usable in array sizes, other constants and `asm.` blocks
- [x] Pointers: `ptr<T>`, indexing (`vga:i`), `addr x`, `addr Method`; reach anything below 1 MB
  through `fs`; a `str` passes as a `ptr<u8>`
- [x] Structs: `struct.Name { ... }`, packed, nested, with field arrays and self-pointers;
  `t.field`, `tasks:i.field`, `p:0.field`
- [x] Bitwise `& | ^ ~` and shifts `<< >>` (`<<`/`>>` no longer mean `<=`/`>=`); bitwise binds
  tighter than comparisons; chained comparisons are an error
- [x] Ports: `out`/`outw`/`outd` statements, `in`/`inw`/`ind` expressions
- [x] Interrupt handlers: `[isr Name]` plus `OS.SetInterruptHandler`, `OS.EndOfInterrupt`,
  `OS.UnmaskIrq`/`MaskIrq`, `OS.EnableInterrupts`/`DisableInterrupts`
- [x] Bounds checks on array and `str` indexes (run time, or build time for constant indexes);
  `unsafe ... ;;` turns them off
- [x] `use path.to.file;` with a standard library in `lib/phi/` (embedded in the compiler)
- [x] `lib/phi/drivers/vga_text.phi`: a VGA text driver in PHI with no `asm.` (print, numbers,
  colors, scrolling, hardware cursor), checked by reading video memory back in `tests/vga_text.phi`
- Differs from the plan:
  - `volatile` isn't needed: generated code never keeps values in registers between statements.
    It will come back when the code generator starts optimizing.
  - `@section` / `@org` placement moved to Phase 3, where the 32-bit kernel and its memory
    layout are designed.
  - Pointer arithmetic is in bytes (`p++ 2` moves two bytes), not elements as in C. It's simpler
    to reason about for hardware work; revisit if it proves error-prone.

---

## Phase 3: Boot into 32-bit protected mode

Goal: a two-stage boot that ends in a PHI kernel running in 32-bit mode.

**Stage 1: boot sector (16-bit, 512 bytes, at `0x7C00`).**
- Set up segments and the stack.
- Load the stage 2 loader and the kernel from disk (`int 0x13`, LBA extensions). The
  compiler **calculates the sector count** instead of the hard-coded 6.

**Stage 2: loader (still 16-bit, while BIOS is still available).**
- Get the memory map with `int 0x15, eax=0xE820` and save it for the kernel. This is the
  last chance to use the BIOS.
- Optionally set the video mode.
- Enable the A20 line.
- Load a GDT (flat code and data segments) and switch to protected mode (`cr0.PE = 1`).
- Far jump to the 32-bit kernel entry point and pass a `BootInfo` struct (memory map,
  boot drive, video info).

**Kernel entry (32-bit PHI).** Set up the stack and call the PHI `phi.Kernel:OS` main
method.

In PHI, `Bootloader` stays a built-in base class. Most users never write a bootloader:
they write `phi.MyOS:Kernel` and the compiler adds the standard stage 1 and stage 2. The
memory layout (load addresses, stack location) is documented in `docs/memory-map.md`.

**Done when:** QEMU shows "Hello from 32-bit PHI" written straight to VGA memory at
`0xB8000`, and the serial test passes.

**Status: done (2026-10-06).** See [docs/memory-map.md](docs/memory-map.md).
- [x] `phi.Name:Kernel` classes compile to a 32-bit protected-mode kernel at `0x10000`
  (up to 512 KB); `bits 32` code generation shares one generator with 16-bit mode
- [x] Standard stage 1 (boot sector) and stage 2 (loader) in `lib/boot/`: the kernel is loaded in
  32 KB reads, the E820 memory map and cursor go to BootInfo at `0x9000`, then fast A20, a flat GDT,
  protected mode, `esp = 0x9F000`, `ebx` = BootInfo
- [x] The build calculates the sector counts for every stage
- [x] 32-bit library `lib/x86_32/`: `log` writes to VGA memory (continuing below the BIOS text,
  scrolling, hardware cursor) and COM1; strings, numbers and bounds errors work as in 16-bit
- [x] `use boot.info;` gives typed access to BootInfo and the memory map
- [x] `phi.Name:Library` classes join whichever kind of program uses them; the VGA driver is one
- [x] Tests made only of `:Library` classes run twice, as a 16-bit program and as a 32-bit kernel:
  15 tests check both code generators against the same expected output
- [x] `samples/kernel.phi`: a 32-bit kernel that prints the memory map with the VGA driver
- Differs from the plan:
  - Users don't write a stage 1 for 32-bit kernels; a program is either a 16-bit program with its
    own boot sector or a 32-bit kernel with PHI's loader. Custom loaders can come later.
  - The kernel is loaded below 1 MB (`0x10000`) instead of at 1 MB, so the real-mode loader can
    place it directly; 512 KB is plenty until paging (Phase 5) can move things around.
  - The BIOS built-ins (keyboard, mouse, timer, drawing) aren't available in 32-bit kernels yet;
    their protected-mode versions are Phase 4.

---

## Phase 4: Interrupts and core drivers

Goal: the kernel reacts to hardware and handles CPU errors instead of rebooting.

1. **IDT.** Set up the Interrupt Descriptor Table with 256 entries.
2. **CPU exceptions (0–31).** Show a panic screen with the exception name, error code,
   `EIP` and registers, and also send it to serial. Without this, every bug is a silent
   triple fault.
3. **PIC.** Remap the 8259 PIC to interrupts 32–47 so hardware interrupts don't collide
   with CPU exceptions, and add EOI handling.
4. **Drivers.** Port the existing 16-bit work to 32-bit, now written in PHI:
   - **PIT timer:** a tick counter, `Timer.Sleep(ms)`, and uptime
   - **PS/2 keyboard:** scancode → key translation, a key buffer, `Keyboard.GetKey()`,
     `IsKeyDown` (replaces BIOS `int 0x16`)
   - **PS/2 mouse:** reuse the current driver's logic (`OS_SetupMouse`, packet parsing)
   - **VGA text console:** from Phase 2, plus a cursor
   - **Serial:** from Phase 0, now interrupt-driven
   - **CMOS RTC:** date and time
5. **Rewrite the built-ins.** `log` and `ask` now use the console and keyboard drivers
   instead of the BIOS, so PHI programs don't change.

**Done when:** you can type text and see it on screen, a clock ticks in the corner, and
a test that divides by zero shows a panic screen instead of rebooting.

---

## Phase 5: Memory management

Goal: the kernel knows what memory it has and can allocate it safely.

1. **Physical memory manager.** Read the E820 map from `BootInfo` and track free 4 KB
   frames with a bitmap. Provide `Memory.AllocFrame()` and `Memory.FreeFrame()`.
2. **Paging.** Identity-map the kernel, turn on paging (`cr3`, `cr0.PG`), and handle page
   faults (exception 14) with a clear message that includes the address from `cr2`.
3. **Kernel heap.** A simple allocator (first-fit free list, or a bump allocator first)
   with `Memory.Alloc(size)` and `Memory.Free(p)`.
4. **Language support.** `new`/`free` (or `alloc`), and dynamic strings and lists built
   on the heap. The "prep for memory allocation" work connects here.
5. **Optional safety.** In safe code, check array bounds and null pointers, and report
   them through the panic screen.

**Done when:** a test allocates and frees thousands of objects without leaking frames,
and a deliberate bad pointer shows a page-fault message.

---

## Phase 6: Storage and file system

Goal: read and write files on the QEMU disk.

1. **ATA PIO driver.** Read and write sectors on the primary IDE disk (ports
   `0x1F0`–`0x1F7`). QEMU supports this by default. Polling is fine; interrupts can come
   later.
2. **Block device layer.** A small interface (`ReadBlock`, `WriteBlock`), so file systems
   don't depend on ATA directly.
3. **File system.** Pick one:
   - **Recommended: FAT16.** It's documented, and you can inspect images from Windows or
     with `mtools`, which helps a lot with debugging.
   - Alternatively, a custom **PHIFS** (a flat directory plus a list of contiguous
     blocks). It's easier to write but has no outside tools.
4. **VFS.** `File.Open`, `Read`, `Write`, `Close`, and `Dir.List`, with mount points so
   more file systems can be added later.
5. **Image builder.** `phi build` makes the disk image: boot sector, then loader and
   kernel, then a file system partition containing everything under `rootfs/`.

**Done when:** the kernel lists the root directory and prints `readme.txt` from the disk
image at boot.

---

## Phase 7: Processes and multitasking

Goal: several programs run at once and are isolated from each other.

1. **Kernel threads.** A task struct (registers, stack, state), a context switch written
   in `asm.` or as a PHI intrinsic, and a round-robin scheduler driven by the PIT timer.
2. **User mode (ring 3).** Add user code and data segments to the GDT, set up a TSS for
   kernel stack switching, and give each process its own page directory.
3. **System calls.** Use `int 0x80` with a syscall number in `eax`. Start with `exit`,
   `write`, `read`, `open`, `close`, `sleep`, `getpid`, `spawn`, and `wait`.
4. **Program loading.** Start with flat binaries at a fixed address, then move to ELF32.
   The compiler gets a `phi build --program` mode that produces a user program instead
   of a kernel.
5. **Language support.** User programs are PHI classes with a different base, for
   example `phi.Hello:Program`. Built-ins like `log` and `ask` compile to syscalls
   instead of driver calls, so the same code runs in kernel or user space.
6. **Synchronization.** Disabling interrupts for short critical sections, then simple
   locks and blocking wait queues (for keyboard input and `sleep`).

**Done when:** two user programs print interleaved output, and a program that writes to
a bad address is killed while the kernel keeps running.

---

## Phase 8: Shell and userland

Goal: a usable little OS.

1. **Shell** (a PHI user program): a prompt, line editing, and running programs from disk.
2. **Core commands:** `ls`, `cat`, `echo`, `clear`, `mem`, `ps`, `kill`, `uptime`, `reboot`.
3. **User standard library:** strings, formatting, file I/O, and memory, all built on syscalls.
4. **Ported demos:** rebuild Pong (`arcade.phi`) as a user program that runs from the shell.

**Done when:** you boot into the shell, run `ls`, start Pong, quit it, and are back at
the prompt.

---

## Proposed project layout

```
PHI/
├─ Plan.md
├─ docs/                  # language reference, memory map, syscall table
├─ Syntax/                # language design notes (existing)
├─ Compiler/              # capital C: on Windows `compiler/` would be the same folder
│  ├─ Phi.Compiler/       # lexer, parser, AST, checker, backends
│  ├─ Phi.Cli/            # `phi build | run | test` (replaces ConvertFile)
│  └─ Phi.Compiler.Tests/
├─ lib/                   # PHI standard library, written in PHI (+ .asm where needed)
│  ├─ boot/               # stage 1, stage 2, GDT, protected-mode switch
│  ├─ cpu/                # IDT, exceptions, PIC, ports
│  ├─ drivers/            # vga, serial, keyboard, mouse, pit, rtc, ata
│  ├─ mem/                # frames, paging, heap
│  ├─ fs/                 # vfs, fat16
│  ├─ task/               # scheduler, processes, syscalls
│  └─ user/               # userland standard library
├─ kernel/                # the reference PHI OS that uses lib/
├─ rootfs/                # files copied into the disk image
├─ samples/               # hello.phi, code.phi, arcade.phi
└─ tests/                 # QEMU integration tests (.phi + .expected)
```

---

## Later

These come after the basics work:

- **Graphics:** a linear framebuffer through QEMU's Bochs VBE adapter (`-vga std`),
  then fonts, windows, and a GUI toolkit as PHI built-ins
- **64-bit long mode**, then an **ARM** backend (QEMU `virt` machine). The README's
  planned x86→ARM translator becomes a second compiler backend.
- **Networking:** RTL8139 or e1000 driver (both emulated by QEMU), then ARP, IP, UDP,
  and a small TCP
- **More hardware:** PCI enumeration, AHCI, PS/2 → USB, ACPI shutdown
- **C compatibility:** call C functions and link C object files (a README goal)
- **Self-hosting:** rewrite the PHI compiler in PHI
- **Real hardware and UEFI boot**

---

## References

- OSDev Wiki: https://wiki.osdev.org (Bare Bones, GDT, IDT, 8259 PIC, PS/2, ATA PIO, Paging)
- *Writing a Simple Operating System from Scratch*, Nick Blundell (free PDF), for the
  16-bit → 32-bit boot process
- Intel® 64 and IA-32 Architectures Software Developer's Manual, Volume 3 (system programming)
- *Crafting Interpreters*, Robert Nystrom (free online), for the lexer, parser and AST
  design in Phase 1
- xv6 (MIT), a small, readable teaching OS to compare against in Phases 5–7

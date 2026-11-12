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
  default (i440FX machine, PS/2 keyboard and mouse, VGA, PIT timer, IDE disk, COM1 serial),
  plus an RTL8139 network card and an AC'97 sound card.
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

**Status: done (2026-10-06).** Demo: `phi run samples/terminal.phi`.
- [x] IDT with 256 gates, built at startup in every 32-bit kernel (`lib/x86_32/interrupts.asm`)
- [x] Panic screen for exceptions 0-31: name, number, error code, `eip`, `cr2` for page faults,
  all registers; red screen and COM1
- [x] PIC remapped to vectors 32-47, all IRQs masked until a driver unmasks one; the shared IRQ
  handler sends end-of-interrupt
- [x] `OS.SetIrqHandler` (an ordinary method per IRQ), `OS.SetInterruptHandler` (an `[isr]` gate,
  also for software interrupts), mask/unmask, enable/disable
- [x] Drivers for 32-bit kernels behind the same built-ins as 16-bit programs, so programs don't
  change: PIT timer at 1000 Hz (`[OS.TimerEvent]` still 60 Hz; new `OS.GetTicks`, `OS.Sleep`),
  PS/2 keyboard with Shift and a key buffer (`OS.ReadKey`, `OS.KeyAvailable`), PS/2 mouse
- [x] `ask` and `Bootloader.WaitForKeyPress` work in kernels through the keyboard driver; the console
  handles backspace
- [x] CMOS clock driver written in PHI: `lib/phi/drivers/rtc.phi` (works in both kinds of program)
- [x] The keyboard, timer, mouse, `ask` and clock tests run in both modes; new tests for both panic
  paths (divide error, general protection fault with its error code), `[isr]` software interrupts,
  IRQ handlers, and `Sleep`/`GetTicks`
- Differs from the plan:
  - The timer, keyboard and mouse drivers are assembly, not PHI: they keep the built-in names
    16-bit programs already use, and `ask` needs them from library code. The clock driver shows
    a driver can be written in PHI.
  - Serial output still polls rather than using interrupts: nothing needs buffered serial yet.
  - Graphics (`DrawRectangle`) isn't available in kernels: setting the video mode needs the BIOS.
    It comes with the framebuffer work in [Later](#later).

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

**Status: done (2026-10-06).** All of it is written in PHI, in `lib/phi/memory/`.
- [x] `memory.frames`: bitmap frame allocator from the E820 map (`Alloc`, `AllocContiguous`, `Free`),
  with everything below 1 MB reserved
- [x] `memory.paging`: identity paging (4 KB pages for the first 4 MB, 4 MB pages for the rest),
  page 0 unmapped as a null guard, nothing mapped past RAM; only switching it on is an `asm.` block
- [x] `memory.heap`: first-fit heap with splitting, merging of free neighbors, zeroed allocations,
  and detection of bad or double frees
- [x] `new T`, `new T[n]` and `free p;` in the language; a kernel that uses them gets the memory
  library automatically
- [x] `memory.list`: a growable list on the heap, with checked indexes
- [x] Null pointers and addresses past RAM are page faults on the panic screen, with the address
- [x] Tests: frames (1000 allocations, contiguous runs, nothing leaked), heap (5000 objects, merging,
  zeroing, out-of-memory), paging (on, high memory works), null and bad pointers, lists
- Differs from the plan:
  - Null-pointer safety comes from the unmapped page 0 rather than a check before every pointer
    use: it costs nothing and catches the same mistakes (plus any small offset from null).
  - Dynamic strings aren't done; `memory.list` covers growable data for now. Strings that grow
    want proper value semantics, which is a bigger language change.

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

**Status: done (2026-10-06).** Demo: `phi run samples/terminal.phi` (`ls`, `cat docs/phi.txt`).
- [x] `drivers.ata`: IDE PIO driver in PHI, 28-bit LBA, read and write (with cache flush), works
  in both kinds of program
- [x] `drivers.disk`: block layer that finds the FAT partition in the partition table; sector numbers
  are relative to it and checked against its size
- [x] `fs.fat16` in PHI: mount, read files (any size, partial reads), list folders, create and replace
  files, delete, free-space count; paths with folders and 8.3 names; the FAT is kept in memory and
  both copies are written back
- [x] `phi build` adds a 16 MB FAT16 partition at sector 2048 to every 32-bit image, filled from
  `NAME.rootfs/` or `rootfs/`; names that aren't 8.3 are build errors
- [x] `log` prints a `ptr<u8>` as text
- [x] Tests: raw sectors in both modes, the file system end to end (read, nested folders, three-cluster
  files, write, replace, delete, space reclaimed), and C# tests that read the built partition back
- Differs from the plan:
  - No VFS layer or mount points yet: with one file system, a `Fat` class with paths is simpler.
    A VFS is worth adding when there is a second file system (or devices as files).
  - No long file names, and folders don't grow when they fill up.
  - FAT16 was the recommended option, so there's no PHIFS.

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

**Status: done (2026-10-06).** Demo: `phi run samples/terminal.phi`, then `run hello.bin`.
- [x] `phi.Name:Program` classes compile to flat ring-3 binaries at `0x40000000`, against a user
  library (`lib/x86_32_user/`) where output, input, timing and exit are system calls; it shares the
  pure routines with the kernel library (`inherit:`)
- [x] `.phi` files in a kernel's rootfs are compiled into `NAME.BIN` on its disk
- [x] Tasks (`lib/x86_32/tasks.asm`): GDT with user segments, TSS, per-task kernel stacks and page
  directories, round-robin scheduler on the timer, sleeping, waiting for keys and for children
- [x] System calls on `int 0x80`: exit, write, debug, read key, sleep, ticks, getpid, yield, key
  available, wait; numbers from 16 go to a PHI handler (`spawn` is one)
- [x] `proc.process` in PHI: load a program from FAT16 into its own address space, start it, wait,
  free its memory afterwards; spawning from programs, with the path checked against the caller's memory
- [x] A program that faults is stopped with a message and exit code -1; the kernel keeps going
- [x] Output from a `log` statement reaches the screen whole, so programs' lines don't mix
- [x] Tests: two programs at once (timed to prove they overlapped), exit codes, a crashing program,
  a program that spawns and waits for another, all memory returned
- Differs from the plan:
  - No kernel threads: method variables have fixed addresses, so two threads running the same
    method would overwrite each other's. The kernel instead runs one thing at a time and is not
    preempted; programs (each with private memory) are. Kernel threads need variables on a stack,
    which is a compiler change for later. (Methods have since got stack frames.)
  - No ELF: flat binaries are all the compiler produces, and they load as is.
  - No locks or wait queues as language features: with a non-preemptible kernel, disabling
    interrupts (the default inside system calls) is the critical section. Waiting for keys, time
    and children is built into the scheduler.

---

## Phase 8: Shell and userland

Goal: a usable little OS.

1. **Shell** (a PHI user program): a prompt, line editing, and running programs from disk.
2. **Core commands:** `ls`, `cat`, `echo`, `clear`, `mem`, `ps`, `kill`, `uptime`, `reboot`.
3. **User standard library:** strings, formatting, file I/O, and memory, all built on syscalls.
4. **Ported demos:** rebuild Pong (`arcade.phi`) as a user program that runs from the shell.

**Done when:** you boot into the shell, run `ls`, start Pong, quit it, and are back at
the prompt.

**Status: done (2026-10-06).** Demo: `phi run samples/os.phi`.
- [x] The shell (`samples/os.rootfs/shell.phi`) is a user program: prompt, backspace, runs `NAME.BIN`
  with the rest of the line as its command line, `&` for background programs, `help`, `exit`;
  `samples/os.phi` boots into it and restarts it if it exits
- [x] Commands as separate programs: `ls`, `cat`, `echo`, `clear`, `mem`, `ps`, `kill`, `uptime`,
  `reboot` (plus `hello` and `count`)
- [x] System calls 16-30 in `proc.process`: spawn with a command line, file read/write/delete/list/size,
  process state/name/kill, memory, clear screen, put a screen cell, key state, reboot; every pointer
  is checked against the calling program's memory
- [x] `user.text`: string helpers for command lines (words, numbers, joining, comparing)
- [x] Pong rebuilt as a user program (`pong.phi`), in text mode
- [x] `addr` gives a typed pointer (`addr buffer` of a `u8` array is a `ptr<u8>`)
- [x] Test: the whole session above, automated (`tests/kernel32_shell.phi`), plus `user.text` in both modes
- Differs from the plan:
  - Pong is text mode (80x25 cells). Graphics mode needs the BIOS, and drawing would need video
    memory mapped into the program; both belong with the framebuffer work in [Later](#later).
  - The "user standard library" had text and the system calls, but no heap for programs at
    first; `memory.program_heap` (with the `OS.Grow` system call) added one later.

## Where things stand

All eight phases are done. A PHI program can be a 16-bit boot sector, a 32-bit kernel, or a
user program; `samples/os.phi` brings the pieces together.

Since then, building a graphical OS in PHI showed where the language got in the way, and
these were added:

- [x] Negative values in lists (`int a: 1 -1;` is two values), arrays where pointers are
  expected, and `sizeof`
- [x] Calls as values (`if (IsReady: port)`), `break`, `continue` and `return`
- [x] Variables on the stack for methods: recursion, and methods that are safe to call from
  interrupt handlers
- [x] Graphics in 32-bit kernels: `drivers.vbe` (Bochs VBE, any resolution, a back buffer, a
  mouse pointer), `gfx.draw`, `gfx.font`, and `drivers.input` (keyboard and mouse on their
  interrupts, with extended keys and modifiers); `samples/sketch.phi`
- [x] `pixel` and `screenshot` in test input scripts, so graphics can be tested
- [x] Methods as values: `method<int int: int>` variables, fields and arrays, called like methods
- [x] Methods on structs, called on a struct value (`call w.Draw;`), with its fields by name
- [x] Casts (`x as ptr<T>`), and a warning when one kind of pointer silently becomes another
- [x] FAT16: folder entries as data (`Fat.Entries`), `MakeFolder`, `DeleteFolder`, `Rename`
  (and moving), and dates and times on what's written
- [x] Docs: [PHI for C programmers](docs/phi-for-c-programmers.md)
- [x] [Objects](#managed-objects): `new T name`, `at`, `takes`, `empty`, copying and comparing
  with `is`, freed by PHI; the heap is safe to use from interrupt handlers
- [x] From the assessment of PhiOS (a graphical OS written in PHI): `phi test` says when a run
  timed out or QEMU stopped by itself; a method's pointer result keeps its type (a `ptr<u8>`
  prints as text); `str` building (`s is 'Ln ' n ', Col ' c;`, `s++ ...`); a mouse event queue
  and the scroll wheel in `drivers.input`; a clip stack, alpha blending, ellipses and fitted
  or scaled text in `gfx.draw`
- [x] The rest of that assessment: `[end: p + n]` keeps the pointer's type; `(f)` calls a
  method variable or field that takes no arguments; a warning when a name binds to a
  variable of another file's class; one-line guards (`if x return y;`); `\xNN` escapes;
  `Draw.BlendRect` in assembly (about 4x faster); [Common patterns](docs/patterns.md)
- [x] Reserved words stay reserved everywhere: `at` is now a keyword in every position (the
  library's `at` variables became `pos`), keyword-as-name errors suggest other names, and
  [language.md](docs/language.md#reserved-words) lists every reserved word
- [x] From the second PhiOS assessment: fields and methods through a pointer (`p.x` for
  `p:0.x`, `ptrs:i.x`, `call p.Add;`); an error when a pointer would keep a method's `str`
  result (a temporary, so it dangled); a keyword used as a name is reported only where it's
  declared; `Draw.TextN` for part of a buffer
- [x] From the third PhiOS assessment: `.max` (the most a value can hold; `.len` is how many
  are in it, `sizeof` its bytes), `.len` of an array field through an element, a warning
  when a `str`'s `.len` is passed as a size, and a one-line `if` that assigns reported at
  that line; `phi test --contains FILE`; `Draw.PushTarget`/`PopTarget`, double clicks
  (`Input.event_clicks`), pointer shapes (`Screen.SetPointer`), time zones in `drivers.rtc`
- [x] Arrays filled as they go: `T name[atmost N]`, with `name++ value;`, a `.len` that counts
  what was added, and indexes checked against it
- [x] A heap for user programs (`memory.program_heap`, growing through `OS.Grow`), so `new`
  and objects work there too
- [x] Strings that grow: a `str` without a size grows on the heap up to 65,535 characters
  (in 32-bit kernels and programs); `str s: [N];` is still a fixed buffer of at most N
- [x] GUI apps as user programs: `gui.app` (a window for a program, drawn with `gfx.draw`,
  events in a queue), `gui.surfaces` (the kernel's side, through system calls 64 to 67 and
  `Process.more_calls`), `gui.desktop` (windows with title bars and close boxes, focus,
  dragging), and `samples/desktop.phi`
- [x] Enums (`enum.Tool { PENCIL  BRUSH }`, a type of their own) and `when x` / `is a b` /
  `else`, on numbers, enums and text
- [x] Widgets (`gui.widgets`: buttons, check boxes, text fields, lists with scroll bars)
  and resizable windows
- [x] `str` results from methods grow (they were cut at 255 characters), and `chr`: a byte
  written as its character (`text++ key as chr;`); a `str`'s characters are `chr`
- [x] `while each x in list`, over any array
- [x] Arrays that grow with no most: `int scores[];`, objects too
- [x] Struct fields that are arrays without a size (`int scores[];`, `new Shape shapes[];`):
  the struct owns them (copied, compared and freed with it), and whole arrays copy into
  them; `str names[];` too, each text growing. Test: `text_array_fields`
- [x] Gaps filled (2026-10-30): methods called through a method value or an interface return
  a `str` or a new object (`method<int: str>`, `method<: Point>`); `list++ (Make: 1);` takes a
  call's new object straight into an array of objects (and a plain struct array copies an
  object's values); `x^^ n` (power, 32-bit whole numbers); interface methods take arrays
  (`[Score: int points[];]`); a whole array copies into a `str rows[]` parameter; `has`
  (the user's choice, 2026-10-31): `list has x` and `text has 'abc'`. Tests:
  `interface_array_params`, `has`, `has_errors`.
- [x] Text in parentheses (the user's idea, 2026-10-31): `('Hello ' name)` is one text built
  from the values, anywhere a value goes (`str greetings: ('hello ' name) 'index 1';`,
  `names++ ('item ' i);`, `call Show: ('Score: ' score);`). A `str` declared with several
  values that aren't all literals is now an error that says to use them. Tests:
  `text_in_parens`, `text_in_parens_errors`. Tests: `method_values_results`,
  `append_new_objects`, `power` Tests: `growing_array_fields`, `growing_array_fields_errors`
- [x] Array parameters of any length: `[Sum: int numbers[];]`, with `.len` and `.max`
- [x] Whole arrays copied (`b is a;`) and compared (`a is b`), objects and text included
- [x] Arrays of objects: `new T list[N]` and `new T list[atmost N]`, and struct fields like
  them; elements are copied, handed over, emptied and compared like any object
- [x] The elements of a `str` array grow too (each starts in a buffer of its own and moves
  to the heap when it needs more)
- [x] `i64`, `u64` and floating point numbers in method values and `when`, and `%` on
  floating point numbers
- [x] Floating point numbers: `f64`, `f32` and `f16` on the x87 FPU (each task keeps its own
  registers), literals like `2.5` and `6.02e23`, printing, and `Float.Sqrt` and friends
- [x] 64-bit integers: `i64` and `u64` (variables, fields, arrays, parameters, results,
  printing and text), with multiply, divide and conversion to text in `int64.asm`
- [x] From the fourth PhiOS assessment: a program asking `OS.IsKeyDown` no longer takes the
  keyboard from `drivers.input` (and under `gui.desktop` only the front window sees held
  keys, through `Process.key_down`); folders for programs (`OS.ListFolder`, `MakeFolder`,
  `Rename`, `DeleteFolder`, with `FileInfo` in `fs.fileinfo` for both sides); window
  requests (`App.SetTitle`, `App.OpenWindow`, `App.Focused`); a lone `f64` or `u64` assigned
  to a `str`; arrays passed to the built-ins' buffers; `SetView` stays inside a `PushClip`;
  F-key constants in `Events`; input scripts send long mouse moves in steps, and wait for a
  script still running once the output is right; an error in shared code reported once
- [x] From the fifth PhiOS assessment: a `str` array passed where text goes is an error, and a
  `str` declaration of several values that aren't all literals warns (it's an array, not
  text being built); `p + n` stays text where text is printed or built; `OS.Arguments` is a
  `ptr<u8>`; `[end: 'literal']` in a method whose returns are text pointers is a `ptr<u8>`
  method; `list++ value;` copies into an array of plain structs; hints for an `asm.` block
  inside a class and for a string that ends with a line break written into it; `OS.Idle`
  (a kernel's main loop rests until something happens); `App.SetResizable`; `reboot` in
  input scripts (restart on the same disk)
- [x] From PhiOS's MATURITY.md and a read of its kernel (2026-11-04): a `u32` going to an `int`
  parameter is an error, unless it's a constant that fits or known to (`x & 0xFF`, `x >> 1`,
  `x / 2`, `x % 100`), which caught a real hole in PhiOS's clipboard call, and in the library
  only places that check first, so those say `as int`; a `ptr<chr>` is text as a `ptr<u8>` is,
  and a moved one is too wherever it goes into a `str` (`s is p + 1` and `s is addr t:1` wrote
  the address, `s++ p + 1` the text); an early `return` of a float or a 64-bit number works;
  `'can''t'` is an error; `Process.CopyBytesFromUser` and `CopyBytesToUser`; `phi check` and
  `phi lsp` find a module's uses from the folder above it when it isn't in a repository, or
  when nothing uses it yet. Not done from that list: `const f64` (a class variable for now),
  and `FatImage.cs` stamping files in local time where the kernel uses UTC
- [x] A number that would come out as a different number is an error when it runs
  (2026-11-04), `ErrorKind.CONVERT`, that `if call ... throws` can catch: a negative number
  going to a `u32`, or a `u32` of 2 billion or more going to an `int`, as a parameter, a
  variable, a field, an element, a result or a new array's size. `as` keeps the same bits and
  checks nothing, and a constant that can't fit is an error when it's built. This replaced
  compile errors on arguments alone (the `u32` to `int` one above, and one the other way,
  briefly): those pushed people to write `as`, after which a `-1` went through silently, and a
  variable got round them. Of the 243 places it checks, 54 hold raw bits or differences meant
  to wrap and say `as` (bytes put together, a syscall's -1, an IP address), and methods that
  ended in a `u32` but returned -1 became `int` methods (`Tcp.Write`). A missed one only fails
  when its top bit is set, so a bad value in a place the tests don't reach shows up later, and
  `numbers_top_bit_program` and `kernel32_numbers_top_bit` send such values through the library's
  calls: that found `Draw.GetPixel`, `Net.Address`, `Tcp.Address` and `Block.Sectors` giving raw
  bits as an `int`, which stopped a caller putting them in a `u32` (192.168.x.x, say), and they
  give a `u32` now. Not
  covered: arithmetic (`x-- 4` wraps), a value from an `OS.` call's own conversion, and 64-bit
  numbers, which already need `as`. No conversion rule sees a subtraction below 0 either, so
  `Mem.Copy`, `Move`, `Fill` and `Fill32` also stop with `ErrorKind.COUNT` on a count of 2
  billion or more, checked at the call so that the error names its line (on 64-bit programs
  too, so a bigger copy there is several calls); not through a method value. Also fixed: the
  `Wav.Parse` calls in `audio/sound.phi` and `samples/os.rootfs/play.phi`, which passed on a
  failed read's -1. `OS.FileSize` on a file of 2 GB or more, which an `int` can't hold, is an
  error on Linux and Windows (it kept the low half, so 5 GB said 1 GB; test
  `big_file_sizes_program`). There's no way yet to ask a file's real size past that, and
  `OS.ReadFileAt`'s offset stops at 2 GB too.
  Tests `numbers_checked`, `kernel32_numbers_checked`, `u32_args_errors`, `mem_counts`,
  `kernel32_mem_counts`.
- [x] Names in a class's own code are scoped by block, as they are in a method
  (2026-11-01): a name declared again in a separate block there is a variable of its own.
  This replaced three special cases for class code (`is Player p`, `while each` over the
  same kind of array, and `throws err`), which only let a name be shared when both
  declarations matched; two loops still share their counter, by the rule methods use.
  Tests `block_scope`, `block_scope_errors`.

- [x] **File streaming, in PHI (2026-11-04).** Reading a big file a piece at a time, with a
  file kept open and positions past 4 GB, the same calls on every system (`user.stream` in a
  program, `fs.stream` in a kernel):

  ```phi
  use user.stream;
  int s: (Stream.Open: 'world.pak');          # -1 if it can't be opened
  int got: (Stream.Read: s addr buffer 65536); # the bytes, 0 at the end, -1 if it failed
  call Stream.Seek: s 5368709120;             # false past the end; u64 positions
  u64 where: (Stream.Position: s);
  u64 size: (Stream.Size: s);
  call Stream.Close: s;
  ```

  1. **A `sys.` call carries a whole `u64`** in 64-bit code, and declaring one on a 32-bit
     system is an error (it was cut to 32 bits, with nothing said). Test `sys_calls_program`.
     Also: `phi check` crashed on one system's module that nothing uses yet.
  2. **Linux and Windows, 32 and 64 bits.** `Stream` over `open`, `read`, `lseek` / `_llseek`,
     `CreateFileW`, `ReadFile` and `SetFilePointer`, all in PHI. A stream is a slot, as a
     socket is: a number that isn't one is refused. Test `stream_program` reads marks written
     either side of 2 GB and 4 GB and at 5 GB in sparse files, so a seek is seen to land.
  3. **PHI OS: a kernel's and a program's.** `Fat` has a saved place in a file (`FatPlace`), so
     reading on doesn't look the path up or walk the chain again, and gives up its lock
     between clusters. A program's streams are its own, closed when it ends, answered without
     the kernel lock (`fs.stream_calls`). Freeing clusters counts in `Fat.frees`, and a place
     that sees a new count looks the file up again, so a file rewritten while it's open reads
     as it is now, and a deleted one gives -1, not what's in its clusters since. FAT16 still
     stops at 2 GB. Test `kernel32_stream`.
  4. **Reading ahead on a thread** (`fs.readahead`), so a game takes what's ready without
     waiting for the disk, in a program or a kernel. Tests `readahead_program`,
     `kernel32_readahead`.

  Not in this: writing through a stream, overlapped I/O or `io_uring` (threads first), a file
  system past FAT16, and `OS.FileSize` and `OS.ReadFileAt`, which still stop at 2 GB (a program
  wanting more uses a stream). Not measured: how fast any of it is under a real game's load.
- [x] **Streams on URLs, and web requests in any program (2026-11-05).** Before this, only a kernel
  could fetch a URL (`Http.Get`, HTTP/1.0, into one buffer), and a program couldn't even turn a name
  into an address. Now:
  1. `Dns` in programs on every system, with `OS.NetNameServer` (kernel, `/etc/resolv.conf`,
     `GetNetworkParams`) and the hosts file; `DnsMessage` is shared with the kernel's. Tests
     `dns_program`, `net_dns`.
  2. `net.web`: HTTP/1.1 and HTTPS, on `Tcp`, `Secure` and `Dns`, so the same code in a program and
     a kernel. Chunks, redirects, `POST` and headers, status codes. Answers are read as they come.
  3. `net.webstream`: `Stream.Open` on a URL, `Seek` by a range request, and so `fs.readahead` on
     one. It's a hook, so a program that reads only files doesn't carry TLS and its roots.
     Which roots to trust is the project's to decide, not PHI's (the user's choice, 2026-11-05):
     PHI keeps `X509.Trust` and the few in `crypto.roots`, and won't ship a full list like
     Mozilla's. PhiOS's browser brings its own (`WEB/ROOTS.BIN`).
  4. Tests `web_program` (four hosts, a server on a thread, http and https) and `net_web` (PHI OS, a
     kernel and a program, against .NET's TLS through `WebServer`, which now has ranges, chunks and
     redirects). None of this has met a real internet server yet: the tests have no internet.
  Found on the way: `Tls13.Received` left `s.room` as it was, so what arrived could overflow a
  session's buffer (`tls_memory`), a heap overflow the other end could cause. `crypto.*` keeps its
  state in shared variables, so `Secure` holds a lock around it and TLS is safe from several threads,
  one at a time; a kernel or program that calls `Tls13` directly from several threads isn't. `when` on
  text and `f is 0` on a method value didn't work in 64-bit programs (`when_text_program`).
  Not yet: HTTP/2, IPv6, uploading a piece at a time, cookies, keeping a connection open between
  requests, and `Http`, which could now be a thin layer on `net.web`.
- [x] From PhiOS's MATURITY.md again (2026-11-05), the core list:
  - `FlushMouse` keeps what the events it forgets did: a release forgotten left the button held,
    so the next click was lost (test `kernel32_mouse_flush`).
  - `phi build` passes on warnings from a rootfs's programs, each once. They were dropped, so
    a kernel built clean while `phi check` found 28 in its programs.
  - `chr` is a result type: `[end: 'N']` and `[end: name:0]` give a character, if each `return`
    is a character too (test `chr`). `[end: c]` was a number, so `log (F)` printed `113`, and
    `[end: 'N']` a `str`, so `chr c: (Letter);` failed. With any other return it's as before,
    since `return -1;` beside `[end: name:i]` needs -1 to stay -1. A one-character `[end: '-']`
    beside a return of a `ptr<u8>` is a `ptr<u8>` method now, as a longer one was. A `chr` going
    to a pointer is an error without `as`, as it would only be found when it's used.
  - A string with a character beyond ASCII is an error, not silently wrong bytes (`'é'`
    was 233, `'日'` was `?`); comments can hold any, and `\xNN` is any byte.
  - `phi check` and `phi lsp`'s false errors in a project of several files (29 of PhiOS's 42
    files had some, and `tests/support/web_test_server.phi` did; none now): a file
    with a `:Program` or `:Kernel` class can be a module, so one with errors by itself is tried
    in what uses it; a program in a rootfs's subfolder finds its uses next to the kernel, in a
    build too; a program can have a module through another module; a project with no `.git` is
    looked for up to 3 folders up; a file that doesn't parse doesn't also say it can't find its
    uses.
  - Stale docs: Windows' file calls from several threads were already fixed in H6 (only `ask`
    from several threads is still not done), and `language.md`, the README and `crypto.roots`
    said what was true long ago.

The parts most worth doing next, roughly in order:

1. **More for windows:** a program opening more than one window, menus, and dialog boxes
   (open, save, a message), and a multi-line `TextArea`. PhiOS draws its dialogs inside the
   app's own window (`shared/dialog.phi`) and wrote Notepad's editor itself for want of
   these. Done after the sixth PhiOS assessment: selection in `TextField`, Tab between
   fields (`FocusChain`) and `GuiEvent.pressed` for right-button presses. Done 2026-11-04:
   `TextArea` in `gui.widgets` (tests `textarea`, `kernel32_text_area`): selection, the wheel,
   growing; it doesn't wrap, has no undo, and its scroll bar shows but can't be dragged yet.
   Done 2026-11-05: menus and dialogs, drawn by PHI so they're the same everywhere (the user's
   choice, over each system's own): `gui.menu`'s `MenuBar` (test `menu`), and `gui.dialogs`'
   `Message`, `Ask`, `Open` and `Save`, on `MessageBox` and `FilePicker` (tests `dialogs_program`
   and `dialogs_window_program`, on all four hosts), with `App.PutBack` for what happens while one's
   up. `OS.ListFolder` on Linux and Windows came with them, and `FileInfo`'s name is 255
   characters rather than 8.3 (test `list_folder_program`). `samples/desktop.rootfs/editor.phi` is
   all of it, run on PHI OS's desktop with real keys and clicks (`kernel32_editor`) and looked at
   in screenshots there; on Linux and Windows the dialogs have only run hidden. Not yet: a
   clipboard shared with other programs, right-click menus, and a menu's submenus; a file dialog
   lists 512 entries at most.
   Done 2026-11-05 too, which finishes this item: several windows in a program, each with a number,
   as a stream or a socket has (`Windows` in `gui.app`), and `App`'s calls staying on window 0,
   so existing programs don't change. An event says which window it's for. Underneath are three new
   calls, `OS.WindowNew`, `OS.WindowSelect` and `OS.WindowClose`, with the calls a window already had
   acting on whichever is selected. On PHI OS each is a surface of its own to the window manager;
   on Windows and Linux the variables each already had are swapped when another is selected, so
   their drawing and events code stayed as it was. Tests `windows_several_program` (all four hosts,
   hidden) and `kernel32_windows_several` (the desktop, real keys and clicks, and looked at in
   screenshots). Not yet run in visible windows on Windows or Linux. Seven windows at most, as
   well as App's, and no window owned by another, so a tool window can go behind its main one.
2. **Text everywhere: done (2026-10-29),** after the sixth and seventh PhiOS assessments. Text
   works the same in variables, struct fields, parameters and array elements:
   `str name: [24];` (also `[atmost 24]`, `chr name[24]`) in a struct; `new str bio;` for text
   that grows in a struct (which makes it managed); `str target;` parameters that are the
   caller's text; `str names[atmost 64];`, `str slots[3];`, `str lines[8]: [40];` and
   `str rows[]` parameters; a check that a pointer into a method's own text isn't handed
   back. Tests `text_fields`, `text_params`, `text_arrays`, `text_new_fields`. Since
   2026-10-30: `str names[];`, a `str` array without a most, laid out as a `str rows[]`
   parameter sees one (each element's text address, then each one's room; a new one is
   the empty text in the program's data), so it's passed to `str rows[]` parameters and
   compared like any `str` array; it grows through `phi_text_array_grow`, and shrinking
   frees the dropped texts. Comparing `str` array parameters whole works now too. Tests
   `text_growing_arrays`, `text_growing_arrays_errors`. Copying into one whole works too
   (`copy is names;`, from any `str` array), and copying a `str` array into an
   `[atmost N]` one now sets its `.len` (it kept the old count before). Method values
   with a caller's-text parameter: `method<str into: int>` (the user's choice; `into` is
   reserved now, and the library's `into` variables became `target`); tests
   `method_values_into`, `method_values_into_errors`.
3. **Interfaces**: done as `interface.Name` with `struct.X has Name` (see S7). Using them
   for PhiOS's tables of handlers is the next step there.
4. Then the [Later](#later) list. (C compatibility and self-hosting are on hold until
   everything else is done.)

---

## Managed objects

**Status: done (2026-10-20).** Steps 1–5 below; tests `objects_*`, docs in [language.md](docs/language.md#objects)
and tutorial 05. Additions made while building it: `new T name: empty;` starts with no
object; whole-struct `is` (copy and compare) also works for plain structs in 32-bit code; a
struct parameter borrows the caller's object, struct or `ptr<T>`; `free` on a `ptr<T>` to a
struct with `new` fields frees what it owns. Not done yet: step 6, and a runtime check that
`takes` through a borrowed parameter can't make an object own itself (only the
compile-time check exists).

**Goal:** a program creates structs with `new` and never frees them. Each object has exactly
one owner, and PHI frees it when the owner goes away. `ptr<T>`, the `new T` expression and
`free` stay as they are, for code that wants to manage memory itself.

### The rules

```phi
struct.Point
{
    i16 x;
    i16 y;
}

struct.Node
{
    u32 value;
    Point where;           # inline: part of the Node's own memory, no new
    new Node next;         # a separate object this Node owns; starts empty
}

new Point a: 1 2;          # allocated, fields filled in order; a owns it
new Point b;               # allocated and zeroed
a is b;                    # copies b's values into a's memory: a and b never share
new Point cell at 0xB8000; # no allocation, never freed: a view of memory you control
```

1. **`new` is needed only where memory is allocated:** a struct variable, or a field that
   holds a separate object. Number types (`u32`, `i16`, ...), inline struct fields
   (`Point where;`) and arrays of structs (`Task tasks[8];`) stay as they are, since their
   memory is part of something else.
2. **The owner gets zeroed memory.** `new Point b;` is always a real, zeroed object. A `new`
   field starts empty until it's assigned.
3. **`is` copies values, never addresses.** Two variables never point at the same memory,
   so there's nothing to count and cycles can't happen. Copying an object with `new` fields
   copies those objects too (a deep copy). An empty field copies as empty.
4. **`takes` hands memory over without copying:** `node.next takes head;` frees whatever
   `node.next` held, gives it `head`'s memory, and leaves `head` empty. There's still only
   one owner, and building a list costs one pointer move per item instead of a copy of the
   whole list.
5. **`empty`** tests a `new` variable or field: `while walk.next is not empty`. Using an empty
   object's fields stops the kernel on the panic screen (it's a null pointer underneath).
6. **`at`** declares a variable over memory that already exists (device memory, a table the
   BIOS left). It's never allocated or freed, and `is` copies values into that memory.
   `at` is a keyword only in this position, because lib code already uses it as a name.
7. **Methods borrow.** Passing a `new` variable to a method passes its address; the method
   uses it for the call but doesn't own it. A method that returns one hands it over to the
   caller (no copy).
8. **`addr` of a managed object** gives a plain `ptr<T>`, valid only while the owner is alive.
   This is how code walks a list without copying it: `ptr<Node> walk: addr head;`.
9. **`is` in a condition compares values**, to match `is` as a copy: `if a is b` is true when
   the two objects hold the same values.
   - The same address is equal straight away (possible through `at` or `addr`).
   - Otherwise the blocks are compared byte for byte. Structs have no padding, so a struct
     without `new` fields is a single memory compare.
   - A `new` field holds an address, so instead of its bytes PHI compares the objects it
     points to, by these same rules: both empty is equal, one empty isn't. Chains are
     followed with a loop, like freeing.
   - A `ptr<T>` field compares as an address.

### When PHI frees

| Owner | Freed |
|---|---|
| A method's `new` local | when the method ends: at `[end]`, at every `return`, and at `exit` |
| A `new` field | just before the struct holding it is freed |
| A class-level `new` variable | never; it lives as long as the program |
| A `new` local declared in a loop | its memory is reused each time round (zeroed and filled again); it's allocated again only if `takes` moved it out |
| The target of `takes` | its old object is freed before it takes the new one |

`free` on a managed object is a compile error. Running out of heap stops the kernel with an
"out of memory" message and the source line.

### How it's built

- **Parser:** `new T name`, then `: values`, `at expr`, or nothing, as a declaration in
  methods, class bodies and structs; `takes` as a statement; `empty` as a value. The `new T`
  expression stays for `ptr<T>`.
- **Binder:** a managed type for these variables (an address underneath, `x.field` access
  like a struct), checks for the initial values, and errors for `free`, for `is` between a
  managed object and a `ptr<T>`, and for `at` in user programs.
- **Generated code for each struct with `new` fields:** a copy routine (deep copy), a compare
  routine and a free routine. Freeing follows `next`-style chains with a loop, not recursion, so freeing a long
  list can't overflow the kernel stack.
- **Method exits:** every way out of a method frees its managed locals. Method frames already
  exist, so this is a list of slots per method.
- **Heap:** `Heap.Alloc` and `Heap.Free` turn interrupts off while they run, so an interrupt
  handler that frees an object can't catch the heap halfway through a change.

### Order

1. **`at` declarations.** No runtime support; works in 16-bit programs too.
2. **Managed locals:** `new T name`, initial values, `is` as a copy, freeing at method exits,
   reuse in loops.
3. **Methods:** borrowing parameters and handing back return values.
4. **`new` fields:** `empty`, deep copy, the free routine, and `takes`.
5. **Docs and samples:** [language.md](docs/language.md),
   [PHI for C programmers](docs/phi-for-c-programmers.md), tutorial 05 rewritten with
   managed objects, and `ptr<T>` moved to a "managing memory yourself" section.
6. **Later:** managed arrays (`new u8 buffer[512];`). (Objects in user programs came with
   their heap.)

### Tests

Every test ends by checking `Heap.used_blocks` is 0, so a leak fails it.

- a local freed at `[end]`, at an early `return`, and inside a loop that runs 10,000 times
- `is` copies (changing one object leaves the other alone), including a deep copy of a list
- `if a is b` on objects: equal values at different addresses, one field different, one
  `new` field empty, two equal lists, and a list compared with a copy of itself
- a list built with `takes`, walked with `addr`, and freed when its head goes away
- a 10,000-node list freed without overflowing the stack (100,000 is too slow to build with
  the first-fit heap, which walks every block on each allocation)
- a method returning a new object, and one borrowing a parameter
- `at` over VGA memory, in a 16-bit program and a kernel
- compile errors: `free` on a managed object, too many initial values, `takes` from a `ptr<T>`

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
├─ samples/               # code.phi, arcade.phi, os.phi, ...
└─ tests/                 # QEMU integration tests (.phi + .expected)
```

---

## Later

These come after the basics work:

- **Graphics:** done: a linear framebuffer through QEMU's Bochs VBE adapter (`drivers.vbe`),
  drawing and the VGA font (`gfx.draw`, `gfx.font`), and the keyboard and mouse on their
  interrupts (`drivers.input`), and windows for programs with a desktop and widgets
  (`gui.*`). Still to do: other fonts (and text bigger than 8x16 that isn't blocky)
- **64-bit long mode:** done for Linux and Windows programs; kernels stay 32-bit until a
  real cloud machine needs them. Then an **ARM** backend (QEMU `virt` machine). The README's
  planned x86→ARM translator becomes a second compiler backend.
- **Networking:** done: PCI, an RTL8139 driver, ARP, IPv4, ICMP, UDP, DHCP, DNS, a TCP
  client, HTTP/1.0, and a TLS 1.3 client for https (`lib/phi/net/`, `samples/web.phi`) with
  its cryptography (`lib/phi/crypto/`: SHA-256, HMAC, HKDF, AES-128-GCM, ChaCha20-Poly1305,
  X25519, random numbers), the server's certificate checked (`crypto.x509`), and listening for
  connections. Still to do: the e1000 card, and interrupts instead of polling
- **Sound:** done: an AC'97 driver (`drivers.ac97`), a synthesizer for songs written as text
  in the style of MML (`audio.synth`: 4 voices, square, pulse, triangle, sawtooth, sine and
  noise), WAV files with resampling (`audio.wav`), `audio.sound` to play them from a kernel,
  system calls and `play`/`volume` commands in PHI OS, `samples/music.phi`, and tests that
  check QEMU's recording of the card. Still to do: interrupts instead of polling (so music
  keeps going without `Sound.Pump`), recording, Intel HD Audio, and compressed formats
- **More hardware:** AHCI, PS/2 → USB, ACPI shutdown (PCI enumeration is done: `drivers.pci`)
- **C compatibility:** call C functions and link C object files (a README goal). **On hold**
  until every other feature in this plan is implemented.
- **Self-hosting:** rewrite the PHI compiler in PHI. **On hold** until every other feature in
  this plan is implemented.
- **Real hardware and UEFI boot**
- **Running on Linux, Windows and macOS** as ordinary programs: 32-bit console programs on
  Linux and Windows are done (H1-H3); see [Running on other systems](#running-on-other-systems)

---

## Future scenarios for development

The long-term goal is a professional language that a AAA game studio could choose. The
phases above built the basics. The scenarios here are large, real projects that show
what is still missing on the way there. Each one lists what PHI already has for it, what
it lacks, and milestones that end in a demo, like the phases do.

Each gap is marked with the kind of work it needs:

- **Library:** written in PHI, in `lib/phi/`
- **Compiler:** code generation or the build, with no change to the language
- **Language:** new syntax or rules, documented in [docs/language.md](docs/language.md)
- **Platform:** booting, hardware, and where PHI runs

A gap that shows up in more than one scenario is worth more than one that doesn't. Those
are collected under [Gaps shared by the scenarios](#gaps-shared-by-the-scenarios).

### Scenario 1: an operating system that is an MMORPG server

**The idea.** A kernel with one job: running the server for a massively multiplayer game.
It has no shell or desktop. It boots, starts the network, and runs the game's tick loop:
accepting players, simulating the world and saving their progress.

**What PHI has for it today**

- A single-purpose kernel is what PHI already builds: `phi.Server:Kernel` with a 1000 Hz
  timer for a fixed tick, and no processes needed.
- Packed structs, `Net.Get16`/`Put32` and pointer math in bytes map straight onto a wire
  protocol.
- Bounds checks, the unmapped page 0 and single-owner objects mean a malformed packet
  can't corrupt memory.
- `f32`/`f64` for positions and physics, `i64`/`u64` for currency and IDs.
- SHA-256, HMAC, ChaCha20-Poly1305, AES-GCM, X25519 and `crypto.random` (RDRAND under
  QEMU) for login tokens and encrypted traffic.
- QEMU tests with input scripts, which could drive simulated clients.

**What's missing**

| Area | Today | The server needs | Kind |
|---|---|---|---|
| TCP | Client only; at most 4 connections, each with an 8 KB buffer in the kernel's 512 KB; `Write` waits for each segment to be acknowledged; segments out of order are dropped | Listen and accept; thousands of connections with buffers on the heap; a sliding window; keeping segments that arrive out of order | Library |
| UDP | One port listened on at a time, and one datagram held: anything that arrives before it's read is dropped | Sockets with a queue each; many clients on one port | Library |
| Network card | RTL8139 only (100 Mbit), polled, an 8 KB receive ring | Interrupts; bigger rings; virtio-net (what cloud machines offer) and e1000 | Library, Platform |
| Network in programs | Kernels only | Game logic in user programs, so a crash in one zone doesn't stop the others | Library (system calls) |
| Surviving errors | A bad index, a null pointer or running out of memory stops the whole machine on the panic screen | A bad packet or a broken quest stops only the work it was part of (one request, one zone), and the server goes on | Language, or Library through programs |
| Several CPUs | One core; the kernel isn't preemptive; no kernel threads | Zones, physics and the network on separate cores | Language (atomics, locks), Compiler, Platform (APIC, starting the other cores) |
| Memory | 32-bit; the heap is a quarter of RAM (at most half); first-fit `Heap.Alloc` walks every block (100,000 objects was too slow to build in the objects tests); a fixed array holds at most 16384 elements | Many GB; pools for objects made and freed every tick | Library (pools, a faster heap), Platform (64-bit) |
| Saving | FAT16 on a 16 MB partition, 8.3 names, folders that don't grow, no journal, a polled disk | Player data that survives a crash or a power cut | Library (an append-only log, a journal, or a database reached over the network) |
| Security | TLS client only, certificates not checked, no password hashing | Server-side TLS for logins; PBKDF2 or Argon2 | Library |
| Speed | Every value goes back to memory after each statement; no optimizer; no SIMD | Interest management and physics for thousands of entities each tick | Compiler |
| Building blocks | No generics, no hash map, no interfaces; errors are `-1` or `false` | Maps from player and entity IDs; kinds of entity that share behavior; errors that can't be ignored | Library (a map), Language |
| Measuring | No profiler; nothing times a test | Tick time and memory per player, watched as the code changes | Compiler, Platform (`phi test`) |
| Running it | QEMU only | A host or cloud machine | Platform (works now as QEMU with KVM on a Linux host, with ports forwarded) |

**Benchmarks for the server's data**

Each kind of data a game server handles has a benchmark (`phi test --bench`, see
[tests/README.md](tests/README.md#benchmarks)), so every phase shows what it changed for
the server. Each benchmark prints what it worked out, checked against a Python model of
the same work, so a change that makes it faster by breaking it shows too. Counts are
instructions (2026-10-29, after S3); QEMU ran about 2 billion a second on the computer
they were measured on, so a tick at 20 a second has room for about 100 million.

| Data | Benchmark | What it measures | Instructions each |
|---|---|---|---|
| World state | `bench_world` | moving an entity one tick (`f32`) | 158 |
| | | putting an entity in a grid of cells | 91 |
| | | finding the entities near one (its cell and the 8 around it) | 2,064 |
| | | a distance with a square root | 61 |
| Network messages | `bench_messages` | packing / unpacking a 10-byte move | 123 / 119 |
| | | a snapshot of 1,000 entities, 125 of them moved | 61,000 in all |
| | | finding a packet's session by address and port (`data.map`) | 176 |
| | | signing a 64-byte packet with HMAC-SHA256 | 84,000 |
| Text | `bench_text` | hashing a name and adding it to a map / finding it again | 667 / 330 |
| | | building a chat line with `str` | 922 |
| | | looking for 5 words in a chat line, ignoring case | 13,400 |
| | | comparing two names ignoring case | 483 |
| Player records | `bench_players` | picking up an item into a 40-slot bag | 1,092 |
| | | sorting 1,000 scores for a leaderboard | 491,000 in all |
| | | saving / loading a 36-byte record, with a CRC-32 | 1,590 |
| NPC logic | `bench_ai` | an A* path across a 64x64 map | 289,000 |
| | | a timed event into and out of a heap | 1,049 |
| | | a loot roll on a table of chances | 101 |
| Memory | `bench_memory` | heap / pool / map (see S2) | 690 / 144 / 116 |
| Security | `bench_crypto` | PBKDF2 (1,000 rounds), X25519 (a TLS handshake's key) | 39 M / 109 M |
| The tick | `mmo/bench_tick` | one tick of the mmo world with 500 players: moves, creatures, the grid, a STATE each (S7) | 5.4 M |

What stands out:
- **Signing every packet with HMAC-SHA256 costs too much:** 1,000 players sending 20
  packets a second would take 1.7 billion instructions a second for that alone. A server
  wants a cheaper per-packet check (Poly1305 with a session key, from `crypto.poly1305`),
  and S6.
- **Calling a small method is a large part of many costs.** The chat filter calls
  `Text.Lower` for every character it compares, and that's most of its 13,400. Inlining
  small methods (S6) is aimed at this.
- **Finding nearby entities is the biggest cost of a tick:** about 4 million for 2,000
  entities. That fits a tick now, but grows with how crowded the world is, so it's the
  one to watch as worlds get bigger.
- **Pathfinding should be rationed:** about 300 A* searches fit in a tick's budget, so NPCs
  share paths or search less often, as on any server.
- Disk writes aren't measured: the disk finishes in real time on the host, so the counts
  change from run to run. S4, which builds saving, measures them its own way.

**Milestones**

These are reached through [Development phases](#development-phases) S1–S11; each
milestone names its phases.

1. **A demo server on one machine** (Phases S1–S4; done 2026-10-29, `samples/mmo/`). UDP sockets with queues, an interrupt-driven network
   card, a pool allocator for entities, a hash map, and an append-only save file. A small
   client (a PHI kernel in a second QEMU, or a program on the host) moves a character
   around.
   *Done when:* a test boots the server and several scripted clients, each client sees the
   others move, and after a restart the server has their positions back.
2. **Hundreds of players** (Phases S5–S7; done 2026-11-01: 300 players hold 20 ticks a second). A TCP server (listen, accept, a sliding window), register
   allocation in the code generator, and interfaces: this scenario is the kind of code
   item 3 of "Where things stand" was waiting for before choosing a design.
   *Done when:* a load test with hundreds of simulated clients holds a fixed tick rate,
   and `phi test` reports the tick time so a slowdown fails it.
3. **Ready to run for real** (Phases S8–S11). Errors contained to one request or zone, several cores,
   64-bit mode, virtio-net, and server-side TLS with certificates.
   *Done when:* an hour of malformed packets from a fuzzer doesn't stop the server, and it
   boots on a cloud machine without QEMU.

### Gaps shared by the scenarios

With one scenario this list is short. It grows as scenarios are added, and what's on it
are the strongest candidates for the next phase of the roadmap.

- **An optimizing code generator**, register allocation first, then SIMD. Speed is the
  first thing a studio asks about.
- **Several cores:** threads, atomics and locks in the language.
- **Containing errors** without stopping the machine.
- **Generic containers and interfaces.**
- **64-bit mode** and more than 4 GB of memory.
- **Measuring:** timing in `phi test`, and a profiler.
- **C compatibility** stays on hold (see [Later](#later)), but every AAA scenario will meet
  it: engines, middleware and platform SDKs are written in C and C++.

### Development phases

These phases reach the scenarios' milestones. Each is marked with what it serves:
**shared** phases close a gap every scenario has, so they carry over to scenarios written
later; the others are for one scenario. The same ground rules as the main phases apply:
every phase ends with a demo that boots in QEMU and an automated test that checks it, and
everything that works today (the samples, PHI OS, PhiOS and every test) keeps working.

| # | Phase | Serves | Kind | Demo at the end |
|---|---|---|---|---|
| S1 | Measuring | Shared | Platform, Compiler | `phi profile` names the slowest lines; a benchmark that gets slower fails |
| S2 | Building blocks for servers | Shared | Library | 100,000 objects from a pool and a map, fast |
| S3 | UDP for servers | Scenario 1 | Library, Platform | Several kernels on one virtual network trade datagrams |
| S4 | Saving, and the first server | Scenario 1 | Library | Milestone 1: clients see each other move; positions survive a restart |
| S5 | TCP for servers | Scenario 1 | Library, Platform | Hundreds of connections from a load tester |
| S6 | An optimizing code generator | Shared | Compiler, Language | `Draw.BlendRect` back in PHI, as fast as the assembly |
| S7 | Interfaces | Shared | Language | Milestone 2: entity kinds checked as one interface; a fixed tick under load |
| S8 | Containing errors | Shared | Language, Library | An hour of fuzzing doesn't stop the server |
| S9 | Several cores | Shared | Language, Compiler, Platform | Zones on 4 cores, faster than on 1 |
| S10 | 64-bit | Shared | Compiler, Platform | The test suite passes as 64-bit kernels |
| S11 | Running for real | Scenario 1, shared | Library, Platform | Milestone 3: boots on a cloud machine with TLS logins |

Measuring comes first, because every later phase (S6 most of all) needs numbers to show it
helped. Interfaces (S7) come after the server code of S2–S5 exists, since that code is
what the design is chosen from.

#### Phase S1: Measuring

Goal: know how fast PHI code is, and notice when it gets slower.

1. **Benchmarks in `phi test`.** A test prints `bench NAME VALUE` lines on COM1, and
   `phi test --bench` compares them with a saved baseline (`NAME.bench`), failing when one
   is more than a set amount worse. Run benchmarks under QEMU's `-icount`, so the count
   of instructions, not the host's speed, decides the time and results repeat.
2. **A tick timer for programs:** `OS.GetMicros` (or the timestamp counter, `rdtsc`) for
   timing short pieces of code.
3. **A sampling profiler.** `phi profile file.phi` runs the program with the timer
   interrupt recording `eip`, then maps the addresses back to PHI source lines (the
   generated `.asm` already shows each line above its code; NASM's listing gives the
   addresses).
4. **First benchmarks:** heap allocation, `Draw.FillRect`/`BlendRect`, SHA-256, AES-GCM,
   X25519, the synthesizer, and a TCP download from `phi serve`.

**Done when:** `phi profile samples/web.phi` lists the source lines that take the most
time, and making a benchmarked routine slower on purpose fails `phi test --bench`.

**Status: done (2026-10-29).** See [tests/README.md](tests/README.md#benchmarks).
- [x] `test.bench` (`lib/phi/test/bench.phi`): `Bench.Start`, `Bench.Stop: name` and
  `Bench.Count` on the timestamp counter, for kernels and programs
- [x] `phi test --bench`: runs the `bench_*` tests under `-icount shift=0,sleep=off` and
  compares each count with `NAME.bench`, failing one more than `--tolerance` (2%) over;
  `--save-bench` keeps new counts, and the first run saves them. Plain `phi test` leaves
  `bench` lines out of the comparison. The counts are exactly the same from run to run.
- [x] `phi profile`: samples the registers through a second QEMU monitor (about 1,500 a
  second), maps each address to its PHI line, method or library routine through NASM's
  listing (`-l`), and reports the busiest lines and methods, idle time (`hlt`) and time in
  user programs; with a window, or headless with `--input` or `--seconds`. Works for
  kernels and 16-bit programs. The generated `.asm` now names each method's class and file
  (`; method Fat.Mount (lib/phi/fs/fat16.phi)`), which the profiler reads.
- [x] Benchmarks: `bench_memory` (heap), `bench_crypto` (SHA-256, AES-GCM, ChaCha20,
  X25519, checked against Python's results), `bench_draw` (fill, blend, lines, circles on
  an image in memory) and `bench_synth` (one second of four voices)
- [x] xUnit tests for bench lines and baselines, register parsing, and address maps (a
  written listing and a real kernel's)
- Differs from the plan:
  - No `OS.GetMicros`: `Bench.Count` (the timestamp counter) times short pieces of code, and
    under `-icount` it's exact. Real microseconds need the counter's speed measured against
    the timer, which is worth doing when something needs wall-clock time.
  - No TCP download benchmark yet: how many times the kernel polls depends on the host
    network's timing, so the count changes from run to run. It belongs with S5, which
    rewrites TCP.
  - The profiler samples from outside (QEMU's monitor) rather than from the timer
    interrupt, so the program needs no changes and 16-bit programs work too.
- First numbers worth knowing for S2 and S6: one X25519 key is about 109 million
  instructions (optimized C needs a few hundred thousand cycles), and 92% of it is one
  method (`X25519.Mul`), three-quarters of that on one line. Allocating 2,000 heap blocks in a
  row costs 38 million instructions, because each `Alloc` walks every block before it.

#### Phase S2: Building blocks for servers

Goal: the containers and allocators a server uses on every tick, in the library.

1. **`memory.pool`:** fixed-size blocks for one struct type, with allocate and free in
   constant time, and checks for double frees like the heap has.
2. **A faster heap:** free lists by size class in front of the first-fit heap, so
   `Heap.Alloc` no longer walks every block.
3. **`data.map`:** a hash map from `u32` keys to `u32` values (an ID to an index or a
   pointer), growing on the heap. Without generics a map stores numbers; a second key
   type (text) comes when something needs it.
4. **`data.queue`:** a ring buffer of fixed-size records, for packets and events.
5. **Password hashing:** PBKDF2 on the existing HMAC-SHA-256 (`crypto.pbkdf2`).

**Done when:** a test makes and frees 100,000 objects from a pool and the faster heap, and
fills a map with 100,000 IDs and finds each one, with benchmarks from S1 recording all
of it, and `Heap.used_blocks` back to 0.

**Status: done (2026-10-29).** See [Memory](docs/language.md#memory-32-bit-kernels).
- [x] `memory.pool`: `New: size per_chunk`, `Alloc`, `Free`, `Delete`; memory from the heap
  a chunk at a time, a list of free slots, and a tag on each slot that catches a double free
  or a free into the wrong pool. 144 instructions to allocate, 65 to free.
- [x] A faster heap, in kernels and programs alike: free blocks are on 28 lists by size
  (found with `bsr`), so `Alloc` no longer walks every block, and memory is zeroed with
  `rep stosd`. Allocating with 2,000 blocks in use went from about 19,000 instructions
  to about 700, and it stays there at 100,000.
- [x] `data.map`: u32 keys to u32 values, open addressing with Fibonacci hashing, doubling
  at three-quarters full, and rebuilt at the same size when removed keys pile up; `Next`,
  `KeyAt` and `ValueAt` walk it. About 116 instructions to find a key.
- [x] `data.queue`: a ring of fixed-size records: `Push`, `Pop`, `Front`, `Drop`
- [x] `crypto.pbkdf2`: PBKDF2-HMAC-SHA256, checked against RFC 7914 and Python's `hashlib`;
  the password's HMAC start is worked out once and copied for each round
- [x] Tests `kernel32_pool`, `kernel32_map`, `kernel32_queue`, `crypto_pbkdf2`, and
  `kernel32_program_data` (all three containers in a user program); `bench_memory` makes
  and frees 100,000 objects from the heap and from a pool and fills a map with 100,000
  IDs, and `bench_crypto` times PBKDF2
- Differs from the plan:
  - The size lists replace the walk over every block rather than sitting in front of it;
    blocks are still kept in address order, so `Free` merges neighbors as before. Freeing
    costs more than it did (about 290 instructions instead of 167), since it keeps the
    lists.
  - `Pool.Alloc` and `Queue.Front` return a `u32`, as `Heap.Alloc` does, so storing the
    result in any `ptr<T>` doesn't warn about one kind of pointer becoming another.
  - A queue isn't safe between an interrupt handler and the rest of a program. S3 needs
    that for the network card, and will add it there.

#### Phase S3: UDP for servers

Goal: a kernel can serve many clients over UDP without losing datagrams it had room for.

1. **Interrupts for the RTL8139:** frames are taken from the card on its IRQ
   (`OS.SetIrqHandler`), not only when something calls `Net.Poll`.
2. **A bigger receive ring:** the card supports 8, 16, 32 or 64 KB; use 64 KB.
3. **UDP sockets:** `Udp.Open: port` gives a socket with its own queue (`data.queue`), any
   number of ports at once, `Udp.Receive` that returns at once when nothing is waiting
   (for a tick loop) and `Udp.SendTo`. `Net.Listen`/`WaitUdp` stay for old code.
4. **Several machines in one test.** `phi test` boots a server and client kernels on one
   virtual network (QEMU's `-netdev socket` or a hub), each with its own expected output,
   and passes when all of them do.

**Done when:** a test boots a server and four clients that each send 1,000 numbered
datagrams, and the server receives every one that the network delivered, counted per
client.

**Status: done (2026-10-29).** See [The network](docs/language.md#the-network-32-bit-kernels).
- [x] `drivers.rtl8139` on its interrupt: the handler moves frames from the card's ring
  into a queue of up to 256 in the driver (`frames_dropped` counts what doesn't fit), and
  `Receive` takes them from there, with interrupts off while it does. `Receive` also empties
  the ring itself, so a kernel with interrupts off still gets its frames. `Net.Poll` and
  everything above it still run outside the interrupt, so the network code didn't change.
- [x] A 32 KB receive ring (from 8 KB)
- [x] `net.udp`: `Udp.Open: port queue` (0 picks a port), `Receive` (never waits), `Wait`,
  `SendTo`, `Close`; each socket has its own `data.queue` of `UdpDatagram`s and counts what
  it `dropped`. `Net.Listen`/`WaitUdp` still work for a port with no socket (DHCP uses them).
- [x] `Net.Start: address mask router` uses that address without DHCP; the ARP table holds
  32 addresses (from 8), and an IP packet from a machine on the same network teaches the
  ARP table its Ethernet address, so a server can answer a client without asking first
- [x] `NAME.machines` in `phi test`: the test and the kernels it lists boot at once on one
  network (`VirtualNetwork`, a hub that QEMU joins with `-netdev socket`), each with a MAC
  address of its own and a `Machine` class saying which machine it is; each must match its
  own `.expected`
- [x] `net_udp`: a server and four clients each sending 1,000 numbered datagrams; the server
  gets all 4,000, in order, nothing dropped, while resting 2 ms between looks
- Differs from the plan:
  - A 32 KB ring, not 64 KB: with a 64 KB ring the card ignores the setting that keeps
    each frame in one piece, and 32 KB plus the driver's queue is plenty.
  - The clients in `net_udp` rest 1 ms after every five datagrams. At full speed four of
    them send far more than one server handles, and it dropped about 70% (counted, as it
    should); UDP has no flow control, so pacing belongs to the game's protocol.
  - The hub is in `phi test` (C#) rather than QEMU's multicast networking, which needs
    multicast to work on the host; the hub works the same on every computer.
  - Records are copied whole into and out of each queue (about 1.5 KB at each step, three
    steps per datagram), however small the datagram. A way to fill a queue's slot in place
    would save that, if S5's load tests show it matters.

#### Phase S4: Saving, and the first server

Goal: Scenario 1's first milestone.

1. **Appending to a file** (`Fat.Append`) without rewriting it, and a flush that writes the
   FAT and the folder entry, so a file is whole up to its last flush.
2. **A bigger FAT16 partition** chosen at build time (up to FAT16's 2 GB), for save files.
3. **A save log:** records appended as players change, read back in order at boot; a
   torn last record (power cut mid-write) is found by a checksum and ignored.
4. **`samples/mmo/`:** a server kernel (a world, players in a `data.map`, a fixed tick,
   UDP) and a client kernel that moves a character and draws the others with `gfx.draw`.
5. **A protocol written once:** packet structs in one file the server and client both `use`.

**Done when:** Scenario 1's milestone 1: a test boots the server and several scripted
clients, each client sees the others move, and after a restart (the input scripts'
`reboot`) the server has their positions back.

**Status: done (2026-10-29): milestone 1.** `phi run samples/mmo/server.phi`.
- [x] `Fat.Append`: adds to the end of a file, filling its last cluster and then new ones;
  writes the data, then the FAT (only if it took a new cluster), then the size, so a stop
  part way through leaves the file as it was, and frees clusters an earlier stop left past
  the end
- [x] `data.crc32` and `fs.savelog`: records of one size, each with a CRC-32; `Read` calls a
  method for each, oldest first, and stops at one cut short or damaged (`torn`); `Clear`
- [x] `samples/mmo/`: `protocol.phi` (the messages, which both sides `use`), `server.phi`
  (players in a pool and a map, a 20-a-second tick, moves saved to a log as they happen,
  and the log read back and compacted at start-up) and `client.phi` (joins, moves with the
  arrow keys, draws everyone with `gfx.draw`, and joins again if the server goes quiet)
- [x] Its test: the server and three clients; each client moves five steps, sees the
  others move, and checks the squares on its screen; the server restarts on the same disk
  and each client comes back where it was (8 s)
- [x] `phi run` of a program with `NAME.machines` boots every machine on one network, each
  in a window; `phi test` with no folder also runs the samples that have tests
  (`mmo/server`); each machine of a network test can have its own `.input` and `.contains`;
  the class each machine gets is now `Machine` (`number`, `count`, `address`, `first`)
- [x] Test `kernel32_savelog`: 300 records, a torn one, a restart, reading back, `Clear`, and
  a file that fills its cluster exactly
- Differs from the plan:
  - No bigger partition yet: a save log of players is a few KB, so 16 MB is plenty for
    now. The image builder's layout is fixed constants and keeps the whole image in
    memory; a 2 GB partition needs it to compute the layout and write the image in pieces.
  - A client that sends JOIN again before the first WELCOME comes back gets two; it now
    counts only the first (the test found it).

#### Phase S5: TCP for servers

Goal: thousands of TCP connections, at a useful speed.

1. **Listen and accept:** `Tcp.Listen: port` and `Tcp.Accept`, with connections kept on
   the heap (not in the kernel's 512 KB), and no fixed limit of 4.
2. **A sliding window:** `Write` sends as much as the other side's window allows and keeps
   it until it's acknowledged, with a retransmit timer per connection, in place of
   waiting for each segment.
3. **Segments out of order** are kept and joined when the gap fills, in place of dropped.
4. **virtio-net:** a driver for QEMU's `virtio-net-pci`, which is much faster than the
   RTL8139 and is what cloud machines offer.
5. **A load tester on the host:** `phi load` (in `Phi.Cli`) opens hundreds of connections to
   the kernel through QEMU's forwarded ports and plays scripted clients.
6. **Logins:** the server checks names and passwords (`crypto.pbkdf2`) over TCP.

**Done when:** `phi load` holds 500 connections to `samples/mmo/` for a minute, every byte
sent arrives in order, and the S1 benchmarks show the TCP download from S1 got faster.

**Status: done (2026-10-29).** Done in three parts: (a) the TCP server, (b) virtio-net, (c)
`phi load` and logins. `phi load samples/mmo/server.phi --connections 500 --seconds 60`:
500 of 500 logged in, 29,958 lines answered over the minute, all in order, 500 quit,
250,195 frames on the network; answers took 3 ms on average (685 ms at most).
- [x] (a, 2026-10-29) `net.tcp` rewritten for servers, with the client calls unchanged
  (`net_http` still fetches over TLS):
  - `Listen: port backlog`, `Accept` (never waits), `Waiting`, `Send` (never waits: what
    fits in the buffer), `Address`; connections on the heap (up to 1,024, each with 8 KB
    each way), found through a `data.map` of their addresses and ports
  - a send buffer: as much goes out as the other side's window allows, and what isn't
    acknowledged goes again from the oldest byte (200 ms, doubling, 8 tries); a zero window
    is asked again a byte at a time
  - segments that come early are kept (one run of them) and joined when the gap fills
  - `Close` sends what's left and a FIN without waiting; the connection lingers until
    the FIN is answered (or 5 s), driven by `Tcp.Tick` from `Net.Poll`
  - `lose 1 in N` in a `.machines` file makes the test network lose every Nth frame
  - test `net_tcp`: a server and three clients sending 64 KB and 32 KB each way at once,
    losing one frame in 40; every byte arrives in order, segments are sent again and
    kept early, and every connection closes
- [x] (b, 2026-10-29) `drivers.virtio_net`: the virtio-net card through its legacy PCI
  interface (two shared rings, 256 receive buffers given to the card at Start, frames moved
  to a queue on the card's interrupt as the RTL8139 driver does); `drivers.nic` uses
  whichever card the machine has, and `net.net` and `net.dhcp` go through it. `--nic virtio`
  for `phi run` and `phi test`, and a `card virtio` line in a `.machines` file. Every
  network test runs on both cards (`net_tcp (virtio)`), and `net_http` passes over virtio
  too. The test network is now a switch (it learns where each Ethernet address is), as
  real networks are.
  - Found on the way: reading the card's 16-bit count of returned buffers a byte at a
    time could catch it half changed and read entries the card hadn't written yet; and
    with no interrupt, a card whose buffers were all full stopped taking frames, which
    held up every machine on the network. Both are fixed, and the tests caught both.
- [x] (c, 2026-10-29) `samples/mmo/lobby.phi`: logins over TCP (port 4001) with accounts
  kept as PBKDF2 hashes with a random salt each, in a save log; `say` and `quit`. `phi
  load` boots the server with load-client kernels (`lib/phi/test/load.phi`, 100
  connections each) on one network: every connection logs in, says a line a second and
  checks the answers come back in order, and quits; `--profile` samples the server
  meanwhile. `Tcp.Connect` now picks a local port no other connection to that server
  has (100 connections from one machine could repeat one).
- Differs from the plan:
  - The load comes from PHI kernels, not from the host through QEMU's forwarded ports:
    QEMU's user network on Windows waits on its sockets with `select`, which takes at most
    64, so it couldn't hold more than 64 connections from outside.
  - The RTL8139 ring is back to 8 KB: the 32 KB ring from S3 stopped taking frames the first
    time it wrapped round under load (the profiler and counters in `phi load` found it).
    With the interrupt emptying it into the driver's queue, 8 KB is enough.
  - PBKDF2 in the lobby uses 100 rounds, not the thousands a real server would: 500
    logins at 1,000 rounds are about 20 billion instructions in today's code. Raise it
    after S6.
  - No TCP download benchmark: S1 left it here, but the counts of a download depend on
    the host's timing. `phi load`'s numbers are the TCP measure instead.
  - No congestion control (slow start, backing off when frames are lost): on a local
    network the window is the limit that matters. The internet needs it.
  - Only one run of early data is kept at a time; a second gap waits for the first to
    fill. That covers a single lost segment, the usual case.

#### Phase S6: An optimizing code generator

Goal: PHI code fast enough that hot routines don't need assembly.

1. **`volatile` first.** Phase 2 left it out because generated code never keeps values in
   registers between statements; once it does, device memory needs it. Add it, and use
   it in every driver, before anything else here.
2. **A backend interface.** Phase 1 put this off until there was a second backend to
   compare against; the optimizer and the 64-bit backend (S10) are that reason. Split
   the generator so both share the work that doesn't depend on the CPU.
3. **Registers for values within a statement,** then **register allocation across a
   method** for its variables (class variables stay in memory, since interrupt handlers
   share them).
4. **Constants folded, small methods inlined** (`Net.Get16` and the like), and **bounds
   checks removed** where the index is proved in range (a `while` over `.len`).
5. Every behavior test passes unchanged; the S1 benchmarks measure each step.

**Done when:** `Draw.BlendRect` goes back from assembly to PHI at the same speed, and the
S1 benchmarks for crypto and drawing are at least twice as fast as before S6.

**Status: paused (2026-10-30)** to work on what the language and OS can do; optimizing
comes back later. Where it stopped, against before S6: X25519 3.2 times faster, PBKDF2 1.55,
AES-GCM 1.5, SHA-256 1.4, ChaCha20 1.4, `line` 1.6, `fill circle` 1.3. Left: a third
register and temporary values in registers (what `BlendRect` in PHI needs), more constant
folding, and (2) the backend interface, which S10 needs first.
- [x] (1) `volatile` before a variable, a field or a pointer (a reserved word now); the
  network drivers mark the memory their cards write. It changes no code yet, since every
  read still goes to memory; it's there before the register allocator needs it. Tests
  `volatile`, `volatile_errors`.
- [x] (3, first part) A constant or a 32-bit variable on either side of arithmetic or a
  comparison is used as the instruction's operand (`cmp eax, 32`, `add eax, dword [x]`)
  instead of going through `ecx` or the stack. Every benchmark got faster, by 5% to 21%:
  X25519 14%, AES-GCM 19%, the synthesizer 21%, a heap allocation 10%.
- [x] (3, second part) A method's two busiest variables live in `esi` and `edi` across
  statements (32-bit code). [VariablePlanner.cs](Compiler/Phi.Compiler/CodeGen/VariablePlanner.cs)
  picks them, with nothing in it about the CPU: a use counts 8 times more for each loop
  around it, and a variable only gets a register when that beats saving it and the fences
  it crosses. Only 32-bit numbers, enums and pointers go in registers, and not when they're
  `volatile`, placed `at` an address, have their address taken, or are a `while each`'s;
  class variables always stay in memory. A statement that uses a library routine, an
  `asm.` block, text, floats, 64-bit numbers or objects is *fenced* (the variables are
  stored to the frame before it and loaded after), and the generator checks its own output
  for a stray `esi` or `edi` outside a fence, or a register variable read from memory.
  Within a statement, places use the registers as they are (`[ebx + esi*4]`,
  `add dword [ebx + esi*4], eax` for `row:j is row:j + ...`), stores skip push and pop
  when the address leaves `eax` alone, compares and pushed arguments use variables and
  constants as they are. Against the counts after the first part: X25519 58% faster
  (39.3M, 2.8 times faster than before S6), ChaCha20 21%, PBKDF2 16%, AES-GCM 13%, map set
  30%, heap alloc 21%, sorting 22%, `line` 11%. Nothing got slower.
  - Differs from the plan: the benchmarks count instructions, and a variable in memory is
    already one instruction as an operand, so registers alone made things 0-8% *slower*
    (saving them costs instructions). The gain came from what registers let the generator
    leave out, so the register work and the work within a statement went in together.
  - Every method that may change `esi` or `edi` saves them (not only ones that use them
    for variables): `Draw.FillRect` calls `asm.DrawFillRow`, which changes `edi`, under
    `FillCircle`'s loop. They're saved just below the frame, so arguments stay where they were.
- [x] (3, last part) A `while`'s test (and a `while each`'s) is at the bottom, so each time
  round ends in one jump back; before the first time a test that calls nothing is written
  out again, and any other is jumped to. 0-10% on every benchmark: X25519 9% (35.6M, 3.1
  times faster than before S6), counting items in bags 10%, AES-GCM and PBKDF2 2%.
  - `ebx` as a third register is left for now: it's the place register in most of the
    generator, and with pointers reached through `esi` and `edi` the hot loops seldom
    need it. Whether `BlendRect` in PHI needs a third one decides it.
  - `net_udp` sometimes counts a packet for the wrong machine or out of order, on either
    card. It did before S6 too (2 runs in 12), so it isn't the code generator.
- [x] (4, first part) Small methods inlined (32-bit code): one with no loops and at most 40
  syntax nodes, whose parameters, variables and result are each one register and whose
  statements need no fence (`Math.Max`, `Draw.Pixel`, `Net.Get16`, `Draw.Rgb`) is written
  out where it's called, two levels deep at most. Its parameters and variables get room
  in the caller's frame for that call; one it never changes, given a variable in a
  register, is that register. Errors in it name its own line. `line` 15% faster, `fill
  circle` 10%, map get 19%, packing messages 27%, comparing names ignoring case 30%.
  Test `inlining`.
  - Done before (2), the backend interface: (4) is what S6's "Done when" needs, and (2) has
    nothing to serve until S10, so it can be shaped by a real optimizer.
- [x] (4, second part) Bounds checks left out where a `while` proves the index in range: its
  test is `i < n` (or `i <= n - 1`) with n a constant no more than the array's length, or
  `i < list.len` for an array parameter the loop only indexes; nothing in the body sets
  `i`; and a signed `i` starts at zero or more and only counts up. An array parameter's
  element is then `[elements + i*4]` with no other code. A-star 8% faster, the rest
  under 1.3%: most hot loops index through pointers, which have no checks. Tests
  `bounds_loops`, `bounds_loops_changed`, `bounds_loops_signed` (the last two show checks
  that must stay).
- [x] (3, more) Elements and fields used where they are: compared (`cmp dword [esi + 20], 0`),
  pushed as arguments, stored into (`mov dword [ebx], esi`), loaded straight into a
  register variable, and a field of an element (`pool:0.used++`) changed in place. A left
  side worked out in `eax` alone keeps the right in `ecx` instead of on the stack. The
  planner counts a declaration with a value as a use. Map set 14% faster, heap free 11%,
  PBKDF2 8%, HMAC 7%, SHA-256 7%, pool alloc 7%; A-star 1% slower (other variables chose).
- [ ] `BlendRect` back in PHI: tried, and not there. Its row in PHI is 21 instructions a
  pixel, against 16 for `asm.DrawBlendRow` (24% slower overall), so the asm stays. A third
  register for the pointer would make it 19; the rest needs temporary values kept in
  registers too, and 32-bit x86 has seven. The 64-bit backend (S10, sixteen registers) is
  where this is likely to be met.
- [x] Dividing by a constant without the CPU's divide: `x / d` and `x % d` with `d` a
  constant of 2 or more are a shift or a mask for a power of two (every kind of code), and
  a multiply by a number near 2^32 / d and a shift for any other (32- and 64-bit code;
  16-bit code keeps the divide, which is shorter). Times a power of two is a shift. A loop
  of `(i / 10) + (i % 8) + i * 5`, 200 million times, as a 64-bit Linux program: 0.48 s to
  0.19 s with the register variables (gcc -O2 0.11 s). The counts in `phi test --bench` go
  *up* where this is used (pool alloc 6%, pool free 9%, comparing names 5%), since it's more instructions
  than one `idiv`, each far quicker: the counts treat them alike, so those baselines were
  saved again. Tests `divide_constants` (against the divide itself, in the 16-bit run) and
  `DivisionMagicTests`.
- [x] **64-bit math in 32-bit code without the stack or the library's multiply**
  (2026-11-11), where the numbers allow it. A Montgomery product written in PHI
  (`bench_wide`) ran 6.0 times the instructions of `BigMontMul`, the same work in
  assembly, and runs 1.97 times now; its inner line, `u64 v: (bi as u64) * a:j + w:j + c;`,
  is `mov`, `mul`, `add`, `adc`, `add`, `adc` and the store.
  - The product of two 32-bit numbers made 64 bits is one `mul` (`imul` for two signed
    ones), and a 64-bit number times a `u32` is a `mul` and an `imul`. Anything else is
    still `phi_mul64`.
  - Adding, taking away and the bit operations use the right side where it is, a 64-bit
    variable or a `u32` variable, element or field, and a store to a variable doesn't go by
    way of the stack.
  - A statement of such math no longer sends the method's register variables back to
    memory around it, so a loop's counter stays in its register.
  - A side that calls a method is still worked out as before, the right side first.
  `int64_products` holds every form to what the old code gave (and Python, for two of its sums).
- [x] `phi fuzz code`: random programs, each written with what it must print, built for
  every system this computer runs and compared (tests/README.md has the details). It's what
  S8's (5) was waiting to come with, for the compiler rather than for packets: the register
  variables, the divisions and the class's loops above were each broken on purpose once to
  see it notice, and 3000 programs then came out right in 32 and 64 bits. `phi fuzz` with
  packets, for a kernel, is still S8's.
- [x] What weblib's JavaScript benchmark showed (`release_js_bench run`, as a 64-bit Linux
  program: 13.7 s for its 17 loops before, 10.9 s after; the plain loop 74 to 102 million
  rounds a second). Its time was sampled by attaching gdb again and again, since `phi
  profile` only samples a kernel in QEMU; over half was in the interpreter's loop:
  - A `when` whose values are constants close together (5 or more, the table no more than
    three entries a value) is one jump through a table, in 32- and 64-bit code, instead of
    a comparison for each: the interpreter's `when op`. Test `when_table`.
  - `x is y` with both 64-bit numbers kept somewhere is one load and one store of the whole
    number in 64-bit code (it was two halves, by way of the stack), and such a statement
    runs with the variables in registers. `p:(a >> 8)`, a pointer in a variable with an
    index worked out, works the index out first and needs nothing on the stack.
  - A 64-bit number shifted by a constant has that count's own instructions (it tested the
    count as it ran), and a 64-bit constant on the right of `+ - & | ^` and comparisons goes
    straight into its registers. Test `int64_shifts` (every count, against a variable's).
  - The planner counts a field reached through a pointer (`c.mark`) as a use of the
    pointer; it counted only the pointer by itself, so one used mostly for its fields
    seldom got a register (the collector's `MarkCell`).
  - What's left there is the library's: `pc`, `sp` and `bp` are class variables, which
    every method shares, so each step reads and writes them in memory.
- Tried and left out: a value that waits while another is worked out kept in `r8` to `r10`
  in 64-bit code, in place of a push and a pop. It took away 3% of weblib's pushes and made
  its tests 1% to 2% faster at most, for a rule about what the code in between may call. The
  pushes that are left are nearly all arguments (22,000 in `test_js`): passing the first few
  in registers is where that time is, and it's a change to every call.
- [ ] (4, last part) constant folding beyond what `ConstantFolder` does now
- [ ] (2) the backend interface
- [ ] (4) folding, inlining small methods, and bounds checks left out where the index is
  proved in range

#### A program's heap, for small blocks

- [x] The profiler said the heap was 46% of filling a map with names, and a program that
  only takes and gives back memory spent 95% of its time in the heap's lists: every `new`
  searched lists by size, split a block and filed the rest, and every `free` merged with
  its neighbors and filed again. A freed block of up to 512 bytes now waits on a list of
  blocks of exactly its size (`heap_kept`), and the next `new` of that size takes it
  straight back; it still counts as free. When nothing else fits, every waiting block is
  given back and merged first (`GiveBackKept`), so the heap doesn't grow for want of
  memory that's only waiting. Freeing one twice is still caught. As a 64-bit Linux
  program (C's malloc beside it):
  - one block taken and given back, 2 million times: 172 ms to 35 (C 12);
  - 200,000 of 32 bytes, freed in order, 5 times: 93 to 25 (C 14);
  - 200,000 of 8 to 200 bytes, freed in another order, 5 times: 200 to 104 (C 84);
  - a list of 200,000 nodes, 5 times: 92 to 41 (C 9);
  - weblib's `test_css_fuzz` 2.21 s to 1.52, and its JavaScript benchmark 10.9 s to 9.7
    (objects made 6.0 to 8.6 million a second).
  What's left against C is mostly each block's header, 32 bytes in 64-bit code where
  C's is 16: the same blocks fill more of the cache.
- [x] A method's variables are set to zero on the way in by a store for each word up to
  16 of them (it was 4): `rep stosd` takes longer to get going than that many stores, and
  it needs `edi`. Small blocks are zeroed a word at a time for the same reason.
- [x] Found by it: at a method's end, an array of objects that grows had its storage freed
  before its objects, which were then found by reading that storage. The old heap left a
  freed block's first words alone more often than not; this one writes its list's link
  there at once, and `array_copy_program` stopped on 32-bit Linux with "free of memory
  that new did not hand out". The objects are freed first now.
  - Not yet: the kernel's heap (memory.heap), which is the same design as this one was.

#### What the browser found in the newest features (test-projects' FINDINGS.md, 2026-11-11)

- [x] **A struct's array of structs that own text shared the text, then freed it.**
  `Request items[];` in a struct, with `Request` holding `new str`, was accepted (a class
  refuses it), and an item added had its numbers copied and its text shared. The check
  was there, but asked what a struct owns before that was worked out, so it never fired.
  It asks while structs are laid out now (`WillOwnObjects`): written so, it's an error
  that says to write `new`.
- [x] **One struct that takes types serves both kinds of item.** `Item items[];` holds a
  struct PHI manages as objects, as `new Item items[];` would, and numbers and plain
  structs in place. So `Map<int Request>` and a `Queue<Item>` are right for either, with
  no `new` to choose (tests/generic_owned_program, on the checking heap too). `new Item
  items[];` given an `int` is one error that says to leave `new` out, not nine about
  `items.len`.
- [x] **A `new` object below where the class's code ends is a compile error.** It's made
  when its line is reached, so below an `exit` (or a `while true` nothing leaves) it never
  was, and its first use stopped the program. A `new T name[N]` array is the same.
- [x] **`phi profile` names a compiled library's methods** (`JsVm.Run  (vm.phi)`, not
  `phi_lib_weblib_s356`): each method's section is named in the program's assembly. Its
  lines within a method aren't shown yet.
- [x] **`--help` after a command** (`phi test --help`, `phi profile --help`) prints that
  command's usage. It was read as a file name, and `phi test` crashed on it.
- [x] **`Clock.Count` is safe from several threads.** It passed the counter through a
  class variable, so two threads reading at once could each get half of the other's. Its
  halves come back in the caller's own variables now.
- [x] **`net.web` keeps connections** (2026-11-11). It said `Connection: close` on every
  request, so each of a page's files was a name looked up, a TCP connection and a TLS
  handshake. Three things, each of which stands alone:
  - A connection whose answer was read to its end is kept (8 of them, 30 s each), and the
    next `GET` or `HEAD` to that host goes out on it; one the server closed meanwhile is
    asked again on a new one. `Web.keep`, `Web.HangUp`.
  - `crypto.x509` remembers the certificates that chained to a root (32, by SHA-256, for
    the dates they hold between), so a chain seen before costs no signature check, and a
    new server under a known issuer one. A Let's Encrypt chain was 948 million instructions
    to check (three P-384 signatures: `x509 chain of 3, p384` in `bench_tls`), and is
    93 thousand the second time.
  - `Dns.Resolve` keeps the last 16 answers for as long as they hold, 5 minutes at most.

  `Web.opened`, `Web.reused`, `Web.finding_us`, `Web.connecting_us`, `Web.securing_us` and
  `Tls13.checking_us` say where fetching time went. What that showed: the time in
  `Big.MontMul` was P-384 signatures (ECDSA), not RSA; and as a Linux program a fetch was
  mostly waiting for the network (four round trips for each file, about 70 ms each to
  Wikipedia, against 60 ms of CPU), where under QEMU without KVM it was mostly the CPU.
- [x] **The arithmetic under a handshake is in assembly** (2026-11-11), where the CPU's
  `mul` gives a 64-bit product and a carry is one instruction. In instructions run
  (`bench_tls`, `bench_crypto`):

  | | before | now |
  |---|---|---|
  | P-384 signature checked | 325.7 million | 39.5 million |
  | P-256 signature checked | 98.9 million | 12.9 million |
  | RSA-2048 signature checked | 39.7 million | 4.0 million |
  | X25519, one operation | 34.2 million | 3.3 million |
  | A Let's Encrypt chain, the first time | 947.8 million | 115.8 million |

  - `Big.MontMul` joins its 16-bit limbs in pairs and multiplies 32 bits at a time
    (`BigMontMul`); a number's limbs are as they were, so `crypto.ec` and `crypto.rsa`
    didn't change. `Big.AddMod` and `Big.SubMod` are new, for the curves' sums.
  - `Big.Setup` reaches R^2 by squaring, not by 2 x 16 x limbs doublings: that was most
    of an RSA check.
  - `crypto.x25519` keeps a number as 8 parts of 32 bits modulo 2^256 - 38, with
    `X25519Mul`, `X25519Add` and `X25519Sub`; it was 32 limbs of 8 bits.
  - `crypto_bignum` and `crypto_x25519_cases` check all of it against Python's big
    numbers: every size of modulus, numbers that make every carry happen, and points at
    and around p, 2^255 and 2^256.
- [ ] Still to do for the network: `crypto.ec` takes one bit of the scalars at a time (a
  window of them would save about a fifth) and has no squaring routine; no TLS session
  resumption, no fetching several files at once, an 8 KB TCP window, and nothing asked
  for compressed.

#### Looking for memory that's used after it's freed

- [x] samples/own_heap, the heap that stops a program where it runs off the end of a block
  or uses one after `free`, was for Windows only. It's for Linux too now, in 32 and 64 bits
  (`mmap` and `mprotect` for Windows' VirtualAlloc and VirtualProtect; a freed block's
  pages go back to the system, and stay untouchable), and counts `used_blocks` and
  `used_bytes` as the standard heap does, so tests that ask for them build on it.
- [x] Every test run on it, by putting its folder beside the tests for a run
  (docs/patterns.md says how):
  - PHI's own, as Linux programs in 32 and 64 bits: nothing, in 135 programs. (Two differ
    for reasons of their own: one tests the standard heap's limit, and one counts blocks
    from four threads at once, which this heap doesn't lock for.)
  - The Code Library's corelib, imagelib and fontlib: nothing.
  - weblib: one, in 15 test programs. `@import "x.css" supports(display: grid)` called
    `CssSupports.Declaration` from the sheet's parser without giving it the sheet's tokens,
    so it read the tokens of whatever `@supports` condition was read last, freed since.
    On the standard heap that memory was still there and the wrong answer was put right
    by what came next; on this one the read stops, weblib catches the error as it does
    any, and the rule after the `@import` was lost. Fixed in weblib.
  - weblib's JavaScript objects are in slabs of its own, which this heap sees only whole,
    so those blocks aren't checked this way.
  - As 32-bit programs the libraries' fuzz tests run out of addresses on it part way (it
    never uses one twice, and there are 3 GB of them): what they did before that was right,
    and the 64-bit run does them whole.

#### Profiling a Linux program

- [x] `phi profile x.phi --target linux` (or `linux64`): `phi profile` only sampled a kernel
  in QEMU, through its monitor, and this computer has no `perf`; the 20% found in weblib's
  JavaScript benchmark was found by attaching gdb to it some thousands of times. The
  program is built with one more module (`test.profile_linux64`, or `_linux32`), which has
  Linux send it SIGPROF for every millisecond of CPU time it uses; the handler, a few
  instructions of assembly, writes where the thread was to `phi-profile.samples`, and the
  report is the kernel profiler's, by line and by method. Nothing new is asked of the
  compiler or the runtime: the module is PHI, with its two system calls declared in it.
  - On weblib's JavaScript benchmark: 11,420 samples in one run of 11.5 s (10.9 s without),
    half of them in the interpreter's loop and the same lines busiest as gdb had found.
  - On a `Map<str int>` filled and read: the heap is 46% of it (`Heap.FindFree`, `AddFree`,
    `RemoveFree`, `Free`, `Take`, `Alloc`), the map's own code 24%, and hashing the text
    7%. So a name's copy on the heap is what adding costs, as guessed, and the heap is
    what to look at next there.
  - A method of a struct made from a template is named in full in a report
    (`Map<str int>.FindHashed`); its name has a space in it, which the listing's reader
    didn't expect.
  - Tests `profile_program` (the samples a program writes are whole addresses, a fair
    number, all inside it) and `MeasuringTests`.
  - Not yet: Windows; who called the busy code (a sample is where the program was, not
    how it got there); and a PHI OS program's time, which the kernel profiler still shows
    as one total.

#### A stopwatch

- [x] `user.stopwatch`: `Stopwatch watch;` with `Start`, `Stop`, `Restart`, `Reset`, and
  `Milliseconds`, `Microseconds`, `Seconds` and `Ticks`, each readable while it runs; any
  number at once (asked for by the user, 2026-11-10, as C#'s Stopwatch). There was
  `OS.GetTicks`, in milliseconds, and `test.bench`'s one timer of raw counts. It reads the
  CPU's timestamp counter, so it's the same code in a kernel, a PHI OS program and a
  program on Linux or Windows, with nothing new asked of any system; how fast the counter
  counts is measured against `OS.GetTicks` the first time, from the moment one millisecond
  turns into the next to the moment another does, 10 later. On this computer a 50 ms sleep
  reads 50.08 ms, and two runs measured the counter within 0.02% of each other. Test
  `stopwatch`, which checks what must always be so, since the times differ run to run.
  - Not yet: a clock that needs no measuring (Linux's and Windows' own, exact to the
    nanosecond, which would want a call every system answers), and time of day.

#### Structs that take types (generics, first part)

Goal: a container is written once. Until now `data.map` held `u32` keys and values only,
and every program wrapped it or wrote its own.

- [x] `struct.Stack<Item> { ... }` and `Stack<int>`: a struct names the types it takes in
  `<>` after its name, and a use gives them the same way (`Map<Key Value>`, by spaces, as
  `method<int int: int>`). A type's name is any name that isn't a type already; there's no
  keyword. Decided with the user, 2026-11-10: `struct.Stack<Item>` over `struct.Stack: Item`,
  so a struct's heading and its uses line up.
  - Each use is a struct of its own, made before any name is looked up (`Templates.Expand`):
    the template's tokens are read again with each type's name standing for the type given
    (`Parser.Instantiate`), under the use's whole name, `Stack<int>`. The binder and the
    generator see ordinary structs, and nothing in them changed but the labels
    (`Stack_of_int`). Uses inside a template are followed (`Stack<Item>` in
    `Counted<Item>`, `Node<Item>` in itself), up to 2000 structs.
  - `Stack<int> undo;` is told from `a < b` by what's between the brackets: only types, on
    one line, then a name.
  - An error in a template is at its line, says which use it was for, and is said once
    when every use has it. Using the name alone, the wrong number of types, a name that's
    a type already and a struct that takes none each have their own message.
  - Tests `generic_structs`, `generic_structs_errors`, `generic_structs_misuse` and
    `TemplateTests`.
- [x] A method only has to suit the types it's used with. Every method of a template was
  compiled for every use, so one that doesn't suit a type (`Pop`'s `Item top: ...;`, which a
  struct variable can't be) stopped the whole use. The binder
  notes what uses each method (`NoteUse`), takes what's wrong in a made struct's method
  back out of the errors, and once every method is checked leaves out the ones nothing
  uses that had something wrong, with any other unused one that calls them
  (`LeaveOutUnusedTemplateMethods`). One that is used says its errors as any method does.
- [x] `Hash.Of: value` (data.hash): the hash of a number, a `chr`, an enum, an `i64` or
  `u64`, or text. The binder picks `OfNumber`, `OfWide` or `OfText` by the value's type,
  which is what lets a template hash a key without knowing what a `Key` is. Chosen over an
  interface a key must have (the user, 2026-11-10: the built-in), which would need a type
  that must have an interface first, and wouldn't cover plain numbers and text.
- [x] `Map<Key Value>` in data.map, beside the older `Map` class (u32 to u32, behind a
  pointer): keys and values in two arrays side by side, in the order they were added, and
  a table of places found by the key's hash, as data.keyed's is. `new Map<str int>
  counts;`, `Map<u32 Player>`, `while each k in counts.keys`. Test `generic_map`.
  - A struct or a pointer as a key has no hash yet.
  - What it costs, in `bench_text` (a 32-bit kernel, instructions run): finding 1000
    names in a `Map<str int>` is 312,000, against 189,000 for the way it was done before,
    each name hashed by hand into the u32 map (which finds a hash, not the name); adding
    them is 1,207,000 against 354,000, about half of it the text itself (its hash, and its
    own copy in the map). With
    numbers for keys it's 131 a find and 568 an add, against the older `Map`'s 64 and 196:
    the older one is all pointers with no checks, and this one is arrays that grow.
  - Each key's hash is kept beside it, so a key is hashed once however the table grows,
    and a key with another hash isn't compared (adding 1000 names took 22% less).
  - Two things in the generator came of it, for any struct with an array that grows: how
    many it has (`places.len`) is one load where the array is kept, and runs with the
    variables in registers; and an element picked by a variable in a register
    (`places:pos`) is the elements' address and `[ebx + i*4]`, with nothing on the stack.
    A find in the map took 29% less for them.
- [x] `phi fuzz code` makes programs with structs that take types: every program has a
  `Box<Item>` (fields, an array, methods that work through self and a loop in one) used as
  `Box<int>` and `Box<u32>`, and a `Pair<First Second>` used both ways round. Until then
  its programs had no structs at all. Broken on purpose to see it notice: a field's
  element reached through self with the address in the wrong register (41 programs in 100
  wrong), and a template's types given in the wrong order (67 in 100).
- [x] Templates in a compiled library. Its interface has each of its templates whole, with
  the methods' code (a method is otherwise only its header there), so a program that uses
  the library makes its own structs from it, and their code is the program's, as it is from
  source. The interface also lists every struct the library had made (`struct.MadeFor...`,
  a pointer field each), its own templates' and the standard library's, so the program
  makes those too: that's where the library's calls to their methods go. Tests
  `library_generic_program` and `TemplateTests`.
- [ ] A type that must have an interface (`struct.Sorted<Item has Ordered>`), and methods
  that take types of their own.
- [x] The editor. The language server needed nothing: a made struct is a struct, read from
  a copy of the template's file, so going to a method or a field of a `Stack<int>` lands on
  its line in `struct.Stack<Item>`, and its members complete (test in
  `LanguageServerTests`). The VS Code extension (0.3.1) highlights the heading and
  variables of one, and its scanner reads both.

#### Phase S7: Interfaces

Goal: Scenario 1's second milestone, and the interface design Plan.md has been waiting for.

1. **Choose the design** from the server's entity kinds (players, monsters, NPCs) and
   PhiOS's tables of handlers: `interface.Name` with `struct.X has Name`, or checked tables
   of method fields. Decide at the same time whether containers become generic
   (`data.map` for any key and value) or stay one per type.
2. **Build it:** a struct that leaves out a method of its interface is a compile error,
   and code can hold and call any struct with that interface.
3. **Use it** in `samples/mmo/` for entity kinds, and in `gui.widgets` if it fits there.

**Done when:** Scenario 1's milestone 2: `phi load` with hundreds of clients holds a fixed
tick rate, `phi test --bench` reports the tick time so a slowdown fails it, and the
server's entity kinds are one interface.

**Status: done (2026-11-01): milestone 2.** `phi load samples/mmo/server.phi --client
samples/mmo/load_players.phi --connections 300 --seconds 60` holds 20 ticks a second (none
late, the longest 43 ms of its 50, on either card). 400 holds too, but its longest ticks
pass 50 ms (62); 500 doesn't (11 late on the RTL8139, 53 on virtio-net), which is the
optimising phase's to fix (see below).

- [x] (1) the design, chosen with the user: `interface.Shape { [Area] [Grow: int by;] }`
  lists method headers, `struct.Circle has Shape { ... }` writes them, and what each
  returns is taken from the structs (they must agree). A `Shape` owns its object, like a
  `new` variable. Generic containers go through interfaces (the user's choice over a
  built-in `map<K V>` and type parameters): a library container holds objects of an
  interface, so it works for any struct that has it.
- [x] (2) built: a missing method, or one that takes or returns something different, is a
  compile error. `new Shape s` is a variable, `new Shape all[atmost 8]` an array and
  `new Shape main;` a field, and `[Draw: Shape s;]` borrows one. `is` copies, `takes` hands
  over, `is empty` frees, and `if a is b` compares. Each heap object of a struct that has an
  interface keeps its type table's address in the 4 bytes before it. The table holds the
  struct's free, clone and equal routines, then its methods at each interface's place. A
  call through a `Shape` is a call through that table, made as a method value's call is.
  For now an interface method returns a number (or nothing), and a struct stored in place
  can't go into one. Tests: `interfaces`, `interfaces_errors`.
- [x] which struct an interface value holds (the user's choice of syntax): `if thing is Player p`,
  where `p` borrows the object as a Player inside the `if`, and a `when` with `is Player p` /
  `is Rock` branches. Each compares the type table address in front of the object. This is
  what library containers built on interfaces need. Tests: `interfaces_which`,
  `interfaces_which_errors`.
- [x] the first container built on interfaces: `data.keyed`, a `KeyedMap` of objects of any
  struct that has `interface.Keyed` (`[Key]`), found by key through a hash table kept in
  its array fields without a size. Test: `keyed_map`.
- [x] (3) the mmo world's entity kinds are one interface: `interface.Entity` (`Id`, `X`, `Y`,
  `Kind`, `Tick`), which `Player` has, and two creatures that move on their own
  (`Wanderer`, `Patrol`). The players are `new Player players[atmost 32]`, which replaced a
  pool and a map of pointers, and the creatures are `new Entity creatures[atmost 4]`. One
  `[Put: Entity thing; ...]` writes any kind into STATE, which now gives each entry's kind
  (9 bytes), and the clients draw creatures as smaller grey squares. `gui.widgets` doesn't
  fit yet: programs keep widgets in place (`Button ok;`) and `FocusChain` borrows pointers
  to them, and an interface holds objects it owns.
- [x] the milestone's tick time, in `phi load` and `phi test --bench`:
  - The world itself is `samples/mmo/zone.phi` now (the entities, their moves, a grid of
    cells and what each player is told), so the world program (`world.phi`) and a
    benchmark share it. Up to 512 players; a STATE is the player it's for, then what's
    nearest it, a ring of cells at a time, up to 64 things, so it fits in a datagram however
    crowded the world is (a quiet one is still sent whole). Players are found by id
    through `data.map`, and the ones who moved are saved once a second in one write
    (`SaveLog.AddMany`, kernel and program alike) rather than one a tick each. Each
    player's join and save is logged while there are 8 or fewer.
  - The world says how its tick kept up every 10 seconds
    (`tick: 200 ticks in 10000 ms, 0 late, longest 41 ms, 300 players`). `phi load` checks
    it: no tick late once the players are in, and the reports must come here as fast as
    they say. `--client FILE` boots another load client: `samples/mmo/load_players.phi`
    plays 100 players a machine (one socket each machine: a STATE names whose it is by
    putting them first). Test `mmo/bench_tick` (500 players' tick, 5.4 M instructions).
  - **The kernel's clock lost time**, which the load test found and is fixed: `OS.GetTicks`
    counted timer interrupts, and QEMU on Windows loses about a third of them even when
    idle (10 s took 14.7 s here), so every sleep and timeout ran long, and a late tick could
    look on time. Its ms come from the timestamp counter now, timed against PIT channel 2
    when the timer starts, and `[OS.TimerEvent]` keeps to 60 a second by it too.
  - **For the optimising phase (not done now: the user, 2026-11-01, optimising waits for a
    release):** most of a tick with 500 players is sending, not working out what to send
    (`bench_tick`'s 5.4 M instructions are about a fifth of it). Telling the virtio card
    about each frame costs about 80 µs under QEMU (500 a tick took 40 of the 50 ms; told
    once a millisecond instead, with the rest of a burst on the next timer tick, it took
    14), and `Net`'s UDP and IP sends copy and add up their checksums a byte at a time (16
    bits at a time, RFC 1071, took about 10% off). Tried on 2026-11-01, both held 500
    players with the longest tick 33 ms (virtio-net) and 44 (RTL8139), and were taken out
    again to wait for that phase.

#### Phase S8: Containing errors

Goal: a mistake stops only the work it was part of, not the whole machine.

1. **Network system calls for programs,** so game logic can run in user programs: sockets
   through system calls, with every pointer checked as the file calls are.
2. **Messages between programs:** a queue a kernel and its programs (or two programs) send
   records through, so a zone program can hand a player to another.
3. **Restarting:** the kernel sees a zone program stop, starts it again from its last save,
   and moves its players back in.
4. **In the language, decide** whether an error (an index out of range, an empty object,
   out of memory) can be caught in a kernel: a block that stops at the error, frees its
   objects (methods already know what to free at each exit) and goes on after it. Build
   it if the design holds up; programs (1–3) are the fallback that needs no new syntax.
5. **A fuzzer on the host:** `phi fuzz` sends malformed packets to a kernel through QEMU's
   forwarded ports and reports anything that stopped it.

**Done when:** an hour of `phi fuzz` against `samples/mmo/` doesn't stop the server, and a
test where a zone program crashes on purpose shows its players back in a restarted zone
(this half: `kernel32_program_restart`).

**Status: parts 1-4 done; part 5 (`phi fuzz`) waits for the work before a release.**
- [x] (1) Network system calls for programs (80 to 94). The kernel has `use net.calls`;
  a program has `use net.sockets`, whose `Udp`, `Tcp` and `Net` have the kernel's methods,
  so code moves by changing its `use` line. Every socket, connection and listener belongs
  to its process: a program can't use another's (or the kernel's), and they're closed
  when it ends or crashes. Pointers are checked as file calls' are. Test
  `net_programs` (a program echoing UDP and TCP, connecting out, and one that crashes
  holding a port).
  - Differs from the plan: a system call never waits. They come in with interrupts off,
    and sleeping in one would hold up the kernel and every program, so each call does one
    step and the program's side does the waiting (sleeping between looks). That needed
    `Tcp.Begin`/`State` (a connect that doesn't wait; `Tick` sends the SYN again) and
    `Tcp.Take` (a read that doesn't); `Connect` and `Read` are those plus a wait now.
    `Tcp.StopListening` is new too (nothing could close a listener before).
  - `net.calls` and `gui.surfaces` both answer `Process.more_calls` and `on_release`, so
    each now passes on what isn't its own to whatever was set before it.
  - `UdpDatagram` moved to `net.datagram`, which both sides use. `Net`'s address and
    byte helpers are copied into the program's `Net`, since a program can't use a
    kernel class.
  - Not yet: `Dns.Resolve` for programs (it waits, so it needs the same split).
- [x] (2) Mailboxes: the kernel has `use proc.mail`, programs `use user.mail`, both with
  `Mail` (`Open: name size capacity`, `Find`, `Send`, `Receive`, `Wait`, `Waiting`,
  `Close`, and `sender`). System calls 96 to 101. Anyone can send to a box; only its owner
  receives, and each record says who sent it. Boxes close with their program; a box's
  number changes each time its place is used, so an old number reaches nothing. Test
  `kernel32_program_mail` (zone A hands a player to zone B, which tells the kernel).
  - Differs from the plan: named boxes of fixed-size records rather than one queue per
    pair of programs, so a zone can find another by name without the kernel wiring them
    up.
- [x] (3) Restarting: `proc.supervisor`'s `Keep: path` starts a program and `Check` starts
  it again when it stops with an error, giving up after `give_up_after` restarts in a
  row. `Process.Ended` says a program has ended without waiting. `user.savelog` is
  `SaveLog` for programs (on a new `OS.AppendFile`, which is `Fat.Append` in a kernel), so
  a restarted zone reads its players back. Test `kernel32_program_restart` (a zone handed
  three players by mail saves them, crashes, and comes back with them; a program that
  always crashes is given up on).
  - Differs from the plan: the zone reads its own players back rather than the kernel
    moving them in, since it's the zone's save log. `fs.savelog` stays a kernel file:
    making it work in both would have meant every kernel that uses it also needing
    `use fs.fat16`, so the program's copy is a file of its own with the same calls.
  - `SaveLog.Read` (both) took a failed read (-1) for a long one, since an `int` and a
    `u32` compare unsigned; it stops there now.
- [x] (1-3 together) `samples/mmo/` runs its world and its lobby as user programs
  (`server.rootfs/world.phi`, `server.rootfs/lobby.phi`) that `server.phi`, now a small
  kernel, keeps running with the supervisor. `phi test samples/mmo/server.phi` stops the
  world on purpose (K in the server's window) and sees it come back with all three
  players where they were, then reboots and sees the same. `phi load` with 500
  connections for a minute still passes, with the lobby in a program.
- [x] Fixed along the way: `net_udp` sometimes counted a datagram for the wrong machine
  (since S3, on both cards). `Mem.Copy` passed its arguments to its asm block through class
  variables, and both network drivers' interrupts call it: a frame arriving in the middle
  of another copy changed where that copy went. `Mem`'s blocks now read the method's own
  arguments from its frame, so nothing is shared and it's safe from interrupts (and, later,
  other cores).
- [x] (4) catching errors in the language (2026-11-01; "not now" on 2026-10-30, then the
  user's own idea, and their words): `if call Load: path throws err ... ;;` (and `else`)
  stops only that call, and `throw 'no such file: ' path;` makes an error. Errors PHI
  finds itself (an index out of range, the heap out of room) are caught the same way, and
  one nothing catches stops the program as before, with the same message. `err` is an
  `Error` (`lib/phi/lang/error.phi`, which a program that catches gets without a `use`):
  kind, line, file, text, and a message that `log err` writes. Docs:
  [Errors](docs/language.md#errors-throw-and-throws). Tests `kernel32_throw`,
  `throw_program` (Linux, Windows), `kernel32_program_throw`, `throw_errors`,
  `throw_cores_errors`, `throw_boot_errors`.
  - How: a catch is a record on a list (`phi_catch_top`, `lib/x86_32*/throw.asm`) with
    esp, ebp and where to go. In a program that catches, each method with things to give
    back at its end keeps a record of itself while it runs (`phi_unwind_top`), and a throw
    calls each one's `_unwind` routine (its tidying-up, with ebp at its frame) before
    jumping to the catch. A program with no catch has none of this, so it costs nothing.
  - Not caught: an error inside a `locked` block (what it guards may be half changed), and
    in an interrupt handler or a system call (by the code they interrupted).
- [x] (part 6) catching errors in kernels that use several cores: the catches, the methods'
  records and the error being thrown were one each for the whole kernel. Each core now
  has a block of its own (`interrupts.asm`, which owns the GDT now: a data segment for
  each core whose base is its block), reached through `fs` in kernel code; the scheduler
  keeps each task's catches as the cores take turns, kernel code resuming on a core gets
  that core's `fs`, and a throw turns interrupts off until the catch has copied its error.
  Found on the way: turning a number into text (`phi_int_to_str`, the 64-bit and floating
  point ones, and printing) wrote its digits in one buffer for the whole kernel, so two
  cores writing a number at once could swap digits; each call has its own now. Test
  `kernel32_throw_cores` (four threads on several cores, each catching 600 errors of its
  own, thrown from methods holding text to give back and from an index out of range: none
  mixed up, and the heap back where it was).
- [x] (part 7) CPU exceptions as errors: dividing by zero, an empty object or a null
  pointer (a page fault under 4 KB), memory that isn't there and a general protection fault
  become `ErrorKind.DIVIDE`, `EMPTY` and `ACCESS`. In a kernel, the exception path asks
  `throw.asm` (`phi_fault_hook`), which, inside a catch, changes the saved state so the
  interrupt returns into `phi_throw` on the faulting code's own stack; anywhere else (no
  catch, a `locked` block, an interrupt handler) it's the panic screen as before. A PHI OS
  program that catches tells the kernel its fault entry at startup (system call 10, from
  its `throw.asm`'s init through `phi_fault_register`, which each system has), and the
  kernel sends a faulting program there rather than stopping it, unless its stack has run
  off its end or the entry itself faulted. Tests `kernel32_throw_faults`,
  `kernel32_program_faults`, and a divide in `kernel32_throw_cores`'s threads.
  - [x] Linux and Windows programs, 32 and 64 bits: `phi_fault_register` installs a signal
    handler for SIGFPE and SIGSEGV on Linux (`rt_sigaction`, with a restorer for
    `rt_sigreturn`), and a vectored exception handler on Windows
    (`AddVectoredExceptionHandler`), which each change the stopped code's saved registers
    so it carries on in the fault entry, as PHI OS sends a program. Test `faults_program`,
    the same output on all four. An uncaught one now ends a program as any error does
    (`PHI:` and the message, then -1), not with the system's own crash.
  - [x] the line a CPU exception happened on: a program that catches errors has a line
    table, `phi_lines` (each line's code's address, the line and its file's name, in the
    order the code is in, ending with a mark after the PHI code), which the runtime's
    `phi_line_at` looks the faulting address up in; each system passes that address to
    the fault entry. A fault inside an asm block or the runtime has no line.
  - Not yet: a line for a fault in the runtime (a null text given to a routine), which
    would need the caller's line, from its stack; and the panic screen's line (the table
    is only in programs that catch, to keep kernels small).


- [ ] (5) `phi fuzz`: **later**, with the optimisations, before a release (the user,
  2026-11-01). Until then this phase's "Done when" waits on it; parts 1-4 are done.

#### Phase S9: Several cores

Goal: one server uses every core of its machine.

1. **The local APIC and I/O APIC** in place of the 8259 PIC, and **starting the other
   cores** (a 16-bit start routine for each, then 32-bit, with a stack each).
2. **Atomics and locks in the language:** atomic add and compare-and-swap on a variable or
   field, and a spin lock; the heap, frames and scheduler made safe across cores (turning
   interrupts off is no longer enough).
3. **Data per core** (each core's current task, its own run queue), and a scheduler on
   every core.
4. **Kernel threads:** methods already have stack frames; decide what a class variable
   means when two threads run the class's methods (shared, and a warning when one is
   written without a lock?).
5. QEMU runs tests with `-smp 4`.

**Done when:** `samples/mmo/` runs its zones on 4 cores in QEMU, the S1 benchmark of its
tick shows them doing more work than one core, and a test that hammers the heap from 4
cores ends with `Heap.used_blocks` at 0.

**Status: done but the demo's 4-zone tick, which waits for the optimising phase (2026-11-02).**
- [x] (1, first part) `proc.cores`: `Cores.Start` wakes every other core at once (the
  local APIC's INIT and STARTUP to all but this one) with a start routine copied to
  `0x1000`. Each comes into 32-bit mode with the first core's GDT, interrupt table and
  paging, turns its own local APIC on, takes a number with `lock xadd`, and waits halted
  on a stack of its own. `Cores.Run: method` runs it on every core with its number and
  waits for them all; an interrupt (vector `0xF0`) wakes them. QEMU runs with 4 cores
  now (5 of the plan), `--cores n` for others. Test `kernel32_cores` (each core sums its
  share of an array; also tried by hand with 1, 2 and 8 cores).
  - Differs from the plan: no ACPI tables yet. Waking every other core at once, and
    counting them as they arrive, needs none; the I/O APIC (for sending devices'
    interrupts to other cores) will.
  - Until locks (2), work on another core must only touch its own data: no heap, log,
    files or network there.
- [x] (1, the I/O APIC) `drivers.acpi` reads ACPI's MADT (found from the RSDP the BIOS
  leaves below 1 MB): the cores' local APIC IDs, the I/O APIC, and how ISA IRQs are wired
  to its inputs (QEMU's timer, IRQ 0, comes in on input 2; IRQs a PCI card shares are level
  triggered). `drivers.apic`'s `Apic.Start` moves the IRQs there, with the vectors they had,
  and masks the PICs; the runtime (`interrupts.asm`) then masks and unmasks in the I/O APIC
  and sends end-of-interrupt to the local APIC, so drivers and handlers don't change. Tests
  `kernel32_acpi`, `kernel32_apic` (the timer, masking, the keyboard) and
  `kernel32_apic_net` (DHCP and a ping over each card's interrupt).
  - Found on the way: the EBDA's segment, where the RSDP may be, is in page 0, which
    paging leaves unmapped for null pointers; `Paging.ReadLowWord` maps it for the moment
    a read takes.
  - Not yet: Cores.Start and the kernels switching to it by themselves; IRQs sent to other
    cores.
- [x] (1, rest) a timer on each other core (its local APIC's, counting for 10 ms, measured
  against the first core's clock), which takes the CPU back from its programs (with 3)
- [x] (2, the language) `atomic` and `locked` (the user's choice, 2026-10-30: words in the
  language). `atomic x++ n;`, `atomic x-- n;` (lock add/sub), `atomic x is v;` (xchg) and
  `atomic x is v when old` (lock cmpxchg; as a statement or a bln value) on a number,
  `bln` or pointer of up to 32 bits in memory. `lock guard;` declares a lock;
  `locked guard ... ;;` takes it (xchg, spinning with `pause` while another core has it),
  runs with this core's interrupts off, and gives it back at the end and on every
  `return`, `break` and `continue` that leaves the block. Test `kernel32_atomic` (4 cores
  adding to one counter with `atomic`, and with plain math in a `locked` block: none
  lost, where a plain `total++` from 4 cores loses some), and `atomic_errors`.
  - The interrupts-off part isn't in the plan: without it, an interrupt handler wanting
    a lock its own core's code holds would wait forever.
- [x] (2, the heap and frames) `Heap.Alloc`, `Free` and `LargestFree` work in a
  `locked heap_lock` block, which replaced turning interrupts off (that kept the flags in
  one shared variable, which two cores would have overwritten); `Frames.Alloc`,
  `AllocContiguous` and `Free` take `frames_lock` (they had no protection at all). Test
  `kernel32_cores_heap`: four cores allocating, filling and freeing 3,000 blocks each at
  once, every block holding only its own core's number, and `Heap.used_blocks` back
  where it was (this is one of the "Done when"s). Without the lock the same test crashes
  at once (double frees, then a page fault on every core).
- [x] (2, `log`) In a kernel, each `log` and `debug` statement is wrapped in a lock
  (`lib/x86_32/log_lock.asm`) once `Cores.Start` has run, so a whole statement comes out
  at once. It knows its core (by its local APIC ID), so a log whose value calls a method
  that logs doesn't wait for itself, and it keeps that core's interrupts off while held.
  Test `kernel32_cores_log` (four cores logging long lines: every one whole; with the
  lock taken out, they come out mixed).
- [x] (2, the rest of the library) and (3), together: programs on every core, with **one
  kernel lock** (the user's choice, 2026-11-01). `Cores.SharePrograms` has the other cores
  run programs from then on; each core takes a ready program no other core is running.
  Kernel code (interrupts, system calls, and the kernel's own code, task 0, which stays on
  the first core) runs on one core at a time under a ticket lock, so the scheduler, files,
  the network and the rest of the kernel library work unchanged. Whoever holds the lock
  sets `phi_current_task` to its core's task, so everything that reads it is as it was.
  Each core counts how deep it is in kernel code (the kernel's own code counts 1) and
  holds the lock while that isn't 0; the kernel lets go of it while it waits (`OS.Idle`,
  `OS.Sleep`, `OS.Yield`, so `Process.Wait`). Data per core: its task, its depth, its TSS
  (where ring 3's interrupts land), and for each other core an idle loop. A program
  killed while another core runs it counts as running until that core has let go of it,
  so its memory isn't freed under it. Single-core kernels don't change: the lock is only
  there once `SharePrograms` has run. Test `kernel32_cores_programs`: four busy programs
  making system calls on 3 or 4 cores at once, finishing in about 1.7 times the time one
  takes alone (four times, on one core), each with the same answer, then one killed while
  another core runs it, and every frame of memory back.
  - Found later, with the mmo's kill and restart: a program killed while its core waited
    for the kernel lock to make a system call made the call once it got the lock, and a
    call that sets its state (to sleep, say) brought it back, so it never ended. A system
    call from a task that has been stopped now isn't made.
  - Not yet: finer locks than the one, which the benchmarks would justify part by part;
    devices' interrupts sent to other cores.
- [x] (4) kernel threads, and what a class variable means in them (the user's choice,
  2026-11-01: shared by every thread, with a warning for writes that aren't guarded).
  `proc.threads`: `Threads.Start: method value` runs it as a task of its own, in ring 0
  with a stack of its own, scheduled on any core like a program and without the kernel
  lock, so threads run at once on several cores once programs share them; `Wait` and
  `Done`. The first core's timer takes the CPU back from a thread as from a program (not
  from kernel code inside an interrupt: the core's depth says). When its method returns,
  a thread marks itself finished and gives up the CPU. The compiler warns when code a
  thread runs (a method given to `Threads.Start` or `Cores.Run`, and every method it
  calls) writes a class variable, or a field of one, outside `locked` or `atomic`; an
  array's element isn't warned about, as that's how each thread keeps its own result.
  Test `kernel32_threads` (four threads summing their parts into their own elements and
  a shared total with atomic, using the heap, first on one core and then on several;
  four busy threads on two or more cores, faster than one after the other), and unit
  tests for the warning. Nothing in the library or the samples warns.
- [ ] (the demo) `samples/mmo`'s zones on 4 cores: the world can be split into zones
  (`Game.ZONES` in `protocol.phi`), each its own world program on its own port
  (`Game.PORT + n`) with its own save log, which `Supervisor.Keep` starts with its zone on
  the command line; the server shares the cores (`Cores.SharePrograms`); `phi load`'s
  players spread over the zones, and it checks each zone's tick (`zone N tick: ...`).
  The scripted test (`mmo/server`) passes with 1 zone and with 4, killing zone 0's world
  while another core runs it and seeing it back with its players. Measured with `phi
  load` (400 players, 40 s): **one zone holds its tick** (20 a second, none late, the
  longest 67 ms), but **four zones don't** (18 a second each, every tick late, the longest
  92 ms; with 100 players, 2 late and the longest 89 ms). A shorter timeslice on the other
  cores changed nothing, so it isn't waking up late. What's left is the one kernel lock:
  every STATE a zone sends is a system call through the network, and each zone polls
  the network and sleeps a thousand times a second, all of them taking turns for the
  lock that one zone alone never waits for. So `Game.ZONES` stays 1 until the network
  path has a lock of its own (the finer locks the user's choice left for when a benchmark
  shows the need: this is that benchmark).
  - [x] the network's own lock (the user's choice, 2026-11-02). `Net.Lock` and `Unlock`:
    one core at a time in the network's code, with that core's interrupts off; the core
    holding it can take it again (`Tcp.Write` calls `Net.Wait`, which calls `Poll`), and
    `Net.Wait` lets it go while it sleeps. Every public method of `Net`, `Udp` and `Tcp`
    takes it (a wrapper around its `...Locked` work). The cards' drivers keep their queue
    of received frames under a lock of their own (`rx_lock`), shared by the interrupt and
    `Receive`. Programs' network calls (80 to 94) don't take the kernel lock at all: the
    system call entry asks `tasks.asm`'s `phi_own_call` first, which sends a range of
    numbers that `net.calls` registered (`phi_own_first`, `phi_own_count`) straight to
    `NetCalls.Calls`, which takes the network's lock; `OS.CurrentTask` now gives this
    core's task, not the kernel lock holder's.
    - Measured on the way, with counters added for the runs: a first version that only
      let the kernel lock go once inside `net.calls` made things worse. Each call then
      took the kernel lock twice, and the entry before it (the general system call path)
      held it about 90,000 cycles each time, more than the network work itself. Waiting
      for the network's lock by reading first (and exchanging only once it looks free)
      cut its holds by about a third under contention.
    - With `phi load` (400 players, 40 s): **one zone** now holds its tick with nothing
      late (20 a second, the longest 59 ms; 11 late and 169 ms before, measured the same
      day). **Four zones** no longer get stuck (before, at 400 players, the worlds never
      emptied their queues of datagrams). Each keeps 20 ticks a second with every player,
      but about 7% of ticks are late, the longest 110 ms, so `Game.ZONES` stays 1. What's
      left: all the zones' network work is one lock's (one card, one queue), and,
      probably, each zone's once-a-second save, which goes to the one disk under the
      kernel lock and is part of the tick's time (even 10 players a zone had 50 ms
      ticks; not measured yet).
  - [x] the file system's own lock (2026-11-02), after measuring the save. With 4 zones
    and 400 players, timing each part of a tick: a zone's save (`OS.AppendFile`) held the
    kernel lock for 20 to 30 ms, and the other zones' calls waited for it. With saving
    turned off, one run had 1 tick late of 2,400 once the players were in; with it on, 598.
    So:
    - `proc.corelock`: `CoreLock`, the network's lock made into a struct any part of the
      kernel can have (`Take`, `Give`, `LetGo`, `TakeAgain`): one core at a time, its
      interrupts off while held, and taken again by the core that holds it. `Net.Lock` is
      one now. Test `kernel32_corelock`.
    - `Fat` has one: each public method takes it (a wrapper around its `...Locked` work).
      Programs' file calls (17 to 21, 31, 38 to 42) go to `Process.FileCalls` without the
      kernel lock, holding `Fat.Lock`. `phi_own_call` takes up to 8 ranges of calls now
      (`phi_own_add`: first, how many, method), the network's and the files'. Test
      `kernel32_cores_files` (four programs on several cores appending to their own file
      and a shared one while the kernel reads the disk: every record whole).
    - A file's date is from `OS.Now` (worked out from the seconds, as Hinnant's
      civil_from_days), not `drivers.rtc`, and `OS.Now` reads the CMOS clock under a lock
      of its own (`phi_clock_lock`), since a file call no longer has the kernel lock.
      `Rtc.Read` in a kernel isn't under that lock yet (only `samples/terminal.phi` uses it).
    - Measured: a save took 10 to 23 ms in a good run, and only holds up other saves. But
      the 4-zone run varies too much between identical runs for one to show the whole
      effect: 2 late of 2,400 in the best run, and the same code in later runs 320, 800 and
      more. Six runs back to back, three before and three after, went 307, 1,108, 1,600
      late and then 1,600, 1,190 and 800 (ticks counted fall when the zones fall behind),
      getting worse run after run whichever code was in. In every bad run the time is in
      sending: a tick's 100 STATEs took 70 to 200 ms (receiving up to 200 ms), against
      about 30 ms in good ones. Each `Rtl8139.Send` waits, holding the network's lock
      with interrupts off, for one of the card's 4 transmit buffers, which free up as
      QEMU hands frames to the test network's switch (`VirtualNetwork`, on the host),
      which a slow client machine can hold up. The runs vary because the computer they ran
      on was in use meanwhile (the user, 2026-11-02). So `Game.ZONES` stays 1, and the
      4-zone tick (the network's speed with four senders) waits for the optimising phase
      before a release, on a machine kept for measuring; the rest of S9 is done.

#### Phase S10: 64-bit

Goal: 64-bit kernels and programs, with more than 4 GB of memory.

1. **A loader that enters long mode,** with 4-level paging, and a memory map past 4 GB.
2. **An x86-64 backend** behind the interface from S6: 64-bit pointers, `i64`/`u64` in a
   register, SSE2 for floating point in place of the x87.
3. **The library on 64 bits:** the assembly in `lib/x86_32/` and `lib/x86_32_user/` ported,
   struct layouts with 8-byte pointers, system calls, and the start routine from S9.
4. **Tests run in both modes,** as tests made of `:Library` classes already run in 16 and
   32 bits.

**Done when:** the test suite passes as 64-bit kernels and programs, and `samples/mmo/`
runs with 8 GB of memory.

**Status: in progress (since 2026-11-01).** Decided with the user: `int` stays 32 bits and
every sized type keeps its size (only pointers become 8 bytes); 64-bit host programs come
before 64-bit kernels (H4 first, then the kernel); and 64-bit is a mode of the x86
generator, not a second backend: x86-64 shares almost everything with 32-bit x86, so (2)
the backend interface waits for ARM, which needs one. The user will test the macOS step on
their Mac.

The order: (a) 64-bit Linux programs, (b) 64-bit Windows programs, (c) 64-bit kernels
(long mode, 4-level paging, the kernel library), (d) macOS.

- [x] (a, first part) `--target linux64`: an ELF64 program at 0x400000 (`bits 64`), a
  runtime of its own (`lib/x86_64_linux/`: the syscall instruction, console, numbers as
  text), and the generator's 64-bit mode. What's the same at either width is translated as
  each instruction is written (`Widen`: the stack and frame registers, push and pop, lea);
  what isn't is each place's own (8-byte argument slots, parameters from rbp + 16, labels'
  addresses by `lea ..., [rel label]`). So far: numbers up to 32 bits (and bln, chr,
  enums), their variables and math, methods that take and give them, calls, if, while,
  when, log, debug and exit. Anything else is an error saying it isn't in 64-bit programs
  yet (`Check64`), which leaves that test to the other systems; 7 tests run as 64-bit
  Linux programs (`bitwise`, `compare_words`, `if_else`, `math`, `numbers`, `while_loop`,
  `while_many_iterations`). Register variables and inlining are off in 64-bit code for now
  (register variables came later: see "register variables" below).
- [x] (a, second part) arrays of numbers kept in place (fixed and `[atmost N]`: indexes,
  their bounds checks and the index error, `.len`, `.max`, `sizeof`, `++`, `.len is`,
  `while each`), and pointers to numbers (`addr`, `p:i`, `p + n`, comparing, casts,
  parameters and results, a `ptr<u8>` printed as text). A pointer is 8 bytes in 64-bit
  code (`PhiType.AddressSize`, which the binder sets for each program, so structs are laid
  out for where they run). `p + n` is still typed as a number, so programs see no change,
  but in 64-bit code it's worked out in all 64 bits (`IsAddress`). A register in a memory
  operand is 64 bits (`Widen`). `lib/x86_64_linux/throw.asm` has 64-bit catch records
  ready, but 64-bit code doesn't catch yet. Tests: 15 run as 64-bit Linux programs, and
  `pointer_math` (new) on all three systems.
- [x] (a, third part) structs made of numbers, pointers and arrays of them (in place,
  arrays of them, and through pointers: `p.x`, `p:i.x`, `t.next.id`), arrays of pointers,
  and the heap (`new`, `free`, `OS.Grow` by Linux's brk). Two decisions on the way:
  - **Addresses are `ptr<u8>`** (the user's choice over a new address-sized number type):
    the heaps (`memory.heap`, `memory.program_heap`) keep addresses in pointers now, so
    `Heap.Alloc` gives a `ptr<u8>` and `Heap.Free` takes one, `OS.Grow` gives a `ptr<u8>`,
    and a header's size is `sizeof HeapBlock` (24 bytes, or 32 in 64-bit code). 32-bit
    code is the same as before.
  - **`x64.Name { }`** is an assembly block's 64-bit version, beside `asm.Name { }` (chosen
    after seeing code for `asm64.Name` and for rewriting in PHI): calls don't change, the
    compiler takes the version for the code it's building, and using a block with no
    version for it is an error naming it. `x64` is a reserved word. The heap's two blocks
    have one so far.
  Tests: 19 run as 64-bit Linux programs, and `heap_lists` (new) on all three systems.
- [x] (a, fourth part) text: strs with a size and strs that grow (a slot of its text's
  address and its room, 16 bytes in 64-bit code), building them and adding to them, the
  caller's str as a parameter, str results, comparing, `has`, characters, `.len`, `.max`,
  text in parentheses, and `lib/x86_64_linux/text.asm`. Two fixes on the way: storing a
  constant into an 8-byte place (`p.next is 0`) wrote only 4 of its bytes, and a method
  giving a str took its hidden result argument as 4 bytes. A test's `NAME.MODE.expected`
  is used before `NAME.expected` (`sizeof.linux64.expected`: a pointer is 8 bytes there).
  Tests: 33 of the 51 that run as Linux programs run as 64-bit ones too, and
  `text_basics` and `heap_churn` (new) on all three systems.
- [x] (a, fifth part) arrays of numbers and pointers as parameters (`int numbers[]`): a
  view is 16 bytes in 64-bit code, laid out as in memory (the elements' address, then how
  many and the most, pushed as one 8-byte value), so `.len`, `.max`, indexes, `while each`
  and passing one on work. Test `array_views` (new); 34 tests run as 64-bit programs.
- [x] (a, sixth part) arrays of strs (`str days: 'Mon' 'Tue';`, `str names[atmost 8];`,
  `str slots[3];`): their texts' addresses and rooms are 8 bytes each in 64-bit code, and
  indexes, `++`, `.len`, `.max`, `while each`, `is` and passing an element as the caller's
  str (a copy of its slot, written back after the call) work. Two fixes on the way, in 32-bit
  code too: an array of strs with no size for its texts (`str slots[3];`) cleared its own
  table of addresses at the start instead of the texts they point to, and a program with
  one (`str names[atmost 8];`) didn't get the heap its texts grow on. Test `str_arrays`
  (new) on all three systems; 41 tests run as 64-bit programs.
- [x] (a, seventh part) str fields kept in the struct (`str name: [8];`, `chr home[atmost
  12];`): set, built, compared, indexed, `.len`, in arrays of structs and through pointers,
  and passed as text or as the caller's str. Test `str_fields` (new) on all three systems;
  42 tests run as 64-bit programs.
- [x] (a, eighth part) copying and comparing structs (`b is a`, `if b is a`), objects
  (`new Point a: 1 2;`, copies, comparing, `takes`, `is empty`, methods that borrow them
  and give new ones, objects in objects: lists and trees), new str fields (`new str bio;`),
  and struct methods. The routines each struct's objects get (free, clone, assign, set,
  renew, equal) keep their frames in 8-byte slots in 64-bit code (`Pushed`), and
  `lib/x86_64_linux/out_of_memory.asm` is new. Tests `struct_copy` and `objects_program`
  (new) on all three systems; 44 tests run as 64-bit programs, and 14 of the kernel tests
  made into programs (the crypto ones, the objects ones, `synth`, ...) give the same output
  as 32-bit and 64-bit programs.
- [x] (a, ninth part) arrays of structs (fixed and `[atmost N]`: `++` a copy or zeros, `while
  each` over them), and arrays that grow (`int scores[];`) of numbers, pointers and structs:
  in 64-bit code one is laid out as a view is (its elements' address, how many, its room:
  16 bytes), so `phi_array_grow`, indexes, `.len`, `.len is` and passing one as `int
  numbers[]` read it alike. Tests `struct_arrays` and `growing_arrays_program` (new) on all
  three systems; 48 tests run as 64-bit programs, and 15 kernel tests as programs.
- [x] (a, tenth part) catching errors (`if call ... throws err`, `throw`, and the runtime's
  errors): a catch's record and each method's are 8 bytes a field in 64-bit code, as
  `lib/x86_64_linux/throw.asm` reads them, so a throw gives back what the methods it passes
  own. The rest of the system calls (`OS.Arguments`, keys, files and folders, memory,
  clearing the screen) in `lib/x86_64_linux/system.asm`, and `ask` (`read_line.asm`). Test
  `throw_unwind` (new) on all three systems; every program test that runs on Linux runs as
  a 64-bit Linux program too (51).
- [x] (a, eleventh part) 64-bit whole numbers (`i64`, `u64`) and floating point (`f16`,
  `f32`, `f64`). For now an i64 is still worked on in edx:eax and a float on the x87, as in
  32-bit code (the same code: functionality first); what differs is that one goes as a
  single 8-byte argument, and a value is loaded onto the FPU through memory rather than by
  pushes. `lib/x86_64_linux/int64.asm` does the 32-bit code's routines in one register each,
  and `float.asm` writes floats out. Test `numbers_program` (new) on all three systems; the
  kernel tests `floats` and `int64` made into programs give the same output in both modes
  (17 kernel tests so far).
- [x] (a, twelfth part) arrays of objects (fixed, `[atmost N]` and growing), copying and
  comparing whole arrays (`b is a`, `if b is a`: numbers, structs, strs and objects), and
  arrays of structs as parameters (`Point points[]`). Tests `object_arrays_program`,
  `array_copy_program` and `struct_array_params` (new) on all three systems; 19 kernel tests
  made into programs give the same output in both modes. (`kernel32_throw` as a program
  doesn't: its `Fill` grows an array until the heap runs out, which a 64-bit program, with
  room for far more, doesn't reach before Linux stops it.)
- [x] (a, thirteenth part) method values (`method<int: str> say: addr Say;`, called through
  `rax`), and str arrays that grow (`str names[];`: each text's address and then each one's
  room, 8 bytes apiece in 64-bit code, as fixed str arrays have them). A fix on the way: the
  generator's size of an address was 2 bytes in 16-bit code, where it's 4, which broke
  copying a str array there (`array_copy` in 16 bits). Test `str_growing_arrays` (new) on
  all three systems; 22 kernel tests made into programs give the same output in both modes.
- [x] (a, fourteenth part) interfaces (`interface.Shape`, structs that have them, calls
  through a type table, `is Square`, `when` on what an interface value holds), and arrays of
  objects in struct fields (`new Shape others[atmost 4];`, whose elements the binder now lays
  out 8 bytes apart in 64-bit code). An object of a struct with interfaces keeps its 4-byte
  type table address before it, and a type table's entries stay 4 bytes, in 64-bit code
  too: the program is below 4 GB, so an entry is read into a register and jumped to. Test
  `interfaces_program` (new, the kernel test as a program) on all three systems; 26 kernel
  tests made into programs give the same output in both modes.
- [x] (a, fifteenth part) a struct's arrays without a size (`int scores[];`, of numbers,
  structs, strs and objects, in a struct): laid out as a view, so the routines each one gets
  (free, clone, equal) and the code that grows, indexes, measures and cuts one read it at
  `ViewCount` and `ViewMax`. Test `growing_array_fields_program` (new, the kernel test as a
  program) on all three systems; 29 kernel tests made into programs give the same output in
  both modes, among them `keyed_map`.
- [x] (a, sixteenth part) str arrays and arrays of objects as parameters (`str rows[]`,
  `new Node list[]`), arrays of texts kept in place (`str codes[atmost 2]: [3];`, in
  variables and fields), and `has` on arrays (numbers of up to 32 bits and pointers, texts,
  structs, objects). Tests `has_program`, `text_growing_arrays_program` and
  `array_params_objects_program` (new, the kernel tests as programs) on all three systems.
  Every kernel test that can be a program (35) now builds as a 64-bit Linux program and
  gives the same output as a 32-bit one, but `kernel32_throw` (above); the rest use what
  only a kernel has (drivers, ports, interrupts, cores).
- [x] (a, the rest) what was left for 64-bit programs:
  - `atomic` and `locked` (S9): an atomic step on a pointer is all 8 bytes of it, and a
    locked block keeps its slots as wide as an address. Found on the way: a *program's*
    `locked` block turned interrupts off (`cli`), which a program in ring 3 may not do, so
    it stopped the program, on PHI OS and on Linux and Windows alike (only kernels had
    tests). A program's block now only takes the lock. Test `atomic_program`.
  - `has` on arrays of 64-bit numbers (whole: both halves) and of floating point numbers
    (compared as `is` compares them: `0.0` finds `-0.0`, `nan` finds nothing), in 32-bit
    code too, where `has` turned them away before. Test `has_numbers`.
  - `x64` versions of the blocks programs can reach: `memory.mem`'s four (they read their
    method's arguments, 8 bytes apart in 64-bit code), `gfx.draw`'s three (whose addresses
    were `u32`s; they're pointers now) and `test.bench`'s. Tests `mem` (a library test now,
    so on every system) and `draw_surface` (drawing into memory). The blocks left without
    one are kernels' (the heap, paging, cores and drivers), as kernels stay 32-bit.
- [x] (a, register variables) A 64-bit method keeps up to four of its most used variables
  in `r12` to `r15`, chosen by the same planner and fenced by the same rules as 32-bit
  code's `esi` and `edi`: a 32-bit number in the low half (`r12d`), a pointer in the whole
  register. An index held in one is part of the address as it is when it can't be negative
  (unsigned, or just checked), and goes through `r11` with its sign otherwise, so a
  negative `int` index through a pointer still reaches before it. `p:i` with a pointer and
  an index that are each one load is one `lea`, pointers in variables are compared where
  they are, and pointer arithmetic with a simple right side no longer uses the stack. As
  64-bit Linux programs: a sieve of 5 million 0.62 s to 0.35 s (32-bit 0.35, gcc -O2 0.31),
  blending a million bytes 100 times 0.30 s to 0.13 s (32-bit 0.14, gcc -O2 0.06), and
  weblib's `test_js`, `test_js_fuzz` and `test_css_fuzz` 8% to 14% faster at the same
  size. Test `register_variables`.
  - Inlining stays off in 64-bit code. `EmitInline` can do it (pointers get 8-byte room)
    and every test passes with it on, but it made weblib's test programs 42% bigger and no
    faster, at 40 nodes or at 8, 14 or 20: an inlined method's parameters are in memory.
  - Found on the way: a number written as text went through one buffer that every thread
    shared (`phi_int_to_str`, and the 64-bit and float ones), so two threads writing
    numbers at once could get each other's digits. That was `files_threads_program`'s
    failure once in a while on 64-bit Linux. Each call has the digits on its own stack
    now, as the 32-bit runtime's do; the test passed 40 times in a row.
- [x] (a class's own loops) A `while` in a class's own code keeps the class variables it
  uses most in registers while it runs, in 32- and 64-bit code: only ones nothing else can
  reach meanwhile (no method or asm block names them, they aren't `volatile`, and their
  address isn't taken), loaded before the loop and stored after it. A sieve written in the
  class itself, as a Linux program: 0.59 s to 0.34 s in 64 bits and 0.58 s to 0.34 s in 32,
  the same as in a method. Test `class_loops`.
- [x] (b) `--target windows64`: a PE32+ .exe at 0x400000 (no relocations, so the code's
  32-bit addresses of labels hold), the same generated code as a 64-bit Linux program, and
  a runtime of its own (`lib/x86_64_windows/`: kernel32's functions called the x64 way, in
  rcx, rdx, r8 and r9 with the stack aligned and its shadow space, by `PHI_CALL`; the
  import table's entries 8 bytes), which shares the pure routines and errors with
  `lib/x86_64_linux/`. Its heap is reserved past 4 GB (at 0x500000000), so every address
  must be whole: the test suite and the kernel tests made into programs give the same
  output there as everywhere else. Casting between a pointer and an `i64` or `u64` keeps
  all of the address now. Every program test runs as a 64-bit Windows program too.
  (Windows programs, 32 and 64-bit, sometimes take more than the 10 seconds a test has
  when the whole suite runs, a test or two a run, and pass when run again: something
  outside PHI, still to look into.)

#### Phase S11: Running for real

Goal: Scenario 1's third milestone: the server boots on a cloud machine.

1. **Checking certificates** (Later's TLS item): ASN.1 and X.509, RSA and ECDSA P-256
   signatures, a set of trusted roots.
2. **Server-side TLS:** signing with the server's own key, so logins go over TLS.
3. **Disks a cloud machine has:** virtio-blk (and NVMe), behind the same block layer
   (`drivers.disk`) that ATA uses.
4. **Booting on a cloud machine:** UEFI (shared with the console OS scenario), or a BIOS
   image where a cloud offers that; serial output where the cloud shows a console.
5. **A deployment guide** in `docs/`: building the image, uploading it, opening ports.

**Done when:** Scenario 1's milestone 3: `samples/mmo/` boots on a cloud machine without
QEMU, players log in over TLS with a certificate the client checks, and it survives the
fuzzing from S8.

**Status: in progress (since 2026-11-01).** The order chosen with the user: what doesn't
need 64-bit kernels first (kernels stay 32-bit until booting on a cloud machine needs
them, step 4).

- [x] (1) Checking certificates, all in PHI, any kind of program:
  - `crypto.bignum`: numbers of up to 4096 bits as 16-bit limbs (every product and carry
    fits 32 bits), multiplied modulo an odd number by Montgomery's way (no division).
  - `crypto.rsa`: RSA signatures of a SHA-256 digest, PKCS #1 v1.5 and PSS.
  - `crypto.p256`: ECDSA on P-256 (Jacobian points, Shamir's way for u1 G + u2 Q, the key
    checked to be on the curve).
  - `crypto.x509`: DER, certificates (keys, signatures, names, dates as text,
    basicConstraints, subjectAltName; an unknown critical extension fails), a signature
    checked by its issuer's key, host names (wildcards, letter case, IPv4 addresses), and a
    chain followed back to a trusted root (issuers in order, CAs, path lengths).
  - `crypto.roots`: five RSA roots (ISRG Root X1, DigiCert Global Root G2 and Global
    Root CA, GlobalSign R3, USERTrust RSA), from Windows' store, with their fingerprints.
  - `net.tls` checks the server's certificate: its chain, the host, and CertificateVerify
    (P-256 or RSA-PSS over the transcript). A connection whose certificate doesn't check
    out fails (the error says why); `Tls.verified` is true when it did. ClientHello offers
    only what's checked (0x0403, 0x0804, 0x0401).
  - Tests `crypto_rsa`, `crypto_p256`, `crypto_x509` (a chain OpenSSL made, and changed
    ones that must fail), `crypto_roots` (Let's Encrypt's real R11, signed by ISRG Root X1
    with RSA-4096), each a library test on every system but 16-bit (`# runs only in 32 and
    64 bits`, new for tests too big for a 16-bit program); `net_http` now trusts the test
    server's own certificate (`TestWeb.root`) and checks it, and with nothing trusted
    fails to connect.
  - [x] P-384 and SHA-384 (Let's Encrypt's ECDSA chains, DigiCert's G3, Microsoft's and
    Azure's chains are signed with them). `crypto.sha512` (SHA-512 and SHA-384; 64-bit
    numbers, so not in 16-bit programs); the curve math moved out of `crypto.p256` into
    `crypto.ec`, where the curve is a parameter (an `EcCurve`), so `crypto.p384` is the
    same code with P-384's numbers, and a digest of any size works with either curve.
    `crypto.x509` reads P-384 keys and ecdsa-with-SHA384 and sha384WithRSAEncryption
    signatures; `net.tls13` checks a server's ecdsa_secp384r1_sha384 handshake signature,
    and a server's `TlsIdentity` can have a P-384 key. Tests `crypto_sha512` (FIPS 180-2's
    examples and .NET's digests), `crypto_p384` (OpenSSL's signatures and key files, and
    a P-256 key .NET used with SHA-384), `crypto_x509_p384` (a chain made the way Let's
    Encrypt's is), `tls_memory` (a P-384 server), and `tls_host_p384`: Windows' own TLS
    client checks a PHI server's P-384 chain and handshake signature. What it costs
    (`bench_tls`): signing 271 million instructions, checking 326 million, about 3.3 times
    P-256's; faster crypto waits for the optimizations, as before.
  - [x] dates: `OS.Now` (new: seconds since 1970, UTC, a `u32`) on every system: a kernel
    reads the CMOS clock (`lib/x86_32/clock.asm`), a PHI OS program asks it (system call
    43), Linux has `time` and Windows `GetSystemTimeAsFileTime`, all four agreeing with the
    host to the second (test `clock_now`). `X509.Chain` checks each certificate but the
    root against it, or against `X509.now` when that's set; the tests whose certificates
    are from 2026-11-02 set it to 2026-11-05, so they don't start failing when those
    expire (`crypto_x509` checks before and after a leaf's dates too).
  - Not yet: RSA-PSS with SHA-384 (TLS servers with RSA keys sign with SHA-256 when it's
    offered, as it is).

- [x] (2) server-side TLS, so logins go over TLS. Its foundations first (the user's
  "build good foundations"):
  - [x] ECDSA P-256 signing (`P256.Sign`) with RFC 6979's nonce, so it needs no random
    numbers, and key files (`P256.ReadPrivate`: SEC 1 and PKCS #8). It matches RFC 6979's
    own example and OpenSSL's deterministic signatures (test `crypto_p256_sign`). The DER
    reader moved out of `crypto.x509` into `crypto.der`.
  - [x] `net.tls13`: TLS 1.3 as an engine with no input or output of its own, each
    connection a `TlsSession` (keys, transcript, buffers), either side: the client as
    before, and a server that shows a `TlsIdentity` (its certificates, and its P-256 key,
    checked to be the certificate's) and signs the handshake. What arrives is given to
    it (`Room`, `Received`), and what it makes is taken from it (`Pending`, `Outgoing`,
    `Sent`), so a kernel's TCP, a program's sockets and a test's memory all drive it the
    same way. A side that fails sends the other an alert saying why; a client's records
    are encrypted from the ServerHello on, as RFC 8446 has it. `net.tls` is now the
    kernel's blocking client on top of it (the same calls as before). `crypto.random`'s
    blocks have `x64` versions. Test `tls_memory`: a client and a server joined in
    memory, on every system but 16-bit: the handshake, data both ways, 40000 bytes,
    close_notify, ChaCha20-Poly1305 and a wildcard name, and a wrong host, nothing trusted
    and a changed byte refused (with the alerts each side sees).
  - [x] `net.tlslink`: TLS on a TCP connection, driven without ever waiting (`Secure.Accept`
    or `Connect`, then `Pump` each time round a server's loop, `Read`, `Write`, `Close`),
    through whichever `Tcp` the program uses (a kernel's `net.tcp`, a program's
    `net.sockets`, which have the same calls). Test `net_tls`: a PHI server showing
    crypto_x509's leaf and three PHI clients (`net.tls`, checking the certificate) on a
    network that loses one frame in 40, each sending 20000 bytes it echoes, on both
    network cards.
  - [x] the lobby (`samples/mmo/`) takes logins over TLS only, showing the sample's own
    certificate (`samples/mmo/certs/`: a root and a P-256 certificate for
    `lobby.mmo.phi.test` it signs, whose keys are public, for the sample), and `phi load`'s
    clients check it (`--root FILE --host NAME`). 100 logins over TLS: every handshake done
    in 14.8 s (both sides in QEMU), every answer in order, and the world's tick never late.
    What a handshake costs (test `bench_tls`): signing (the server) 79 million
    instructions, checking a P-256 signature 99 million and an RSA-2048 one 40 million (the
    client checks one of each), X25519 34 million on each side: about 115 million on the
    lobby's side and 175 million on a client's. Faster crypto waits for the optimizations
    before a release.
  - [x] a check against another TLS 1.3 client: an input script's `tls PORT HOST ROOT TEXT`
    has QEMU forward the port, and the host's own client (.NET's SslStream: Windows'
    SChannel) connect to a PHI server, check its chain and host, and get a line echoed
    (test `tls_host_client`, on both network cards). So both sides of PHI's TLS have met
    another implementation: the client Windows' server (`net_http`), the server Windows'
    client.
- [x] (3) Disks a cloud machine has, behind one block layer:
  - `drivers.block`: whichever disk the machine has (virtio-blk, then NVMe, then IDE), as
    sectors from the start of the disk, with its size; `drivers.disk` (the FAT partition)
    now sits on it, so the file system, programs and saved files work on any of them.
  - `drivers.virtio`: what virtio devices share (the legacy PCI registers, turning one on,
    its queues), which `drivers.virtio_net` now uses too.
  - `drivers.virtio_blk` and `drivers.nvme` (any maker's NVMe disk, found by its PCI class
    with the new `Pci.FindClass`: an admin queue, Identify, one I/O queue pair, page
    lists for requests of more than two pages, the disk's own limit on a request). Both
    poll, and go through a buffer of their own, so a caller's buffer can be anywhere.
    `Ata` gained `Present` and `Sectors` (IDENTIFY).
  - `phi run` and `phi test` take `--disk virtio` or `--disk nvme`; a kernel test that uses
    the disk itself runs on all three (test `disk_block`: the size, sector 0, a request
    bigger than the drivers' buffers, a count the layer refuses, past the end; and
    `kernel32_files`, `kernel32_folders`, `kernel32_reboot`). Programs loaded from the disk
    were checked on each too.
  - Not yet: NVMe disks with 4096-byte sectors, and a disk whose registers are above 4 GB
    (out of a 32-bit kernel's reach: a 64-bit kernel, step 4, will have them).
- [ ] (4) booting on a cloud machine. Getting ready, with what can be checked here:
  - [x] a network's MTU: some clouds' networks carry less than Ethernet's 1500 bytes
    (Google Cloud's 1460) and say so by DHCP (option 26). `Net.mtu` takes it, and what IP
    sends and TCP's segments (the MSS it offers and takes) keep to it. The test network
    can act like such a cloud: `mtu N` drops bigger frames and `dhcp` answers DHCP itself
    (test `net_mtu`: three machines by DHCP on a 1400-byte network, 32 KB each way; with
    the MTU ignored, TCP stalls on the dropped frames).
- [ ] (5) the deployment guide

### Scenarios to write up next

Candidates, each a large project that tests a different side of PHI:

- **A game engine runtime:** rendering, loading assets, audio and input at 60 frames a
  second or more. Tests speed, SIMD and access to a GPU.
- **A game console OS:** boots on real hardware, runs games from storage, reads
  controllers. Tests drivers, USB, UEFI and real hardware.
- **Studio tools:** asset pipelines and build tools that run on Windows and Linux. Tests a
  hosted target (PHI programs that run outside PHI OS).

---

## Running on other systems

In progress: H1-H3 are done, so a PHI program builds as a 32-bit console program for Linux
and Windows (`phi build hello.phi --target linux`), and the tests run on both. This is the
plan for PHI programs running on Linux, Windows and macOS as ordinary programs, as well as
on PHI OS. It comes up in every
AAA scenario (studio tools, build machines, editors and test machines all run on those
systems), and it's most of the distance between PHI and a language a studio could choose.

### What stays the same

- PHI OS, the kernels, the samples and QEMU stay as they are. Running on another system
  is another target, not a replacement.
- The same program builds for PHI OS and for a host, unchanged.
- What a program can do is what it already asks PHI OS for. A user program
  (`phi.Name:Program`) never touches hardware: everything it does outside itself is one of
  about 60 system calls (`OS.ReadFile`, `OS.WriteFile`, `OS.Grow` for its heap, `OS.GetTicks`,
  `OS.Sleep`, the UDP and TCP calls, windows, sound), in one small runtime folder,
  `lib/x86_32_user/`. A host target is a second version of that folder, answering each call
  with that system's own, and a different file around the code (an executable instead of a
  disk image).

### Decided

- **How a program says where it runs:** a build flag, `--target linux` (or `windows`, or
  `phios`, the default), on `phi build`, `run`, `check` and `test`. The source doesn't
  change.
- **Which modules work where:** found by building, not marked by hand. A module that can't
  work in a program on that system (it uses ports, interrupts, or a kernel's calls) fails to
  bind there, and its errors become one at the `use` that brought it in: `drivers.ata doesn't
  work in a Linux program (lib/phi/drivers/ata.phi:37: user programs can't use ports ...)`.
  PHI OS programs get the same message for a kernel's module. So a module works wherever it
  builds, with nothing to keep up to date.

### Phases

| # | Phase | Size | Demo at the end |
|---|---|---|---|
| H1 | Linux, 32-bit, console | Small | `phi build hello.phi --target linux` makes a program that runs on Linux |
| H2 | Tests on the host | Small | `phi test --target linux` runs the program tests without QEMU |
| H3 | Windows, 32-bit, console | Medium | the same program as a Windows `.exe` |
| H4 | 64-bit on the hosts | Large | Linux and Windows 64-bit programs (with S10), and macOS |
| H5 | The network and threads | Medium | the mmo server's world and lobby running as Linux programs |
| H6 | The runtime in PHI | Medium | the hosts' sockets and threads written once, in PHI, with a little assembly per system |
| H7 | Windows and graphics | Large | a PHI game window on Windows and Linux |
| H8 | Tools for a host | Medium | a debugger steps through PHI lines; an editor shows PHI's errors |

#### Phase H1: Linux, 32-bit, console

1. **The executable:** an ELF file. NASM can write the ELF header itself, so no linker is
   needed at first.
2. **A runtime for Linux:** `lib/x86_32_linux/`, with the same calls as
   `lib/x86_32_user/` made as Linux system calls: `log` and `ask` through `write` and
   `read`, the heap through `mmap`, files through `open`, `read` and `write`, time through
   `clock_gettime` and `nanosleep`, and `exit`. Linux system calls are traps with their
   arguments in registers, as PHI OS's are, so most are a line or two each.
3. **The target flag,** and an error for a module that only works on PHI OS.
4. **Calls with no Linux equivalent** (`OS.PutCell`, the PHI OS process calls) are errors
   for this target, not something that fails when it runs.

**Done when:** the programs among the tests (`phi.Name:Program`, and `:Library` tests built
as programs) build for Linux and give the same output as on PHI OS.

**Status: done.** `lib/x86_32_linux/` answers the program calls with Linux system calls:
`log` to standard output (a statement at a time, as on PHI OS) and `debug` to standard error,
`ask` and `OS.ReadKey` from standard input (Enter is 13, as on PHI OS), files and folders,
`OS.Arguments`, time, `OS.Grow` (the heap) through `brk`, memory through `sysinfo`, and
`OS.ClearScreen` as a terminal code. The generator writes the ELF header itself, so the
executable is NASM's flat output with no linker. Not there yet: `OS.ListFiles` and
`OS.ListFolder` (a host's long names don't fit `FileInfo`'s 8.3 name), and what has no
equivalent (`OS.PutCell`, `OS.IsKeyDown`, `OS.Reboot`, the PHI OS process calls); each is an
error when the program is built. Sound, windows, the network and mailboxes come in H5 and
H7. `phi run --target linux` runs the program under WSL on Windows.

#### Phase H2: Tests on the host

1. **`phi test --target linux`** runs each test as a Linux program and compares its output,
   as it now does with QEMU's serial port. On Windows it runs under WSL.
2. **Both targets in the suite:** a test runs on PHI OS, and on the host as well when it
   can, so the two can't drift apart.

**Done when:** the program tests run on the host in a fraction of the time QEMU takes, and a
difference between the two targets fails the run.

**Status: done.** `phi test` runs each test that can be a program (`:Library` tests, built
with a `:Program` wrapper, and `:Program` tests) on Linux and Windows too, where the computer
can run them, after PHI OS: 46 of the tests, which pass on all three. A test is left to PHI OS
when it doesn't build for the system or needs QEMU (an input script, a web server, sound,
other machines); `pointers_structs` builds but writes to the VGA buffer, so it says
`# runs only on PHI OS`. `phi test --target linux` runs only those, in about 8 seconds under
WSL. `host_program` tests the calls a program makes of its system (files, folders, the heap,
time, input), on Linux and Windows. Building them found that a PHI OS program couldn't use
`has` on text (`lib/x86_32_user/` didn't have `str_has.asm`), now fixed.

#### Phase H3: Windows, 32-bit, console

1. **The executable:** a PE file with an import table, since Windows has no fixed system
   call numbers: programs call `kernel32.dll` (`WriteFile`, `ReadFile`, `VirtualAlloc`,
   `CreateFileW`, `GetTickCount64`, `Sleep`, `ExitProcess`).
2. **Calling those functions** the way Windows does (stdcall). This is the first piece of
   calling C code, which C compatibility builds on later.
3. **`lib/x86_32_windows/`,** the runtime, as for Linux. Text is converted to UTF-16 for the
   file and console calls that need it.

**Done when:** the same programs as H1 build as Windows `.exe` files and give the same output.

**Status: done.** `lib/x86_32_windows/` makes the same calls through `kernel32.dll`:
`imports.asm` is the import table (the functions by name; Windows fills in their addresses),
and each call is `call [phi_imp_Name]` with stdcall. The generator writes the PE headers: one
section, with section and file alignment both a page, so no linker is needed here either.
The heap is 128 MB of addresses reserved at the first `OS.Grow` and committed as it grows;
paths are converted to UTF-16 for `CreateFileW` and the other `W` functions. Console text is
written as bytes, not UTF-16 (PHI's text is bytes; the console shows ASCII the same either
way). `OS.KeyAvailable` asks a pipe how much is waiting, and a console whether it has any
input event, keys or not. The tests give the same output as on Linux and PHI OS.

#### Phase H4: 64-bit on the hosts

Comes with S10 (the x86-64 backend). macOS only runs 64-bit programs, many Linux
distributions don't install 32-bit support any more, and Windows on ARM emulates it, so
32-bit is where hosting starts, not where it stays.

1. **The x86-64 backend** from S10, and the System V (Linux, macOS) and Microsoft x64
   calling conventions for system functions.
2. **ELF64, PE32+ and Mach-O** executables.
3. **macOS** (`lib/x86_64_macos/`), and ARM64 once there's an ARM backend (Apple machines,
   Windows on ARM, phones and consoles).

**Done when:** the H1 programs build and pass as 64-bit programs on Linux, Windows and macOS.

**Status: in progress.** 64-bit Linux and Windows programs (`--target linux64`,
`--target windows64`) are done with S10's (a) and (b): ELF64 and PE32+, the System V
system calls and the Microsoft x64 calling convention, and every program test passing on
both. macOS is next after the 64-bit kernels; the user will test it on their Mac.

#### Phase H5: The network and threads

1. **Sockets:** the UDP and TCP calls answered by BSD sockets on Linux and macOS and
   Winsock on Windows. PHI OS keeps its own network stack.
2. **Threads:** `atomic` and `locked` work across threads made with pthreads or
   `CreateThread`, with the same rules S9 sets on PHI OS. PHI's memory model is written
   down in `docs/language.md`.

**Done when:** `samples/mmo/`'s world and lobby run as Linux programs, with clients on PHI
OS or on the host, and `phi load` holds the same tick rate.

**Status: done (2026-11-03).** The world and the lobby run as Linux and Windows programs,
and `phi load --target linux` (or `windows`) plays them with clients that are programs
too, all on this computer: 500 players in the world for a minute at 20 ticks a second,
none late (the longest 4 ms on Linux, 5 on Windows), and 500 TLS logins in the lobby,
30,000 lines answered in order, 500 quits, on both. The same clients still play the
kernels on PHI OS (500 logins; 300 players, none late).
- [x] (1) Sockets on Linux and Windows, 32 and 64 bits: `net.sockets`' calls (`OS.UdpOpen`
  to `OS.TcpStopListening`, the 15 that net.calls answers on PHI OS) in each system's
  `sockets.asm`: Linux's socket system calls (non-blocking, `MSG_DONTWAIT`), and Winsock
  (`ws2_32.dll`, a second DLL in every Windows program's import table, started the first
  time it's needed). Each call never waits, as on PHI OS. A program's sockets are checked
  as on PHI OS: on Linux a socket is its file descriptor, kept with its kind (UDP,
  connection, listener), so a wrong number (standard output, say) is refused; on Windows
  it's a slot holding Winsock's socket, whose handles can be any number. `Net.Address` is
  the address the machine would reach the internet from (or 127.0.0.1). Test
  `sockets_program` (UDP and TCP to itself over 127.0.0.1) on all four.
  - Found on the way: on Windows, `getpeername` works on a connection that is still
    connecting, so a connection is "connecting" until `select` says it can be written to
    (or failed). And Windows asks the first time a new program opens a socket whether it
    may use the network (its firewall); until someone answers, UDP sockets fail.
- [x] (the demo, first part) `samples/mmo/`'s world and lobby build as Linux and Windows
  programs, unchanged. A program in a rootfs folder built on its own now finds modules
  next to its kernel, as it does when the kernel builds it (`use zone;`). Tried by hand:
  the world on Windows with a Python player, and on Linux with a PHI one (a Linux program
  too): it joins, moves, gets 20 STATEs a second, and is back where it was after a
  restart; the lobby on Windows with Python's TLS client (OpenSSL), which checks its
  certificate: TLS 1.3, a login, `say`, the account kept across a restart, a wrong
  password refused.
  - [x] (2026-11-03) `phi load` against a world on the host. `phi load FILE --target
    linux` takes a program (`server.rootfs/world.phi` or `lobby.phi`), runs it here in a
    folder of its own with copies of the files beside it (the lobby's certificate), as a
    fresh disk has them, and runs the clients as programs (a hundred connections each) to
    127.0.0.1. The clients (`lib/phi/test/load.phi`, `samples/mmo/load_players.phi`) are
    now `:Library` with no network of their own: phi load adds the one for where they run
    (`net.tcp` and `net.udp` in a kernel, `net.sockets` in a program, which got `Net.Start`
    and `Net.Wait` so the calls are the same), and the class that makes them a kernel or a
    program. Found on the way:
    - `Tcp.Waiting` on Linux and Windows said a connection had closed when data came in
      between its two looks (the count, then a peek): about 17 of 500 lobby connections
      died mid-run that way. Data the peek finds is now data waiting; an interrupted call
      on Linux is "not yet", as in `Send` and `Read`.
    - On Windows, `OS.Sleep: 1` slept up to 15.6 ms and `OS.GetTicks` stepped by 15.6 ms
      (the timer's usual step), so the world's ticks looked late, or took "0 ms". A program
      now asks for 1 ms steps (`timeBeginPeriod`, from `winmm.dll`, a third DLL in its
      import table) and its clock is `timeGetTime`: the same milliseconds as elsewhere.
    - A module's classes start in the order they're used, so the default client's file
      has the network's `use` lines before `use test.load;` (its body talks to the
      network as it starts). And on a host the build goes next to a rootfs folder, not in
      it (a kernel's disk is made from it).
- [ ] (2) threads in programs, on every system (the user's choice, 2026-11-02: PHI OS
  programs too, so a program behaves the same everywhere), with the kernel's calls
  (`Threads.Start`, `Wait`, `Done`, as `proc.threads` has them). The parts:
  - [x] (a) PHI OS: several tasks in one program (2026-11-02). Done as planned:
    `phi_task_process`, `OS.CreateUserThread`, system calls 11 (a thread's end) and 44 to
    46, `user.threads` and `lib/x86_32_user/threads.asm`, the program's end ending every
    task of it, its memory back once no core runs any. `MAX_TASKS` is 64 (and the tables
    sized by processes in `proc.process`, `net.calls`, `proc.mail`, `gui.surfaces`,
    `proc.threads`). Test `kernel32_program_threads` (four threads on one core and on
    every core, atomic and locked across them; a thread's uncaught error ends the
    program; every frame back). The heap and catching in several threads at once wait
    for (b) and (c).
  - (a, as planned) PHI OS: several tasks in one program. Each task knows its program
    (`phi_task_process`); a thread is a task with the program's page directory, its own
    kernel stack and a stack the program gives it from its heap. System calls start one,
    end one, say whether one is done and give its number back. What belongs to a
    program (sockets, files' checks, mailboxes, its heap's end) goes by its program, not
    its task. When a program ends (`exit`, an error nothing catches, a fault, a kill) all
    its threads end, and its memory goes back once no core runs any of them. More tasks
    than 16 (`MAX_TASKS`), since threads take them too.
  - [x] (b) A block of its own for each thread (2026-11-02), as each core has in a kernel:
    its innermost catch, its methods' records and the error it throws
    (`PHI_THREAD_...` in each `throw.asm`; `phi_main_thread` for the first thread, and a
    new thread's at the top of its stack). Each system's runtime has `phi_thread_setup`
    and `phi_thread_block`, and the code generator reaches the block as the runtime does:
    on PHI OS through `gs`, a descriptor of the task's own (`phi_gdt_tasks`, one for each
    task, its base set by system call 12, so the scheduler needn't change anything); on
    32-bit Linux through `gs` too (`set_thread_area`); on 64-bit Linux through `fs`
    (`arch_prctl`); on Windows, whose `fs` and `gs` are its own, from a TLS slot in the
    TEB (`TlsAlloc`, read at `fs:0xE10` or `gs:0x1480`). Test `kernel32_program_threads`:
    four threads on every core each catching 600 errors at once (throws with texts of
    their own, indexes out of range, divisions by zero), none mixed up.
  - (b, as planned) A block of its own for each thread, as each core has in a kernel: a program's
    catches and methods' records (`phi_catch_top`, `phi_unwind_top`) are one per thread.
    On PHI OS a user segment whose base the scheduler sets for the task it runs; on Linux
    the thread area (32-bit, `gs`) or `fs` (64-bit); on Windows a TLS slot.
  - [x] (c) (2026-11-02) Threads on the hosts: `OS.ThreadStart`, `ThreadDone` and
    `ThreadFree` in each host's `threads.asm`: Linux's `clone` (the program's memory,
    files and signal handlers shared, the thread's slot cleared by Linux when it has gone)
    and Windows' `CreateThread` (on the stacks Windows gives them; finished once the
    handle is signaled). The program's heap takes a lock; each `log` takes a lock in
    programs too (the line flushed inside it), told the thread by its block, so the first
    thread's block is set up in every program now (each `threads.asm`'s
    `phi_threads_init`). A catch keeps how deep its thread (or core, in a kernel) was in
    the log's lock, and a throw back to it puts that back: before, an error in a log's
    value kept the lock, which hung every other thread, and in a kernel with cores left
    interrupts off. Linux's runtimes keep their scratch for system calls (the time, stat,
    poll, sysinfo) on the stack. Tests `threads_program` (sums, catching, the heap and
    whole lines from four threads at once, on all four hosts) and the heap and log in
    `kernel32_program_threads`.
    - Not yet: Windows' file calls from several threads at once (they share the buffers
      their paths are made wide in; fixed in H6), and `ask` or `OS.ReadKey` from several at once.
  - (c, as planned) The program's side: `user.threads` (`Threads`, the same calls) on PHI OS, and
    the host runtimes: a thread is `clone` on Linux (no C library) and `CreateThread` on
    Windows. What the runtimes keep in one place for everyone (the line `log` is building,
    scratch for system calls) becomes the thread's own or takes a lock, and the program's
    heap (`memory.program_heap`, which assumed it ran alone) takes one.
  - [x] (d) The class-variable warning (S9) for the methods a program's threads run: it
    already finds them (by `Threads.Start`, which `user.threads` has too); a unit test
    says so.
  - [x] (e) Tests on all five systems (`threads_program`, `kernel32_program_threads`): threads summing their parts, atomic and locked
    across them, the heap from several threads, catching errors in each.
- [x] (3) PHI's memory model in `docs/language.md` (2026-11-02): what a `locked` block
  sees (everything the lock's last holder wrote), what an `atomic` step orders, what
  plain reads and writes of class variables give (whole when aligned, each thread's in
  its order, nothing more), wider values, and `volatile`; the same on every system and
  kept so on a weaker one later. Every class variable now starts on a word in 32- and
  64-bit code, so that holds (and atomic steps don't straddle cache lines).

#### Phase H6: The runtime in PHI

Goal: what each system's runtime does that is logic rather than machine detail is written
once, in PHI, so a feature that reaches outside a program (files, the network, threads,
and next windows, sound and the GPU in H7) costs one PHI module, not an assembly file
for each system.

Why now: today every call a program makes of its system is hand-written assembly in each
host's folder (`lib/x86_32_linux/`, `lib/x86_64_linux/`, `lib/x86_32_windows/`,
`lib/x86_64_windows/`, and `lib/x86_32_user/` for PHI OS). H5's sockets were about 15
routines for each of the four hosts, and its threads five versions of one job, each with
its own registers and rules, and mistakes that only show when they run. H7 adds windows,
sound and the GPU, and macOS (H4) a fifth host, so the cost is about to multiply. And H7
needs half of this anyway: calling Vulkan, Direct3D or a window system is calling an
outside function, best built once as a general piece (good foundations first). No C is
involved: PHI makes the system calls, and calls other libraries' functions the way they
expect, itself. It's also the first half of C compatibility (calling C functions), without
starting that phase.

0. **Seeing where a run stopped** (done 2026-11-02, first, because moving the runtime out of
   assembly will make exactly the hangs and crashes that were hardest to find in H5, which
   showed only "timed out after 10s"). A test that runs out of time now says where each
   thread was and the calls that led there, as PHI lines and library routines:
   ```
   where it was when it was stopped:
     thread 1: OS_Sleep (system.asm), in system call 162 (nanosleep)
         called from Threads.Wait, lib/phi/user/threads.phi:48: call OS.Sleep: 1;
         called from HangThreads (class body), hang_threads.phi:11: call Threads.Wait: t;
     thread 2: HangThreads.Waiter, hang_threads.phi:15: locked guard
   ```
   On PHI OS it's each CPU (read through a QEMU monitor), kernel or program; on Windows
   each thread (stopped and read with `GetThreadContext`); on Linux each thread (stopped
   with SIGSTOP and read through `/proc`). The calls are the values on the thread's stack
   that are where one of the program's calls returns to (from NASM's listing), so they're
   found through assembly and system code too. A method written out in its caller is
   named as such, with its own file. A test that fails runs once more, and one that passes
   then is listed at the end: the computer was busy, or a race is worth a look
   (`--no-retry` turns it off). The first full run with it showed what H5's flaky Windows
   timeouts were: nothing was running from the `.exe` when time ran out, so it hadn't
   started (Windows checks each new `.exe` first), and a timeout like that now says so. Not yet: a PHI OS program's threads that aren't on a CPU
   at that moment (it would mean reading the kernel's task table), and 16-bit programs'
   calls.

1. **System calls in the language** (done 2026-11-03: `sys.Name` declarations and the
   system words `:Linux`, `:Linux32`, `:Linux64`, `:Windows`, `:PhiOS`, as decided below;
   test `sys_calls_program` on both Linuxes, and `SystemModuleTests`). As planned: A call that takes a system call's number and up to
   six arguments and gives its result, which the code generator writes for the target:
   `int 0x80` with ebx, ecx, edx, esi, edi, ebp (PHI OS, 32-bit Linux), or `syscall` with
   rdi, rsi, rdx, r10, r8, r9 (64-bit Linux, macOS later). Linux's answer comes back as it
   is (-errno when it fails). Only in programs (a kernel has no one to ask). First users:
   the Linux runtimes' simplest calls (time, sleep, files). (Not yet: moving those needs
   `OS.Sleep` and the rest answered by a PHI module rather than an assembly routine,
   which is step 3's to build; step 1 is the language and its tests.)
2. **Outside functions** (done 2026-11-03, on Windows: `use 'kernel32.dll' as Kernel32;`
   and `Kernel32.Name: type name ...: result;`, as decided below, with the result type the
   user chose for both, `: type` at the end, as method types write it; test
   `outside_functions_program` on 32- and 64-bit Windows, including user32.dll, six values
   and a pointer back, and `SystemModuleTests`. Not yet: floats, and 64-bit numbers in a
   32-bit program). As planned: A declaration of a function in another library (on Windows, a
   DLL's: `CreateThread` in kernel32.dll, `connect` in ws2_32.dll), with its arguments;
   the compiler adds it to the program's import table and calls it as that system's
   functions are called (stdcall in 32 bits; in 64, the first four in rcx, rdx, r8 and
   r9, 32 bytes of room, the stack aligned). The Windows runtimes move onto it. On Linux
   and macOS a library's functions need the program to be linked against it at load
   time (an ELF interpreter, or the macOS loader): that part waits for H7, when a window
   system or the GPU first needs it.
3. **The runtimes' logic in PHI.** (Under way. Time is done, 2026-11-03: `OS.Sleep`,
   `OS.GetTicks`, `OS.Now` and `OS.Yield` in a Linux or Windows program are answered by
   `lib/phi/host/time_linux32.phi`, `time_linux64.phi` and `time_windows.phi`, through
   their own system calls and Windows' functions; a built-in names the method that does it
   on a host (`OnHost = "HostTime.Sleep"`), as `InKernel` does for a kernel, and the
   compiler gives every host program those modules, first, each system keeping its own.
   The four routines are gone from the hosts' assembly (about 220 lines), with `phi_now`,
   the start time and four Windows imports; test `time_program`, and the mmo world under
   `phi load` holds its tick on both. Files are done too, 2026-11-03: `OS.ReadFile`,
   `ReadFileAt`, `WriteFile`, `AppendFile`, `FileSize`, `DeleteFile`, `MakeFolder`,
   `DeleteFolder` and `Rename` are `files_linux.phi` (one module for both widths, on
   `linux32.phi`'s and `linux64.phi`'s numbers and the one layout that differs, stat's)
   and `files_windows.phi`, whose UTF-16 paths are on each call's own stack: H5's gap,
   Windows' file calls from several threads at once, is closed (test
   `files_threads_program`: four threads at once on all four hosts). About 860 more lines
   of assembly gone, with seven Windows imports. A system call or outside function may be
   declared by two modules if they declare it the same way (a program's own and
   lib/phi/host's); differently is an error. And a program test that builds for no system
   now fails, saying why, rather than passing by running nowhere (that hid a clash).
   The network is done too, 2026-11-03: `OS.NetAddress` and `OS.UdpOpen` to
   `OS.TcpStopListening` are `net.phi` (what the systems share: the slots, the kinds and
   the states of a connection, the addresses, a lock around the slots that the assembly
   didn't have) on `net_linux.phi` and `net_windows.phi` (a `NetSystem` of a call or two
   each); a socket number on Linux is a slot now, as it was on Windows. The four
   `sockets.asm` are gone (about 2,500 lines), with ws2_32.dll's list in the import
   table. `sockets_program` on all four, and `phi load` holds on both systems: 500 TLS
   logins, 300 players with none late. A program gets only the parts it uses (a built-in it
   calls brings its part's modules, as `new` brings the heap), so one that has no network
   doesn't import or start Winsock. Threads are done too, 2026-11-03: `OS.ThreadStart`,
   `ThreadDone` and `ThreadFree` are `threads.phi` (a thread's number, under a lock the
   assembly didn't have, and the frame its first instructions find) on `threads_linux.phi`
   and `threads_windows.phi`. Each `threads.asm` keeps what has to be assembly: the block's
   setup, a thread's first instructions, and on Linux `clone` itself (`OS.ThreadClone`: the
   new thread comes back from it on another stack), on Windows only where those
   instructions are (`OS.ThreadEntry`, for `CreateThread`). Test
   `threads_starting_program`: threads starting threads at once, on all four hosts.) A module for each part (the network, threads, files,
   time) holding what every system shares (checking a socket's kind, keeping thread
   slots, the waiting and retrying), on top of a small module per system of constants and
   calls (Linux's numbers, Windows' functions). Moved a part at a time, the assembly
   version staying until the PHI one passes the same tests on every system. Windows' file
   calls from several threads at once (H5's gap) are fixed on the way, once for all.
4. **What stays assembly,** a few dozen lines for each system: where a program starts
   (before any PHI code can run), a new thread's first instructions (on its new stack),
   the handlers that turn a CPU fault into a throw (they change the stopped code's
   registers), loading the thread's block (`gs`, `fs`, the TLS slot), and `throw`'s jump
   back to its catch.

Along the way, smaller things that make building PHI easier:
- **Errors that say what to write instead** for the mistakes that cost a build each in H5:
  `(asm.X)` as a value (write `call x is asm.X`), several values for one text without
  parentheses (write `('W' me '.LOG')`), as a keyword used as a name already does.
- **`phi check` for every system at once,** so a change that breaks one system's module
  (step 3) shows in seconds, not after a whole test run.
- **A warning when `phi` is older than `lib/`** (the library is built into it, so a
  change there needs `dotnet build` first, and a stale one tests the old library).
- **`docs/language.md` in a file for each part** (threads, text, errors...), with an index:
  at 2,700 lines it's read in pieces, so a rule in another piece can be missed.

**Done when:** the hosts' sockets, threads and file calls are PHI modules shared by Linux
and Windows (32 and 64 bits), with assembly only for the pieces in (4); every test passes
on all five systems as before; and a new call a program makes of its system can be added
in PHI alone.

**Status: done (2026-11-03).** System calls and outside functions are in the language, and
time, files, the network and threads on the hosts are PHI modules in `lib/phi/host/`, about
4,000 lines of assembly lighter: 639 tests on all five systems, and `phi load` holds on
Linux and Windows. The network and threads are one module for both systems over a small
one for each; files are a module for each system (one for both widths of each), since Linux
and Windows share almost nothing there beyond what a call means. A new call can be added in
PHI alone (`sys_calls_program`, `outside_functions_program` do).
- Still assembly, beyond (4), and could move when something needs it: `OS.CurrentTask`,
  `OS.Grow` (under every program's heap), `OS.TotalMemory` and `OS.FreeMemory`; and what's
  tied to assembly the code generator calls directly: `log`'s buffer (`phi_print`,
  `phi_flush`), reading lines and keys (`ask` and `OS.ReadKey` share a buffer), and
  `OS.ClearScreen`, which flushes the log first. PHI OS programs keep their assembly: their
  calls are the kernel's, one instruction each.
- Not yet: floats, and 64-bit numbers in a 32-bit program, to and from outside functions;
  and libraries on Linux and macOS, loaded with the program. H7 needs these. (Callbacks and
  C's struct layout came first in H7: `addr foreign`, `struct.Name foreign`.)
- Open: `sockets_program` on 64-bit Windows ended with -1 once in a full run (2026-11-03,
  before the network moved to PHI) and couldn't be made to again; a test that passes only
  on its second run now keeps its first run's output (`NAME.first.actual`).
- A pointer and a number in 64-bit code now warn (the user's call, 2026-11-03): a pointer in
  a 32-bit number, or a number that isn't constant in a pointer, unless `as` says it's meant.
  Its first sweep found `memory.pool` keeping addresses in u32s, and the lobby too. And the
  lobby on 64-bit Windows, whose heap is past 4 GB on purpose, found a compiler bug no warning
  could: an array in a struct reached through a pointer and indexed by a variable lost its
  address's top half (`add ebx, eax`). Test `high_addresses_program`. 64-bit Linux's heap is
  low (brk), which hides such bugs; worth moving past 4 GB too, as Windows' is.

Decided (2026-11-02): **a module names its system where it names where it runs.** The
word after the colon already says that (`:Kernel` only in kernels, `:Program` only in
programs, `:Library` anywhere); the systems are narrower kinds of `:Program`:

```phi
phi.Sockets:Linux    { ... }     # built only for Linux programs, 32 and 64 bits
phi.Sockets:Windows  { ... }     # built only for Windows programs, 32 and 64 bits
```

- The words: `:Linux`, `:Windows`, `:PhiOS` (PHI OS programs only), `:MacOS` when it
  comes; `:Linux32` and `:Linux64` for one width only (Linux's system call numbers differ
  completely between them; Windows' functions don't, so it has no width words).
- One name, a version per system, each in its own file. Callers write `Sockets.Send: ...`
  and never say which system; the compiler builds the version for the target and skips
  the rest. A name can't have both a `:Linux` and a `:Linux64` version (which to build
  would be unclear); that's an error.
- Using a name with no version for the target is an error where it's used ("Sockets has
  no version for macOS"), when the program is built, never a missing piece when it runs.
- A system's word implies a program: nothing PHI builds runs as a Linux or Windows
  kernel, so the module never says both.
- Raw system calls are allowed only in `:Linux`, `:Linux32`, `:Linux64`, `:PhiOS` (and
  later `:MacOS`) modules, and outside functions only in `:Windows` ones (and later where
  H7 loads libraries), so the code that is one system's alone stays in the files marked
  for it, and everything else is the same everywhere.
- H7's class for a window on the screen is `Window` (singular), not to be confused with
  `:Windows`.

Decided (2026-11-02): **a system call is a `sys.Name` declared once with its number, and
used by name,** as `asm.Name` blocks are:

```phi
phi.Sockets:Linux64
{
	[Open: ...
		call fd is sys.Socket: 2 1 0;      # used by name, like asm.Name
	;]
}

sys.Socket: 41  int domain int type int protocol;   # declared once, with its number
sys.Close:  3   int fd;
```

- `sys` becomes a reserved word, beside `asm`, `x64` and `arm` (no `.phi` file uses it).
- The word after the dot is the programmer's name for the call, as after `asm.`; the
  number is written only in the declaration, so a `:Linux32` and a `:Linux64` version of
  a module differ in their declarations and not in the code that uses them.
- The declaration lists the arguments' types, and the compiler checks every use against
  them; the answer comes back as the system gives it (on Linux, -errno when it fails).
- Allowed only in the modules for systems with system calls (see above); `sys.Name` in a
  `:Windows`, `:Program` or `:Library` module is an error.

Decided (2026-11-02): **an outside library is brought in with `use`, under a class-like
name, and each function used is declared once with its arguments:**

```phi
phi.Threads:Windows
{
	[Start: ...
		call h is Kernel32.CreateThread: 0 0 work value 0 0;
	;]
}

use 'kernel32.dll' as Kernel32;
Kernel32.CreateThread: ptr<u8> attributes u32 stack ptr<u8> start ptr<u8> value u32 flags ptr<u32> id;
Kernel32.CloseHandle:  ptr<u8> handle;
```

- `use`, PHI's word for bringing code in, with the file's name quoted (`use kernel32.dll`
  would be read as a PHI file, `kernel32/dll.phi`); `as` is already a reserved word.
- The name after `as` is capitalized and reads as a class does (`Kernel32.CreateThread:`
  like `OS.Now` or `Sockets.Send:`); a lowercase name would look like a variable's field.
- Each function is declared once, as a `sys.Name` is but without a number, because a
  library lists only its functions' names: the compiler needs the arguments to call it
  correctly (in 32 bits the function removes its own arguments, so a wrong count breaks
  the stack; in 64, floats go in other registers than integers), and checks every use
  against them. The compiler adds the library and its functions to the program's import
  table.
- Allowed only in `:Windows` modules for now; Linux and macOS libraries use the same
  form when H7 first loads one.

#### Phase H7: Windows and graphics

What it needed of the language first, done 2026-11-03 (the user's word, `foreign`, chosen
because `from` is a name on 168 lines today and reserved words stay reserved):
- [x] `addr foreign Name`: where outside code calls a PHI method (test `foreign_program`:
  Windows' `EnumWindows` calling PHI, stopping early, in 32 and 64 bits).
- [x] `struct.Name foreign`: C's layout, in 32 and 64 bits (a real `MSG` and `SYSTEM_INFO`
  read back from Windows). PHI's own structs stay packed.
- [ ] Floats to and from outside functions (the GPU's); libraries on Linux and macOS.

1. **A window, drawing and input** behind the calls `gui.*` and `gfx.*` already use: Win32 on
   Windows, Wayland or X11 on Linux, Cocoa on macOS, or one library that covers all three
   (that needs C compatibility, or H6's outside functions).
   - [x] Windows, 32 and 64 bits (2026-11-03): `lib/phi/host/window_windows.phi` answers
     `OS.SurfaceOpen`, `SurfacePresent`, `NextEvent`, `CopyFont`, `SetWindowTitle`,
     `SetResizable`, `WindowFocused` and `IsKeyDown`. `samples/desktop.rootfs/`'s `sketch`,
     `notes`, `form` and `clock` build unchanged. `form` (64-bit) and `notes` (32-bit) have
     been run in real windows and looked at, with real keystrokes in `notes` and the close
     button on both; `sketch` and `clock` only built so far, and the mouse only through the
     test's posted messages. The font is Windows'
     Consolas at 8x16, not smoothed, for code page 437, drawn into `Font.glyphs` when a window
     opens (PHI OS copies the VGA card's; no font data to carry). Test `window_program`: a
     hidden window, with messages posted to it and its pixels read back, so nothing pops up.
     Not yet: `OS.OpenWindow` gives -1 (starting a program by a path on its disk is PHI OS's
     way), and a window's messages come only to the thread that made it.
   - [x] Linux, 32 and 64 bits (2026-11-03): `lib/phi/host/window_linux.phi` answers the same
     calls by speaking X11's protocol over its socket (`/tmp/.X11-unix/X0`), so it needs no
     library, on `sys.` calls alone. Pixels go to a pixmap the server keeps, copied to the
     window when they change or it needs painting, in pieces as long as the server takes. X
     has no double click (two presses close enough are one) and says a held key came up and
     went down again as it repeats (passed over, so a game sees it held). The font is the
     server's own, drawn a character at a time and read back; only ASCII, since its other
     codes aren't code page 437's. No authentication yet (a server that wants a cookie
     refuses). `window_program` runs on all four hosts, hidden, on `WindowTest` (a small module
     for each system that plays the user), so on Linux it needs an X server (WSLg has one);
     without one it fails. All the samples build for Linux; `paddles` has been played on it
     (WSLg, 64 bits), by the user.
   - [x] A game on `gui.app`: `samples/desktop.rootfs/paddles.phi` (held keys, 60 frames a
     second), the same program on PHI OS's desktop, Windows and Linux. It builds for all
     five, and the user has played it on 64-bit Windows and Linux (2026-11-03). That found
     what the hidden test couldn't: making a window fixed-size hid it on Windows, as its
     style and whether it's showing are one word. `window_program` now shows its window off
     the screen to check. Not played yet: the 32-bit builds, and PHI OS's desktop.
   - [ ] macOS, with H4's.
2. **Sound** behind `audio.*`'s calls: done (2026-11-03), with nothing new to learn: programs
   already had `OS.SoundWrite`, `SoundRate`, `SoundQueued`, `SoundStop` and `SoundVolume`.
   - [x] `Sound` is a library on those calls, so it works in any program, not only a kernel.
     (A kernel's calls go to `Ac97` and `Fat`, which it names with `use`.)
   - [x] Windows: winmm's `waveOut` (`lib/phi/host/sound_windows.phi`).
   - [x] Linux: PulseAudio's native protocol over its socket, with no library, 32 and 64 bits
     (`sound_linux.phi`). Tried against WSLg's PulseAudio only: not yet a desktop's, or
     PipeWire's stand-in for it, nor a server that wants its cookie.
   - [x] `sound_program` runs the same program at volume 0 on all four and they print the same;
     `sound_library` runs `Sound` in a PHI OS program. It's how long sound takes that they
     check, not how it sounds. `paddles` makes sounds now, for ears to check; not yet heard.
   - What's "still to play" on Linux is counted by the clock from when the server starts on
     it, as the server reads well ahead of what's heard, and WSLg's output misreports what it
     holds. A sound that's about to end can be up to the output's real latency early.
   - Not done: a machine with no sound device fails `sound_program` rather than skipping it,
     the same gap as `window_program` has with no X server; sound from another machine (PULSE_SERVER as
     `tcp:`); recording.
3. **The GPU, on hold** until everything else is done (the user's choice, 2026-11-03; ask them
   again then). A game needs it: Vulkan, Direct3D 12 or Metal. Large, and depends on
   calling C functions (H6's outside functions, and on Linux, loading a library).

**Done when:** a PHI game runs in a window on Windows and Linux. (The arcade sample is a
16-bit program that writes to the VGA card, so it isn't the one; `paddles` is, and step 1 is
done: played on both, 2026-11-03.) Sound is built too, and the rest of H7, the GPU, is on hold.

#### Phase H8: Tools for a host

1. **Debug information** (DWARF on Linux, PDB on Windows), so a debugger steps through PHI
   lines and shows variables, not only the generated assembly.
   - [x] Windows, 32 and 64 bits (2026-11-03): `--debug` writes a `.pdb`, by phi itself as
     there's no linker (`Compiler/Phi.Cli/Pdb.cs`). Breakpoints on lines, stepping, the stack
     (64-bit code has a table of its methods for that, which only `--debug` builds carry),
     variables by name and type, structs through pointers, text as text. Such a build keeps
     variables in memory, writes every method out and gives each a frame, and its variables
     are in a section of their own, as `dbghelp` takes a variable in the code's section for
     a piece of that code. `tests/debugger.cdb` drives Microsoft's `cdb`, and `phi test
     --debug-builds` runs every Windows program test built that way. Only `cdb` has been
     tried: not WinDbg, Visual Studio or VS Code.
   - [ ] Enums by name (they show as numbers), and the growing arrays' counts (a `T x[]` shows
     as a pointer to its first element).
   - [ ] Linux: DWARF. WSL has no `gdb` or `readelf` here to check it with.
2. **A language server** for editors such as VS Code: errors as you type, going to a
   definition, completion.
   - [x] `phi lsp` (2026-11-03), with no packages, which the VS Code extension
     ([PHI-VSCODE](https://github.com/BrandonHilde/PHI-VSCODE), its 0.2.0) runs. Errors as you type, from unsaved text and in
     every file the open programs are made of; F12 to what a name is, through `a.b:i.c`,
     across files, to the file of a `use` and into the standard library; completion of what's
     in sight, and after a dot, including in code that doesn't parse yet. It answers from the
     tokens around the place and what's declared, so half-written code works. The extension
     asks it first for definitions and completions, and adds what its own scanner knows (hover,
     references, rename, documents on completions). Tried in VS Code (that repository's
     `test:integration`, inside a real one, against this repository); no other editor yet.
   - [x] `SourceChecker`, which `phi check` uses too: a module is checked in a smallest program
     around it, its system's whole runtime or a program here that uses it, and only the errors in
     it are reported. Before, `phi check` said 144 of this repository's files had errors they
     didn't have (a module not being a program, the class Machine); it's 1 now,
     `samples/code.phi`, which is a real one. What the test tools make when they run is there
     too: a load client's network, Machine and LoadSettings, and the TestWeb of a test with a
     NAME.web folder.
   - [x] One build shows every mistake: with a syntax error, `phi build` and `phi check` used
     to stop at the syntax errors, so the type errors and unknown names only showed once those
     were fixed. Now the rest of the program is checked too, and its errors are shown beside
     them, but never one a line the parser gave up on could have caused: an error on or next
     to such a line, one that names something such a line names (and then what that error's
     own line and method name, and so on), an "unknown" one whose name is written elsewhere
     in the file, and nothing at all when the line was a heading, a block's start or end, a
     `use`, a constant or in a struct. Measured by spoiling one line at a time in the tests
     and samples that build by themselves: 721 spoiled lines, and no error that wasn't
     there but for one line replaced whole by nonsense. An editor still gets the syntax
     errors alone while code is half written. Tests `ErrorsTogetherTests`.
   - [ ] In the server too, so other editors get them and they come from the compiler: hover,
     finding every use of a name, renaming (the extension's scanner does these for now), and a
     program's other systems: a program is checked for one system (PHI OS unless what it uses
     says otherwise).
3. **A formatter,** and a way to share and depend on modules (packages).

**Done when:** a PHI program on Windows is stepped through by line in a debugger, and an
editor shows its errors as it's written.

#### Phase H9: Compiled libraries

**Status: done (2026-11-05).** A project compiles into one file, and another project includes
that file without having the first one's source (the user's goal: a codebase of libraries that
extend PHI and are reused). The library's code goes inside the program, so there is nothing
extra to ship, and PHI OS needs no library system. See
[Compiled libraries](docs/language.md#compiled-libraries) and
[docs/architecture.md](docs/architecture.md#compiled-libraries).

```
phi build physics.phi --library     # makes physics.philib
use physics;                        # finds physics.phi or physics.philib
```

Chosen by the user: the file holds machine code, not assembly text; a library exposes everything
it declares (marking what's public can come later); `--library`, `.philib` and `use` finding
either; and `[Area: : int]` for what an interface's method returns.

- [x] Interface slots as labels (`phi_slot_Shape`), with no change to what programs do. Class
  variables' labels are always `VALUE_Class_name`, and an import slot's says its library by name.
  Checked over 31 builds of 10 programs: the machine code stayed byte-identical.
- [x] An interface can say what a method returns. Pointers and `chr`s now stay what they are
  through an interface: a pointer used to be cut to an `int`, which lost its top half on 64-bit
  Windows, whose heap is above 4 GB.
- [x] Methods bound from their headers alone, and each file's interface written as PHI.
- [x] `--library`, for every system: an ELF object from NASM, and no linker. The final build
  writes the bytes as source, with `dd label` at each address, in its usual `-f bin` run.
- [x] Libraries that use libraries, a version check, and two libraries with interfaces of their
  own in one program.
- [x] A program gets only the methods it reaches: each method is a section of the object.

**Tests:** `library_use_program` (Linux and Windows, 32 and 64-bit), `library_use` (a kernel),
`library_disk` (a program on a PHI OS disk), `library_chain_program`, `library_errors`,
`interface_results_program`, `interface_results_errors`. By hand: the same program gave the same output from
source and from the library on all four host systems, and all of `lib/phi/crypto` as one 1.2 MB
library passed every crypto test run against it (40 runs, with RSA, P-384 and X.509). A
program using only SHA-256 from it kept 7 of its sections, with no RSA.

**Not done, and not needed yet:**

- A library's class variables and text always go in, reached or not: only methods are left out.
- A program can't call a library's `asm.` blocks directly, and a debugger steps over its code.
- Calls into a library aren't written out in place, as a small method's can be. A program with a
  library always keeps the records that catching errors needs. (Speed: for the optimising phase.)
- Not tried: going to a definition in a library from an editor, whose file isn't text.
- `library_use_program (windows)` failed once and passed on its retry, the first run after a
  compiler rebuild (2026-11-05); 26 runs in a row with no retries then passed, some with the
  library built fresh. Its failure wasn't saved. The full suite (764 of 764) had one retry of
  the same kind, `interfaces_program (windows64)`, whose `.exe` never started while Windows checked
  it, which may be what this was too. If it comes back with its program running, look there.

**Not in this phase:** DLLs and `.so` files, loading a library while a program runs, and C code
calling PHI. Reading C's object files could use the same route, and stays with C compatibility.

### Order

As of 2026-11-05. H1-H3, H5 and H6 are done, and 64-bit Linux and Windows programs work (H4,
with S10). H7's windows and sound are done on Windows and Linux, and H8's debugger on Windows
and language server are built. H9's compiled libraries are done.

Waiting, each for something:

- **macOS** (H4, H7, H8's parts of it): not started; nothing has needed it yet.
- **The GPU** (H7): on hold until everything else is done, then to be asked about again.
- **C compatibility and self-hosting:** on hold until the other features are done. H6's
  outside functions are the first half of C compatibility.
- **A real cloud machine, and 64-bit kernels:** no boot on one until there's a release, and
  kernels stay 32-bit until that needs them.
- **`phi fuzz` and optimising:** in the phase before a release, in an environment of their own.
- **Characters beyond ASCII:** waiting until the core language is solid (the user's choice,
  2026-11-05); English text in ASCII for now. Worked out so far: `str` stays bytes, with UTF-8
  for other text, and `.len` and `s:i` keep counting bytes, so nearly all of it can be PHI code
  (a `text.utf8` library to walk characters, a larger font loaded from a file). The one compiler
  part is string literals, where anything beyond ASCII is an error for now.

From the assessment written after building weblib (November 2026), done: an uncaught CPU error
always prints, with its line; the stack running out is an error (`ErrorKind.STACK`), caught on
any thread of a program; f64 math is exact to
the f64 on every system (the x87 set to double precision); `[Make: : ptr<Node>]`, with a
pointer cut to 32 bits now an error; `(F: x).field` and `(F: x):0`; `not` on an object's method;
an operator can start a line inside parentheses; `(p as u8[n])`, a checked array from a pointer;
a `.philib` keeps files by their path; `X509.MAX_ROOTS` is 4,096 in a table that grows; and
`tests/c_programmers.phi` holds that page to what PHI does; and text that doesn't fit in a `str`
is an error (`ErrorKind.TEXT`), with `Cut.Into` for when losing the rest is meant (the user's
choice, 2026-11-06; a literal is an error when it's built, and `ask` and a `throw`'s own text
still cut). Still to do, each for something:

- **`phi fuzz` and optimising, to add to the list above:** a jump table for a dense `when`
  (interpreters and parsers; weblib nested `when`s by hand); SSE2 for floating point; register
  allocation; making `NASMENV=-O1` the default for development builds. A string test weblib ran
  took 92 s with the program heap and 1 s with its own slab allocator: the heap has had size
  lists since S2, so `FindFree` walks only one list, but that's the case to look at (blocks of
  nearly one size). The same sieve took 642 ms in PHI and 353 ms in C#.
- **Method values that carry data: no language change (the user's choice, 2026-11-06, from
  examples: it reads best).** A callback with data of its own is an interface, which is checked and
  already there; `Threads.StartWith` gives a thread a pointer to its data, which an int couldn't
  carry in 64-bit code. No closures: they'd allocate where nothing says so.
- **Left to the developer, by the user's choice (2026-11-06):** watching a kernel's own stack
  (`tests/kernel32_stack_check.phi`) and a checked heap (`samples/own_heap`). The language has
  what they take, and adds nothing to every call or every program for them.

H8's formatter is built: `phi fmt`, and in `phi lsp` for an editor. It changes only a file's layout,
and adds parentheses around an expression in a call with several arguments, rather than the
language taking commas, which would make two ways to write a call. The library hasn't been run
through it (about 470 lines would gain parentheses): not until the rest is working, the user's choice. `FormatterTests` compiles programs both ways
and compares the code; `PHI_FMT_ALL=1 dotnet test` does it for every test, after changing it.

Left in H8: hover, finding uses and renaming in `phi lsp` itself, DWARF for Linux, and packages. Everything in the list above called "The parts most worth doing next" is done up
to its last item, the [Later](#later) list.

## References

- OSDev Wiki: https://wiki.osdev.org (Bare Bones, GDT, IDT, 8259 PIC, PS/2, ATA PIO, Paging)
- *Writing a Simple Operating System from Scratch*, Nick Blundell (free PDF), for the
  16-bit → 32-bit boot process
- Intel® 64 and IA-32 Architectures Software Developer's Manual, Volume 3 (system programming)
- *Crafting Interpreters*, Robert Nystrom (free online), for the lexer, parser and AST
  design in Phase 1
- xv6 (MIT), a small, readable teaching OS to compare against in Phases 5–7

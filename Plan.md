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

The parts most worth doing next, roughly in order:

1. **More for windows:** a program opening more than one window, menus, and dialog boxes
   (open, save, a message), and a multi-line `TextArea`. PhiOS draws its dialogs inside the
   app's own window (`shared/dialog.phi`) and wrote Notepad's editor itself for want of
   these. Done after the sixth PhiOS assessment: selection in `TextField`, Tab between
   fields (`FocusChain`) and `GuiEvent.pressed` for right-button presses.
2. **Text everywhere: done (2026-10-29),** after the sixth and seventh PhiOS assessments. Text
   works the same in variables, struct fields, parameters and array elements:
   `str name: [24];` (also `[atmost 24]`, `chr name[24]`) in a struct; `new str bio;` for text
   that grows in a struct (which makes it managed); `str into;` parameters that are the
   caller's text; `str names[atmost 64];`, `str slots[3];`, `str lines[8]: [40];` and
   `str rows[]` parameters; a check that a pointer into a method's own text isn't handed
   back. Tests `text_fields`, `text_params`, `text_arrays`, `text_new_fields`. Not yet: a
   `str` array without a most (`str names[];`), and method values with `str into;`.
3. **Interfaces**, so a table of handlers (`struct.App` with method fields) can be checked
   and filled in one place. Waiting for code that needs them before choosing a design
   (`interface.Name` with `struct.X has Name`, or checked tables of method fields).
4. Then the [Later](#later) list: TLS certificates, 64-bit mode, ARM. (C compatibility and
   self-hosting are on hold until everything else is done.)

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
- **64-bit long mode**, then an **ARM** backend (QEMU `virt` machine). The README's
  planned x86→ARM translator becomes a second compiler backend.
- **Networking:** done: PCI, an RTL8139 driver, ARP, IPv4, ICMP, UDP, DHCP, DNS, a TCP
  client, HTTP/1.0, and a TLS 1.3 client for https (`lib/phi/net/`, `samples/web.phi`) with
  its cryptography (`lib/phi/crypto/`: SHA-256, HMAC, HKDF, AES-128-GCM, ChaCha20-Poly1305,
  X25519, random numbers). Still to do: **checking the server's certificate** (X.509,
  RSA and ECDSA signatures, a set of trusted roots), listening for connections (a server),
  the e1000 card, and interrupts instead of polling
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
2. **Hundreds of players** (Phases S5–S7). A TCP server (listen, accept, a sliding window), register
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
- [ ] (4, last part) constant folding beyond what `ConstantFolder` does now
- [ ] (2) the backend interface
- [ ] (4) folding, inlining small methods, and bounds checks left out where the index is
  proved in range

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

**Status: not started.**

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

**Status: in progress.**
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
- [ ] (4) catching errors in the language: **not now** (the user, 2026-10-30). Errors are
  contained at the program level (parts 1-3); revisit if kernel code turns out to need a
  block that catches them.
- [ ] (5) `phi fuzz`

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

**Status: not started.**

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

**Status: not started.**

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

**Status: not started.**

### Scenarios to write up next

Candidates, each a large project that tests a different side of PHI:

- **A game engine runtime:** rendering, loading assets, audio and input at 60 frames a
  second or more. Tests speed, SIMD and access to a GPU.
- **A game console OS:** boots on real hardware, runs games from storage, reads
  controllers. Tests drivers, USB, UEFI and real hardware.
- **Studio tools:** asset pipelines and build tools that run on Windows and Linux. Tests a
  hosted target (PHI programs that run outside PHI OS).

---

## References

- OSDev Wiki: https://wiki.osdev.org (Bare Bones, GDT, IDT, 8259 PIC, PS/2, ATA PIO, Paging)
- *Writing a Simple Operating System from Scratch*, Nick Blundell (free PDF), for the
  16-bit → 32-bit boot process
- Intel® 64 and IA-32 Architectures Software Developer's Manual, Volume 3 (system programming)
- *Crafting Interpreters*, Robert Nystrom (free online), for the lexer, parser and AST
  design in Phase 1
- xv6 (MIT), a small, readable teaching OS to compare against in Phases 5–7

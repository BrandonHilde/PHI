# Memory map and boot process

PHI builds two kinds of programs. Both boot from a raw disk image (`phi build`), as an
IDE hard disk in QEMU.

## 16-bit programs (`Bootloader` and `OS` classes)

The BIOS loads sector 0 and runs it in 16-bit real mode. Your `Bootloader` class *is*
that sector; `call Bootloader.JumpToSectorTwo;` loads the `OS` classes and jumps to them.

| Disk | Contents |
|---|---|
| sector 0 | the `Bootloader` class (512 bytes) |
| sector 1 … | the `OS` classes (up to 32 KB) |

| Address | Contents |
|---|---|
| `0x00000`–`0x003FF` | real-mode interrupt vector table |
| `0x00400`–`0x004FF` | BIOS data area |
| below `0x7C00` | the stack (grows down from `0x7C00`) |
| `0x07C00`–`0x07DFF` | the boot sector |
| `0x07E00`–`0x0FFFF` | the `OS` classes, code and variables |
| `0xA0000` | VGA graphics memory (mode 13h) |
| `0xB8000` | VGA text memory |

The BIOS stays available, so `log`, `ask` and the keyboard and video built-ins use it.

## 32-bit kernels (`Kernel` classes)

PHI supplies the boot sector ([lib/boot/stage1.asm](../lib/boot/stage1.asm)) and a loader
([lib/boot/stage2.asm](../lib/boot/stage2.asm)). Your `Kernel` classes start in 32-bit
protected mode with nothing else running.

| Disk | Contents |
|---|---|
| sector 0 | stage 1: loads stage 2 |
| sectors 1–8 | stage 2: the loader (4 KB) |
| sector 9 … | the kernel (up to 512 KB) |

**What happens at boot:**

1. The BIOS runs stage 1 at `0x7C00`, which loads stage 2 to `0x7E00`.
2. Stage 2, still in 16-bit mode with the BIOS available:
   - loads the kernel to `0x10000`, 32 KB at a time
   - asks the BIOS for the memory map (`int 0x15`, E820) and the cursor position, and
     writes them to **BootInfo** at `0x9000`
   - enables the A20 line (so addresses above 1 MB work)
   - loads a GDT with flat 4 GB code (`0x08`) and data (`0x10`) segments
   - switches to protected mode and jumps to the kernel
3. The kernel starts at `0x10000` with:
   - every segment register flat (base 0, limit 4 GB)
   - `esp` = `0x9F000`
   - `ebx` = `0x9000` (the BootInfo address)
   - interrupts disabled; before running your code, the kernel's startup sets up an
     interrupt table, remaps the interrupt controllers (IRQ n becomes vector 32 + n,
     every IRQ masked until a driver unmasks it), and enables interrupts

| Address | Contents |
|---|---|
| `0x00500`–`0x07BFF` | free (stage 1 and 2 used it as their stack) |
| `0x07C00`–`0x07DFF` | stage 1 (no longer needed) |
| `0x07E00`–`0x08DFF` | stage 2 and its GDT (the GDT is still in use) |
| `0x09000`–`0x0903F` | BootInfo |
| `0x09040`–`0x0963F` | memory map: up to 64 `MemoryRegion`s of 24 bytes |
| `0x10000`–`0x8FFFF` | the kernel: code, then variables (including the 2 KB interrupt table) |
| `0x90000`–`0x9EFFF` | the kernel stack (grows down from `0x9F000`) |
| `0x9FC00`–`0x9FFFF` | BIOS extended data area (reserved) |
| `0xA0000`–`0xFFFFF` | video memory and BIOS ROM (`0xB8000` is the text screen) |
| `0x100000` … | free RAM (127 MB on QEMU's default 128 MB machine) |

### BootInfo

`use boot.info;` (in [lib/phi/boot/info.phi](../lib/phi/boot/info.phi)) gives typed access:

```phi
use boot.info;

phi.Main:Kernel
{
    ptr<BootInfo> boot: Boot.info;
    log boot:0.region_count ' memory regions\n';
}
```

| Offset | Field | |
|---|---|---|
| 0 | `magic` | `0x21494850` ('PHI!') |
| 4 | `boot_drive` | BIOS drive number (0x80 = first hard disk) |
| 8 | `region_count` | entries in the memory map |
| 12 | `regions` | address of the first `MemoryRegion` (`0x9040`) |
| 16 | `cursor_row` | where the BIOS left the cursor |
| 20 | `cursor_column` | |
| 24 | `kernel_start` | `0x10000` |
| 28 | `kernel_size` | bytes, rounded up to whole sectors |

Each `MemoryRegion` is 24 bytes: `base`, `base_high`, `length`, `length_high`, `type`
(1 = usable RAM) and `attributes`.

### Output in a 32-bit kernel

There is no BIOS in protected mode, so `log` writes straight to the VGA text screen
(continuing below the BIOS's boot messages) and to COM1, through
[lib/x86_32/console.asm](../lib/x86_32/console.asm). `debug` writes to COM1 only.

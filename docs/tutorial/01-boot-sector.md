# 1. Your first boot sector

When a PC starts, its firmware (the BIOS) knows nothing about operating systems. It reads
the first 512 bytes of the disk, the **boot sector**, into memory at address `0x7C00` and
jumps to it. Whatever is in those bytes is now running the computer.

In this part you'll write a boot sector in PHI that talks to you.

## The program

Save this as `hello.phi` (it's also [examples/01-hello.phi](../examples/01-hello.phi)):

```phi
# Tutorial 1: a boot sector. The BIOS loads these 512 bytes and runs them.
phi.Hello:Bootloader
{
	str name: [20];

	log 'Hello from the boot sector!\r\n';
	log 'What is your name? ';
	ask name;
	log '\r\nNice to meet you, ' name '.\r\n';
}
```

Run it:

```
phi run hello.phi
```

QEMU starts, the BIOS prints a few lines, then your program asks for your name. Type it and
press Enter.

## What's going on

**`phi.Hello:Bootloader { ... }`** is a class named `Hello`. The part after the colon, its
*base*, decides where the code goes. `Bootloader` means "this is the boot sector". A
program has exactly one.

**Statements run top to bottom.** When they're done, the CPU halts.

**`str name: [20];`** makes a string variable with room for 20 characters. Variables are
declared as `type name: value;`, so `str greeting: 'hi';` would be a string holding `hi`.

**`log`** prints any number of values, separated by spaces. A boot sector still has the
BIOS available, so `log` asks the BIOS to draw each character. It also sends them to the
serial port, which is how `phi run` shows them in your terminal and how tests read them.

**`ask name;`** reads a line from the keyboard into `name`, echoing what you type.

**`\r\n`** is a new line. On the BIOS screen, `\n` only moves down a line, and `\r` goes back
to the start of it, so you need both. (32-bit kernels treat `\n` alone as a new line.)

**`#`** starts a comment.

## Look at what you built

`phi build hello.phi` writes everything to `build/hello/`:

- `hello.img`: the disk image. Its first 512 bytes are your boot sector, ending in the bytes
  `0x55 0xAA` that tell the BIOS it's bootable.
- `boot.asm`: the assembly PHI generated. Each line of your program appears as a comment
  above the instructions it became:

```nasm
    ; 7: log 'Hello from the boot sector!\r\n';
    mov esi, phi_string_0
    call phi_print
```

Only the parts of the standard library your program uses are included, which matters when
everything has to fit in 512 bytes.

## Bigger than 512 bytes

A boot sector is small. If yours grows too much, the build says:

```
error: the boot sector is 12 bytes too big (it has to fit in 512 bytes).
```

PHI's answer for 16-bit programs is a second class with the base `OS`, which the boot sector
loads from the following sectors of the disk:

```phi
phi.Boot:Bootloader
{
	call Bootloader.JumpToSectorTwo;
}

phi.Rest:OS
{
	log 'Now I have 32 KB to work with.\r\n';
}
```

From the next part on, you'll mostly write **32-bit kernels** instead, where PHI supplies
the boot sector for you and your kernel can be up to 512 KB.

## Try this

1. Ask for a second thing, like a favorite color, and print both.
2. Print the length of the name: `name.len` is the number of characters in a string.
3. Look in `build/hello/boot.asm` for `jmp 0x0000:phi_start`. That's the very first
   instruction the computer runs after the BIOS.

Next: [2. Language basics](02-language-basics.md)

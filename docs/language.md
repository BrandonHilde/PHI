# PHI language reference

This describes the language as the compiler in `Compiler/Phi.Compiler` implements it today
(16-bit x86, QEMU). Ideas that aren't implemented yet live in [Syntax/](../Syntax) and
[Plan.md](../Plan.md).

## Program structure

A program is a list of classes. Each class has a base that says where its code runs.
There are two kinds of programs:

**16-bit programs** run in real mode with the BIOS available:

```phi
phi.Hello:Bootloader        # the boot sector: exactly one, and it must fit in 512 bytes
{
    log 'Booting...';
    call Bootloader.JumpToSectorTwo;
}

phi.Shell:OS                # loaded from disk after the boot sector (up to 32 KB)
{
    log 'Hello from the OS';
}
```

- The `Bootloader` class becomes the 512-byte boot sector at `0x7C00`.
- All `OS` classes are combined and loaded at `0x7E00` by `call Bootloader.JumpToSectorTwo;`.
  The build works out how many sectors to load.

**32-bit kernels** run in protected mode, with no BIOS:

```phi
phi.Hello:Kernel            # up to 512 KB, loaded at 0x10000
{
    log 'Hello from 32-bit PHI\n';
}
```

- PHI supplies the boot sector and a loader that collects the memory map, switches the CPU
  to 32-bit protected mode and jumps to the kernel. See [memory-map.md](memory-map.md).
- A kernel can't contain `Bootloader` or `OS` classes.

**User programs** run inside a 32-bit kernel, in user mode (ring 3):

```phi
phi.Hello:Program
{
    log 'Hello from a program\n';
    exit 0;
}
```

- A `.phi` file in a kernel's `rootfs` folder is compiled into `NAME.BIN` on its disk, and
  the kernel starts it with `Process.Spawn` (see [Programs](#programs-and-processes)).
- `phi build hello.phi` on its own makes `hello.bin`, which isn't bootable.

In every kind:

- Statements directly inside a class run in order, top to bottom, when the class starts.
  When they finish, the CPU halts (a 16-bit program still handles interrupts, so timer
  and keyboard events keep running).
- `phi.Name:Library` classes are shared code, like drivers. They join the `OS` classes of a
  16-bit program or the kernel of a 32-bit one, whichever uses them.
- In a 16-bit program the boot sector and the `OS` classes are built separately, so they
  can't use each other's variables or methods.

Other top-level items: `struct.Name { ... }` (see [Structs](#structs)), `asm.Name { ... }`
(see [Assembly blocks](#assembly-blocks)) and `use path.to.file;` (see [Files](#files)).

## Comments

```phi
# a comment to the end of the line
log 'hi';  # after code too

#
    A '#' alone on its line starts a block comment,
    which runs to the next '#'.
#
```

## Values

| Kind | Examples |
|---|---|
| Numbers | `42`, `-7`, `0x2A`, `101010b` (binary), `1_000` |
| Booleans | `true`, `false` (stored as 1 and 0) |
| Strings | `'hello'`; `'hasn't'` works without escaping |
| Escapes | `\r` `\n` `\t` `\0` `\\` `\'` |
| Characters | a one-character string like `'w'` is its character code wherever a number is expected |
| Constants | `Colors.Black` … `Colors.White` (the 16 VGA colors), and your own `const` |

A quote followed directly by a letter or digit doesn't end a string, which is why
`'hasn't'` works. Strings can span several lines. A number too big for `int` (like
`0x80000000`) is a `u32`.

## Variables

| Type | Size | Values |
|---|---|---|
| `int` (also `i32`) | 4 bytes | -2147483648 to 2147483647 |
| `u32` | 4 bytes | 0 to 4294967295 |
| `i16` / `u16` | 2 bytes | -32768 to 32767 / 0 to 65535 |
| `i8` / `byt` (also `u8`) | 1 byte | -128 to 127 / 0 to 255 |
| `bln` | 1 byte | `true` or `false` |
| `str` | its capacity | text (see below) |
| `ptr<T>` | 4 bytes | an address (see [Pointers](#pointers)) |
| `method<T ...: R>` | 4 bytes | a method that can be called (see [Methods as values](#methods-as-values)) |
| a struct | its fields | see [Structs](#structs) |
| `var` | | `str` if every value is a string, otherwise `int` |

```phi
int count: 0;
u16 port: 0x3F8;
bln ready: false;
str name: 'PHI';
int total;                  # no value: zero

int numbers: 1 3 5 9 2;     # several values make an array
str days: 'Mon' 'Tue';      # an array of strings
int squares[5];             # an array of 5 zeros
str buffer: [40];           # room for 40 characters (also: str buffer[40];)
u8 sectors[SECTOR_SIZE];    # sizes can be constants

str text: count;            # a number written out as text
```

`=` works in place of `:` in declarations. Storing a value in a smaller type keeps the low
bytes (so `u8` wraps around at 256).

**Size of a `str`.** A `str` has a fixed amount of room, decided when it's declared:

- `str s: 'abc';` holds 3 characters (the length of its first value)
- `str s: [40];` holds 40
- `str s: someNumber;` holds 11 (enough for any `int`)
- `str s: otherStr;` holds as much as `otherStr`
- each element of a `str` array holds as much as the longest initial value

Longer text is cut to fit, without an error: after `str tiny: [3]; tiny is 'abcdef';`,
`tiny` is `abc`. A `str` takes one more byte than it holds, for the zero that ends the text.

**How a `str` array is stored.** A `str` array is an array of 4-byte pointers, one per
element, and each points to that element's own buffer. So `days:1` is the address of the
second string, and code that walks the array's memory by a fixed stride (for rows of a
sprite, say) must use a `u8` array instead (`u8 rows[8 * 16];`, indexed as
`rows:(row * 16 + column)`).

**A list of values** is separated by spaces, which applies to declarations, call arguments,
`log` and `out`. A `-` with a space before it and none after starts a new, negative value:
`int steps: 1 -1 2 -2;` has four values, and `call Move: x -1;` passes two arguments.
With spaces on both sides or neither (`x - 1`, `x-1`) it subtracts. Inside parentheses it
always subtracts.

**Where variables live.** Variables declared directly in a class have a fixed place in
memory and are set once, when the program is built. A method's parameters and variables
(including its loop counters) live on the stack, in a new set for every call: they start
at zero, and are set again each time their declaration runs. So a method can call itself,
and an interrupt handler can safely call a method the main code is also running. A pointer
to a method's variable (`addr total`) is only good until the method returns.

**Scope.** A name is looked up in the current method, then the current class, then in
other classes of the same unit (if only one class has it). `Class.name` picks a specific
class. `x.len` is the number of elements of an array, or the current length of a `str`.

### Constants

```phi
const u16 COM1: 0x3F8;
const u16 COM1_STATUS: COM1 + 5;
const int BUFFER_SIZE: 512;
```

A constant's value must be known when the program is built. Constants take no memory and
can be used as array sizes and inside `asm.` blocks (`{COM1}`).

### sizeof

`sizeof` gives the bytes a type or a variable takes, worked out when the program is built,
so it can be used in constants and array sizes:

```phi
const int ENTRY_SIZE: sizeof DirEntry;   # a struct
u8 copy[sizeof DirEntry];
int a: sizeof u16;                       # 2; also ptr<T> (4), bln (1), ...
int b: sizeof entries;                   # a whole array: elements * element size
int c: sizeof entries:0.name;            # a field
int d: sizeof name;                      # a str variable: its capacity plus the ending zero
```

A `str` type (or `str` array) has no fixed size, so `sizeof str` is an error.

## Expressions

From lowest to highest precedence:

| Operators | Meaning |
|---|---|
| `or` | either is true |
| `and` | both are true |
| `is`, `==`, `is not`, `!=`, `<`, `>`, `<=`, `>=`, `below`, `above`, `atmost`, `atleast` | compare (one per expression: write `(a < b) is false`) |
| `\|` | bitwise or |
| `^` | bitwise exclusive or |
| `&` | bitwise and |
| `<<` `>>` | shift left / right |
| `+` `-` | add, subtract |
| `*` `/` `%` | multiply, divide, remainder (rounding toward zero) |
| `-x`, `~x`, `not x`, `!x`, `addr x`, `sizeof x`, `in port` | negate, bitwise not, logical not, address, size in bytes, port input |
| `list:i`, `list:i.field` | element `i` of an array, character `i` of a `str`, a field of an element |
| `( )` | grouping |

Unlike C, the bitwise operators come before the comparisons, so `flags & 4 is 4` means
`(flags & 4) is 4`.

- Math is done in 32 bits and wraps around on overflow. It is **unsigned** if either side
  is a `u32` or a pointer (this affects `/`, `%`, `>>` and comparisons), and signed otherwise.
- `below`, `above`, `atmost` and `atleast` are word forms of `<`, `>`, `<=` and `>=`, the
  way `is` is a word form of `==`: `if lives above 0 and score atleast best`.
- Two strings compare as text, but only with `is` / `==` / `is not` / `!=`.
- In a condition, any number counts as true unless it is 0.
- A binary operator must be on the same line as its left side. This is how
  `if ready` followed by a statement on the next line knows where the condition ends.
- The index after `:` is a number, a single name, or something in parentheses:
  `cells:(row * 80 + column)`.

## Statements

Simple statements end with `;`. Blocks (`if`, `else`, `while`, `unsafe`) end with `;;`.

### Output and input

```phi
log 'Score: ' score '\r\n';     # screen and serial port (COM1)
debug 'x = ' x '\n';            # serial port only: handy while developing
ask name;                       # read a line from the keyboard into a str
exit 0;                         # stop QEMU (used by tests); halts on real hardware
```

`log` and `debug` print strings as text and numbers in decimal (`u32` and pointers as
unsigned). A `ptr<u8>` prints as the zero-terminated text it points to, like a `str`; to
print the address instead, put it in a `u32` first. On the screen, a new line needs `\r\n`.

### Assignment

```phi
x is 5;        # or: x = 5;
x++;           # add 1
x--;           # subtract 1
x++ 10;        # add 10        (also x ++ 10, x++10)
x-- y;         # subtract y
x** 3;         # multiply by 3
x// 2;         # divide by 2
x%% 2;         # remainder of dividing by 2
list:2 is 7;   # one element
task.id is 3;  # one field
name is 'new text';
```

### Conditions

```phi
if score > best and lives > 0
    log 'new record';
;;
elif score is best
    log 'tied';
;;
else
    log 'try again';
;;
```

The condition ends at the end of its line (a `;` after it is also allowed).

### Loops

```phi
while lives > 0
    lives--;
;;

while int i: 0; i < days.len; i++;
    log days:i ' ';
;;

while int i: 0; i < count; i++;
    if items:i is 0
        continue;           # skip to the next round (the step, i++, still runs)
    ;;
    if items:i is wanted
        break;              # leave the loop
    ;;
;;
```

`break` and `continue` act on the innermost loop.

### Ports

```phi
out 0x3F8 'A';                  # write a byte to an I/O port
outw port value;                # 16 bits
outd port value;                # 32 bits
u8 status: in 0x3FD;            # read a byte (inw and ind read 16 and 32 bits)
```

The port and the value are separate values, so `out port -1;` writes -1 to `port` (see
"A list of values" under [Variables](#variables)).

### Bounds checks

Indexing an array or a `str` is checked when the program runs. An index outside the
array stops the program with `PHI: index out of range on line N`. A constant index is
checked when the program is built instead. Code inside `unsafe` isn't checked:

```phi
unsafe
    buffer:i is 0;      # no check: you promise i is in range
;;
```

Pointers are never checked.

## Pointers

A `ptr<T>` holds a 32-bit address of memory containing `T` values. Index it like an array:

```phi
ptr<u16> vga: 0xB8000;          # the VGA text screen
vga:0 is 0x0748;                # 'H' in light gray

ptr<u8> p: addr buffer;         # the address of a variable (or element, or field)
p:3 is 0;
p++ 2;                          # adding to a pointer moves it by that many *bytes*

str word: 'hey';
ptr<u8> w: word;                # a str can be used where a ptr<u8> is expected
ptr<u8> b: buffer;              # so can an array: this is addr buffer
call Clear: tasks:0.name;       # including an array field ([Clear: ptr<u8> to: 0;])

ptr<Task> t: addr tasks:0;
log t:0.id;                     # a field of what t points to
```

- Pointers can reach any address below 1 MB (in 16-bit mode, through the `fs` segment).
- `addr x` is a pointer to what `x` holds: `addr buffer` of a `u8` array is a `ptr<u8>`,
  `addr task` of a `Task` is a `ptr<Task>`.
- An array, or an array field like `tasks:0.name`, used where a pointer is expected (a
  parameter, a pointer variable) is its address, the same as `addr`.
- `addr Method` is the address of a method, for interrupt handlers.
- Every read and write goes to memory (values are never kept in registers between
  statements), so pointers to device memory work without anything like C's `volatile`.

## Structs

```phi
struct.Point
{
    i16 x;
    i16 y;
}

struct.Task
{
    u32 id;
    Point position;        # structs can contain structs
    u8 name[8];            # and fixed arrays
    ptr<Task> next;        # and pointers, including to themselves
}
```

```phi
Task current;              # starts as all zeros
Task tasks[8];

current.id is 1;
current.position.x is -5;
current.name:0 is 'A';
tasks:i.id is i;
tasks:i.position.y is 10;
```

Fields are laid out in order with no padding, which is what hardware tables (like the GDT
or a disk's partition table) need. Structs can't be assigned or passed as a whole; work
with their fields, or pass a `ptr<Task>`.

## Memory (32-bit kernels)

```phi
ptr<Task> task: new Task;           # one Task, all zeros
ptr<u8> buffer: new u8[512];        # 512 bytes, all zeros
task:0.id is 7;
free task;
free buffer;
```

`new` gives memory from the kernel heap and `free` gives it back. A kernel that uses them
gets the memory library automatically: at startup it reads the BIOS memory map, turns on
paging and sets up a heap of a quarter of free memory (at least 8 MB; about 31 MB on
QEMU's default 128 MB machine). `new` returns 0 when the heap has no room. Freeing a
pointer `new` didn't give out (or freeing twice) stops the kernel with a message.

Paging maps every address to the same physical address, but only memory that exists:
**a null pointer, or an address past the end of RAM, stops the kernel with a page fault**
on the panic screen, showing the address that was used.

The pieces can also be used directly:

| File | Class | What it does |
|---|---|---|
| `memory.frames` | `Frames` | physical memory in 4 KB frames: `Alloc` →, `AllocContiguous: count` →, `Free: address`; `free_frames`, `total_frames`, `memory_top` |
| `memory.paging` | `Paging` | identity paging with page 0 unmapped; `page_directory`, `mapped_bytes` |
| `memory.heap` | `Heap` | `Alloc: size` →, `Free: address`, `LargestFree` →; `used_bytes`, `used_blocks`, `heap_size` |
| `memory.list` | `List` | a growable list of u32 values: `New` →, `Add: list value`, `Get: list index` →, `Set: list index value`, `Delete: list`; the list's `count` and `capacity` |

```phi
use memory.list;

ptr<ListData> numbers: 0;
call numbers is List.New;
call List.Add: numbers 42;
log numbers:0.count;
```

## Files on disk (32-bit kernels)

`phi build` gives a 32-bit kernel's disk image a 16 MB FAT16 partition, filled with the
contents of the folder `NAME.rootfs/` next to `NAME.phi` (or a folder called `rootfs/`).
Names must be 8.3, like `README.TXT` or `DOCS/NOTES.TXT`.

```phi
use fs.fat16;

ptr<u8> buffer: new u8[4096];
int size: 0;
bln ok: false;

call size is Fat.ReadFile: 'readme.txt' buffer 4096;   # bytes read, or -1 if it isn't there
buffer:size is 0;
log buffer;                                            # a ptr<u8> prints as text

call ok is Fat.WriteFile: 'docs/new.txt' 'hello' 5;     # creates or replaces
call ok is Fat.Delete: 'docs/new.txt';
call size is Fat.List: '';                              # prints the root folder
```

| Method | What it does |
|---|---|
| `ReadFile: path buffer max` → | copy up to `max` bytes of the file into `buffer`; the count, or -1 |
| `ReadAt: path offset buffer max` → | the same, starting `offset` bytes into the file (0 at its end) |
| `FileSize: path` → | the size in bytes, or -1 |
| `Exists: path` → | 1 if the file or folder is there |
| `WriteFile: path data length` → | create or replace a file; 0 if a folder is missing, the folder is full, or the disk is full |
| `Delete: path` → | remove a file |
| `List: path` → | print a folder (`''` is the root); returns how many entries it has |
| `Entries: path into max` → | copy up to `max` of a folder's entries into a `FileInfo` array (`addr entries:0`); how many, or -1 |
| `MakeFolder: path` → | make a folder; 0 if it's already there or its parent isn't |
| `DeleteFolder: path` → | remove a folder, which must be empty |
| `Rename: from to` → | rename a file or folder, or move it to another folder; 0 if `to` exists |
| `FreeClusters` → | free 2 KB clusters left |

A `FileInfo` has the entry's `name` (`NAME.EXT`, ending with a zero, so `log addr info.name`
prints it), `size`, `folder` (true for a folder), and when it was last written: `year`,
`month`, `day`, `hour`, `minute`. Files and folders get that from the clock (`drivers.rtc`).

Case doesn't matter in paths. Long file names aren't read, and folders don't grow, so
each folder holds as many new files as it has free entries (the root holds 511). Changes
are written to the disk image, so a kernel sees them the next time it boots the same image
(`phi build` makes a fresh image each time).

## The network (32-bit kernels)

`phi run` and `phi test` give every machine an RTL8139 network card on QEMU's user
network, which reaches the internet through the host. `net.http` fetches web pages:

```phi
use net.http;

ptr<u8> page: new u8[65536];
bln ok: false;
call ok is Net.Start;                    # finds the card and gets an address by DHCP

int size: 0;
call size is Http.Get: 'http://example.com/' page 65536;
if size >= 0
    log page;                            # the raw response: status line, headers, HTML
    log Http.status;                     # 200
;;
else
    log Http.error;                      # why it failed
;;
```

`samples/web.phi` asks for URLs and prints what comes back. `https://` URLs go over TLS
1.3 (`net.tls`), with AES-128-GCM or ChaCha20-Poly1305, whichever the server picks. **The
server's certificate isn't checked yet** (`Tls.verified` is false): the connection is
encrypted, but someone who can intercept it could pretend to be the server.

The layers, each usable on its own:

| File | Class | What it does |
|---|---|---|
| `drivers.pci` | `Pci` | `Find: vendor device` → (or `Pci.NOT_FOUND`), `Read`/`Write: device offset`, `Bar: device n` →, `Irq` →, `EnableBusMaster` |
| `drivers.rtl8139` | `Rtl8139` | the card: `Start` →, `Send: frame length` →, `Receive: buffer max` → (0 when nothing is waiting); `mac` |
| `net.net` | `Net` | `Start` →, `Configure: ip netmask gateway dns`; `ip`, `netmask`, `gateway`, `dns`, `dhcp_ok`; `Ping: address timeout` → (ms, or -1); `SendUdp: to from_port to_port data length` →, `Listen: port`, `WaitUdp: timeout` → (the datagram is in `udp_data`); `ParseIp: text` →, `PrintIp: address` |
| `net.dhcp` | `Dhcp` | `Configure` → (`Net.Start` does this) |
| `net.dns` | `Dns` | `Resolve: name` → an address, or 0 |
| `net.tcp` | `Tcp` | `Connect: address port` → a connection or -1, `Write: c data length` →, `Read: c buffer max` → (0 once the other side has closed, -1 on a timeout), `Close: c`; `timeout` (ms, 10000) |
| `net.tls` | `Tls` | one TLS 1.3 connection: `Connect: address port host` → (false: `error` says why), `Write: data length` →, `Read: buffer max` → (0 once the server has closed), `Close`; `suite`, `alert`, `verified` |
| `net.http` | `Http` | `Get: url buffer max` → the response's size or -1, for `http://` and `https://`; `status`, `body` (where the body starts), `error` |

Addresses are `u32`s with the first number in the top byte (`10.0.2.15` is
`0x0A00020F`). On QEMU's network the kernel is `10.0.2.15`, the gateway `10.0.2.2` (which
is also the host computer's `127.0.0.1`), and the DNS server `10.0.2.3`. `Net.Start` asks
for an address by DHCP first; if no server answers it keeps these addresses and
`Net.dhcp_ok` is 0.

Nothing waits on an interrupt: the methods that wait (`Ping`, `Dns.Resolve`, `Tcp.Read`,
...) poll the card, and sleep a millisecond when it has nothing. Up to four TCP connections
can be open at once, each with an 8 KB receive buffer.

## Graphics and input (32-bit kernels)

`drivers.vbe` switches QEMU's standard display adapter to graphics with 32-bit color,
`gfx.draw` draws, and `drivers.input` reads the keyboard and mouse from their interrupts.

```phi
use drivers.vbe;
use drivers.input;

if (Screen.Start: 800 600)                     # false without the adapter (or the memory)
    call Draw.Clear: Draw.BLACK;
    call Draw.FillRect: 10 10 200 100 (Draw.Rgb: 40 90 200);
    call Draw.Line: 0 0 799 599 Draw.WHITE;
    call Draw.FillCircle: 400 300 50 Draw.YELLOW;
    call Draw.Text: 20 20 'Hello' Draw.WHITE;  # the VGA font, 8 x 16 pixels a character
    call Screen.PresentAll;                    # show it

    call Input.Start;
    call Input.SetArea: 800 600;               # where the mouse can go
    while true
        int key: (Input.NextKey);              # 0 if nothing was pressed
        call Screen.MoveCursor: Input.mouse_x Input.mouse_y;
    ;;
;;
```

- **Drawing happens off screen.** `Draw` draws on `Screen.back`, a buffer in memory, and
  `Screen.Present: x y w h` (or `PresentAll`) copies it to the screen, so the screen never
  shows a half-drawn picture. The mouse pointer (`MoveCursor`, `HideCursor`) is drawn on the
  screen only, over the picture.
- **Views.** `Draw.SetView: x y w h` moves the origin to (x, y) and cuts all drawing to that
  rectangle; `ResetView` undoes it. `Draw.Target: pixels w h` draws on a block of pixels of
  your own instead, like an image being built.
- **Shapes:** `Clear`, `FillRect`, `Rect`, `Pixel`, `GetPixel`, `HLine`, `VLine`, `Line`,
  `Circle`, `FillCircle`, `Gradient` (top color to bottom color), `Blit` (copy an image),
  `Char`, `Text` and `Number` (both return their width), `TextWidth`, `Centered`. Colors are
  `0xRRGGBB`; `Draw.Rgb: r g b` makes one, and `Draw.RED` ... name a few.
- **Keys** from `Input.NextKey` are characters (`'a'`, `'A'`, 13 for Enter, 27 for Esc) or
  `Input.KEY_UP`, `KEY_F1` ... for keys that don't type one, with `MOD_SHIFT`, `MOD_CTRL` and
  `MOD_ALT` added for the modifiers held (`key & Input.KEY_MASK` is the key alone).
  `(Input.IsDown: 'a')` says whether a key is held now. `Input` replaces the built-in keyboard
  driver, so don't also use `ask` or `OS.GetKey`.
- **The mouse** is `Input.mouse_x`, `Input.mouse_y` and `Input.buttons` (bit 0 left, bit 1
  right, bit 2 middle), kept inside the area given to `SetArea`.
- `Paging.MapDevice: address bytes` makes other device memory reachable the same way.

[samples/sketch.phi](../samples/sketch.phi) is a small drawing program using all three.

## Sound (32-bit kernels)

`phi run` and `phi test` give every machine an AC'97 sound card, which `drivers.ac97` drives.
`audio.sound` plays music on it: songs written as text, and WAV files.

```phi
use audio.sound;

bln ok: false;
call ok is Sound.PlaySong: 't140 @1 o4 l8 c e g >c4 <g4 c2';   # waits until it ends
call ok is Sound.PlayFile: 'MUSIC/ODE.SNG';     # a song from the disk
call ok is Sound.PlayFile: 'MUSIC/HARP.WAV';    # a WAV file (a name ending in .WAV)
if ok is false
    log Sound.error '\n';                       # no sound card, no such file, a mistake on line Sound.error_line ...
;;

# or start it, and keep the card fed while doing other things
call ok is Sound.StartFile: 'MUSIC/CHIP.SNG';
while Sound.playing
    call Sound.Pump;                            # at least every half second or so
    # ... anything else; Sound.Position is how far it has got, of Sound.milliseconds ...
;;
```

**Songs** are text in the style of MML, the music language of old computers and game
consoles. Each line is a voice, and the voices play together (up to 4). A line starting
with a voice number and a colon (`1:` to `9:`) carries on that voice, so a long part can be
split over many lines; `#` starts a comment.

| Command | Meaning |
|---|---|
| `c d e f g a b` | a note; then `#` or `+` for sharp, `-` for flat, a length, and dots (`c#8.`) |
| `r` or `p` | a rest, with a length and dots |
| `o4`, `>`, `<` | the octave (0 to 8; `o4` starts at middle C), one up, one down |
| `l8` | the length of notes that don't give one: 1 whole, 2 half, 4 quarter, 8, 16 ... |
| `t120` | the tempo, in quarter notes a minute (the first voice's starting tempo is everyone's) |
| `v12` | the volume, 0 to 15 |
| `@0` to `@5` | the sound: square, pulse, triangle, sawtooth, sine, noise (drums) |
| `q7` | how much of each note sounds, in eighths (`q8` runs notes together) |
| `[ ... ]3` | play what's inside 3 times (loops don't nest) |
| `\|` and spaces | ignored: use them to show bars |

```
# Ode to Joy, the first line, with a bass
1: t112 @1 o4 l4 e e f g | g f e d | c c d e | e. d8 d2
2: @2 o3 c2 g2 | o2 g2 o3 d2 | o3 c2 g2 | o2 g2 o3 d2
```

**WAV files** can be 8-bit or 16-bit, mono or stereo, at any rate (the card plays at 44100
frames a second, and other rates are resampled). Compressed WAV files aren't played.

The layers, each usable on its own:

| File | Class | What it does |
|---|---|---|
| `drivers.ac97` | `Ac97` | the card: `Start` →, `SetRate: hz` →, `SetVolume: percent`, `Write: data length` → (bytes taken; never waits), `Play: data length` (waits for room), `Flush`, `Drain` (waits until it has played), `Queued` →, `Stop`; `rate`, `bytes_written` |
| `audio.synth` | `Synth` | `Load: song rate` → (false: `error` on `error_line`), `Render: buffer frames` → (frames made; 0 at the end), `Rewind`; `voices`, `milliseconds`, `finished` |
| `audio.wav` | `Wav` | `Parse: header length file_size` → (false: `error`), `Convert: data length output` → (to 16-bit stereo), `StartResample: rate`, `Resample: data frames output` →, `ResampleRoom: frames` →; `rate`, `channels`, `bits`, `frame_bytes`, `data_start`, `data_size`, `milliseconds` |
| `audio.sound` | `Sound` | `PlayFile: path` →, `PlaySong: text` →, `StartFile` →, `StartSong` →, `Pump`, `Position` →, `Stop`; `playing`, `milliseconds`, `error`, `error_line` |

Sound is 16-bit samples, left then right. The card reads it from memory by itself (DMA),
from a ring of 32 buffers of 4 KB (0.75 s at 44100); `Ac97.Write` copies into the ones it
has finished with. When nothing new arrives in time the card plays silence, and it starts
again by itself when more comes. `audio.synth` and `audio.wav` only fill buffers, so they
work in user programs too; [samples/os.rootfs/play.phi](../samples/os.rootfs/play.phi)
plays through the system calls below. QEMU takes some sound ahead of time, so `Drain`
returns a little before the last of it is heard.

## Cryptography

`lib/phi/crypto/` has what TLS 1.3 (`https://`, in `net.tls`) is built from, written in
PHI and checked against the published test vectors. Unlike the network, these
work in any kind of program.

```phi
use crypto.chachapoly;
use crypto.x25519;
use crypto.random;

u8 digest[32];
call Sha256.Hash: 'abc' 3 addr digest;
call Bytes.PrintHex: addr digest 32;         # ba7816bf...

u8 secret[32];
u8 public[32];
call Random.Fill: addr secret 32;
call X25519.Base: addr public addr secret;   # the public half of a key pair
```

| File | Class | What it does |
|---|---|---|
| `crypto.bytes` | `Bytes` | `PrintHex: data length`, `FromHex: text buffer max` → (bytes, or -1), `Equal: a b length` → (takes the same time whatever the bytes are), `Copy: to from length`, `Zero: buffer length` |
| `crypto.sha256` | `Sha256` | `Hash: data length digest`; in pieces: `Init`, `Update`, `Final` on a `Sha256State`, and `Copy: from to` |
| `crypto.hmac` | `Hmac`, `Hkdf` | `Hmac.Mac: key key_length data length output` (or `Start`, `Update`, `Finish`); `Hkdf.Extract: salt salt_length ikm ikm_length prk`, `Hkdf.Expand: prk info info_length output length` |
| `crypto.chacha20` | `ChaCha20` | `Xor: key nonce counter data length` (encrypts or decrypts in place), `Block` |
| `crypto.poly1305` | `Poly1305` | `Start: key`, `Update: data length`, `Finish: tag` |
| `crypto.chachapoly` | `ChaChaPoly` | ChaCha20-Poly1305: `Seal: key nonce header header_length data length tag` (encrypts in place), `Open: ...` → false if anything was changed |
| `crypto.aes` | `Aes` | AES-128: `SetKey: key`, `Encrypt: block output` |
| `crypto.aesgcm` | `AesGcm` | AES-128-GCM: `Seal` and `Open`, like `ChaChaPoly` (with a 16-byte key) |
| `crypto.x25519` | `X25519` | `Base: public secret`, `ScalarMult: output secret their_public` (the shared secret) |
| `crypto.random` | `Random` | `Fill: buffer length`; `random_hardware` says whether the CPU's RDRAND was used |

PHI's math is 32-bit, so the big-number math keeps numbers as many 8-bit pieces, which
keeps every product within 32 bits. An X25519 takes about 35 ms in QEMU. AES looks
bytes up in tables, which a program sharing the CPU could time through the cache. `Random` mixes
RDRAND (QEMU, as `phi` starts it, answers with the host's random numbers) and timing into
a ChaCha20 generator. Without RDRAND it has only timing, which isn't good enough for real
secrets.

## Programs and processes

A 32-bit kernel runs user programs with `proc.process`:

```phi
use proc.process;

phi.Kernel:Kernel
{
    int a: 0;
    int b: 0;
    call a is Process.Spawn: 'ALPHA.BIN';     # -1 if it can't start
    call b is Process.Spawn: 'BETA.BIN';
    int code: 0;
    call code is Process.Wait: a;             # its exit code; -1 if it crashed
    call Process.WaitAll;
}
```

Each program has its own memory: its code and data at `0x40000000`, a 64 KB stack ending
at `0x40400000`, and nothing else it can reach. The timer shares the CPU between running
programs. A program that touches memory that isn't its own is stopped with a message
(`PHI: program 3 stopped by exception 14 ...`) and the kernel carries on.

**The kernel runs one thing at a time.** Its own code keeps the CPU until it waits
(`Process.Wait`, `Process.WaitAll`, `OS.Yield`) or its main code ends; only then do
programs run. Methods keep their variables on the stack, but class variables (like a
driver's state) are shared, and this keeps two pieces of kernel code from changing them at
once.

**Inside a program**, the hardware is out of reach: no ports, interrupt handlers, `new`
or drivers. Instead:

| In a program | What it does |
|---|---|
| `log`, `debug`, `ask` | through the kernel's console and keyboard; a `log` statement reaches the screen whole |
| `exit code;` | end the program; the end of the main code is `exit 0` |
| `OS.Sleep: ms`, `OS.GetTicks` →, `OS.Yield` | timing |
| `OS.ReadKey` →, `OS.KeyAvailable` →, `Bootloader.WaitForKeyPress` → | the keyboard |
| `OS.CurrentTask` → | this program's process number |
| `OS.Spawn: path` → | start another program; its process number, or -1 |
| `OS.Run: path arguments` → | the same, with a command line for it |
| `OS.Arguments` → | this program's command line, as a `ptr<u8>` |
| `OS.Wait: process` → | wait for a program this one started; its exit code |
| `OS.ReadFile: path buffer max` →, `OS.ReadFileAt: path offset buffer max` →, `OS.WriteFile: path data length` →, `OS.DeleteFile: path` →, `OS.FileSize: path` →, `OS.ListFiles: path` → | files on the kernel's disk, like `Fat`'s methods |
| `OS.SoundWrite: data length` →, `OS.SoundRate: hz` →, `OS.SoundQueued` →, `OS.SoundStop` →, `OS.SoundVolume: percent` → | the sound card, like `Ac97`'s methods (`SoundWrite` never waits: it returns the bytes it took, 0 while the card is full); each returns -1 if there's no card |
| `OS.TaskState: process` →, `OS.TaskName: process buffer` →, `OS.Kill: process` → | other programs (`ps`, `kill`) |
| `OS.FreeMemory` →, `OS.TotalMemory` → | memory, in KB |
| `OS.ClearScreen`, `OS.PutCell: column row cell` | the text screen; a cell is a character in the low byte and a color in the high byte |
| `OS.IsKeyDown: key` → | whether a key is held down (for games) |
| `OS.Reboot` | restart the machine |

Every pointer a program passes is checked against its own memory; a bad one makes the
call return -1 instead of reaching the kernel.

### The shell

[samples/os.phi](../samples/os.phi) is a kernel that boots into a shell:

```
phi run samples/os.phi
```

The shell and every command are user programs in
[samples/os.rootfs](../samples/os.rootfs). It reads a line, runs `NAME.BIN` with the rest
of the line as that program's command line, and waits for it (or not, with a trailing
`&`). Its own commands are `help` and `exit`; the others are programs: `ls`, `cat`,
`echo`, `ps`, `kill`, `mem`, `uptime`, `clear`, `reboot`, `pong`, `play`, `volume`, `hello` and `count`.
Adding a command is adding a `.phi` file to the folder.

The system calls behind these (`int 0x80`, number in `eax`, argument in `ebx`, result in
`eax`) are listed in [lib/x86_32/tasks.asm](../lib/x86_32/tasks.asm). A kernel can add
its own from number 16 up with `OS.SetSyscallHandler` (`proc.process` uses 16 for spawn).

## Methods

```phi
[Add: int x: 0; int y: 0;]      # parameters, with defaults
[end: x + y]                    # [end: value] returns a value; [end] returns nothing

[Greet: str who: 'world';]
    log 'hello ' who;
[end]

call Greet;                     # who is 'world'
call Greet: 'PHI';
call total is Add: 2 3;         # store the result

int width: (Add: 2 3) * 8;      # a call in parentheses is a value
if (IsReady: port)
    log (Greeting) '\n';       # (Name) calls a method that takes no arguments
;;

[Find: int wanted: 0;]
    while int i: 0; i < items.len; i++;
        if items:i is wanted
            return i;           # leave the method early, with its result
        ;;
    ;;
[end: -1]
```

- Arguments are separated by spaces and may span lines. `call Move: x -1;` passes two
  arguments; `x - 1` or `(x -1)` is one (see "A list of values" under
  [Variables](#variables)). A missing argument uses the parameter's default (or zero if it
  has none).
- A method can return a number or a `str`.
- **Calls as values.** `(Name: arguments)` can go anywhere a value can: in conditions,
  math, `log`, indexes and other calls' arguments. The space after the `:` is what makes it
  a call; `(list:i)`, with no space, is an element. `(Name)` with no arguments is a call
  when no variable has that name. The method must return a value.
- **Leaving early.** `return value;` ends a method that has `[end: value]`, and `return;`
  ends one that has `[end]`. The value must be the same kind (number or `str`) as the one
  after `[end: ...]`.
- Methods can be called before they appear, and from any class in the same unit
  (`call Other.Method;` to be explicit).
- A method can call itself, directly or through other methods: every call has its own
  parameters and variables (see "Where variables live").

  ```phi
  [Factorial: int n: 0;]
      if n < 2
          return 1;
      ;;
  [end: n * (Factorial: n - 1)]
  ```

- A method's variables take stack space while it runs, at most 4096 bytes per method; put
  large buffers in the class.
- A method called through its address (`addr Handler`, for an IRQ or a system call) gets no
  arguments, so it can't have parameters. A parameter's default can't use the method's
  other parameters.

### Methods as values

A `method<...>` variable holds a method, which is called through it like any method. The
types before the `:` are what it takes, and the type after is what it returns (leave out
`: R` for a method that returns nothing):

```phi
method<int int: int> op: addr Add;          # addr Name gives the method
int sum: (op: 2 3);                         # called like a method
op is addr Multiply;                        # and it can change

method<int int: int> table: addr Add addr Subtract addr Multiply;
call result is table:i: 6 3;                # through an index or a field

struct.Shape
{
    int size;
    method<ptr<Shape>: int> area;           # a method in a struct
}
debug (shapes:i.area: addr shapes:i);

[Apply: method<int: int> f: 0; int x: 0;]   # methods passed to methods
[end: (f: x)]
```

- The method must match the type exactly: the same parameter types in the same order, and
  the same kind of result. Every argument has to be given (defaults don't apply).
- A method variable starts as 0, meaning no method; calling it then stops the program
  (the panic screen in a 32-bit kernel). `if op is 0` checks.
- Methods called this way can't return a `str` yet.
- After the callee, `:` with a space starts the arguments; `table:i` with no space is an index.

### Events

A method named after an event runs when that event happens:

```phi
[OS.TimerEvent]        # 60 times a second, after call OS.SetupInteruptTimer;
    ticks++;
[end]

[OS.KeyboardEvent]     # on every key press and release, after call OS.SetupKeyboardInterupt;
    call key is OS.GetKey;
[end]
```

Events interrupt the main program, so they should be short. Every register is saved and
restored around them. They work the same in 16-bit programs and 32-bit kernels.

### Interrupt handlers

For full control, write the handler yourself and install it:

```phi
call OS.SetInterruptHandler: 0x09 addr Keyboard;   # IRQ 1 (the keyboard) is vector 0x09
call OS.UnmaskIrq: 1;

[isr Keyboard]
    u8 scan: in 0x60;
    # ...
    call OS.EndOfInterrupt: 1;                     # tell the interrupt controller it's handled
[end]
```

An `[isr Name]` method saves and restores every register and returns with `iret`. It
can't take parameters or return a value, and can't be called with `call`. In a 16-bit
program (the BIOS's setup), IRQ 0–7 are vectors `0x08`–`0x0F` and IRQ 8–15 are
`0x70`–`0x77`; in a 32-bit kernel IRQ n is vector `32 + n` (see below).

## Assembly blocks

```phi
asm.Double
{
    mov eax, [{value}]     ; {name} is the address of a class variable
    add eax, eax
    mov [{result}], eax
    mov ecx, {LIMIT}       ; {CONST} is a constant's value; {task.id} a field's address
    ret                    ; blocks must end with ret
}
```

Call them with `call Double;` (or `call asm.Double;`). `call x is Double;` stores what the
block leaves in `eax`. Blocks are only included in a unit that calls them. `arm.` blocks
are parsed but can't be called yet.

In a 16-bit program the code runs in real mode with `ds = es = ss = 0`; you may change
any general-purpose register and `fs`, but you must keep `sp`, `bp` and the other segment
registers. In a 32-bit kernel it runs in protected mode with flat segments; you may change
any general-purpose register, but keep `esp`, `ebp` and the segment registers.

## Files

```phi
use drivers.vga_text;        # loads drivers/vga_text.phi
use support.helpers;         # loads support/helpers.phi
```

`use a.b;` looks for `a/b.phi` next to the file that has the `use`, then next to the main
program, then in the standard library ([lib/phi](../lib/phi)). Each file is loaded once,
and the classes of used files run before the classes that use them.

### Standard library

| File | What it provides |
|---|---|
| `drivers.vga_text` | class `Vga` (both kinds of program): `Clear`, `SetColor: fg bg`, `SetCursor: row column`, `PutChar: c`, `Print: text`, `PrintNumber: n`, scrolling, the hardware cursor |
| `boot.info` | 32-bit kernels: `Boot.info`, a `ptr<BootInfo>` to what the loader found (memory map, boot drive, cursor) |
| `memory.frames`, `memory.paging`, `memory.heap`, `memory.list` | 32-bit kernels: see [Memory](#memory-32-bit-kernels) |
| `drivers.ata` | class `Ata` (both kinds of program): `Read: lba count buffer` →, `Write: lba count buffer` → for the IDE disk |
| `drivers.disk` | class `Disk` (both): the disk's FAT partition as numbered sectors: `partition_found`, `Read`, `Write` |
| `fs.fat16` | 32-bit kernels: see [Files on disk](#files-on-disk-32-bit-kernels) |
| `proc.process` | 32-bit kernels: `Process.Spawn: path` →, `Wait: pid` →, `WaitAll` (see [Programs](#programs-and-processes)) |
| `user.text` | class `Text` (any program): `Length`, `Equal`, `StartsWith`, `SkipSpaces`, `CopyWord: text buffer max`, `AfterWord`, `Append: buffer text size`, `TrimEnd`, `ToNumber` |
| `drivers.rtc` | class `Rtc` (both kinds of program): `Read` fills in `year month day hour minute second` from the CMOS clock |
| `drivers.pci`, `drivers.rtl8139`, `net.net`, `net.dhcp`, `net.dns`, `net.tcp`, `net.tls`, `net.http` | 32-bit kernels: see [The network](#the-network-32-bit-kernels) |
| `drivers.ac97`, `audio.sound` | 32-bit kernels: see [Sound](#sound-32-bit-kernels) |
| `audio.synth`, `audio.wav` | any program: see [Sound](#sound-32-bit-kernels) |
| `crypto.bytes`, `crypto.sha256`, `crypto.hmac`, `crypto.chacha20`, `crypto.poly1305`, `crypto.chachapoly`, `crypto.aes`, `crypto.aesgcm`, `crypto.x25519`, `crypto.random` | any program: see [Cryptography](#cryptography) |

## Built-in functions

Call them like methods. Those marked → return a value (`call x is OS.GetKey;`, or
`(OS.GetKey)` as a value). "Where"
says which kind of program has them; the 16-bit versions use the BIOS, the 32-bit ones
are drivers in [lib/x86_32](../lib/x86_32).

| Function | Where | What it does |
|---|---|---|
| `Bootloader.JumpToSectorTwo` | 16-bit boot sector | load the OS classes from disk and start them |
| `Bootloader.WaitForKeyPress` → | both | wait for a key and return its character |
| `Bootloader.EnableVideoMode` | 16-bit | switch to 320×200 graphics with 256 colors |
| `OS.SetupInteruptTimer` | both | run `[OS.TimerEvent]` 60 times a second |
| `OS.GetTicks` → | 32-bit | milliseconds since the timer started |
| `OS.Sleep: ms` | 32-bit | wait (other interrupts keep running) |
| `OS.SetupKeyboardInterupt` | both | track keys and run `[OS.KeyboardEvent]` on every press and release (in 16-bit programs the BIOS keyboard, `ask` and `WaitForKeyPress` stop working) |
| `OS.GetKey` → | both | the character of the last key pressed or released |
| `OS.IsKeyDown: key` → | both | 1 if the key (e.g. `'w'`) is held down |
| `OS.ReadKey` → | 32-bit | wait for a key press and return its character (Shift gives capitals and symbols) |
| `OS.KeyAvailable` → | 32-bit | 1 if `ReadKey` has a character waiting |
| `OS.SetupMouse` | both | turn on the PS/2 mouse |
| `OS.UpdateMouse` | both | read waiting mouse movement (the functions below do this too) |
| `OS.GetMouseX` → / `OS.GetMouseY` → | both | cursor position (0–310, 0–190) |
| `OS.GetMouseDown` → / `OS.GetMouseUp` → | both | left button state |
| `OS.DrawRectangle: x y width height color` | 16-bit | fill a rectangle (clipped to the screen) |
| `OS.DrawPixel: x y color` | 16-bit | set one pixel |
| `OS.SetInterruptHandler: vector handler` | both | run an `[isr]` method for an interrupt vector |
| `OS.SetIrqHandler: irq handler` | 32-bit | run an ordinary method for a hardware interrupt; end-of-interrupt is sent for you |
| `OS.EndOfInterrupt: irq` | both | acknowledge a hardware interrupt (IRQ 0–15), from an `[isr]` |
| `OS.UnmaskIrq: irq` / `OS.MaskIrq: irq` | both | let a hardware interrupt through, or block it |
| `OS.EnableInterrupts` / `OS.DisableInterrupts` | both | `sti` / `cli` |
| `OS.Yield` | 32-bit, programs | let other tasks run |
| `OS.CurrentTask` → | 32-bit, programs | this task's number (0 is the kernel) |
| `OS.Spawn: path` → / `OS.Run: path arguments` → / `OS.Wait: process` → | programs | start a program from the disk / wait for it |
| `OS.ClearScreen`, `OS.PutCell: column row cell`, `OS.Reboot` | 32-bit, programs | the text screen; restarting |
| `OS.KillTask: task` → | 32-bit | stop a task (programs use `OS.Kill`) |
| the file, process and memory calls | programs | see [Programs](#programs-and-processes) |
| `OS.StartMultitasking`, `OS.CreateUserTask`, `OS.TaskState` →, `OS.TaskExitCode` →, `OS.TaskParent` →, `OS.FreeTask`, `OS.SetSyscallHandler`, `OS.SyscallFrame` → | 32-bit | the task machinery `proc.process` is built on |

`OS.SetupInterruptTimer` and `OS.SetupKeyboardInterrupt` (spelled correctly) also work.
`WaitForKeyPress` and `EnableVideoMode` can be called with `OS.` too. In a 32-bit kernel,
`GetTicks`, `Sleep`, `ReadKey` and `ask` start the timer or keyboard driver themselves.

### Interrupts in a 32-bit kernel

Every kernel starts with an interrupt table and interrupts enabled. The interrupt
controllers are remapped so **IRQ n is vector 32 + n** (the timer is 32, the keyboard 33).

- A **CPU exception** (dividing by zero, a bad memory access, an invalid instruction)
  shows the **panic screen**: the exception's name and number, its error code, the
  instruction address (`eip`) and every register, on a red screen and on COM1. Then the
  CPU stops.
- For a **hardware interrupt**, `call OS.SetIrqHandler: 1 addr KeyPressed;` runs an
  ordinary method each time IRQ 1 fires and sends end-of-interrupt afterwards. The IRQ
  is unmasked for you.
- For full control, `call OS.SetInterruptHandler: vector addr Handler;` points a vector
  straight at an `[isr]` method (which must call `OS.EndOfInterrupt` for hardware IRQs).
  This is also how software interrupts (`int 0x80` in an `asm.` block) are handled.

## Not supported yet

- `dec` / `fin` (decimal numbers), `has`, `^^` (power)
- division by zero isn't caught in 16-bit programs (in a 32-bit kernel it shows the panic screen)
- growable strings (use a `str` buffer, or `memory.list` for growable data)
- placing code or data at chosen addresses (`@org`, `@section`); the kernel's layout is fixed
  (see [memory-map.md](memory-map.md))
- networking in user programs (only 32-bit kernels have the network), checking TLS
  certificates (`https://` is encrypted, but the server isn't verified), accepting incoming
  connections (TCP is client-only), and network cards other than the RTL8139
- recording sound, sound cards other than the AC'97, and compressed audio (MP3, Ogg, ADPCM WAV files)

## How it is built

`phi build` runs the compiler and then NASM:

1. **Lexer** (`Syntax/Lexer.cs`): text to tokens
2. **Parser** (`Syntax/Parser.cs`): tokens to a syntax tree, for the program and every file it uses
3. **Binder** (`Semantics/Binder.cs`): resolves names, works out types, storage and struct
   layouts, and reports errors
4. **Code generator** (`CodeGen/X86Generator.cs`): one NASM file per unit (`boot.asm` and
   `os.asm` for a 16-bit program, `kernel.asm` for a 32-bit one), plus only the library
   routines the program uses, from [lib/x86_16](../lib/x86_16) or [lib/x86_32](../lib/x86_32)
5. **NASM** assembles them into a 1.44 MB disk image; a 32-bit kernel also gets the boot
   stages from [lib/boot](../lib/boot). The layouts are in [memory-map.md](memory-map.md).

The `.asm` files are kept in the build folder, with each source line shown above the code
it produced.

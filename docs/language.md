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

## Reserved words

These words are part of the language everywhere, so none of them can be the name of a
variable, field, parameter, constant, method or struct:

| Kind | Words |
|---|---|
| Types | `int` `i32` `u32` `i16` `u16` `i8` `u8` `i64` `u64` `f16` `f32` `f64` `byt` `bln` `chr` `str` `var` `dec` `fin` `ptr` `method` `lock` |
| Structure | `phi` `struct` `enum` `interface` `asm` `arm` `use` `const` `volatile` `isr` `end` `into` |
| Control | `if` `elif` `else` `while` `each` `when` `unsafe` `break` `continue` `return` `call` `exit` `atomic` `locked` |
| Comparing and logic | `is` `not` `and` `or` `has` `below` `above` `atmost` `atleast` `true` `false` |
| Memory and ports | `new` `free` `addr` `sizeof` `as` `takes` `empty` `at` `in` `inw` `ind` `out` `outw` `outd` |
| Input and output | `log` `debug` `ask` |

Using one as a name is an error that suggests other names for the common ones:
`'at' is a keyword and can't be a variable name; try 'pos' or 'offset'`. It's reported
once, where the name is declared; later uses (`out++ 4;`) read as the name rather than
adding errors of their own.

## Values

| Kind | Examples |
|---|---|
| Numbers | `42`, `-7`, `0x2A`, `101010b` (binary), `1_000` |
| Booleans | `true`, `false` (stored as 1 and 0) |
| Strings | `'hello'`; `'hasn't'` works without escaping |
| Escapes | `\r` `\n` `\t` `\0` `\\` `\'`, and `\xNN` for any byte by its two hex digits (`'\xED'` is φ in the VGA font) |
| Characters | a one-character string like `'w'` is its character code wherever a number is expected; where text goes (a `str`, a `ptr<u8>` parameter, `log`) it's still text |
| Constants | `Colors.Black` … `Colors.White` (the 16 VGA colors), and your own `const` |

A quote followed directly by a letter or digit doesn't end a string, which is why
`'hasn't'` works. Strings can span several lines. A number too big for `int` (like
`0x80000000`) is a `u32`.

## Variables

| Type | Size | Values |
|---|---|---|
| `f64` / `f32` / `f16` | 8 / 4 / 2 bytes | floating point numbers: about 15, 7 and 3 digits (32-bit kernels and programs; see below) |
| `i64` / `u64` | 8 bytes | about ±9.2 × 10^18 / 0 to about 1.8 × 10^19 (32-bit kernels and programs; see below) |
| `int` (also `i32`) | 4 bytes | -2147483648 to 2147483647 |
| `u32` | 4 bytes | 0 to 4294967295 |
| `i16` / `u16` | 2 bytes | -32768 to 32767 / 0 to 65535 |
| `i8` / `byt` (also `u8`) | 1 byte | -128 to 127 / 0 to 255 |
| `bln` | 1 byte | `true` or `false` |
| `chr` | 1 byte | a character: a number from 0 to 255 that `log` and text write as its character |
| `str` | its capacity | text (see below) |
| `ptr<T>` | 4 bytes | an address (see [Pointers](#pointers)) |
| `method<T ...: R>` | 4 bytes | a method that can be called (see [Methods as values](#methods-as-values)) |
| a struct | its fields | see [Structs](#structs) |
| `new` struct | 4 bytes | an object PHI allocates and frees (see [Objects](#objects)) |
| `var` | | `str` if every value is a string, otherwise `int` |

```phi
int count: 0;
u16 port: 0x3F8;
bln ready: false;
str name: 'PHI';
int total;                  # no value: zero

int numbers: 1 3 5 9 2;     # several values make an array
str days: 'Mon' 'Tue';      # an array of strings (a warning if any value isn't a literal)
int squares[5];             # an array of 5 zeros
str buffer: [40];           # room for 40 characters (also: str buffer[40];)
u8 sectors[SECTOR_SIZE];    # sizes can be constants

str text: count;            # a number written out as text (an f64 or u64, too)
str copy: line;             # line is a ptr<u8>: the text it points to
```

`=` works in place of `:` in declarations. Storing a value in a smaller type keeps the low
bytes (so `u8` wraps around at 256).

**Characters.** A `chr` is a byte that's a character: `log` and building a `str` write it
as its character, where a `u8` (a plain byte) is written as its number. A `str`'s
characters (`name:0`) are `chr`. Math on one gives a number, and `as chr` makes a number a
character again:

```phi
int key: 104;
str word: '';
word++ key as chr;              # 'h', not 104
log word:0 ' ' (word:0 + 1) ' ' (word:0 as int);   # h 105 104
chr c: 'a';
c is (c + 1) as chr;            # 'b'
```

**64-bit numbers.** `i64` and `u64` hold what doesn't fit in 32 bits: sizes of big files,
money in cents, a product before it's divided. Math with one on either side happens in 64
bits (unsigned if either side is a `u64`), and literals past 32 bits are `i64`:

```phi
i64 bytes: 5000000000;
int a: 2000000;
int b: 3000000;
i64 scaled: (a as i64) * b / 1000;      # 6000000000: no overflow in the middle
log bytes * 4 ' ' scaled;               # printed, compared and built into text like any number
int low: bytes as int;                  # to 32 bits only with `as`, which keeps the low half
```

**Floating point numbers.** `f64`, `f32` and `f16` hold fractions and very big or small
numbers. A number with a point or an exponent (`2.5`, `6.02e23`, `1e-5`) is one, and math
with one on either side is floating point, in the wider of the two kinds:

```phi
f64 pi: 3.14159265358979;
f32 radius: 2.5;
int n: 3;
f64 area: pi * radius * radius;
log area ' ' (n / 2.0) ' ' 6.02e23;        # 19.634954 1.5 6.02e23
int whole: area as int;                     # 19: to a whole number only with `as` (toward zero)
log (Float.Sqrt: 2) ' ' (Float.Round: 2.5); # 1.414214 2
```

- Whole numbers become floating point wherever one goes, but a floating point number
  becomes a whole number only with `as`, which drops what's after the point.
- `log` and building a `str` write up to 6 digits after the point, without zeros at the
  end, and an exponent from 10^12 up and under 0.0001; `inf`, `-inf` and `nan` too.
- `%` gives what's left over with the left's sign (`-7.5 % 2` is -1.5, like C's `fmod`);
  `~`, `&`, `|`, `^` and the shifts work only on whole numbers.
- `Float.Sqrt`, `Abs`, `Sin`, `Cos` (radians), `Round` (halves to even), `Floor`, `Ceil`
  and `Trunc` each take one number and give an `f64`.
- `f16` is for storing many numbers in little memory: it keeps about 3 digits, up to
  65504. Math on any kind happens with more precision than any of them, and is rounded
  when it's stored.
- They work as variables, fields, array elements, parameters, results, method values
  and a `when`'s value, in 32-bit kernels and programs. Each program keeps its own floating
  point registers when the CPU switches between them, but an interrupt handler shouldn't
  use them.

Putting a 64-bit number where a 32-bit number goes (an `int`, an index, a parameter) is an
error, since it may not fit: write `as int` to keep its low 32 bits. They work as variables, fields,
array elements, parameters, results, method values and a `when`'s value, and need 32-bit
code: a 16-bit program can't have them.

**Size of a `str`.** A `str` grows as text is written to it, up to its max (`.max`):

- `str s: [40];` holds up to 40 characters. Its room is fixed when it's declared, so it
  works as a buffer for methods that write into it (`sizeof s` is 41, with the ending zero).
  `str s: [atmost 40];` and `chr s[40];` are other ways to write it.
- `str s: 'abc';`, `str s: '';`, `str s: someNumber;` and `str s: otherStr;` (no size) grow
  up to 65,535 characters in a 32-bit kernel or a user program. The text is on the heap,
  which PHI adds and frees for you: a method's own `str` gives its text back when the
  method ends. Starting with `''` and adding to it is the usual way to build text:

  ```phi
  str report: '';
  while int i: 0; i < count; i++;
      report++ names:i ', ';
  ;;
  ```

  Because its room changes, a `str` without a size can't be handed to a method as a buffer
  to write into: `sizeof s` is an error, and so is passing `s.max` where a size goes. Give it
  a size for that. `ask` reads a line of up to 255 characters into one.
- In a 16-bit program there's no heap, so a `str` without a size is fixed: it holds the
  length of its first value (`'abc'`: 3), 11 for a number, as much as another `str`, or 64
  for a `ptr<u8>`.
- Each element of a `str` array grows the same way, in a 32-bit kernel or a user program
  (`str days: 'Mon' 'Tue'; days:1 is 'Tuesday';`). In a 16-bit program each holds as much
  as the longest initial value.

Text longer than the max is cut to fit, without an error: after
`str tiny: [3]; tiny is 'abcdef';`, `tiny` is `abc`. A `str` with a size takes one more byte
than it holds, for the zero that ends the text.

**How a `str` array is stored.** A `str` array is an array of 4-byte pointers, one per
element, and each points to that element's text (in 32-bit code each element's room
follows the table, and an element that grows moves its text, updating its pointer). So
`days:1` is the address of the second string, and code that walks the array's memory by a fixed stride (for rows of a
sprite, say) must use a `u8` array instead (`u8 rows[8 * 16];`, indexed as
`rows:(row * 16 + column)`). Where a `ptr<ptr<u8>>` is expected, a `str` array is the address
of its table of pointers, so a method can take any `str` array:

```phi
str invader: '..X..' '.XXX.' 'X.X.X';
call Sprite: invader invader.len;

[Sprite: ptr<ptr<u8>> rows: 0; int count: 0;]
    while int i: 0; i < count; i++;
        debug rows:i '\n';         # each element is a ptr<u8>, which prints as text
    ;;
[end]
```

**A list of values** is separated by spaces, which applies to declarations, call arguments,
`log` and `out`. A `-` with a space before it and none after starts a new, negative value:
`int steps: 1 -1 2 -2;` has four values, and `call Move: x -1;` passes two arguments.
With spaces on both sides or neither (`x - 1`, `x-1`) it subtracts. Inside parentheses it
always subtracts. Since `x -1` is easy to misread, a negative number right after a name or
expression in a call is a warning; write `x - 1` to subtract, or `(-1)` if two values are
meant. After a number (`5 -1`) it's plainly a list, and there's no warning.

**Where variables live.** Variables declared directly in a class have a fixed place in
memory and are set once, when the program is built. A method's parameters and variables
(including its loop counters) live on the stack, in a new set for every call: they start
at zero, and are set again each time their declaration runs. So a method can call itself,
and an interrupt handler can safely call a method the main code is also running. A pointer
to a method's variable (`addr total`) is only good until the method returns.

**Scope.** A name is looked up in the current method, then the current class, then in
other classes of the same unit (if only one class has it). `Class.name` picks a specific
class. When a name found that way is a variable of a class in another file, it's a warning
(`'dirty' is a variable of Wm, in wm.phi; write Wm.dirty to say so`), since in a large
program a typo or a missing declaration would otherwise bind to it silently. Classes in the
same file share names freely.

`x.len` is how many are in `x` now, the number to count up to when going through it: the
elements of an array, or the characters of a `str`. `x.max` is the most `x` can hold: the
elements of an array (the same as `.len`), or the characters that fit in a `str`. `sizeof x`
is the bytes it takes, which for a `str` includes the ending zero.

```phi
int scores[5];
str name: [20];
name is 'Ada';
log scores.len ' ' scores.max;     # 5 5
log name.len ' ' name.max;         # 3 20
log sizeof scores ' ' sizeof name; # 20 21
log list:0.name.len;               # an array field, through an element
```

An array's `.len` and `.max`, and a `str`'s `.max`, are known when the program is built,
so they work as array sizes and in constants. Both give way to a struct's own field of
that name. Passing a `str`'s `.len` where a size goes (`call Text.Append: s 'x' s.len;`)
is a warning: it's how much text is there now, often 0, so nothing would be written.

An array or `str` kept in place (a variable, a field) holds at most 16,384 elements or
characters. A bigger buffer goes on the heap: `ptr<u8> text: new u8[60004];` (32-bit code).

**Whole arrays.** `b is a;` copies every element of `a` into `b`, and `(a is b)` is true
when they have the same number of elements and each is equal. Both must hold the same kind
of element. A fixed array takes one of the same length; an `atmost` array takes any that
can't hold more than it can, and its `.len` becomes the source's. Objects are copied and
compared by value, and `str` elements by their text.

```phi
int a: 1 2 3;
int b[3];
b is a;                       # 1 2 3
b:1 is 20;
log (a is b);                 # 0
int some[atmost 10];
some is a;                    # some.len is 3
```

**Arrays that grow.** An array without a size, `int scores[];`, starts empty and grows as
`++` adds to it, with no most (in a 32-bit kernel or a user program: its elements are on
the heap). `.len` is how many it has, and `.max` the room it has right now. Everything
else works as for any array: indexes (checked against `.len`), `.len is` and `.len--` to
shrink it, copying (`scores is other;`, which grows it to fit), comparing, and passing it
to a method that takes `int numbers[]`. A method's own are let go when it ends.

```phi
int scores[];
scores++ 42;
scores++ 7 9;
new Node nodes[];             # objects too
nodes++;
```

A `str` array grows the same way, `str names[];`, and each of its elements is text that
grows too (`names:0 is 'a much longer name';`, `names++ 'ann' 'bob';`). Shrinking it gives
the dropped elements' texts back, and a method's own goes back with its texts when it ends.
It can be passed to a method that takes `str rows[]` (which can make its texts longer),
and copied and compared whole with any `str` array (`copy is names;` grows it to fit, and
gives back the texts it no longer needs). Not yet: struct fields without a size.

**Arrays of text.** A `str` array follows the same rules as any array, and each element is a
`str` (32-bit code):

```phi
str names[atmost 64];        # filled as it goes; each element grows
names++ 'Ann' 'Bob';
names++ count;               # a number is written out as text
names:1++ ' Smith';          # an element is a str like any other
log names.len ' ' names:1.len;

str slots[3];                # three, each starting empty
str lines[8]: [40];          # eight texts of up to 40 characters, kept in place
```

`str lines[8]: [40];` (and `[atmost 8]: [40]`) keeps each text in the array's own bytes, so it
works in a struct too, and in 16-bit code. The others keep a table of where each element's
text is, like a `str` array of values (see below), so they can go where a `ptr<ptr<u8>>`
goes (`call list.SetItems: names names.len;`, or `call list.SetList: names;`) and to a `str
rows[]` parameter.

**Arrays filled as they go.** `T name[atmost N]` has room for `N` elements and starts
empty. `++` adds to its end, `.len` is how many are in it, and an index is checked against
`.len`, not the room, so an element that was never added can't be read by mistake:

```phi
int scores[atmost 10];
scores++ 42;
scores++ 7 9;                 # several at once
log scores.len ' ' scores.max;     # 3 10
log scores:2;                 # 9
log scores:5;                 # index out of range: only 3 are in it

while int i: 0; i < scores.len; i++;
    log scores:i;
;;

scores.len--;                 # drop the last one
scores.len is 0;              # empty it
```

- Adding to a full one stops the program with an index error, like a bad index.
- `.len` can only be made smaller (`is` or `--`), so an element that was dropped can't come
  back with its old value.
- An array of structs adds an element of zeros with `name++;` on its own, then sets its
  fields: `list++;` then `list:(list.len - 1).score is 10;`, or adds copies of other
  structs with `list++ best;` (a variable, field or element). An array of objects adds a new
  one with `list++;`, or a copy with `list++ p;` (see [Arrays of objects](#arrays-of-objects)).
- It works as a struct field too (`int members[atmost 4];` in a struct), and as a
  method's variable.
- The count is a `u32` kept after the elements, so `sizeof` is 4 more than a plain array's,
  and passing one where a pointer goes passes its elements as usual. A method that takes
  the pointer gets no count, so pass `.len` with it.
- `unsafe` skips the check, as for any array. A `str` already knows how long its text is,
  so it can't be `atmost`.

A method's variable can be used anywhere in the method after its declaration, including
after the block it was declared in. A name can be declared again in a *separate* block
(the other branch of an `if`, another loop, or after a loop that declared it), and each
declaration is then a variable of its own, seen only in its block:

```phi
if to < from
    while int i: 0; i < count; i++;    # this i is the loop's
        ...
    ;;
;;
else
    int i: count - 1;                   # and this one is the else block's
    ...
;;
```

Declaring a name again where the first one is still in sight (in the same block, or in a
block inside it) is an error. Two loops that declare the same counter share it, as before.

### Constants

```phi
const u16 COM1: 0x3F8;
const u16 COM1_STATUS: COM1 + 5;
const int BUFFER_SIZE: 512;
```

A constant's value must be known when the program is built. Constants take no memory and
can be used as array sizes and inside `asm.` blocks (`{COM1}`).

### volatile

`volatile` before a variable, a field or a pointer marks memory that changes by itself: a
device's registers, a buffer a network card writes, a counter an interrupt handler
changes. Every read and every write of it goes to the memory, however the code generator
gets better at keeping values in the CPU's registers:

```phi
volatile ptr<u16> status: 0xFEBF0010;      # a device's register
while (status:0 & 1) is 0                  # read again each time round
;;

struct.Device
{
    volatile u32 ready;
}
```

Today every read goes to memory anyway, so `volatile` changes nothing yet; mark such
memory now, so code keeps working as the optimizer grows (see Phase S6 in
[Plan.md](../Plan.md)).

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
| `x as T` | `x` used as type `T` (see [Pointers](#pointers)) |
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
- The index after `:` is a number, a single name, a class's constant or variable
  (`handlers:Wm.NOTEPAD`), or something in parentheses: `cells:(row * 80 + column)`.
  A dotted name after `:` counts as a class's member when it starts with a capital letter
  and has a lowercase letter in it (class names are PascalCase), or when what follows the
  dot is in capitals; otherwise `tasks:i.id` is a field of the element, `(tasks:i).id`.

  | Written | Means |
  |---|---|
  | `tasks:i.id` | field `id` of element `i` |
  | `windows:(order:i).minimized` | element `order:i` of `windows`, then its field |
  | `Users.list:(Users.current).zone` | another class's array, by another class's variable, then a field |
  | `windows:Apps.PAINT.open` | element `Apps.PAINT` (a constant: in capitals), then its field |
  | `(Users.Name):0` | the first element of what a call returned (a `ptr<u8>`'s first character) |
  | `(Twice: 3)` / `(list:3)` | a call (a space after `:`) / an element (none) |

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
name is line;  # line is a ptr<u8>: copies the text it points to (cut to fit)
```

`b is h is 64;` stores whether `h is 64` in `b`, which reads ambiguously, so it's a warning;
write `b is (h is 64);`.

**Building a `str`.** A `str` takes several values, read the way `log` reads them: each is
written in as text (numbers in decimal), one after another, and `++` adds to the end.

```phi
str status: [40];
status is 'Ln ' line + 1 ', Col ' column + 1;   # Ln 5, Col 12
status++ ' (' (Text.Length: name) ' chars)';
status is status ' more';                        # the str itself can be the first value
```

As in any list, `'Total' -3` is two values. The text is cut to fit, like any `str`. The
`str` can't appear after the first value (it's being rewritten by then), and `s++ s;` is an
error: copy it to another `str` first. Only a `str` takes several values: `n is n -1;`
still subtracts.

The values can carry on over several lines, as a declaration's can, when each line after
the first is indented deeper than the statement's first line. The same goes for `log` and
`debug`. A line that isn't indented deeper starts a new statement, so a missing `;` is
still an error.

```phi
note is 'Welcome, ' name '!\n\n'
	'This is your home folder.\n';
```

Building is an assignment, not a declaration: `str docs: home '/DOCS';` is an *array* of two
strings, since several values in a declaration make an array. That gets a warning when
any of the values isn't a literal; write `str docs: ''; docs is home '/DOCS';` for the
text. A `str` array is never text: passing one where a `ptr<u8>` goes is an error (pass
an element, `docs:0`).

`p + n` with `p` a `ptr<u8>` is the text from `n` characters in, wherever text is being
printed or built (`log p + 1;`, `s++ p + 1;`), as it is in `[end: p + n]`; elsewhere it's
a number.

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

A guard fits on one line: when `return`, `break`, `continue`, `call` or `exit` follows the
condition on the same line, that one statement is the whole `if`, with no `;;`. It can't
have an `elif` or `else`, and anything else after the condition (`if k is 'p' tool is 1;`)
is an error at that line.

```phi
if key is 27 return true;
if done break;
if count is 0 call Show: 'empty';
```

### when

`when` picks one branch by a value: the first `is` line with an equal value runs, or
`else` if none does. The value is worked out once, and any number of values can share a
branch. Each branch, the `else`, and the `when` itself end with `;;`:

```phi
when (Input.NextKey)
    is 'q' 27
        exit 0;
    ;;
    is Input.KEY_LEFT
        x--;
    ;;
    else
        log 'not a key this game uses\n';
    ;;
;;
```

The value can be a number (an `i64` or a floating point one too), an [enum](#enums) or
text (`when command` / `is 'ls' 'dir'`).
A value written in two branches is an error. A `when` on an enum with no `else` gets a
warning for each of the enum's values it leaves out.

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

`while each x in list` goes through an array's elements in order, without a counter.
`x` is a copy of each one, or for an array of structs or objects the element itself, so
changing `x.field` changes the array. It works with any array (fixed, `atmost`, one that
grows, or one a method was given), and checks how many there are each time round.

```phi
int total: 0;
while each score in scores
    total++ score;
;;

while each p in points
    p.x is 0;                   # each point in the array
;;
```

The same name can be used in another `while each` loop over the same kind of array.

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
array stops the program with `PHI: index out of range on line N in FILE.phi`. A constant index is
checked when the program is built instead (and also when it runs, for an
[`atmost` array](#variables), against how many are in it). Code inside `unsafe` isn't
checked:

```phi
unsafe
    buffer:i is 0;      # no check: you promise i is in range
;;
```

Pointers are never checked.

A loop that keeps its counter in range needs no check on each element, so in 32-bit code
it gets none: `while u32 i: 0; i < 4; i++;` indexing an array of 4 or more with `i`, or
`while int i: 0; i < list.len; i++;` indexing the array parameter `list`, as long as
nothing in the loop changes `i` (a signed counter must also start at zero or more and
count up). Any other index is still checked.

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
log t.id;                       # a field of what t points to (the same as t:0.id)
t.next.id is 3;                 # through a pointer field too
```

- Pointers can reach any address below 1 MB (in 16-bit mode, through the `fs` segment).
- **Fields through a pointer.** When `p` is a `ptr<T>` to a struct, `p.field` is
  `p:0.field` and `call p.Method;` calls `T`'s method with `p` as `self`. Each `.` goes
  through one pointer: a `ptr<ptr<T>>` needs an index first (`pp:0.field`), and so does an
  element of an array of pointers (`ptrs:i.field`). `p.len` is only ever a field of `T`,
  never a length. Use `p:i.field` for the other elements when `p` points at several.
- `addr x` is a pointer to what `x` holds: `addr buffer` of a `u8` array is a `ptr<u8>`,
  `addr task` of a `Task` is a `ptr<Task>`.
- An array, or an array field like `tasks:0.name`, used where a pointer is expected (a
  parameter, a pointer variable) is its address, the same as `addr`.
- `addr Method` is the address of a method, for interrupt handlers (and see
  [Methods as values](#methods-as-values)).
- **`as`** uses a value as another type: `w:0.data as ptr<Note>`, `count as u8` (a smaller
  number keeps its low bytes). Numbers, pointers and methods can become each other this way.
  One kind of pointer silently becoming another (`ptr<A>` given where `ptr<B>` goes) is a
  warning, since it's usually a mistake; `as` says it's meant. `ptr<u8>` goes to and from any
  pointer without one.
- Memory reached through a pointer is read and written every time, and so are class
  variables. A method's own number and pointer variables may be kept in registers; mark a
  variable [`volatile`](#volatile) when something else changes it.

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
    str name: [12];        # text of up to 12 characters
    u8 flags[8];           # and fixed arrays
    ptr<Task> next;        # and pointers, including to themselves
}
```

```phi
Task current;              # starts as all zeros
Task tasks[8];

current.id is 1;
current.position.x is -5;
current.name is 'Idle';
tasks:i.id is i;
tasks:i.position.y is 10;
```

Fields are laid out in order with no padding, which is what hardware tables (like the GDT
or a disk's partition table) need. In 32-bit code, `a is b` copies one struct into another
and `if a is b` compares them, as for [objects](#objects). A struct parameter borrows the
caller's struct rather than copying it. In 16-bit code, work with the fields one at a time.

**Text in a struct.** `str name: [12];` holds text of up to 12 characters in the struct
itself (13 bytes, with the zero that ends it). It works like any `str`: it's assigned, built,
added to, compared, printed and passed where a `ptr<u8>` goes, with no `addr` or `sizeof`
needed, and `.len` and `.max` work on it. Copying the struct copies its text, and it can be
written to a file or shared by a kernel and its programs, since the text is part of the
struct's own bytes. `str name: [atmost 12];`, `chr name[12];` and `chr name[atmost 12];` mean
the same thing: an array of characters is text. A field needs its size, or `new`:

**Text that grows, in a struct.** `new str bio;` is text that grows on the heap, like a `str`
variable without a size, and the struct owns it. It works like any `str` (`is`, `++`, `.len`,
`.max`, comparing, passing it to a `str target;` parameter) and starts as `''`. As with a `new`
object field, the struct is then one PHI manages: it's declared with `new`
(`new Account a;`, `new Account list[atmost 8];`), copying it copies the text, comparing it
compares the text, and the text is freed with it (see [Objects](#objects)). Its bytes hold
where the text is, not the text, so `sizeof` of the struct is an error: a struct that's
written to a file or shared by a kernel and its programs uses `str bio: [200];` instead.

```phi
struct.Account
{
    str id: [8];          # in place: on its own, Account would stay a plain struct
    new str bio;          # grows on the heap: Account is managed
}

new Account a;
a.bio is 'Ada wrote';
a.bio++ ' the first program';
new Account b: a;         # b has its own copy of the text
```

```phi
struct.Account
{
    str name: [24];
    str home: [64];
}

a.name is 'Ada';
a.home is 'HOME/' a.name;
log a.name ' (' a.name.len ' of ' a.name.max ')';      # Ada (3 of 24)
if a.home is 'HOME/Ada' ...
call Draw.Text: 10 10 a.name color;
```

### Methods on structs

A struct can have methods, written after its fields. They're called on a struct value, and
work on that one: inside, its fields are reached by name, and `self` is a pointer to it.

```phi
struct.Counter
{
    int count;
    int step;

    [Add: int times: 1;]
        count++ step * times;           # this counter's fields
    [end: count]

    [AddTwice]
        call Add;                       # another method, on the same counter
        call Add;
    [end: count]
}

Counter c;
call c.Add: 3;                          # on a variable
int n: (c.AddTwice);                    # as a value, like any call
call counters:i.Add;                    # on an element, a field, or through a pointer (p.Add)
method<ptr<Counter> int: int> f: addr Counter.Add;   # as a method value, self comes first
```

- A struct's methods are part of the program's main code: a kernel, a user program, or
  the OS classes of a 16-bit program (the boot sector, if that's all there is).
- From outside the struct, a method is always called on a value (`c.Add`), never as
  `Counter.Add` on its own.

### Interfaces

An interface names methods that different structs share. A struct that `has` it must
write each one, and a variable of the interface holds an object of any of those structs.
Calling a method on that variable runs the method of whichever struct it holds.

```phi
interface.Shape
{
    [Area]
    [Grow: int by;]
    [Describe: str target;]
}

struct.Circle has Shape
{
    int r;

    [Area]
    [end: 3 * r * r]

    [Grow: int by;]
        r++ by;
    [end]

    [Describe: str target;]
        target is 'a circle of radius ' r;
    [end]
}

new Shape s: (MakeCircle: 2);       # takes over the call's new Circle
log (s.Area);                       # Circle's Area: 12
call s.Grow: 1;
new Rect box: 3 4;
s is box;                           # a copy of box; the circle is freed
new Shape all[atmost 8];
all++ box;                          # copies too
while each shape in all
    log (shape.Area) '\n';
;;

[AreaOf: Shape shape;]              # borrows a Shape, or a new Circle or Rect
[end: (shape.Area)]
```

- **An interface lists method headers, with no body or `[end]`.** Its parameters are
  written as any method's are: `str target;` is the caller's own text, and `str name: '';`
  is a copy. Other parameters have no defaults, so a call through it gives every argument.
- **What a method returns comes from the structs.** Every struct with the interface has to
  return the same, and the first one says what that is. For now it's a number (or nothing).
- **A struct can have several interfaces** (`struct.Player has Shape Named { ... }`), and it's
  an error to leave out one of their methods, or to take something different.
- **An interface variable owns its object**, like a `new` variable: it's declared with
  `new` (in methods, classes, arrays and a struct's fields), starts empty unless it's given
  a value, and its object is freed with it. `is` copies, `takes` hands an object over,
  `is empty` frees it, and `if a is b` is true when both hold the same struct with the same
  values.
- **What goes into one is an object**: a call's new object, or a copy of one a `new`
  variable, field or element holds. A struct stored in place (`Circle c;`) can't go in,
  because every object of a struct that has an interface keeps what struct it is just
  before its fields, and one stored in place doesn't.
- **A parameter of the interface borrows**, as any struct parameter does, and it can't be
  given another object.
- The fields of a struct that has an interface are laid out as for any struct; `sizeof` is
  the same. The 4 bytes that say what it is are only in front of its objects on the heap.

## Enums

An enum gives names to numbers, as a type of its own. Each name is one more than the one
before it, starting at 0, unless it says otherwise:

```phi
enum.Tool
{
    PENCIL          # 0
    BRUSH           # 1
    ERASER: 10
    FILL            # 11
}

Tool tool: Tool.PENCIL;
struct.Stroke
{
    Tool tool;      # in structs, arrays and parameters, like any type
    int width;
}
```

Only an enum's own values go into it, and it's only compared with them, so a value from
one set of names can't be mixed up with another (`Wm.MOUSE_DOWN` with `Paint.LINE`): those
are errors. An enum is 4 bytes and goes anywhere a number does (`names:tool`,
`log tool` prints its number), and `as` converts both ways: `n as Tool`, `tool as int`.
`when` is the usual way to act on one.

## Objects

An object is a struct that PHI allocates and frees. Declare it with `new`; there is no
`free` to call. Each object has exactly one owner, so two variables never share one.

```phi
struct.Point
{
    i16 x;
    i16 y;
}

struct.Node
{
    u32 value;
    Point where;               # stored inside the Node, as in any struct
    new Node next;             # a separate object that this Node owns; starts empty
}

new Point a: 1 2;              # zeroed memory from the heap, then the fields in order
new Point b;                   # all zeros
new Node list: empty;          # no object yet
a.x is 5;                      # fields by name, as in a struct

b is a;                        # copies a's values into b's memory; a and b stay separate
if a is b                      # compares values, not addresses
    log 'same\n';
;;
```

- **`new` is only for structs**, and only where memory is allocated: a variable, or a field
  that holds a separate object. Numbers, inline struct fields (`Point where;`) and arrays of
  structs are stored where they're declared, as before.
- **The values after `:` fill the fields in order**; any left over are zero. A single object
  value is copied instead (`new Point c: a;`), a call's new object is taken over
  (`new Point c: (Make: 1 2);`), and `empty` starts with no object.
- **A `new` field starts empty.** Using an empty object's fields stops the kernel with a page
  fault on the panic screen.

### When objects are freed

| Owner | Freed |
|---|---|
| A method's `new` variable | when the method ends, at `[end]` or a `return` |
| A `new` field | when the object holding it is freed |
| A class's `new` variable | never; it lives as long as the program |
| A `new` variable declared in a loop | its memory is emptied and reused each time round |

### Copying, handing over and comparing

```phi
a.next is b;                   # a copy of b, including everything b owns (a deep copy)
a.next is empty;               # frees a.next
node.next takes head;          # node.next gets head's object without copying; head is now empty
head takes node;
if head.next is not empty      # does it hold an object?
    ...
;;
```

- **`is` copies values, never addresses.** Copying an object copies the objects it owns, so
  copying a list copies every node. Copying into an empty field makes a new object for it.
- **`takes` hands an object over** from one owner to another (a `new` variable or field)
  and leaves the giver empty. Building a list with `takes` costs nothing per node; building
  it with `is` would copy the list each time. A field can't take the object it's inside
  (`a.next takes a` is an error), because that object would own itself.
- **`if a is b` compares values.** Two objects are equal when their fields are, following
  `new` fields to compare the objects they hold (both empty is equal, one empty isn't).
  Fields that are `ptr<T>` compare as addresses.
- Long chains (a list's `next`) are freed, copied and compared with a loop, not recursion,
  so a long list can't use up the stack.

### Methods and objects

```phi
[Move: Point p; i16 dx: 0;]    # a struct parameter borrows: no copy, no new
    p.x++ dx;                  # changes the caller's object
[end]

[Make: i16 x: 0; i16 y: 0;]
    new Point made: x y;
[end: made]                    # handed to the caller, so it isn't freed here

call Move: a 3;
call b is Make: 1 2;           # b takes the new object (and frees what it had)
```

- A struct parameter is the caller's object (or struct, or `ptr<T>`) for the length of the
  call. It's written without `new`, and it can't have a default.
- A method that returns an object always gives the caller one of its own: one of the
  method's `new` variables is handed over, anything else is copied. Its result goes into
  something that can take it over: `call x is ...;`, `x is (...);`, or a `new` declaration.
- Struct methods work on objects as on structs: `call a.Add: b;`, `(shape.Area)`.

### Objects and pointers

`addr a` is a plain `ptr<Point>` to an object's memory, which is how code walks a list
without copying it. It's only good while the object's owner is alive. An object can be
given where a pointer goes (`walk is walk.next;`), but a pointer can't be put in an
object: to copy what it points at, use `p:0`.

```phi
ptr<Node> walk: addr list;
while walk is not 0
    log walk.value '\n';
    walk is walk.next;
;;
```

### Objects at an address

`new T name at address;` is a `T` over memory that's already there, such as device memory
or a table the BIOS left. Nothing is allocated or freed, and `at` has no `:` because no
value is assigned. It works in 16-bit programs and kernels (not user programs), and the
struct can't have `new` fields.

```phi
new Cell corner at 0xB8000;    # the first character of the VGA text screen
corner.letter is 'P';
```

### Arrays of objects

`new T name[N]` is N objects, each zeroed and ready to use; `new T name[atmost N]` starts
with none, and `++` adds them. Each element is an object the array owns, so it works like a
`new` variable or field:

```phi
new Point list[3];
list:0.x is 1;                  # its fields
list:1 is p;                    # a copy of p's values
list:2 takes q;                 # q's object, handed over (q is empty after)
list:2 is empty;                # freed
if list:1 is p ...              # compared by value
log (Sum: list:1);              # borrowed by a method that takes a Point

new Point added[atmost 8];
added++;                        # a new zeroed Point at the end
added++ p q;                    # copies of p and q
added.len--;                    # the last one is freed
```

A struct can have one too: `new Point corner[4];` in a struct is four objects it owns,
each starting empty like a single `new` field (give one a value with `is` or `takes`, or
use `atmost` and `++`). Copying the struct copies them, comparing compares them, and
freeing it frees them. A struct that owns objects can only be in an array of objects
(`new Shape shapes[4]`), not stored in place (`Shape shapes[4]`).

A method's own arrays of objects are freed when it ends, along with everything in them.

### Limits

Objects that PHI allocates need a heap, so they're in 32-bit kernels and user programs
(each gets one automatically). Methods that return objects can't be used as method values.

## Memory (32-bit kernels)

[Objects](#objects) are the usual way to get memory. For code that manages memory itself,
`new` also works as a value, giving a plain pointer that's freed with `free`:

```phi
ptr<Task> task: new Task;           # one Task, all zeros
ptr<u8> buffer: new u8[512];        # 512 bytes, all zeros
task.id is 7;
free task;
free buffer;
```

`new` gives memory from the kernel heap and `free` gives it back. A kernel that uses them
gets the memory library automatically: at startup it reads the BIOS memory map, turns on
paging and sets up a heap of a quarter of free memory (at least 8 MB; about 31 MB on
QEMU's default 128 MB machine). `new` returns 0 when the heap has no room (for an object,
the kernel stops with `PHI: out of memory for a new object on line N`). Freeing a pointer
`new` didn't give out (or freeing twice) stops the kernel with a message. Freeing a
`ptr<T>` to a struct with `new` fields frees the objects it owns too.

Paging maps every address to the same physical address, but only memory that exists:
**a null pointer, or an address past the end of RAM, stops the kernel with a page fault**
on the panic screen, showing the address that was used.

The pieces can also be used directly:

| File | Class | What it does |
|---|---|---|
| `memory.frames` | `Frames` | physical memory in 4 KB frames: `Alloc` →, `AllocContiguous: count` →, `Free: address`; `free_frames`, `total_frames`, `memory_top` |
| `memory.paging` | `Paging` | identity paging with page 0 unmapped; `page_directory`, `mapped_bytes` |
| `memory.heap` | `Heap` | `Alloc: size` →, `Free: address`, `LargestFree` →; `used_bytes`, `used_blocks`, `heap_size`. Free blocks are kept on lists by size, so `Alloc` takes about as long with 100,000 blocks in use as with ten |
| `memory.list` | `List` | a growable list of u32 values: `New` →, `Add: list value`, `Get: list index` →, `Set: list index value`, `Delete: list`; the list's `count` and `capacity` |
| `memory.pool` | `Pool` | many objects of one size, handed out and taken back without searching: `New: size per_chunk` →, `Alloc: pool` → (zeroed), `Free: pool object`, `Delete: pool` (all of them at once); the pool's `used` and `capacity`. Freeing twice, or into the wrong pool, stops the program |
| `data.map` | `Map` | u32 keys to u32 values (an ID to an index or a pointer): `New` →, `Set: map key value`, `Get: map key otherwise` →, `Has: map key` →, `Remove: map key` →, `Next: map after` → (walks every key: `KeyAt`, `ValueAt`), `Clear`, `Delete`; the map's `count` |
| `data.queue` | `Queue` | records of one size, oldest out first: `New: size capacity` →, `Push: queue record` → (false when full), `Pop: queue into` → (false when empty), `Front: queue` → (the oldest, where it is), `Drop: queue`, `Clear`, `Delete`; the queue's `count` |

`memory.pool`, `data.map` and `data.queue` work in user programs too, on the program's
heap. A server keeps its players like this:

```phi
use memory.pool;
use data.map;

ptr<PoolData> players: (Pool.New: sizeof Player 256);
ptr<MapData> by_id: (Map.New);

ptr<Player> p: (Pool.Alloc: players);
p.id is 1042;
call Map.Set: by_id p.id p;

ptr<Player> found: (Map.Get: by_id 1042);       # 0 if there's no player 1042
int pos: (Map.Next: by_id);                     # every player
while pos >= 0
    call Tick: (Map.ValueAt: by_id pos);
    pos is (Map.Next: by_id pos);
;;
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
| `Append: path data length` → | add to the end of a file (creating it if need be), writing the data, then the FAT, then the new size: a machine that stops part way leaves the file as it was |
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

**Saving as things change.** `fs.savelog` keeps records of one size in a file, each with a
CRC-32 (`data.crc32`), added with `Fat.Append`. A kernel adds a record whenever something
changes, and reads them all back when it starts again. A record cut short, or damaged,
ends the reading there:

```phi
use fs.savelog;

struct.SavedPlayer
{
    u32 id;
    i32 x;
    i32 y;
}

SavedPlayer record;
call SaveLog.Add: 'PLAYERS.LOG' addr record sizeof SavedPlayer;       # false if it couldn't be written
int count: (SaveLog.Read: 'PLAYERS.LOG' sizeof SavedPlayer addr Restore);   # -1 if there's no log
log SaveLog.torn;                       # true if it stopped at a damaged record
call SaveLog.Clear: 'PLAYERS.LOG';

[Restore: ptr<u8> bytes: 0;]            # each record, oldest first
    ptr<SavedPlayer> r: bytes as ptr<SavedPlayer>;
    # ...
[end]
```

A log only grows, so a kernel that keeps one for a long time reads it, clears it, and adds
back the latest record of each thing (the mmo sample's world, `samples/mmo/server.rootfs/world.phi`, does this when it starts).

## Several cores (32-bit kernels)

`phi run` and `phi test` give the machine 4 cores (`--cores n` for 1 to 8). A kernel starts
on one of them; `proc.cores` starts the others and runs work on them:

```phi
use proc.cores;

u32 data[4096];
u32 sums[8];
int n: (Cores.Start);          # how many cores there are in all, this one included
call Cores.Run: addr Part;     # Part: 0 here, Part: 1, 2 ... on the others; back when all are done

[Part: int core: 0;]           # this core's share: every n-th number, from its own
    u32 sum: 0;
    int i: core;
    while i < 4096
        sum++ data:i;
        i++ Cores.count;
    ;;
    sums:core is sum;
[end]
```

- The other cores wait, halted, between `Run`s; `Run` wakes them with an interrupt, and
  each runs the method with its own number on a stack of its own.
- Work on other cores can share data through `atomic` and `locked` (below). The heap
  (`new` and `free`) and `Frames` take a lock of their own, so any core can use them, and
  each `log` (or `debug`) comes out whole: a kernel takes a lock around the whole
  statement once other cores have started, so two cores' lines never mix. **The rest of
  the library doesn't yet**: work on another core mustn't use files, the network or the
  `OS.` calls, which assume one core.
- Interrupts from devices and the timer still go to the first core only.
- Up to 8 cores take part; any more stay halted.

### atomic and locked

`atomic` before a statement makes it one step that no other core can come between: the
number in memory is read, changed and written back at once.

```phi
u32 hits: 0;
atomic hits++;                       # lock add: from any number of cores, none is lost
atomic hits-- 3;
atomic owner is 7;                   # an exchange
bln won: atomic owner is 9 when 7;   # compare and swap: true if it was 7 (and is 9 now)
if atomic slot:2 is 42 when 0        # also as a statement, or anywhere a value goes
    log 'claimed\n';
;;
```

A `lock` is a variable (or a field) that a `locked` block takes and gives back. The block
waits while another core has it, runs with it held, and gives it back however it's left:
at its end, or by `return`, `break` or `continue`:

```phi
lock counter_lock;
u32 counted: 0;

locked counter_lock
    counted is counted + 1;           # only one core at a time is in here
    if counted > 100 return;          # the lock goes back here too
;;
```

- `atomic` works on a variable, field or element that is a number, `bln` or pointer of up
  to 32 bits. A method's variable used with it stays in memory (never in a register).
- A `locked` block turns interrupts off on its own core while it holds the lock, so an
  interrupt handler that wants the same lock can't wait forever for the code it
  interrupted. Keep locked blocks short.
- A lock starts free; it can't have a value (`lock guard;`), and isn't a number.
- Both need 32-bit code: 16-bit code runs on one core.

## The network (32-bit kernels)

`phi run` and `phi test` give every machine an RTL8139 network card (or, with `--nic
virtio`, a virtio-net card, which is what cloud machines have) on QEMU's user network,
which reaches the internet through the host. The network works the same on either card.
`net.http` fetches web pages:

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
| `drivers.nic` | `Nic` | whichever card the machine has: `Start` →, `Send: frame length` →, `Receive: buffer max` → (0 when nothing is waiting), `Dropped` →; `mac`, `kind` (`Nic.RTL8139` or `Nic.VIRTIO`) |
| `drivers.rtl8139`, `drivers.virtio_net` | `Rtl8139`, `VirtioNet` | the two cards, with the same calls as `Nic`; `frames_dropped`, `interrupts` |
| `net.net` | `Net` | `Start` → (by DHCP), `Start: address mask router` → (no DHCP), `Configure: ip netmask gateway dns`; `ip`, `netmask`, `gateway`, `dns`, `dhcp_ok`; `Ping: address timeout` → (ms, or -1); `SendUdp: to from_port to_port data length` →, `Listen: port`, `WaitUdp: timeout` → (the datagram is in `udp_data`); `ParseIp: text` →, `PrintIp: address` |
| `net.udp` | `Udp` | sockets: `Open: port queue` → a socket or -1 (port 0 picks one), `Receive: s datagram` → (never waits), `Wait: s datagram timeout` →, `SendTo: s address port data length` →, `Close: s`; a `UdpDatagram` has `from`, `from_port`, `length` and `data`; each socket's `received` and `dropped` |
| `net.dhcp` | `Dhcp` | `Configure` → (`Net.Start` does this) |
| `net.dns` | `Dns` | `Resolve: name` → an address, or 0 |
| `net.tcp` | `Tcp` | clients: `Connect: address port` → a connection or -1, `Write: c data length` → (waits until it's all taken), `Read: c buffer max` → (waits for something: 0 once the other side has closed, -1 on a timeout), `Close: c`; `timeout` (ms, 10000). Never waiting: `Begin: address port` → a connection still connecting, `State: c` → (1 open, 0 connecting, -1 failed), `Take: c buffer max` → (0 if nothing yet, `Tcp.CLOSED_AND_READ` once closed). Servers, never waiting: `Listen: port backlog` → a listener or -1, `Accept: listener` → a connection or -1, `StopListening: listener`, `Waiting: c` → bytes to read (-1 once closed), `Send: c data length` → how much fitted, `Address: c` →; `open_count`, `retransmits` |
| `net.calls` | `NetCalls` | the network for user programs: `use` it in the kernel (see below) |
| `net.tls` | `Tls` | one TLS 1.3 connection: `Connect: address port host` → (false: `error` says why), `Write: data length` →, `Read: buffer max` → (0 once the server has closed), `Close`; `suite`, `alert`, `verified` |
| `net.http` | `Http` | `Get: url buffer max` → the response's size or -1, for `http://` and `https://`; `status`, `body` (where the body starts), `error` |

Addresses are `u32`s with the first number in the top byte (`10.0.2.15` is
`0x0A00020F`). On QEMU's network the kernel is `10.0.2.15`, the gateway `10.0.2.2` (which
is also the host computer's `127.0.0.1`), and the DNS server `10.0.2.3`. `Net.Start` asks
for an address by DHCP first; if no server answers it keeps these addresses and
`Net.dhcp_ok` is 0.

The card's interrupt moves arriving frames from the card (its 8 KB ring, or the virtio
card's 256 buffers) into a queue of up to 256 in the driver, and `Net.Poll` handles them
from there, outside the interrupt. So a kernel
can be busy for a while without losing frames; the methods that wait (`Ping`,
`Dns.Resolve`, `Tcp.Read`, ...) poll, and sleep a millisecond when nothing has come.

Up to 1,024 TCP connections can be open at once, each on the heap with an 8 KB buffer each
way. What's sent stays in its buffer until it's acknowledged: as much goes out as the other
side has room for, and what isn't acknowledged in time goes again (after 200 ms, then
twice as long each time, 8 times before the connection counts as broken). Segments that
come early are kept and joined up when the gap fills. A server looks after many
connections at once without waiting:

```phi
use net.tcp;

int listener: (Tcp.Listen: 5000);
while true
    int c: (Tcp.Accept: listener);       # a new connection, or -1
    if c >= 0
        call Remember: c;
    ;;
    # for each connection: what's arrived, and what to send
    int ready: (Tcp.Waiting: c);         # bytes to read, or -1 once it's closed
    if ready > 0
        int got: (Tcp.Read: c addr buffer 2048);
    ;;
    int taken: (Tcp.Send: c reply length);   # 0 when its buffer is full: try again later
    call Net.Wait;
;;
```

There's no congestion control, which suits a local network more than the internet.

A server answers on a UDP socket. Each socket keeps what arrives for its port in a queue
of its own until `Receive` takes it, so a game's tick can read everything that came since
the last one:

```phi
use net.udp;

bln ok: (Net.Start: 0x0A000301);         # 10.0.3.1, without asking DHCP
int s: (Udp.Open: 4000 1024);            # port 4000, keeping up to 1,024 datagrams
UdpDatagram d;
while true
    while (Udp.Receive: s addr d)        # everything that's waiting
        call Udp.SendTo: s d.from d.from_port 'ok' 2;
    ;;
    call Tick;
;;
```

What arrives when a queue is full is dropped and counted (the socket's `dropped`, and
`Rtl8139.frames_dropped`): UDP has no way to slow a sender down.

### The network in programs

A user program uses the network through its kernel: the kernel has `use net.calls` and
starts the network, and the program has `use net.sockets`, which gives it `Udp`, `Tcp`
and `Net` with the same methods as the kernel's. So code moves between a kernel and a
program by changing its `use` line:

```phi
# in the kernel
use proc.process;
use net.calls;
bln ok: (Net.Start);
int code: (Process.Wait: (Process.Spawn: 'ZONE.BIN'));

# zone.phi, a program
use net.sockets;
int s: (Udp.Open: 4000);
UdpDatagram d;
while (Udp.Receive: s addr d)
    call Udp.SendTo: s d.from d.from_port 'ok' 2;
;;
```

What differs:

- A program doesn't start the network; `Net.Address` is its machine's address, or 0 if
  the kernel hasn't started one.
- A program waits by sleeping (`Tcp.Read`, `Tcp.Connect`, `Udp.Wait`), so the kernel and
  the other programs carry on meanwhile. Each system call does one step that never
  waits.
- Every socket, connection and listener belongs to the program that opened it. It can't
  use anyone else's (the kernel's included), and when it ends, even by crashing,
  they're all closed.
- A socket's queue holds at most 256 datagrams, and a listener's backlog at most 256.

## Graphics and input (32-bit kernels and programs)

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
  screen only, over the picture. `Screen.SetPointer: Screen.POINTER_TEXT` changes its shape
  (`POINTER_ARROW`, `POINTER_TEXT` for an I-beam, `POINTER_CROSS` for crosshairs), and
  `SetPointerRows: rows x y` uses one of your own: 19 rows of 12 characters (`'k'` black,
  `'w'` white, `'.'` clear) with the pixel at the mouse's position at (x, y).
- **Views.** `Draw.SetView: x y w h` moves the origin to (x, y) and cuts all drawing to that
  rectangle; `ResetView` undoes it. `Draw.Target: pixels w h` draws on a block of pixels of
  your own instead, like an image being built (it resets the view). `Draw.PushTarget: pixels
  w h` does the same until `PopTarget`, which goes back to what was being drawn on with the
  view and clip it had, so drawing on a canvas can't leave the screen's view wrong (8 deep).
- **Clipping inside a view.** `Draw.PushClip: x y w h` cuts drawing to that rectangle as well
  (relative to the origin, and never wider than the clip already in force); `PopClip` puts the
  clip back. They nest, 16 deep. A `SetView` inside a `PushClip` stays inside its rectangle,
  and `ResetView` there goes back to it rather than to the whole screen.
- **Shapes:** `Clear`, `FillRect`, `Rect`, `Pixel`, `GetPixel`, `HLine`, `VLine`, `Line`,
  `Circle`, `FillCircle`, `Ellipse` and `FillEllipse` (`cx cy rx ry`), `Gradient` (top color
  to bottom color), `Blit` (copy an image). Colors are `0xRRGGBB`; `Draw.Rgb: r g b` makes
  one, and `Draw.RED` ... name a few.
- **See-through color.** `Draw.BlendRect: x y w h color alpha` lays a color over what is
  already drawn (alpha 0 to 255; `Draw.BLACK 64` darkens by a quarter, for shadows), and
  `(Draw.Mix: color under alpha)` works out one blended color.
- **Text:** `Char`, `Text` and `Number` (both return their width), `TextWidth`, `Centered`,
  `TextFitted: x y w text color` (cut to `w` pixels, ending in `...`),
  `TextScaled: x y text scale color` (each pixel of the font drawn `scale` x `scale`), and
  `TextN: x y text count color` (the first `count` characters, for part of a buffer).
- **Keys** from `Input.NextKey` are characters (`'a'`, `'A'`, 13 for Enter, 27 for Esc) or
  `Input.KEY_UP`, `KEY_F1` to `KEY_F12` ... for keys that don't type one, with `MOD_SHIFT`, `MOD_CTRL` and
  `MOD_ALT` added for the modifiers held (`key & Input.KEY_MASK` is the key alone).
  `(Input.IsDown: 'a')` says whether a key is held now. `Input` replaces the built-in keyboard
  driver, so don't also use `ask` or `OS.GetKey`.
- **The mouse** is `Input.mouse_x`, `Input.mouse_y` and `Input.buttons` (bit 0 left, bit 1
  right, bit 2 middle), kept inside the area given to `SetArea`. Those say what is true now,
  so a loop that looks between redraws can miss a whole click; `Input.NextMouse` can't. It
  takes the oldest button change or wheel turn (64 are kept) into `Input.event_buttons`,
  `event_x`, `event_y` (where the mouse was) and `event_wheel`. `event_clicks` is 1 when the
  left button went down and 2 when that made a double click (a second press within
  `Input.DOUBLE_CLICK_MS`, 450 ms, a few pixels from the first):

  ```phi
  while (Input.NextMouse)
      if Input.event_clicks is 2
          call Open: Input.event_x Input.event_y;
      ;;
      elif Input.event_clicks is 1
          call Clicked: Input.event_x Input.event_y;
      ;;
      call Scroll: Input.event_wheel;    # down is positive
  ;;
  ```

  The wheel needs a mouse that speaks the IntelliMouse protocol, as QEMU's does
  (`Input.has_wheel`); `Input.wheel` adds up every turn. `FlushMouse` forgets events not
  taken yet.
- `Paging.MapDevice: address bytes` makes other device memory reachable the same way.

[samples/sketch.phi](../samples/sketch.phi) is a small drawing program using all three.

### Windows for programs

A GUI app can be a [user program](#programs-and-processes) of its own, so a bug in it ends
only that program. It opens a window with `gui.app`, draws on it with the same `Draw`
(and the kernel's font), and takes events:

```phi
use gui.app;

phi.Hello:Program
{
    if (App.Open: 200 100) is false     # w x h pixels for it to draw on
        exit 1;
    ;;
    call Draw.Clear: Draw.WHITE;
    call Draw.Text: 10 10 'Hello' Draw.BLACK;
    call App.Present;                   # show what's been drawn
    while true
        call App.WaitEvent;             # into App.event (a GuiEvent, gui.events)
        if App.event.kind is Events.CLOSE
            exit 0;
        ;;
        # Events.KEY: App.event.value is the key, with Input's MOD_ bits
        # Events.MOUSE: .x .y (from its top left), .value (buttons held), .clicks (1, or 2 for a
        # double click), .pressed (the buttons that just went down: bit 1 is the right one), .wheel
    ;;
}
```

The kernel decides where each window goes and who gets the input. `gui.desktop` does it
all: a window with a title bar and close box for each program, the front window taking
the keys, clicks bringing a window to the front, dragging by the title bar, and the close
box asking the program to close (it gets `Events.CLOSE`). A program that ends, or crashes,
loses its window.

```phi
use gui.desktop;

if (Screen.Start: 800 600)
    call Desktop.Start;
    call Desktop.Run: 'HELLO.BIN' 'Hello' 40 40;   # start a program in a window
    while Desktop.windows.len > 0
        call Desktop.Step;              # input, events, drawing (only when something changed)
        call OS.Idle;                   # the programs run, or it rests until something happens
    ;;
;;
```

Dragging a window's bottom right corner resizes it. When the button is let go the
program gets `Events.RESIZE`, with the new size in `App.event.x` and `.y`: it calls
`App.Resize: w h` and draws again.

**What a program with a window can ask for.** On top of everything a
[program](#programs-and-processes) can do, `gui.app` gives it:

| Call | What it does |
|---|---|
| `App.Open: w h` →, `App.Resize: w h` → | its pixels, drawn on with `Draw`; false if the kernel can't make them |
| `App.Present` | show what's been drawn |
| `App.NextEvent` →, `App.WaitEvent` | the next event, into `App.event` |
| `App.SetTitle: title` | name its window |
| `App.OpenWindow: path arguments` → | start another program, with a command line, in a window of its own; its process number, or -1 |
| `App.Focused` → | true while its window is in front, getting the keys |
| `App.SetResizable: false` | its window keeps its size (for a program that doesn't handle `Events.RESIZE`) |
| `OS.IsKeyDown: key` → | whether a key is held, for games; only the program in front sees held keys |

A program can't move, size or close its own window, or see other windows: those are the
window manager's. `gui.desktop` is one window manager; a kernel can write its own on
`gui.surfaces`, which keeps each program's pixels and events and takes these requests
(`Surfaces.TakeTitle`, `TakeNewWindow`, `Resizable`, and `Surfaces.front`, which the window
manager keeps set to the program in front). A window manager with its own keyboard driver sets
`Process.key_down` to a method that answers `OS.IsKeyDown` (`gui.desktop` does).

`gui.widgets` has the usual parts of a form, each a struct that keeps its own place and
state. Its `Mouse` and `Key` say what happened, and the program draws everything again
after each event:

```phi
use gui.widgets;

TextField name;         # a line to type in: caret, arrows, Home/End, Backspace/Delete, selection
CheckBox shout;
Button add;
ListBox list;           # rows from a str array, a scroll bar, the wheel and arrow keys
call name.Place: 60 10 190;
call shout.Place: 60 42 'Shout';
call add.Place: 170 38 80 24 'Add';
call list.Place: 10 72 240 180;
call list.SetItems: fruits fruits.len;

# after App.WaitEvent:
if (add.Mouse: App.event) ...           # true: clicked (pressed and let go on it)
if (shout.Mouse: App.event) ...         # true: ticked or cleared (shout.on)
int picked: (list.Mouse: App.event);    # 1: a row picked (list.selected), 2: double-clicked
int typed: (name.Key: App.event.value); # 1: changed, 2: Enter, 3: Esc
call name.Mouse: App.event;             # a click gives it the keys (name.focused)

call Draw.Clear: Widgets.FACE;
call name.Paint;                        # ... and each of the others, then App.Present
```

A `TextField` selects text with Shift and the arrows, Home or End, with Ctrl+A, with a drag
or with a double click, and typing replaces what's selected. `SelectAll`, `SelectionStart`,
`SelectionEnd` and `CopySelection: buffer size` → work with it from the program. A
`FocusChain` moves the keys between text fields with Tab and Shift+Tab, and selects the text
of the field it moves to:

```phi
FocusChain tabs;
call tabs.Add: addr name;
call tabs.Add: addr city;
call tabs.Focus: 0;                     # name has the keys
int typed: (tabs.Key: App.event.value); # Tab moves on; other keys go to the field with the keys
call tabs.Mouse: App.event;             # a click picks a field
call tabs.Paint;                        # paints every field
```

Key codes in `App.event.value` are `drivers.input`'s, also named in `Events`
(`Events.KEY_ENTER`, `KEY_LEFT`, `MOD_CTRL` ...), since a program can't use
`drivers.input`.

A kernel with a window manager of its own uses `gui.surfaces` underneath:
`(Surfaces.TakeChanged: pid)` says a program showed a new picture, `Surfaces.DrawAt: pid x
y` draws it (through `Draw`, so cut to the view), and `PostKey: pid key`, `PostMouse: pid x
y buttons clicks wheel` and `PostClose: pid` queue events for it (32 each). The system calls
underneath are `OS.SurfaceOpen: w h`, `OS.SurfacePresent: pixels`, `OS.NextEvent: event` and
`OS.CopyFont: glyphs` (64 to 67); `proc.process` hands calls from 64 up to
`Process.more_calls`, so other libraries can add their own.

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
| `crypto.pbkdf2` | `Pbkdf2` | for storing passwords: `Derive: password password_length salt salt_length iterations output length`, PBKDF2-HMAC-SHA256. Keep the salt (random, from `Random.Fill`), the count and the result; to check a password, derive it again and compare with `Bytes.Equal`. 1,000 rounds are about 39 million instructions |
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
at `0x40400000`, a heap above that, and nothing else it can reach. The timer shares the CPU between running
programs. A program that touches memory that isn't its own is stopped with a message
(`PHI: program 3 stopped by exception 14 ...`) and the kernel carries on.

**The kernel runs one thing at a time.** Its own code keeps the CPU until it waits
(`Process.Wait`, `Process.WaitAll`, `OS.Yield`, `OS.Idle`) or its main code ends; only then do
programs run. A main loop that waits on programs and input should call `OS.Idle`: like
`OS.Yield` it lets the programs run, but when none of them can, it waits for the next
interrupt (the timer, a key, the mouse) instead of coming straight back, so the CPU rests. Methods keep their variables on the stack, but class variables (like a
driver's state) are shared, and this keeps two pieces of kernel code from changing them at
once.

**Objects and `new` work in a program too.** Its heap
([memory.program_heap](../lib/phi/memory/program_heap.phi), added automatically) starts
empty and grows when nothing in it fits, asking the kernel for at least 64 KB at a time,
up to 124 MB (and only while the kernel has memory to spare). `new` gives 0 when there's no
more, and an object that can't get memory ends the program with
`PHI: out of memory for a new object on line N`. Freeing a pointer `new` didn't give out
ends it with -1. All of it goes back to the kernel when the program ends.

```phi
phi.Big:Program
{
    ptr<u8> pixels: new u8[800 * 600 * 4];   # more than a fixed array can hold
    new Node first: 1;                       # objects, freed by PHI
    ...
}
```

**Inside a program**, the hardware is out of reach: no ports, interrupt handlers or
drivers. Instead:

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
| `OS.ReadFile: path buffer max` →, `OS.ReadFileAt: path offset buffer max` →, `OS.WriteFile: path data length` →, `OS.AppendFile: path data length` →, `OS.DeleteFile: path` →, `OS.FileSize: path` →, `OS.ListFiles: path` → | files on the kernel's disk, like `Fat`'s methods (`AppendFile` is `Fat.Append`); a buffer can be an array (`u8 text[512]`) or a pointer |
| `OS.ListFolder: path into max` →, `OS.MakeFolder: path` →, `OS.Rename: from to` →, `OS.DeleteFolder: path` → | folders, like `Fat.Entries`, `MakeFolder`, `Rename` (which can move, too) and `DeleteFolder` (only an empty one); `ListFolder` fills an array of `FileInfo` (from `use fs.fileinfo;`, the same struct `Fat` uses) and gives how many, or -1 |
| `OS.SoundWrite: data length` →, `OS.SoundRate: hz` →, `OS.SoundQueued` →, `OS.SoundStop` →, `OS.SoundVolume: percent` → | the sound card, like `Ac97`'s methods (`SoundWrite` never waits: it returns the bytes it took, 0 while the card is full); each returns -1 if there's no card |
| `OS.TaskState: process` →, `OS.TaskName: process buffer` →, `OS.Kill: process` → | other programs (`ps`, `kill`) |
| `OS.FreeMemory` →, `OS.TotalMemory` → | memory, in KB |
| `OS.Grow: bytes` → | more memory at the end of the heap (whole pages, zeroed); where it starts, or 0. `new` does this for you |
| `OS.ClearScreen`, `OS.PutCell: column row cell` | the text screen; a cell is a character in the low byte and a color in the high byte |
| `OS.IsKeyDown: key` → | whether a key is held down (for games); under `gui.desktop`, only for the program whose window is in front |
| `OS.Reboot` | restart the machine |

Every pointer a program passes is checked against its own memory; a bad one makes the
call return -1 instead of reaching the kernel.

The file calls and `OS.TaskName` and `OS.Kill` work in a kernel too: there they are the
kernel's own versions (`OS.ReadFile` is `Fat.ReadFile`, `OS.ListFolder` is `Fat.Entries`,
`OS.TaskName` is `Process.TaskName`, `OS.Kill` is `OS.KillTask`), so code shared by a kernel
and its programs reads and writes files the same way. `memory.mem` (`Mem.Copy`, `Mem.Fill`
...) works on both sides as well.

```phi
use fs.fileinfo;

phi.Ls:Program
{
    FileInfo entries[64];
    int count: (OS.ListFolder: 'DOCS' entries entries.len);    # -1 if there's no such folder
    while int i: 0; i < count; i++;
        log addr entries:i.name ' ' entries:i.size '\n';
    ;;
}
```

A program with a window has a few more calls, from `gui.app`: see
[Windows for programs](#windows-for-programs). One that uses the network has `net.sockets`:
see [The network in programs](#the-network-in-programs).

### Mailboxes

Programs send each other records (a zone handing a player to another, say) through
mailboxes the kernel keeps. The kernel has `use proc.mail`, and programs `use user.mail`;
both have `Mail` with the same calls:

```phi
struct.Handover
{
    u32 player;
    int x;
    int y;
}

int box: (Mail.Open: 'zone2' sizeof Handover 64);   # records of this size, 64 waiting at most
Handover h;
while (Mail.Receive: box addr h)          # never waits; Mail.Wait: box addr h ms does
    log 'player ' h.player ' from ' Mail.sender '\n';   # a process, or 0 for the kernel
;;
call Mail.Send: (Mail.Find: 'zone1') addr h;        # false if it's full, or there's no such box
```

- A box has a name of up to 15 characters, a record size of up to 1 KB, and room for up
  to 256 records. `Mail.Open` gives -1 if the name is taken.
- Anyone can `Find` a box and `Send` to it. Only its owner (the program that opened it,
  or the kernel) can `Receive` from it, `Close` it or `Wait` on it, and `Mail.sender` says
  who sent each record, so it can be answered.
- A box closes when its program ends, even by crashing; what the program sent to other
  boxes stays there. Up to 32 boxes are open at once.
- A box's number changes each time its place is used again, so an old number reaches
  nothing (`Send` gives false), even once a new box has the same name.
- `Mail.Wait` sleeps in a program, and lets the programs run in the kernel.

### Keeping programs running

`proc.supervisor` starts a program and starts it again whenever it stops with an error
(a crash, or an exit code other than 0). A program that saves what it has as it goes
(`user.savelog` is the kernel's `SaveLog` for programs, with the same calls) reads it
back when it starts, so a zone that crashes comes back with its players:

```phi
use proc.supervisor;

int zone: (Supervisor.Keep: 'ZONE.BIN');   # started now; -1 if it can't be
while (Supervisor.Running: zone)           # false once it ends with 0, or is given up on
    call Supervisor.Check;                 # never waits: starts again any that stopped
    call OS.Yield;                         # the programs run
;;
log Supervisor.restarts:zone;              # how many times it was started again
```

- Up to 8 programs are kept, each by a path of up to 15 characters.
  `Supervisor.ProcessOf: zone` is its process number now, which changes each time it's
  started again.
- One that keeps stopping within a second of starting is given up on after
  `Supervisor.give_up_after` (5) restarts in a row; `last_code` is how it last ended.
- `Process.Ended: pid` is what it uses: true once a program has ended, with its exit
  code in `Process.exit_code`, without waiting the way `Process.Wait` does.

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
  arguments (and warns); `x - 1` or `(x -1)` is one (see "A list of values" under
  [Variables](#variables)). A comparison is one argument too: `call Show: count is 0;`. A
  missing argument uses the parameter's default (or zero if it has none).
- **Text parameters.** A `str` parameter with a default (`str text: '';`, `str text: [20];`)
  is a copy of the caller's text. One with no default (`str target;`, or `chr target[];`) is the
  caller's own text, as a struct parameter is the caller's struct (32-bit code). The
  method reads it, or fills it in: what it writes goes into the caller's `str`, which grows
  if it can or is cut to fit if it has a size. Any text can be passed to one the method only
  reads, a literal, a `ptr<u8>` or a number. One the method writes needs a `str` variable,
  field or element to write into, and passing anything else is an error.

  ```phi
  [TaskName: int pid: 0; str target;]
      target is 'task ' pid;
  [end]

  str name: '';
  call TaskName: 3 name;        # name is 'task 3'
  call TaskName: 3 account.name;
  ```

  `target.len` is its text's length and `target.max` the most it holds: the caller's size, or
  65,535 for a `str` that grows. Because it may grow, it can't be handed to a method as a
  buffer with a size; write it with `is` and `++` instead.
- A method can return a number, a pointer, a `str` or an object. A result keeps its type
  at the call: a method whose `[end: ...]` is a `ptr<u8>` gives text to `log` and to a
  `str`, and a `ptr<T>` result can be indexed. Moving a pointer keeps its type there too:
  `[end: path + start]` with `path` a `ptr<u8>` is still text. A `ptr<u8>` method can also
  `return 'text';` (a string literal lives as long as the program; a method's own `str`
  doesn't).
- A `str` result is copied into a temporary that lasts until the end of the statement, so
  it can be passed on (`call Draw.Text: x y (Name) color;`) or stored in a `str`, but not
  kept by a pointer: `ptr<u8> t: (Name);` is an error. For text that outlives the call,
  have the method end in a `ptr<u8>` (see [Common patterns](patterns.md#returning-text-from-a-method)).
- A method's own variables, and the text of its own `str`s and `str` arrays, are let go
  when it ends. So a pointer into them can't be handed back or kept in a class variable,
  even through another pointer (`ptr<u8> name: names:n; [end: name]` is an error). Declare
  the array in the class instead, or return the text as a `str`.
- **Calls as values.** `(Name: arguments)` can go anywhere a value can: in conditions,
  math, `log`, indexes and other calls' arguments. The space after the `:` is what makes it
  a call; `(list:i)`, with no space, is an element. `(Name)` with no arguments is a call
  when no variable has that name, and so is `(f)` or `(apps:i.closing)` when that is a
  method variable or field that takes no arguments (see [Methods as values](#methods-as-values)).
  The method must return a value. Without the parentheses (`if Ready: 3`) the error says to
  add them.
- **Leaving early.** `return value;` ends a method that has `[end: value]`, and `return;`
  ends one that has `[end]`. The value must be the same kind (number or `str`) as the one
  after `[end: ...]`. The `[end: value]` decides what kind of method it is, so it's needed
  even when every way out is a `return` (`[end: 0]` will do). A string literal there
  (`[end: 'none']`) makes a `str` method, unless a `return` hands back an `addr` or a
  `ptr<u8>`: then the method is a `ptr<u8>` one, and the literal's address is its result.
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

### Arrays as parameters

A parameter written with empty brackets takes an array of any length, and knows it:
`.len` is how many elements it has (an `atmost` array's count), and `.max` the most the
caller's array holds. Its elements are the caller's, so changing one changes theirs; its
length can't change.

```phi
[Sum: int numbers[];]
    int total: 0;
    while int i: 0; i < numbers.len; i++;
        total++ numbers:i;
    ;;
[end: total]

int a: 1 2 3;
int scores[atmost 10];
log (Sum: a) ' ' (Sum: scores);        # any array of ints, fixed or atmost
```

- An index is checked against `.len` when it runs. Copying into one (`to is from;`)
  checks the lengths then too.
- An array of objects is `new T list[]` (the only parameter written with `new`, since
  `T list[]` is an array of structs stored in place). Its elements are the caller's
  objects: `list:i is p` copies into one, and `list:i.x` reaches its fields.
- It can be passed on to another method that takes one, or where a pointer to its elements
  goes. Method values can't take arrays.
- `str rows[]` takes any `str` array whose elements grow: one of values, `str names[8];` or
  `str names[atmost 64];` (32-bit code). Its elements are the caller's texts, so writing or
  adding to one changes the caller's, and it grows as theirs would.

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
- One that takes no arguments is called in parentheses, as `(Name)` is: with
  `method<: int> next;`, `(next)` is its result. Without them, `next` is the method itself,
  so `if next is 0` still checks whether one is set.
- Methods called this way can't return a `str` yet (a method called by name can).
- After the callee, `:` with a space starts the arguments; `table:i` with no space is an index.
- A method whose `str` parameter has no default writes into the caller's own text
  (`[Fill: str target;]`). Its type says so with `into` after `str`, and the argument has to
  be a `str` it can write (a variable, field or element), as with a call by name. Such a
  method isn't a `method<str>`, whose parameter is a copy:

  ```phi
  method<str into: int> fill: addr Fill;
  str name: '';
  call fill: name;                          # Fill writes into name

  [Fill: str target;]
      target is 'filled';
  [end: 1]
  ```

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
program, then (for a program in a kernel's rootfs) next to the kernel, then in the standard
library ([lib/phi](../lib/phi)). So the programs of a rootfs can share code kept in a folder
next to the kernel, like `lib/widgets.phi` used as `use lib.widgets;`. Each file is loaded once,
and the classes of used files run before the classes that use them.

### Standard library

| File | What it provides |
|---|---|
| `drivers.vga_text` | class `Vga` (both kinds of program): `Clear`, `SetColor: fg bg`, `SetCursor: row column`, `PutChar: c`, `Print: text`, `PrintNumber: n`, scrolling, the hardware cursor |
| `boot.info` | 32-bit kernels: `Boot.info`, a `ptr<BootInfo>` to what the loader found (memory map, boot drive, cursor) |
| `memory.frames`, `memory.paging`, `memory.heap`, `memory.list` | 32-bit kernels: see [Memory](#memory-32-bit-kernels) |
| `memory.pool`, `data.map`, `data.queue` | 32-bit kernels and programs: see [Memory](#memory-32-bit-kernels) |
| `drivers.ata` | class `Ata` (both kinds of program): `Read: lba count buffer` →, `Write: lba count buffer` → for the IDE disk |
| `drivers.disk` | class `Disk` (both): the disk's FAT partition as numbered sectors: `partition_found`, `Read`, `Write` |
| `fs.fat16`, `fs.savelog` | 32-bit kernels: see [Files on disk](#files-on-disk-32-bit-kernels) |
| `data.crc32` | class `Crc32` (32-bit kernels and programs): `Of: data length` →, `Add: crc data length` → (carries on from an earlier CRC) |
| `fs.fileinfo` | `FileInfo`, one entry of a folder: what `Fat.Entries` and a program's `OS.ListFolder` fill in |
| `proc.process` | 32-bit kernels: `Process.Spawn: path` →, `Wait: pid` →, `WaitAll` (see [Programs](#programs-and-processes)) |
| `gui.app`, `gui.surfaces`, `gui.desktop`, `gui.events`, `gui.widgets` | windows for user programs, and a desktop for them: see [Windows for programs](#windows-for-programs) |
| `user.text` | class `Text` (any program): `Length`, `Equal`, `EqualIgnoreCase`, `StartsWith`, `EndsWith`, `Lower: c`, `Upper: c`, `SkipSpaces`, `CopyWord: text buffer max`, `AfterWord`, `Copy: buffer text size` →, `Append: buffer text size` →, `AppendNumber: buffer n size` →, `AppendPadded: buffer n width size` → (zeros in front), `AppendSize: buffer bytes size` → (`512 bytes`, `13 KB`, `4 MB`), `TrimEnd`, `ToNumber`; the `Copy` and `Append` methods cut to fit the buffer and return the new length |
| `user.math` | class `Math` (any program): `Abs`, `Min`, `Max`, `Clamp: value low high`, `Sign`, `Sqrt` (whole-number square root, rounded down) |
| `memory.mem` | class `Mem` (32-bit kernels and programs): `Copy: to from count`, `Move: to from count` (the areas may overlap), `Fill: to byte count`, `Fill32: to value count`, `Equal: a b count` →, using the CPU's `rep movs` and `rep stos` |
| `drivers.rtc` | class `Rtc` (both kinds of program): `Read` fills in `year month day hour minute second` from the CMOS clock (UTC on QEMU); `ReadLocal` gives the time in the host's time zone (which `phi run` and `phi test` pass to QEMU; `HostZone` → reads it), or in the one `SetZone: minutes` sets, and `AddMinutes: n` moves the fields, carrying into the day, month and year |
| `drivers.pci`, `drivers.nic`, `drivers.rtl8139`, `drivers.virtio_net`, `net.net`, `net.udp`, `net.dhcp`, `net.dns`, `net.tcp`, `net.tls`, `net.http` | 32-bit kernels: see [The network](#the-network-32-bit-kernels) |
| `drivers.ac97`, `audio.sound` | 32-bit kernels: see [Sound](#sound-32-bit-kernels) |
| `audio.synth`, `audio.wav` | any program: see [Sound](#sound-32-bit-kernels) |
| `crypto.bytes`, `crypto.sha256`, `crypto.hmac`, `crypto.pbkdf2`, `crypto.chacha20`, `crypto.poly1305`, `crypto.chachapoly`, `crypto.aes`, `crypto.aesgcm`, `crypto.x25519`, `crypto.random` | any program: see [Cryptography](#cryptography) |
| `test.bench` | class `Bench` (32-bit kernels and programs): `Start`, `Stop: name` → (writes `bench NAME COUNT` to COM1 and returns the count), `Count` → (the timestamp counter, a `u64`); for benchmarks, see [tests/README.md](../tests/README.md#benchmarks) |

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
| `OS.Idle` | 32-bit | let the programs run, then, if none of them can, wait for the next interrupt instead of coming straight back (a kernel's main loop, so it doesn't spin) |
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

- `dec` / `fin` (exact decimal numbers; `f64` and the others are binary floating point),
  `has`, `^^` (power)
- division by zero isn't caught in 16-bit programs (in a 32-bit kernel it shows the panic screen)
- strings that grow in 16-bit programs (they have no heap; a `str` there is a fixed buffer)
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

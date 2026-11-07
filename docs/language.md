# PHI language reference

This describes the language as the compiler in `Compiler/Phi.Compiler` implements it today:
16-bit programs and 32-bit kernels and their programs on PHI OS (run in QEMU), and programs for
Linux and Windows, in 32 or 64 bits. Ideas that aren't implemented yet live in [Syntax/](../Syntax)
and [Plan.md](../Plan.md).

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
| Structure | `phi` `struct` `enum` `interface` `asm` `x64` `arm` `sys` `foreign` `use` `const` `volatile` `isr` `end` `into` |
| Control | `if` `elif` `else` `while` `each` `when` `unsafe` `break` `continue` `return` `call` `exit` `atomic` `locked` `throw` `throws` |
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
| Strings | `'hello'`; `'hasn't'` works without escaping. ASCII characters only for now: any other is an error, and a byte above 127 is written as `\xNN` |
| Escapes | `\r` `\n` `\t` `\0` `\\` `\'`, and `\xNN` for any byte by its two hex digits (`'\xED'` is φ in the VGA font) |
| Characters | a one-character string like `'w'` is its character code wherever a number is expected; where text goes (a `str`, a `ptr<u8>` parameter, `log`) it's still text |
| Constants | `Colors.Black` … `Colors.White` (the 16 VGA colors), and your own `const` |

A quote followed directly by a letter or digit doesn't end a string, which is why
`'hasn't'` works. Doubling a quote doesn't make one: `'can''t'` is an error, since it would
be two strings with nothing between them (write `'can't'`, or `'can\'t'`). Strings can span
several lines. A number too big for `int` (like `0x80000000`) is a `u32`.

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
str days: 'Mon' 'Tue';      # an array of strings
str greeting: ('Hi ' name); # one text, built from the values in parentheses
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
  65504. Math on any kind happens in `f64`s, and each result is the nearest `f64` to the true
  answer, on every system; it's rounded again if it's stored in a smaller kind.
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

Text longer than the max is an error (`ErrorKind.TEXT`), which can be caught, and what fitted
is what the `str` is left with: losing the end of a text quietly is how a path ends up naming
a different file. A literal that can't fit is an error when it's built. When losing the rest
is what's wanted, `Cut.Into` says so (in a 32-bit kernel or a program; a 16-bit one has `Text.Copy`):

```phi
use user.cut;
str tiny: [3];
tiny is 'abc';                      # just fits
tiny is line;                       # an error if line is longer
call Cut.Into: tiny line;           # as much of line as fits
```

`ask` is the exception: it reads what fits of a line, as typing has no one to tell. And a
`throw`'s own long text is cut to the 255 characters an `Error` holds, so that the error is the
one thrown. A `str` with a size takes one more byte than it holds, for the zero that ends the text.

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
gives back the texts it no longer needs).

**In a struct.** A field without a size, `int scores[];` (or `new Shape shapes[];` for
objects), grows in the same way, and the struct owns it, so the struct is one PHI manages
(declared with `new`). Copying the struct copies its arrays, comparing compares them, and
they're freed with it. Its elements are numbers, structs, or objects. `++`, indexes, `.len`,
`.len is`, `while each`, copying a whole array in (`b.scores is other;`, which grows it to
fit) and passing it to a method all work, and `board.scores.max` is its room. A `str` one,
`str names[];`, is like a `str` array variable without a size: each of its texts grows too.

```phi
struct.Board
{
    int scores[];
    new Shape shapes[];
}

new Board b;
b.scores++ 10 20;
new Board copy: b;            # its own scores and shapes
```

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

A class's own code (the statements directly in it, which run when it starts) works the
same way: a name declared again in a separate block there is a variable of its own. A name
declared once, in a block or not, is the class's variable, which its methods can use too.

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
  `if ready` followed by a statement on the next line knows where the condition ends. A long
  expression can break after an operator anywhere, and inside parentheses before one too,
  where the `)` says where it ends:

  ```phi
  int total: price +
      tax;
  int width: (left + middle
      + right);
  ```
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
unsigned). A `ptr<u8>` or `ptr<chr>` prints as the zero-terminated text it points to, like a
`str`, and so does one moved along (`p + 1` is the text from its second character, and
`addr s:1` is a `ptr<chr>`); to print the address instead, put it in a `u32` first. On the
screen, a new line needs `\r\n`.

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
x^^ 3;         # to the power 3 (whole numbers of up to 32 bits; a power below 0 gives 0)
list:2 is 7;   # one element
task.id is 3;  # one field
name is 'new text';
name is line;  # line is a ptr<u8>: copies the text it points to (an error if it doesn't fit)
name is line + 1;   # the same text from its second character, as name++ line + 1; adds it
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

As in any list, `'Total' -3` is two values. Text that doesn't fit is an error, as for any `str`. The
`str` can't appear after the first value (it's being rewritten by then), and `s++ s;` is an
error: copy it to another `str` first. Only a `str` takes several values: `n is n -1;`
still subtracts.

**Text in parentheses.** Several values in parentheses, `('Hello ' name)`, are one text built
from them the same way, and it goes anywhere a value goes: a declaration, one element of an
array, an argument, a comparison.

```phi
str greeting: ('Hello ' name);                 # one text
str greetings: ('hello ' name) 'index 1';      # an array of two
names++ ('item ' i) 'other';                   # two elements
call Show: ('Score: ' score) 10;               # one argument
status is (status '!' status);                 # its own value can be in it
```

A `str` declared with several values is an array of them, so one that would read as text
being built (`str docs: home '/DOCS';`) is an error that says which to write: the values
in parentheses for one text, or each value that isn't a literal in its own parentheses for
an array. A list of literals (`str days: 'Mon' 'Tue';`) is an array as before.

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

**has.** `list has x` is true when an array holds that value, each element compared as `is`
compares it: numbers of every size (and enums; floats by value, so `0.0` finds `-0.0` and
`nan` finds nothing), texts, structs and objects. `text has 'abc'` is true when
the text is somewhere in a `str` (or `ptr<u8>`), and `text has c` when a `chr` is (32-bit
code):

```phi
if names has 'ann' ...
if scores has 100 ...
if line has 'GET ' ...
if not (line has 9 as chr) ...     # no tab in it
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

The same name can be used in another `while each` loop, over any array: each loop's is a
variable of its own.

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

### Errors: throw and throws

An error stops the program (a kernel stops on the spot, and a user program ends with -1),
unless it happened inside a call that an `if call ... throws` is guarding. Then only that
call stops, and the `if` runs, with the error in the name after `throws` (32-bit kernels
and programs):

```phi
if call Load: path throws err
    log err '\n';               # no such file: A.TXT on line 12 in files.phi
    return false;
;;
else
    log 'loaded\n';             # (an else runs when nothing went wrong)
;;

[Load: str path;]
    if (OS.FileSize: path) < 0
        throw 'no such file: ' path;
    ;;
    ...
[end]
```

- **`throw`** takes values as `log` does, which become the error's text.
- **The errors PHI finds itself** are caught the same way: an index out of range, the
  heap out of room for a new object, and a count of 2 billion or more to `Mem.Copy`, `Move`, `Fill`
  or `Fill32` (a negative number, or a subtraction below 0, which would otherwise run through
  all of memory; the error names the line of the call, and nothing is copied), and a number
  that would come out as a different number (a negative one going to a `u32`, or a `u32` of 2
  billion or more going to an `int`; `as` says the same bits are meant), and text that doesn't
  fit in the `str` it's going to.
- **`err` is an `Error`**, a struct with the error's `kind` (`ErrorKind.THROWN`, `INDEX`,
  `MEMORY`, `COUNT`, `CONVERT`, `STACK` or `TEXT`, among others), `line`, `file`, `text`, and `message` (the text, then where it happened, as
  an uncaught error is written). `log`, `debug` and text built from values write its
  message, so `log err;` and `str note: ('failed: ' err);` say everything.
- **The methods in between give back what they own**, as if each had returned: their new
  objects, and their texts and arrays that grow.
- **An error inside a `locked` block** (or anything it calls) isn't caught: what the lock
  guards may be half changed, so it stops the program.
- **The call can give a result** (`if call n is Parse: text throws err`), and the guard
  fits on one line (`if call Save: data throws err return false;`).
- **Catches nest**: an error inside the `if`'s own code goes to the catch around that.
- **The CPU's errors are caught too**: dividing a whole number by zero
  (`ErrorKind.DIVIDE`), using an empty object or a null pointer (`ErrorKind.EMPTY`), and
  memory that isn't there or an instruction the CPU refuses (`ErrorKind.ACCESS`), in a
  kernel, in a PHI OS program, and in a Linux or Windows program (32 or 64 bits) alike,
  with the line they happened on (`division by zero on line 42 in world.phi`; inside an
  asm block or the runtime there's none, and the message is the text alone). Uncaught, a
  program prints the error and ends with -1, as for any error, whether or not it catches
  anything anywhere else.
- **The stack running out is caught too** (`ErrorKind.STACK`: `the stack ran out: calls too deep
  on line 12 in parser.phi`), usually a method that never stops calling itself. It works on
  any thread of a program, on Linux, Windows or PHI OS, and it can happen and be caught again
  and again. Each thread keeps some memory aside, a spare stack, for the error to run on. A
  kernel's own stack is the kernel's to watch: running past its end writes over what's below it,
  with no error, and PHI adds nothing to its calls to look. An `asm` block that gives `esp`
  and a `throw` where it matters do it ([Common patterns](patterns.md#a-kernel-watching-its-stack)).
- An error in an interrupt handler or a system call isn't caught by the code it
  interrupted. In a kernel each core has its own list of catches, and each task (a
  thread, say) keeps its own as the cores take turns, so threads catch their own errors.
- In a kernel, catching an error doesn't undo work that was half done (half of a file
  written, a list half updated), so it suits work that stands alone, like one request or
  one command. For a whole part of a system that may go wrong, a user program, which a
  supervisor can start again, is still the way (see `proc.supervisor`).

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
  written as any method's are: `str target;` is the caller's own text, `str name: '';`
  is a copy, and `int points[];` takes an array of any length. Other parameters have no defaults, so a call through it gives every argument.
- **What a method returns comes from the structs, unless the interface says.** Every struct
  with the interface has to return the same, and the first one says what that is: a number, a
  `str`, a new object, or nothing. Or the interface says, after a second `:`, and then code can
  call it before any struct has it, as a library's code does for structs that programs write:

  ```phi
  interface.Plugin
  {
      [Number: : int]            # takes nothing, returns an int
      [Scaled: int by; : int]
      [Name: : str]
      [Where: : ptr<u8>]
      [Run: int times;]          # doesn't say: what the structs return (in a library with no struct, nothing)
  }
  ```

  A pointer stays a whole pointer and a `chr` still prints as a character, whichever way it
  was said.
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

**Which struct it holds.** `if thing is Player p` is true when `thing` holds a Player.
Inside the `if` (only there), `p` is that object as a Player, so its own fields and methods
can be used. It borrows the object, and `thing` still owns it, so changing `p` changes
what `thing` holds. A `when` does the same with one branch for each struct, and `is Rock`
with no name just picks the branch. An empty value holds nothing, so it goes to `else`.

```phi
if thing is Player p
    call p.Win: 5;
;;

when things:i
    is Player p
        p.score is 0;
    ;;
    is Rock
        log 'a rock\n';
    ;;
    else
        log 'something else\n';
    ;;
;;
```

- `is Player p` is the whole condition of an `if` or `elif` (not part of an `and`), since
  `p` exists only when it's true.
- The struct has to have the value's interface. Two tests in one method can use the same
  name for the same struct.

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
| `memory.paging` | `Paging` | identity paging with page 0 unmapped; `page_directory`, `mapped_bytes`; `MapDevice: address bytes`; `ReadLowWord: address` → (16 bits from the BIOS's first page, which stays unmapped) |
| `memory.heap` | `Heap` | `Alloc: size` →, `Free: address`, `LargestFree` →; `used_bytes`, `used_blocks`, `heap_size`. Free blocks are kept on lists by size, so `Alloc` takes about as long with 100,000 blocks in use as with ten |
| `memory.list` | `List` | a growable list of u32 values: `New` →, `Add: list value`, `Get: list index` →, `Set: list index value`, `Delete: list`; the list's `count` and `capacity` |
| `memory.pool` | `Pool` | many objects of one size, handed out and taken back without searching: `New: size per_chunk` →, `Alloc: pool` → (zeroed), `Free: pool object`, `Delete: pool` (all of them at once); the pool's `used` and `capacity`. Freeing twice, or into the wrong pool, stops the program |
| `data.map` | `Map` | u32 keys to u32 values (an ID to an index or a pointer): `New` →, `Set: map key value`, `Get: map key otherwise` →, `Has: map key` →, `Remove: map key` →, `Next: map after` → (walks every key: `KeyAt`, `ValueAt`), `Clear`, `Delete`; the map's `count` |
| `data.keyed` | `KeyedMap` (a struct) | objects of any struct that has `interface.Keyed` (`[Key]`, a u32), found by key: `Add: thing` → (a copy; false if the key is taken), `Find: key` → (where it is in `items`, or -1), `Has: key` →, `Remove: key` →, `Clear`; `items` holds them, so `if map.items:i is Player p` gets one back |
| `data.queue` | `Queue` | records of one size, oldest out first: `New: size capacity` →, `Push: queue record` → (false when full), `Pop: queue into` → (false when empty), `Front: queue` → (the oldest, where it is), `Drop: queue`, `Clear`, `Delete`; the queue's `count` |

`memory.pool`, `data.map`, `data.keyed` and `data.queue` work in user programs too, on the program's
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

**Reading a big file a piece at a time.** `fs.stream` keeps a file open and remembers its
place, so reading on doesn't look the path up or walk its clusters again. A program's
`user.stream` has the same calls, on Linux and Windows past 4 GB, and on PHI OS with
`use fs.stream_calls` in its kernel:

```phi
use fs.stream;
int s: (Stream.Open: 'WORLD.PAK');              # -1 if it can't be opened
int got: (Stream.Read: s addr buffer 65536);    # the bytes, 0 at the end, -1 if it failed
call Stream.Seek: s 1048576;                    # false past the end, and it stays put
u64 where: (Stream.Position: s);
u64 size: (Stream.Size: s);
call Stream.Close: s;
```

A stream is a number, as a socket is, and one that isn't open is refused. A program's are its
own, and closed when it ends. A file rewritten while it's open reads as it is now, and one
that's deleted gives -1, on PHI OS. Threads may each read their own stream at once.

`fs.readahead` reads a stream on a thread of its own, so a game takes what's ready without
waiting for the disk. It uses whichever `Stream` and `Threads` the program has:

```phi
use user.stream;
use user.threads;                               # in a kernel: fs.stream and proc.threads
use fs.readahead;
int r: (ReadAhead.Start: s 1048576);            # keeps up to 1 MB ready; -1 if it can't start
int got: (ReadAhead.Take: r addr buffer 4096);  # never waits: the bytes, 0 if none yet, -1 at the end
call ReadAhead.Seek: r 8192;                    # what was ready is dropped
call ReadAhead.Stop: r;                         # the thread ends; the stream's still open
```

**A URL reads the same way.** With `use net.webstream` as well, `Stream.Open` takes an `http://`
or `https://` URL, in a program (with `net.sockets`) or a kernel (with `net.tcp` and `net.dns`):

```phi
use net.sockets;
use user.stream;
use net.webstream;
int s: (Stream.Open: 'https://example.com/world.pak');   # -1 if it failed: Web.error says why
int got: (Stream.Read: s addr buffer 65536);
call Stream.Seek: s 1048576;                             # false if the server can't start there
u64 size: (Stream.Size: s);                              # 0 if the server didn't say
```

`Open` fails on a status of 400 or above, and a read waits for the server, up to `Web.timeout` ms,
so `fs.readahead` works on one too, for a game that can't wait for the network.

**Web requests.** `net.web` is what's under that, for anything else a program wants of a server. It
speaks HTTP/1.1 and HTTPS, follows up to 5 redirects, and reads an answer as it comes, whether it
has a length, comes in chunks or runs until the server closes:

```phi
use net.sockets;                                # in a kernel: net.tcp and net.dns
use net.web;
int r: (Web.Get: 'https://example.com/page.html');
log (Web.Status: r);                            # 200, say
int got: (Web.Read: r addr buffer 4096);        # the bytes, 0 at the end, -1 if it failed or was cut short
call Web.Close: r;

int posted: (Web.Send: 'POST' url 'Content-Type: text/plain\r\n' data length);
u8 kind[64];
int n: (Web.Header: posted 'Content-Type' addr kind 64);   # its length; -1 if there's none
```

A certificate must check out for an `https://` URL, against the roots in `crypto.roots` unless a
program has trusted its own with `X509.Trust`. Those are only a few, so a site that goes back to
another root fails with `no trusted root`: which roots to trust is up to the project, which brings
what it needs, as many as it likes up to `X509.MAX_ROOTS` (4,096). Not yet: HTTP/2, IPv6 addresses, and
uploading a body a piece at a time.

**Names.** `Dns.Resolve: name` gives a name's address, 0 if it can't be found, in a program as in a
kernel. A program tries `localhost`, then the system's hosts file, then its name server
(`OS.NetNameServer`).

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

**Programs on every core.** `call Cores.SharePrograms;` (in a kernel that runs programs,
`proc.process`) has the other cores run programs too, from then on: each takes a ready
program no other core is running, and its own timer (its local APIC's) takes the CPU back
every 10 ms. Kernel code (system calls, interrupts, and the kernel's own code) still runs
on one core at a time: it holds one kernel lock, which a kernel lets go of while it waits
(`Process.Wait`, `OS.Sleep`, `OS.Idle`, `OS.Yield`), so the kernel library needs no
change. The network and the file system have locks of their own (`Net.Lock`,
`Fat.Lock`): programs' network calls (`net.calls`) and file calls (`proc.process`) hold
those instead of the kernel lock, so a program's network calls only wait for other
network calls and its file calls for other file calls, not for the rest of the kernel. A
program killed while another core runs it is freed once that core has let go of it.
`Run` can't be used once programs share the cores.

**A lock for a part of the kernel.** `proc.corelock`'s `CoreLock` is the lock the network
and the file system have: one core at a time, with that core's interrupts off while it
holds it, as in a `locked` block. Unlike a `locked` block, the core that holds it can take
it again, so a part whose methods call each other can have each of them take it:

```phi
use proc.corelock;

CoreLock guard;
call guard.Take;                           # waits while another core has it
call guard.Give;                           # free once every Take is given back
u32 depth: (guard.LetGo);                  # let go however deep, to sleep...
call guard.TakeAgain: depth;               # ...and taken back as deep as it was
```

**Kernel threads.** `proc.threads` runs a method on its own, at the same time as the
kernel's code and other threads, on whichever core is free:

```phi
use proc.threads;

int t: (Threads.Start: addr Count 3);     # Count: 3, as a thread of its own (-1 if it can't start)
call Threads.Wait: t;                      # back once Count has returned
log (Threads.Done: t);                     # whether it has finished, without waiting
```

- A thread is a task in ring 0 with a stack of its own, which the timer shares the CPU
  with, like a program. It runs without the kernel lock, so after `Cores.SharePrograms`
  threads run at once on several cores; on one core they take turns with the kernel,
  which gives up the CPU when it waits.
- In a thread, the heap, `log`, `atomic` and `locked` work; files, the network and the
  `OS.` calls don't yet (they're the kernel lock's).
- A thread shares the class variables with everything else. Code a thread runs (the
  method given to `Threads.Start` or `Cores.Run`, and every method it calls) that writes
  a class variable, or a field of one, outside a `locked` block or an `atomic` step gets a
  warning: another thread may be reading or writing it at the same moment. An element of
  an array isn't warned about, since that's how each thread keeps a result of its own
  (`sums:part is sum;`).

**Threads in programs** (in progress, H5 in [Plan.md](../Plan.md)). A program has the
same calls, from `user.threads`, on PHI OS, Linux and Windows:

```phi
use user.threads;

int t: (Threads.Start: addr Count 3);     # Count: 3, as a thread of this program
call Threads.Wait: t;
```

- On PHI OS a thread is a task of the program, in its memory, on a stack of its own (about
  92 KB, from the heap); once programs share the cores, a program's threads run at once on
  several. On Linux it's a thread made with `clone`, and on Windows with `CreateThread`.
- A thread finishes when its method returns. `exit` in any thread ends the whole program,
  and so does an error nothing catches (or a fault) in any of them.
- What belongs to a program (its sockets, its mailboxes, its memory's checks) is the
  program's, whichever thread uses it; `OS.CurrentTask` gives the program's number.
- Each thread catches errors of its own: its catches, and the error it throws, are its own.
  The heap, `log` and file calls work from several threads at once (a `log` comes out whole).
- Not yet: `ask` from several threads at once.

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

- `atomic` works on a variable, field or element that is a number or `bln` of up to 32
  bits, or a pointer (8 bytes in 64-bit code). A method's variable used with it stays in
  memory (never in a register).
- A `locked` block turns interrupts off on its own core while it holds the lock, so an
  interrupt handler that wants the same lock can't wait forever for the code it
  interrupted. Keep locked blocks short. A program's `locked` block leaves interrupts alone
  (they're its system's, and a program may not turn them off): it only takes the lock.
- A lock starts free; it can't have a value (`lock guard;`), and isn't a number.
- Both need 32-bit or 64-bit code: 16-bit code runs on one core.

**What threads see of each other's writes** (PHI's memory model). The same on PHI OS,
Linux and Windows, in kernels and programs, 32 and 64 bits:

- **A `locked` block sees everything the last thread to hold its lock wrote** before it
  gave the lock back, in that block or before it. Data that's only ever changed inside
  blocks of the same lock is never seen half changed.
- **An `atomic` step is one step for every thread**, and also orders: whatever a thread
  wrote before an atomic step, another thread sees once it sees that step's result (an
  `atomic x is v`, say, that another thread's `atomic ... when` or read of `x` finds).
  So a flag set with `atomic` after filling in a record tells another thread the record
  is ready.
- **Plain reads and writes of class variables** (and fields and elements of them) from
  several threads at once, without either: each read or write of a number of up to 32
  bits, a `bln` or a pointer is whole (never half of one value and half of another) when
  it's aligned: every class variable starts on a word, and an element or field is
  aligned when its place in the array or struct is a multiple of its size (structs have
  no padding: put 32-bit fields before smaller ones). Each thread's writes are seen by
  the others in the order it made them. But a thread
  can read a variable before its own earlier write to another one is seen, and nothing
  stops two threads' read-change-write steps (`total++`) from losing one: that's what
  `atomic` and `locked` are for, and what the compiler warns about in code a thread runs.
  Class variables are always read from memory, so a loop that waits for one to change
  sees the change; a method's own variables are its thread's alone.
- **Wider values** (`i64`, `u64` in 32-bit code, floats, structs, text) can be seen half
  written by a plain read while another thread writes them: guard them with a lock.
- `volatile` is for memory a device changes, not for threads: it doesn't order anything.

These are the promises to rely on: today every system PHI runs on is x86, whose own rules
give at least this much, and a later system with weaker ones (ARM) will keep these by
putting what's needed into `atomic`, `locked` and lock handover.

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
1.3 (`net.tls`), with AES-128-GCM or ChaCha20-Poly1305, whichever the server picks. The
server's certificate is checked (`crypto.x509`): it must be for the host (its
subjectAltName names, or its IP addresses), go back to a root the program trusts, and
have signed the handshake. If any of that fails, so does the connection, and the error
says why; `Tls.verified` says it checked out. A program trusts roots before connecting:
`call Roots.TrustAll;` (`crypto.roots`, a few that many sites go back to) or `call
X509.Trust: der length;` for its own. Keys can be RSA (up to 4096 bits), P-256 or P-384,
with SHA-256 or SHA-384 (so Let's Encrypt's ECDSA chains check out too). Each certificate
but the root must be within its dates, now (`OS.Now`), or at `X509.now` when a program
sets it (seconds since 1970: a test whose certificates were made at a given time does).

The layers, each usable on its own:

| File | Class | What it does |
|---|---|---|
| `drivers.pci` | `Pci` | `Find: vendor device` → (or `Pci.NOT_FOUND`), `FindClass: code` → (by what it is: `0x010802` an NVMe disk), `Read`/`Write: device offset`, `Bar: device n` →, `Irq` →, `EnableBusMaster` |
| `drivers.nic` | `Nic` | whichever card the machine has: `Start` →, `Send: frame length` →, `Receive: buffer max` → (0 when nothing is waiting), `Dropped` →; `mac`, `kind` (`Nic.RTL8139` or `Nic.VIRTIO`) |
| `drivers.rtl8139`, `drivers.virtio_net` | `Rtl8139`, `VirtioNet` | the two cards, with the same calls as `Nic`; `frames_dropped`, `interrupts` |
| `drivers.virtio` | `Virtio` | what virtio devices share (legacy PCI): `Begin: io features` →, `SetUpQueue: io n queue` →, `Ready: io`, `Notify: io n`, `Used: queue` →; structs `Virtqueue`, `VirtqDesc` |
| `net.net` | `Net` | `Start` → (by DHCP), `Start: address mask router` → (no DHCP), `Configure: ip netmask gateway dns`; `ip`, `netmask`, `gateway`, `dns`, `mtu` (1500, or less where DHCP says so, as some clouds do: what IP sends and TCP's segments keep to), `dhcp_ok`; `Ping: address timeout` → (ms, or -1); `SendUdp: to from_port to_port data length` →, `Listen: port`, `WaitUdp: timeout` → (the datagram is in `udp_data`); `ParseIp: text` →, `PrintIp: address` |
| `net.udp` | `Udp` | sockets: `Open: port queue` → a socket or -1 (port 0 picks one), `Receive: s datagram` → (never waits), `Wait: s datagram timeout` →, `SendTo: s address port data length` →, `Close: s`; a `UdpDatagram` has `from`, `from_port`, `length` and `data`; each socket's `received` and `dropped` |
| `net.dhcp` | `Dhcp` | `Configure` → (`Net.Start` does this) |
| `net.dns` | `Dns` | `Resolve: name` → an address, or 0 |
| `net.tcp` | `Tcp` | clients: `Connect: address port` → a connection or -1, `Write: c data length` → (waits until it's all taken), `Read: c buffer max` → (waits for something: 0 once the other side has closed, -1 on a timeout), `Close: c`; `timeout` (ms, 10000). Never waiting: `Begin: address port` → a connection still connecting, `State: c` → (1 open, 0 connecting, -1 failed), `Take: c buffer max` → (0 if nothing yet, `Tcp.CLOSED_AND_READ` once closed). Servers, never waiting: `Listen: port backlog` → a listener or -1, `Accept: listener` → a connection or -1, `StopListening: listener`, `Waiting: c` → bytes to read (-1 once closed), `Send: c data length` → how much fitted, `Address: c` →; `open_count`, `retransmits` |
| `net.calls` | `NetCalls` | the network for user programs: `use` it in the kernel (see below) |
| `net.tls` | `Tls` | one TLS 1.3 client connection over the kernel's TCP, which waits as it goes: `Connect: address port host` → (false: `error` says why), `Write: data length` →, `Read: buffer max` → (0 once the server has closed), `Close`; `suite`, `alert`, `verified` |
| `net.tls13` | `Tls13` | TLS 1.3 itself, either side, any kind of program, with no input or output of its own: a `TlsSession` each, `Client: session host` or `Server: session identity` (a `TlsIdentity`: `Identity: identity certificate length key length`, a P-256 or P-384 key, which signs the handshake; `AddIssuer`), then `Room` and `Received: session count` for what arrives, `Pending`, `Outgoing` and `Sent: session count` for what to send, `Read`, `Write`, `Close`, `Free`; `connected`, `failed`, `error`, `verified` |
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
  taken yet: a button let go is still up afterward, and a press forgotten isn't the first of a
  double click.
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

**On Windows and Linux** a program with a window builds and runs unchanged, 32 or 64 bits, in
a real window: `phi run sketch.phi --target windows64` or `--target linux64`.
`lib/phi/host/window_windows.phi` answers the calls below with Win32, and
`window_linux.phi` by speaking X11's protocol over its socket, with no library (`DISPLAY`
says which, `:0` if it isn't set; a server that wants authentication refuses, and the window
can't open). On both, `App.OpenWindow` gives -1 (starting a program by a path on its disk
is PHI OS's way), and the thread that opened the window draws and takes the events. The
font is Windows' Consolas in the same 8x16 cells, or the X server's own (ASCII only).
`samples/desktop.rootfs/paddles.phi` is a small game that runs on all of them.

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

A `TextArea` is many lines to type into and scroll through, with its text on the heap, growing
as it's typed into (up to its `most`, 1 MB unless that's changed): `call notes.Place: x y w h;`,
`notes.Set: text`, `notes.Key`, `notes.Mouse` and `notes.Paint` as a field has, plus `Insert: text
count` and `Free`. The caret moves by character, line and page; selecting works as a field's
does; the wheel scrolls it; and `notes.text` is all of it, each line ending with a `\n`. It
doesn't wrap: a long line scrolls sideways as the caret goes along it.

**Several windows.** A program's window is number 0, which is App's, and it can open more, each
with a number of its own, on PHI OS's desktop, Windows and Linux alike:

```phi
int tools: (Windows.Open: 200 300);             # another window; -1 if it can't (7 at most)
call Windows.SetTitle: tools 'Tools';
call Windows.Use: tools;                        # Draw draws on it from now on
call Draw.Clear: Draw.WHITE;
call Windows.Present: tools;
call Windows.Use: 0;                            # and back to App's
```

Each event says which window it's for, in `App.event.window`, and all of them come through
`App.NextEvent` and `App.WaitEvent`. A close button only asks: `Windows.Close: tools` closes that
one and the program carries on (when the program ends, they all go). A new size is `Windows.Resize:
App.event.window App.event.x App.event.y`, which needs `Windows.Use` again if it's being drawn on.
`Windows.Width`, `Height`, `SetResizable` and `Focused` take a number too, 0 meaning App's, and
App's own calls go on acting on window 0 whatever else is open.

**Menus and dialogs** are drawn by PHI inside the window, so they're the same on every system.
`gui.menu` has a `MenuBar`, which is painted last as what's open lies over the window:

```phi
use gui.menu;
MenuBar menu;
call menu.Place: 0 0 W;
int file: (menu.AddMenu: 'File');
call menu.AddItem: file 'Open...' OPEN 'Ctrl+O' 'o' | Events.MOD_CTRL;  # any command but 0
call menu.AddLine: file;
call menu.AddItem: file 'Quit' QUIT;
call menu.Enable: OPEN false;                   # shown dimmer, and can't be chosen
call menu.Check: QUIT true;                     # a tick beside it
int chosen: (menu.Mouse: App.event);            # or menu.Key: App.event.value: a command, or 0
if menu.took ...                                # the event was the menu's: don't act on it too
call menu.Paint;
```

F10, or Alt and a title's first letter, opens one, the arrows go around it, Enter chooses and Esc
closes; an item's own key works when nothing's open. `gui.dialogs` has dialogs that wait for
their answer over the window as it was, and put it back:

```phi
use gui.dialogs;
call Dialogs.Message: 'The file was saved.';
int answer: (Dialogs.Ask: 'Save the changes?' 'Save' 'Discard' 'Cancel');  # 0 to 2; -1 for Esc
if (Dialogs.Open: 'DOCS')                       # in that folder; '' is where it runs
    int got: (OS.ReadFile: Dialogs.path buffer size);
;;
if (Dialogs.Save: 'DOCS' 'NOTES.TXT')           # asks before replacing a file
    call OS.WriteFile: Dialogs.path data length;
;;
```

A dialog goes over App's window, and keys and clicks in a program's other windows are ignored
until it's done. If any window is closed or resized while one's up, that comes to the program's
loop as an event once it's done (`App.PutBack` is how: anything else that waits for events can use
it too).
`samples/desktop.rootfs/editor.phi` is all of these in one small program.

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
`Process.more_calls`, so other libraries can add their own. A call's arguments come from the
program, so what it reads or writes has to be the program's own memory:
`Process.CopyBytesFromUser: from to count` and `Process.CopyBytesToUser: to from count` copy
only if every byte is, and say whether they did; `Process.CopyFromUser: from to max` does the
same for a string.

## Sound

`audio.sound` plays music: songs written as text, and WAV files. It's the same in a program
on PHI OS, Windows and Linux, and in a 32-bit kernel, which also says what it plays them on
and reads them from (`use drivers.ac97; use fs.fat16;`). `phi run` and `phi test` give every
machine an AC'97 sound card, which `drivers.ac97` drives; **on Windows** a program's sound
goes through winmm's `waveOut`, and **on Linux** to PulseAudio over its socket, with no library
needed, as a window speaks to X. PHI OS's programs and those two answer the same `OS.Sound`
calls, 16-bit stereo at 8000 to 48000 frames a second, and a machine with no sound gets
`-1` from them and `there is no sound card` from `Sound.error`.

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
| `drivers.ac97` | `Ac97` | the card: `Start` →, `StartAt: hz` → (`OS.SoundRate` in a kernel: starts it; -1 if there's no card), `Left` → (`OS.SoundQueued` in a kernel), `SetRate: hz` →, `SetVolume: percent`, `Write: data length` → (bytes taken; never waits), `Play: data length` (waits for room), `Flush`, `Drain` (waits until it has played), `Queued` →, `Stop`; `rate`, `bytes_written` |
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
| `crypto.sha512` | `Sha512` | SHA-512 and SHA-384 (32-bit and 64-bit code): `Hash: data length digest` (64 bytes), `Hash384` (48); in pieces: `Init` or `Init384`, `Update`, `Final` on a `Sha512State`, and `Copy: from to` |
| `crypto.hmac` | `Hmac`, `Hkdf` | `Hmac.Mac: key key_length data length output` (or `Start`, `Update`, `Finish`); `Hkdf.Extract: salt salt_length ikm ikm_length prk`, `Hkdf.Expand: prk info info_length output length` |
| `crypto.pbkdf2` | `Pbkdf2` | for storing passwords: `Derive: password password_length salt salt_length iterations output length`, PBKDF2-HMAC-SHA256. Keep the salt (random, from `Random.Fill`), the count and the result; to check a password, derive it again and compare with `Bytes.Equal`. 1,000 rounds are about 39 million instructions |
| `crypto.chacha20` | `ChaCha20` | `Xor: key nonce counter data length` (encrypts or decrypts in place), `Block` |
| `crypto.poly1305` | `Poly1305` | `Start: key`, `Update: data length`, `Finish: tag` |
| `crypto.chachapoly` | `ChaChaPoly` | ChaCha20-Poly1305: `Seal: key nonce header header_length data length tag` (encrypts in place), `Open: ...` → false if anything was changed |
| `crypto.aes` | `Aes` | AES-128: `SetKey: key`, `Encrypt: block output` |
| `crypto.aesgcm` | `AesGcm` | AES-128-GCM: `Seal` and `Open`, like `ChaChaPoly` (with a 16-byte key) |
| `crypto.x25519` | `X25519` | `Base: public secret`, `ScalarMult: output secret their_public` (the shared secret) |
| `crypto.random` | `Random` | `Fill: buffer length`; `random_hardware` says whether the CPU's RDRAND was used |
| `crypto.bignum` | `Big` | numbers of up to 4096 bits in 16-bit limbs: `Setup: modulus bytes count` (a `Modulus`), `FromBytes`, `ToBytes`, `Compare`, `Add`, `Subtract`, `MontMul`, `PowMod: r base exponent count modulus` → |
| `crypto.rsa` | `Rsa` | checking a signature of a SHA-256 digest: `VerifyPkcs1: modulus length exponent length digest signature length` → (a SHA-384 one with `48` after), `VerifyPss` (MGF1-SHA256, a 32-byte salt) → |
| `crypto.der` | `Der` | reading DER: `Element: p left` →, `Next: cursor` →, `Enter: cursor` (a `DerCursor` over what was read), `IsOid`, `Number: output count` →; then `tag`, `content`, `length` |
| `crypto.p256` | `P256` | ECDSA P-256: `Verify: public digest r s` → (public: 65 bytes, 04 then x and y; a SHA-256 digest), `VerifyDigest: public digest length r s` → (a digest of any size), `Sign: r s private digest` → (RFC 6979: no random numbers), `PublicFrom: public private` →, `ReadPrivate: private public der length` → (a SEC 1 or PKCS #8 key file) |
| `crypto.p384` | `P384` | ECDSA P-384, the same calls as `P256` with 48-byte numbers, 97-byte public keys and SHA-384 digests |
| `crypto.ec` | `Ec` | the math `P256` and `P384` share, for any curve y^2 = x^3 - 3x + b: an `EcCurve` from `Setup: curve bytes p n b g` (hex), then `Verify`, `Sign`, `PublicFrom`, `ReadPrivate` on it |
| `crypto.x509` | `X509` | certificates: `Parse: certificate der length` →, `CheckSignature: certificate issuer` →, `MatchesHost: certificate host` →, `Trust: der length`, `Untrust`, `Chain: certificates count host` → (false: `error` says why) |
| `crypto.roots` | `Roots` | `TrustAll`: a few RSA roots many sites go back to (ISRG Root X1, DigiCert Global Root G2 and Global Root CA, GlobalSign Root CA - R3, USERTrust RSA) |

The big-number math keeps numbers as many small pieces (8 bits in X25519, 16 in
`crypto.bignum`), which keeps every product within 32 bits. An X25519 takes about 35 ms
in QEMU. AES looks
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
programs. A program that touches memory that isn't its own gets an error it can catch; if it
doesn't, it's stopped with the error's message (`PHI: memory that is not there was used, or the
CPU refused an instruction on line 5 in crash.phi`) and the kernel carries on.

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
| `OS.Now` → | the date and time, as seconds since 1970 (UTC), a `u32`: from the kernel's clock |
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

### Programs on Linux and Windows

The same program builds as an ordinary Linux or Windows program, unchanged:

```
phi build hello.phi --target linux       # build/hello/hello, a 32-bit ELF executable
phi build hello.phi --target windows     # build/hello/hello.exe
phi run hello.phi --target linux -- a b  # build and run it here, with a b as its command line
```

**64-bit programs** (in progress, S10 in [Plan.md](../Plan.md)): `--target linux64`
builds a 64-bit Linux program, and `--target windows64` a 64-bit Windows one. In 64-bit
code pointers (and objects, and method values) are
8 bytes, and every number keeps its size (`int` is 32 bits on every system). A 64-bit
program can use what a 32-bit one can: numbers of every size and floating point, text,
structs, objects and interfaces, arrays of all of them (fixed, `[atmost N]`, growing, and
as parameters), method values, catching errors, `ask`, and the system's calls (files,
arguments, time, memory), and `atomic` and `locked`. What it can't use yet (assembly
blocks that have no `x64.Name { }` version) is an error saying it isn't in 64-bit
programs yet. An address kept as a number is a `ptr<u8>` (the heap's `Heap.Alloc` gives
one), so it's whole on every system.

On Windows, `phi run --target linux` runs it under WSL. NASM writes the whole executable,
so no linker or C library is involved: what a program asks PHI OS for, the program's
library for that system asks Linux (its system calls) or Windows (`kernel32.dll`)
instead. Only `Program` and `Library` classes build for another system; kernels and
16-bit programs are PHI OS's own.

| In a program | On Linux and Windows |
|---|---|
| `log` | standard output, a whole statement at a time |
| `debug` | standard error (what the serial port is on PHI OS) |
| `ask`, `OS.ReadKey` →, `OS.KeyAvailable` → | standard input. The terminal shows what's typed and hands it over a line at a time, so `ReadKey` waits for Enter; Enter is 13, as on PHI OS, and the end of the input gives 0 (an `ask` gets an empty line) |
| `exit code;` | the process's exit code |
| `OS.Arguments` → | what follows the program's name on its command line (on Linux, its arguments joined by spaces) |
| `OS.GetTicks` →, `OS.Sleep: ms`, `OS.Yield`, `OS.CurrentTask` → | milliseconds since the program started; sleeping; the process number. Both count in milliseconds on every system: a Windows program asks for its timer to step by 1 ms (`timeBeginPeriod`, from `winmm.dll`), since it would otherwise step by 15.6 ms, and its clock is `timeGetTime` |
| `OS.Now` → | the date and time, as seconds since 1970 (UTC): Linux's `time`, Windows' `GetSystemTimeAsFileTime` |
| `OS.ReadFile`, `OS.ReadFileAt`, `OS.WriteFile`, `OS.AppendFile`, `OS.DeleteFile`, `OS.FileSize`, `OS.MakeFolder`, `OS.DeleteFolder`, `OS.Rename`, `OS.ListFolder` | files and folders, by that system's paths (relative to the folder it runs in). A `FileInfo`'s name is whole here, not 8.3. `OS.FileSize` on a file of 2 GB or more is an error, as an `int` can't hold its size |
| `new`, objects, text that grows | the heap, which grows as on PHI OS (Linux's `brk`, or memory Windows reserves and commits as it's needed) |
| `OS.FreeMemory` →, `OS.TotalMemory` → | the machine's memory, in KB |
| `OS.ClearScreen` | clears the terminal |
| `net.sockets`' `Udp`, `Tcp` and `Net` | the system's own sockets (Linux's, or Winsock): UDP and TCP to any address, listening and accepting, each call never waiting as on PHI OS. `Net.Address` is the address the machine reaches the internet from (or 127.0.0.1). A program's sockets are its own: a number that isn't one it opened is refused. `Net.Start` and `Net.Wait` are there as in a kernel (true if the system's network is there; a millisecond's sleep), so code that calls them moves by its `use` line too |

The rest of PHI OS's calls (`OS.PutCell`, `OS.IsKeyDown`, `OS.Reboot`, starting and
watching other programs, `OS.ListFiles` and `OS.ListFolder`, sound, windows and
mailboxes) aren't there yet, or have no equivalent: using one is an error when the
program is built (`OS.PutCell isn't available in a Linux program; it works in a PHI OS
program`). So is a module that can't work there, at the `use` that brings it in:

```
hello.phi:1:1: error: drivers.ata doesn't work in a Linux program (lib/phi/drivers/ata.phi:37: user programs can't use ports; only the kernel can talk to hardware)
```

Most of the standard library (data, text, crypto, math, file formats) works anywhere.

### A system's own modules, and system calls

A module can be one system's own: its classes say which instead of `:Program`. The same
name can have a version for each system, each in a file of its own, and the code that uses
it never says which; the compiler builds the version for the system it's building for and
leaves the others out:

```phi
# raw_linux32.phi                       # raw_linux64.phi
phi.Raw:Linux32                         phi.Raw:Linux64
{                                       {
    [Pid]                                   [Pid]
    [end: (sys.GetPid)]                     [end: (sys.GetPid)]
}                                       }
sys.GetPid: 20;                         sys.GetPid: 39;

# the program
use raw_linux32;
use raw_linux64;
phi.Hello:Program
{
    log 'my number: ' (Raw.Pid) '\n';
}
```

| Word | Built for |
|---|---|
| `:Linux` | Linux programs, 32 and 64 bits |
| `:Linux32`, `:Linux64` | Linux programs of one width (the system calls' numbers differ between them) |
| `:Windows` | Windows programs, 32 and 64 bits (Windows' functions are the same in both) |
| `:PhiOS` | PHI OS programs |

A system's word is a kind of `:Program`: its classes are a program's, and they're left
out of a kernel. A file with one system's classes has only that system's. A name with no
version for the system being built is an error where it's used (`Raw has no version for
Windows: there's only :Linux32 and :Linux64`), when the program is built; so is a name
with two versions for one system (a `:Linux` and a `:Linux64`).

**System calls.** `sys.Name: number  type name ...;` declares one, at the top level of a
system's module, with that system's number for it and what it takes; the code uses it by
name, as `call fd is sys.Open: path 0;` or `(sys.Open: path 0)`, and `(sys.GetPid)` when
it takes nothing. Each value is checked against its type, as a method's are. The answer
comes back as the system gives it: on Linux a number that's negative when it says no
(`-9`, EBADF). It's an `int`, or what `: type` at the end says, as a method type's result
is written (`sys.Map: 9  ptr<u8> address  u32 size ...: ptr<u8>;`, and a `u64` value or result
is all 64 bits in 64-bit code, and an error in a 32-bit system's, whose calls take two halves): a pointer is all 64
bits of it in 64-bit code, where an `int` would keep only 32. A system call is only in a module for a system that has them (`:Linux`,
`:Linux32`, `:Linux64`, `:PhiOS`), and is used only from that module's classes, so the
code that is one system's alone stays in the files marked for it.

| System | Instruction | Values in |
|---|---|---|
| PHI OS, 32-bit Linux | `int 0x80` | `ebx`, `ecx`, `edx`, `esi`, `edi`, `ebp` |
| 64-bit Linux | `syscall` | `rdi`, `rsi`, `rdx`, `r10`, `r8`, `r9` |

Up to six values; the number goes in `eax`, and the answer comes back there. Windows has
no system calls a program makes itself: its programs call DLLs' functions.

The standard library does this itself: in a Linux or Windows program, time (`OS.Sleep`,
`OS.GetTicks`, `OS.Now`, `OS.Yield`), files (`OS.ReadFile` to `OS.Rename`), the network
(`net.sockets`' calls), threads (`user.threads`' calls) and a window (`gui.app`'s calls) are
done by modules in `lib/phi/host/` (`time_*.phi`, `files_*.phi`, `net*.phi`, `threads*.phi`,
`window_*.phi`, with Linux's numbers in `linux32.phi` and `linux64.phi`) rather than by
assembly. A program gets only
the parts it uses, as it gets the heap only if it uses `new`. Two modules may declare the same system call or
outside function if they declare it the same way (a program's own module and these);
declaring it differently is an error.

**Outside functions.** A library outside PHI (on Windows, a DLL) is brought in with `use`
and its file's name, quoted, under a name that reads as a class's; each of its functions
the module calls is declared once, as a system call is but without a number, and called by
that name:

```phi
use 'kernel32.dll' as Kernel32;
use 'user32.dll' as User32;

phi.Win:Windows
{
    [Page]
        ptr<u8> page: (Kernel32.VirtualAlloc: 0 4096 0x3000 4);
    [end: page]
}

Kernel32.VirtualAlloc: ptr<u8> address  u32 size  u32 kind  u32 protect: ptr<u8>;
Kernel32.GetLastError;                          # takes nothing, gives an int
Kernel32.GetCommandLineA: : ptr<u8>;            # takes nothing, gives a pointer
User32.CharUpperA: ptr<u8> text: ptr<u8>;
```

The compiler puts each function that's called in the program's import table (a library
the runtime already uses, such as kernel32.dll, gets a second entry; Windows reads them one
by one) and calls it as Windows' functions are called: in 32-bit code (stdcall) the values
are pushed last first and the function takes them off; in 64-bit code the first four go in
`rcx`, `rdx`, `r8` and `r9` and the rest on the stack, above 32 bytes the function may
write, with the stack 16 bytes aligned. A function takes and gives numbers, `bln`, `chr`,
enums and pointers; floats, and 64-bit numbers in a 32-bit program, aren't there yet. For
now outside libraries are only in `:Windows` modules, whose classes alone may call them;
Linux's and macOS's (a library loaded with the program) come with H7.

**Being called from outside.** `addr foreign Name` is where outside code can call a method:
a window system's window procedure, say, or anything else that takes a function. The
compiler makes an entry for it that takes the values the system's way, keeps the registers
the system expects to find as it left them, calls the method, and returns the system's way.
What it takes and gives go in a register, as with an outside function. Give a pointer back
as a pointer (`[end: p]`, with `p` a `ptr<u8>`), since an `int` is only 32 bits in 64-bit code.
It runs on the thread that called it, so that has to be one PHI started (the program's own
or a `Threads` one), and an error thrown out of it would have to cross the system's frames:
catch what can go wrong inside it.

```phi
[OnWindow: ptr<u8> window: 0; ptr<u8> value: 0;]
    count++;
[end: 1]                                        # 1: carry on

int done: (User32.EnumWindows: addr foreign OnWindow 0);
```

**Structs as C lays them out.** A PHI struct is packed, each field right after the last.
Outside code expects C's layout: `struct.Name foreign` puts each field on a multiple of its
own size, with padding before it if it needs some, and rounds the whole up to a multiple of
its largest field, so it matches in 32 and 64 bits without writing the padding by hand. It
holds what C does: numbers, pointers, arrays of them, and other foreign structs.

```phi
struct.Message foreign          # 28 bytes in 32-bit code, 48 in 64-bit code
{
    ptr<u8> window;
    u32 message;
    ptr<u8> w;                  # (after 4 bytes of padding, in 64-bit code)
    ptr<u8> l;
    u32 time;
    i32 x;
    i32 y;
}
```

## Methods

```phi
[Add: int x: 0; int y: 0;]      # parameters, with defaults
[end: x + y]                    # [end: value] returns a value; [end] returns nothing

[Greet: str who: 'world';]
    log 'hello ' who;
[end]

[Make: int value: 0; : ptr<Node>]   # what it returns, after a second ':' (it can be left out)
    if value < 0 return 0;
    ptr<Node> n: new Node;
    n.value is value;
[end: n]

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
  if it can, and what doesn't fit one with a size is an error. Any text can be passed to one the method only
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
  doesn't). A `chr` result stays a character, as `[end: name:0]` and `[end: 'N']` give: `log`
  and text write it as one, and `chr c: (Letter);` takes it. That's when each `return` is a
  character too (a one-character string, a `chr`, `name:i`, or `x as chr`). With any other
  return, a method is what it would be without one: `[end: name:i]` beside `return -1;` gives a
  number, so -1 stays -1, and `[end: 'x']` beside `return name;` a `str`. Called through a
  method value or an interface, any result that's a number is an `int`, so it takes `as chr`
  there to be written as a character. A `chr` isn't an address: giving one to a pointer is an
  error (`c as ptr<u8>` if that's meant).
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
- **A number that would come out as a different number is an error when it runs:** a
  negative number going to a `u32` (`-1` would be 4294967295, and a count of that runs through
  all of memory), or a `u32` of 2 billion or more going to an `int` (it would be negative, and
  get past a check like `if n > 0`). That goes for a parameter, a variable, a field, an
  element, a result and a new array's size, and it can be caught
  ([Errors: throw and throws](#errors-throw-and-throws)).

  ```phi
  int n: -1;
  if call Take: n throws err        # a negative number can't be a u32 on line 2 in game.phi
      log err '\n';
  ;;
  call Take: n as u32;              # `as` keeps the same bits, and checks nothing
  call Take: (-1);                  # a constant that can't fit is an error when it's built

  [Take: u32 count: 0;]
  [end]
  ```

  So write `as` for raw bits: bytes put together (`(p:3 << 24) | ...`), a hash, an address, or
  a difference that's meant to wrap. A number that can't be a problem isn't checked: a
  constant that fits, `.len`, `.max`, `x & 0xFF`, and for a `u32` going to an `int`, `x >> 1`,
  `x / 2` and `x % 100`. Arithmetic on a variable isn't a conversion (`u32 x: 2; x-- 4;` wraps,
  which is what `Mem`'s own check is for), an index has its own check, and an `OS.` call takes
  either kind the same on every system. An `OS.` call's result is an `int`, so one holding bits
  says `as u32` going into a `u32`, as `u32 now: (OS.GetTicks) as u32;` does once a machine has
  been up for 25 days; `Net.Address` and the like do that for you. In a kernel an error that isn't caught stops the
  kernel, so a value that comes from a program should be checked first, not left to this.
- **Leaving early.** `return value;` ends a method that has `[end: value]`, and `return;`
  ends one that has `[end]`. The value must be the same kind (number, float, 64-bit number
  or `str`) as the one after `[end: ...]`. The `[end: value]` decides what kind of method it is, so it's needed
  even when every way out is a `return` (`[end: 0]` will do). A string literal of other than
  one character there (`[end: 'none']`) makes a `str` method, and so does one of one character
  beside a `return` of text. Unless a `return` hands back an `addr` or a
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

- **What a method returns** is worked out from its `[end: value]`, or said in its header
  after a second `:`, as an interface's methods say it: `[Size: : int]`,
  `[Find: int id: 0; : ptr<Node>]`. Then its `[end: value]` and each `return` go to that type,
  so `[end: 0]` and `return 0;` are null pointers in a method that returns a pointer, and a
  reader can see what it returns from the top. It can be any type a method can return, with a
  whole number being an `int`, `u32`, `i64` or `u64`, and the method has to end with a value.
- In 64-bit code a pointer going to a 32-bit number (an `int` variable, a parameter, or a
  method whose `[end: 0]` made it return an `int`) is an error, since it only works until the
  program's memory is above 4 GB. `as int` says the cut is meant; a number going to a pointer
  is a warning, as it may be an address.
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
  goes. Method values can't take arrays; an interface's methods can.
- **A pointer and a count make one**, with `as`: `(p as u8[n])` is the `n` elements at `p`.
  The method's indexes are checked against `n`, where a pointer's never are, so passing
  this in place of a pointer and a length is how code that works through pointers gets
  checked. It's also how to pass part of an array: `(addr a:2 as int[3])`. A negative count is
  an error when it runs (`ErrorKind.CONVERT`). It can only be passed to such a parameter, and
  it's the program's word for it that the memory is there and that many elements long.

  ```phi
  ptr<u8> bytes: new u8[size];
  call Parse: bytes as u8[size];

  [Parse: u8 input[];]
      chr c: input:i as chr;      # an index past size is an error, which can be caught
  [end]
  ```
- `str rows[]` takes any `str` array whose elements grow: one of values, `str names[8];` or
  `str names[atmost 64];` (32-bit code). Its elements are the caller's texts, so writing or
  adding to one changes the caller's, and it grows as theirs would. A whole array copies
  into it (`rows is other;`) when both have as many elements.

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
- One can return a `str` (`method<int: str>`, in 32-bit code) or a new object
  (`method<: Point>`), handed to the caller as a call by name's is.
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

**64-bit code** (a 64-bit program) takes a block's `x64.Name` version, written beside its
`asm.Name` with the same name. The call doesn't change, and each kind of code gets the one
for it; using a block that has no version for the code being built is an error naming it.
An x64 block keeps `rsp` and `rbp`; an address is 64 bits (`mov rdi, [rel {at}]`), and
`rel` reaches a variable wherever the program is loaded.

```phi
asm.Zero                   # 16-bit and 32-bit code
{
    mov edi, [{at}]
    mov ecx, [{words}]
    xor eax, eax
    rep stosd
    ret
}

x64.Zero                   # 64-bit code: the same, with the address in all of rdi
{
    mov rdi, [rel {at}]
    mov ecx, [rel {words}]
    xor eax, eax
    rep stosd
    ret
}
```

## Files

```phi
use drivers.vga_text;        # loads drivers/vga_text.phi
use support.helpers;         # loads support/helpers.phi
```

`use a.b;` looks for `a/b.phi` next to the file that has the `use`, then next to the main
program, then (for a program in a kernel's rootfs) next to the kernel, then in the standard
library ([lib/phi](../lib/phi)). So the programs of a rootfs can share code kept in a folder
next to the kernel, like `lib/widgets.phi` used as `use lib.widgets;`. Each file is loaded once,
and the classes of used files run before the classes that use them. At each of those places, a
`.phi` file comes before a compiled library of the same name, `a/b.philib` (below).

### Compiled libraries

A project can be compiled into one file, which another project includes without having its
source:

```
phi build physics.phi --library     # physics.philib, next to physics.phi (or in -o)
```

```phi
use physics;                        # in a program in another folder, with physics.philib next to it
```

The `.phi` file's classes are `phi.Name:Library`, and it and the files it uses from beside it
are the library: everything they declare can be used, as if they were next to the program. A
library has no start of its own, so a `phi.Name:Program` class is an error. Each `use` of
one finds the file by name as it would a `.phi` file, so a program doesn't change when the source is
swapped for the compiled file.

**The code goes inside the program,** so there's nothing to ship with it and nothing is loaded
when it runs. PHI OS needs no library system for this: a program is still one `NAME.BIN`, and a
`.philib` in a rootfs folder isn't put on the disk.

**One file for every system.** It has a version for each one the library builds for: PHI OS
programs, PHI OS kernels, and 32 and 64-bit Linux and Windows (`--target` builds only that one).
One that doesn't build, like a driver on Linux, is left out and the build says why; using the library there is
an error that says which systems it has. If none builds, those are the build's errors. 16-bit
programs can't use one.

**A library that uses others.** The standard library's modules it uses aren't copied in: the
program compiles one copy of each, whichever libraries want it. Another compiled library is
needed next to this one (or next to the program), and is included once however many use it.
The library's classes run together, after those of what it uses, where its `use` first puts them.

**A program only gets what it reaches.** Methods that nothing calls are left out. The library's
class variables and text always go in, and so does its classes' own code.

**Rebuild it with PHI.** A library has to be built by the same build of `phi` that uses it, so
keep its source: another one is an error saying to build it again. (The conventions between
compiled code can change until there's a release.)

**Not there:** a program can't call a library's `asm.` blocks directly, only its methods. Two
libraries, or a library and a program, can't both have a class, struct or enum of the same name:
that's an error. A debugger steps over a library's code, since its source isn't there. Calls
into a library aren't written out in place, as a small method's can be. A library is for PHI code:
DLLs and `.so` files, and C calling PHI, are [C compatibility](../Plan.md#later)'s.

### Standard library

| File | What it provides |
|---|---|
| `drivers.vga_text` | class `Vga` (both kinds of program): `Clear`, `SetColor: fg bg`, `SetCursor: row column`, `PutChar: c`, `Print: text`, `PrintNumber: n`, scrolling, the hardware cursor |
| `boot.info` | 32-bit kernels: `Boot.info`, a `ptr<BootInfo>` to what the loader found (memory map, boot drive, cursor) |
| `memory.frames`, `memory.paging`, `memory.heap`, `memory.list` | 32-bit kernels: see [Memory](#memory-32-bit-kernels) |
| `memory.pool`, `data.map`, `data.keyed`, `data.queue` | 32-bit kernels and programs: see [Memory](#memory-32-bit-kernels) |
| `drivers.ata` | class `Ata` (both kinds of program): `Read: lba count buffer` →, `Write: lba count buffer` → for the IDE disk, `Present` →, `Sectors` → |
| `drivers.virtio_blk`, `drivers.nvme` | classes `VirtioBlk` and `Nvme` (32-bit kernels): a virtio-blk disk and an NVMe one (any maker's: found by its PCI class), with `Start` →, the same `Read` and `Write` as `Ata`, and `sectors`; both poll, and their sectors go through the driver's own buffer, so the caller's can be anywhere. An NVMe disk's sectors must be 512 bytes |
| `drivers.block` | class `Block` (32-bit kernels): whichever disk the machine has (virtio-blk, then NVMe, then IDE), as numbered sectors from the start of the disk: `Start` →, `Read: sector count buffer` →, `Write` →, `Sectors` →, `kind` (`Block.ATA`, `Block.VIRTIO` or `Block.NVME`). `phi run` and `phi test` give a machine an IDE disk, or another kind with `--disk virtio` or `--disk nvme` |
| `drivers.disk` | class `Disk` (32-bit kernels): the disk's FAT partition, on whichever disk `Block` found, as numbered sectors: `partition_found`, `Read`, `Write` |
| `fs.fat16`, `fs.savelog`, `fs.stream`, `fs.stream_calls` | 32-bit kernels: see [Files on disk](#files-on-disk-32-bit-kernels) |
| `user.stream`, `fs.readahead`, `net.web`, `net.webstream` | a program's streams, a stream read ahead on a thread, web requests, and streams on URLs (a kernel's too): see [Files on disk](#files-on-disk-32-bit-kernels) |
| `data.crc32` | class `Crc32` (32-bit kernels and programs): `Of: data length` →, `Add: crc data length` → (carries on from an earlier CRC) |
| `fs.fileinfo` | `FileInfo`, one entry of a folder: what `Fat.Entries` and a program's `OS.ListFolder` fill in |
| `proc.process` | 32-bit kernels: `Process.Spawn: path` →, `Wait: pid` →, `WaitAll` (see [Programs](#programs-and-processes)) |
| `proc.cores`, `proc.threads` | 32-bit kernels: the other cores (`Cores.Start`, `Run`, `SharePrograms`) and kernel threads (`Threads.Start: method value`, `Wait`, `Done`): see [Several cores](#several-cores-32-bit-kernels) |
| `gui.app`, `gui.surfaces`, `gui.desktop`, `gui.events`, `gui.widgets`, `gui.menu`, `gui.dialogs` | windows for user programs, and a desktop for them: see [Windows for programs](#windows-for-programs) |
| `user.cut` | class `Cut` (32-bit kernels and programs): `Into: target value`, as much of `value` as fits, into a `str` |
| `user.text` | class `Text` (any program): `Length`, `Equal`, `EqualIgnoreCase`, `StartsWith`, `EndsWith`, `Lower: c`, `Upper: c`, `SkipSpaces`, `CopyWord: text buffer max`, `AfterWord`, `Copy: buffer text size` →, `Append: buffer text size` →, `AppendNumber: buffer n size` →, `AppendPadded: buffer n width size` → (zeros in front), `AppendSize: buffer bytes size` → (`512 bytes`, `13 KB`, `4 MB`), `TrimEnd`, `ToNumber`; the `Copy` and `Append` methods cut to fit the buffer and return the new length |
| `user.math` | class `Math` (any program): `Abs`, `Min`, `Max`, `Clamp: value low high`, `Sign`, `Sqrt` (whole-number square root, rounded down) |
| `memory.mem` | class `Mem` (32-bit kernels and programs): `Copy: to from count`, `Move: to from count` (the areas may overlap), `Fill: to byte count`, `Fill32: to value count`, `Equal: a b count` →, using the CPU's `rep movs` and `rep stos`. A count of 2 billion or more to any but `Equal` is an error at the call |
| `drivers.rtc` | class `Rtc` (both kinds of program): `Read` fills in `year month day hour minute second` from the CMOS clock (UTC on QEMU); `ReadLocal` gives the time in the host's time zone (which `phi run` and `phi test` pass to QEMU; `HostZone` → reads it), or in the one `SetZone: minutes` sets, and `AddMinutes: n` moves the fields, carrying into the day, month and year |
| `drivers.pci`, `drivers.nic`, `drivers.rtl8139`, `drivers.virtio_net`, `net.net`, `net.udp`, `net.dhcp`, `net.dns`, `net.tcp`, `net.tls`, `net.http` | 32-bit kernels: see [The network](#the-network-32-bit-kernels) |
| `drivers.acpi` | class `Acpi` (32-bit kernels): ACPI's MADT: `Start` →, `cores` and `core_ids` (local APIC IDs), `local_apic`, `io_apic`, `io_apic_base`, `Input: irq` → (the I/O APIC input an ISA IRQ comes in on), `Flags: irq` → (its polarity and trigger), `Find: name` → (any table by its 4 letters) |
| `drivers.apic` | class `Apic` (32-bit kernels): `Start` → moves the IRQs from the 8259 PICs to the I/O APIC (the same vectors, to this core; handlers, `OS.SetIrqHandler` and `OS.MaskIrq` work as before), and turns this core's local APIC on; `id` |
| `drivers.ac97` | 32-bit kernels: see [Sound](#sound) |
| `audio.sound`, `audio.synth`, `audio.wav` | any program or kernel: see [Sound](#sound) |
| `crypto.bytes`, `crypto.sha256`, `crypto.sha512`, `crypto.hmac`, `crypto.pbkdf2`, `crypto.chacha20`, `crypto.poly1305`, `crypto.chachapoly`, `crypto.aes`, `crypto.aesgcm`, `crypto.x25519`, `crypto.random` | any program: see [Cryptography](#cryptography) |
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
| `OS.GetTicks` → | 32-bit | milliseconds since the timer started (by the CPU's timestamp counter, so it keeps time when timer interrupts come late) |
| `OS.Now` → | 32-bit | the date and time, as seconds since 1970 (UTC), a `u32` (good until 2106): a kernel reads the CMOS clock (QEMU's is in UTC), a program asks its system |
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
  that isn't caught (see Errors: inside a catch, dividing by zero, an empty object or a null
  pointer, and memory that isn't there become errors) shows the **panic screen**: the exception's name and number, its error code, the
  instruction address (`eip`) and every register, on a red screen and on COM1. Then the
  CPU stops.
- For a **hardware interrupt**, `call OS.SetIrqHandler: 1 addr KeyPressed;` runs an
  ordinary method each time IRQ 1 fires and sends end-of-interrupt afterwards. The IRQ
  is unmasked for you.
- The **timer** interrupts 1000 times a second, to wake sleeping tasks and switch between
  them, but `OS.GetTicks` doesn't count those interrupts: its milliseconds come from the
  CPU's timestamp counter, timed against the PIT when the timer starts. A count would lose
  time whenever one interrupt comes before the last was taken (code that keeps interrupts
  off, or a busy host: QEMU on Windows loses about a third of them even when idle).
  `[OS.TimerEvent]` runs 60 times a second by the same clock.
- For full control, `call OS.SetInterruptHandler: vector addr Handler;` points a vector
  straight at an `[isr]` method (which must call `OS.EndOfInterrupt` for hardware IRQs).
  This is also how software interrupts (`int 0x80` in an `asm.` block) are handled.

## Not supported yet

- `dec` / `fin` (exact decimal numbers; `f64` and the others are binary floating point),
  and `^^` on 64-bit and floating point numbers
- division by zero isn't caught in 16-bit programs (in a 32-bit kernel it shows the panic screen)
- strings that grow in 16-bit programs (they have no heap; a `str` there is a fixed buffer)
- placing code or data at chosen addresses (`@org`, `@section`); the kernel's layout is fixed
  (see [memory-map.md](memory-map.md))
- characters beyond ASCII in strings (a byte above 127 can be written as `\xNN`)
- RSA-PSS with SHA-384, and network cards other than the RTL8139 and virtio-net
- NVMe disks with 4096-byte sectors, and disks above 4 GB in memory (where a 32-bit kernel
  can't reach their registers)
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

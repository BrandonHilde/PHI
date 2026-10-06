# PHI language reference

This describes the language as the compiler in `Compiler/Phi.Compiler` implements it today
(16-bit x86, QEMU). Ideas that aren't implemented yet live in [Syntax/](../Syntax) and
[Plan.md](../Plan.md).

## Program structure

A program is a list of classes. Each class has a base that says where its code runs:

```phi
phi.Hello:Bootloader        # the boot sector: exactly one, and it must fit in 512 bytes
{
    log 'Booting...';
    call Bootloader.JumpToSectorTwo;
}

phi.Kernel:OS               # loaded from disk after the boot sector (up to 32 KB)
{
    log 'Hello from the OS';
}
```

- The `Bootloader` class becomes the 512-byte boot sector at `0x7C00`.
- All `OS` classes are combined into the **kernel**, loaded at `0x7E00` by
  `call Bootloader.JumpToSectorTwo;`. The build works out how many sectors to load.
- Statements directly inside a class run in order, top to bottom, when the class starts.
  When they finish, the CPU halts but still handles interrupts (so timer and keyboard
  events keep running).
- The boot sector and the kernel are built separately, so they can't use each other's
  variables or methods.

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
| Strings | `'hello'`, `'it''s'` is two strings; `'hasn't'` works without escaping |
| Escapes | `\r` `\n` `\t` `\0` `\\` `\'` |
| Characters | a one-character string like `'w'` is its character code wherever a number is expected |
| Constants | `Colors.Black` … `Colors.White` (the 16 VGA colors) |

A quote followed directly by a letter or digit doesn't end a string, which is why
`'hasn't'` works. Strings can span several lines.

## Variables

```phi
int count: 0;               # 32-bit signed
byt small: 250;             # 8-bit, 0-255 (wraps around)
bln ready: false;           # 8-bit, 0 or 1
str name: 'PHI';            # text
var guess: 'text';          # str if every value is a string, otherwise int

int numbers: 1 3 5 9 2;     # several values make an array
str days: 'Mon' 'Tue';      # an array of strings
int squares[5];             # an array of 5 zeros
str buffer: [40];           # room for 40 characters (also: str buffer[40];)

str text: count;            # a number written out as text
```

`=` works in place of `:` in declarations.

**Size of a `str`.** A `str` has a fixed amount of room, decided when it's declared:

- `str s: 'abc';` holds 3 characters (the length of its first value)
- `str s: [40];` holds 40
- `str s: someNumber;` holds 11 (enough for any `int`)
- `str s: otherStr;` holds as much as `otherStr`
- each element of a `str` array holds as much as the longest initial value

Longer text is cut to fit: after `str tiny: [3]; tiny is 'abcdef';`, `tiny` is `abc`.

**Where variables live.** Every variable has a fixed place in memory (there is no stack
of local variables yet). Variables declared directly in a class are set once, when the
program is built. Variables declared in a method, or as a loop counter, are set again
each time the declaration runs.

**Scope.** A name is looked up in the current method, then the current class, then in
other classes of the same unit (if only one class has it). `Class.name` picks a specific
class. `x.len` is the number of elements of an array, or the current length of a `str`.

## Expressions

From lowest to highest precedence:

| Operators | Meaning |
|---|---|
| `or` | either is true |
| `and` | both are true |
| `is`, `==`, `is not`, `!=`, `<`, `>`, `<=`, `>=`, `<<` (same as `<=`), `>>` (same as `>=`) | compare |
| `+` `-` | add, subtract |
| `*` `/` `%` | multiply, divide, remainder (signed, rounding toward zero) |
| `-x`, `not x`, `!x` | negate, logical not |
| `list:i` | element `i` of an array, or character `i` of a `str` |
| `( )` | grouping |

- Math is 32-bit signed and wraps around on overflow.
- Two strings compare as text, but only with `is` / `==` / `is not` / `!=`.
- In a condition, any number counts as true unless it is 0.
- A binary operator must be on the same line as its left side. This is how
  `if ready` followed by a statement on the next line knows where the condition ends.

## Statements

Simple statements end with `;`. Blocks (`if`, `else`, `while`) end with `;;`.

### Output and input

```phi
log 'Score: ' score '\r\n';     # screen and serial port (COM1)
debug 'x = ' x '\n';            # serial port only: handy while developing
ask name;                       # read a line from the keyboard into a str
exit 0;                         # stop QEMU (used by tests); halts on real hardware
```

`log` and `debug` print strings as text and numbers in decimal. On the screen, a new line
needs `\r\n`.

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
```

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
```

- Arguments are separated by spaces and may span lines. A missing argument uses the
  parameter's default.
- A method can return a number or a `str`.
- Methods can be called before they appear, and from any class in the same unit
  (`call Other.Method;` to be explicit).
- Parameters and method variables have fixed places in memory, so a method can't call
  itself (recursion) yet.

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
restored around them.

## Assembly blocks

```phi
asm.Double
{
    mov eax, [{value}]     ; {name} is the address of a class variable
    add eax, eax
    mov [{result}], eax
    ret                    ; blocks must end with ret
}
```

Call them with `call Double;` (or `call asm.Double;`). `call x is Double;` stores what the
block leaves in `eax`. Blocks are only included in a unit that calls them. `arm.` blocks
are parsed but can't be called yet.

The code runs in 16-bit real mode with `ds = es = ss = 0`. You may change any
general-purpose register, but you must keep `sp`, `bp` and the segment registers.

## Built-in functions

Call them like methods. Those marked → return a value (`call x is OS.GetKey;`).

| Function | Where | What it does |
|---|---|---|
| `Bootloader.JumpToSectorTwo` | boot sector only | load the OS classes from disk and start them |
| `Bootloader.WaitForKeyPress` → | both | wait for a key (through the BIOS) and return its character |
| `Bootloader.EnableVideoMode` | both | switch to 320×200 graphics with 256 colors |
| `OS.SetupInteruptTimer` | both | run `[OS.TimerEvent]` 60 times a second |
| `OS.SetupKeyboardInterupt` | both | track keys and run `[OS.KeyboardEvent]` (the BIOS keyboard, `ask` and `WaitForKeyPress` stop working) |
| `OS.GetKey` → | both | the character of the last key pressed or released |
| `OS.IsKeyDown: key` → | both | 1 if the key (e.g. `'w'`) is held down |
| `OS.SetupMouse` | both | turn on the PS/2 mouse |
| `OS.UpdateMouse` | both | read waiting mouse movement (the functions below do this too) |
| `OS.GetMouseX` → / `OS.GetMouseY` → | both | cursor position (0–310, 0–190) |
| `OS.GetMouseDown` → / `OS.GetMouseUp` → | both | left button state |
| `OS.DrawRectangle: x y width height color` | both | fill a rectangle (clipped to the screen) |
| `OS.DrawPixel: x y color` | both | set one pixel |

`OS.SetupInterruptTimer` and `OS.SetupKeyboardInterrupt` (spelled correctly) also work.
`WaitForKeyPress` and `EnableVideoMode` can be called with `OS.` too.

## Not supported yet

- `dec` / `fin` (decimal numbers), `has`, `^^` (power)
- recursion (see "Where variables live")
- bounds checks on arrays and `str` indexes
- division by zero isn't caught

## How it is built

`phi build` runs the compiler and then NASM:

1. **Lexer** (`Syntax/Lexer.cs`): text to tokens
2. **Parser** (`Syntax/Parser.cs`): tokens to a syntax tree
3. **Binder** (`Semantics/Binder.cs`): resolves names, works out types and storage, reports errors
4. **Code generator** (`CodeGen/X86_16Generator.cs`): one NASM file per unit (`boot.asm`, `kernel.asm`),
   plus only the library routines the program uses, from [lib/x86_16](../lib/x86_16)
5. **NASM** assembles both; the kernel goes right after the boot sector in a 1.44 MB disk image

The `.asm` files are kept in the build folder, with each source line shown above the code
it produced.

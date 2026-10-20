# PHI for C programmers

PHI looks different from C, but most of it maps across directly. This page covers the
differences that catch people out: the ones that compile without complaint and then do the
wrong thing, or that give an error you might not expect. The full rules are in the
[language reference](language.md).

## Syntax

| C | PHI | Notes |
|---|---|---|
| `x = 5;` | `x is 5;` | As a statement, `is` assigns. |
| `if (x == 5)` | `if x is 5` | In a condition, `is` compares. `==` works too. |
| `{ ... }` | `... ;;` | `if`, `while`, `else` and `unsafe` blocks end with `;;`. |
| `int a[] = {1, -2};` | `int a: 1 -2;` | Values are separated by spaces, not commas. |
| `f(a, b)` | `call f: a b;` | A call is a statement. |
| `x = f(a);` | `call x is f: a;` | The result goes into a variable. |
| `return v;` | `[end: v]` | A method has one exit, at its end. |
| `&x` | `addr x` | |
| `sizeof(T)` | `sizeof T` | Works with types, structs and variables. |
| `p[i]`, `s.f` | `p:i`, `p:i.f` | The index after `:` is a number, a name, or `( )`. |

## Traps

**Spacing around `-` matters in lists.** Declarations, call arguments, `log` and `out` take
space-separated values. `-` with a space before and none after starts a new value. With
spaces on both sides, or none, it subtracts:

```phi
int steps: 1 -1 2 -2;     # four values
call Move: x -1;          # two arguments: x and -1
call Move: x - 1;         # one argument: x minus 1
```

**Calls can't be used inside expressions.** Not in an `if`, not in `log`, not as an
argument. Store the result first:

```phi
bln picture: false;
call picture is Str.EndsWith: name '.BMP';
if picture
    ...
;;
```

**There is no `break`, `continue` or early `return`.** Use a flag in the loop condition:

```phi
bln found: false;
while int i: 0; i < count and not found; i++;
    if items:i is wanted
        found is true;
    ;;
;;
```

**Variables don't live on a stack.** Every variable, including method parameters and
locals, has a fixed address. This has three consequences:
- A method can't call itself, even through another method.
- An interrupt handler that calls a method the main code also calls can overwrite that
  method's variables mid-call. Give interrupt handlers their own methods and variables.
- A method's local variables share one scope across the whole method. Declaring
  `int got` in both branches of an `if` is an error ("already declared"). Declare it once,
  above the `if`.

**Operator precedence differs from C.** The bitwise operators bind tighter than
comparisons, so `flags & 4 is 4` means `(flags & 4) is 4`, which is the opposite of C.
Only one comparison is allowed per expression: write `(a < b) is false`. A binary operator
must be on the same line as its left side.

**Math is signed or unsigned by type.** Arithmetic is done in 32 bits. It's unsigned if
either side is a `u32` or a pointer, which affects `/`, `%`, `>>` and comparisons.

**A `str` has a fixed capacity, and longer text is silently cut.** The capacity comes from
the declaration: `str s: 'abc';` holds 3 characters, and `str s: [40];` holds 40. Assigning
`'abcdef'` to a 3-character `str` stores `abc`.

**Putting a pointer in a `str` writes the number, not the text.** `s is p;`, where `p` is a
`ptr<u8>`, stores the address as decimal digits, because numbers assigned to a `str` are
written out as text. To build text from bytes, copy them into a `u8` array, or give the
parameter the type `str` so a literal is copied in.

**A `str` array is an array of pointers.** Each element points to its own buffer, so the
strings aren't laid out one after another in memory. For a 2D grid of bytes, use a `u8`
array and index it as `rows:(row * width + column)`.

**Arrays work where pointers are expected.** `call Fill: buffer;` and
`ptr<u8> p: windows:0.title;` both pass the array's address, as `addr` would. Any number
also converts to any pointer type without a cast, so the compiler won't catch a pointer of
the wrong type.

**Array indexes are checked.** An index outside the array stops the program with
`PHI: index out of range on line N`, and a constant index is checked at build time. Code
inside an `unsafe` block skips the checks, and a pointer is never checked.

**Some common names are keywords.** These can't be variable names: `end`, `is`, `in`,
`out`, `addr`, `sizeof`, `new`, `free`, `has`, `below`, `above`, `atmost` and `atleast`.
The compiler says so (`'end' is a keyword and can't be a variable name`).

**A fixed-size array holds at most 16384 elements.** Larger buffers in a kernel come from
`new`. User programs don't have a heap yet.

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
| `f(a, b);` | `call f: a b;` | A call on its own is a statement. |
| `x = f(a) * 2;` | `x is (f: a) * 2;` | A call used as a value goes in parentheses, with a space after `:`. |
| `g()` as a value | `(g)` | A call with no arguments. |
| `return v;` | `return v;` | The method's normal result is the `[end: v]` at its end. |
| `break;`, `continue;` | `break;`, `continue;` | Only in `while` loops. |
| `if (!ok) return 0;` | `if not ok return 0;` | One line, no `;;`, for `return`, `break`, `continue`, `call` and `exit`. |
| `switch (k) { case 'q': case 27: ...; break; default: ... }` | `when k` / `is 'q' 27` ... `;;` / `else` ... `;;` / `;;` | No fall-through and no `break`; works on text too. |
| `enum Tool { PENCIL, BRUSH = 5 };` | `enum.Tool { PENCIL  BRUSH: 5 }` (one a line) | A type of its own: only `Tool.` values go into a `Tool`, and comparing it with another enum is an error. |
| `"\xED"` | `'\xED'` | Strings use single quotes; `\x` takes exactly two hex digits. |
| `&x` | `addr x` | |
| `int (*f)(int, int)` | `method<int int: int> f` | Set with `f is addr Add;`, call with `(f: 1 2)`. |
| C++ `c.add(3)` | `call c.Add: 3;` | Methods written inside a struct; `self` is the pointer to it. |
| C++ `auto p = std::make_unique<Point>(1, 2)` | `new Point p: 1 2;` | An object: PHI frees it when its owner goes away. There's no `delete`. |
| C++ `*a = *b` (deep, for owned members) | `a is b;` | Copies values, including the objects `a` owns. Addresses are never shared. |
| C++ `a = std::move(b)` | `a takes b;` | Hands the object over and leaves `b` empty. |
| `p == NULL` for an object | `p is empty` | |
| `sizeof(T)` | `sizeof T` | Works with types, structs and variables. |
| `int64_t`, `uint64_t` | `i64`, `u64` | 32-bit kernels and programs. Narrowing to 32 bits needs `as`. |
| `double`, `float`, `_Float16` | `f64`, `f32`, `f16` | 32-bit kernels and programs. To a whole number only with `as` (toward zero); `sqrt(x)` is `(Float.Sqrt: x)`. |
| `(T)x` | `x as T` | A cast. Binds tighter than math: `(a + b) as u8`. |
| `p[i]`, `s.f` | `p:i`, `p:i.f` | The index after `:` is a number, a name, or `( )`. |
| `p->f` | `p.f` | `.` reaches a field through a pointer to a struct too (one pointer per `.`). |

## Traps

**Spacing around `-` matters in lists.** Declarations, call arguments, `log` and `out` take
space-separated values. `-` with a space before and none after starts a new value. With
spaces on both sides, or none, it subtracts:

```phi
int steps: 1 -1 2 -2;     # four values
call Move: x -1;          # two arguments: x and -1
call Move: x - 1;         # one argument: x minus 1
```

**A call used as a value needs parentheses and a space after the `:`.** `(Length: text)`
is a call, but `(list:i)` is an element of `list`:

```phi
if (EndsWith: name '.BMP')
    ...
;;
```

**Class variables are globals; method variables are locals.** A method's parameters and
variables are on the stack, as in C, and start at zero on every call. Variables declared in
a class have fixed addresses and are shared by everything, including interrupt handlers.
Two differences from C:
- A method's variables share one scope across the whole method. Declaring `int got` in both
  branches of an `if` is an error ("already declared"). Declare it once, above the `if`.
- A method's variables can take at most 4096 bytes of stack. Put large buffers in the class.

**Operator precedence differs from C.** The bitwise operators bind tighter than
comparisons, so `flags & 4 is 4` means `(flags & 4) is 4`, which is the opposite of C.
Only one comparison is allowed per expression: write `(a < b) is false`. A binary operator
must be on the same line as its left side.

**Math is signed or unsigned by type.** Arithmetic is done in 32 bits. It's unsigned if
either side is a `u32` or a pointer, which affects `/`, `%`, `>>` and comparisons.

**A `str` has a fixed capacity, and longer text is silently cut.** The capacity comes from
the declaration: `str s: 'abc';` holds 3 characters, and `str s: [40];` holds 40. Assigning
`'abcdef'` to a 3-character `str` stores `abc`.

**A method's `str` result lasts only for the statement that called it.** It's copied into
a temporary in the caller, so `ptr<u8> name: (Name);` would point at memory reused by the
next call, and the compiler refuses it. Store the result in a `str`
(`s is (Name);` copies the text, as `s is p;` does for a `ptr<u8>`), or have the method
end in a `ptr<u8>`, such as `ptr<u8> text: 'literal';` and `[end: text]`.

**A `str` array is an array of pointers.** Each element points to its own buffer, so the
strings aren't laid out one after another in memory. For a 2D grid of bytes, use a `u8`
array and index it as `rows:(row * width + column)`.

**Arrays work where pointers are expected.** `call Fill: buffer;` and
`ptr<u8> p: windows:0.title;` both pass the array's address, as `addr` would. Any number
also converts to any pointer type without a cast. One kind of pointer becoming another
(`ptr<A>` where `ptr<B>` goes) gives a warning; write `p as ptr<B>`, PHI's cast, when it's
meant.

**Array indexes are checked.** An index outside the array stops the program with
`PHI: index out of range on line N`, and a constant index is checked at build time. Code
inside an `unsafe` block skips the checks, and a pointer is never checked.

**Some common names are keywords.** These can't be variable names: `end`, `at`, `is`, `in`,
`out`, `addr`, `as`, `sizeof`, `method`, `new`, `free`, `takes`, `empty`, `has`, `break`, `continue`, `return`,
`below`, `above`, `atmost` and `atleast`. The compiler says so and suggests other names
(`'end' is a keyword and can't be a variable name; try 'stop' or 'last'`). The full list is
in [Reserved words](language.md#reserved-words).

**A fixed-size array holds at most 16384 elements.** Larger buffers in a kernel come from
`new`, in a kernel or a user program.

**`is` between objects copies; it never makes two names for one object.** After `b is a;`,
changing `b.x` leaves `a.x` alone, and copying a list copies every node. To move an object
without copying, use `takes`. To refer to an object without owning it, pass it to a
method (a struct parameter borrows it) or take a `ptr<T>` with `addr`, which is only good
while the owner is alive.

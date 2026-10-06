# 2. Language basics

This part covers the everyday language: variables, conditions, loops, arrays, strings and
methods. The program is a 32-bit kernel (`phi.Name:Kernel`), so there's no 512-byte limit.
You'll learn what that means in the next part; for now it's just a roomier place to run code.

The whole program is [examples/02-basics.phi](../examples/02-basics.phi). Run it with
`phi run docs/examples/02-basics.phi`.

## Variables

```phi
int width: 80;
int height: 25;
int cells: width * height;
log 'The screen has ' cells ' cells.\n';
```

A declaration is `type name: value;`. The common types:

| Type | Holds |
|---|---|
| `int` | whole numbers, -2147483648 to 2147483647 |
| `u8`, `u16`, `u32` | unsigned numbers of 1, 2 and 4 bytes (`byt` is another name for `u8`) |
| `bln` | `true` or `false` |
| `str` | text, with a fixed amount of room |

Changing a variable uses `is` (or `=`):

```phi
width is 40;
width++;        # add 1
width++ 10;     # add 10
width-- 5;      # subtract 5
```

## Conditions

```phi
int temperature: 31;
if temperature > 30
	log 'Hot!\n';
;;
elif temperature < 10
	log 'Cold!\n';
;;
else
	log 'Just right.\n';
;;
```

The condition runs to the end of its line, and the block ends with `;;`. Compare with
`is` (or `==`), `is not` (or `!=`), `<`, `>`, `<=` and `>=`, and combine with `and`,
`or` and `not`.

## Loops and arrays

```phi
int primes: 2 3 5 7 11;
int sum: 0;
while int i: 0; i < primes.len; i++;
	sum++ primes:i;
;;
log 'The first ' primes.len ' primes add up to ' sum '.\n';
```

Several values make an **array**. `primes:i` is element `i` (counting from 0), and
`primes.len` is how many there are. An index outside the array stops the program with a
message giving the line, instead of quietly reading other memory.

There are two kinds of `while`: `while condition` repeats while the condition is true, and
`while int i: 0; i < 10; i++;` has a counter, like `for` in other languages.

## Strings

```phi
str greeting: 'hello';
str copy: greeting;
copy:0 is 'j';
log greeting ' becomes ' copy ' (' copy.len ' letters)\n';
```

This prints `hello becomes jello (5 letters)`. A `str` copies its text when you assign it,
`copy:0` is its first character, and a one-character string like `'j'` works as a
character code. Comparing two strings with `is` compares their text.

A `str` has a fixed amount of room: `'hello'` gives it 5 characters, `str name: [40];` gives
it 40, and longer text is cut to fit.

## Methods

```phi
int area: 0;
call area is Area: 6 7;
log 'A 6 by 7 rectangle has an area of ' area '.\n';
call Greet: 'PHI';
call Greet;

[Area: int w: 0; int h: 0;]
[end: w * h]

[Greet: str who: 'world';]
	log 'Hello, ' who '!\n';
[end]
```

A method is written `[Name: parameters] ... [end]`. Each parameter has a default, which is
used when the call leaves it out (so `call Greet;` says hello to the world). `[end: value]`
returns a value, which `call result is Method: arguments;` stores. Arguments are separated
by spaces.

The statements outside methods are the class's main code, and they run in order. Methods
only run when called, and they can be written after the code that calls them, so keeping
them at the bottom of the class reads well.

> **One thing to know:** a method's variables live at fixed places in memory, not on a stack.
> That keeps things simple and fast, but it means a method can't call itself (no recursion).

## Printing while you develop

`log` prints to the screen and the serial port. `debug` prints only to the serial port, which
`phi run` shows in your terminal. Use it to trace what your code is doing without cluttering
the screen.

## Try this

1. Write a method `Max: int a: 0; int b: 0;` that returns the larger number.
2. Count down from 10 with `while int i: 10; i > 0; i--;`.
3. Make an array of 5 strings (`str days: 'Mon' 'Tue' 'Wed' 'Thu' 'Fri';`) and print them
   on one line.

The [language reference](../language.md) has everything else: constants, bit operations,
sized types, structs and more.

Next: [3. A 32-bit kernel](03-kernel.md)

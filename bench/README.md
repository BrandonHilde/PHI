# Benchmarks: PHI beside C and C#

Each benchmark here is one program written three times, `NAME.phi`, `NAME.c` and `NAME.cs`, doing
the same work the same way and printing the same line.

```
phi compare                     build and run them all, and list the times
phi compare -f sort             only those with "sort" in their names
phi compare --runs 10           the best of 10 runs of each (5 unless said)
phi compare --save RESULTS.md   the tables into a file too
phi compare some/folder         the benchmarks of another folder
```

It builds each as a program for this computer: PHI in 64 and 32 bits, C with `-O2` and with `-O0`
(`gcc`, or `clang` or `cc` if that's what's there; `PHI_CC` names another), and C# as a Release
build with `dotnet`. A language whose compiler isn't installed is left out. It needs what
`phi build --target linux` needs (NASM), and nothing else.

[RESULTS.md](RESULTS.md) is the last run that was saved.

## A real program

The benchmarks here are small loops. A program that's only in PHI can be timed the same way, to
see what a change to the compiler does to real code: its C and C# columns are left out.

```
phi compare ../PHI-Code-Library/libs/web -f release_js_bench --varies -- run
```

What follows `--` is given to each program as its arguments, and `--varies` says its output isn't
the same from run to run (this one prints its own timings), so it's shown and not compared. That
line times weblib's JavaScript engine running its own benchmarks, and
[RESULTS-js.md](RESULTS-js.md) is the last run of it that was saved.

## What the numbers are

- **A time is the whole program's,** from being started to having ended, as `time` would give it,
  and the best of the runs. So a C# time has the runtime starting and the JIT compiling in it,
  which a longer-running program would notice less. `startup` does nothing, to show how much that
  is for each.
- **The second table is PHI's 64-bit time over each of the others'.** 1.50 means PHI took half as
  long again; 0.50 means half as long. "together" is the geometric mean of the rows.
- **A program that prints something else than PHI's does isn't doing the same work,** and
  `phi compare` says so and fails.
- These are small loops, each chosen to lean on one thing. A real program is a mixture, and its
  difference will be smaller than the largest here.

| Benchmark | What it leans on |
|---|---|
| `startup` | Nothing: starting and ending |
| `sieve` | A loop over bytes in memory |
| `fib` | Calls of a small method, 331 million of them |
| `interp` | A bytecode interpreter's loop: fetch, pick a case, move a value, its state in class variables |
| `collatz` | 64-bit arithmetic and a branch that can't be guessed |
| `sort` | Indexing, swapping and recursion (quicksort) |
| `mandelbrot` | Floating point multiplies and adds |
| `matrix` | Floating point in arrays, three loops deep |
| `crc` | A table, shifts and exclusive ors |
| `lists` | Making and freeing 20 million small objects (C# doesn't free: it collects) |
| `strings` | Building text: a line from words and numbers, added 3 million times to a text that grows to 30,000 characters. C keeps its length beside it and C#'s `StringBuilder` does too; PHI's method remembers it for a `str` of its own |

## Adding one

Write the three files with the same name. Keep them the same program:

- the same algorithm, line for line where the languages allow, with the same sizes of number
  (`u32` is `uint32_t` and `uint`; `i64` is `int64_t` and `long`);
- one line printed at the end that depends on all the work, so a compiler can't leave the work out
  and a mistake in one version shows;
- long enough to measure: a few tenths of a second;
- in C#, a public class named as the file is, with `public static void Main()`.

The `.c` or the `.cs` can be left out, and that column is empty for it.

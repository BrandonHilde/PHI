# Contributing to PHI

Thanks for helping! PHI is a language for writing operating systems, and there's plenty to
do at every level: the compiler, the standard library, drivers, the userland, and the docs.

## Set up

Follow [docs/getting-started.md](docs/getting-started.md) to install the .NET 8 SDK, NASM and
QEMU and build the compiler. Then check everything passes before you change anything:

```
dotnet build PHI.sln
dotnet test PHI.sln
phi test
phi test docs/examples
```

The QEMU tests take a couple of minutes; `phi test -f NAME` runs only the tests whose name
contains `NAME`.

## Find your way around

[docs/architecture.md](docs/architecture.md) explains how the compiler, library and build fit
together. Some common changes:

| To... | Change |
|---|---|
| add a statement or operator | `Syntax/Parser.cs` (and `Ast.cs`), `Semantics/Binder.cs`, `CodeGen/X86Generator.cs` |
| add a built-in function | an entry in `Semantics/Builtins.cs`, and a routine with that label in the right `lib/<target>/` file |
| add a driver or library | a `.phi` file in `lib/phi/`, used with `use folder.name;` |
| add a system call | `lib/phi/proc/process.phi` (kernel side, numbers 16+) and `lib/x86_32_user/system.asm` (program side) |
| add a shell command | a `.phi` file in `samples/os.rootfs/` (see [tutorial 8](docs/tutorial/08-shell-command.md)) |

## Before you open a pull request

- **Add a test.** For language and library changes, a QEMU test in `tests/`: write the
  program, run `phi test -f NAME`, check `NAME.actual`, and rename it to `NAME.expected`. If it
  works in both 16-bit and 32-bit mode, make its classes `:Library` so it runs in both. For
  compiler errors, a `NAME.errors` test or a unit test.
- **Run everything** (the four commands above) and make sure it all passes.
- **Update the docs** if you changed what PHI accepts or does: [docs/language.md](docs/language.md)
  for the language, the tutorial if an example changes, and [Plan.md](Plan.md) if you finished
  part of the roadmap.

## Style

- **PHI code** uses tabs, `lowerCamelCase` for variables, `PascalCase` for methods and classes,
  and `UPPER_CASE` for constants. In library files, class-level variables use `snake_case` with
  a prefix (`vga_row`, `frame_count`), so they don't clash with names in the programs that use
  them. Comments explain why, not what.
- **C#** follows the code around it: small methods, comments on anything non-obvious.
- **Assembly** files start with the `; provides:` header, then a comment saying what the file is
  for and how to call each routine (arguments, results, registers it changes).
- **Error messages** say what's wrong and, when possible, how to fix it:
  `str can't be used here; use a u8 array (u8 name[16]) or ptr<u8>`.

## Good first contributions

- New shell commands (`rm`, `write`, `sleep`, `top`)
- More tutorial exercises, with their solutions as tested examples
- Running PHI on Linux or macOS, and fixing what breaks
- The items under "Where things stand" in [Plan.md](Plan.md), for something bigger

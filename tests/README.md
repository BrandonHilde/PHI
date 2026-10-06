# PHI tests

There are two kinds of tests.

**Programs that run in QEMU** (this folder). Each test is a `.phi` program plus a
`.expected` file containing the serial output it must produce. `phi test` builds each
program, boots it headless in QEMU and compares the output (line endings and trailing
whitespace are ignored). `log` and `debug` both write to the COM1 serial port, which is
what the tests read.

```
phi test                 # everything in tests/
phi test -f loop         # only tests whose name contains "loop"
phi test samples/x.phi   # a single file
```

On a mismatch the real output is saved as `NAME.actual`. To add a test, write
`NAME.phi`, run `phi test -f NAME`, check `NAME.actual`, and rename it to `NAME.expected`.

Two optional files change what a test does:

- **`NAME.input`** types keys and moves the mouse, through the QEMU monitor. One command
  per line:

  | Line | Meaning |
  |---|---|
  | `expect TEXT` | wait until the output contains TEXT (up to 10 s) |
  | `wait MS` | pause |
  | `type TEXT` | press the keys for TEXT |
  | anything else | a QEMU monitor command, e.g. `sendkey ret`, `mouse_move 10 -5`, `mouse_button 1` |

- **`NAME.contains`** replaces `.expected` when the output changes from build to build
  (like addresses on a panic screen): each line must appear somewhere in the output.

- **`NAME.errors`** means the program must *fail* to compile. Each line is text that must
  appear in one of the errors, such as `4:6: error: unknown name 'missing'`.

A test made only of `phi.Name:Library` classes runs twice, as a 16-bit program and as a
32-bit kernel (`numbers (16-bit)`, `numbers (32-bit)`), against the same `.expected` file.
Most language tests are written this way, so both code generators stay in step.

A folder `NAME.rootfs/` becomes the FAT16 disk of a 32-bit test (see
`kernel32_files.rootfs/`); `.phi` files in it are compiled into `.BIN` user programs (see
`kernel32_processes.rootfs/`). `NAME.rootfs` can also be a file holding the path of a folder to
use, which is how `kernel32_shell` runs the real userland in `samples/os.rootfs/`.

`tests/support/` holds files that tests load with `use`; they aren't tests themselves.

**Compiler unit tests** (`Compiler/Phi.Compiler.Tests`) check the lexer, parser and error
messages without QEMU, and that the programs in `samples/` compile. Run them with
`dotnet test PHI.sln`.

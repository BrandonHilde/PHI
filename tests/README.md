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
phi test -f menu --input scenarios/open-files.input   # a different input script
phi test -f menu --keep-output                         # save NAME.actual even when it passes
```

On a mismatch (or always, with `--keep-output`) the real output is saved as `NAME.actual`. To add a test, write
`NAME.phi`, run `phi test -f NAME`, check `NAME.actual`, and rename it to `NAME.expected`.

Each test gets 10 seconds (`--timeout <sec>` changes that). A failure says why the run
stopped: `timed out after 10s` (with the input script's line, if one was running), or
`QEMU stopped by itself` when the program never reached `exit` or its expected output,
which is usually a triple fault (`phi run --debug` logs interrupts and resets).

Two optional files change what a test does:

- **`NAME.input`** types keys and moves the mouse, through the QEMU monitor. One command
  per line:

  | Line | Meaning |
  |---|---|
  | `expect TEXT` | wait until the output contains TEXT (up to 10 s) |
  | `wait MS` | pause |
  | `type TEXT` | press the keys for TEXT |
  | `pixel X Y RRGGBB` | check the color on the screen at (X, Y), in hex |
  | `screenshot NAME` | save the screen as `build/TEST/NAME.png` |
  | `mouse_to X Y` | move the mouse pointer to (X, Y) on the screen |
  | `click X Y` | move there and click the left button |
  | `dclick X Y` | move there and double-click |
  | `drag X1 Y1 X2 Y2` | press the left button at (X1, Y1), move to (X2, Y2) and let go |
  | anything else | a QEMU monitor command, e.g. `sendkey ret`, `mouse_move 10 -5`, `mouse_button 1` |

  QEMU only moves the mouse by amounts, so the first `mouse_to`, `click`, `dclick` or `drag`
  pushes the pointer into the top left corner, where a driver stops it at (0, 0) (the
  `drivers.input` driver does); from then on the script keeps track of where it is,
  including through `mouse_move` lines. `--input FILE` runs a different script in place of
  `NAME.input`, so one kernel can be tested with several scenarios.

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

A folder `NAME.web/` is served over HTTP and HTTPS while a test runs, by a small server in
`phi test` (responses have no date, so the output is the same every time). The program gets
the addresses as `TestWeb.url` (`'http://10.0.2.2:PORT'`) and `TestWeb.secure_url`
(`'https://10.0.2.2:PORT'`, TLS 1.3 with a self-signed certificate): QEMU's user network
reaches the host's `127.0.0.1` through the gateway, 10.0.2.2. See `net_http.web/`.
`phi serve folder [--https]` runs the same server by hand.

A file `NAME.sound` checks what the sound card played. Every machine has an AC'97 card;
with `NAME.sound`, QEMU records its output to `build/NAME/NAME.wav` (otherwise the sound
goes nowhere), and once the output matches, the recording is checked line by line:

| Line | Meaning |
|---|---|
| `sound at least SECONDS` | at least this much of it wasn't silent (`sound at most` too) |
| `tones HZ HZ ...` | the steady pitches heard, in order, each within 3% (a pitch heard again straight after itself counts once) |

The program should end with `exit`, so QEMU finishes the recording; see `sound_tones.phi`.

`tests/support/` holds files that tests load with `use`; they aren't tests themselves.

**Compiler unit tests** (`Compiler/Phi.Compiler.Tests`) check the lexer, parser and error
messages without QEMU, and that the programs in `samples/` compile. Run them with
`dotnet test PHI.sln`.

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
phi test os.phi --input a.input --contains a.contains  # ... and what its output must contain
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
  | `expect next TEXT` | the same, but only in the output after what the last `expect` saw, so a scenario that logs in twice waits for the second login |
  | `wait MS` | pause |
  | `type TEXT` | press the keys for TEXT |
  | `pixel X Y RRGGBB` | check the color on the screen at (X, Y), in hex |
  | `screenshot NAME` | save the screen as `build/TEST/NAME.png` |
  | `mouse_to X Y` | move the mouse pointer to (X, Y) on the screen |
  | `reboot` | restart the machine on the same disk, so what the kernel saved is still there (its output carries on); with one in the script, a kernel's own `OS.Reboot` restarts it too |
  | `click X Y` | move there and click the left button |
  | `dclick X Y` | move there and double-click |
  | `drag X1 Y1 X2 Y2` | press the left button at (X1, Y1), move to (X2, Y2) and let go |
  | anything else | a QEMU monitor command, e.g. `sendkey ret`, `sendkey shift-tab`, `mouse_move 10 -5`, `mouse_button 1` (`2` is the right button, `4` the middle) |

  QEMU only moves the mouse by amounts, so the first `mouse_to`, `click`, `dclick` or `drag`
  pushes the pointer into the top left corner, where a driver stops it at (0, 0) (the
  `drivers.input` driver does); from then on the script keeps track of where it is,
  including through `mouse_move` lines. `--input FILE` runs a different script in place of
  `NAME.input`, so one kernel can be tested with several scenarios.

- **`NAME.contains`** replaces `.expected` when the output changes from build to build
  (like addresses on a panic screen): each line must appear somewhere in the output.
  `--contains FILE` checks a different file of lines instead (of `NAME.expected` too), so
  `--input` and `--contains` together run one scenario of many against the same kernel:
  `phi test phios.phi --input tests/paint.input --contains tests/paint.contains`.

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

A file `NAME.machines` puts the test on a network with other kernels, which boot at the
same time. Each line names a `.phi` file next to the test and how many copies of it run:

```
# net_udp.machines: four clients for the server in net_udp.phi
net_udp_client.phi 4
```

The machines are connected by a hub in `phi test` instead of QEMU's user network, so there's
no DHCP server and no internet. Each machine gets a class `Machine` telling it who it is:
`Machine.number` (1 for the test, then 2, 3, ... in the order listed), `Machine.count`
(how many), `Machine.address` (`10.0.3.N`, for `Net.Start: Machine.address`) and
`Machine.first` (machine 1's address). Each machine is checked like a test of its own,
against its own file's `.expected` or `.contains`, with its own `.input` script if it has
one (copies of one file share them); a mismatch is saved as `NAME.actual` for the test and
`FILE.N.actual` for machine N. A file listed in a `.machines` file isn't run as a test of
its own. See `net_udp.phi`, and `samples/mmo/` for one with input scripts and a restart.
`phi run` boots the same machines, each in a window (see the README). A line `lose 1 in 40`
in the `.machines` file makes the hub lose every 40th frame, so a test sees what happens
when frames go missing (see `net_tcp.machines`). The machines are joined by a switch that
learns where each Ethernet address is, so a frame goes only to its destination (or to
everyone, for a broadcast). Every network test runs twice, once with each network card
(`net_tcp (rtl8139)`, `net_tcp (virtio)`), unless its `.machines` file has a line
`card virtio` (or `card rtl8139`) or `phi test` is given `--nic`.

A sample is a test too when it has a `.expected`, `.contains` or `.machines` file: plain
`phi test` runs those after `tests/`, named by their folder (`mmo/server`, so `-f mmo`
picks it).

A file `NAME.sound` checks what the sound card played. Every machine has an AC'97 card;
with `NAME.sound`, QEMU records its output to `build/NAME/NAME.wav` (otherwise the sound
goes nowhere), and once the output matches, the recording is checked line by line:

| Line | Meaning |
|---|---|
| `sound at least SECONDS` | at least this much of it wasn't silent (`sound at most` too) |
| `tones HZ HZ ...` | the steady pitches heard, in order, each within 3% (a pitch heard again straight after itself counts once) |

The program should end with `exit`, so QEMU finishes the recording; see `sound_tones.phi`.

`tests/support/` holds files that tests load with `use`; they aren't tests themselves.

## Benchmarks

A test named `bench_NAME.phi` is a benchmark. It times pieces of code with `test.bench`
and writes a `bench WHAT COUNT` line to COM1 for each:

```phi
use test.bench;

call Bench.Start;
call Sha256.Hash: addr data 4096 addr digest;
call Bench.Stop: 'sha256 4 KB';      # bench sha256 4 KB 984895
```

A plain `phi test` runs it like any other test, leaving the `bench` lines out when it
compares the output with `NAME.expected`. So a benchmark should also print what it worked
out (a hash, a checksum of pixels), which shows a change that makes it faster by breaking it.

`phi test --bench` runs only the benchmarks, under QEMU's `-icount`. There the timestamp
counter goes up with the instructions the CPU runs, so a count is the same on every run and
every computer. Each count is compared with the one saved in `NAME.bench`:

```
phi test --bench                      # every benchmark, against its saved counts
phi test --bench -f crypto            # only bench_crypto
phi test --bench --save-bench         # keep the latest counts as the new baseline
phi test --bench --tolerance 5        # allow 5% (the default is 2%)
```

A count more than the tolerance above its baseline fails the test (`SLOWER`), and so does
one that's no longer written. A count well below says `faster`, and `--save-bench` keeps
it. The first run of a new benchmark saves its counts. Commit the `.bench` files: they're
the baseline everyone compares with.

The benchmarks cover the kinds of data a game server handles (world state, network
messages, text, player records, NPC logic, memory and cryptography); the table under
Scenario 1 in [Plan.md](../Plan.md#scenario-1-an-operating-system-that-is-an-mmorpg-server)
says what each measures and what it costs.

A count is instructions, not time. It doesn't see what makes real hardware slower or faster
(caches, memory speed), and time spent waiting is skipped, but it's exact, which is what
noticing a change needs.

## Profiling

`phi profile file.phi` boots a kernel (or a 16-bit program) in QEMU and reads the CPU's
registers through QEMU's monitor about a thousand times a second. When it ends, it lists
the PHI lines, methods and library routines where the samples landed:

```
phi profile samples/web.phi                         # with a window, until you close it
phi profile samples/web.phi --input fetch.input     # headless, until the script ends
phi profile tests/bench_crypto.phi --seconds 20     # headless, for 20 s (or until it exits)
```

The report is also saved as `build/NAME/profile.txt`. Samples taken while the CPU waited
in `hlt` are counted separately, and a kernel waiting for a key shows time in the interrupt
handler and the keyboard routine. Time in user programs is shown as a total, without a
breakdown yet. The program runs more slowly while it's sampled (by about a third), but
each line's share of the time comes out about the same.

**Compiler unit tests** (`Compiler/Phi.Compiler.Tests`) check the lexer, parser and error
messages without QEMU, and that the programs in `samples/` compile. Run them with
`dotnet test PHI.sln`.

## Load testing

`phi load` boots a server together with load-client kernels (`lib/phi/test/load.phi`, up
to 100 connections each) on one network, and checks what the server does under that many
connections:

```
phi load samples/mmo/server.phi --connections 500 --seconds 60
phi load samples/mmo/server.phi --connections 100 --profile    # and where the server's time went
```

Each connection logs in (`login NAME PASSWORD`, answered `welcome NAME`), says a numbered
line once a second (`say ...`, answered `said ...`) and finally `quit` (answered `bye`),
the protocol of the mmo sample's lobby (`samples/mmo/server.rootfs/lobby.phi`, a user program). It passes when every
connection logged in and quit, and every answer came back, in order. Each machine's output
is saved in `build/NAME.load/`. The clients are PHI kernels rather than connections from
this computer because QEMU's user network on Windows keeps at most 64 connections from
outside (it waits on its sockets with `select`).

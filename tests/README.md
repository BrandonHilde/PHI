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

A test of a compiled library says what to build first with a comment line, `# library:
FILE.phi` (putting `FILE.philib` next to the test) or `# library: FILE.phi FOLDER`, one for each
in the order they're needed; each is built once in a run (see `library_use_program.phi`). The
source goes in `library_src/`, away from the test, so that `use` finds the compiled file.

Each test gets 10 seconds (`--timeout <sec>` changes that), or more if it says so with a
comment line `# takes up to N seconds: why` (see `kernel32_shell.phi`). A failure says
why the run stopped: `timed out after 10s` (with the input script's line, if one was
running), or `QEMU stopped by itself` when the program never reached `exit` or its expected
output, which is usually a triple fault (`phi run --debug` logs interrupts and resets).

A test that times out also says where it was: each CPU (in QEMU) or each thread (a Linux
or Windows program), as the PHI line or library routine it was in and the calls that led
there, found on its stack:

```
FAIL hang_threads (linux)
     output did not match (timed out after 3s)
     where it was when it was stopped:
       thread 1: OS_Sleep (system.asm), in system call 162 (nanosleep)
           called from Threads.Wait, lib/phi/user/threads.phi:48: call OS.Sleep: 1;
           called from HangThreads (class body), hang_threads.phi:11: call Threads.Wait: t;
       thread 2: HangThreads.Waiter, hang_threads.phi:15: locked guard
           called from phi_host_thread_main (threads.asm)
```

A call is a value on the stack that is where one of the program's calls returns to, so
once in a while one a finished call left behind shows up too. On PHI OS only what is on a
CPU at that moment is seen (a program's thread that is waiting isn't), and `(in NAME)`
says which program's code it is. Each build keeps NASM's listings (`build/NAME/*.lst`),
which the addresses are looked up in.

A test that fails runs once more. One that passes then is shown in yellow and listed at the
end: the computer was busy, or the test has a race between threads worth a look.
`--no-retry` runs each test once.

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
  | `tls PORT HOST ROOT TEXT` | connect to the guest's TCP `PORT` with the host's own TLS 1.3 client (.NET's, Windows' SChannel on Windows), check its certificate is for `HOST` and goes back to `ROOT` (a root certificate's DER, in hex), send `TEXT` and a newline and expect them back; QEMU forwards the port from the start (see `tls_host_client`) |
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
Most language tests are written this way, so both code generators stay in step. One too big
for a 16-bit program (a 16-bit program's classes share 32 KB) says so with a comment line,
`# runs only in 32 and 64 bits: why`, and runs as a 32-bit kernel and on the other systems
(see `crypto_rsa.phi`).

Such a test also runs as a Linux and a Windows program (`numbers (linux)`, `numbers
(windows)`), where this computer can run them (Linux under WSL on Windows), so PHI OS and
the other systems give the same output. A test is left to PHI OS when it can't be built for
them (it uses a driver, say) or has an `.input`, `.web`, `.sound`, `.machines` or `.errors`
file. One that builds but needs a PHI OS machine (it writes to the screen's memory) says
so with a comment line, `# runs only on PHI OS: why`. A test that is a `phi.Name:Program`
runs only on the other systems (see `host_program.phi`). `phi test --target linux` (or
`windows`) runs just those, without QEMU, in a few seconds.

A test with `NAME.cdb` is about a `--debug` build's `.pdb`: it runs in Microsoft's debugger, as
a 32-bit and a 64-bit Windows program, where `cdb` is installed. `NAME.cdb` is the commands, and
`NAME.expected` what `cdb` should say from its first `.echo ---- ` on, without the addresses and
registers that change from run to run (see `debugger.phi`). `phi test --debug-builds` builds
every Windows program test as `--debug` does, since such a build compiles differently, and
should still do the same.

They run as 64-bit Linux and Windows programs too (`numbers (linux64)`, `numbers
(windows64)`; `--target linux64`, `--target windows64`), where the generator can build
them: until 64-bit code can do everything (S10 in Plan.md), a test that uses what it can't
yet is left to the other systems. What's meant to differ in 64-bit code, such as `sizeof`
of a pointer, goes in `NAME.linux64.expected` and `NAME.windows64.expected` (any mode can
have one: `NAME.MODE.expected` is used before `NAME.expected`).

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
when frames go missing (see `net_tcp.machines`). Like some clouds' networks, a line `mtu
1400` makes it drop every frame whose IP packet is bigger than that, and a line `dhcp` makes
it answer DHCP itself, giving machine N 10.0.3.N (its `Machine.address`) and the MTU, so
the machines can `Net.Start` by DHCP (see `net_mtu.machines`). The machines are joined
by a switch that learns where each Ethernet address is, so a frame goes only to its
destination (or to everyone, for a broadcast). Every network test runs twice, once with
each network card (`net_tcp (rtl8139)`, `net_tcp (virtio)`), unless its `.machines` file
has a line `card virtio` (or `card rtl8139`) or `phi test` is given `--nic`.

In the same way, a kernel test that uses the disk itself (`use fs.fat16`, `drivers.disk` or
`drivers.block`) runs once on each kind of disk: `kernel32_files (ide)`, `(virtio)` and
`(nvme)`, against the same `.expected` file, unless `phi test` is given `--disk`, which
gives every test that disk (see `disk_block.phi`).

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
phi load samples/mmo/server.phi --connections 500 --seconds 60 --root samples/mmo/certs/root.der --host lobby.mmo.phi.test
phi load samples/mmo/server.phi --connections 100 --profile --root samples/mmo/certs/root.der --host lobby.mmo.phi.test
```

Each connection logs in (`login NAME PASSWORD`, answered `welcome NAME`), says a numbered
line once a second (`say ...`, answered `said ...`) and finally `quit` (answered `bye`),
the protocol of the mmo sample's lobby (`samples/mmo/server.rootfs/lobby.phi`, a user program). With
`--root FILE --host NAME` every connection is TLS 1.3, checking the server's certificate is
for NAME and goes back to the root in FILE (DER); the lobby only takes TLS, with the
sample's certificate (`samples/mmo/certs/`). It passes when every connection's handshake
worked, it logged in and quit, and every answer came back, in order. Each machine's output
is saved in `build/NAME.load/`. The clients are PHI kernels rather than connections from
this computer because QEMU's user network on Windows keeps at most 64 connections from
outside (it waits on its sockets with `select`).

`--client FILE` boots another load-client kernel in place of the lobby's, such as the mmo
world's players:

```
phi load samples/mmo/server.phi --client samples/mmo/load_players.phi --connections 300 --seconds 60
```

It's built with the same `LoadSettings` (`connections`, `seconds`, `port`) and `Machine`
classes, and its last line is `load: NAME COUNT NAME COUNT ...`. `phi load` adds each count
up across the machines (and takes the largest of one whose name starts with `most`), and
the run passes when `problems` comes to 0. `load_players.phi` plays its share of the
players: each joins, steps four times a second, and counts the STATEs it's sent.

**The tick.** A server that writes `tick: N ticks in MS ms, L late, longest X ms, P players`
lines has its tick checked as well, whichever client is used; the mmo world writes one every
10 seconds. Leaving out the first (it has the start in it) and any before the players are
all in, no tick may be late. `phi load` also times when each report arrives here: a machine
that can't keep up can fall behind in its own time too, where its own ms wouldn't show it, so
a report that takes more than 5% longer here than it says fails the run.

```
  tick:       1,000 ticks in 50.0 s (20.0 a second) with 300 players, 0 late, longest 43 ms
  its clock:  50.0 s of the server's time took 50.0 s here
```

The tick's own work is a benchmark, `samples/mmo/bench_tick.phi`: 500 players' moves,
the creatures, the grid, and a STATE for each player, without the network, so the count is
the same on every run.

## Random programs: `phi fuzz code`

`phi fuzz code` checks the code generator with programs nobody wrote. Each is made at random
from what the optimizer works on (ints and u32s in class variables, parameters and variables,
arrays by index and through pointers, math and division by every kind of constant, ifs, whens,
loops in loops, calls), and the fuzzer works out what it prints as it writes it, with 32-bit
math that wraps as PHI's does. It then builds the program for each system this computer runs
(32- and 64-bit Linux here) and compares. A difference is a bug in the compiler, or in the
fuzzer's idea of the language (`Compiler/Phi.Cli/CodeFuzzer.cs`).

```
phi fuzz code                          # 100 programs, from a seed it prints
phi fuzz code --count 3000 --seed 5000 # the same 3000 every time (about 15 a second)
phi fuzz code --target linux64         # one system only
phi fuzz code --count 5 --keep         # keep every program, to read them
```

A program that comes out wrong is kept in `fuzz-failures/` (or `-o`'s folder) with its
`.expected`, and the report says the first line of output that differs. The programs never
do what PHI stops for (an index out of range, dividing by zero, a number that changes on its
way to another type), so they don't test those errors: the tests in this folder do.

Run it after any change to the code generator. When adding an optimisation, break it on
purpose once and see that the fuzzer notices: if it doesn't, its programs don't reach the
new code yet, and `CodeFuzzer.Maker` needs to make some that do.

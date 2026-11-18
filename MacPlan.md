# Moving PHI's development to a Mac

Written 2026-11-17 on the Linux machine, from reading the code, and then carried out on the
Mac. **Where it stands (2026-11-18):** stages 1 to 3 are done (development is on the Mac,
with Linux programs run in a Lima machine by `PHI_LINUX_RUN=lima`), and the ARM target
further down exists as native macOS programs and apps (`--target macos`), merged into `main`
at 7ff7c04: 218 of the 446 tests run as macOS programs and pass, `phi test` is 1052 runs,
and over the benchmarks a macOS program takes 1.13 times clang 21 `-O2`'s time. Stage 4
(real x86 from GitHub's machines) is on hold with the release work: the Linux machine is
still where x86 is timed, and Windows where its programs are run. The stages below are as
they were written, with what was found added; "macOS programs: where they are" is the
running account of the target.

## The short of it

A Mac can do most of the work, but not all of it by itself, and on an Apple Silicon Mac every
x86 instruction is emulated or translated. So the plan is in four stages, and each one is
useful without the next:

1. **The compiler and the kernels** work as soon as the tools are installed (an hour).
2. **Linux programs**, which is where all of PHI's 64-bit code and its optimiser are tested,
   need a small Linux machine on the Mac and one change to the test runner (a day).
3. **PHI OS and the code library** need a few lines of their scripts changed (an hour or two).
4. **Real x86**, for what emulation can't answer (speed, and a failure that might be the
   emulator's), comes from GitHub's machines on every push, or from the Linux machine kept
   as it is.

What the Mac gives that nothing else does: macOS itself, and real ARM for an ARM target.

### What works where, today

| | On the Mac, as the code is | After stage 2 | Needs real x86 |
|---|---|---|---|
| Building the compiler, `dotnet test` (658) | yes | | |
| 16-bit code, kernels, PHI OS programs in QEMU (618 of `phi test`'s 830 runs) | yes | | |
| Linux programs, 32 and 64 bits (212 runs; `phi test --target linux`, 52 s on Linux) | **no** | yes, emulated | to be sure of a failure |
| `phi fuzz`, the code library's tests, PHI OS's `render`, `icons`, `atlas` | **no** (Linux programs) | yes | |
| Windows programs | no | in a Windows VM (stage 4) | to be sure |
| `phi test --bench` (counts instructions in QEMU) | yes, the same counts | | |
| `phi compare` (time against C# and gcc), `phi profile` | no | not worth it: the times would be the translator's | **yes** |
| PHI OS in a window | yes, slower: no KVM for an x86 guest on Apple Silicon | | for its speed |

## Stage 0: on the Linux machine, before leaving it

- [ ] **PHI OS has no remote.** It's committed (`c197c4b`) but only on this disk. Make an empty
      repository for it on GitHub and push:

      ```
      cd ~/prj/PHI-OS
      git remote add origin https://github.com/<you>/PHI-OS
      git push -u origin main
      ```
- [ ] The other three are pushed, with `main` and `linux-bringup` the same commit: PHI,
      PHI-Code-Library, PHI-VSCODE. On the Mac use `main`.
- [ ] **Files that are in no repository**, beside the four folders in `~/prj`: `Assessment.md`,
      `FuturePlan.md`, `NetworkSpeed.md`, `PerformanceStrategy.md` (the compiler's comments point
      at this one) and `test-projects/`. Copy them across (`scp -r`, or a USB stick).
- [ ] **Claude Code's memory of this project** is a folder on this machine, not in git:
      `~/.claude/projects/-home-<you>-prj/memory/`. Copy the folder to the Mac as
      `~/.claude/projects/-Users-<you>-prj/memory/` (the name is the project's path with `-` for
      each `/`), or a new session there starts knowing none of the choices made so far. It has
      notes about you in it: don't put it in a public repository.

## Stage 1: the compiler and the kernels (the Mac alone)

First, which Mac:

```
uname -m          # arm64: Apple Silicon.  x86_64: Intel
```

On an Intel Mac everything below is the same but faster and surer (x86 runs as itself), and
stage 2's Linux machine is an ordinary x86 one.

- [ ] Install [Homebrew](https://brew.sh), then:

      ```
      brew install nasm qemu git mtools imagemagick coreutils bash lima node
      brew install --cask powershell
      ```
- [ ] **.NET 10 SDK.** The projects are for `net10.0`. `brew install --cask dotnet-sdk` gives
      the newest SDK, which by now may be 11: check `dotnet --list-sdks`, and if there's no
      10.x, get 10 from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0).

**As done on 2026-11-17 (macOS 27.2, Apple Silicon, 8 cores, 8 GB), without sudo.** The casks
install `.pkg`s, which want an administrator's password, so instead:

- .NET 10 into `~/.dotnet` with Microsoft's script (`curl -sSfL https://dot.net/v1/dotnet-install.sh
  | bash -s -- --channel 10.0`, 10.0.404). An older .NET in `/usr/local/share/dotnet` (6 and 7
  were there) is where `phi` looks unless told: without `DOTNET_ROOT` it says "You must install
  or update .NET". VS Code needs `DOTNET_ROOT` too, or it can't start the language server.
- PowerShell as a .NET tool: `dotnet tool install --global PowerShell --version 7.6.10` (7.7.0
  has no `osx-arm64` package on NuGet yet).
- Homebrew's folder was last on the `PATH` (after `/bin`), so `#!/usr/bin/env bash` found
  macOS's bash 3.2. In `~/.zshrc`:

      ```
      eval "$(/opt/homebrew/bin/brew shellenv)"
      export DOTNET_ROOT="$HOME/.dotnet"
      export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
      ```
- The four folders are in `~/Documents/prj`, not `~/prj`: anywhere works, side by side.
- [ ] Get the code, all four side by side (the scripts look for `../PHI` and
      `../PHI-Code-Library`):

      ```
      mkdir ~/prj && cd ~/prj
      git clone https://github.com/<you>/PHI
      git clone https://github.com/<you>/PHI-Code-Library
      git clone https://github.com/<you>/PHI-VSCODE
      git clone https://github.com/<you>/PHI-OS
      ```
- [ ] Build and check:

      ```
      cd ~/prj/PHI
      dotnet build PHI.sln
      Compiler/Phi.Cli/bin/Debug/net10.0/phi --version
      dotnet test PHI.sln                                  # 658 pass
      Compiler/Phi.Cli/bin/Debug/net10.0/phi run samples/kernel.phi    # a QEMU window
      Compiler/Phi.Cli/bin/Debug/net10.0/phi test          # the QEMU runs; the Linux ones say why they're skipped
      ```

**What to expect.** `phi` finds `nasm` and `qemu-system-i386` on the `PATH`
(`Compiler/Phi.Cli/Toolchain.cs`; `PHI_NASM` and `PHI_QEMU` name them if it doesn't). It
never asks QEMU for KVM, so the kernels run on a Mac the way they do on Linux: emulated.
About 618 runs should be tried. Things that might differ, each worth a line here when seen:

- Tests that depend on time (the ones that have needed a second try on Linux: `net_dns` on a
  virtio card). `phi test --timeout 120` if a slower machine runs out of time.

  **Seen:** 404 runs, not 618 (`:Program` tests run only as Linux or Windows programs, which
  here wait for stage 2), in 12 minutes, one at a time. 400 passed. The 4 that failed have this
  computer's TLS 1.3 as their other side (`net_http` and `net_web` through `TestWeb.secure_url`,
  `tls_host_client` and `tls_host_p384` through an input script's `tls`), and .NET on macOS has
  none: `SslProtocols.Tls13` is "not supported" for a server or a client, and the Network.framework
  switch (`DOTNET_SYSTEM_NET_SECURITY_USENETWORKFRAMEWORK=1`) gives a client that fails the
  handshake. `phi test` now sets them aside on a Mac and names them; Linux and Windows run them.
- Sound: with a window QEMU is given `coreaudio` (`Qemu.cs` picks it); `PHI_AUDIO=none` if
  it complains.
- The disk doesn't tell `Net.phi` from `net.phi`. A `use` or a file name in the wrong case
  works on the Mac and fails on Linux. No two files clash today (checked in all four
  repositories); stage 4 is what catches a new one.
- `dotnet test` should pass all 658. One that doesn't is a real finding about the compiler
  on macOS (a path, a line ending), not about emulation.

  **Seen, and fixed:** two, neither a path nor a line ending.
  - `TemplateTests.OneThatUsesItselfWithABiggerTypeEachTimeIsStopped` overflowed the stack and
    took the test run down with it (so the totals changed from run to run: 369, 425). After
    `Templates.Expand` gives up at 2000 structs, `Compile` still bound the program for its other
    errors, and laying out a struct in a struct ~2000 deep is a call each: Linux's 8 MB stacks
    hold it, a Mac's thread-pool threads don't. Now nothing binds after the templates give up
    (`ProgramNode.TemplatesGaveUp`). The language server's `bindAnyway` would have crashed too.
  - `InputScriptTests.AMachineThatEndsWithLinesLeftSaysWhichOneItDidntGetTo` passed alone and
    failed in the whole run: the test's pretend QEMU accepted on a pool thread, which, with 8
    cores busy, started after the script had sent every line into the socket's buffer. 22 cores
    hid it. It accepts on a thread of its own now.

## Stage 2: Linux programs on the Mac

This is the stage that decides whether the Mac can be where the work is done. Everything
64-bit PHI makes is a Linux or Windows program, so without it the optimiser, `vec`, the
fuzzers and the code library can't be tested at all.

**Why it's simpler than it sounds:** a PHI program is one file with no C library and nothing
to load, so an emulator for one program at a time (`qemu-user`) runs it with no x86 system
underneath.

- [ ] **A Linux machine on the Mac that sees the Mac's files at the same paths.** Any of
      Lima (`brew install lima`), OrbStack or Docker will do; Lima is free and its `lima`
      command runs one command inside and comes back, which is what the test runner needs:

      ```
      limactl start --name=default --mount-writable template://ubuntu    # check limactl start --help: these flags are from memory
      lima uname -m                       # aarch64 on Apple Silicon
      lima sudo apt install -y qemu-user-static
      ```

      After that a 32-bit or 64-bit x86 program started inside is run by `qemu-user` without
      being asked (Linux's `binfmt_misc`). Try it by hand before changing any code:

      ```
      cd ~/prj/PHI
      Compiler/Phi.Cli/bin/Debug/net10.0/phi build tests/throws_answers_program.phi --target linux64
      lima tests/build/throws_answers_program/throws_answers_program
      Compiler/Phi.Cli/bin/Debug/net10.0/phi build tests/throws_answers_program.phi --target linux
      lima tests/build/throws_answers_program/throws_answers_program
      ```
- [ ] **Teach the test runner to use it.** `Compiler/Phi.Cli/HostRunner.cs` already runs Linux
      programs from Windows through WSL: `CanRun` says whether this computer can, `Start` and
      `RunLinuxScript` put `wsl.exe -e /bin/sh` in front, and `WslPath` turns `C:\a` into
      `/mnt/c/a`. The Mac wants the same with a command of its own and no change of path:

  - an environment variable, say `PHI_LINUX_RUN=lima` (or `orb`, or
    `docker run --rm -v $HOME:$HOME ...`), read once;
  - `CanRun`: a third case, Linux programs `when OperatingSystem.IsMacOS()` and the variable
    is set and `<command> true` works; otherwise "Linux programs run here in a Linux machine:
    set PHI_LINUX_RUN";
  - `Start` and `RunLinuxScript`: the command in place of `wsl.exe -e`, paths as they are;
  - `Where.cs` (where a run that timed out was) reads `/proc` from a script run the same
    way, so it comes along;
  - `Profiler.cs` and `Compare.cs` ask `OperatingSystem.IsLinux()`: leave them (see the
    table).

  It's about 30 lines. Check: `phi test --target linux` gives **212/212**, and `PHI_IR=0`
  the same.
- [ ] Then the things that are Linux programs underneath: `phi fuzz numbers`, `phi fuzz
      code`, `phi fuzz objects`, and stage 3's.

**What to expect from emulation.** A pass is good evidence. A failure has two possible
causes, PHI or the emulator, and the ones most likely to be the emulator's:

- `faults_program` and the others that catch CPU faults (the emulator makes the signal and
  the registers PHI's handler reads);
- threads, `atomic` and `locked` tests (timing, and the emulator's own locking);
- `dialogs_window_program`, `window_*` and the sound tests: they talk to an X server and a
  sound server, and a small Linux machine has neither. They may need a skip of their own
  when there's no `DISPLAY`.

For speed on Apple Silicon, Rosetta can run the 64-bit programs instead of `qemu-user`
(Lima's `--rosetta`); it doesn't run 32-bit ones. Only worth it if 212 runs are slow.

**As done on 2026-11-17** (Lima 2.3, Ubuntu 26.10 arm64):

```
limactl start --name=default --tty=false --cpus 4 --memory 2 --disk 20 \
    --mount-only ~/Documents/prj:w template:ubuntu
lima sudo apt-get install -y qemu-user-binfmt          # 26.10 has no qemu-user-static
lima sudo apt-get install -y --no-install-recommends xvfb pipewire pipewire-pulse wireplumber
```

Only the project's folder is shared, writable, at the same path; `lima` runs a command in the
folder it was started from. The window and sound tests failed as predicted, and don't need a
skip: PHI speaks X11 and PulseAudio's protocol itself, so an X server with no screen and a sound
server with no card are enough. In the machine (`lima`):

```
mkdir -p ~/.config/systemd/user
printf '[Service]\nExecStart=/usr/bin/Xvfb :99 -screen 0 1280x800x24 -nolisten tcp\nRestart=on-failure\n[Install]\nWantedBy=default.target\n' \
    > ~/.config/systemd/user/xvfb.service
sudo loginctl enable-linger $USER
systemctl --user enable --now xvfb pipewire.socket pipewire-pulse.socket wireplumber
echo DISPLAY=:99 | sudo tee -a /etc/environment        # then limactl stop default; limactl start default
```

**PipeWire, not PulseAudio.** With PulseAudio itself (17) `sound_program` fails one line: its
stream never fills (`it fills up: 0`: 4 MB taken, ~45 s of sound). With PipeWire's stand-in,
which Ubuntu's desktop runs, it passes. So PHI's sound against a real PulseAudio server is worth
a look one day; it isn't the emulator.

`HostRunner` as written: `PHI_LINUX_RUN` is split at spaces (`lima`, `orb`, or a `docker run`
line); `CanRun` asks the machine `uname -m` once. A program that runs out of time is killed in
the machine too (by its pid file), since ending `lima` here needn't end it there. Where its
threads were (`Where.FromLinux`) isn't read on an ARM machine: that `/proc` is the emulator's.
`phi compare` refuses to run with `PHI_LINUX_RUN` set.

## Stage 3: PHI OS and the code library

**PHI Code Library** (`pwsh ./build.ps1`, `-Test`): building needs nothing new. Its tests
are Linux programs, so they wait for stage 2. Check: `All 7 test runs passed.`

**Seen:** with `PHI_LINUX_RUN=lima`, 5 of 7 in 21 minutes. The fuzzers (`test_jpeg_fuzz`,
`test_ttf_fuzz`, and `test_png_fuzz` on a second try) ran out of the 60 s `build.ps1` gives a
test: they decode tens of thousands of files, and emulated that's slower. With
`-Timeout 600` all of them pass (image's three fuzzers 3.6 min, font's 2.4, builds included),
so on a Mac: `pwsh ./build.ps1 -Test -Timeout 300`.

They also found a race: Lima's virtiofs remembers for a moment that a name wasn't there, so a
build folder made here a moment before was "Directory nonexistent" to the machine, its pid file
wasn't written (a program out of time couldn't have been killed there) and the shell's
complaint was in the output. `HostRunner` now tries the pid file and the `cd` again, quietly,
for up to 3 s in a Linux machine.

**PHI OS**: its scripts were written on Linux. What to change, by file:

- [ ] `run.sh`, the window: it asks QEMU for `-display gtk` (or `sdl`), and Homebrew's QEMU
      has `cocoa`. Where it sets `DISPLAY_ARGS`, on a Mac (`[ "$(uname)" = Darwin ]`) use
      `-display cocoa` and drop the two `export ..._BACKEND=x11` lines.
- [ ] `run.sh`, speed: it uses KVM when `/dev/kvm` is there and falls back by itself when it
      isn't, so nothing breaks. On an Intel Mac, add `-accel hvf` beside it. On Apple Silicon
      there's nothing to add: an x86 guest is emulated. **PHI OS will feel slower than it did
      with KVM**, the browser most of all.
- [ ] `tools/qemu-headless.sh`, `tools/compare.sh`: they use `timeout`, which macOS doesn't
      have. `brew install coreutils` gives `gtimeout`; either put
      `$(brew --prefix coreutils)/libexec/gnubin` on the `PATH` or use `gtimeout` on a Mac.
- [ ] `tools/compare.sh`: `realpath -m` isn't in macOS's `realpath` (gnubin's has it), and
      Firefox is `/Applications/Firefox.app/Contents/MacOS/firefox`.
- [ ] `tools/mon.py` calls `convert`, ImageMagick's old name; it may only warn, or want
      `magick`.
- [ ] The scripts start with `#!/usr/bin/env bash`, so with Homebrew's `bash` first on the
      `PATH` they get a new one. macOS's own is 3.2, from 2007, and `run.sh` uses arrays in
      ways it may not like.
- [ ] `atlas`, `icons` and `render` are Linux programs (`run.sh` builds and runs them).
      The fonts and the icons they make are in git, so they're only run when a font or a
      drawing changes; when they are, run them through stage 2's command
      (`lima build/icons/icons icons os.rootfs/ICONS/ICONS.ICN`). The icons' file should come
      out the same to the byte as the one in git.

  Check: `./run.sh --build-only`, then `./test.sh` (`1/1 passed`), then `./run.sh` and sign
  in.

  **Seen (2026-11-17):** the script changes above are made (uncommitted in PHI-OS, whose
  repository is set up by hand). Homebrew's QEMU 11 has `cocoa`, `curses` and `dbus` displays,
  no GTK or SDL, so on a Mac `run.sh` (and `run.sh sdl`) opens the Cocoa window; `hvf` is used
  when QEMU lists it (an Intel Mac). `./run.sh --build-only` in 68 s, libraries included;
  `./test.sh` 1/1. Made again in the Linux machine, `ICONS.ICN`, `UI.FNT` and `NEWS.FNT` are
  the same to the byte as the ones in git (NASM 3.02 here). `./run.sh headless` reaches
  "desktop ready", network up, in about 14 s, and `tools/mon.py shot` shows the sign-in screen.

**PHI-VSCODE**: `npm install`, and set `phi.compilerPath` to
`~/prj/PHI/Compiler/Phi.Cli/bin/Debug/net10.0/phi` (`phi.nasmPath` and `phi.qemuPath` too if
VS Code doesn't have Homebrew's folder on its `PATH`).

## Stage 4: real x86, and Windows

The Mac's answers about x86 are an emulator's. Two ways to have real ones, and the first
makes the Linux machine optional:

- [ ] **GitHub Actions, on every push** (free for a public repository). A workflow with
      three jobs:

  | Runner | What it runs |
  |---|---|
  | Linux, x86-64 | `dotnet test`, `phi test`, `PHI_IR=0 phi test`: all 830, as on the Linux machine today |
  | Windows, x86-64 | `dotnet test`, `phi test --target windows`: **the run 0.1.0 is waiting for** |
  | macOS, Apple Silicon | `dotnet test`, `phi test`: what stage 1 does, kept working |

  Each installs the .NET 10 SDK, NASM and QEMU. The runners have about 4 cores against this
  machine's 22, so the full run is more like half an hour than 8 minutes: fine for a push,
  not for the loop of changing and trying, which stays local.
- [ ] **Windows on the Mac**, if a look by hand is wanted: a Windows 11 ARM machine (UTM is
      free; Parallels and VMware Fusion also do it). Windows there runs 32-bit and 64-bit
      x86 programs by its own emulation. Clone PHI inside it and run
      `phi test --target windows`. Don't run the kernel tests there: that's QEMU inside an
      emulator.
- [ ] **Keep the Linux machine** for the two things nothing above does: `phi compare` and
      `bench/RESULTS.md` (times against C# and gcc mean something only on a real x86 CPU),
      and PHI OS with KVM.

## An ARM target, since it's why a Mac is interesting

Not part of moving, but it changes what the Mac is for. Today the Mac emulates everything PHI
makes. With an ARM64 back end (a translator beside `CodeGen/X86Generator.Ir.cs`, an assembler for
it since NASM is x86 only, the runtime's helper routines, the library's 42 assembly blocks,
and Linux's ARM system calls), stage 2's Linux machine would run PHI's programs **as
themselves, at full speed**, and the Mac would be the real hardware for that target the way
the Linux machine is for x86.

**The assembler (decided 2026-11-17): an established one first.** PHI writes ARM64 assembly as
text for an existing assembler, as it writes NASM's today: LLVM's `llvm-mc -triple
aarch64-linux-gnu -filetype=obj` and `ld.lld` (`brew install llvm`), or GNU `as` for aarch64
(`binutils-aarch64-linux-gnu`, in the Linux machine). PHI's own assembler, **PHI-Assembler**,
waits until ARM64 programs work end to end with those. (macOS's own `as` assembles arm64 too,
but makes Mach-O objects: for macOS programs, not Linux ones.)

macOS programs are a further step: another file format, a
signature every program must carry, and Apple's system library in place of system calls.
(That step was taken first, since the Mac runs them itself: the next section. ARM Linux
programs, and PHI-Assembler, wait; the user's reason for PHI-Assembler is self-hosting on
PHI OS, on hold until a release is out.)

### macOS programs: where they are (`phi build --target macos`)

Native arm64 Mach-O programs, assembled and linked by Apple's clang (`CodeGen/A64/`, the
runtime in `lib/arm64_macos/`). Every method and every class's statements go through the
intermediate form; what it or the ARM emitter doesn't do yet is the error "... which macOS
programs can't do yet", so `phi test --target macos` runs exactly what builds
(`PHI_TEST_WHY=1` says why each other test doesn't).

- **M1** (e94e711): ints, branches, calls, log, exit, line marks, uncaught faults: 11 tests.
- **M2**: i64s, f64s and f32s (sin, cos and fmod from libm; x86's results kept: a too-big
  float made a whole number is the smallest i64, fsin of 2^63 or more is x), index checks,
  division by a number known only when it runs (by zero, or the smallest number by -1, is
  "division by zero", as x86's CPU says), text routines, `when` tables, the heap (OS.Grow
  reserves 16 GB of address space and makes 16 KB pages memory as the heap grows by 4 KB
  ones), the log's lock, atomics (ARMv8.1's), `arm.` assembly blocks (the binder picks them
  for macOS; `mem.phi` and `program_heap.phi` have theirs), and `if call ... throws`:
  64 tests, all passing. A macOS program without its own `NAME.macos.expected` is held to
  `NAME.linux64.expected`.
- **M3**: objects, interfaces, text and arrays that grow, and the frees a throw does on its
  way past. Each struct's routines (free, clone, assign, set, renew, equal, and an array's)
  are functions in the intermediate form (`A64Generator.Objects.cs`), compiled like the
  program's methods; a struct with interfaces keeps its type table's 8-byte address before
  each object. `objects.s` has room in a growing str, a store into one, and arrays' growth.
  The program's texts are in writable data, as x86's are. 143 tests, all passing;
  `phi fuzz objects` passes on the standard heap (the checking heap waits for M6's outside
  calls: it maps its own pages).
- **M6**: outside calls. A `:MacOS` module calls libSystem's (or a framework's) functions as
  a `:Windows` module calls kernel32's: `use 'libSystem.B.dylib' as LibC;` and
  `LibC.read: int fd ptr<u8> buffer u64 count: i64;`, as C calls them (x0-x7, d0-d7, the
  rest on the stack); `LibC.Absolute = 'labs': ...;` gives one a PHI name of its own, and
  `...` starts C's variadic part (on the stack, as Apple's convention has it). i64s, u64s,
  f64s and f32s go to and from them. Frameworks and other libraries are linked by their
  names (-framework AppKit, -lobjc). The host modules: `host/macos.phi` (libSystem's
  functions, stat's and dirent's layouts, measured with clang), `time_macos`, `files_macos`,
  `environment_macos`, `stream_macos`; `ask`; the checking heap's macOS version.
- **M7**: threads are pthreads, on a stack of the size asked for, each with a 64 KB spare
  stack for its faults (`threads.s`; the new built-in OS.ThreadCreate starts one). The
  memory model: what a program shares between threads it shares through locks, atomic
  steps and a thread's start and end, which are acquire and release on ARM; plain
  variables written by one thread and read by another without those aren't promised to
  be seen in order (a finished thread's work is: `Finished` reads with an atomic step).
  Each thread test passes 20 times running, without a retry. 163 tests, all passing.
- **M8**: the network (`host/net_macos.phi`): BSD sockets through libSystem, with what
  differs from Linux's (sin_len, fcntl for non-blocking, SO_NOSIGPIPE, errno through
  `__error`); DNS, sockets and network errors pass.
- **M9, first part**: `arm.` versions of the library's other assembly blocks: crypto's
  bignum and x25519 (so TLS 1.3 works), random (the system's arc4random in place of RDRAND,
  the CPU's counter as the timestamp), the stopwatch's and bench's counter (cntvct_el0),
  and drawing's rows. 184 tests, all passing. NEON for vecs comes after M10.
- **M10**: windows. `host/window_macos.phi` answers gui.app's calls with AppKit, through
  the Objective-C runtime (objc_msgSend, declared once for each shape of call): an NSWindow
  per PHI window, the program's pixels copied into a CGImage that the window's view shows
  (stretched while it's resized), the program's own event loop (NSApp's events: keys,
  held keys, the mouse, double clicks, the wheel), a new size told once the user stops
  dragging, closing a window (its delegate, made at run time in `appkit.s`) and Command-Q
  as CLOSE, and the font drawn by Core Text from Menlo. `window_program` passes, with
  `support/mac_window.phi` playing the user with NSEvents. **`phi build --target macos --app`**
  makes NAME.app (Info.plist with the id org.phi.NAME, signed ad hoc), which the Finder
  opens: the desktop samples' clock, notes, form and sketch build as apps. 186 tests.
- **M11, sound**: `host/sound_macos.phi`, an AudioQueue with a ring of buffers as the
  Windows module has, the queue's thread marking a played buffer through `audio.s`.
  `sound_program` passes, and the desktop's paddles game builds as an app. Also: gui.dialogs,
  several windows, locks in structs, and more of a class's own statements. 197 tests.
- **M11, debugging**: `phi build --target macos --debug` writes NAME.dSYM: each line's
  code has a `.loc`, and a DWARF compile unit with a subprogram for each method (dsymutil
  keeps only what it can match to the program's symbols). `atos` gives a PHI line for an
  address. This Mac's lldb (Command Line Tools 14 on a newer macOS) crashes on any program,
  a C one too, so stepping through PHI lines waits for a newer Xcode or CLT.
- **M9, NEON**: vecs in q registers (`A64Generator.Vec.cs`), giving x86's results where SSE2
  and NEON differ (min and max with not-a-number, float-to-int conversions out of range,
  sums lane by lane in order, shifts of a lane's width or more), and the loop vectoriser on
  for ARM. The vec tests (the generated lane-by-lane ones too) and the three fuzzers pass.
  Found with it: some emitter code used x10-x15 (registers the allocator hands out) as
  temporaries; it uses x0-x9 and x16-x17 now. A str array's element given to a writable str
  parameter (a slot of the call's, put back after). 201 tests.
- **Compiled libraries**: `phi build --library` builds a macOS target too (on a Mac): the
  library's code is an object file of Apple's clang, kept whole in the .philib, which the
  program's link puts beside its own (`A64Generator.Library.cs`). Its classes' start, methods,
  variables and line marks are global; what it leaves to the program (modules, the runtime,
  type tables) the program gives, its labels all global when it has libraries. An error in a
  library's code has its line, from the library's marks put after the program's. `if x is
  Struct name` for ARM too. The 7 library tests pass. 208 tests.
- **The long tail**: f16 (kept as an f64 and rounded as it's set or written, to an f32 and
  then to an f16 half up as x86's float.asm rounds, `lib/arm64_macos/half.s`); has on an
  array parameter, and one array parameter given another; `++` on an `[atmost N]` str array
  (a table of texts, or strs with a size in place); a class's str array given texts worked
  out as it runs; while each over a str in a class's own statements, and over a str array's
  element; a class's `[atmost N]` array of objects of any size. Every test and sample that is
  a program now builds for macOS but PHI OS's own (the rootfs programs, which make PHI OS's
  system calls). `phi compare` on a Mac times PHI as a macOS program. 215 tests.
- **Speed** (`phi compare` on this Mac, PHI as a macOS program, beside Apple clang -O2 and
  -O0 and C#; C built with `-ffp-contract=off`, since clang on ARM fuses a multiply and an add
  and mandelbrot's last digit came out otherwise): PHI takes 1.2 times clang -O2's time over
  all the benchmarks (x86 on the Linux machine: 1.17 times gcc's), about C#'s. What it took:
  a method's own str that grows remembers its length on ARM too (strings went from 23 s to
  0.6 s: each append had measured the whole text), an element read and written in one
  instruction (`[base, index, lsl #n]`), and a field's offset folded into its load. (Kept
  registers saved and read back two at a time, stp and ldp, made fib a quarter slower and
  nothing faster: they're one at a time.)
- **Speed, second pass: 1.13 times clang -O2's** (from 1.21). What it took, each for ARM only
  (x86's code is the same, by the snapshot):
  - a method's loop keeps the class variables it changes in registers, by x86's rules
    (`X86Generator.ReachedByAddress`, shared): interp 1.68 -> 1.23. Not a class's own
    statements' loops, which x86 does another way with more rules (7 tests failed with it);
  - a class's variable read or written in two instructions, not three
    (`adrp x16, v@PAGE` then `ldr w0, [x16, v@PAGEOFF]`; every variable starts at a multiple
    of 8, which the linker needs): crc 1.01 -> 0.90, sort 1.10 -> 1.00;
  - of the registers every method keeps, one already saved is taken before another (the
    free list gave each short value a new one: Map's FindHashed saved 8, now 2), and a
    result may have the register of an operand read there for the last time;
  - a value made by one instruction and read only by the next, or by the return, is made in
    x0 (`ThroughX0`): fib 1.47 -> 1.41;
  - and, orr, eor and tst take a mask as it is; an index made 64 bits goes into the read
    itself (`[x1, w2, uxtw #2]`).
  (Against clang 21, Command Line Tools 27.2, installed 2026-11-18, the same PHI code is
  1.17 times: the newer clang made the C faster in interp, mandelbrot and maps. The figures
  here were measured with clang 14. lldb from the same tools steps through a `--debug`
  program's PHI lines; lldb 14 crashed on any program.)
- **Speed, third pass (beside clang 21): 1.15 times, from 1.17.** A class's own statements'
  loops keep class variables in registers too (a variable declared in such a loop was set in
  memory while its register was the one read: the 7 failures). And a small method is put in
  where it's called (`A64Generator.Inline.cs`): one of at most 48 instructions that keeps
  nothing in its frame and can't stop with an error naming a line, up to three deep, never a
  compiled library's in the program using it; its lines stay its own file's, so a fault in
  it names the line it's on. maps 2.94 -> 2.46. ARM only; x86's code is the same.
- **Speed, fourth pass (beside clang 21): 1.13 times.** A method that may stop with an error
  is put in where it's called too (the emitter names the file of the line it's writing, so
  the error names its own line and file: tests/inline_errors_program): maps 2.46 -> 2.0. A
  short if and else that set one variable is a choice with no jump (`SelectI`, csel and
  csinc; `Selects`): collatz 1.30 -> 1.00. Tried and left out: an if alone as a choice (a
  new best found now and then is a branch the CPU guesses; collatz was a tenth slower with
  it). A method that returns at once one way saves its kept registers only on the other
  (`SplitEntry`): fib 1.43 -> 1.42, hardly anything, kept since it costs a copy.
  Furthest behind now: maps (2.0; the C one keeps key, value and used in one 12-byte slot and
  is all in one function, PHI's Map is three arrays and four calls a lookup), fib (1.4),
  collatz and lists (1.3). What would take them further: a choice without a branch (csel)
  for collatz, the kept registers not saved on a path that returns at once for fib, small
  methods put in where they're called for maps.
- **ABI v2 (a method's arguments in registers): not now.** Measured, calls aren't where ARM
  is slow: fib, nearly all calls, is 1.5 times clang's (x86: 3.2 times gcc's, which turns its
  recursion into a loop), and the benchmarks furthest behind are slow inside their methods.
  An argument in a register would save a store and a load a call, which the M1 forwards; and
  it would change every routine the runtime and the generator make, and every compiled
  library's ABI. (After the second pass above fib is 1.4 times clang's.)
- Found on the way in x86's methods the intermediate form builds, and fixed there too
  (tests/ir_checks_program): an `[atmost N]` array's index was checked against N, not how many
  it holds; `Mem.Copy` (Move, Fill, Fill32) didn't check its count;
  and a method's object result kept in an interface (`call shape is Make: 3`) freed what the
  interface had with the result's struct's free, not the interface's. This changed x86's code
  on purpose (237 of the snapshot's 5403 builds, 31 files); the snapshot was recorded again.

### PHI-Assembler for ARM: what it would take

Everything works with Apple's clang (assembling) and ld64 (linking and signing), as decided.
What PHI's own assembler would have to do, measured on the macOS tests' programs:

- **Instructions**: 146 mnemonics, most of them in a few forms: the general ones (mov, add,
  ldr/str and their byte, half, unscaled and pair forms, adrp, cmp, b.cond, bl, ret, csel,
  tbz/cbz, mul/div), the atomics (casa, swpa, ldaddal, stlr), the floats (fadd ... fcvt*,
  scvtf), and NEON's (about 50, each in a lane size or two). An encoder is a table of these:
  A64's encodings are regular, 32 bits each, the hard parts being bitmask immediates (for
  and/orr/eor/tst) and which offsets a load takes scaled.
- **Directives**: .section, .p2align, .globl, .quad/.long/.short/.byte, .double, .space,
  .asciz, .zerofill, .set; and `.file`/`.loc` for --debug (DWARF line rows, which PHI would
  then write itself).
- **Relocations**: PAGE21 and PAGEOFF12 (adrp/add and the loads after it: 66,000 of them in
  the tests), BRANCH26 (bl to another file's symbol, libSystem's), GOT_LOAD_PAGE21/PAGEOFF12
  (213: a symbol of a dylib), and UNSIGNED 64-bit (.quad label: the line table, type
  tables). Within one file most branches and adrp pairs resolve at assembly time.
- **The object file**: a Mach-O MH_OBJECT with __TEXT,__text, __DATA,__data/__bss, a symbol
  table and the relocations: about what ElfObject.cs reads today, written instead.

Linking would stay ld64's: a macOS program needs dyld's load commands, chained fixups for
its pointers, libSystem bound lazily, a code signature (ad hoc, but every arm64 program must
have one) and, for --debug, what dsymutil reads. Writing a linker is a project of its own
with nothing to gain on a Mac, where ld64 is always there with clang.

So PHI-Assembler would replace `clang -c`: an encoder and a Mach-O object writer, checked by
assembling every test's program both ways and comparing the bytes (`otool -t`) section by
section, until they're the same. On a Mac it wouldn't free PHI from Apple's tools (ld64 comes
with the same Command Line Tools as clang), so what it's for is elsewhere: an ARM assembler
PHI OS could carry, or a PHI that assembles its own code everywhere, as it would need to for
an ARM PHI OS. That's the choice to make before it's started.

## Order, and where to stop

| After | You can |
|---|---|
| Stage 0 and 1 | change the language, the binder, kernels and the library, and test all of it but 64-bit code |
| Stage 2 | do everything but time things: this is "development has moved" |
| Stage 3 | work on PHI OS and the web engine |
| Stage 4 | stop depending on the Linux machine for anything but speed |

If stage 2 goes badly (the emulator fails tests PHI passes on Linux, and too many to keep
apart), the fallback is to keep writing on the Mac and let stage 4's Linux job be the test
run, at the cost of a push for each try.

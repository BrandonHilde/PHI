# Moving PHI's development to a Mac

Written 2026-11-17 on the Linux machine, from reading the code. **None of it has been run on
a Mac.** Each step says how to check it; where a step turns out wrong, fix this file.

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
makes. With an ARM64 back end (a translator beside `CodeGen/X86Generator.Ir.cs`, an encoder of
PHI's own since NASM is x86 only, the runtime's helper routines, the library's 42 assembly
blocks, and Linux's ARM system calls), stage 2's Linux machine would run PHI's programs **as
themselves, at full speed**, and the Mac would be the real hardware for that target the way
the Linux machine is for x86. macOS programs are a further step: another file format, a
signature every program must carry, and Apple's system library in place of system calls.

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

# 8. Add a command to PHI OS

PHI OS ([samples/os.phi](../../samples/os.phi)) is a kernel that starts one program, the
shell, and restarts it if it exits. Everything you type at the `phi>` prompt is handled by
programs in [samples/os.rootfs](../../samples/os.rootfs):

- [shell.phi](../../samples/os.rootfs/shell.phi) reads a line, takes the first word as a command,
  and runs `COMMAND.BIN` with the rest of the line as its command line. A trailing `&` runs it
  without waiting.
- Each command (`ls`, `cat`, `echo`, `ps`, `kill`, `mem`, `uptime`, `clear`, `reboot`, `pong`)
  is its own program.

So adding a command means adding a program. Here is how `wc` was added: it counts the lines,
words and bytes in a file.

## 1. Write the program

[samples/os.rootfs/wc.phi](../../samples/os.rootfs/wc.phi):

```phi
# wc FILE: count the lines, words and bytes in a file
use user.text;

phi.Wc:Program
{
	u8 contents[16384];
	ptr<u8> name: 0;
	call name is OS.Arguments;

	int size: 0;
	call size is OS.ReadFile: name addr contents 16384;
	if size < 0
		log 'wc: no such file: ' name '\n';
		exit 1;
	;;

	int lines: 0;
	int words: 0;
	bln inWord: false;
	while int i: 0; i < size; i++;
		u8 c: contents:i;
		if c is '\n'
			lines++;
		;;
		bln space: c is ' ' or c is '\n' or c is '\r' or c is '\t';
		if space
			inWord is false;
		;;
		elif inWord is false
			inWord is true;
			words++;
		;;
	;;

	log lines ' lines, ' words ' words, ' size ' bytes\n';
}
```

**`OS.Arguments`** is everything after the command's name: for `wc readme.txt`, it's
`readme.txt`.

**`OS.ReadFile`** is the program version of `Fat.ReadFile`: a system call, because a program
can't reach the disk itself. The kernel checks that `contents` really is this program's
memory before writing into it.

**`u8 contents[16384];`** is part of the program's image, so it's simply there; programs
don't have a heap (yet).

**`exit 1;`** tells the shell something went wrong; it prints `[exit code 1]`.

## 2. Build and try it

Nothing to register: the file being in the folder is enough.

```
phi run samples/os.phi
phi> wc readme.txt
4 lines, 25 words, 150 bytes
```

`phi build` compiled `wc.phi` into `WC.BIN` on the disk, and the shell found it by name.

## 3. Tell people about it

The shell's `help` command is a list of `log` lines in `shell.phi`; `wc` has one there.

## 4. Test it

[tests/kernel32_shell.phi](../../tests/kernel32_shell.phi) boots PHI OS without a window,
types commands into it, and checks the output. Its input script,
[kernel32_shell.input](../../tests/kernel32_shell.input), has:

```
type wc readme.txt
sendkey ret
expect bytes
```

and [kernel32_shell.contains](../../tests/kernel32_shell.contains) the line the output must
include:

```
4 lines, 25 words, 150 bytes
```

Run it with `phi test -f shell`.

## Ideas for more commands

- **`rm FILE`**: `OS.DeleteFile: name`
- **`write FILE TEXT`**: split the arguments with `Text.CopyWord` and `Text.AfterWord`
  ([user.text](../../lib/phi/user/text.phi)), then `OS.WriteFile`
- **`sleep N`**: `Text.ToNumber` and `OS.Sleep`
- **`top`**: `ps` in a loop, with `OS.ClearScreen` between rounds
- **a game**: [pong.phi](../../samples/os.rootfs/pong.phi) draws with `OS.PutCell`, reads keys
  with `OS.IsKeyDown`, and quits on `q`

## Where to go from here

You've now seen every layer: a boot sector, a protected-mode kernel, drivers, interrupts,
memory, a file system, processes and a shell. To change PHI itself (the compiler, the
standard library, or the build), read [architecture.md](../architecture.md) and
[CONTRIBUTING.md](../../CONTRIBUTING.md). The [plan](../../Plan.md) ends with a list of
what's worth building next.

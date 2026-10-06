# 7. Programs and processes

So far all your code has been the kernel, with full power over the machine. Real operating
systems run other code, **programs**, with much less: each in its own memory, unable to
touch the hardware or each other, asking the kernel for everything. If a program crashes,
the kernel survives.

## A program

A program is a class with the base `Program`. Put it in your kernel's rootfs folder, and
`phi build` compiles it into a `.BIN` file on the disk.
[examples/07-programs.rootfs/greet.phi](../examples/07-programs.rootfs/greet.phi):

```phi
phi.Greet:Program
{
	int me: 0;
	call me is OS.CurrentTask;
	ptr<u8> who: 0;
	call who is OS.Arguments;
	log 'Hello, ' who ', from program ' me '\n';
	exit me;
}
```

and [ticker.phi](../examples/07-programs.rootfs/ticker.phi), which takes a while:

```phi
phi.Ticker:Program
{
	while int i: 1; i <= 3; i++;
		log 'tick ' i '\n';
		call OS.Sleep: 100;
	;;
}
```

## The kernel that runs them

[examples/07-programs.phi](../examples/07-programs.phi):

```phi
use proc.process;

phi.Launcher:Kernel
{
	int greet: 0;
	call greet is Process.Spawn: 'GREET.BIN' 'world';
	int code: 0;
	call code is Process.Wait: greet;
	log 'greet exited with ' code '\n';

	int ticker: 0;
	call ticker is Process.Spawn: 'TICKER.BIN';
	call Process.WaitAll;
	log 'all programs finished\n';
	exit 0;
}
```

```
Hello, world, from program 1
greet exited with 1
tick 1
tick 2
tick 3
all programs finished
```

**`Process.Spawn: path arguments`** loads the program from the disk and starts it, with an
optional command line it reads with `OS.Arguments`. It returns the new **process number**.
**`Process.Wait`** waits for it to finish and returns its exit code. **`Process.WaitAll`**
waits for everything.

## What a program can and can't do

A program runs in the CPU's **user mode** (ring 3). Its code and data are at `0x40000000`,
in memory of its own; the kernel's memory is there too, but the CPU won't let user mode
touch it. Ports, interrupt handlers and drivers are compile errors in a program.

Instead, a program asks the kernel through **system calls**. In PHI they look like ordinary
statements and built-ins: `log`, `ask` and `exit` work as usual, and functions like
`OS.Sleep`, `OS.ReadFile`, `OS.Spawn` and `OS.ClearScreen` each make a system call. The
[language reference](../language.md#programs-and-processes) lists them all.

Underneath, a system call is the instruction `int 0x80` with a number in `eax`. The CPU
switches to the kernel, which checks the request (every pointer a program passes must point
into that program's own memory) and does the work.

## Taking turns

The kernel starts both programs, but there's only one CPU. The timer interrupts 1000 times
a second, and each time it lets the next program run: **multitasking**. A program that
sleeps or waits for a key gives up its turn until it's ready.

The kernel's own code takes turns differently: it runs until it waits (`Process.Wait`,
`Process.WaitAll`, `OS.Yield`) or its main code ends. Only then do programs run. This keeps
kernel code simple, because two pieces of it never run at the same time.

## Crashes stay contained

Put this in the rootfs as `crash.phi`:

```phi
phi.Crash:Program
{
	ptr<u32> kernel: 0x100000;
	kernel:0 is 1;          # kernel memory: not allowed
}
```

`Process.Spawn: 'CRASH.BIN'` starts it, and the kernel prints something like

```
PHI: program 1 stopped by exception 14 at 0x40000019, address 0x00100000
```

Exception 14 is a page fault, `0x40000019` is the program's instruction that caused it, and
`0x00100000` is the address it tried to write.

and carries on. `Process.Wait` returns -1 for it.

## Try this

1. Start `TICKER.BIN` three times at once and watch the ticks interleave.
2. Write a program that asks your name with `ask` and greets you.
3. Write a program that starts another with `OS.Spawn` and waits for it with `OS.Wait`.

Next: [8. Add a command to PHI OS](08-shell-command.md)

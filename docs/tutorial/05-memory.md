# 5. Memory

Every variable so far had a fixed place, decided when the program was built. Real systems
also need memory on demand: a new task, a buffer for a file, a list that grows. This part
covers **structs**, **pointers** and the **heap**.

## The program

[examples/05-memory.phi](../examples/05-memory.phi) builds a linked list of tasks:

```phi
struct.Task
{
	u32 id;
	u8 priority;
	ptr<Task> next;
}

phi.Tasks:Kernel
{
	# build a linked list with new
	ptr<Task> first: 0;
	while u32 i: 1; i <= 3; i++;
		ptr<Task> t: new Task;          # zeroed memory from the heap
		t:0.id is i;
		t:0.priority is 10 * i;
		t:0.next is first;
		first is t;
	;;

	# walk it
	ptr<Task> walk: first;
	while walk is not 0
		log 'task ' walk:0.id ' has priority ' walk:0.priority '\n';
		walk is walk:0.next;
	;;

	# give the memory back
	while first is not 0
		ptr<Task> rest: first:0.next;
		free first;
		first is rest;
	;;
	log 'blocks still in use: ' Heap.used_blocks '\n';
	exit 0;
}
```

It prints the three tasks, newest first, and then `blocks still in use: 0`.

## Structs

`struct.Task { ... }` describes a block of memory with named fields. Fields are laid out one
after another with no gaps, exactly as written, which is what hardware expects when a
struct describes something like a disk's partition table. Fields can be numbers, pointers,
fixed arrays (`u8 name[16];`) or other structs.

A struct variable (`Task current;`) starts as all zeros, and its fields are `current.id`.
An array of structs (`Task tasks[8];`) has `tasks:i.id`.

## Pointers

`ptr<Task>` holds the address of a `Task`. `t:0` is the `Task` it points at, so
`t:0.priority` is that task's priority. `t:1` would be the next `Task` in memory, as if
`t` pointed at an array.

`addr x` is the address of a variable, element or field, as a pointer to its type:

```phi
u8 buffer[64];
ptr<u8> p: addr buffer;
p:0 is 'A';
```

Adding to a pointer moves it by **bytes** (`p++ 4` skips four bytes), whatever it points at.

A `ptr<u8>` is also how PHI passes text around: a `str` can be used wherever a `ptr<u8>` is
expected, and `log` prints a `ptr<u8>` as the text it points at.

## new and free

`new Task` takes a `Task`-sized block from the **heap**, fills it with zeros and returns a
pointer to it. `new u8[512]` takes 512 bytes. `free t` gives a block back. `new` returns 0
if the heap is full.

A kernel that uses `new` gets the memory library automatically. When it starts, the library:

1. reads the memory map the loader collected and keeps a bitmap of free 4 KB pages
   ([memory.frames](../../lib/phi/memory/frames.phi));
2. turns on **paging** ([memory.paging](../../lib/phi/memory/paging.phi));
3. sets up an 8 MB heap ([memory.heap](../../lib/phi/memory/heap.phi)).

All three are written in PHI; reading them is a good way to see how an operating system
manages memory.

## Paging catches mistakes

With paging on, the CPU translates every address through tables the kernel controls. PHI
maps each address to the same physical address, but leaves out the first page and
everything past the end of RAM. So using a **null pointer**, or one that points nowhere, is
a *page fault* on the panic screen, with the address that was used:

```phi
ptr<Task> nothing: 0;
log nothing:0.id;
```

```
 Page fault (exception 14)
 ...
 address: 0x00000000
```

Uncomment the last two lines of the example to try it.

## Growable lists

`use memory.list;` adds a list of 32-bit values that grows as you add to it:

```phi
ptr<ListData> numbers: 0;
call numbers is List.New;
call List.Add: numbers 42;
u32 value: 0;
call value is List.Get: numbers 0;
call List.Delete: numbers;
```

## Try this

1. Add a `str`-like name to `Task` with `u8 name[16];` and set it with `t:0.name:0 is 'A';`.
2. Allocate a big buffer with `new u8[1000000]` and check that `Heap.used_bytes` went up.
3. Free the same pointer twice and read the message you get.

Next: [6. Files on disk](06-files.md)

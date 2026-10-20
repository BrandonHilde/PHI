# 5. Memory

Every variable so far had a fixed place, decided when the program was built. Real systems
also need memory on demand: a new task, a buffer for a file, a list that grows. This part
covers **structs**, **objects**, **pointers** and the **heap**.

## The program

[examples/05-memory.phi](../examples/05-memory.phi) builds a linked list of tasks:

```phi
struct.Task
{
	u32 id;
	u8 priority;
	new Task next;              # the task after this one, which this one owns
}

phi.Tasks:Kernel
{
	call Run;
	log 'blocks still in use: ' Heap.used_blocks '\n';
	exit 0;

	[Run]
		# build a linked list: each new task takes the list so far as its next
		new Task first: empty;
		while u32 i: 1; i <= 3; i++;
			new Task t: i 10 * i;   # zeroed memory from the heap, then id and priority
			t.next takes first;
			first takes t;
		;;

		# walk it with a pointer, which borrows the tasks without copying them
		ptr<Task> walk: addr first;
		while walk is not 0
			log 'task ' walk:0.id ' has priority ' walk:0.priority '\n';
			walk is walk:0.next;
		;;

		# a copy is a whole new list
		new Task copy: first;
		copy.priority is 99;
		log 'first still has priority ' first.priority ', the copy ' copy.priority '\n';
		log 'blocks while running: ' Heap.used_blocks '\n';
	[end]                       # first, copy and all their tasks are freed here
}
```

It prints the three tasks, newest first, then that the copy is separate, that six blocks
of memory are in use while `Run` runs, and `blocks still in use: 0` once it has ended.

## Structs

`struct.Task { ... }` describes a block of memory with named fields. Fields are laid out one
after another with no gaps, exactly as written, which is what hardware expects when a
struct describes something like a disk's partition table. Fields can be numbers, pointers,
fixed arrays (`u8 name[16];`), other structs, or objects (`new Task next;`).

A struct variable (`Task current;`) starts as all zeros, and its fields are `current.id`.
An array of structs (`Task tasks[8];`) has `tasks:i.id`.

## Objects

`new Task t: i 10 * i;` asks the **heap** for a `Task`-sized block of memory, fills it with
zeros, and then sets the fields in order from the values. `t` *owns* that memory: when `Run`
ends, PHI gives it back. There is no `free` to forget.

Every object has exactly one owner, which is what lets PHI know when to free it:

- **`is` copies.** `new Task copy: first;` makes a whole new list with the same values.
  Changing `copy` leaves `first` alone. (`if copy is first` compares the values.)
- **`takes` hands an object over.** `t.next takes first;` moves the list into `t.next`
  without copying it, and leaves `first` empty. `first takes t;` then makes the new task
  the head of the list.
- **`empty` means no object.** A `new` field starts empty, and `new Task first: empty;`
  starts a variable with none. `if t.next is empty` checks.

When an owner is freed, so is everything it owns: freeing `first` frees the whole list.

A kernel that uses objects gets the memory library automatically. When it starts, the
library:

1. reads the memory map the loader collected and keeps a bitmap of free 4 KB pages
   ([memory.frames](../../lib/phi/memory/frames.phi));
2. turns on **paging** ([memory.paging](../../lib/phi/memory/paging.phi));
3. sets up a heap of a quarter of free memory, at least 8 MB ([memory.heap](../../lib/phi/memory/heap.phi)).

All three are written in PHI; reading them is a good way to see how an operating system
manages memory.

## Pointers

`ptr<Task>` holds the address of a `Task`. `addr first` is the address of `first`'s task, so
`walk:0` is that `Task` and `walk:0.priority` is its priority. `walk:1` would be the next
`Task` in memory, as if `walk` pointed at an array.

A pointer doesn't own anything, so walking the list with one copies nothing, and it's only
good while the list's owner is alive. `addr` works on any variable, element or field:

```phi
u8 buffer[64];
ptr<u8> p: addr buffer;
p:0 is 'A';
```

Adding to a pointer moves it by **bytes** (`p++ 4` skips four bytes), whatever it points at.

A `ptr<u8>` is also how PHI passes text around: a `str` can be used wherever a `ptr<u8>` is
expected, and `log` prints a `ptr<u8>` as the text it points at.

## Managing memory yourself

`new` can also be used as a value. It gives a plain pointer that PHI doesn't track, and
`free` gives the memory back:

```phi
ptr<u8> buffer: new u8[512];    # 512 bytes, all zeros
buffer:0 is 1;
free buffer;
```

This is for raw buffers and low-level code. `new` returns 0 if the heap is full, and freeing
the same pointer twice stops the kernel with a message.

## Paging catches mistakes

With paging on, the CPU translates every address through tables the kernel controls. PHI
maps each address to the same physical address, but leaves out the first page and
everything past the end of RAM. So using a **null pointer**, an empty object's fields, or a
pointer that points nowhere is a *page fault* on the panic screen, with the address that was
used:

```phi
ptr<Task> nothing: 0;
log nothing:0.id;
```

```
 Page fault (exception 14)
 ...
 address: 0x00000000
```

Uncomment the two lines near the top of the example to try it.

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

1. Add a name to `Task` with `u8 name[16];` and set it with `t.name:0 is 'A';`.
2. Write a method `[Count: Task list;]` that walks a list with a pointer and returns how
   many tasks it has. Passing `first` to it borrows the list; nothing is copied.
3. Change `t.next takes first;` to `t.next is first;`. The output is the same, but each
   round now copies the whole list into the new task (and frees the old one) instead of
   moving it: fine for three tasks, slow for ten thousand.

Next: [6. Files on disk](06-files.md)

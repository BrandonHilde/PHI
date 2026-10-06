# 6. Files on disk

A 32-bit kernel's disk image has room for files: `phi build` adds a 16 MB **FAT16**
partition (the same file system old DOS and Windows machines used) after the kernel. To put
files on it, make a folder next to your program named after it with `.rootfs` on the end:

```
06-files.phi
06-files.rootfs/
    NOTES.TXT
```

Everything in that folder is copied onto the disk. Names have to be **8.3**: up to 8
characters, a dot, and up to 3 more, like `NOTES.TXT` or `DOCS/README.TXT`. Case doesn't
matter. Folders inside it become folders on the disk.

## The program

[examples/06-files.phi](../examples/06-files.phi):

```phi
use fs.fat16;

phi.Files:Kernel
{
	ptr<u8> buffer: new u8[1024];
	int size: 0;

	log 'Files on the disk:\n';
	int count: 0;
	call count is Fat.List: '';

	call size is Fat.ReadFile: 'notes.txt' buffer 1023;
	buffer:size is 0;
	log 'notes.txt says: ' buffer;

	str message: 'Written by my kernel.';
	bln ok: false;
	call ok is Fat.WriteFile: 'hello.txt' message message.len;
	call size is Fat.ReadFile: 'hello.txt' buffer 1023;
	buffer:size is 0;
	log 'hello.txt says: ' buffer '\n';
	exit 0;
}
```

```
Files on the disk:
  NOTES.TXT  30
notes.txt says: Remember to water the plants.
hello.txt says: Written by my kernel.
```

## What's going on

**`Fat.ReadFile: path buffer max`** copies up to `max` bytes of a file into `buffer` and
returns how many it copied, or -1 if the file isn't there. Files aren't zero-terminated, so
the example puts a 0 after the data (`buffer:size is 0`) before printing it as text.

**`Fat.WriteFile: path data length`** creates the file, or replaces it if it exists.
`Fat.Delete`, `Fat.FileSize`, `Fat.Exists` and `Fat.List` do what they say.

**The changes really are on the disk.** The kernel writes to the image file, so if you boot
the same image again, `HELLO.TXT` is there. (`phi build` makes a fresh image each time.)

## How it works

Three layers, each a PHI file you can read:

1. **[drivers.ata](../../lib/phi/drivers/ata.phi)** talks to the IDE disk controller through
   its I/O ports, reading and writing 512-byte sectors.
2. **[drivers.disk](../../lib/phi/drivers/disk.phi)** reads the partition table in the disk's
   first sector and finds the FAT partition, so the file system sees sector 0 as the start of
   its partition and can't touch anything outside it.
3. **[fs.fat16](../../lib/phi/fs/fat16.phi)** understands FAT16: the boot sector that describes
   the layout, the *file allocation table* that chains a file's 2 KB clusters together, and
   the 32-byte directory entries. Its structs (`FatBootSector`, `DirEntry`) match the on-disk
   layout byte for byte.

## Try this

1. Put a folder in the rootfs with a file inside, and read it with `Fat.ReadFile: 'folder/file.txt' ...`.
2. Write a kernel that keeps a counter in `COUNT.TXT`, adding 1 every time it boots. (Build
   once with `phi build`, then boot the image several times with
   `qemu-system-i386 -drive file=build/NAME/NAME.img,format=raw`.)
3. Print the size of every file with `Fat.FileSize`.

Next: [7. Programs and processes](07-programs.md)

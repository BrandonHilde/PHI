# libc: C's own library

The part of C's library that Windows (`msvcrt.dll`), macOS (`libSystem`) and Linux
(`libc.so.6`, `libm.so.6`) all have under the same names: memory, text, bytes, numbers out
of text, sorting, files, and the maths. Nothing is shipped: every system has it.

```phi
use vendor.libc;
use vendor.libc.math;

phi.Sorted:Program
{
	int values[5];
	values:0 is 42;
	values:1 is -7;
	values:2 is 1000;
	values:3 is 3;
	call C.qsort: addr values 5 4 addr foreign Order;
	log values:0 ' ' values:4 ' ' (CMath.pow: 2 10) '\n';
	exit 0;

	[Order: ptr<int> a: 0; ptr<int> b: 0;]
		if a:0 < b:0 return -1;
		if a:0 > b:0 return 1;
	[end: 0]
}
```

- `use vendor.libc;` is `C.` (`C.malloc`, `C.strlen`, `C.fopen`, `C.qsort`), and
  `use vendor.libc.math;` is `CMath.` (`CMath.sin`, `CMath.sqrt`, `CMath.atan2`), on `f64`s.
- C's `long` is `lng`, its `size_t` `usz`, and a `FILE` is held by its address
  (`ptr<FILE>`). `C.SEEK_SET`, `C.SEEK_CUR`, `C.SEEK_END` and `C.EOF` are its constants.
- `printf`, `sprintf` and `sscanf` take any number of values in C. Here each form has a
  name: `C.printf_int`, `C.printf_text`, `C.printf_f64`, and the same for `sprintf`;
  `C.sscanf_int`, `C.sscanf_f64`. Declare another form the same way
  ([extra.phi](extra.phi) shows how).
- PHI has its own text, files, memory and maths, which are what a PHI program is written
  with. This is for code that follows C's, and for what another C library expects to be
  given: memory from `malloc`, a `FILE`.
- What differs between the systems isn't here (`errno`, `stdin` and `stdout` as variables,
  `clock`, C99's additions, which Windows' `msvcrt.dll` lacks).
  [libc.h](libc.h) and [cmath.h](cmath.h) list what is.

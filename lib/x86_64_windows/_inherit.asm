; inherit: x86_64_linux text.asm print_int.asm int64.asm float.asm read_line.asm index_error.asm out_of_memory.asm throw.asm log_lock.asm
;
; 64-bit Windows programs share these with 64-bit Linux ones (lib/x86_64_linux): the pure
; routines, reading a line (both read standard input a byte at a time), and errors, which
; go to a catch or print and exit through the system's calls. Everything that reaches
; outside the program is a call to kernel32.dll instead (the files here).

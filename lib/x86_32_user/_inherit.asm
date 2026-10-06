; inherit: x86_32 int_to_str.asm strcpy.asm strcmp.asm strlen.asm print_int.asm print_int_serial.asm
;
; User programs share these pure routines with the kernel's library (lib/x86_32);
; everything that touches hardware is a system call instead (the files here).

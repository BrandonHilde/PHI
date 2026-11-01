; inherit: x86_32_user index_error.asm out_of_memory.asm throw.asm
;
; ... and these with PHI OS programs (lib/x86_32_user): errors, which go to a catch
; (if call ... throws err) or print and exit, through the same system calls.

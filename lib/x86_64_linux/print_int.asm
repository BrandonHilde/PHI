; provides: phi_print_int phi_print_uint phi_print_int_serial phi_print_uint_serial phi_int_to_str phi_uint_to_str phi_strcpy
; requires: phi_print phi_print_serial
;
; Numbers as text, for 64-bit code (x86_32's print_int, print_int_serial, int_to_str and
; strcpy, with 64-bit addresses): eax in decimal, signed or unsigned, to the screen and
; COM1 (on Linux, standard output) or to COM1 only (standard error).
phi_print_int:
    call phi_int_text
    jmp phi_print

phi_print_uint:
    call phi_uint_text
    jmp phi_print

phi_print_int_serial:
    call phi_int_text
    jmp phi_print_serial

phi_print_uint_serial:
    call phi_uint_text
    jmp phi_print_serial

; rsi = eax as text, in a buffer of its own
phi_int_text:
    lea rdi, [rel phi_print_int_text]
    mov ecx, 12
    call phi_int_to_str
    lea rsi, [rel phi_print_int_text]
    ret

phi_uint_text:
    lea rdi, [rel phi_print_int_text]
    mov ecx, 12
    call phi_uint_to_str
    lea rsi, [rel phi_print_int_text]
    ret

; Write eax as decimal text to rdi: signed (phi_int_to_str) or unsigned (phi_uint_to_str).
; ecx is the size of the destination including its terminator.
phi_uint_to_str:
    push rcx
    xor ebx, ebx            ; never negative
    jmp phi_int_to_str.start

phi_int_to_str:
    push rcx
    xor ebx, ebx            ; ebx = 1 if negative
    test eax, eax
    jns .start
    neg eax
    inc ebx
.start:
    lea rsi, [rel phi_int_to_str_scratch + 11]   ; digits are built backwards from the end
    mov byte [rsi], 0
    mov ecx, 10
.digit:
    xor edx, edx
    div ecx
    add dl, '0'
    dec rsi
    mov [rsi], dl
    test eax, eax
    jnz .digit
    test ebx, ebx
    jz .copy
    dec rsi
    mov byte [rsi], '-'
.copy:
    pop rcx
    jmp phi_strcpy

; Copy the string at rsi to rdi. ecx is the size of the destination including its
; terminator; longer strings are cut short. The result is always terminated.
phi_strcpy:
    jecxz .done
.next:
    dec ecx
    jz .terminate
    lodsb
    test al, al
    jz .terminate
    stosb
    jmp .next
.terminate:
    mov byte [rdi], 0
.done:
    ret

phi_print_int_text:      times 12 db 0
phi_int_to_str_scratch:  times 12 db 0

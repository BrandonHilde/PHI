; provides: phi_exit OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_Sleep OS_GetTicks OS_Yield OS_CurrentTask OS_Spawn OS_Wait
;
; requires: phi_flush
;
; The system calls a user program makes (see lib/x86_32/tasks.asm for the kernel side).
SYS_EXIT      equ 0
SYS_READ_KEY  equ 3
SYS_SLEEP     equ 4
SYS_TICKS     equ 5
SYS_GETPID    equ 6
SYS_YIELD     equ 7
SYS_KEY_READY equ 8
SYS_WAIT      equ 9
SYS_SPAWN     equ 16

; ebx = exit code
phi_exit:
    call phi_flush
.again:
    mov eax, SYS_EXIT
    int 0x80
    jmp .again              ; never returns

OS_ReadKey:
Bootloader_WaitForKeyPress:
phi_read_char:
    mov eax, SYS_READ_KEY
    int 0x80
    ret

OS_KeyAvailable:
    mov eax, SYS_KEY_READY
    int 0x80
    ret

; arg: milliseconds
OS_Sleep:
    mov ebx, [esp + 4]
    mov eax, SYS_SLEEP
    int 0x80
    ret

OS_GetTicks:
    mov eax, SYS_TICKS
    int 0x80
    ret

OS_Yield:
    mov eax, SYS_YIELD
    int 0x80
    ret

OS_CurrentTask:
    mov eax, SYS_GETPID
    int 0x80
    ret

; arg: path of a program on the disk -> eax = its process number, or -1
OS_Spawn:
    mov ebx, [esp + 4]
    mov eax, SYS_SPAWN
    int 0x80
    ret

; arg: process number of a child -> eax = its exit code (-1 if it crashed, or isn't a child)
OS_Wait:
    mov ebx, [esp + 4]
    mov eax, SYS_WAIT
    int 0x80
    ret

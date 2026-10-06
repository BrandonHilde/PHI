; provides: phi_exit OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_Sleep OS_GetTicks OS_Yield OS_CurrentTask OS_Spawn OS_Run OS_Wait OS_Arguments OS_ReadFile OS_WriteFile OS_DeleteFile OS_ListFiles OS_FileSize OS_TaskState OS_TaskName OS_Kill OS_FreeMemory OS_TotalMemory OS_Reboot OS_ClearScreen OS_PutCell OS_IsKeyDown
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
SYS_SPAWN     equ 16      ; 16 and up are handled by the kernel's proc.process
SYS_READ_FILE equ 17
SYS_WRITE_FILE equ 18
SYS_DELETE_FILE equ 19
SYS_LIST_FILES equ 20
SYS_FILE_SIZE equ 21
SYS_TASK_STATE equ 22
SYS_TASK_NAME equ 23
SYS_KILL      equ 24
SYS_FREE_MEMORY equ 25
SYS_TOTAL_MEMORY equ 26
SYS_REBOOT    equ 27
SYS_CLEAR     equ 28
SYS_PUT_CELL  equ 29
SYS_KEY_DOWN  equ 30

; where the kernel puts a program's command line (the bottom of its stack area)
ARGUMENTS     equ 0x403F0000

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
    xor ecx, ecx
    mov eax, SYS_SPAWN
    int 0x80
    ret

; args: path, command line for it -> eax = its process number, or -1
OS_Run:
    mov ebx, [esp + 4]
    mov ecx, [esp + 8]
    mov eax, SYS_SPAWN
    int 0x80
    ret

; arg: process number of a child -> eax = its exit code (-1 if it crashed, or isn't a child)
OS_Wait:
    mov ebx, [esp + 4]
    mov eax, SYS_WAIT
    int 0x80
    ret

; eax = this program's command line (what came after its name)
OS_Arguments:
    mov eax, ARGUMENTS
    ret

; a system call taking 0-3 arguments from the stack (into ebx, ecx, edx);
; it only reads the arguments there are, so it never reads past the top of the stack
%macro PHI_SYSCALL 3
%1:
%if %3 >= 1
    mov ebx, [esp + 4]
%endif
%if %3 >= 2
    mov ecx, [esp + 8]
%endif
%if %3 >= 3
    mov edx, [esp + 12]
%endif
    mov eax, %2
    int 0x80
    ret
%endmacro

PHI_SYSCALL OS_ReadFile, SYS_READ_FILE, 3        ; path buffer max -> bytes read, or -1
PHI_SYSCALL OS_WriteFile, SYS_WRITE_FILE, 3      ; path data length -> 1 if written
PHI_SYSCALL OS_DeleteFile, SYS_DELETE_FILE, 1    ; path -> 1 if deleted
PHI_SYSCALL OS_ListFiles, SYS_LIST_FILES, 1      ; path -> entries printed, or -1
PHI_SYSCALL OS_FileSize, SYS_FILE_SIZE, 1        ; path -> bytes, or -1
PHI_SYSCALL OS_TaskState, SYS_TASK_STATE, 1      ; process -> state (0 = no such process)
PHI_SYSCALL OS_TaskName, SYS_TASK_NAME, 2        ; process buffer -> 1 if it has a name (up to 15 characters)
PHI_SYSCALL OS_Kill, SYS_KILL, 1                 ; process -> 1 if it was stopped
PHI_SYSCALL OS_FreeMemory, SYS_FREE_MEMORY, 0    ; -> free memory in KB
PHI_SYSCALL OS_TotalMemory, SYS_TOTAL_MEMORY, 0  ; -> all usable memory in KB
PHI_SYSCALL OS_Reboot, SYS_REBOOT, 0
PHI_SYSCALL OS_ClearScreen, SYS_CLEAR, 0
PHI_SYSCALL OS_PutCell, SYS_PUT_CELL, 3          ; column row cell
PHI_SYSCALL OS_IsKeyDown, SYS_KEY_DOWN, 1        ; key -> 1 if held down

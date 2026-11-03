; provides: phi_exit phi_fault_register phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_CurrentTask OS_Arguments OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
; requires: phi_flush phi_write_all phi_imports
; init: phi_windows_start
;
; What a PHI OS program asks its kernel for, answered by kernel32.dll in a 64-bit Windows
; program, as x86_32_windows/system.asm does in a 32-bit one (see imports.asm for how its
; functions are called). Paths are converted to UTF-16 for the functions that take them,
; a byte a character.
;
; Arguments PHI pushes are 8 bytes each in 64-bit code: the first is at [rsp + 8]. Each
; call keeps rsi and rdi.
STD_INPUT_HANDLE equ -10
STD_OUTPUT_HANDLE equ -11
FILE_TYPE_PIPE equ 3
MEM_COMMIT    equ 0x1000
MEM_RESERVE   equ 0x2000
PAGE_READWRITE equ 4
HEAP_RESERVE  equ 0x400000000           ; 16 GB of addresses: a 64-bit heap has room
HEAP_AT       equ 0x500000000           ; where they start (past 4 GB, so every address must be whole)
ENABLE_VIRTUAL_TERMINAL_PROCESSING equ 4
ARGUMENTS_SIZE equ 1024

; (the same as console.asm's: whichever comes first defines it)
%ifnmacro PHI_CALL 1-4
%macro PHI_CALL 1-4
    mov r12, rsp
    and rsp, -16
    sub rsp, 64
%if %0 >= 2
    mov qword [rsp + 32], %2
%endif
%if %0 >= 3
    mov qword [rsp + 40], %3
%endif
%if %0 >= 4
    mov qword [rsp + 48], %4
%endif
    call [rel phi_imp_%1]
    mov rsp, r12
%endmacro
%endif

; Called before the program's code: keeps its command line after its own name. Windows'
; timer steps every 15.6 ms unless a program asks for less, so OS.Sleep: 1 would sleep up
; to 15 ms here and 1 ms everywhere else: the program asks for 1 ms steps, which
; timeGetTime then counts in too (GetTickCount64 still steps by 15.6). Time itself is
; lib/phi/host/time_windows.phi's, in PHI.
phi_windows_start:
    mov ecx, 1
    PHI_CALL timeBeginPeriod
    PHI_CALL GetCommandLineA
    mov rsi, rax
    mov ah, ' '                         ; what ends the program's name
    cmp byte [rsi], '"'
    jne .name
    inc rsi
    mov ah, '"'
.name:
    mov al, [rsi]
    test al, al
    jz .spaces
    inc rsi
    cmp al, ah
    jne .name
.spaces:
    cmp byte [rsi], ' '
    je .space
    cmp byte [rsi], 9
    jne .copy
.space:
    inc rsi
    jmp .spaces
.copy:
    lea rdi, [rel phi_arguments]
    mov ecx, ARGUMENTS_SIZE - 1
.byte:
    mov al, [rsi]
    test al, al
    jz .done
    mov [rdi], al
    inc rsi
    inc rdi
    dec ecx
    jnz .byte
.done:
    mov byte [rdi], 0
    ret

; ebx = exit code
phi_exit:
    call phi_flush
    mov ecx, ebx
    PHI_CALL ExitProcess

; eax = the next byte of standard input, or -1 at its end (keeps rsi and rdi)
phi_read_byte:
    mov ecx, STD_INPUT_HANDLE
    PHI_CALL GetStdHandle
    mov rcx, rax
    lea rdx, [rel phi_byte]
    mov r8d, 1
    lea r9, [rel phi_read_count]
    PHI_CALL ReadFile, 0
    test eax, eax
    jz .end
    cmp dword [rel phi_read_count], 1
    jne .end
    movzx eax, byte [rel phi_byte]
    ret
.end:
    mov eax, -1
    ret

; eax = the next character typed; Enter is 13, as on PHI OS (a line ending in 13 10, or
; in 10, gives one), and 0 means the input ended. A console hands a program its input a
; line at a time, so this waits for Enter.
OS_ReadKey:
Bootloader_WaitForKeyPress:
phi_read_char:
    call phi_flush
.again:
    call phi_read_byte
    mov dl, [rel phi_last_byte]
    mov [rel phi_last_byte], al
    cmp eax, 10
    jne .not_enter
    cmp dl, 13
    je .again                           ; the end of 13 10: that Enter was given already
    mov eax, 13
.not_enter:
    test eax, eax
    jns .done
    xor eax, eax
.done:
    ret

; eax = 1 if there's input waiting
OS_KeyAvailable:
    push rbx
    mov ecx, STD_INPUT_HANDLE
    PHI_CALL GetStdHandle
    mov rbx, rax
    mov rcx, rbx
    PHI_CALL GetFileType
    cmp eax, FILE_TYPE_PIPE
    jne .wait
    mov rcx, rbx
    xor edx, edx
    xor r8d, r8d
    xor r9d, r9d
    lea r10, [rel phi_read_count]       ; how many bytes are waiting
    PHI_CALL PeekNamedPipe, r10, 0
    test eax, eax
    jz .none
    cmp dword [rel phi_read_count], 0
    je .none
    jmp .some
.wait:
    mov rcx, rbx                        ; a console: is anything waiting? (don't wait)
    xor edx, edx
    PHI_CALL WaitForSingleObject
    test eax, eax
    jnz .none
.some:
    mov eax, 1
    pop rbx
    ret
.none:
    xor eax, eax
    pop rbx
    ret

; -> this program's process number
OS_CurrentTask:
    PHI_CALL GetCurrentProcessId
    ret

; -> what came after the program's name on its command line
OS_Arguments:
    lea rax, [rel phi_arguments]
    ret

; arg: bytes -> rax = where the new memory starts (whole pages, zeroed, right after the
; last it gave), or 0. The heap's addresses are reserved the first time, and each call
; commits the next part of them.
OS_Grow:
    mov rax, [rel phi_heap_start]
    test rax, rax
    jnz .grow
    mov rcx, HEAP_AT
    mov rdx, HEAP_RESERVE
    mov r8d, MEM_RESERVE
    mov r9d, PAGE_READWRITE
    PHI_CALL VirtualAlloc
    test rax, rax
    jz .failed
    mov [rel phi_heap_start], rax
    mov [rel phi_heap_end], rax
.grow:
    mov edx, [rsp + 8]
    add rdx, 4095
    and rdx, ~4095
    mov rcx, [rel phi_heap_end]
    sub rcx, [rel phi_heap_start]
    add rcx, rdx
    mov rax, HEAP_RESERVE
    cmp rcx, rax
    ja .failed
    push rdx
    push rdx                            ; (twice: the stack stays as PHI_CALL found it either way)
    mov rcx, [rel phi_heap_end]
    mov r8d, MEM_COMMIT
    mov r9d, PAGE_READWRITE
    PHI_CALL VirtualAlloc
    pop rdx
    pop rdx
    test rax, rax
    jz .failed
    mov rax, [rel phi_heap_end]
    add [rel phi_heap_end], rdx
    ret
.failed:
    xor eax, eax
    ret

; -> all memory, in KB
OS_TotalMemory:
    mov eax, 8                          ; ullTotalPhys
    jmp phi_memory

; -> free memory, in KB
OS_FreeMemory:
    mov eax, 16                         ; ullAvailPhys

phi_memory:
    push rbx
    mov ebx, eax
    mov dword [rel phi_memory_status], 64   ; the structure's size
    lea rcx, [rel phi_memory_status]
    PHI_CALL GlobalMemoryStatusEx
    lea rcx, [rel phi_memory_status]
    mov rax, [rcx + rbx]
    shr rax, 10                         ; (a u32 of KB: up to 4 TB)
    pop rbx
    ret

; clear the console and put the cursor at its top left
OS_ClearScreen:
    call phi_flush
    push rsi
    push rbx
    mov ecx, STD_OUTPUT_HANDLE
    PHI_CALL GetStdHandle
    mov rbx, rax
    mov rcx, rbx
    lea rdx, [rel phi_console_mode]
    PHI_CALL GetConsoleMode
    test eax, eax
    jz .write                           ; not a console: the codes go out as they are
    mov edx, [rel phi_console_mode]
    or edx, ENABLE_VIRTUAL_TERMINAL_PROCESSING
    mov rcx, rbx
    PHI_CALL SetConsoleMode
.write:
    lea rsi, [rel phi_clear]
    mov edx, phi_clear_end - phi_clear
    call phi_write_all
    pop rbx
    pop rsi
    ret

phi_clear: db 27, '[2J', 27, '[H'
phi_clear_end:
phi_byte: db 0
phi_last_byte: db 0
align 8, db 0
phi_memory_status: times 64 db 0
phi_heap_start: dq 0
phi_heap_end: dq 0
phi_read_count: dd 0
phi_console_mode: dd 0
phi_arguments: times ARGUMENTS_SIZE db 0

; rax = where this program goes when the CPU stops it (dividing by zero, or memory that
; isn't there): throw.asm's phi_fault_entry, with ebx = the kind of error. Windows tells a
; vectored exception handler first, which changes the stopped code's registers (its
; CONTEXT) so it carries on there, as PHI OS sends a program
phi_fault_register:
    mov [rel phi_fault_to], rax
    mov ecx, 1                          ; before any other handler
    lea rdx, [rel phi_fault_handler]
    PHI_CALL AddVectoredExceptionHandler
    ret

; Windows' call (rcx = its EXCEPTION_POINTERS) -> eax = -1 to carry on in the changed
; CONTEXT, 0 to let Windows go on (and end the program, as before); it changes only what
; a call may (rax, rcx, rdx, r8)
phi_fault_handler:
    mov rdx, [rcx]                      ; the EXCEPTION_RECORD
    mov rcx, [rcx + 8]                  ; the CONTEXT
    mov eax, [rdx]                      ; what happened
    cmp eax, 0xC0000094                 ; EXCEPTION_INT_DIVIDE_BY_ZERO
    je .divide
    cmp eax, 0xC0000005                 ; EXCEPTION_ACCESS_VIOLATION: its address in [rdx + 40]
    jne .not_ours
    mov eax, ERROR_ACCESS
    cmp qword [rdx + 40], 4096
    jae .send
    mov eax, ERROR_EMPTY
    jmp .send
.divide:
    mov eax, ERROR_DIVIDE
.send:
    mov r8, [rel phi_fault_to]
    cmp [rcx + CONTEXT_RIP], r8
    je .not_ours                        ; the entry itself was stopped: it would only be again
    mov [rcx + CONTEXT_RBX], rax
    mov rax, [rcx + CONTEXT_RIP]
    mov [rcx + CONTEXT_RCX], rax        ; where it happened, for its line
    mov [rcx + CONTEXT_RIP], r8
    mov eax, -1                         ; EXCEPTION_CONTINUE_EXECUTION
    ret
.not_ours:
    xor eax, eax                        ; EXCEPTION_CONTINUE_SEARCH
    ret

ERROR_DIVIDE  equ 3                     ; ErrorKind (lib/phi/lang/error.phi)
ERROR_EMPTY   equ 4
ERROR_ACCESS  equ 5
CONTEXT_RBX   equ 0x90                  ; in an x64 CONTEXT
CONTEXT_RCX   equ 0x80
CONTEXT_RIP   equ 0xF8
align 8
phi_fault_to: dq 0

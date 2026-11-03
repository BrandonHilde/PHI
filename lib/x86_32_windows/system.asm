; provides: phi_exit phi_fault_register phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_CurrentTask OS_Arguments OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
; requires: phi_flush phi_write_all phi_imports
; init: phi_windows_start
;
; What a PHI OS program asks its kernel for, answered by kernel32.dll (see imports.asm
; for how its functions are called). The calls PHI OS has that Windows has no answer for
; here (the screen's cells, other PHI OS programs, sound, windows) aren't here, so using
; one is an error when the program is built. The network's are in sockets.asm. Paths are converted
; to UTF-16 for the functions that take them, a byte a character.
STD_INPUT_HANDLE equ -10
STD_OUTPUT_HANDLE equ -11
FILE_TYPE_PIPE equ 3
MEM_COMMIT    equ 0x1000
MEM_RESERVE   equ 0x2000
PAGE_READWRITE equ 4
HEAP_RESERVE  equ 0x8000000             ; 128 MB: more than the heap ever asks for
ENABLE_VIRTUAL_TERMINAL_PROCESSING equ 4
ARGUMENTS_SIZE equ 1024

; Called before the program's code: keeps its command line after its own name. Windows'
; timer steps every 15.6 ms unless a program asks for less, so OS.Sleep: 1 would sleep up
; to 15 ms here and 1 ms everywhere else: the program asks for 1 ms steps, which
; timeGetTime then counts in too (GetTickCount64 still steps by 15.6). Time itself is
; lib/phi/host/time_windows.phi's, in PHI.
phi_windows_start:
    push 1
    call [phi_imp_timeBeginPeriod]
    call [phi_imp_GetCommandLineA]
    mov esi, eax
    mov ah, ' '                         ; what ends the program's name
    cmp byte [esi], '"'
    jne .name
    inc esi
    mov ah, '"'
.name:
    mov al, [esi]
    test al, al
    jz .spaces
    inc esi
    cmp al, ah
    jne .name
.spaces:
    cmp byte [esi], ' '
    je .space
    cmp byte [esi], 9
    jne .copy
.space:
    inc esi
    jmp .spaces
.copy:
    mov edi, phi_arguments
    mov ecx, ARGUMENTS_SIZE - 1
.byte:
    mov al, [esi]
    test al, al
    jz .done
    mov [edi], al
    inc esi
    inc edi
    dec ecx
    jnz .byte
.done:
    mov byte [edi], 0
    ret

; ebx = exit code
phi_exit:
    call phi_flush
    push ebx
    call [phi_imp_ExitProcess]

; eax = the next byte of standard input, or -1 at its end
phi_read_byte:
    push STD_INPUT_HANDLE
    call [phi_imp_GetStdHandle]
    push 0
    push phi_read_count
    push 1
    push phi_byte
    push eax
    call [phi_imp_ReadFile]
    test eax, eax
    jz .end
    cmp dword [phi_read_count], 1
    jne .end
    movzx eax, byte [phi_byte]
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
    mov dl, [phi_last_byte]
    mov [phi_last_byte], al
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
    push STD_INPUT_HANDLE
    call [phi_imp_GetStdHandle]
    mov ebx, eax
    push ebx
    call [phi_imp_GetFileType]
    cmp eax, FILE_TYPE_PIPE
    jne .wait
    push 0
    push phi_read_count                 ; how many bytes are waiting
    push 0
    push 0
    push 0
    push ebx
    call [phi_imp_PeekNamedPipe]
    test eax, eax
    jz .none
    cmp dword [phi_read_count], 0
    je .none
    mov eax, 1
    ret
.wait:
    push 0                              ; a console: is anything waiting? (don't wait)
    push ebx
    call [phi_imp_WaitForSingleObject]
    test eax, eax
    jnz .none
    mov eax, 1
    ret
.none:
    xor eax, eax
    ret

; -> this program's process number
OS_CurrentTask:
    call [phi_imp_GetCurrentProcessId]
    ret

; -> what came after the program's name on its command line
OS_Arguments:
    mov eax, phi_arguments
    ret

; arg: bytes -> where the new memory starts (whole pages, zeroed, right after the last
; it gave), or 0. The heap's addresses are reserved the first time, and each call commits
; the next part of them.
OS_Grow:
    mov eax, [phi_heap_start]
    test eax, eax
    jnz .grow
    push PAGE_READWRITE
    push MEM_RESERVE
    push HEAP_RESERVE
    push 0
    call [phi_imp_VirtualAlloc]
    test eax, eax
    jz .failed
    mov [phi_heap_start], eax
    mov [phi_heap_end], eax
.grow:
    mov edx, [esp + 4]
    add edx, 4095
    jc .failed
    and edx, ~4095
    mov ecx, [phi_heap_end]
    sub ecx, [phi_heap_start]
    add ecx, edx
    jc .failed
    cmp ecx, HEAP_RESERVE
    ja .failed
    push edx
    push PAGE_READWRITE
    push MEM_COMMIT
    push edx
    push dword [phi_heap_end]
    call [phi_imp_VirtualAlloc]
    pop edx
    test eax, eax
    jz .failed
    mov eax, [phi_heap_end]
    add [phi_heap_end], edx
    ret
.failed:
    xor eax, eax
    ret

; -> all memory, in KB
OS_TotalMemory:
    mov ebx, 8                          ; ullTotalPhys
    jmp phi_memory

; -> free memory, in KB
OS_FreeMemory:
    mov ebx, 16                         ; ullAvailPhys

phi_memory:
    mov dword [phi_memory_status], 64   ; the structure's size
    push phi_memory_status
    call [phi_imp_GlobalMemoryStatusEx]
    mov eax, [phi_memory_status + ebx]
    mov edx, [phi_memory_status + ebx + 4]
    shrd eax, edx, 10
    ret

; clear the console and put the cursor at its top left
OS_ClearScreen:
    call phi_flush
    push STD_OUTPUT_HANDLE
    call [phi_imp_GetStdHandle]
    mov ebx, eax
    push phi_console_mode
    push ebx
    call [phi_imp_GetConsoleMode]
    test eax, eax
    jz .write                           ; not a console: the codes go out as they are
    mov eax, [phi_console_mode]
    or eax, ENABLE_VIRTUAL_TERMINAL_PROCESSING
    push eax
    push ebx
    call [phi_imp_SetConsoleMode]
.write:
    mov ecx, phi_clear
    mov edx, phi_clear_end - phi_clear
    jmp phi_write_all

phi_clear: db 27, '[2J', 27, '[H'
phi_clear_end:
phi_byte: db 0
phi_last_byte: db 0
align 8, db 0
phi_memory_status: times 64 db 0
phi_read_count: dd 0
phi_heap_start: dd 0
phi_heap_end: dd 0
phi_console_mode: dd 0
phi_arguments: times ARGUMENTS_SIZE db 0

; eax = where this program goes when the CPU stops it (dividing by zero, or memory that
; isn't there): throw.asm's phi_fault_entry, with ebx = the kind of error. Windows tells a
; vectored exception handler first, which changes the stopped code's registers (its
; CONTEXT) so it carries on there, as PHI OS sends a program
phi_fault_register:
    mov [phi_fault_to], eax
    push phi_fault_handler
    push 1                              ; before any other handler
    call [phi_imp_AddVectoredExceptionHandler]
    ret

; Windows' call (stdcall: [esp + 4] = its EXCEPTION_POINTERS) -> eax = -1 to carry on in
; the changed CONTEXT, 0 to let Windows go on (and end the program, as before)
phi_fault_handler:
    mov ecx, [esp + 4]
    mov edx, [ecx]                      ; the EXCEPTION_RECORD
    mov ecx, [ecx + 4]                  ; the CONTEXT
    mov eax, [edx]                      ; what happened
    cmp eax, 0xC0000094                 ; EXCEPTION_INT_DIVIDE_BY_ZERO
    je .divide
    cmp eax, 0xC0000005                 ; EXCEPTION_ACCESS_VIOLATION: its address in [edx + 24]
    jne .not_ours
    mov eax, ERROR_ACCESS
    cmp dword [edx + 24], 4096
    jae .send
    mov eax, ERROR_EMPTY
    jmp .send
.divide:
    mov eax, ERROR_DIVIDE
.send:
    mov edx, [phi_fault_to]
    cmp [ecx + CONTEXT_EIP], edx
    je .not_ours                        ; the entry itself was stopped: it would only be again
    mov [ecx + CONTEXT_EBX], eax
    mov eax, [ecx + CONTEXT_EIP]
    mov [ecx + CONTEXT_ECX], eax        ; where it happened, for its line
    mov [ecx + CONTEXT_EIP], edx
    mov eax, -1                         ; EXCEPTION_CONTINUE_EXECUTION
    ret 4
.not_ours:
    xor eax, eax                        ; EXCEPTION_CONTINUE_SEARCH
    ret 4

ERROR_DIVIDE  equ 3                     ; ErrorKind (lib/phi/lang/error.phi)
ERROR_EMPTY   equ 4
ERROR_ACCESS  equ 5
CONTEXT_EBX   equ 0xA4                  ; in an x86 CONTEXT
CONTEXT_ECX   equ 0xAC
CONTEXT_EIP   equ 0xB8
phi_fault_to: dd 0

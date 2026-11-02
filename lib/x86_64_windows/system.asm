; provides: phi_exit phi_fault_register phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_Sleep OS_GetTicks OS_Yield OS_CurrentTask OS_Arguments OS_ReadFile OS_ReadFileAt OS_WriteFile OS_AppendFile OS_DeleteFile OS_FileSize OS_MakeFolder OS_DeleteFolder OS_Rename OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
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
GENERIC_READ  equ 0x80000000
GENERIC_WRITE equ 0x40000000
FILE_APPEND_DATA equ 4
FILE_SHARE_ALL equ 7                    ; others can read, write and delete it meanwhile
CREATE_ALWAYS equ 2
OPEN_EXISTING equ 3
OPEN_ALWAYS   equ 4
FILE_ATTRIBUTE_NORMAL equ 0x80
FILE_ATTRIBUTE_DIRECTORY equ 0x10
FILE_TYPE_PIPE equ 3
MEM_COMMIT    equ 0x1000
MEM_RESERVE   equ 0x2000
PAGE_READWRITE equ 4
HEAP_RESERVE  equ 0x400000000           ; 16 GB of addresses: a 64-bit heap has room
HEAP_AT       equ 0x500000000           ; where they start (past 4 GB, so every address must be whole)
ENABLE_VIRTUAL_TERMINAL_PROCESSING equ 4
ARGUMENTS_SIZE equ 1024
WIDE_PATH     equ 1024                  ; characters

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

; Called before the program's code: keeps the time it started (OS.GetTicks counts from
; there) and its command line after its own name.
phi_windows_start:
    PHI_CALL GetTickCount64
    mov [rel phi_started], eax
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

; -> milliseconds since the program started
OS_GetTicks:
    PHI_CALL GetTickCount64
    sub eax, [rel phi_started]
    ret

; arg: milliseconds
OS_Sleep:
    mov ecx, [rsp + 8]
    test ecx, ecx
    jle .done
    PHI_CALL Sleep
.done:
    ret

OS_Yield:
    PHI_CALL SwitchToThread
    ret

; -> this program's process number
OS_CurrentTask:
    PHI_CALL GetCurrentProcessId
    ret

; -> what came after the program's name on its command line
OS_Arguments:
    lea rax, [rel phi_arguments]
    ret

; rsi = a path, rdi = room for WIDE_PATH characters -> rax = rdi, the path in UTF-16
; there. Changes ecx, rsi and rdi.
phi_wide:
    push rdi
    mov ecx, WIDE_PATH - 1
.char:
    movzx eax, byte [rsi]
    test eax, eax
    jz .done
    mov [rdi], ax
    inc rsi
    add rdi, 2
    dec ecx
    jnz .char
.done:
    mov word [rdi], 0
    pop rax
    ret

; rcx = a path in UTF-16, edx = what for (GENERIC_READ ...), r8d = how (OPEN_EXISTING ...)
; -> rax = the file's handle, or -1
phi_open:
    mov r10d, r8d
    mov r8d, FILE_SHARE_ALL
    xor r9d, r9d                        ; no security attributes
    PHI_CALL CreateFileW, r10, FILE_ATTRIBUTE_NORMAL, 0
    ret

; rsi = path, eax = offset, rdi = buffer, ebx = most bytes -> eax = bytes read, or -1
; (changes rsi; the file's handle is kept in it, which kernel32 keeps)
phi_file_read:
    push rax                            ; the offset
    push rdi
    lea rdi, [rel phi_wide_path]
    call phi_wide
    pop rdi
    mov rcx, rax
    mov edx, GENERIC_READ
    mov r8d, OPEN_EXISTING
    call phi_open
    pop rcx                             ; the offset
    cmp rax, -1
    je .failed
    mov rsi, rax                        ; the file
    test ecx, ecx
    jz .read
    mov edx, ecx
    mov rcx, rsi
    xor r8d, r8d
    xor r9d, r9d                        ; from the start
    PHI_CALL SetFilePointer
    cmp eax, -1
    je .close_failed
.read:
    mov dword [rel phi_total], 0
.more:
    mov eax, [rel phi_total]
    cmp eax, ebx
    jge .close
    mov r8d, ebx
    sub r8d, eax
    lea rdx, [rdi + rax]
    mov rcx, rsi
    lea r9, [rel phi_read_count]
    PHI_CALL ReadFile, 0
    test eax, eax
    jz .close_failed
    mov eax, [rel phi_read_count]
    test eax, eax
    jz .close                           ; the end of the file
    add [rel phi_total], eax
    jmp .more
.close:
    mov rcx, rsi
    PHI_CALL CloseHandle
    mov eax, [rel phi_total]
    ret
.close_failed:
    mov rcx, rsi
    PHI_CALL CloseHandle
.failed:
    mov eax, -1
    ret

; args: path buffer max -> bytes read, or -1
OS_ReadFile:
    push rsi
    push rdi
    push rbx
    mov rsi, [rsp + 32]
    xor eax, eax
    mov rdi, [rsp + 40]
    mov ebx, [rsp + 48]
    call phi_file_read
    pop rbx
    pop rdi
    pop rsi
    ret

; args: path offset buffer max -> bytes read, or -1
OS_ReadFileAt:
    push rsi
    push rdi
    push rbx
    mov rsi, [rsp + 32]
    mov eax, [rsp + 40]
    mov rdi, [rsp + 48]
    mov ebx, [rsp + 56]
    call phi_file_read
    pop rbx
    pop rdi
    pop rsi
    ret

; rsi = path, ebx = GENERIC_WRITE or FILE_APPEND_DATA, ecx = CREATE_ALWAYS or OPEN_ALWAYS,
; rdi = data, edx = length -> eax = 1 if written
phi_file_write:
    push rdx
    push rcx
    push rdi
    lea rdi, [rel phi_wide_path]
    call phi_wide
    pop rdi
    pop r8                              ; how it opens
    mov edx, ebx
    mov rcx, rax
    call phi_open
    pop rdx
    cmp rax, -1
    je .failed
    mov rbx, rax                        ; the file (kernel32 keeps rbx)
    mov rsi, rdi
    call phi_write_all
    push rax
    mov rcx, rbx
    PHI_CALL CloseHandle
    pop rax
    ret
.failed:
    xor eax, eax
    ret

; args: path data length -> 1 if written
OS_WriteFile:
    mov ebx, GENERIC_WRITE
    mov ecx, CREATE_ALWAYS
    jmp phi_file_write_args

; args: path data length -> 1 if added to the end (a new file if there's none)
OS_AppendFile:
    mov ebx, FILE_APPEND_DATA
    mov ecx, OPEN_ALWAYS

; ebx and ecx as phi_file_write takes them; the arguments are OS_WriteFile's
phi_file_write_args:
    push rsi
    push rdi
    push rbx
    mov rsi, [rsp + 32]
    mov rdi, [rsp + 40]
    mov edx, [rsp + 48]
    call phi_file_write
    pop rbx
    pop rdi
    pop rsi
    ret

; arg: path -> its size in bytes, or -1 if it isn't a file
OS_FileSize:
    push rsi
    push rdi
    mov rsi, [rsp + 24]
    lea rdi, [rel phi_wide_path]
    call phi_wide
    mov rcx, rax
    xor edx, edx                        ; the standard information
    lea r8, [rel phi_attributes]
    PHI_CALL GetFileAttributesExW
    pop rdi
    pop rsi
    test eax, eax
    jz .failed
    test byte [rel phi_attributes], FILE_ATTRIBUTE_DIRECTORY
    jnz .failed
    mov eax, [rel phi_attributes + 32]  ; its size (the low 32 bits)
    ret
.failed:
    mov eax, -1
    ret

; eax = what a function gave, with rsi and rdi pushed -> 1 if it worked (not 0), else 0
phi_succeeded:
    pop rdi
    pop rsi
    test eax, eax
    setnz al
    movzx eax, al
    ret

; arg: path -> 1 if deleted
OS_DeleteFile:
    push rsi
    push rdi
    mov rsi, [rsp + 24]
    lea rdi, [rel phi_wide_path]
    call phi_wide
    mov rcx, rax
    PHI_CALL DeleteFileW
    jmp phi_succeeded

; arg: path -> 1 if made
OS_MakeFolder:
    push rsi
    push rdi
    mov rsi, [rsp + 24]
    lea rdi, [rel phi_wide_path]
    call phi_wide
    mov rcx, rax
    xor edx, edx
    PHI_CALL CreateDirectoryW
    jmp phi_succeeded

; arg: path -> 1 if deleted (only an empty folder)
OS_DeleteFolder:
    push rsi
    push rdi
    mov rsi, [rsp + 24]
    lea rdi, [rel phi_wide_path]
    call phi_wide
    mov rcx, rax
    PHI_CALL RemoveDirectoryW
    jmp phi_succeeded

; args: from to -> 1 if renamed (or moved)
OS_Rename:
    push rsi
    push rdi
    mov rsi, [rsp + 32]
    lea rdi, [rel phi_wide_to]
    call phi_wide
    mov rsi, [rsp + 24]
    lea rdi, [rel phi_wide_path]
    call phi_wide
    lea rcx, [rel phi_wide_path]
    lea rdx, [rel phi_wide_to]
    PHI_CALL MoveFileW
    jmp phi_succeeded

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
phi_started: dd 0
phi_read_count: dd 0
phi_total: dd 0
phi_console_mode: dd 0
phi_attributes: times 36 db 0
phi_wide_path: times WIDE_PATH dw 0
phi_wide_to: times WIDE_PATH dw 0
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
CONTEXT_RIP   equ 0xF8
align 8
phi_fault_to: dq 0

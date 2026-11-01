; provides: phi_exit phi_read_byte OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_Sleep OS_GetTicks OS_Yield OS_CurrentTask OS_Arguments OS_ReadFile OS_ReadFileAt OS_WriteFile OS_AppendFile OS_DeleteFile OS_FileSize OS_MakeFolder OS_DeleteFolder OS_Rename OS_Grow OS_FreeMemory OS_TotalMemory OS_ClearScreen
; requires: phi_flush phi_write_all phi_imports
; init: phi_windows_start
;
; What a PHI OS program asks its kernel for, answered by kernel32.dll (see imports.asm
; for how its functions are called). The calls PHI OS has that Windows has no answer for
; here (the screen's cells, other PHI OS programs, sound, windows, the network for now)
; aren't here, so using one is an error when the program is built. Paths are converted
; to UTF-16 for the functions that take them, a byte a character.
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
HEAP_RESERVE  equ 0x8000000             ; 128 MB: more than the heap ever asks for
ENABLE_VIRTUAL_TERMINAL_PROCESSING equ 4
ARGUMENTS_SIZE equ 1024
WIDE_PATH     equ 1024                  ; characters

; Called before the program's code: keeps the time it started (OS.GetTicks counts from
; there) and its command line after its own name.
phi_windows_start:
    call [phi_imp_GetTickCount64]
    mov [phi_started], eax
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

; -> milliseconds since the program started
OS_GetTicks:
    call [phi_imp_GetTickCount64]
    sub eax, [phi_started]
    ret

; arg: milliseconds
OS_Sleep:
    mov eax, [esp + 4]
    test eax, eax
    jle .done
    push eax
    call [phi_imp_Sleep]
.done:
    ret

OS_Yield:
    call [phi_imp_SwitchToThread]
    ret

; -> this program's process number
OS_CurrentTask:
    call [phi_imp_GetCurrentProcessId]
    ret

; -> what came after the program's name on its command line
OS_Arguments:
    mov eax, phi_arguments
    ret

; esi = a path, edi = room for WIDE_PATH characters -> eax = edi, the path in UTF-16
; there. Changes ecx, esi and edi.
phi_wide:
    push edi
    mov ecx, WIDE_PATH - 1
.char:
    movzx eax, byte [esi]
    test eax, eax
    jz .done
    mov [edi], ax
    inc esi
    add edi, 2
    dec ecx
    jnz .char
.done:
    mov word [edi], 0
    pop eax
    ret

; eax = a path in UTF-16, ebx = what for (GENERIC_READ ...), ecx = how (OPEN_EXISTING ...)
; -> eax = the file's handle, or -1
phi_open:
    push 0
    push FILE_ATTRIBUTE_NORMAL
    push ecx
    push 0
    push FILE_SHARE_ALL
    push ebx
    push eax
    call [phi_imp_CreateFileW]
    ret

; esi = path, eax = offset, edi = buffer, ebx = most bytes -> eax = bytes read, or -1
phi_file_read:
    push eax
    push edi
    mov edi, phi_wide_path
    call phi_wide
    pop edi
    push ebx
    mov ebx, GENERIC_READ
    mov ecx, OPEN_EXISTING
    call phi_open
    pop ebx
    pop ecx                             ; the offset
    cmp eax, -1
    je .failed
    mov esi, eax                        ; the file
    test ecx, ecx
    jz .read
    push 0                              ; from the start
    push 0
    push ecx
    push esi
    call [phi_imp_SetFilePointer]
    cmp eax, -1
    je .close_failed
.read:
    mov dword [phi_total], 0
.more:
    mov eax, [phi_total]
    cmp eax, ebx
    jge .close
    mov ecx, ebx
    sub ecx, eax
    lea edx, [edi + eax]
    push 0
    push phi_read_count
    push ecx
    push edx
    push esi
    call [phi_imp_ReadFile]
    test eax, eax
    jz .close_failed
    mov eax, [phi_read_count]
    test eax, eax
    jz .close                           ; the end of the file
    add [phi_total], eax
    jmp .more
.close:
    push esi
    call [phi_imp_CloseHandle]
    mov eax, [phi_total]
    ret
.close_failed:
    push esi
    call [phi_imp_CloseHandle]
.failed:
    mov eax, -1
    ret

; args: path buffer max -> bytes read, or -1
OS_ReadFile:
    mov esi, [esp + 4]
    xor eax, eax
    mov edi, [esp + 8]
    mov ebx, [esp + 12]
    jmp phi_file_read

; args: path offset buffer max -> bytes read, or -1
OS_ReadFileAt:
    mov esi, [esp + 4]
    mov eax, [esp + 8]
    mov edi, [esp + 12]
    mov ebx, [esp + 16]
    jmp phi_file_read

; esi = path, ebx = GENERIC_WRITE or FILE_APPEND_DATA, ecx = CREATE_ALWAYS or OPEN_ALWAYS,
; edi = data, edx = length -> eax = 1 if written
phi_file_write:
    push edx
    push ecx
    push edi
    mov edi, phi_wide_path
    call phi_wide
    pop edi
    pop ecx
    call phi_open
    pop edx
    cmp eax, -1
    je .failed
    mov esi, eax
    mov ebx, eax
    mov ecx, edi
    call phi_write_all
    push eax
    push esi
    call [phi_imp_CloseHandle]
    pop eax
    ret
.failed:
    xor eax, eax
    ret

; args: path data length -> 1 if written
OS_WriteFile:
    mov esi, [esp + 4]
    mov ebx, GENERIC_WRITE
    mov ecx, CREATE_ALWAYS
    mov edi, [esp + 8]
    mov edx, [esp + 12]
    jmp phi_file_write

; args: path data length -> 1 if added to the end (a new file if there's none)
OS_AppendFile:
    mov esi, [esp + 4]
    mov ebx, FILE_APPEND_DATA
    mov ecx, OPEN_ALWAYS
    mov edi, [esp + 8]
    mov edx, [esp + 12]
    jmp phi_file_write

; arg: path -> its size in bytes, or -1 if it isn't a file
OS_FileSize:
    mov esi, [esp + 4]
    mov edi, phi_wide_path
    call phi_wide
    push phi_attributes
    push 0                              ; the standard information
    push eax
    call [phi_imp_GetFileAttributesExW]
    test eax, eax
    jz .failed
    test byte [phi_attributes], FILE_ATTRIBUTE_DIRECTORY
    jnz .failed
    mov eax, [phi_attributes + 32]      ; its size (the low 32 bits)
    ret
.failed:
    mov eax, -1
    ret

; eax = what a function gave -> 1 if it worked (not 0), else 0
phi_succeeded:
    test eax, eax
    setnz al
    movzx eax, al
    ret

; arg: path -> 1 if deleted
OS_DeleteFile:
    mov esi, [esp + 4]
    mov edi, phi_wide_path
    call phi_wide
    push eax
    call [phi_imp_DeleteFileW]
    jmp phi_succeeded

; arg: path -> 1 if made
OS_MakeFolder:
    mov esi, [esp + 4]
    mov edi, phi_wide_path
    call phi_wide
    push 0
    push eax
    call [phi_imp_CreateDirectoryW]
    jmp phi_succeeded

; arg: path -> 1 if deleted (only an empty folder)
OS_DeleteFolder:
    mov esi, [esp + 4]
    mov edi, phi_wide_path
    call phi_wide
    push eax
    call [phi_imp_RemoveDirectoryW]
    jmp phi_succeeded

; args: from to -> 1 if renamed (or moved)
OS_Rename:
    mov esi, [esp + 8]
    mov edi, phi_wide_to
    call phi_wide
    push eax
    mov esi, [esp + 8]                  ; (one more on the stack now)
    mov edi, phi_wide_path
    call phi_wide
    push eax
    call [phi_imp_MoveFileW]
    jmp phi_succeeded

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
phi_started: dd 0
phi_read_count: dd 0
phi_total: dd 0
phi_heap_start: dd 0
phi_heap_end: dd 0
phi_console_mode: dd 0
phi_attributes: times 36 db 0
phi_wide_path: times WIDE_PATH dw 0
phi_wide_to: times WIDE_PATH dw 0
phi_arguments: times ARGUMENTS_SIZE db 0

; provides: phi_exit OS_ReadKey Bootloader_WaitForKeyPress phi_read_char OS_KeyAvailable OS_Sleep OS_GetTicks OS_Yield OS_CurrentTask OS_Spawn OS_Run OS_Wait OS_Arguments OS_ReadFile OS_WriteFile OS_DeleteFile OS_ListFiles OS_FileSize OS_TaskState OS_TaskName OS_Kill OS_FreeMemory OS_TotalMemory OS_Reboot OS_ClearScreen OS_PutCell OS_IsKeyDown OS_ReadFileAt OS_SoundWrite OS_SoundRate OS_SoundQueued OS_SoundStop OS_SoundVolume OS_Grow OS_ListFolder OS_MakeFolder OS_Rename OS_DeleteFolder OS_SurfaceOpen OS_SurfacePresent OS_NextEvent OS_CopyFont OS_SetWindowTitle OS_OpenWindow OS_WindowFocused OS_SetResizable OS_NetAddress OS_UdpOpen OS_UdpClose OS_UdpReceive OS_UdpSend OS_TcpBegin OS_TcpState OS_TcpSend OS_TcpTake OS_TcpClose OS_TcpListen OS_TcpAccept OS_TcpWaiting OS_TcpAddress OS_TcpStopListening OS_MailOpen OS_MailFind OS_MailSend OS_MailReceive OS_MailWaiting OS_MailClose
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
SYS_READ_FILE_AT equ 31
SYS_SOUND_WRITE equ 32
SYS_SOUND_RATE equ 33
SYS_SOUND_QUEUED equ 34
SYS_SOUND_STOP equ 35
SYS_SOUND_VOLUME equ 36
SYS_GROW      equ 37
SYS_LIST_FOLDER equ 38
SYS_MAKE_FOLDER equ 39
SYS_RENAME    equ 40
SYS_DELETE_FOLDER equ 41
SYS_SURFACE_OPEN equ 64   ; 64 and up: libraries in the kernel (gui.surfaces)
SYS_SURFACE_PRESENT equ 65
SYS_NEXT_EVENT equ 66
SYS_COPY_FONT equ 67
SYS_SET_TITLE equ 68
SYS_OPEN_WINDOW equ 69
SYS_FOCUSED   equ 70
SYS_RESIZABLE equ 71
SYS_NET_ADDRESS equ 80   ; 80 and up: net.calls in the kernel
SYS_UDP_OPEN equ 81
SYS_UDP_CLOSE equ 82
SYS_UDP_RECEIVE equ 83
SYS_UDP_SEND equ 84
SYS_TCP_BEGIN equ 85
SYS_TCP_STATE equ 86
SYS_TCP_SEND equ 87
SYS_TCP_TAKE equ 88
SYS_TCP_CLOSE equ 89
SYS_TCP_LISTEN equ 90
SYS_TCP_ACCEPT equ 91
SYS_TCP_WAITING equ 92
SYS_TCP_ADDRESS equ 93
SYS_TCP_STOP_LISTENING equ 94
SYS_MAIL_OPEN equ 96   ; 96 and up: proc.mail in the kernel
SYS_MAIL_FIND equ 97
SYS_MAIL_SEND equ 98
SYS_MAIL_RECEIVE equ 99
SYS_MAIL_WAITING equ 100
SYS_MAIL_CLOSE equ 101

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

; a system call taking 0-4 arguments from the stack (into ebx, ecx, edx, esi);
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
%if %3 >= 4
    mov esi, [esp + 16]
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
PHI_SYSCALL OS_ReadFileAt, SYS_READ_FILE_AT, 4   ; path offset buffer max -> bytes read, or -1
PHI_SYSCALL OS_SoundWrite, SYS_SOUND_WRITE, 2    ; data length -> bytes taken, or -1 (no sound card)
PHI_SYSCALL OS_SoundRate, SYS_SOUND_RATE, 1      ; frames a second -> the rate the card got, or -1
PHI_SYSCALL OS_SoundQueued, SYS_SOUND_QUEUED, 0  ; -> bytes still to play, or -1
PHI_SYSCALL OS_SoundStop, SYS_SOUND_STOP, 0      ; -> 0, or -1
PHI_SYSCALL OS_SoundVolume, SYS_SOUND_VOLUME, 1  ; percent -> 0, or -1
PHI_SYSCALL OS_Grow, SYS_GROW, 1                 ; bytes -> where the new memory starts, or 0
PHI_SYSCALL OS_ListFolder, SYS_LIST_FOLDER, 3    ; path into max -> FileInfo entries written, or -1
PHI_SYSCALL OS_MakeFolder, SYS_MAKE_FOLDER, 1    ; path -> 1 if made
PHI_SYSCALL OS_Rename, SYS_RENAME, 2             ; from to -> 1 if renamed (or moved)
PHI_SYSCALL OS_DeleteFolder, SYS_DELETE_FOLDER, 1 ; path -> 1 if deleted (only an empty folder)
PHI_SYSCALL OS_SurfaceOpen, SYS_SURFACE_OPEN, 2  ; width height -> 1, or 0
PHI_SYSCALL OS_SurfacePresent, SYS_SURFACE_PRESENT, 1   ; pixels -> 1, or 0
PHI_SYSCALL OS_NextEvent, SYS_NEXT_EVENT, 1      ; event -> 1, or 0 if there's none
PHI_SYSCALL OS_CopyFont, SYS_COPY_FONT, 1        ; glyphs (4096 bytes) -> 1, or 0
PHI_SYSCALL OS_SetWindowTitle, SYS_SET_TITLE, 1  ; title -> 1
PHI_SYSCALL OS_OpenWindow, SYS_OPEN_WINDOW, 2    ; path arguments -> process number, or -1
PHI_SYSCALL OS_WindowFocused, SYS_FOCUSED, 0     ; -> 1 if its window is in front
PHI_SYSCALL OS_SetResizable, SYS_RESIZABLE, 1    ; 1 or 0 -> 1
PHI_SYSCALL OS_NetAddress, SYS_NET_ADDRESS, 0   ; -> this machine's address, or 0 if the kernel has no network
PHI_SYSCALL OS_UdpOpen, SYS_UDP_OPEN, 2   ; port queue -> socket, or -1
PHI_SYSCALL OS_UdpClose, SYS_UDP_CLOSE, 1   ; socket -> 1, or 0 if it isn't ours
PHI_SYSCALL OS_UdpReceive, SYS_UDP_RECEIVE, 2   ; socket datagram -> 1, or 0 if none is waiting
PHI_SYSCALL OS_UdpSend, SYS_UDP_SEND, 4   ; socket|port<<16 to data length -> 1 if sent
PHI_SYSCALL OS_TcpBegin, SYS_TCP_BEGIN, 2   ; to port -> connection (still connecting), or -1
PHI_SYSCALL OS_TcpState, SYS_TCP_STATE, 1   ; connection -> 1 open, 0 connecting, -1 failed
PHI_SYSCALL OS_TcpSend, SYS_TCP_SEND, 3   ; connection data length -> bytes taken, or -1
PHI_SYSCALL OS_TcpTake, SYS_TCP_TAKE, 3   ; connection buffer max -> bytes, 0 none yet, -2 closed and read, -1 broken
PHI_SYSCALL OS_TcpClose, SYS_TCP_CLOSE, 1   ; connection -> 1
PHI_SYSCALL OS_TcpListen, SYS_TCP_LISTEN, 2   ; port backlog -> listener, or -1
PHI_SYSCALL OS_TcpAccept, SYS_TCP_ACCEPT, 1   ; listener -> connection, or -1 if none is waiting
PHI_SYSCALL OS_TcpWaiting, SYS_TCP_WAITING, 1   ; connection -> bytes to read, or -1 once closed
PHI_SYSCALL OS_TcpAddress, SYS_TCP_ADDRESS, 1   ; connection -> the other end's address
PHI_SYSCALL OS_MailOpen, SYS_MAIL_OPEN, 3   ; name size capacity -> box, or -1
PHI_SYSCALL OS_MailFind, SYS_MAIL_FIND, 1   ; name -> box, or -1
PHI_SYSCALL OS_MailSend, SYS_MAIL_SEND, 2   ; box record -> 1 if it went in
PHI_SYSCALL OS_MailReceive, SYS_MAIL_RECEIVE, 2   ; box into -> the sender + 1, or 0 if none is waiting
PHI_SYSCALL OS_MailWaiting, SYS_MAIL_WAITING, 1   ; box -> records waiting, or -1
PHI_SYSCALL OS_MailClose, SYS_MAIL_CLOSE, 1   ; box -> 1, or 0 if it isn't ours
PHI_SYSCALL OS_TcpStopListening, SYS_TCP_STOP_LISTENING, 1   ; listener -> 1, or 0 if it isn't ours

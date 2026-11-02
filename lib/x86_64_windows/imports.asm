; provides: phi_imports
;
; The import table of a 64-bit Windows program (PE32+), as x86_32_windows/imports.asm's:
; kernel32.dll, and the functions of it this library calls as [rel phi_imp_NAME]. Each entry
; is 8 bytes here. A function takes its first four arguments in rcx, rdx, r8 and r9 (the
; rest on the stack), wants 32 bytes of room above its return address that it may write
; (its shadow space) and the stack 16-byte aligned when it's called, and gives its result in
; rax; it keeps rbx, rbp, rdi, rsi, rsp and r12 to r15, and can change rax, rcx, rdx and
; r8 to r11. PHI_CALL (in system.asm) does the aligning. Every Windows program gets the
; whole list.
%macro PHI_KERNEL32 0
    PHI_FUNCTION GetStdHandle
    PHI_FUNCTION WriteFile
    PHI_FUNCTION ReadFile
    PHI_FUNCTION ExitProcess
    PHI_FUNCTION GetCommandLineA
    PHI_FUNCTION GetTickCount64
    PHI_FUNCTION Sleep
    PHI_FUNCTION SwitchToThread
    PHI_FUNCTION GetCurrentProcessId
    PHI_FUNCTION VirtualAlloc
    PHI_FUNCTION GlobalMemoryStatusEx
    PHI_FUNCTION CreateFileW
    PHI_FUNCTION CloseHandle
    PHI_FUNCTION SetFilePointer
    PHI_FUNCTION GetFileAttributesExW
    PHI_FUNCTION DeleteFileW
    PHI_FUNCTION CreateDirectoryW
    PHI_FUNCTION RemoveDirectoryW
    PHI_FUNCTION MoveFileW
    PHI_FUNCTION GetFileType
    PHI_FUNCTION PeekNamedPipe
    PHI_FUNCTION WaitForSingleObject
    PHI_FUNCTION GetConsoleMode
    PHI_FUNCTION SetConsoleMode
    PHI_FUNCTION AddVectoredExceptionHandler
    PHI_FUNCTION GetSystemTimeAsFileTime
%endmacro

align 8, db 0
phi_imports:
    dd phi_kernel32_lookup - $$, 0, 0, phi_kernel32_name - $$, phi_kernel32_addresses - $$
    times 5 dd 0                        ; the end of the table
phi_imports_size equ $ - phi_imports

; what the program wants from kernel32.dll: each function's name
%macro PHI_FUNCTION 1
    dq phi_name_%1 - $$
%endmacro
align 8, db 0
phi_kernel32_lookup:
    PHI_KERNEL32
    dq 0
%unmacro PHI_FUNCTION 1

; where Windows puts their addresses
%macro PHI_FUNCTION 1
phi_imp_%1: dq phi_name_%1 - $$
%endmacro
phi_kernel32_addresses:
    PHI_KERNEL32
    dq 0
%unmacro PHI_FUNCTION 1

%macro PHI_FUNCTION 1
%defstr PHI_FUNCTION_NAME %1
phi_name_%1:
    dw 0
    db PHI_FUNCTION_NAME, 0
    align 2, db 0
%endmacro
    PHI_KERNEL32
%unmacro PHI_FUNCTION 1

phi_kernel32_name: db 'kernel32.dll', 0

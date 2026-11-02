; provides: phi_imports
;
; The import table of a Windows program: kernel32.dll, and the functions of it this
; library calls as [phi_imp_NAME]. Windows has no fixed system call numbers, so a
; program asks for functions by name and Windows writes their addresses here when it
; loads the program. Each takes its arguments on the stack, pushed last first, and
; removes them itself (stdcall); it keeps ebx, esi, edi and ebp, and can change eax,
; ecx and edx. Every Windows program gets the whole list.
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

align 4, db 0
phi_imports:
    dd phi_kernel32_lookup - $$, 0, 0, phi_kernel32_name - $$, phi_kernel32_addresses - $$
    times 5 dd 0                        ; the end of the table
phi_imports_size equ $ - phi_imports

; what the program wants from kernel32.dll: each function's name
%macro PHI_FUNCTION 1
    dd phi_name_%1 - $$
%endmacro
phi_kernel32_lookup:
    PHI_KERNEL32
    dd 0
%unmacro PHI_FUNCTION 1

; where Windows puts their addresses
%macro PHI_FUNCTION 1
phi_imp_%1: dd phi_name_%1 - $$
%endmacro
phi_kernel32_addresses:
    PHI_KERNEL32
    dd 0
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

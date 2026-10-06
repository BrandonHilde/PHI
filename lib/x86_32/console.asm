; provides: phi_print phi_console_putc
; requires: phi_serial_putc
; init: phi_console_init
;
; Text output for 32-bit kernels, without the BIOS: writes to the VGA text
; screen at 0xB8000 (80x25, light gray on black) and to COM1. \n starts a new
; line, \r goes back to the start of the line; the screen scrolls at the bottom.
CONSOLE_VGA    equ 0xB8000
CONSOLE_COLS   equ 80
CONSOLE_ROWS   equ 25
CONSOLE_COLOR  equ 0x07
CONSOLE_INFO   equ 0x9000       ; BootInfo: the loader saved the BIOS's cursor there

; carry on below the BIOS's messages
phi_console_init:
    mov eax, [CONSOLE_INFO + 16]
    mov [phi_console_row], eax
    mov eax, [CONSOLE_INFO + 20]
    mov [phi_console_col], eax
    ret

; print the zero-terminated string at esi
phi_print:
    lodsb
    test al, al
    jz .done
    call phi_console_putc
    jmp phi_print
.done:
    ret

; print the character in al; preserves all registers
phi_console_putc:
    pushad
    call phi_serial_putc
    cmp al, 10
    je .newline
    cmp al, 13
    je .return

    mov ebx, [phi_console_row]
    imul ebx, ebx, CONSOLE_COLS
    add ebx, [phi_console_col]
    mov ah, CONSOLE_COLOR
    mov [CONSOLE_VGA + ebx * 2], ax
    inc dword [phi_console_col]
    cmp dword [phi_console_col], CONSOLE_COLS
    jb .cursor
.newline:
    inc dword [phi_console_row]
.return:
    mov dword [phi_console_col], 0

    cmp dword [phi_console_row], CONSOLE_ROWS
    jb .cursor
    ; scroll: move rows 1-24 up, blank the last row
    mov esi, CONSOLE_VGA + CONSOLE_COLS * 2
    mov edi, CONSOLE_VGA
    mov ecx, CONSOLE_COLS * (CONSOLE_ROWS - 1)
    rep movsw
    mov ax, (CONSOLE_COLOR << 8) | ' '
    mov ecx, CONSOLE_COLS
    rep stosw
    mov dword [phi_console_row], CONSOLE_ROWS - 1

.cursor:
    ; move the hardware cursor (CRT controller registers 0x0E and 0x0F)
    mov ebx, [phi_console_row]
    imul ebx, ebx, CONSOLE_COLS
    add ebx, [phi_console_col]
    mov dx, 0x3D4
    mov al, 0x0F
    out dx, al
    inc dx
    mov al, bl
    out dx, al
    dec dx
    mov al, 0x0E
    out dx, al
    inc dx
    mov al, bh
    out dx, al
    popad
    ret

phi_console_row: dd 0
phi_console_col: dd 0

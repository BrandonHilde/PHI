; bootloader.asm
BITS 16
org 0x7c00    
start:
    ; Set up segment registers
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7c00

    call loop_start

    mov si, phi_msg
    call print_string

    mov si, next_msg
    call print_string

    ; Wait for key press
    mov ah, 0x00
    int 0x16

    mov ah, 0x02    ; BIOS read sector function
    mov al, 1       ; Number of sectors to read
    mov ch, 0       ; Cylinder number
    mov dh, 0       ; Head number
    mov cl, 2       ; Sector number (1-based, sector 1 is the boot sector)
    mov bx, 0x7E00  ; Load address (just after boot sector)
    int 0x13        ; BIOS interrupt

    ;video mode
    ;mov ax, 0x13
    ;int 0x10

    jmp 0x7E00 ; jump to sector two

print_string:
    mov ah, 0x0E
.loop:
    lodsb
    cmp al, 0
    je .done
    int 0x10
    jmp .loop
.done:
    ret


loop_content:
    ;print hello world
    mov si, hello
    call print_string
loop_start:
    mov cx, [loop_count]
.loop_continue:
    cmp cx, [loop_limit]
    jge .done
    call .loop_check
    call loop_content
.loop_check:
    inc cx
    jmp .loop_continue
.done:
    mov [loop_count], cx
    ret

loop_limit dw 3
loop_count dw 0
hello db 'Hello, World', 13, 10, 0
phi_msg db 'Welcome to PHI', 13, 10, 0
next_msg db 'Press any key to continue...', 13, 10, 0
times 510-($-$$) db 0
dw 0xaa55
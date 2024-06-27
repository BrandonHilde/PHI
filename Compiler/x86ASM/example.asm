; bootloader.asm
BITS 16
org 0x7c00    
start:
    ; Set up segment registers
    ; Set up segment registers
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7c00

    ;print hello world
    mov si, hello
    call print_string

    mov si, phiMsg
    call print_string

    ; Wait for key press
    mov ah, 0x00
    int 0x16

    cli
    hlt

print_string:
    xor bh, bh

    mov ah, 0x0E
    lodsb

    cmp al, $0
    je .done
    
    int 0x10
    jmp print_string
.done:
    ret

hello db 'Hello, World', 13, 10, 0
phiMsg db 'PHI', 13, 10, 0

times 510-($-$$) db 0
dw 0xaa55
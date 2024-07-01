; second sector
BITS 16
org 0x7E00

second_sector_start:

    mov si, welcome_msg
    call print_string

    ; Wait for key press
    mov ah, 0x00
    int 0x16

    ; Halt the system
    cli
    hlt

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

welcome_msg db 'Welcome to sector two', 13, 10, 0

times 512-($-$$) db 0  ; Pad to 512 bytes
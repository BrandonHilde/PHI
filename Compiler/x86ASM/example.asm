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

    mov si, phiMsg
    call print_string

    ; Wait for key press
    mov ah, 0x00
    int 0x16

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

loop_limit dw 20
loop_count dw 0
hello db 'Hello, World', 13, 10, 0
phiMsg db 'PHI', 13, 10, 0

times 510-($-$$) db 0
dw 0xaa55
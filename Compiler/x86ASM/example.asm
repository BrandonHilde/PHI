; keyboard_driver.asm
[bits 16]
[org 0x7c00]

start:
    ; Set up the interrupt handler
    cli                     ; Disable interrupts
    mov ax, 0
    mov es, ax
    mov word [es:9*4], keyboard_handler
    mov [es:9*4+2], cs
    sti                     ; Enable interrupts

    ; Main loop
main_loop:
    hlt                     ; Halt the CPU until an interrupt occurs
    jmp main_loop

keyboard_handler:
    push ax
    push bx

    in al, 0x60             ; Read scan code from keyboard controller
    mov bl, al
    
    ; Convert scan code to ASCII (simplified)
    mov bx, scan_code_table
    xlat

    ; Print the character
    mov ah, 0x0e            ; BIOS teletype output
    int 0x10

    mov al, 0x20            ; Send EOI (End of Interrupt) to PIC
    out 0x20, al

    pop bx
    pop ax
    iret

scan_code_table:
    db 0, 0, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 0, 0
    db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 0, 0
    db 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', "'", '`', 0, '\\'
    db 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '

times 510-($-$$) db 0
dw 0xaa55
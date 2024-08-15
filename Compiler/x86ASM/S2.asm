; pit_example.asm
BITS 16
ORG 0x7C00

start:
    ; Set up segments
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7C00

    mov si, msg
    call print_string  

    ; Set up PIT
    cli                     ; Disable interrupts
    mov al, 00110100b       ; Channel 0, lobyte/hibyte, rate generator
    out 0x43, al            ; Send to PIT command port
    
     mov si, msg
    call print_string

    mov ax, 11932           ; Set frequency to ~100 Hz (1193182 / 100)
    out 0x40, al            ; Send low byte
    mov al, ah
    out 0x40, al            ; Send high byte

     mov si, msg
    call print_string

    ; Set up interrupt handler
    mov word [0x0020], timer_interrupt  ; Set offset
    mov word [0x0022], 0x0000           ; Set segment

    mov si, msg
    call print_string

    ; Enable interrupts
    sti

main_loop:
    hlt                     ; Halt until next interrupt
    jmp main_loop

timer_interrupt:
    mov si, hello
    call print_string          
    mov al, 0x20            ; End of Interrupt (EOI)
    out 0x20, al            ; Send EOI to PIC
    iret

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

msg db 'hi', 0
hello db 'hello',0
times 510-($-$$) db 0
dw 0xAA55
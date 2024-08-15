; bootloader.asm
org 0x7c00    
start:
    ; Set up segment registers
    xor ax, ax
    mov ds, ax
    mov es, ax
    mov ss, ax
    mov sp, 0x7c00

    mov si, phi_msg
    call print_stringQ

    mov ah, 0x02    ; BIOS read sector function
    mov al, 1       ; Number of sectors to read
    mov ch, 0       ; Cylinder number
    mov dh, 0       ; Head number
    mov cl, 2       ; Sector number (1-based, sector 1 is the boot sector)
    mov bx, 0x7E00  ; Load address (just after boot sector)
    int 0x13        ; BIOS interrupt

    jc error        ; If carry flag is set, there was an error

    ; video
    mov ax, 0x13
    int 0x10


    jmp 0x7E00      ; Jump to sector two

error:
    push ax         ; Save AX (contains error code in AH)
    mov si, error_msg
    call print_stringQ
    
    pop ax          ; Restore AX
    mov al, ah      ; Move error code to AL for printing
    call print_hex  ; New routine to print hexadecimal value
    
    jmp $

print_hex:
    push ax
    push cx
    mov ah, 0x0E
    mov cx, 4       ; Loop 4 times for each hex digit
.loop:
    rol al, 4       ; Rotate left by 4 bits
    mov bl, al
    and bl, 0x0F    ; Mask off low nibble
    add bl, '0'     ; Convert to ASCII
    cmp bl, '9'
    jle .print
    add bl, 7       ; Adjust for A-F
.print:
    mov al, bl
    int 0x10        ; Print the character
    loop .loop
    pop cx
    pop ax
    ret
print_stringQ:
    mov ah, 0x0E
.loop:
    lodsb
    cmp al, 0
    je .done
    int 0x10
    jmp .loop
.done:
    ret

VALUE_name times 40 db 0
error_msg db 'Error loading sector 2', 13, 10, 0
hello db 'Hello, World', 13, 10, 0
phi_msg db 'Welcome to PHI', 13, 10, 0
next_msg db 'Press any key to continue...', 13, 10, 0
times 510-($-$$) db 0
dw 0xaa55
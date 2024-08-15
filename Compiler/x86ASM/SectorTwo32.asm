[bits 16]
[org 0x7E00]

start_it:

    cli
; Set up the PIT
    mov al, 00110100b    ; Channel 0, lobyte/hibyte, rate generator
    out 0x43, al
    
    ; Set the divisor
    mov ax, 11932
    out 0x40, al    ; Low byte
    mov al, ah
    out 0x40, al    ; High byte

    ; Set up the timer ISR
    mov word [0x0020], timer_interrupt
    mov word [0x0022], 0x7E00  

    ; Enable interrupts
    sti
    ; Set up the stack
    mov bp, 0x9000      ; Set base pointer to 0x9000
    mov sp, bp          ; Set stack pointer to the same value as base pointer

    jmp switch_to_pm    ; Jump to the switch_to_pm procedure

; GDT (Global Descriptor Table)
gdt_start:
    dq 0x0              ; Null descriptor (required)
gdt_code:
    dw 0xffff           ; Limit (bits 0-15)
    dw 0x0              ; Base (bits 0-15)
    db 0x0              ; Base (bits 16-23)
    db 10011010b        ; Access byte
    db 11001111b        ; Flags and Limit (bits 16-19)
    db 0x0              ; Base (bits 24-31)
gdt_data:
    dw 0xffff           ; Limit (bits 0-15)
    dw 0x0              ; Base (bits 0-15)
    db 0x0              ; Base (bits 16-23)
    db 10010010b        ; Access byte
    db 11001111b        ; Flags and Limit (bits 16-19)
    db 0x0              ; Base (bits 24-31)
gdt_end:                ; Label to mark the end of the GDT

gdt_descriptor:
    dw gdt_end - gdt_start - 1    ; Size of the GDT
    dd gdt_start                  ; Address of the GDT

CODE_SEG equ gdt_code - gdt_start ; Calculate offset of code segment
DATA_SEG equ gdt_data - gdt_start ; Calculate offset of data segment

; Switch to protected mode
switch_to_pm:
    cli                 ; Disable interrupts
    lgdt [gdt_descriptor]   ; Load the GDT descriptor
    mov eax, cr0        ; Move contents of cr0 to eax
    or eax, 0x1         ; Set the protected mode bit
    mov cr0, eax        ; Move the modified value back to cr0

    jmp dword CODE_SEG:init_pm    ; Far jump to 32-bit code segment

[bits 32]
init_pm:
    mov ax, DATA_SEG
    mov ds, ax
    mov ss, ax
    mov es, ax
    mov fs, ax
    mov gs, ax

    mov ebp, 0x90000
    mov esp, ebp

    call BEGIN_PM

; Start of protected mode code
BEGIN_PM:
    call clear_screen

    mov eax, SCREEN_WIDTH
    mov ecx, SCREEN_HEIGHT
    mul ecx
    add eax, DRAW_START
    mov [BUFFER_SIZE], eax

    ; set square x coord
    mov eax, 300
    mov [SQX], eax

    ; set square y coord
    mov eax, 180
    mov [SQY], eax


    jmp main_loop

; PIT-related constants
PIT_COMMAND    equ 0x43
PIT_CHANNEL_0  equ 0x40
PIT_FREQUENCY  equ 1193180  ; Base frequency of the PIT
DESIRED_FREQ   equ 100      ; Desired interrupt frequency (Hz)

; Calculate the divisor for the desired frequency
DIVISOR        equ PIT_FREQUENCY / DESIRED_FREQ

setup_pit:
    cli
    ; Set up the PIT
    mov al, 00110100b    ; Channel 0, lobyte/hibyte, rate generator
    out 0x43, al
    
    ; Set the divisor
    mov ax, 11932
    out 0x40, al    ; Low byte
    mov al, ah
    out 0x40, al    ; High byte

    ; Set up the timer ISR
    mov word [0x0020], timer_interrupt
    mov word [0x0022], 0x0000  

    ; Enable interrupts
    sti

    ret
main_loop:
    hlt                     ; Halt until next interrupt
    jmp main_loop
timer_interrupt:
    
    call move_square

    ; Send EOI (End of Interrupt) to PIC
    mov al, 0x20
    out 0x20, al
    iret

move_square:
    ; set square x coord
    mov eax, [SQX]
    inc eax
    mov [SQX], eax

    call draw_square
    ret

draw_square:
    mov edi, DRAW_START  ; Start of VGA memory for mode 13h (320x200)
    mov eax, [SQY]
    mov ecx, SCREEN_WIDTH
    mul ecx
    add eax, [SQX]
    add edi, eax
    mov edx, 0       ; Y counter

.draw_row:
    mov ecx, 0      ; Reset X counter

.draw_pixel:
    cmp edi, [BUFFER_SIZE]
    jl .continue_draw
    mov edi, DRAW_START

.continue_draw:
    mov byte [edi], 4 ; Set pixel color to red (color index 4 in default palette)
    inc edi
    inc ecx
    cmp ecx, [SQ_WIDTH]
    jl .draw_pixel

    add edi, SCREEN_WIDTH
    sub edi, [SQ_WIDTH] ;Move to the next row (320 - square width)
    inc edx
    cmp edx, [SQ_HEIGHT]
    jl .draw_row

    ret

clear_screen:
    push eax
    push ecx
    push edi

    mov edi, DRAW_START
    xor eax, eax
    mov ecx, SCREEN_WIDTH * SCREEN_HEIGHT

    rep stosb

    pop edi
    pop ecx
    pop eax
    ret

; Data
SQX dd 10
SQY dd 10
SQ_WIDTH dd 50
SQ_HEIGHT dd 50

BUFFER_SIZE dd 0

DRAW_START equ 0xA0000
SCREEN_WIDTH equ 320
SCREEN_HEIGHT equ 200
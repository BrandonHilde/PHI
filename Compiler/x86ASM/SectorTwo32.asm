[bits 16]
[org 0x7E00]

start_it:

    ; Set up the stack
    mov bp, 0x9000
    mov sp, bp

    mov si, REAL_MODE_MSG
    call print_string

    call switch_to_pm

print_string:
    mov ah, 0x0E
.loop:
    lodsb
    cmp al, 0 ;check for 0
    je .done
    int 0x10
    jmp .loop
.done:
    ret

; GDT
gdt_start:
    dq 0x0
gdt_code:
    dw 0xffff
    dw 0x0
    db 0x0
    db 10011010b
    db 11001111b
    db 0x0
gdt_data:
    dw 0xffff
    dw 0x0
    db 0x0
    db 10010010b
    db 11001111b
    db 0x0
gdt_end:

gdt_descriptor:
    dw gdt_end - gdt_start - 1
    dd gdt_start

CODE_SEG equ gdt_code - gdt_start
DATA_SEG equ gdt_data - gdt_start

; Switch to protected mode
switch_to_pm:
    cli
    lgdt [gdt_descriptor]
    mov eax, cr0
    or eax, 0x1
    mov cr0, eax

    jmp dword CODE_SEG:init_pm

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

    call draw_square

    jmp $



draw_square:
    mov edi, DRAW_START  ; Start of VGA memory for mode 13h (320x200)
    xor eax, eax
    xor ecx, ecx
    mov eax, [SQY]
    mov ecx, SCREEN_WIDTH
    mul ecx
    add eax, [SQX]

    add edi, eax
    mov edx, 0       ; Y counter

.draw_row:
    mov ecx, 0      ; Reset X counter

.check_value:
    cmp edi, [BUFFER_SIZE]
    jl .draw_pixel
    mov edi, DRAW_START

.draw_pixel:

   
    mov byte [edi], 4 ; Set pixel color to red (color index 4 in default palette)
    inc edi
    inc ecx
    cmp ecx, [SQ_WIDTH]
    jl .draw_pixel

    add edi, SCREEN_WIDTH ; 
    sub edi, [SQ_WIDTH] ;Move to the next row (320 - square width)
    inc edx
    cmp edx, [SQ_HEIGHT]
    jl .draw_row

    ret

clear_screen:
    push eax
    push ecx
    push edi

    mov edi, 0xB8000      ; Video memory address
    mov ax, 0x0720        
    mov ecx, 2000         

    rep stosw             

    pop edi
    pop ecx
    pop eax
    ret
; Protected mode print function
print_string32:
    pusha
    mov edx, 0xb8000 ; Video memory address
.loop:
    mov al, [esi]    ; load one byte from esi
    mov ah, 0x0F     ; white on black
    cmp al, 0        ; check for 0 and end
    je .done
    mov [edx], ax    ; write character
    add esi, 1      
    add edx, 2       
    jmp .loop
.done:
    popa
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
REAL_MODE_MSG db 'Started in 16-bit Real Mode', 0
PROTECTED_MODE_MSG db 'Now in 32-bit Protected Mode', 0
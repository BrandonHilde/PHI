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

    mov edi, 0xA0000  ; Start of VGA memory for mode 13h (320x200)
    mov byte [edi + (30 * 320) + 32], 0x04 

    jmp $


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
REAL_MODE_MSG db 'Started in 16-bit Real Mode', 0
PROTECTED_MODE_MSG db 'Now in 32-bit Protected Mode', 0
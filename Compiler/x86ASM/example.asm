
;ASMx86 CODE

ORG 0x7c00

;{CONSTANTS}

start:
   xor ax, ax
   mov ds, ax
   mov es, ax
   mov ss, ax
   mov sp, 0x7c00

   mov si, VALUE_0
   call print_log

   ;mov al, [scan_code_table + bx]

   ;mov bl, byte [KeyCodeValue]
   ;add bx, key_down_table
   ;mov byte [key_down_table + bx], 0x01

   mov dword [VALUE_array + ebx], eax
   mov eax, [VALUE_array + ebx]

;{CODE}

   jmp $

print_int:
    push bp
    mov bp, sp
    push dx

    cmp ax, 10
    jge .div_num

    add al, '0'
    mov ah, 0x0E
    int 0x10
    jmp .done

.div_num:
    xor dx, dx
    mov bx, 10
    div bx
    push dx
    call print_int
    pop dx
    add dl, '0'
    mov ah, 0x0E
    mov al, dl
    int 0x10

.done:
    pop dx
    mov sp, bp
    pop bp
    ret

print_log:
   mov ah, 0x0E
.loop:
   lodsb
   cmp al, 0
   je .done
   int 0x10
   jmp .loop
.done:
   ret

;{INCLUDE}

VALUE_array: times 20 dd 0 
VALUE_newline db '',13,10,'',0
VALUE_i dd 12
VALUE_y dd 7
VALUE_t dd 3
VALUE_val dd 0
VALUE_0 db 'test',0
VALUE_1 db 'hello',0
;{VARIABLE}
times 510-($-$$) db 0
dw 0xaa55
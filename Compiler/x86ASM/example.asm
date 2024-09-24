ORG 0x7c00

;{CONSTANTS}

start:
   xor ax, ax
   mov ds, ax
   mov es, ax
   mov ss, ax
   mov sp, 0x7c00

   mov si, VALUE_hello
   call print_log
   mov si, VALUE_newline
   call print_log

   mov eax, [VALUE_value]
   call prep_convert
   call convert_int_to_str

   mov si, VALUE_str_buffer
   call print_log
;{CODE}

   jmp $

prep_convert:
   mov cl, 0
   mov [VALUE_str_index], cl
   ret
convert_int_to_str:
   cmp eax, 10
   jge .div_num

   jmp .store_value
.div_num:
   xor edx, edx
   mov ebx, 10
   div ebx
   push edx
   call convert_int_to_str
   pop edx
   mov al, dl

.store_value:
   add al, '0' 

   push ebx 
   mov ebx, VALUE_str_buffer
   mov cl, [VALUE_str_index]
   add ebx, ecx
   mov byte [ebx], al
   inc cl
   mov [VALUE_str_index], cl
   pop ebx
    
.done:
   ret

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

VALUE_int_to_str_buffer db 0,0,0,0,0,0,0,0,0,0,0
VALUE_int_to_str_index db 0
VALUE_newline db 13,10,0
VALUE_hello db 'hello, world',0
VALUE_value dd 21
;{VARIABLE}
times 510-($-$$) db 0
dw 0xaa55

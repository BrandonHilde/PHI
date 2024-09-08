ORG 0x7c00

;{CONSTANTS}

start:
   xor ax, ax
   mov ds, ax
   mov es, ax
   mov ss, ax
   mov sp, 0x7c00

   mov ax, word [VALUE_testI + 5]
   call print_int
   mov si, VALUE_newline
   call print_log

   pop eax


   mov [VALUE_val], eax

   mov ax, word [VALUE_val]
   call print_int
   mov si, VALUE_newline
   call print_log

   mov eax, [VALUE_y]
   mov [VALUE_testI + 3], eax

   mov ax, word [VALUE_y]
   call print_int
   mov si, VALUE_newline
   call print_log

   pop eax


   mov [VALUE_val], eax

   mov ax, word [VALUE_val]
   call print_int
   mov si, VALUE_newline
   call print_log
   call IF_0
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

IF_0:
   mov eax, val is > 0
   cmp eax, 1
   je IF_0_CONTENT

;{ELSE CONTENT}

   ret
IF_0_CONTENT:
   mov si, VALUE_1
   call print_log
;{CODE}
   mov si, VALUE_newline
   call print_log
;{CODE}
   mov ax, word [VALUE_val]
   call print_int
;{CODE}
;{IF CONTENT}
   ret
;{INCLUDE}

VALUE_newline db 13,10,0
VALUE_testarr db 'hi','hello','maybe','hmm',0
VALUE_testI dd 1,5,3,8,9,9,3,2
VALUE_i dd 12
VALUE_y dd 7
VALUE_t dd 3
VALUE_val dd 0
VALUE_0 dd 5
VALUE_1 db 'hello',0
;{VARIABLE}
times 510-($-$$) db 0
dw 0xaa55

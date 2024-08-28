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
   mov di, VALUE_name
   call get_input
   mov si, VALUE_newline
   call print_log
   mov si, VALUE_1
   call print_log
   mov si, VALUE_name
   call print_log
   mov si, VALUE_newline
   call print_log
   mov si, VALUE_2
   call print_log

   mov eax, 3


   mov ebx, [VALUE_x]


   add eax,ebx


   push eax


   mov eax, [VALUE_y]


   mov ebx, 4


   sub eax,ebx


   push eax


   mov eax, 6


   pop ebx


   add eax,ebx


   push eax


   pop eax


   pop ebx


   add eax,ebx


   push eax


   pop eax


   mov ebx, 2


   sub eax,ebx


   push eax


   pop eax


   mov [VALUE_m], eax

   mov ax, word [VALUE_m]
   call print_int

   mov eax, [VALUE_y]
   mov ecx,3
   mul ecx
   mov [VALUE_y], eax

   mov si, VALUE_newline
   call print_log
   mov ax, word [VALUE_y]
   call print_int

   mov eax, [VALUE_y]
   mov ebx,2
   div ebx
   mov [VALUE_y], eax

   mov si, VALUE_newline
   call print_log
   mov ax, word [VALUE_y]
   call print_int

   mov eax, [VALUE_y]
   mov ebx,4
   div ebx
   mov [VALUE_y], edx

   mov si, VALUE_newline
   call print_log
   mov ax, word [VALUE_y]
   call print_int
    call  WaitForKeyPress
    call  EnableVideoMode
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

get_input:
   xor cx, cx
.loop:
    mov ah, 0
    int 0x16
    cmp al, 0x0D
    je .done
    stosb
    inc cx
    mov ah, 0x0E
    int 0x10
    jmp .loop

.done:
    mov byte [di], 0
    ret
OS16BIT_WaitForKeyPress:
   mov ah, 0x00
   int 0x16
   ret
WaitForKeyPress:

        mov ah, 0x00
        int 0x16


EnableVideoMode:

        mov ax, 0x13
        int 0x10


;{INCLUDE}

VALUE_hello db 'Hello, World!',13,10,'',0
VALUE_newline db '',13,10,'',0
VALUE_name: times 40 db 0
VALUE_x dd 10
VALUE_y dd 45
VALUE_m dd 0
VALUE_0 db 'What is your name: ',0
VALUE_1 db 'hello: ',0
VALUE_2 db 'Press any key to continue...',0
;{VARIABLE}
times 510-($-$$) db 0
dw 0xaa55
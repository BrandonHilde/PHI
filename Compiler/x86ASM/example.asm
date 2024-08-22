;ASMx86 CODE

ORG 0x7c00

;{CONSTANTS}

start:
   xor ax, ax
   mov ds, ax
   mov es, ax
   mov ss, ax
   mov sp, 0x7c00

   call  OS16BIT_SetupKeyboardInterupt
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
OS16BIT_WaitForKeyPress:
   mov ah, 0x00
   int 0x16
   ret
keyboard_handler:
   push ax
   push bx
   in al, 0x60             ; Read scan code
   test al, 0x80
   jz .key_down
   ;key up event
   and al, 0x7F            ; Clear the key release bit
   xor bh, bh
   mov bl, al
   mov al, [scan_code_table + bx]  ; Convert scan code to ASCII

   mov [KeyCodeValue], al

   mov bx, key_down_table
   add bx, [KeyCodeValue]

   mov byte [bx], 0

   jmp .done
   
   cmp al, 0               ; Check if it's a valid key
   je .done

.key_down:
   xor bh, bh
   mov bl, al
   ; Convert scan code to ASCII (simplified)
   mov al, [scan_code_table + bx]

   mov [KeyCodeValue], al

   mov bx, key_down_table
   add bx, [KeyCodeValue]

   mov byte [bx], 1
.done:
   call OS16BIT_KeyboardEvent
   mov al, 0x20            ; Send End of Interrupt
   out 0x20, al
   pop bx
   pop ax
   iret
OS16BIT_SetupKeyboardInterupt:
   ; Set up the interrupt handler
   cli                     ; Disable interrupts
   mov ax, 0
   mov es, ax
   mov word [es:9*4], keyboard_handler
   mov [es:9*4+2], cs
   sti                     ; Enable interrupts
   ret

OS16BIT_KeyboardEvent:

   mov al, [KeyCodeValue]
   mov byte [VALUE_key], al
   mov ax, word [VALUE_key]

   mov si, VALUE_key
   call print_log

   mov si, VALUE_newline
   call print_log

   mov bx, [KeyCodeValue]
   add bx, key_down_table
   mov byte ax, [bx]
   call print_int

   ret

;{INCLUDE}

key_down_table: times 120 db 0
            

VALUE_last_key db 0

scan_code_table:
   db 0, 0, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 0, 0
   db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 0, 0
   db 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', "'", '`', 0, '\'
   db 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '
KeyCodeValue db 0
VALUE_keyL dw 0
VALUE_newline db '',13,10,'',0
VALUE_key dd 0
VALUE_0 db '_',0
;{VARIABLE}
times 510-($-$$) db 0
dw 0xaa55

;;;;;;;;;;;;;;;;;;; CONTINUE WORKING ON KEYS
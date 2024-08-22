ORG 0x7E00

; interupt timer constants
PIT_COMMAND    equ 0x43
PIT_CHANNEL_0  equ 0x40
PIT_FREQUENCY  equ 1193180  ; Base frequency
DESIRED_FREQ   equ 60      ; Desired interrupt frequency 
DIVISOR        equ PIT_FREQUENCY / DESIRED_FREQ
;{CONSTANTS}

start:

    call  OS16BIT_SetupInteruptTimer
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
OS16BIT_timer_interrupt:
   call OS16BIT_TimerEvent
   mov al, 0x20
   out 0x20, al
   iret
OS16BIT_SetupInteruptTimer:
   cli    ; Set up the PIT
   mov al, 00110100b    ; Channel 0, lobyte/hibyte, rate generator
   out PIT_COMMAND, al
       ; Set the divisor
   mov ax, DIVISOR
   out PIT_CHANNEL_0, al    ; Low byte
   mov al, ah
   out PIT_CHANNEL_0, al    ; High byte
   ; Set up the timer ISR
   mov word [0x0020], OS16BIT_timer_interrupt
   mov word [0x0022], 0x0000    ; Enable interrupts
   sti
   ret
OS16BIT_TimerEvent:

   ret
OS16BIT_KeyboardEvent:
   mov al, [KeyCodeValue]
   mov byte [VALUE_key], al
   mov bx, [KeyCodeValue]
   add bx, key_down_table
   mov byte al, [bx]
   mov [VALUE_down], al

   ret
;{INCLUDE}

key_down_table: times 255 db 0
scan_code_table:
   db 0, 0, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 0, 0
   db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 0, 0
   db 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', "'", '`', 0, '\'
   db 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '
KeyCodeValue db 0
VALUE_hello db 'Hello, World!',13,10,'',0
VALUE_newline db '',13,10,'',0
VALUE_ScreenW dd 320
VALUE_ScreenH dd 200
VALUE_key db 0
VALUE_down db 0
;{VARIABLE}


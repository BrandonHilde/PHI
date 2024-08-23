ORG 0x7E00

; interupt timer constants
PIT_COMMAND    equ 0x43
PIT_CHANNEL_0  equ 0x40
PIT_FREQUENCY  equ 1193180  ; Base frequency
DESIRED_FREQ   equ 60      ; Desired interrupt frequency 
DIVISOR        equ PIT_FREQUENCY / DESIRED_FREQ
; drawing constants
DRAW_START equ 0xA0000
SCREEN_WIDTH equ 320
SCREEN_HEIGHT equ 200
BUFFER_SIZE equ DRAW_START + (SCREEN_WIDTH * SCREEN_HEIGHT)
;color array
VALUE_Colors.Black equ 0x0  ;Black
VALUE_Colors.Blue equ 0x1  ;Blue
VALUE_Colors.Green equ 0x2  ;Green
VALUE_Colors.Cyan equ 0x3  ;Cyan
VALUE_Colors.Red equ 0x4  ;Red
VALUE_Colors.Magenta equ  0x5  ;Magenta
VALUE_Colors.Brown equ  0x6  ;Brown
VALUE_Colors.LightGray equ  0x7  ;Light Gray
VALUE_Colors.Gray equ  0x8  ;Gray
VALUE_Colors.LightBlue equ  0x9  ;Light Blue
VALUE_Colors.LightGreen equ 0xA  ;Light Green
VALUE_Colors.LightCyan   equ 0xB  ;Light Cyan
VALUE_Colors.LightRed   equ 0xC  ;Light Red
VALUE_Colors.LightMagenta   equ 0xD  ;Light Magenta
VALUE_Colors.Yellow equ 0xE  ;Yellow 
VALUE_Colors.White equ 0xF  ;White

;{CONSTANTS}

start:

    call  OS16BIT_SetupInteruptTimer
    call  OS16BIT_SetupKeyboardInterupt
;{CODE}

   jmp $

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
   xor bx, bx
   mov bl, al
   mov al, [scan_code_table + bx]  ; Convert scan code to ASCII

   mov [KeyCodeValue], al

   xor bx, bx
   mov bl, byte [KeyCodeValue]
   ;add bx, key_down_table
   mov byte [key_down_table + bx], 0x00

   jmp .done
   
   cmp al, 0               ; Check if it's a valid key
   je .done

.key_down:
   xor bx, bx
   mov bl, al
   ; Convert scan code to ASCII (simplified)
   mov al, [scan_code_table + bx]

   mov [KeyCodeValue], al

   xor bx, bx
   mov bl, byte [KeyCodeValue]
   ;add bx, key_down_table
   mov byte [key_down_table + bx], 0x01
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
OS16BITVideo_EnableVideoMode:
   mov ax, 0x13
   int 0x10
   ret
OS16BITVideo_DrawRectangle:
   mov edi, DRAW_START; Start of VGA memory
   mov eax, [DrawRectY]
   mov ecx, SCREEN_WIDTH
   mul ecx
   add eax, [DrawRectX]
   add edi, eax
   mov edx, 0
.draw_row:
   mov ecx, 0
.draw_pixel:
   cmp edi, BUFFER_SIZE
   jl .continue_draw
   mov edi, DRAW_START
.continue_draw:
   mov al, [DrawRectColor]
   mov byte [edi], al
   inc edi
   inc ecx
   cmp ecx, [DrawRectW]
   jl .draw_pixel
   add edi, SCREEN_WIDTH
   sub edi, [DrawRectW]
   inc edx
   cmp edx, [DrawRectH]
   jl .draw_row
   ret
IF_0:
   mov eax, [VALUE_ballspeed]
   cmp eax, [VALUE_maxspeed]
   jg IF_0_CONTENT

;{ELSE CONTENT}

   ret
IF_0_CONTENT:

   mov eax, [VALUE_maxspeed]
   mov [VALUE_ballspeed], eax

;{IF CONTENT}
   ret
IF_1:
   mov al, [VALUE_downW]
   cmp al, 1
   je IF_1_CONTENT

;{ELSE CONTENT}

   ret
IF_1_CONTENT:

   mov eax, [VALUE_lefty]
   sub eax,[VALUE_paddleSpeed]
   mov [VALUE_lefty], eax

;{IF CONTENT}
   ret
IF_2:
   mov eax, [VALUE_lefty]
   cmp eax, 0
   jg IF_2_CONTENT

   mov eax, 0
   mov [VALUE_lefty], eax
;{ELSE CONTENT}

   ret
IF_2_CONTENT:
   call IF_1
;{IF CONTENT}
   ret
IF_3:
   mov eax, [VALUE_botL]
   cmp eax, [VALUE_ScreenH]
   jl IF_3_CONTENT

;{ELSE CONTENT}

   ret
IF_3_CONTENT:

   mov eax, [VALUE_lefty]
   add eax,[VALUE_paddleSpeed]
   mov [VALUE_lefty], eax

;{IF CONTENT}
   ret
IF_4:
   mov al, [VALUE_downS]
   cmp al, 1
   je IF_4_CONTENT

;{ELSE CONTENT}

   ret
IF_4_CONTENT:
   call IF_3
;{IF CONTENT}
   ret
IF_5:
   mov al, [VALUE_downO]
   cmp al, 1
   je IF_5_CONTENT

;{ELSE CONTENT}

   ret
IF_5_CONTENT:

   mov eax, [VALUE_righty]
   sub eax,[VALUE_paddleSpeed]
   mov [VALUE_righty], eax

;{IF CONTENT}
   ret
IF_6:
   mov eax, [VALUE_righty]
   cmp eax, [VALUE_paddleSpeed]
   jg IF_6_CONTENT

   mov eax, [VALUE_paddleSpeed]
   mov [VALUE_righty], eax
;{ELSE CONTENT}

   ret
IF_6_CONTENT:
   call IF_5
;{IF CONTENT}
   ret
IF_7:
   mov eax, [VALUE_botR]
   cmp eax, [VALUE_ScreenH]
   jl IF_7_CONTENT

;{ELSE CONTENT}

   ret
IF_7_CONTENT:

   mov eax, [VALUE_righty]
   add eax,[VALUE_paddleSpeed]
   mov [VALUE_righty], eax

;{IF CONTENT}
   ret
IF_8:
   mov al, [VALUE_downL]
   cmp al, 1
   je IF_8_CONTENT

;{ELSE CONTENT}

   ret
IF_8_CONTENT:
   call IF_7
;{IF CONTENT}
   ret
IF_9:
   mov eax, [VALUE_bally]
   cmp eax, [VALUE_leftColx]
   jl IF_9_CONTENT

;{ELSE CONTENT}

   ret
IF_9_CONTENT:

   mov eax, 1
   mov [VALUE_directr], eax


   mov eax, [VALUE_ballspeed]
   add eax,1
   mov [VALUE_ballspeed], eax

;{IF CONTENT}
   ret
IF_10:
   mov eax, [VALUE_bally]
   cmp eax, [VALUE_lefty]
   jg IF_10_CONTENT

;{ELSE CONTENT}

   ret
IF_10_CONTENT:

   mov eax, [VALUE_lefty]
   mov [VALUE_leftColx], eax


   mov eax, [VALUE_leftColx]
   add eax,[VALUE_Bot]
   mov [VALUE_leftColx], eax

   call IF_9
;{IF CONTENT}
   ret
IF_11:
   mov eax, [VALUE_ballx]
   cmp eax, 20
   jl IF_11_CONTENT

;{ELSE CONTENT}

   ret
IF_11_CONTENT:
   call IF_10
;{IF CONTENT}
   ret
IF_12:
   mov eax, [VALUE_bally]
   cmp eax, [VALUE_rightColx]
   jl IF_12_CONTENT

;{ELSE CONTENT}

   ret
IF_12_CONTENT:

   mov eax, 0
   mov [VALUE_directr], eax


   mov eax, [VALUE_ballspeed]
   add eax,1
   mov [VALUE_ballspeed], eax

;{IF CONTENT}
   ret
IF_13:
   mov eax, [VALUE_bally]
   cmp eax, [VALUE_righty]
   jg IF_13_CONTENT

;{ELSE CONTENT}

   ret
IF_13_CONTENT:

   mov eax, [VALUE_righty]
   mov [VALUE_rightColx], eax


   mov eax, [VALUE_rightColx]
   add eax,[VALUE_Bot]
   mov [VALUE_rightColx], eax

   call IF_12
;{IF CONTENT}
   ret
IF_14:
   mov eax, [VALUE_ballx]
   cmp eax, 290
   jg IF_14_CONTENT

;{ELSE CONTENT}

   ret
IF_14_CONTENT:
   call IF_13
;{IF CONTENT}
   ret
IF_15:
   mov eax, [VALUE_directr]
   cmp eax, 0
   je IF_15_CONTENT

   mov eax, [VALUE_ballx]
   add eax,[VALUE_ballspeed]
   mov [VALUE_ballx], eax
   call IF_14
;{ELSE CONTENT}

   ret
IF_15_CONTENT:

   mov eax, [VALUE_ballx]
   sub eax,[VALUE_ballspeed]
   mov [VALUE_ballx], eax

   call IF_11
;{IF CONTENT}
   ret
IF_16:
   mov eax, [VALUE_ballyDir]
   cmp eax, 0
   je IF_16_CONTENT

   mov eax, [VALUE_bally]
   add eax,[VALUE_ballspeedy]
   mov [VALUE_bally], eax
;{ELSE CONTENT}

   ret
IF_16_CONTENT:

   mov eax, [VALUE_bally]
   sub eax,[VALUE_ballspeedy]
   mov [VALUE_bally], eax

;{IF CONTENT}
   ret
IF_17:
   mov eax, [VALUE_bally]
   cmp eax, 0
   jl IF_17_CONTENT

;{ELSE CONTENT}

   ret
IF_17_CONTENT:

   mov eax, 1
   mov [VALUE_ballyDir], eax

;{IF CONTENT}
   ret
IF_18:
   mov eax, [VALUE_bally]
   cmp eax, 190
   jg IF_18_CONTENT

;{ELSE CONTENT}

   ret
IF_18_CONTENT:

   mov eax, 0
   mov [VALUE_ballyDir], eax

;{IF CONTENT}
   ret
IF_19:
   mov eax, [VALUE_ballx]
   cmp eax, 0
   jl IF_19_CONTENT

;{ELSE CONTENT}

   ret
IF_19_CONTENT:

   mov eax, 160
   mov [VALUE_ballx], eax


   mov eax, 1
   mov [VALUE_directr], eax


   mov eax, [VALUE_rightPoints]
   add eax,1
   mov [VALUE_rightPoints], eax


   mov eax, 1
   mov [VALUE_ballspeed], eax

;{IF CONTENT}
   ret
IF_20:
   mov eax, [VALUE_ballx]
   cmp eax, 310
   jg IF_20_CONTENT

;{ELSE CONTENT}

   ret
IF_20_CONTENT:

   mov eax, 160
   mov [VALUE_ballx], eax


   mov eax, 0
   mov [VALUE_directr], eax


   mov eax, [VALUE_leftPoints]
   add eax,1
   mov [VALUE_leftPoints], eax


   mov eax, 1
   mov [VALUE_ballspeed], eax

;{IF CONTENT}
   ret
OS16BIT_TimerEvent:

   mov eax, 0
   mov [DrawRectX], eax


   mov eax, 0
   mov [DrawRectY], eax


   mov eax, 320
   mov [DrawRectW], eax


   mov eax, 200
   mov [DrawRectH], eax


   mov al, VALUE_Colors.Black
   mov [DrawRectColor], al

    call  OS16BITVideo_DrawRectangle
   call IF_0
   call IF_2

   mov eax, [VALUE_lefty]
   mov [VALUE_botL], eax


   mov eax, [VALUE_botL]
   add eax,[VALUE_Bot]
   mov [VALUE_botL], eax

   call IF_4
   call IF_6

   mov eax, [VALUE_righty]
   mov [VALUE_botR], eax


   mov eax, [VALUE_botR]
   add eax,[VALUE_Bot]
   mov [VALUE_botR], eax

   call IF_8
   call IF_15
   call IF_16
   call IF_17
   call IF_18
   call IF_19
   call IF_20

   mov eax, [VALUE_ballx]
   mov [DrawRectX], eax


   mov eax, [VALUE_bally]
   mov [DrawRectY], eax


   mov eax, 10
   mov [DrawRectW], eax


   mov eax, 10
   mov [DrawRectH], eax


   mov al, VALUE_Colors.White
   mov [DrawRectColor], al

    call  OS16BITVideo_DrawRectangle

   mov eax, 10
   mov [DrawRectX], eax


   mov eax, [VALUE_lefty]
   mov [DrawRectY], eax


   mov eax, 10
   mov [DrawRectW], eax


   mov eax, [VALUE_Bot]
   mov [DrawRectH], eax


   mov al, VALUE_Colors.LightGreen
   mov [DrawRectColor], al

    call  OS16BITVideo_DrawRectangle

   mov eax, 300
   mov [DrawRectX], eax


   mov eax, [VALUE_righty]
   mov [DrawRectY], eax


   mov eax, 10
   mov [DrawRectW], eax


   mov eax, [VALUE_Bot]
   mov [DrawRectH], eax


   mov al, VALUE_Colors.LightGreen
   mov [DrawRectColor], al

    call  OS16BITVideo_DrawRectangle

   ret
OS16BIT_KeyboardEvent:
   mov al, [KeyCodeValue]
   mov byte [VALUE_key], al
   xor bx, bx
   mov bl, byte 'w'
   add bx, key_down_table
   mov ax, [bx]
   xor ah, ah
   mov byte [VALUE_downW], al
   xor bx, bx
   mov bl, byte 's'
   add bx, key_down_table
   mov ax, [bx]
   xor ah, ah
   mov byte [VALUE_downS], al
   xor bx, bx
   mov bl, byte 'o'
   add bx, key_down_table
   mov ax, [bx]
   xor ah, ah
   mov byte [VALUE_downO], al
   xor bx, bx
   mov bl, byte 'l'
   add bx, key_down_table
   mov ax, [bx]
   xor ah, ah
   mov byte [VALUE_downL], al

   ret
;{INCLUDE}

key_down_table:
   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0
   db 0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0

scan_code_table:
   db 0, 0, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 0, 0
   db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 0, 0
   db 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', "'", '`', 0, '\'
   db 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '
KeyCodeValue db 0
; drawing variables
DrawRectX dd 0
DrawRectY dd 0
DrawRectW dd 10
DrawRectH dd 10
DrawRectColor db 0xA
VALUE_hello db 'Hello, World!',13,10,'',0
VALUE_newline db '',13,10,'',0
VALUE_downW db 0
VALUE_downS db 0
VALUE_downO db 0
VALUE_downL db 0
VALUE_ScreenW dd 320
VALUE_ScreenH dd 200
VALUE_lefty dd 50
VALUE_Bot dd 60
VALUE_botR dd 0
VALUE_botL dd 0
VALUE_direction dd 0
VALUE_ballspeed dd 1
VALUE_ballspeedy dd 1
VALUE_maxspeed dd 8
VALUE_paddleSpeed dd 2
VALUE_ballx dd 160
VALUE_bally dd 100
VALUE_ballyDir dd 0
VALUE_leftColx dd 50
VALUE_rightColx dd 50
VALUE_directr dd 1
VALUE_righty dd 50
VALUE_key dd 0
VALUE_leftPoints dd 0
VALUE_rightPoints dd 0
;{VARIABLE}


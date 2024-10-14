org 0x7E00

Colors.Black equ 0x0  ;Black
Colors.Blue equ 0x1  ;Blue
Colors.Green equ 0x2  ;Green
Colors.Cyan equ 0x3  ;Cyan
Colors.Red equ 0x4  ;Red
Colors.Magenta equ  0x5  ;Magenta
Colors.Brown equ  0x6  ;Brown
Colors.LightGray equ  0x7  ;Light Gray
Colors.Gray equ  0x8  ;Gray
Colors.LightBlue equ  0x9  ;Light Blue
Colors.LightGreen equ 0xA  ;Light Green
Colors.LightCyan   equ 0xB  ;Light Cyan
Colors.LightRed   equ 0xC  ;Light Red
Colors.LightMagenta   equ 0xD  ;Light Magenta
Colors.Yellow equ 0xE  ;Yellow
Colors.White equ 0xF  ;White
PIT_COMMAND equ 0x43
PIT_CHANNEL_0 equ 0x40
PIT_FREQUENCY equ 1193180
DESIRED_FREQ equ 60
DIVISOR equ PIT_FREQUENCY/DESIRED_FREQ

start:

   call Timer_Setup
   call SetupKeyboardInterupt

   jmp $


SetupKeyboardInterupt:

   cli                    
   mov word [0x0024], keyboard_handler ; 0x0024
   mov [0x0026], cs                    ; 0x0026
   sti                 
   ret

key_prep:
   xor bx, bx
   mov bl, al 
   mov al, [scan_code_table + bx]
   mov [keyval], al
   ret

keyboard_handler:
   in al, 0x60 
   test al, 0x80
   jz .key_down

   ; key up 
   and al, 0x7F
   call key_prep
   call Key_Up

   jmp .done


.key_down:
   call key_prep
   call Key_Down
.done:
   mov al, 0x20
   out 0x20, al
   iret

Key_Up:
   ;;call write_char

   cmp byte [keyval], 's'
   je .s_press

   cmp byte [keyval], 'w'
   je .w_press

   
   cmp byte [keyval], 'i'
   je .i_press

   
   cmp byte [keyval], 'k'
   je .k_press

   jmp .done
   
.w_press:
   mov byte [w_down], 0
   jmp .done
.s_press:
   mov byte [s_down], 0
   jmp .done
.i_press:
   mov byte [i_down], 0
   jmp .done
.k_press:
   mov byte [k_down], 0
   jmp .done
   
.done:
   ret

Key_Down:
   ;;call write_char

   cmp byte [keyval], 's'
   je .s_press

   cmp byte [keyval], 'w'
   je .w_press

   
   cmp byte [keyval], 'i'
   je .i_press

   
   cmp byte [keyval], 'k'
   je .k_press

   jmp .done
   
.w_press:
   mov byte [w_down], 'w'
   jmp .done
.s_press:
   mov byte [s_down], 's'
   jmp .done
.i_press:
   mov byte [i_down], 'i'
   jmp .done
.k_press:
   mov byte [k_down], 'k'
   jmp .done
   
.done:
   ret

move_box_down:
   mov eax, [left_y_paddle]
   add eax, 3
   mov [left_y_paddle], eax
   ret
move_box_up:
   mov eax, [left_y_paddle]
   sub eax, 3
   mov [left_y_paddle], eax
   ret

move_right_box_down:
   mov eax, [right_y_paddle]
   add eax, 3
   mov [right_y_paddle], eax
   ret
move_right_box_up:
   mov eax, [right_y_paddle]
   sub eax, 3
   mov [right_y_paddle], eax
   ret

draw_box:
   mov edi, DRAW_START
   mov eax, [sq_y]
   cmp eax, 0
   jge .proceed
   mov eax, 0
.proceed:
   mov ebx, 320
   mul ebx
   add eax, edi
   mov edi, eax
   add edi, [sq_x]
   xor ecx, ecx
   jmp .put_pixel

.move_down:
   add edi, 320
   sub edi, [sq_width]
   xor ecx, ecx

.put_pixel:
   mov al, [colorDraw]
   cmp edi, DRAW_START
   jl .continue
   mov byte [edi], al
.continue:
   inc edi 
   inc ecx 
   cmp ecx, [sq_width]
   jl .put_pixel

   inc edx
   cmp edx, [sq_height]
   jl .move_down
.done:
   ret

Timer_Event:

   cmp byte [s_down], 's'
   jne .check
   call move_box_down
.check:
   cmp byte [w_down], 'w'
   jne .skip_next
   call move_box_up
.skip_next:
   cmp byte [i_down], 'i'
   jne .check_right
   call move_right_box_down
.check_right:
   cmp byte [k_down], 'k'
   jne .skip
   call move_right_box_up

.skip:

   mov al, Colors.Black
   mov [colorDraw], al

   mov eax, 0
   mov [sq_x], eax
   mov eax, 0
   mov [sq_y], eax

   mov eax, SCREEN_WIDTH
   mov [sq_width], eax
   mov eax, SCREEN_HEIGHT
   mov [sq_height], eax
   
   call draw_box

   mov al, Colors.Green
   mov [colorDraw], al

   mov eax, 20
   mov [sq_x], eax
   mov eax, [left_y_paddle]
   mov [sq_y], eax

   mov eax, 10
   mov [sq_width], eax
   mov eax, 80
   mov [sq_height], eax
   
   call draw_box

   mov eax, 290
   mov [sq_x], eax
   mov eax, [right_y_paddle]
   mov [sq_y], eax
   
   call draw_box

   ret


timer_interrupt:
   call Timer_Event
   mov al, 0x20
   out 0x20, al
   iret

Timer_Setup:
   cli 
   mov al, 00110100b    ; Channel 0, lobyte/hibyte, rate generator
   out PIT_COMMAND, al
       ; Set the divisor
   mov ax, DIVISOR
   out PIT_CHANNEL_0, al    ; Low byte
   mov al, ah
   out PIT_CHANNEL_0, al    ; High byte
   ; Set up the timer ISR
   mov word [0x0020], timer_interrupt
   mov word [0x0022], 0x0000    ; Enable interrupts

   sti 
   ret


scan_code_table:
   db 0, 0, '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 0, 0
   db 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', 0, 0
   db 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', "'", '`', 0, '\'
   db 'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/', 0, '*', 0, ' '
begin_draw dd 0

w_down db 0
s_down db 0

i_down db 0
k_down db 0

keyval db 0
colorDraw db 0
sq_x dd 10
sq_y dd 50
sq_width dd 40
sq_height dd 70

;; user drawing

left_y_paddle dd 60
right_y_paddle dd 60

;;;;;;;;;;;;;;;;;;;;;;;



DRAW_START equ 0xA0000
SCREEN_HEIGHT equ 200
SCREEN_WIDTH equ 320
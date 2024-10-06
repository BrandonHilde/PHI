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
DESIRED_FREQ equ 100
DIVISOR equ PIT_FREQUENCY/DESIRED_FREQ

DRAW_START equ 0xA0000
SCREEN_WIDTH equ 320
SCREEN_HEIGHT equ 200
BUFFER_SIZE equ DRAW_START + (SCREEN_WIDTH * SCREEN_HEIGHT)

sctortwostart:
   mov al, 0x13 ; color 320x200
   int 0x10

   call Timer_Setup
   call SetupKeyboardInterupt

   jmp $

SetupKeyboardInterupt:

   cli                    
   mov word [0x0024], keyboard_handler ; 0x0024
   mov [0x0026], cs                    ; 0x0026
   sti                 
   ret

keyboard_handler:
   in al, 0x60 
   test al, 0x80
   jz .key_down

   ; key up 
   call Key_Up

   jmp .done


.key_down:
   and al, 0x7F
   xor bx, bx
   mov bl, al 
   mov al, [scan_code_table + bx]
   mov [keyval], al
   call Key_Down
.done:
   mov al, 0x20
   out 0x20, al
   iret

Key_Up:
   ;;call write_char
   ;;call move_box_down
   ret

Key_Down:
   call write_char

   cmp byte [keyval], 's'
   je .s_press

.w_press:
   cmp byte [keyval], 'w'
   jne .done
   call move_box_up
   jmp .done
.s_press:
   call move_box_down
.done:
   ret

move_box_down:
   push eax
   mov eax, [sq_y]
   add eax, 3
   mov [sq_y], eax
   pop eax
   ret
move_box_up:
   push eax
   mov eax, [sq_y]
   sub eax, 3
   mov [sq_y], eax
   pop eax
   ret
write_char:
   mov ah, 0x0E
   mov bl, Colors.LightBlue
   int 0x10
   ret
fill_pixel:
    mov byte [edi], Colors.LightRed
    inc edi
    ret

DrawRectangle:
   mov edi, DRAW_START; Start of VGA memory
   mov eax, [sq_y]
   mov ecx, SCREEN_WIDTH
   mul ecx
   add eax, [sq_x]
   add edi, eax
   mov edx, 0
.draw_row:
   mov ecx, 0
.draw_pixel:
   cmp edi, BUFFER_SIZE
   jl .continue_draw
   mov edi, DRAW_START
.continue_draw:
   mov al, [colorDraw]
   mov byte [edi], al
   inc edi
   inc ecx
   cmp ecx, [sq_width]
   jl .draw_pixel
   add edi, SCREEN_WIDTH
   sub edi, [sq_width]
   inc edx
   cmp edx, [sq_height]
   jl .draw_row
   ret

draw_box:
   mov edi, DRAW_START
   mov eax, [sq_y]
   mov ebx, SCREEN_WIDTH
   mul ebx
   add eax, edi
   mov edi, eax
   add edi, [sq_x]

   xor ecx, ecx

   jmp .put_pixel

.move_down:
   add edi, SCREEN_WIDTH
   sub edi, [sq_width]
   xor ecx, ecx

.put_pixel:
   mov al, [colorDraw]
   mov byte [edi], al
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

   push eax
   mov al, Colors.Black
   mov [colorDraw], al

   xor eax, eax   
   mov [sq_x], eax

   xor eax, eax   
   mov [sq_y], eax

   mov eax, [screen_height]   
   mov [sq_height], eax

   mov eax, [screen_width]   
   mov [sq_width], eax

   call draw_box

   mov eax, 80  
   mov [sq_height], eax

   mov eax,  10
   mov [sq_width], eax

   mov eax, 20  
   mov [sq_x], eax

   mov eax, 55  
   mov [sq_y], eax

   mov al, Colors.Green
   mov [colorDraw], al
   
   call draw_box

   pop eax

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
keyval db 0
colorDraw db 0

DrawRectX dd 0
DrawRectY dd 0
DrawRectW dd 10
DrawRectH dd 10
sq_x dd 10
sq_y dd 50
sq_width dd 40
sq_height dd 70

screen_height dd 200
screen_width dd 320

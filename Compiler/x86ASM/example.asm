ORG 0x7C00

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
   xor ax, ax
   mov ds, ax
   mov es, ax
   mov ss, ax
   mov sp, 0x7C00

   mov al, 0x13 ; color 320x200
   int 0x10

   call Timer_Setup

   jmp $


fill_pixel:
    mov byte [edi], Colors.LightRed
    inc edi
    ret

draw_box:
   mov edi, DRAW_START
   mov eax, [sq_y]
   mov ebx, 320
   mul ebx
   add eax, edi
   mov edi, eax
   add edi, [sq_x]

   jmp .put_pixel

.move_down:
   add edi, 320
   sub edi, [sq_width]
   xor ecx, ecx

.put_pixel:
   push eax
   mov al, [colorDraw]
   mov byte [edi], al
   pop eax
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

   mov al, Colors.Black
   mov [colorDraw], al
   
   call draw_box

   mov eax, [sq_x]
   inc eax
   mov [sq_x], eax

   mov al, Colors.Green
   mov [colorDraw], al
   
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


begin_draw dd 0

colorDraw db 0
sq_x dd 10
sq_y dd 50
sq_width dd 40
sq_height dd 70
DRAW_START equ 0xA0000

times 510-($-$$) db 0
dw 0xAA55

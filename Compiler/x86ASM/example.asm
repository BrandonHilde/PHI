ORG 0x7C00

start:
   xor ax, ax
   mov ds, ax
   mov es, ax
   mov ss, ax
   mov sp, 0x7C00

   mov al, 0x13 ; color 320x200
   int 0x10

   call draw_square

   jmp $

draw_square:

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
   mov byte [edi], COLOR_RED
   inc edi 
   inc ecx 
   cmp ecx, [sq_width]
   jl .put_pixel

   inc edx
   cmp edx, [sq_height]
   jl .move_down
.done
   ret
   

begin_draw dd 0

sq_x dd 100
sq_y dd 50
sq_width dd 40
sq_height dd 70
DRAW_START equ 0xA0000
COLOR_RED equ 0x04

times 510-($-$$) db 0
dw 0xAA55

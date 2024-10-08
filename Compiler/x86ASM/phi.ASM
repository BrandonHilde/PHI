ORG 0x7c00

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
   xor ax, ax
   mov ds, ax
   mov es, ax
   mov ss, ax
   mov sp, 0x7c00

   mov si, VALUE_hello
   call print_log
   mov si, VALUE_newline
   call print_log
   mov si, VALUE_0
   call print_log
    call  OS16BIT_WaitForKeyPress
    call  OS16BITVideo_EnableVideoMode
    call  OS16BIT_JumpToSectorTwo
;{CODE}

   jmp $

OS16BIT_PrepSectorTwo:
   mov ah, 0x02    ; BIOS read sector
   mov al, 6       ; Number of sectors
   mov ch, 0       ; Cylinder number
   mov dh, 0       ; Head number
   mov cl, 2       ; Sector number
   mov bx, 0x7E00  ; Load address
   int 0x13
   ret
OS16BIT_JumpToSectorTwo:
   call OS16BIT_PrepSectorTwo
   jmp 0x7E00 ; jump to sector two
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
;{INCLUDE}

; drawing variables
DrawRectX dd 0
DrawRectY dd 0
DrawRectW dd 10
DrawRectH dd 10
DrawRectColor db 0xA
VALUE_hello db 'Hello, World!',13,10,'',0
VALUE_newline db '',13,10,'',0
VALUE_0 db 'Press any key to continue...',0
;{VARIABLE}
times 510-($-$$) db 0
dw 0xaa55

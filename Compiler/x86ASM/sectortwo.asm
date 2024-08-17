; second sector
org 0x7E00

second_sector_start:
    cli
    ; Set up the PIT
    mov al, 00110100b    ; Channel 0, lobyte/hibyte, rate generator
    out PIT_COMMAND, al
    
    ; Set the divisor
    mov ax, DIVISOR
    out PIT_CHANNEL_0, al    ; Low byte
    mov al, ah
    out PIT_CHANNEL_0, al    ; High byte

    ; Set up the timer ISR
    mov word [0x0020], timer_interrupt
    mov word [0x0022], 0x0000  

    ; Enable interrupts
    sti

   ; mov eax, SCREEN_WIDTH
   ; mov ecx, SCREEN_HEIGHT
   ; mul ecx
   ; add eax, DRAW_START
    ;mov [BUFFER_SIZE], eax

    call reset_sqx
    call reset_sqy

    jmp $

;main_loop:
    ;hlt                     ; Halt until next interrupt
    ;jmp main_loop
timer_interrupt:

    ;mov di, DRAW_START
    ;call set_color_black
    ;call draw_square
    ;call set_color_inc
    ;call single_pixel
    mov al, COLORS.LightGreen
    mov [COLOR], al
    call move_square

    mov al, 0x20
    out 0x20, al
    iret

reset_sqx:
    mov eax, 0
    mov [SQX], eax
    ret
reset_sqy:
    mov eax, 0
    mov [SQY], eax
    ret

move_square:
    ; set square x coord
    mov eax, [SQX]
    add eax, [SQ_WIDTH]
    mov [SQX], eax
    cmp eax, SCREEN_WIDTH
    jle .done
.movey:
    mov eax, [SQY]
    add eax, [SQ_HEIGHT]
    mov [SQY], eax
    call reset_sqx
.done:
   ; call set_color_red
    call draw_square
    ret

draw_square:
    mov edi, DRAW_START  ; Start of VGA memory for mode 13h (320x200)
    mov eax, [SQY]
    mov ecx, SCREEN_WIDTH
    mul ecx
    add eax, [SQX]
    add edi, eax
    mov edx, 0       ; Y counter

.draw_row:
    mov ecx, 0      ; Reset X counter

.draw_pixel:
    cmp edi, BUFFER_SIZE
    jl .continue_draw
    mov edi, DRAW_START

.continue_draw:
    mov al, [COLOR]
    mov byte [edi], al; Set pixel color to red (color index 4 in default palette)
    inc edi
    inc ecx
    cmp ecx, [SQ_WIDTH]
    jl .draw_pixel

    add edi, SCREEN_WIDTH
    sub edi, [SQ_WIDTH] ;Move to the next row (320 - square width)
    inc edx
    cmp edx, [SQ_HEIGHT]
    jl .draw_row

    ret

single_pixel:
    mov edi, DRAW_START
    add edi, [PX_I]
    ;mov edi, DRAW_START
.draw:
    mov al, [COLOR]
    mov byte [edi], al
    mov eax, [PX_I]
    inc eax
    mov [PX_I], eax
.done:
    ret

clear_screen:
    push eax
    push ecx
    push edi

    mov edi, DRAW_START
    xor eax, eax
    mov ecx, SCREEN_WIDTH * SCREEN_HEIGHT

    rep stosb

    pop edi
    pop ecx
    pop eax
    ret

set_color_red:
    mov al, 0x04
    mov [COLOR], al
    ret
set_color_black:
    mov al, 0x00
    mov [COLOR], al
    ret

set_color_inc:
    mov al, [COLOR]
    inc al
    cmp al, 0x20
    jg set_color_black
    mov [COLOR], al
    ret

welcome_msg db 'Welcome to sector two', 13, 10, 0
name_prompt db 'Please enter your name: ', 0
input_prompt db 'Enter some text: ', 0
greeting db 'Hello, ', 0
newline db 0x0D, 0x0A, 0
to_string_number_test dd 1618
dec_number dd 3.14
dec_mul dd 100.0
; Buffers for user input
name_buffer times 32 db 0
input_buffer times 64 db 0
; PIT-related constants
PIT_COMMAND    equ 0x43
PIT_CHANNEL_0  equ 0x40
PIT_FREQUENCY  equ 1193180  ; Base frequency of the PIT
DESIRED_FREQ   equ 60      ; Desired interrupt frequency (Hz)
DIVISOR        equ PIT_FREQUENCY / DESIRED_FREQ

COLOR db 0x4 ; default red
; Data
SQX dd 0
SQY dd 0
SQ_WIDTH dd 5
SQ_HEIGHT dd 5

PX_I dd 0

COLORS.Black equ 0x0  ;Black
COLORS.Blue equ 0x1  ;Blue
COLORS.Green equ 0x2  ;Green
COLORS.Cyan equ 0x3  ;Cyan
COLORS.Red equ 0x4  ;Red
COLORS.Magenta equ  0x5  ;Magenta
COLORS.Brown equ  0x6  ;Brown
COLORS.LightGray equ  0x7  ;Light Gray
COLORS.Gray equ  0x8  ;Gray
COLORS.LightBlue equ  0x9  ;Light Blue
COLORS.LightGreen equ 0xA  ;Light Green
COLORS.LightCyan   equ 0xB  ;Light Cyan
COLORS.LightRed   equ 0xC  ;Light Red
COLORS.LightMagenta   equ 0xD  ;Light Magenta
COLORS.Yellow equ 0xE  ;Yellow 
COLORS.White equ 0xF  ;White


DRAW_START equ 0xA0000
SCREEN_WIDTH equ 320
SCREEN_HEIGHT equ 200

BUFFER_SIZE equ DRAW_START + (SCREEN_WIDTH * SCREEN_HEIGHT)
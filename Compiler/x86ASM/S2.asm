ORG 0x7c00

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
   mov si, VALUE_1
   call print_log
   mov si, VALUE_name
   call print_log
   mov si, VALUE_2
   call print_log
    call  OS16BITVideo_WaitForKeyPress
    call  OS16BITVideo_EnableVideoMode
    call  OS16BITVideo_JumpToSectorTwo
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
OS16BITVideo_EnableVideoMode:
   mov ax, 0x13
   int 0x10
   ret
OS16BITVideo_PrepSectorTwo:
   mov ah, 0x02    ; BIOS read sector
   mov al, 1       ; Number of sectors
   mov ch, 0       ; Cylinder number
   mov dh, 0       ; Head number
   mov cl, 2       ; Sector number
   mov bx, 0x7E00  ; Load address
   int 0x13
   ret
OS16BITVideo_JumpToSectorTwo:
   call OS16BITVideo_PrepSectorTwo
   jmp 0x7E00 ; jump to sector two
   ret
OS16BITVideo_WaitForKeyPress:
   mov ah, 0x00
   int 0x16
   ret
;{INCLUDE}

VALUE_name: times 40 db 0
VALUE_0 db 'What is your name: ',0
VALUE_1 db '',13,10,'hello: ',0
VALUE_2 db '',13,10,'Press any key to continue...',0
;{VARIABLE}
times 510-($-$$) db 0
dw 0xaa55
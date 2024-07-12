; second sector
BITS 16
org 0x7E00

second_sector_start:

    mov si, welcome_msg
    call print_string

    mov si, name_prompt
    call print_string

    ; Get u    
    mov di, name_buffer
    call get_input

    ; Print greeting with name
    mov si, newline
    call print_string
    mov si, greeting
    call print_string
    mov si, name_buffer
    call print_string

    mov si, newline
    call print_string

    xor eax,eax
    mov eax, [to_string_number_test]   ; load the number into ax
    call int_to_string

    mov si, newline
    call print_string

    xor eax,eax
    mov eax, [dec_number]
    mov ecx, [dec_mul]
    mul ecx
    ;mov eax, [dec_number]   ; load the number into eax
    mov edx, eax
    call int_to_string

    ; Wait for key press
    mov ah, 0x00
    int 0x16

    ; Halt the system
    cli
    hlt

print_string:
    mov ah, 0x0E
.loop:
    lodsb
    cmp al, 0 ;check for 0
    je .done
    int 0x10
    jmp .loop
.done:
    ret

get_input:
    xor cx, cx        ; set to 0
.loop:
    mov ah, 0         ; read keyboard input
    int 0x16       
    
    cmp al, 0x0D      ; if Enter key was pressed
    je .done       
    
    stosb             ; Store character
    inc cx            ; count increment
    
    mov ah, 0x0E      ; print character
    int 0x10          
    
    jmp .loop         

.done:
    mov byte [di], 0  ; 0 terminate the string
    ret 

int_to_string:
    push ebp
    mov ebp, esp
    push edx  

    cmp eax, 10
    jge .div_num

    add al, '0'
    mov ah, 0x0E  
    int 0x10
    jmp .done

.div_num:
    xor edx, edx    ; clear
    mov ebx, 10
    div ebx        
    push edx       ; save value
    call int_to_string  
    pop edx        ; retreive value
    add dl, '0'   ; convert to ASCII
    mov ah, 0x0E  
    mov al, dl
    int 0x10

.done:
    pop edx
    mov esp, ebp
    pop ebp
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

times 512-($-$$) db 0  ; fill extra bytes
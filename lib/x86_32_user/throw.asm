; provides: phi_throw phi_catch_top phi_unwind_top phi_error phi_fault_init phi_fault_entry
; requires: phi_print phi_exit phi_fault_register
; init: phi_fault_init
;
; Errors (user programs): a throw, an index out of range or the heap out of room
; all come here, with eax = the line, esi = the file's name (0 if not known),
; edx = the text and ebx = the kind (ErrorKind in lib/phi/lang/error.phi).
;
; The error goes into phi_error, laid out as an Error is: kind, line, file (64
; characters and a zero), text (255 and a zero), and message (the text, then
; where: 383 and a zero). Then:
;
;   - with a catch (an if call ... throws err running), each method between here
;     and it gives back what it owns (its record on phi_unwind_top says how), esp
;     and ebp go back to where the catch was set, and it carries on there
;   - with none, PHI: and the message are printed, and the program ends with -1,
;     as it did before errors could be caught (a supervisor may start it again)
;
; A catch record (see X86Generator.EmitIfThrows): +0 the catch before it, +4 esp,
; +8 ebp, +12 phi_unwind_top when it was set, +16 where to go. A method's record:
; +0 the record before it, +4 what gives back its things, +8 its ebp.
ERROR_KIND    equ 0
ERROR_LINE    equ 4
ERROR_FILE    equ 8
ERROR_TEXT    equ 73
ERROR_MESSAGE equ 329
ERROR_SIZE    equ 713

phi_throw:
    cld
    mov [phi_error + ERROR_KIND], ebx
    mov [phi_error + ERROR_LINE], eax
    mov edi, phi_error + ERROR_FILE
    mov ecx, 64
    call .copy
    mov esi, edx
    mov edi, phi_error + ERROR_TEXT
    mov ecx, 255
    call .copy

    ; the message: the text, ' on line ', the line, then ' in ' and the file if it's known
    mov edi, phi_error + ERROR_MESSAGE
    mov ecx, 383
    mov esi, phi_error + ERROR_TEXT
    call .append
    cmp dword [phi_error + ERROR_LINE], 0
    je .said                        ; (a CPU exception's line isn't known: the text alone)
    mov esi, .on_line
    call .append
    mov eax, [phi_error + ERROR_LINE]
    call .number
    cmp byte [phi_error + ERROR_FILE], 0
    je .said
    mov esi, .in_file
    call .append
    mov esi, phi_error + ERROR_FILE
    call .append
.said:
    mov byte [edi], 0

    mov ebx, [phi_catch_top]
    test ebx, ebx
    jz .uncaught
.unwind:
    ; each method that's still running above the catch gives back its things
    mov esi, [phi_unwind_top]
    cmp esi, [ebx + 12]
    je .resume
    mov eax, [esi]
    mov [phi_unwind_top], eax       ; off the list first
    push ebx
    mov eax, [esi + 4]
    mov ebp, [esi + 8]
    call eax
    pop ebx
    jmp .unwind
.resume:
    mov eax, [ebx]
    mov [phi_catch_top], eax        ; this catch is done
    mov esp, [ebx + 4]
    mov ebp, [ebx + 8]
    jmp [ebx + 16]

.uncaught:
    mov esi, .prefix
    call phi_print
    mov esi, phi_error + ERROR_MESSAGE
    call phi_print
    mov esi, .newline
    call phi_print
    mov ebx, -1
    jmp phi_exit

; esi's text (none if esi is 0) at edi, up to ecx characters, then a zero
.copy:
    call .append
    mov byte [edi], 0
    ret

; esi's text (none if esi is 0) at edi, up to ecx characters (ecx counts down, edi moves on)
.append:
    test esi, esi
    jz .appended
.next:
    test ecx, ecx
    jz .appended
    mov al, [esi]
    test al, al
    jz .appended
    mov [edi], al
    inc esi
    inc edi
    dec ecx
    jmp .next
.appended:
    ret

; eax in decimal at edi, as .append writes
.number:
    sub esp, 12
    lea esi, [esp + 11]
    mov byte [esi], 0
    mov ebx, 10
.digit:
    xor edx, edx
    div ebx
    add dl, '0'
    dec esi
    mov [esi], dl
    test eax, eax
    jnz .digit
    call .append
    add esp, 12
    ret

.prefix:
    db 13, 10, 'PHI: ', 0
.on_line:
    db ' on line ', 0
.in_file:
    db ' in ', 0
.newline:
    db 13, 10, 0

phi_catch_top:  dd 0                ; the innermost catch, or 0
phi_unwind_top: dd 0                ; the innermost running method that has things to give back, or 0
phi_error:      times ERROR_SIZE db 0

; ---- CPU exceptions, caught as errors
;
; Dividing by zero, or using memory that isn't there (an empty object, a null pointer),
; stops a program in the CPU. Where the system can (PHI OS: system.asm's
; phi_fault_register), it sends the program here instead, with ebx = the kind of error,
; and it's thrown like any other: caught by a catch, or printed and the program ended.
ERROR_KIND_DIVIDE equ 3                 ; ErrorKind in lib/phi/lang/error.phi
ERROR_KIND_EMPTY  equ 4

phi_fault_init:
    mov eax, phi_fault_entry
    jmp phi_fault_register

phi_fault_entry:
    mov edx, .refused
    cmp ebx, ERROR_KIND_DIVIDE
    jne .not_divide
    mov edx, .divided
.not_divide:
    cmp ebx, ERROR_KIND_EMPTY
    jne .thrown
    mov edx, .empty
.thrown:
    xor eax, eax                        ; the line isn't known
    xor esi, esi                        ; nor the file
    jmp phi_throw
.divided: db 'division by zero', 0
.empty:   db 'an empty object or a null pointer was used', 0
.refused: db 'memory that is not there was used, or the CPU refused an instruction', 0

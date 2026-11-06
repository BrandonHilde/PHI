; provides: phi_throw phi_fault_init phi_fault_entry
; requires: phi_print phi_exit phi_fault_register phi_stack_restore phi_thread_block phi_log_take phi_log_back
; init: phi_fault_init
;
; Errors (user programs): a throw, an index out of range or the heap out of room
; all come here, with eax = the line, esi = the file's name (0 if not known),
; edx = the text and ebx = the kind (ErrorKind in lib/phi/lang/error.phi).
;
; Each thread has a block of its own (PHI_THREAD_...): the innermost catch, the innermost
; running method with things to give back, and the error being thrown, so threads catch
; errors of their own. Each system's runtime (its threads.asm) gives the program's first
; thread one and keeps where the running thread's is (phi_thread_block), and
; the code generator reaches it the same way (gs, fs, or a TLS slot on Windows).
;
; The error goes into the thread's block, laid out as an Error is: kind, line, file (64
; characters and a zero), text (255 and a zero), and message (the text, then
; where: 383 and a zero). Then:
;
;   - with a catch (an if call ... throws err running), each method between here
;     and it gives back what it owns (its record on the unwind list says how), esp
;     and ebp go back to where the catch was set, and it carries on there
;   - with none, PHI: and the message are printed, and the program ends with -1,
;     as it did before errors could be caught (a supervisor may start it again)
;
; A catch record (see X86Generator.EmitIfThrows): +0 the catch before it, +4 esp,
; +8 ebp, +12 the unwind list when it was set, +16 where to go, +20 how deep the thread
; was in the log's lock (put back here: log_lock.asm). A method's record:
; +0 the record before it, +4 what gives back its things, +8 its ebp.
PHI_THREAD_SELF   equ 0                 ; the block's own address
PHI_THREAD_CATCH  equ 4                 ; the innermost catch, or 0
PHI_THREAD_UNWIND equ 8                 ; the innermost running method with things to give back, or 0
PHI_THREAD_ERROR  equ 12                ; the error being thrown (an Error)
PHI_THREAD_SPARE  equ 1020              ; the top of its spare stack, on Linux and Windows (system.asm's and threads.asm's)
PHI_THREAD_SIZE   equ 1024
ERROR_KIND    equ 0
ERROR_LINE    equ 4
ERROR_FILE    equ 8
ERROR_TEXT    equ 73
ERROR_MESSAGE equ 329
ERROR_SIZE    equ 713

phi_throw:
    cld
    push eax                        ; (the line)
    call phi_thread_block
    lea edi, [eax + PHI_THREAD_ERROR]
    pop eax
    push edi                        ; [esp]: this thread's error
    mov [edi + ERROR_KIND], ebx
    mov [edi + ERROR_LINE], eax
    add edi, ERROR_FILE
    mov ecx, 64
    call .copy
    mov esi, edx
    mov edi, [esp]
    add edi, ERROR_TEXT
    mov ecx, 255
    call .copy

    ; the message: the text, ' on line ', the line, then ' in ' and the file if it's known
    mov edi, [esp]
    add edi, ERROR_MESSAGE
    mov ecx, 383
    mov esi, [esp]
    add esi, ERROR_TEXT
    call .append
    mov eax, [esp]
    cmp dword [eax + ERROR_LINE], 0
    je .said                        ; (a CPU exception's line isn't known: the text alone)
    mov esi, .on_line
    call .append
    mov eax, [esp]
    mov eax, [eax + ERROR_LINE]
    call .number
    mov eax, [esp]
    cmp byte [eax + ERROR_FILE], 0
    je .said
    mov esi, .in_file
    call .append
    mov esi, [esp]
    add esi, ERROR_FILE
    call .append
.said:
    mov byte [edi], 0

    pop edi
    sub edi, PHI_THREAD_ERROR       ; the block
    mov ebx, [edi + PHI_THREAD_CATCH]
    test ebx, ebx
    jz .uncaught
.unwind:
    ; each method that's still running above the catch gives back its things
    mov esi, [edi + PHI_THREAD_UNWIND]
    cmp esi, [ebx + 12]
    je .resume
    mov eax, [esi]
    mov [edi + PHI_THREAD_UNWIND], eax  ; off the list first
    push ebx
    push edi
    mov eax, [esi + 4]
    mov ebp, [esi + 8]
    call eax
    pop edi
    pop ebx
    jmp .unwind
.resume:
    push ebx
    push edi
    cmp dword [edi + PHI_THREAD_ERROR + ERROR_KIND], ERROR_KIND_STACK
    jne .stack_is_fine
    call phi_stack_restore          ; (it's still on its spare stack)
    mov edi, [esp]
    mov ebx, [esp + 4]
.stack_is_fine:
    mov eax, [ebx + 20]
    call phi_log_back
    pop edi
    pop ebx
    mov eax, [ebx]
    mov [edi + PHI_THREAD_CATCH], eax   ; this catch is done
    mov esp, [ebx + 4]
    mov ebp, [ebx + 8]
    jmp [ebx + 16]

.uncaught:
    push edi
    call phi_log_take               ; (printed whole, even with other threads logging)
    mov edi, [esp]
    mov esi, .prefix
    call phi_print
    pop edi
    lea esi, [edi + PHI_THREAD_ERROR + ERROR_MESSAGE]
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


; ---- CPU exceptions, caught as errors
;
; Dividing by zero, or using memory that isn't there (an empty object, a null pointer),
; stops a program in the CPU. Where the system can (PHI OS: system.asm's
; phi_fault_register), it sends the program here instead, with ebx = the kind of error,
; and ecx = where it happened (the faulting instruction), and it's thrown like any other:
; caught by a catch, or printed and the program ended. When the stack has run out, it
; sends it here on a spare stack, so the throw has room to run and the methods it gives
; back for have theirs left as they were.
ERROR_KIND_DIVIDE equ 3                 ; ErrorKind in lib/phi/lang/error.phi
ERROR_KIND_EMPTY  equ 4
ERROR_KIND_STACK  equ 8

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
    jne .not_empty
    mov edx, .empty
.not_empty:
    cmp ebx, ERROR_KIND_STACK
    jne .thrown
    mov edx, .stack
.thrown:
    mov eax, ecx                        ; the line and file of where it happened, if they're known
    call phi_line_at
    jmp phi_throw
.divided: db 'division by zero', 0
.empty:   db 'an empty object or a null pointer was used', 0
.stack:   db 'the stack ran out: calls too deep', 0
.refused: db 'memory that is not there was used, or the CPU refused an instruction', 0

; eax = an address in the program's code -> eax = the line it's on, esi = its file's name;
; 0 and 0 when it isn't PHI code (an asm block, the runtime) or isn't known (the program's
; phi_lines, which the code generator writes: a count, then each line's address, line and
; file, in the order the code is in)
phi_line_at:
    push ebx
    push ecx
    push edx
    mov ecx, [phi_lines]
    lea edx, [phi_lines + 4]
    xor ebx, ebx                        ; the last mark at or before the address
.next:
    test ecx, ecx
    jz .found
    cmp [edx], eax
    ja .found
    mov ebx, edx
    add edx, 12
    dec ecx
    jmp .next
.found:
    xor eax, eax
    xor esi, esi
    test ebx, ebx
    jz .done
    mov eax, [ebx + 4]
    test eax, eax
    jz .done                            ; (past the end of the PHI code)
    mov esi, [ebx + 8]
.done:
    pop edx
    pop ecx
    pop ebx
    ret

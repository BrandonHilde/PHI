; provides: phi_throw phi_fault_init
; requires: phi_print phi_load_core_fs phi_fault_hook
; init: phi_fault_init
;
; Errors (32-bit kernels): a throw, an index out of range or the heap out of room
; all come here, with eax = the line, esi = the file's name (0 if not known),
; edx = the text and ebx = the kind (ErrorKind in lib/phi/lang/error.phi).
;
; The error goes into this core's block (fs:PHI_CORE_ERROR, interrupts.asm), laid out
; as an Error is: kind, line, file (64 characters and a zero), text (255 and a zero),
; and message (the text, then where: 383 and a zero). Then:
;
;   - with a catch (an if call ... throws err running), each method between here
;     and it gives back what it owns (its record on fs:PHI_CORE_UNWIND says how), esp
;     and ebp go back to where the catch was set, and it carries on there
;   - with none, PHI: and the message are printed, and the kernel stops, as it
;     did before errors could be caught
;
; A catch record (see X86Generator.EmitIfThrows): +0 the catch before it, +4 esp,
; +8 ebp, +12 fs:PHI_CORE_UNWIND when it was set, +16 where to go. A method's record:
; +0 the record before it, +4 what gives back its things, +8 its ebp.
ERROR_KIND    equ 0
ERROR_LINE    equ 4
ERROR_FILE    equ 8
ERROR_TEXT    equ 73
ERROR_MESSAGE equ 329
ERROR_SIZE    equ 713

phi_throw:
    ; interrupts off until the catch has copied the error out of this core's block (it puts
    ; them back), so no other error, and no other core, comes between
    pushfd
    pop dword [fs:PHI_CORE_FLAGS]
    cli
    cld
    mov [fs:PHI_CORE_ERROR + ERROR_KIND], ebx
    mov [fs:PHI_CORE_ERROR + ERROR_LINE], eax
    mov ebp, [fs:PHI_CORE_SELF]
    add ebp, PHI_CORE_ERROR              ; ebp = this core's error, as an address
    lea edi, [ebp + ERROR_FILE]
    mov ecx, 64
    call .copy
    mov esi, edx
    lea edi, [ebp + ERROR_TEXT]
    mov ecx, 255
    call .copy

    ; the message: the text, ' on line ', the line, then ' in ' and the file if it's known
    ; (a CPU exception's line isn't: then the text alone)
    lea edi, [ebp + ERROR_MESSAGE]
    mov ecx, 383
    lea esi, [ebp + ERROR_TEXT]
    call .append
    cmp dword [ebp + ERROR_LINE], 0
    je .said
    mov esi, .on_line
    call .append
    mov eax, [ebp + ERROR_LINE]
    push ebp
    call .number
    pop ebp
    cmp byte [ebp + ERROR_FILE], 0
    je .said
    mov esi, .in_file
    call .append
    lea esi, [ebp + ERROR_FILE]
    call .append
.said:
    mov byte [edi], 0

    mov ebx, [fs:PHI_CORE_CATCH]
    test ebx, ebx
    jz .uncaught
.unwind:
    ; each method that's still running above the catch gives back its things
    mov esi, [fs:PHI_CORE_UNWIND]
    cmp esi, [ebx + 12]
    je .resume
    mov eax, [esi]
    mov [fs:PHI_CORE_UNWIND], eax       ; off the list first
    push ebx
    mov eax, [esi + 4]
    mov ebp, [esi + 8]
    call eax
    pop ebx
    jmp .unwind
.resume:
    mov eax, [ebx]
    mov [fs:PHI_CORE_CATCH], eax        ; this catch is done
    mov esp, [ebx + 4]
    mov ebp, [ebx + 8]
    jmp [ebx + 16]

.uncaught:
    mov esi, .prefix
    call phi_print
    mov esi, [fs:PHI_CORE_SELF]
    add esi, PHI_CORE_ERROR + ERROR_MESSAGE
    call phi_print
    mov esi, .newline
    call phi_print
    cli
.halt:
    hlt
    jmp .halt

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


; ---- CPU exceptions in kernel code, caught as errors
;
; Dividing by zero, using memory that isn't there (an empty object, a null pointer) and
; what the CPU refuses (a general protection fault) in kernel code that is inside a catch
; become errors like any other: interrupts.asm's exception path calls phi_fault_hook,
; which this sets, and the saved state is changed so that returning from the interrupt
; goes to phi_throw, on the faulting code's own stack. Anywhere else (no catch, inside a
; locked block, in an interrupt handler) they still stop the kernel with the panic screen.
ERROR_KIND_DIVIDE equ 3                 ; ErrorKind in lib/phi/lang/error.phi
ERROR_KIND_EMPTY  equ 4
ERROR_KIND_ACCESS equ 5
FRAME_EBX         equ 32                ; in the state interrupts.asm saved
FRAME_EDX         equ 36
FRAME_EAX         equ 44
FRAME_VECTOR      equ 48
FRAME_EIP         equ 56
FRAME_ESI         equ 20

phi_fault_init:
    mov dword [phi_fault_hook], phi_fault
    ret

; [esp + 4] = the saved state of kernel code an exception came in -> eax = 1 if it will be
; thrown as an error when the interrupt returns (the state is changed), 0 if not
phi_fault:
    mov ecx, [esp + 4]
    xor eax, eax
    cmp dword [fs:PHI_CORE_CATCH], 0
    je .not
    mov edx, [ecx + FRAME_VECTOR]
    cmp edx, 0
    je .divide
    cmp edx, 13
    je .access
    cmp edx, 14
    jne .not
    mov eax, cr2
    cmp eax, 4096
    jae .access
    mov dword [ecx + FRAME_EBX], ERROR_KIND_EMPTY
    mov dword [ecx + FRAME_EDX], .empty
    jmp .throw
.divide:
    mov dword [ecx + FRAME_EBX], ERROR_KIND_DIVIDE
    mov dword [ecx + FRAME_EDX], .divided
    jmp .throw
.access:
    mov dword [ecx + FRAME_EBX], ERROR_KIND_ACCESS
    mov dword [ecx + FRAME_EDX], .refused
.throw:
    mov dword [ecx + FRAME_EAX], 0            ; the line isn't known
    mov dword [ecx + FRAME_ESI], 0            ; nor the file
    mov dword [ecx + FRAME_EIP], phi_throw
    mov eax, 1
.not:
    ret
.divided: db 'division by zero', 0
.empty:   db 'an empty object or a null pointer was used', 0
.refused: db 'memory that is not there was used, or the CPU refused an instruction', 0

; provides: phi_strlen
;
; eax = length of the string at esi. Every other register is left as it was.
;
; A byte at a time up to an address that divides by 4, then four at a time: a text that has
; grown long is measured each time it's added to. (v - 0x01010101) & ~v & 0x80808080 is zero
; when none of v's four bytes is, and its lowest set bit is in the first byte that is. Reading
; four from an address that divides by 4 never reaches into the next page of memory.
phi_strlen:
    push ecx
    push edx
    mov eax, esi
.byte:
    test al, 3
    jz .words
    cmp byte [eax], 0
    je .done
    inc eax
    jmp .byte
.words:
    mov ecx, [eax]
    mov edx, ecx
    sub ecx, 0x01010101
    not edx
    and ecx, edx
    and ecx, 0x80808080
    jnz .found
    add eax, 4
    jmp .words
.found:
    bsf ecx, ecx
    shr ecx, 3
    add eax, ecx
.done:
    sub eax, esi
    pop edx
    pop ecx
    ret

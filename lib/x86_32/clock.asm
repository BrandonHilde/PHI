; provides: OS_Now
;
; The date and time (32-bit kernels): OS_Now gives the CMOS clock's time as seconds since
; the start of 1970, UTC (QEMU's clock is in UTC; drivers.rtc reads the same clock, with
; time zones). Its registers hold the date as BCD or binary, and the hour in 12 or 24 hours,
; as status register B says; it's read again while an update was in progress, and until
; two reads agree. The years are 2000 to 2099.
CMOS_INDEX equ 0x70
CMOS_DATA  equ 0x71

; eax = seconds since 1970-01-01 00:00 UTC; keeps the other registers
OS_Now:
    push ebx
    push ecx
    push edx
    push esi
    push edi
.again:
    call .read
    mov esi, eax                        ; the first read: seconds of the day and the date,
    mov edi, edx                        ; packed
    call .read
    cmp eax, esi
    jne .again
    cmp edx, edi
    jne .again

    ; edx = year << 16 | month << 8 | day; eax = hour << 16 | minute << 8 | second
    push eax
    movzx ecx, dh                       ; month
    movzx ebx, dl                       ; day
    shr edx, 16                         ; year, 2000 + the register
    add edx, 2000
    ; days from 1970 to the start of the year, then the month and the day
    mov eax, edx
    sub eax, 1970
    imul eax, eax, 365
    mov esi, edx
    dec esi                             ; the leap years before this one, since 1970
    mov edi, esi
    shr edi, 2
    add eax, edi
    mov edi, esi
    push eax
    mov eax, esi
    push edx
    xor edx, edx
    push ecx
    mov ecx, 100
    div ecx
    pop ecx
    pop edx
    mov edi, eax                        ; (year - 1) / 100
    pop eax
    sub eax, edi
    push eax
    mov eax, esi
    push edx
    xor edx, edx
    push ecx
    mov ecx, 400
    div ecx
    pop ecx
    pop edx
    mov edi, eax                        ; (year - 1) / 400
    pop eax
    add eax, edi
    sub eax, 477                        ; the same count up to 1969: 492 - 19 + 4
    add eax, [.before + ecx * 4 - 4]    ; the months before this one
    add eax, ebx
    dec eax                             ; and the days before today
    ; one more after February in a leap year
    cmp ecx, 2
    jbe .counted
    test edx, 3
    jnz .counted
    push eax
    mov eax, edx
    push edx
    xor edx, edx
    mov ecx, 100
    div ecx
    mov ecx, edx
    pop edx
    pop eax
    test ecx, ecx
    jnz .leap
    push eax
    mov eax, edx
    push edx
    xor edx, edx
    mov ecx, 400
    div ecx
    mov ecx, edx
    pop edx
    pop eax
    test ecx, ecx
    jnz .counted
.leap:
    inc eax
.counted:
    ; days, then the time of day
    imul eax, eax, 86400
    pop ebx                             ; hour << 16 | minute << 8 | second
    movzx ecx, bl
    add eax, ecx
    movzx ecx, bh
    imul ecx, ecx, 60
    add eax, ecx
    shr ebx, 16
    imul ebx, ebx, 3600
    add eax, ebx
    pop edi
    pop esi
    pop edx
    pop ecx
    pop ebx
    ret

; the clock once, after any update in progress: eax = hour << 16 | minute << 8 | second,
; edx = (year - 2000) << 16 | month << 8 | day, each made binary and the hour 24-hour
.read:
.busy:
    mov al, 0x0A
    out CMOS_INDEX, al
    in al, CMOS_DATA
    test al, 0x80
    jnz .busy
    mov al, 0x0B
    out CMOS_INDEX, al
    in al, CMOS_DATA
    mov ah, al                          ; status B: bit 2 binary, bit 1 24-hour
    mov cl, 0x09                        ; year
    call .register
    movzx edx, al
    shl edx, 16
    mov cl, 0x08                        ; month
    call .register
    mov dh, al
    mov cl, 0x07                        ; day of the month
    call .register
    mov dl, al
    mov cl, 0x04                        ; hour (bit 7: PM, in 12-hour mode)
    call .register_raw
    mov bl, al
    and al, 0x7F
    call .binary
    test ah, 2
    jnz .hour_done
    cmp al, 12
    jne .not_twelve
    xor al, al                          ; 12 AM is 0, 12 PM 12
.not_twelve:
    test bl, 0x80
    jz .hour_done
    add al, 12
.hour_done:
    movzx ebx, al
    shl ebx, 16
    mov cl, 0x02                        ; minute
    call .register
    mov bh, al
    mov cl, 0x00                        ; second
    call .register
    mov bl, al
    mov eax, ebx
    ret

; al = CMOS register cl, made binary (ah = status B)
.register:
    call .register_raw
.binary:
    test ah, 4
    jnz .done
    push ecx
    mov cl, al
    and cl, 0x0F                        ; the units
    shr al, 4                           ; the tens, times 10 (8 + 2), keeping ah
    mov ch, al
    shl al, 3
    add al, ch
    add al, ch
    add al, cl
    pop ecx
.done:
    ret

.register_raw:
    mov al, cl
    out CMOS_INDEX, al
    in al, CMOS_DATA
    ret

; days before each month in a year that isn't a leap year
.before:
    dd 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334

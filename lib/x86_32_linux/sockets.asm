; provides: OS_NetAddress OS_UdpOpen OS_UdpClose OS_UdpReceive OS_UdpSend OS_TcpBegin OS_TcpState OS_TcpSend OS_TcpTake OS_TcpClose OS_TcpListen OS_TcpAccept OS_TcpWaiting OS_TcpAddress OS_TcpStopListening
;
; The network for Linux programs (net.sockets): the calls a PHI OS program makes of its
; kernel's network (net.calls), answered by Linux's sockets, so a program that uses the
; network builds for Linux unchanged. Each never waits, as on PHI OS: the sockets are
; non-blocking, and net.sockets does the waiting.
;
; A socket, connection or listener is its file descriptor. Each call checks that the
; number is one these calls opened, of the right kind (phi_socket_kinds), so a program
; can't close its standard output by mistake, as on PHI OS it can't use what isn't its
; own. Addresses and ports are numbers as PHI has them (10.0.2.2 is 0x0A000202); Linux
; wants them high byte first in memory.
;
; Linux's 32-bit socket calls (Linux 4.3 and later) take their arguments in ebx, ecx, edx,
; esi and edi, and give -errno when they fail.
SYS_SOCK_CLOSE     equ 6
SYS_SOCK_IOCTL     equ 54
SYS_SOCK_POLL      equ 168
SYS_SOCKET         equ 359
SYS_BIND           equ 361
SYS_CONNECT        equ 362
SYS_LISTEN         equ 363
SYS_ACCEPT4        equ 364
SYS_GETSOCKOPT     equ 365
SYS_SETSOCKOPT     equ 366
SYS_GETSOCKNAME    equ 367
SYS_GETPEERNAME    equ 368
SYS_SENDTO         equ 369
SYS_RECVFROM       equ 371

AF_INET            equ 2
SOCK_STREAM        equ 1
SOCK_DGRAM         equ 2
SOCK_NONBLOCK      equ 0x800
SOCK_CLOEXEC       equ 0x80000
SOL_SOCKET         equ 1
SO_REUSEADDR       equ 2
SO_ERROR           equ 4
SO_RCVBUF          equ 8
MSG_PEEK           equ 2
MSG_DONTWAIT       equ 0x40
MSG_NOSIGNAL       equ 0x4000
FIONREAD           equ 0x541B
EAGAIN             equ 11
EINPROGRESS        equ 115

SOCKETS_MOST       equ 1024             ; file descriptors these calls keep track of
KIND_UDP           equ 1
KIND_TCP           equ 2                ; a connection
KIND_LISTENER      equ 3
KIND_FAILED        equ 4                ; a connection that couldn't connect, or broke
UDP_MOST           equ 1472             ; what a UdpDatagram holds
DATAGRAM_SIZE      equ 1536             ; the room each queued datagram takes

; each call's frame: [ebp - 16] a sockaddr_in (family, port, address, 8 zeros), [ebp - 20]
; its length, [ebp - 24] a number for setsockopt, getsockopt and ioctl
%define ADDR       ebp - 16
%define ADDR_LEN   ebp - 20
%define OPTION     ebp - 24
%macro PHI_SOCK_ENTER 0
    push ebp
    mov ebp, esp
    sub esp, 24
    push ebx
    push esi
    push edi
%endmacro
%macro PHI_SOCK_LEAVE 0
    pop edi
    pop esi
    pop ebx
    mov esp, ebp
    pop ebp
    ret
%endmacro

; the sockaddr_in at ADDR for address eax and port edx
phi_sock_address:
    mov word [ADDR], AF_INET
    xchg dl, dh
    mov [ADDR + 2], dx
    bswap eax
    mov [ADDR + 4], eax
    mov dword [ADDR + 8], 0
    mov dword [ADDR + 12], 0
    mov dword [ADDR_LEN], 16
    ret

; eax = a new non-blocking socket of type ecx, kept as kind dl; or -1
phi_sock_new:
    push edx
    mov eax, SYS_SOCKET
    mov ebx, AF_INET
    or ecx, SOCK_NONBLOCK | SOCK_CLOEXEC
    xor edx, edx
    int 0x80
    pop edx
    test eax, eax
    js .failed
    cmp eax, SOCKETS_MOST
    jae .too_many
    mov [phi_socket_kinds + eax], dl
    ret
.too_many:
    mov ebx, eax
    mov eax, SYS_SOCK_CLOSE
    int 0x80
.failed:
    mov eax, -1
    ret

; carry clear if eax is a socket of kind dl (or, with dh, kind dh too)
phi_sock_is:
    cmp eax, SOCKETS_MOST
    jae .no
    cmp [phi_socket_kinds + eax], dl
    je .yes
    test dh, dh
    jz .no
    cmp [phi_socket_kinds + eax], dh
    je .yes
.no:
    stc
    ret
.yes:
    clc
    ret

; close socket ebx and forget it
phi_sock_close:
    mov byte [phi_socket_kinds + ebx], 0
    mov eax, SYS_SOCK_CLOSE
    int 0x80
    ret

; bind socket ebx to port edx on every address (ADDR is used); eax = 0 or -errno
phi_sock_bind:
    push ebx
    xor eax, eax
    call phi_sock_address
    pop ebx
    mov eax, SYS_BIND
    lea ecx, [ADDR]
    mov edx, 16
    int 0x80
    ret

; -> this machine's address: the one it would reach the internet from (a UDP socket
; "connected" to 8.8.8.8 sends nothing, but Linux picks the address), or 127.0.0.1
OS_NetAddress:
    PHI_SOCK_ENTER
    mov edi, 0x7F000001
    mov eax, SYS_SOCKET
    mov ebx, AF_INET
    mov ecx, SOCK_DGRAM | SOCK_CLOEXEC
    xor edx, edx
    int 0x80
    test eax, eax
    js .done
    mov esi, eax
    mov eax, 0x08080808
    mov edx, 53
    call phi_sock_address
    mov eax, SYS_CONNECT
    mov ebx, esi
    lea ecx, [ADDR]
    mov edx, 16
    int 0x80
    test eax, eax
    jnz .close
    mov eax, SYS_GETSOCKNAME
    mov ebx, esi
    lea ecx, [ADDR]
    lea edx, [ADDR_LEN]
    int 0x80
    test eax, eax
    jnz .close
    mov eax, [ADDR + 4]
    bswap eax
    test eax, eax
    jz .close
    mov edi, eax
.close:
    mov eax, SYS_SOCK_CLOSE
    mov ebx, esi
    int 0x80
.done:
    mov eax, edi
    PHI_SOCK_LEAVE

; args: port queue -> socket, or -1 if the port is taken. Linux keeps the queue: room for
; queue datagrams is asked for.
OS_UdpOpen:
    PHI_SOCK_ENTER
    mov ecx, SOCK_DGRAM
    mov dl, KIND_UDP
    call phi_sock_new
    test eax, eax
    js .done
    mov esi, eax
    mov ebx, eax
    mov edx, [ebp + 8]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    mov eax, [ebp + 12]
    imul eax, eax, DATAGRAM_SIZE
    mov [OPTION], eax
    mov eax, SYS_SETSOCKOPT
    mov ebx, esi
    mov ecx, SOL_SOCKET
    mov edx, SO_RCVBUF
    lea esi, [OPTION]
    mov edi, 4
    int 0x80
    mov eax, ebx
    jmp .done
.failed:
    mov ebx, esi
    call phi_sock_close
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: socket -> 1, or 0 if it isn't one
OS_UdpClose:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_UDP
    call phi_sock_is
    jc .not
    mov ebx, eax
    call phi_sock_close
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: socket datagram -> 1 with the oldest datagram in it (a UdpDatagram: from,
; from_port, length, data), or 0 if none is waiting
OS_UdpReceive:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_UDP
    call phi_sock_is
    jc .none
    mov ebx, eax
    mov edi, [ebp + 12]
    mov dword [ADDR_LEN], 16
    lea eax, [ADDR_LEN]
    push eax                            ; recvfrom's sixth argument goes in ebp: kept on
    push ebp                            ; the stack meanwhile
    lea eax, [ADDR]
    mov ebp, [esp + 4]                  ; (the length's address)
    mov ecx, edi
    add ecx, 12                         ; data
    mov edx, UDP_MOST
    mov esi, MSG_DONTWAIT
    mov edi, eax                        ; the sender's address
    mov eax, SYS_RECVFROM
    int 0x80
    pop ebp
    add esp, 4
    test eax, eax
    js .none
    mov edi, [ebp + 12]
    mov [edi + 8], eax                  ; length
    mov eax, [ADDR + 4]
    bswap eax
    mov [edi], eax                      ; from
    movzx eax, word [ADDR + 2]
    xchg al, ah
    mov [edi + 4], eax                  ; from_port
    mov eax, 1
    jmp .done
.none:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: socket | port << 16, to, data, length -> 1 if sent
OS_UdpSend:
    PHI_SOCK_ENTER
    movzx eax, word [ebp + 8]
    mov dx, KIND_UDP
    call phi_sock_is
    jc .not
    mov ebx, eax
    mov eax, [ebp + 12]
    movzx edx, word [ebp + 10]
    push ebx
    call phi_sock_address
    pop ebx
    push ebp
    mov ecx, [ebp + 16]
    mov edx, [ebp + 20]
    mov esi, MSG_DONTWAIT | MSG_NOSIGNAL
    lea edi, [ADDR]
    mov ebp, 16
    mov eax, SYS_SENDTO
    int 0x80
    pop ebp
    test eax, eax
    js .not
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; args: to port -> a connection, still connecting, or -1
OS_TcpBegin:
    PHI_SOCK_ENTER
    mov ecx, SOCK_STREAM
    mov dl, KIND_TCP
    call phi_sock_new
    test eax, eax
    js .done
    mov esi, eax
    mov eax, [ebp + 8]
    mov edx, [ebp + 12]
    call phi_sock_address
    mov eax, SYS_CONNECT
    mov ebx, esi
    lea ecx, [ADDR]
    mov edx, 16
    int 0x80
    test eax, eax
    jz .begun
    cmp eax, -EINPROGRESS
    je .begun
    mov ebx, esi
    call phi_sock_close
    mov eax, -1
    jmp .done
.begun:
    mov eax, esi
.done:
    PHI_SOCK_LEAVE

; arg: connection -> 1 open, 0 still connecting, -1 failed (or broken)
OS_TcpState:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dl, KIND_TCP
    mov dh, KIND_FAILED
    call phi_sock_is
    jc .failed
    mov ebx, eax
    cmp byte [phi_socket_kinds + ebx], KIND_FAILED
    je .failed
    ; an error waiting on it (a refused connect, a reset) means it failed, for good
    mov dword [OPTION], 0
    mov dword [ADDR_LEN], 4
    mov eax, SYS_GETSOCKOPT
    mov ecx, SOL_SOCKET
    mov edx, SO_ERROR
    lea esi, [OPTION]
    lea edi, [ADDR_LEN]
    int 0x80
    test eax, eax
    jnz .broke
    cmp dword [OPTION], 0
    jne .broke
    ; connected once it has the other end
    mov dword [ADDR_LEN], 16
    mov eax, SYS_GETPEERNAME
    lea ecx, [ADDR]
    lea edx, [ADDR_LEN]
    int 0x80
    test eax, eax
    jnz .connecting
    mov eax, 1
    jmp .done
.connecting:
    xor eax, eax
    jmp .done
.broke:
    mov byte [phi_socket_kinds + ebx], KIND_FAILED
.failed:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; args: connection data length -> the bytes taken (0 when Linux's buffer is full), or -1
; if it's broken
OS_TcpSend:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_TCP
    call phi_sock_is
    jc .broken
    mov ebx, eax
    mov ecx, [ebp + 12]
    mov edx, [ebp + 16]
    mov esi, MSG_DONTWAIT | MSG_NOSIGNAL
    xor edi, edi
    push ebp
    xor ebp, ebp
    mov eax, SYS_SENDTO
    int 0x80
    pop ebp
    test eax, eax
    jns .done
    cmp eax, -EAGAIN
    jne .broken
    xor eax, eax
    jmp .done
.broken:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; args: connection buffer max -> the bytes read, 0 if none have come yet, -2 once the
; other end has closed and everything is read, -1 if it's broken
OS_TcpTake:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_TCP
    call phi_sock_is
    jc .broken
    cmp dword [ebp + 16], 0
    je .none
    mov ebx, eax
    mov ecx, [ebp + 12]
    mov edx, [ebp + 16]
    mov esi, MSG_DONTWAIT
    xor edi, edi
    push ebp
    xor ebp, ebp
    mov eax, SYS_RECVFROM
    int 0x80
    pop ebp
    test eax, eax
    jg .done
    jz .closed
    cmp eax, -EAGAIN
    jne .broken
.none:
    xor eax, eax
    jmp .done
.closed:
    mov eax, -2
    jmp .done
.broken:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: connection -> 1. Linux sends what's left, then a FIN, after the program has gone on.
OS_TcpClose:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dl, KIND_TCP
    mov dh, KIND_FAILED
    call phi_sock_is
    jc .done
    mov ebx, eax
    call phi_sock_close
.done:
    mov eax, 1
    PHI_SOCK_LEAVE

; args: port backlog -> a listener, or -1 if the port is taken
OS_TcpListen:
    PHI_SOCK_ENTER
    mov ecx, SOCK_STREAM
    mov dl, KIND_LISTENER
    call phi_sock_new
    test eax, eax
    js .done
    mov ebx, eax
    ; a port a program listened on just before can be listened on again at once
    mov dword [OPTION], 1
    mov eax, SYS_SETSOCKOPT
    mov ecx, SOL_SOCKET
    mov edx, SO_REUSEADDR
    lea esi, [OPTION]
    mov edi, 4
    int 0x80
    mov edx, [ebp + 8]
    call phi_sock_bind
    test eax, eax
    jnz .failed
    mov eax, SYS_LISTEN
    mov ecx, [ebp + 12]
    int 0x80
    test eax, eax
    jnz .failed
    mov eax, ebx
    jmp .done
.failed:
    call phi_sock_close
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: listener -> a connection that has come in, or -1 if none is waiting
OS_TcpAccept:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_LISTENER
    call phi_sock_is
    jc .none
    mov ebx, eax
    mov eax, SYS_ACCEPT4
    xor ecx, ecx
    xor edx, edx
    mov esi, SOCK_NONBLOCK | SOCK_CLOEXEC
    int 0x80
    test eax, eax
    js .none
    cmp eax, SOCKETS_MOST
    jae .too_many
    mov byte [phi_socket_kinds + eax], KIND_TCP
    jmp .done
.too_many:
    mov ebx, eax
    mov eax, SYS_SOCK_CLOSE
    int 0x80
.none:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: connection -> the bytes that can be read now, or -1 once the other end has closed
; (and everything is read) or it's broken
OS_TcpWaiting:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_TCP
    call phi_sock_is
    jc .closed
    mov ebx, eax
    mov dword [OPTION], 0
    mov eax, SYS_SOCK_IOCTL
    mov ecx, FIONREAD
    lea edx, [OPTION]
    int 0x80
    test eax, eax
    jnz .closed
    mov eax, [OPTION]
    test eax, eax
    jnz .done
    ; nothing to read: closed, or not yet? A peek says (0 bytes once it's closed)
    lea ecx, [OPTION]
    mov edx, 1
    mov esi, MSG_PEEK | MSG_DONTWAIT
    xor edi, edi
    push ebp
    xor ebp, ebp
    mov eax, SYS_RECVFROM
    int 0x80
    pop ebp
    cmp eax, -EAGAIN
    jne .closed
    xor eax, eax
    jmp .done
.closed:
    mov eax, -1
.done:
    PHI_SOCK_LEAVE

; arg: connection -> the other end's address, or 0
OS_TcpAddress:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_TCP
    call phi_sock_is
    jc .none
    mov ebx, eax
    mov dword [ADDR_LEN], 16
    mov eax, SYS_GETPEERNAME
    lea ecx, [ADDR]
    lea edx, [ADDR_LEN]
    int 0x80
    test eax, eax
    jnz .none
    mov eax, [ADDR + 4]
    bswap eax
    jmp .done
.none:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

; arg: listener -> 1, or 0 if it isn't one; connections waiting for Accept are closed
OS_TcpStopListening:
    PHI_SOCK_ENTER
    mov eax, [ebp + 8]
    mov dx, KIND_LISTENER
    call phi_sock_is
    jc .not
    mov ebx, eax
    call phi_sock_close
    mov eax, 1
    jmp .done
.not:
    xor eax, eax
.done:
    PHI_SOCK_LEAVE

phi_socket_kinds: times SOCKETS_MOST db 0     ; what each file descriptor is to these calls (KIND_...), 0: none

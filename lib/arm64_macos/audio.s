; provides: _phi_audio_played _phi_audio_played_address
;
; Sound in a macOS program (host/sound_macos.phi): an AudioQueue calls this, on a thread of its
; own, as C calls it, when it has played one of the program's buffers: x0 = what the program
; gave it (unused), x1 = the queue, x2 = the buffer (an AudioQueueBuffer: its mUserData, at
; 24, is 1 while it's with the queue). The buffer is the program's to fill again: 0 there,
; written last (a release, so what the queue did with it comes first).
	.section __TEXT,__text,regular,pure_instructions
	.p2align 2
_phi_audio_played:
	add x9, x2, #24
	stlr xzr, [x9]
	ret

; -> the address of _phi_audio_played, for AudioQueueNewOutput
_phi_audio_played_address:
	adrp x0, _phi_audio_played@PAGE
	add x0, x0, _phi_audio_played@PAGEOFF
	ret

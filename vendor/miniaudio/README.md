# miniaudio 0.11.26

Sound played and recorded: files decoded (WAV, FLAC, MP3), sounds mixed, with volume, pitch,
panning, effects and a place in space. <https://miniaud.io/docs/manual/> is the manual.

```phi
use vendor.miniaudio;

phi.Play:Program
{
	ma_engine engine;
	if (Ma.engine_init: 0 addr engine) is not Ma.SUCCESS
		log 'no sound device\n';
		exit 1;
	;;
	int r: (Ma.engine_play_sound: addr engine 'ping.wav' 0);
	log 'Enter ends\n';
	str line: [8];
	ask line;
	call Ma.engine_uninit: addr engine;
	exit 0;
}
```

- A function is `Ma.name` for C's `ma_name` (`Ma.engine_init`, `Ma.sound_set_volume`), and
  a constant `Ma.NAME` for `MA_NAME` or `ma_name` (`Ma.SUCCESS`, `Ma.format_f32`).
- Structs keep C's whole names (`ma_engine`, `ma_engine_config`).
- Some structs differ from one system to the next, so PHI holds them by their address:
  a `ma_sound`, a `ma_decoder`, a `ma_device`, a `ma_context` and the others in
  [left-out.txt](left-out.txt). The library makes one for you, all zeros
  (`ptr<ma_sound> sound: (Ma.new_sound);`), and `Ma.delete` frees it after its `uninit`.
- The engine (`ma_engine`, `ma_sound`) is the easy way in, and all of it is here. A device
  of your own with a callback (`ma_device_config`) isn't: its config holds one of those
  structs.
- [tests/vendor/miniaudio_program.phi](../../tests/vendor/miniaudio_program.phi) makes a
  wave, decodes a WAV from memory, and reads the engine's mix with no sound card.

Built from miniaudio's own source, unchanged ([alloc.c](alloc.c) adds the `new_`
functions). Public domain or MIT-0, as you choose ([LICENSE](LICENSE)). On Linux it finds
ALSA, PulseAudio or JACK when the program runs.

# stb

Sean Barrett's single-file libraries, as one library: pictures read (`stb_image` 2.30: PNG,
JPEG, BMP, GIF, TGA, PSD, HDR) and written (`stb_image_write` 1.19: PNG, BMP, TGA, JPEG),
fonts drawn from TrueType files (`stb_truetype` 1.28), rectangles packed
(`stb_rect_pack` 1.02) and Ogg Vorbis sound decoded (`stb_vorbis` 1.25). Each header's own
comments are its manual: <https://github.com/nothings/stb>.

```phi
use vendor.stb;

phi.Picture:Program
{
	int w: 0;
	int h: 0;
	int channels: 0;
	ptr<u8> pixels: (Stb.stbi_load: 'photo.jpg' addr w addr h addr channels 4);
	if pixels is 0
		log 'not read: ' (Stb.stbi_failure_reason) '\n';
		exit 1;
	;;
	log w ' x ' h '\n';
	int ok: (Stb.stbi_write_png: 'photo.png' w h 4 pixels w * 4);
	call Stb.stbi_image_free: pixels;
	exit 0;
}
```

- Functions have C's whole names: `Stb.stbi_load`, `Stb.stbi_write_png`,
  `Stb.stbtt_InitFont`, `Stb.stbrp_pack_rects`, `Stb.stb_vorbis_decode_filename`.
- Pixels come as bytes, a row after a row, `channels` bytes each (ask for 4: red, green,
  blue, how solid).
- A function stb calls with each piece of a file it writes is a PHI method given as
  `addr foreign Name` ([tests/vendor/stb_program.phi](../../tests/vendor/stb_program.phi)).

Built from stb's own headers, unchanged ([stb.c](stb.c) is all that's added). Public domain
or MIT, as you choose ([LICENSE](LICENSE)).

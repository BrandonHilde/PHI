# zlib 1.4

Compression: deflate, zlib and gzip streams, and their checksums (CRC-32, Adler-32).
<https://zlib.net/manual.html> is the manual.

```phi
use vendor.zlib;

phi.Pack:Program
{
	u8 packed[200];
	u8 back[100];
	ulng packedSize: 200;
	ulng backSize: 100;
	int r: (Z.compress: addr packed addr packedSize 'the same words, the same words, the same words' 47);
	r is (Z.uncompress: addr back addr backSize addr packed packedSize);
	log (packedSize as int) ' bytes packed, and back: ' (addr back) '\n';
	exit 0;
}
```

- Functions and constants have C's names: `Z.compress2`, `Z.crc32`, `Z.Z_BEST_COMPRESSION`,
  `Z.Z_STREAM_END`.
- Lengths zlib writes back are `ulng` (C's `unsigned long`: 4 bytes on Windows, 8 elsewhere).
- A stream is a `z_stream` of the program's own, started with `Z.deflateInit` or
  `Z.inflateInit` (macros in C, methods here) and fed with `Z.deflate` or `Z.inflate`.
  [tests/vendor/zlib_program.phi](../../tests/vendor/zlib_program.phi) does both.
- `.gz` files are read and written with `Z.gzopen`, `Z.gzread`, `Z.gzwrite`, `Z.gzclose`.

Built from zlib's own source, unchanged. Licence: zlib ([LICENSE](LICENSE)).

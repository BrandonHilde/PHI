/* stb's single-file libraries, made one shared library: vendor/fetch.py compiles this for
   each system with the headers it fetched (see package.json). Nothing of stb's is changed. */
#define STB_IMAGE_IMPLEMENTATION
#define STB_IMAGE_WRITE_IMPLEMENTATION
#define STB_RECT_PACK_IMPLEMENTATION
#define STB_TRUETYPE_IMPLEMENTATION
#include "stb_image.h"
#include "stb_image_write.h"
#include "stb_rect_pack.h"
#include "stb_truetype.h"
#include "stb_vorbis.c"

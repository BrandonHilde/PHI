/* A C library for PHI's tests of calling C (tests/clib/c_abi_program.phi): each function
   is one way C takes or gives a value, small enough to check by eye. build.sh makes it for
   macOS and for Windows, 32 and 64 bits. */
#include <stdarg.h>
#include <stdint.h>
#include <string.h>

#if defined(_WIN32)
#define API __declspec(dllexport)
#else
#define API __attribute__((visibility("default")))
#endif

/* whole numbers: more than the registers hold, each kind's width and sign */
API int abi_ints10(int a, int b, int c, int d, int e, int f, int g, int h, int i, int j)
{ return a + 2 * b + 3 * c + 4 * d + 5 * e + 6 * f + 7 * g + 8 * h + 9 * i + 10 * j; }
API int64_t abi_wide(int64_t a, int b, int64_t c) { return a * b + c; }
API uint64_t abi_wide_back(uint32_t high, uint32_t low) { return ((uint64_t)high << 32) | low; }
API int64_t abi_wides9(int64_t a, int64_t b, int64_t c, int64_t d, int64_t e, int64_t f, int64_t g, int64_t h, int64_t i)
{ return a - b + c - d + e - f + g - h + i; }
API int abi_narrow(signed char a, unsigned char b, short c, unsigned short d) { return a + b + c + d; }
API signed char abi_s8(int x) { return (signed char)x; }
API unsigned char abi_u8(int x) { return (unsigned char)x; }
API short abi_s16(int x) { return (short)x; }
API unsigned short abi_u16(int x) { return (unsigned short)x; }

/* floats: both sizes, more than the registers hold, among whole numbers */
API double abi_doubles9(double a, double b, double c, double d, double e, double f, double g, double h, double i)
{ return a + b / 2 + c / 4 + d / 8 + e / 16 + f / 32 + g / 64 + h / 128 + i / 256; }
API float abi_floats(float a, float b, float c) { return a * b + c; }
API double abi_mixed(int a, double b, int c, float d, int64_t e, double f, int g, float h, int i, double j)
{ return a + b + c + d + (double)e + f + g + h + i + j; }
API float abi_half(float x) { return x / 2; }
API int abi_round(double x) { return (int)(x + (x < 0 ? -0.5 : 0.5)); }

/* pointers: text both ways, values C writes through */
API int abi_length(const char *text) { return (int)strlen(text); }
API const char *abi_word(int which) { return which == 0 ? "zero" : which == 1 ? "one" : "many"; }
API void abi_fill(int *into, int count, int from) { for (int i = 0; i < count; i++) into[i] = from + i * i; }
API void abi_outs(int *whole, int64_t *wide, double *real, float *small, unsigned char *byte)
{ *whole = -7; *wide = 6000000000LL; *real = 2.5; *small = 0.25f; *byte = 200; }

/* C's variadic part */
API int abi_sum(int count, ...)
{
    va_list values; int total = 0;
    va_start(values, count);
    for (int i = 0; i < count; i++) total += va_arg(values, int);
    va_end(values);
    return total;
}
API double abi_sum_reals(int count, ...)
{
    va_list values; double total = 0;
    va_start(values, count);
    for (int i = 0; i < count; i++) total += va_arg(values, double);
    va_end(values);
    return total;
}

/* C calling PHI: at once, and later through what it kept */
typedef int (*abi_two)(int, int);
typedef double (*abi_real)(double, int);
typedef int64_t (*abi_many)(int, int64_t, double, int, int, int, int, int, int, int);
static abi_two kept;
API int abi_call(abi_two f, int a, int b) { return f(a, b) + f(b, a); }
API double abi_call_real(abi_real f, double x) { return f(x, 3) + f(x / 2, 1); }
API int64_t abi_call_many(abi_many f) { return f(1, 20000000000LL, 0.5, 4, 5, 6, 7, 8, 9, 10); }
API void abi_keep(abi_two f) { kept = f; }
API int abi_call_kept(int a, int b) { return kept ? kept(a, b) : -1; }
API int abi_each(const int *values, int count, void (*f)(void *, int), void *with)
{ for (int i = 0; i < count; i++) f(with, values[i]); return count; }

/* a struct by its address */
typedef struct { unsigned char tag; int count; double weight; short small; } abi_record;
API int abi_record_size(void) { return (int)sizeof(abi_record); }
API double abi_record_read(const abi_record *r) { return r->tag + r->count + r->weight + r->small; }
API void abi_record_write(abi_record *r) { r->tag = 9; r->count = -1234; r->weight = 0.125; r->small = -5; }

/* structs by value: each size and make-up C's conventions treat their own way */
typedef struct { float x, y; } abi_vec2;                       /* two floats */
typedef struct { float x, y, z; } abi_vec3;                    /* three */
typedef struct { float x, y, w, h; } abi_rect;                 /* four */
typedef struct { double x, y; } abi_dvec2;                     /* two doubles */
typedef struct { unsigned char r, g, b, a; } abi_color;        /* 4 bytes */
typedef struct { int x, y; } abi_point;                        /* 8 bytes */
typedef struct { int id; float weight; int count; } abi_mixed3; /* 12 bytes, floats and not */
typedef struct { int64_t a, b; } abi_pair;                     /* 16 bytes */
typedef struct { unsigned id; int width, height, mips, format; } abi_texture;  /* 20 bytes */
typedef struct { abi_vec2 offset, target; float turn, zoom; } abi_camera;       /* 24 bytes, structs inside */

API float abi_vec2_sum(abi_vec2 v) { return v.x + 2 * v.y; }
API float abi_vec3_sum(abi_vec3 v) { return v.x + 2 * v.y + 4 * v.z; }
API float abi_rect_area(abi_rect r) { return r.w * r.h + r.x - r.y; }
API double abi_dvec2_sum(abi_dvec2 v) { return v.x + 2 * v.y; }
API int abi_color_sum(abi_color c) { return c.r + 2 * c.g + 4 * c.b + 8 * c.a; }
API int abi_point_sum(abi_point p) { return p.x - 2 * p.y; }
API double abi_mixed3_sum(abi_mixed3 m) { return m.id + m.weight + m.count; }
API int64_t abi_pair_sum(abi_pair p) { return p.a - p.b; }
API int abi_texture_sum(abi_texture t) { return (int)t.id + t.width + t.height + t.mips + t.format; }
API float abi_camera_sum(abi_camera c) { return c.offset.x + c.offset.y + c.target.x + c.target.y + c.turn + c.zoom; }

/* among other values, and several at once */
API float abi_draw(int id, abi_vec2 at, float radius, abi_color color, abi_rect in, const char *name)
{ return id + at.x + at.y + radius + color.g + in.w + (float)strlen(name); }
API double abi_many_structs(abi_vec2 a, abi_vec2 b, abi_vec2 c, abi_vec2 d, abi_vec2 e, abi_point p, abi_pair q, abi_texture t, abi_color k, double last)
{ return a.x + b.y + c.x + d.y + e.x + e.y + p.x + p.y + (double)q.a + (double)q.b + t.format + k.a + last; }

/* given back */
API abi_vec2 abi_make_vec2(float x, float y) { abi_vec2 v = { x, y }; return v; }
API abi_vec3 abi_make_vec3(float x) { abi_vec3 v = { x, x * 2, x * 3 }; return v; }
API abi_rect abi_make_rect(float x, float size) { abi_rect r = { x, x + 1, size, size * 2 }; return r; }
API abi_dvec2 abi_make_dvec2(double x) { abi_dvec2 v = { x, -x }; return v; }
API abi_color abi_make_color(int grey) { abi_color c = { (unsigned char)grey, (unsigned char)(grey + 1), (unsigned char)(grey + 2), 255 }; return c; }
API abi_point abi_make_point(int x, int y) { abi_point p = { x, y }; return p; }
API abi_mixed3 abi_make_mixed3(int id) { abi_mixed3 m = { id, id / 2.0f, -id }; return m; }
API abi_pair abi_make_pair(int64_t a) { abi_pair p = { a, a * 3 }; return p; }
API abi_texture abi_make_texture(int id) { abi_texture t = { (unsigned)id, 640, 480, 1, 7 }; return t; }
API abi_camera abi_make_camera(float zoom) { abi_camera c = { { 1, 2 }, { 3, 4 }, 0.5f, zoom }; return c; }
/* taken and given back */
API abi_vec2 abi_vec2_add(abi_vec2 a, abi_vec2 b) { abi_vec2 v = { a.x + b.x, a.y + b.y }; return v; }
API abi_texture abi_texture_half(abi_texture t) { t.width /= 2; t.height /= 2; t.mips++; return t; }

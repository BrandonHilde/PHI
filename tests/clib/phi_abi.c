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

/* by value is a copy: these write over all of what they're given (through a volatile
   pointer, so the writes are really made), which the caller's struct mustn't show */
typedef struct { unsigned char c[152]; } abi_long;             /* 152 bytes */
API int abi_texture_spoil(abi_texture t)
{
    volatile abi_texture *p = &t;
    int sum = (int)p->id + p->width + p->height + p->mips + p->format;
    p->id = 0; p->width = -1; p->height = -1; p->mips = -1; p->format = -1;
    return sum + p->width;
}
API int abi_long_spoil(int a, int b, int c, int d, int e, int f, int g, int h, abi_long l, abi_texture t)
{
    volatile abi_long *p = &l;
    volatile abi_texture *q = &t;
    int sum = a + b + c + d + e + f + g + h + q->height, i;
    for (i = 0; i < 152; i++) { sum += p->c[i] * (i + 1); p->c[i] = 0xEE; }
    q->height = 0; q->id = 0;
    return sum;
}

/* C's long and unsigned long: 32 bits on Windows, 64 on macOS and Linux (PHI's lng and ulng) */
typedef struct { int id; long size; unsigned long mask; } abi_longs;
API long abi_long_neg(long v) { return -v; }
API unsigned long abi_ulong_top(void) { return (unsigned long)-1; }
API int abi_long_bytes(void) { return (int)sizeof(long); }
API long abi_longs_sum(const abi_longs *l) { return l->id + l->size + (long)(l->mask & 0xFF) + (long)sizeof(abi_longs); }
API void abi_long_out(long *out, unsigned long *top) { *out = -77; *top = (unsigned long)-1; }

/* C calling PHI on threads of the library's own (as a library that plays sound does):
   f(a, b) once on one thread, and f(thread, i) `times` times on each of `threads` threads
   at once, the results added up */
#if defined(_WIN32)
#include <windows.h>
typedef HANDLE abi_thread;
#define ABI_THREAD_RESULT DWORD WINAPI
static abi_thread abi_start(LPTHREAD_START_ROUTINE run, void *with) { return CreateThread(0, 0, run, with, 0, 0); }
static void abi_wait(abi_thread t) { WaitForSingleObject(t, INFINITE); CloseHandle(t); }
#else
#include <pthread.h>
typedef pthread_t abi_thread;
#define ABI_THREAD_RESULT void *
static abi_thread abi_start(void *(*run)(void *), void *with) { pthread_t t; pthread_create(&t, 0, run, with); return t; }
static void abi_wait(abi_thread t) { pthread_join(t, 0); }
#endif
typedef struct { abi_two f; int a, times; int64_t result; } abi_job;
static ABI_THREAD_RESULT abi_run(void *with)
{
    abi_job *job = with;
    int i;
    for (i = 0; i < job->times; i++) job->result += job->f(job->a, i);
    return 0;
}
API int abi_call_on_thread(abi_two f, int a, int b)
{
    abi_job job = { f, a, 1, 0 };
    (void)b;
    abi_wait(abi_start(abi_run, &job));
    return (int)job.result;
}
API int64_t abi_call_on_threads(abi_two f, int threads, int times)
{
    abi_job jobs[16];
    abi_thread started[16];
    int64_t sum = 0;
    int i;
    if (threads > 16) threads = 16;
    for (i = 0; i < threads; i++) { abi_job job = { f, i + 1, times, 0 }; jobs[i] = job; }
    for (i = 0; i < threads; i++) started[i] = abi_start(abi_run, &jobs[i]);
    for (i = 0; i < threads; i++) { abi_wait(started[i]); sum += jobs[i].result; }
    return (int)sum;
}

/* what C keeps for each thread (its own variables, its malloc's): called from threads the
   program started, each must have its own */
#include <stdlib.h>
#if defined(_MSC_VER)
#define ABI_OWN __declspec(thread)
#else
#define ABI_OWN __thread
#endif
static ABI_OWN int abi_mine;
API int abi_thread_work(int id, int rounds)
{
    int i, sum = 0;
    abi_mine = id;
    for (i = 0; i < rounds; i++)
    {
        char *p = malloc(64 + i % 100);
        if (!p) return -1;
        memset(p, id, 64);
        sum += p[10];
        free(p);
        if (abi_mine != id) return -2;
    }
    return sum;
}

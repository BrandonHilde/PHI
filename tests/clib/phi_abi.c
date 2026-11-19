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

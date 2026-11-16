#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

/* Pixels: a 4 MB image brightened (stopping at 255) and blended with another (each byte the
   average of the two, rounding up), 200 times, as plain loops (gcc makes vector code of them). */
static int64_t run(int bytes, int rounds)
{
    uint8_t *image = malloc(bytes), *other = malloc(bytes);
    for (int i = 0; i < bytes; i++) {
        image[i] = (uint8_t)(i * 7);
        other[i] = (uint8_t)(i * 13 + 5);
    }
    for (int r = 0; r < rounds; r++)
        for (int i = 0; i < bytes; i++) {
            int brighter = image[i] + 3;
            if (brighter > 255) brighter = 255;
            image[i] = (uint8_t)((brighter + other[i] + 1) >> 1);
        }
    int64_t sum = 0;
    for (int i = 0; i < bytes; i++) sum += image[i];
    free(image);
    free(other);
    return sum;
}

int main(void)
{
    printf("%lld\n", (long long)run(4000000, 200));
    return 0;
}

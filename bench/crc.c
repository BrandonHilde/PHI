#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

static uint32_t table[256];

static uint32_t run(int size, int rounds)
{
    for (uint32_t n = 0; n < 256; n++) {
        uint32_t c = n;
        for (int k = 0; k < 8; k++) {
            if ((c & 1) == 1) c = (c >> 1) ^ 0xEDB88320u;
            else c = c >> 1;
        }
        table[n] = c;
    }
    unsigned char *bytes = malloc(size);
    uint32_t seed = 1;
    for (int i = 0; i < size; i++) {
        seed = seed * 1103515245u + 12345u;
        bytes[i] = (unsigned char)(seed >> 16);
    }
    uint32_t crc = 0;
    for (int r = 0; r < rounds; r++) {
        crc = crc ^ 0xFFFFFFFFu;
        for (int i = 0; i < size; i++)
            crc = table[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
        crc = crc ^ 0xFFFFFFFFu;
    }
    return crc;
}

int main(void)
{
    printf("%u\n", run(16777216, 8));
    return 0;
}

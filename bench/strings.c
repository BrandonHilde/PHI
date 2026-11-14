#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

static uint32_t round_(int count)
{
    char line[81], wrapped[81];
    size_t room = 64, length = 0;
    char *all = malloc(room);
    all[0] = 0;
    uint32_t sum = 0;
    for (int i = 0; i < count; i++) {
        int n = snprintf(line, sizeof line, "item %d, of %d", i, count);
        snprintf(line + n, sizeof line - (size_t)n, " (%d)", i * 3);
        snprintf(wrapped, sizeof wrapped, "[%s]", line);
        strcpy(line, wrapped);
        size_t add = strlen(line) + 1;
        if (length + add + 1 > room) {
            while (length + add + 1 > room) room *= 2;
            all = realloc(all, room);
        }
        memcpy(all + length, line, add - 1);
        all[length + add - 1] = ';';
        length += add;
        all[length] = 0;
        sum += (uint8_t)line[i % 8];
    }
    sum += (uint32_t)strlen(all);
    free(all);
    return sum;
}

int main(void)
{
    uint32_t total = 0;
    for (int r = 0; r < 3000; r++) total += round_(1000);
    printf("%u\n", total);
    return 0;
}

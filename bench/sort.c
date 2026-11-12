#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

static void quick(uint32_t *a, int low, int high)
{
    while (low < high) {
        uint32_t pivot = a[(low + high) / 2];
        int i = low, j = high;
        while (i <= j) {
            while (a[i] < pivot) i++;
            while (a[j] > pivot) j--;
            if (i <= j) {
                uint32_t kept = a[i];
                a[i] = a[j];
                a[j] = kept;
                i++;
                j--;
            }
        }
        if (j - low < high - i) {
            quick(a, low, j);
            low = i;
        } else {
            quick(a, i, high);
            high = j;
        }
    }
}

static uint32_t run(int count)
{
    uint32_t *numbers = malloc((size_t)count * sizeof(uint32_t));
    uint32_t seed = 12345;
    for (int i = 0; i < count; i++) {
        seed = seed * 1664525u + 1013904223u;
        numbers[i] = seed >> 8;
    }
    quick(numbers, 0, count - 1);
    uint32_t sum = 0;
    for (int k = 0; k < count; k += 1000) sum += numbers[k];
    for (int m = 1; m < count; m++)
        if (numbers[m - 1] > numbers[m]) sum = 0;
    return sum;
}

int main(void)
{
    printf("%u\n", run(4000000));
    return 0;
}

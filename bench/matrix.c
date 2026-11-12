#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

static int64_t run(int n, int rounds)
{
    double *a = malloc((size_t)n * n * sizeof(double));
    double *b = malloc((size_t)n * n * sizeof(double));
    double *c = malloc((size_t)n * n * sizeof(double));
    for (int i = 0; i < n; i++) {
        for (int j = 0; j < n; j++) {
            a[i * n + j] = (double)((i + j) % 7);
            b[i * n + j] = (double)((i * 3 + j) % 5);
        }
    }
    int64_t sum = 0;
    for (int r = 0; r < rounds; r++) {
        for (int i = 0; i < n; i++) {
            for (int j = 0; j < n; j++) {
                double cell = 0.0;
                for (int k = 0; k < n; k++) cell += a[i * n + k] * b[k * n + j];
                c[i * n + j] = cell;
            }
        }
        for (int d = 0; d < n; d++) sum += (int64_t)c[d * n + d];
    }
    return sum;
}

int main(void)
{
    printf("%lld\n", (long long)run(300, 8));
    return 0;
}

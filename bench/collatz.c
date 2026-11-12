#include <stdio.h>
#include <stdint.h>

static int longest(int limit)
{
    int best = 0, best_start = 0;
    for (int start = 1; start < limit; start++) {
        int64_t n = start;
        int steps = 0;
        while (n != 1) {
            if ((n & 1) == 0) n = n >> 1;
            else n = n * 3 + 1;
            steps++;
        }
        if (steps > best) {
            best = steps;
            best_start = start;
        }
    }
    return best_start + best;
}

int main(void)
{
    printf("%d\n", longest(2000000));
    return 0;
}

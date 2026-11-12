#include <stdio.h>
#include <stdlib.h>

static int run(int size, int rounds)
{
    unsigned char *composite = calloc(size, 1);
    int count = 0;
    for (int r = 0; r < rounds; r++) {
        count = 0;
        for (int z = 0; z < size; z++) composite[z] = 0;
        for (int i = 2; i < size; i++) {
            if (composite[i] == 0) {
                count++;
                for (int j = i + i; j < size; j += i) composite[j] = 1;
            }
        }
    }
    return count;
}

int main(void)
{
    printf("%d\n", run(5000000, 20));
    return 0;
}

#include <stdio.h>

static int run(int size, int most)
{
    int total = 0;
    for (int py = 0; py < size; py++) {
        double ci = (double)py * 2.0 / (double)size - 1.0;
        for (int px = 0; px < size; px++) {
            double cr = (double)px * 3.0 / (double)size - 2.0;
            double zr = 0.0, zi = 0.0;
            int steps = 0;
            while (steps < most && zr * zr + zi * zi <= 4.0) {
                double next = zr * zr - zi * zi + cr;
                zi = 2.0 * zr * zi + ci;
                zr = next;
                steps++;
            }
            total += steps;
        }
    }
    return total;
}

int main(void)
{
    printf("%d\n", run(1000, 200));
    return 0;
}

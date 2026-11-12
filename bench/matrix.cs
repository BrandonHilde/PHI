using System;

public static class Matrix
{
    static long Run(int n, int rounds)
    {
        double[] a = new double[n * n];
        double[] b = new double[n * n];
        double[] c = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                a[i * n + j] = (double)((i + j) % 7);
                b[i * n + j] = (double)((i * 3 + j) % 5);
            }
        }
        long sum = 0;
        for (int r = 0; r < rounds; r++)
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    double cell = 0.0;
                    for (int k = 0; k < n; k++) cell += a[i * n + k] * b[k * n + j];
                    c[i * n + j] = cell;
                }
            }
            for (int d = 0; d < n; d++) sum += (long)c[d * n + d];
        }
        return sum;
    }

    public static void Main()
    {
        Console.WriteLine(Run(300, 8));
    }
}

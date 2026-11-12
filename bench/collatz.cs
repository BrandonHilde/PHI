using System;

public static class Collatz
{
    static int Longest(int limit)
    {
        int best = 0, bestStart = 0;
        for (int start = 1; start < limit; start++)
        {
            long n = start;
            int steps = 0;
            while (n != 1)
            {
                if ((n & 1) == 0) n = n >> 1;
                else n = n * 3 + 1;
                steps++;
            }
            if (steps > best)
            {
                best = steps;
                bestStart = start;
            }
        }
        return bestStart + best;
    }

    public static void Main()
    {
        Console.WriteLine(Longest(2000000));
    }
}

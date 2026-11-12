using System;

public static class Sieve
{
    static int Run(int size, int rounds)
    {
        byte[] composite = new byte[size];
        int count = 0;
        for (int r = 0; r < rounds; r++)
        {
            count = 0;
            for (int z = 0; z < size; z++) composite[z] = 0;
            for (int i = 2; i < size; i++)
            {
                if (composite[i] == 0)
                {
                    count++;
                    for (int j = i + i; j < size; j += i) composite[j] = 1;
                }
            }
        }
        return count;
    }

    public static void Main()
    {
        Console.WriteLine(Run(5000000, 20));
    }
}

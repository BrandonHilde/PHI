using System;

public static class Sort
{
    static void Quick(uint[] a, int low, int high)
    {
        while (low < high)
        {
            uint pivot = a[(low + high) / 2];
            int i = low, j = high;
            while (i <= j)
            {
                while (a[i] < pivot) i++;
                while (a[j] > pivot) j--;
                if (i <= j)
                {
                    uint kept = a[i];
                    a[i] = a[j];
                    a[j] = kept;
                    i++;
                    j--;
                }
            }
            if (j - low < high - i)
            {
                Quick(a, low, j);
                low = i;
            }
            else
            {
                Quick(a, i, high);
                high = j;
            }
        }
    }

    static uint Run(int count)
    {
        uint[] numbers = new uint[count];
        uint seed = 12345;
        for (int i = 0; i < count; i++)
        {
            seed = unchecked(seed * 1664525u + 1013904223u);
            numbers[i] = seed >> 8;
        }
        Quick(numbers, 0, count - 1);
        uint sum = 0;
        for (int k = 0; k < count; k += 1000) sum = unchecked(sum + numbers[k]);
        for (int m = 1; m < count; m++)
            if (numbers[m - 1] > numbers[m]) sum = 0;
        return sum;
    }

    public static void Main()
    {
        Console.WriteLine(Run(4000000));
    }
}

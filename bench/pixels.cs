using System;

// Pixels: a 4 MB image brightened (stopping at 255) and blended with another (each byte the
// average of the two, rounding up), 200 times, as plain loops.
public static class Pixels
{
    static long Run(int bytes, int rounds)
    {
        byte[] image = new byte[bytes], other = new byte[bytes];
        for (int i = 0; i < bytes; i++)
        {
            image[i] = unchecked((byte)(i * 7));
            other[i] = unchecked((byte)(i * 13 + 5));
        }
        for (int r = 0; r < rounds; r++)
            for (int i = 0; i < bytes; i++)
            {
                int brighter = image[i] + 3;
                if (brighter > 255) brighter = 255;
                image[i] = (byte)((brighter + other[i] + 1) >> 1);
            }
        long sum = 0;
        for (int i = 0; i < bytes; i++) sum += image[i];
        return sum;
    }

    public static void Main() => Console.WriteLine(Run(4000000, 200));
}

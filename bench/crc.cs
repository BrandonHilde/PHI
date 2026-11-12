using System;

public static class Crc
{
    static readonly uint[] table = new uint[256];

    static uint Run(int size, int rounds)
    {
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                if ((c & 1) == 1) c = (c >> 1) ^ 0xEDB88320u;
                else c = c >> 1;
            }
            table[n] = c;
        }
        byte[] bytes = new byte[size];
        uint seed = 1;
        for (int i = 0; i < size; i++)
        {
            seed = unchecked(seed * 1103515245u + 12345u);
            bytes[i] = (byte)(seed >> 16);
        }
        uint crc = 0;
        for (int r = 0; r < rounds; r++)
        {
            crc = crc ^ 0xFFFFFFFFu;
            for (int i = 0; i < size; i++)
                crc = table[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
            crc = crc ^ 0xFFFFFFFFu;
        }
        return crc;
    }

    public static void Main()
    {
        Console.WriteLine(Run(16777216, 8));
    }
}

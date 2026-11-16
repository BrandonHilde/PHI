using System;
using System.Collections.Generic;

public static class Maps
{
    static uint Round(uint count)
    {
        const uint spread = 2654435761;
        var map = new Dictionary<uint, uint>();
        for (uint i = 0; i < count; i++) map[unchecked(i * spread)] = i;
        uint sum = 0;
        for (uint i = 0; i < count; i++)
            sum = unchecked(sum + (map.TryGetValue(unchecked(i * spread), out uint v) ? v : 0));
        for (uint i = 0; i < count; i++)
            if (map.ContainsKey(unchecked((count + i) * spread))) sum = unchecked(sum + 1000000);
        for (uint i = 0; i < count; i += 2) map.Remove(unchecked(i * spread));
        for (uint i = 0; i < count; i++)
            sum = unchecked(sum + (map.TryGetValue(unchecked(i * spread), out uint v) ? v : 0));
        return unchecked(sum + (uint)map.Count);
    }

    public static void Main()
    {
        uint total = 0;
        for (int r = 0; r < 50; r++) total = unchecked(total + Round(100000));
        Console.WriteLine(total);
    }
}

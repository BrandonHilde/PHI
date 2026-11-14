using System;
using System.Text;

public static class Strings
{
    static uint Round(int count)
    {
        var all = new StringBuilder();
        uint sum = 0;
        for (int i = 0; i < count; i++)
        {
            string line = "item " + i + ", of " + count;
            line += " (" + (i * 3) + ")";
            line = "[" + line + "]";
            all.Append(line).Append(';');
            sum += line[i % 8];
        }
        return sum + (uint)all.Length;
    }

    public static void Main()
    {
        uint total = 0;
        for (int r = 0; r < 3000; r++) total = unchecked(total + Round(1000));
        Console.WriteLine(total);
    }
}

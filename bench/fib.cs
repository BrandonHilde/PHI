using System;

public static class Fib
{
    static int Of(int n)
    {
        if (n < 2) return n;
        return Of(n - 1) + Of(n - 2);
    }

    public static void Main()
    {
        Console.WriteLine(Of(40));
    }
}

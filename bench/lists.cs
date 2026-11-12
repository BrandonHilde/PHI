using System;

public static class Lists
{
    sealed class Node
    {
        public uint Value;
        public Node Next;
    }

    static uint Round(int count)
    {
        Node head = null;
        for (int i = 0; i < count; i++)
        {
            Node node = new Node();
            node.Value = (uint)i;
            node.Next = head;
            head = node;
        }
        uint sum = 0;
        for (Node walk = head; walk != null; walk = walk.Next) sum = unchecked(sum + walk.Value);
        return sum;
    }

    public static void Main()
    {
        uint total = 0;
        for (int r = 0; r < 200; r++) total = unchecked(total + Round(100000));
        Console.WriteLine(total);
    }
}

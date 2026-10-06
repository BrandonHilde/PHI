namespace Phi.Compiler
{
    public sealed class SourceFile
    {
        public string Path { get; }
        public string Text { get; }
        readonly List<int> lineStarts = new() { 0 };

        public SourceFile(string path, string text)
        {
            Path = path;
            Text = text;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') lineStarts.Add(i + 1);
        }

        public static SourceFile Load(string path) => new(path, File.ReadAllText(path));

        /// <returns>1-based line and column</returns>
        public (int Line, int Column) GetLocation(int offset)
        {
            int index = lineStarts.BinarySearch(offset);
            if (index < 0) index = ~index - 1;
            return (index + 1, offset - lineStarts[index] + 1);
        }
    }

    public readonly record struct Span(int Start, int Length)
    {
        public int End => Start + Length;
        public static Span FromBounds(int start, int end) => new(start, end - start);
        public Span To(Span other) => FromBounds(Start, Math.Max(End, other.End));
    }

    public enum Severity { Error, Warning }

    public sealed record Diagnostic(Severity Severity, SourceFile? File, Span Span, string Message)
    {
        public override string ToString()
        {
            string kind = Severity == Severity.Error ? "error" : "warning";
            if (File == null) return $"{kind}: {Message}";

            var (line, col) = File.GetLocation(Span.Start);
            return $"{File.Path}:{line}:{col}: {kind}: {Message}";
        }
    }

    public sealed class DiagnosticBag
    {
        readonly List<Diagnostic> items = new();
        public IReadOnlyList<Diagnostic> Items => items;
        public bool HasErrors => items.Any(d => d.Severity == Severity.Error);

        public void Error(SourceFile? file, Span span, string message) =>
            items.Add(new Diagnostic(Severity.Error, file, span, message));

        public void Warning(SourceFile? file, Span span, string message) =>
            items.Add(new Diagnostic(Severity.Warning, file, span, message));
    }
}

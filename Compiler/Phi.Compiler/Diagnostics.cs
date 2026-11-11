namespace Phi.Compiler
{
    public sealed class SourceFile
    {
        public string Path { get; }
        public string Text { get; }

        /// <summary>For a file of a compiled library: declarations with no code, from a .philib.</summary>
        public Libraries.CompiledTarget? Compiled { get; init; }
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

        /// <summary>Where a 0-based line and column are in the text, kept inside it.</summary>
        public int GetOffset(int line, int column)
        {
            if (line < 0) return 0;
            if (line >= lineStarts.Count) return Text.Length;
            int end = line + 1 < lineStarts.Count ? lineStarts[line + 1] : Text.Length;
            return Math.Clamp(lineStarts[line] + column, lineStarts[line], end);
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

        /// <summary>
        /// A struct made from a template is read from a copy of the template's file, so what's
        /// said about it can be told from what's said about the template's other uses: each is
        /// reported at the real file, with which use it was, and once if every use has it.
        /// </summary>
        readonly Dictionary<SourceFile, (SourceFile Real, string Note)> copies = new();

        public void CopyOf(SourceFile copy, SourceFile real, string note) => copies[copy] = (real, note);

        public void Error(SourceFile? file, Span span, string message) => Add(Severity.Error, file, span, message);

        public void Warning(SourceFile? file, Span span, string message) => Add(Severity.Warning, file, span, message);

        void Add(Severity severity, SourceFile? file, Span span, string message)
        {
            if (file != null && copies.TryGetValue(file, out var copy))
            {
                // (the same thing wrong in another use of the template is the template's, said once)
                if (items.Any(d => d.File == copy.Real && d.Span == span && d.Severity == severity
                                   && (d.Message == message || d.Message.StartsWith(message + " (in ")))) return;
                file = copy.Real;
                message += copy.Note;
            }
            items.Add(new Diagnostic(severity, file, span, message));
        }
    }
}

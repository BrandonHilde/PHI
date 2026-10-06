using Phi.Compiler.CodeGen;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler
{
    public sealed class CompileResult
    {
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
        public bool Success => !Diagnostics.Any(d => d.Severity == Severity.Error);

        /// <summary>The boot sector first, then the kernel if the program has OS classes.</summary>
        public List<AsmUnit> Units { get; } = new();

        public ProgramNode? Syntax { get; init; }
        public BoundProgram? Bound { get; init; }
    }

    /// <summary>source -> Lexer -> Parser -> Binder -> X86_16Generator -> NASM source</summary>
    public static class PhiCompiler
    {
        public static CompileResult Compile(SourceFile file, bool generate = true)
        {
            var diagnostics = new DiagnosticBag();

            ProgramNode syntax = Parser.Parse(file, diagnostics);
            if (diagnostics.HasErrors) return new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax };

            BoundProgram? bound = Binder.Bind(syntax, diagnostics);
            if (bound == null || diagnostics.HasErrors || !generate)
                return new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound };

            var units = new List<AsmUnit> { X86_16Generator.Generate(bound, bound.Boot, diagnostics) };
            if (bound.Kernel != null) units.Add(X86_16Generator.Generate(bound, bound.Kernel, diagnostics));

            var result = new CompileResult { Diagnostics = diagnostics.Items, Syntax = syntax, Bound = bound };
            if (result.Success) result.Units.AddRange(units);
            return result;
        }

        public static CompileResult Compile(string path) => Compile(SourceFile.Load(path));
    }
}

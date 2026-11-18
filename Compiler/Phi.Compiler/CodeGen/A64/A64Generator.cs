using Phi.Compiler.Semantics;

namespace Phi.Compiler.CodeGen.A64
{
    /// <summary>
    /// A macOS program's code (Apple Silicon, arm64): assembly for Apple's clang, which assembles
    /// it and links it with libSystem into a Mach-O executable (Phi.Cli/Builder.cs). Every method
    /// and every class's statements are written from the intermediate form (Ir/), as a 64-bit
    /// program's x86 methods are; there's no other way here, so what the form doesn't take yet is
    /// an error, "... which macOS programs can't do yet". That's also what phi test goes by: a
    /// test runs as a macOS program once it builds as one.
    /// </summary>
    public sealed partial class A64Generator
    {
        /// <summary>What a check (no code wanted) finds: the same as a build, without the text.</summary>
        public static void Check(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics) =>
            diagnostics.Error(program.File, default, "macOS programs can't be made yet: the ARM code generator is being written (see Plan.md, the macOS target)");

        public static AsmUnit Generate(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics)
        {
            Check(program, unit, diagnostics);
            return new AsmUnit { Kind = unit.Kind, Name = "program", Text = "" };
        }
    }
}

using Phi.Compiler.Semantics;
using Phi.Compiler;

namespace Phi.Compiler.CodeGen
{
    /// <summary>
    /// What a debugger needs to know of a program's code, from a --debug build: which line each
    /// piece of code came from, its methods and where each variable is. It goes by the assembly's
    /// labels, as nothing has an address until NASM has run.
    /// </summary>
    public sealed class DebugInfo
    {
        /// <summary>The room for the record that says which .pdb goes with the program.</summary>
        public const int RecordSize = 512;

        public List<DebugMethod> Methods { get; } = new();

        /// <summary>Where each line's code starts, in the order they come in the program.</summary>
        public List<DebugLine> Lines { get; } = new();
    }

    public sealed record DebugLine(string Label, int Line, SourceFile File);

    public sealed class DebugMethod
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";

        /// <summary>The label just past its last instruction.</summary>
        public string EndLabel { get; init; } = "";

        /// <summary>Not a method: what the classes' own statements do, when the program starts.</summary>
        public bool IsStart { get; init; }

        public List<DebugVariable> Variables { get; } = new();
    }

    /// <param name="Place">The label of its storage, or "ebp + 8", "ebp - 4" for its place in the frame.</param>
    /// <param name="Grows">A str whose storage starts with the address of its text.</param>
    /// <param name="Capacity">A str's bytes, kept in its storage, if it doesn't grow.</param>
    /// <param name="TextRef">A parameter that is the address of the caller's text.</param>
    public sealed record DebugVariable(string Name, PhiType Type, string Place, bool Grows, int Capacity, bool TextRef)
    {
        /// <summary>The standard library's own: there to ask for, but not listed with the program's.</summary>
        public bool Library { get; init; }
    }
}

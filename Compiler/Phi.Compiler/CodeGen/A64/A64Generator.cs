using System.Text;
using System.Text.RegularExpressions;
using Phi.Compiler.CodeGen.Ir;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen.A64
{
    /// <summary>
    /// A macOS program's code (Apple Silicon, arm64): assembly for Apple's clang, which assembles
    /// it and links it with libSystem into a Mach-O executable (Phi.Cli/Builder.cs). Every method,
    /// and every class's statements (as a method of their own), are written from the intermediate
    /// form (Ir/), as a 64-bit program's x86 methods are; there's no other way here, so what the
    /// form doesn't take, or this doesn't write yet, is an error, "... which macOS programs can't
    /// do yet". That's also what phi test goes by: a test runs as a macOS program once it builds
    /// as one.
    ///
    /// The contract between PHI's methods is x86's, so the intermediate form's places in a frame
    /// mean the same here: a frame on x29 (rbp in the form's places) made by stp x29, x30 and
    /// mov x29, sp, so a method's arguments are at x29 + 16 up, the last first, 8 bytes each;
    /// the caller writes them into the bottom of its own frame (sp stays where the frame put it,
    /// 16-aligned, the whole method: no pushes). A result is in x0 (w0 for 32 bits; a 64-bit
    /// number whole, not edx:eax). Values live in x10-x15 (a call may change them) and x19-x27
    /// (every method keeps them); x9, x16, x17 and x0-x8 are the emitter's own; x18 is Apple's,
    /// never touched; x28 is kept for this thread's block. The library's routines
    /// (lib/arm64_macos/*.s) are ordinary AAPCS64 functions, so they call libSystem directly.
    /// </summary>
    public sealed partial class A64Generator
    {
        readonly BoundProgram program;
        readonly BoundUnit unit;
        readonly DiagnosticBag diagnostics;
        readonly StringBuilder code = new();
        readonly SortedSet<string> needed = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);
        SourceFile file;
        int labels;
        bool failed;

        /// <summary>The runtime's folder for macOS programs: lib/arm64_macos.</summary>
        const string Target = "arm64_macos";

        A64Generator(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics)
        {
            this.program = program;
            this.unit = unit;
            this.diagnostics = diagnostics;
            file = program.File;
        }

        /// <summary>What a check (no code wanted) finds: the same as a build, without the text.</summary>
        public static void Check(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics) =>
            new A64Generator(program, unit, diagnostics).Build();

        public static AsmUnit Generate(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics)
        {
            var generator = new A64Generator(program, unit, diagnostics);
            string text = generator.Build();
            return new AsmUnit { Kind = unit.Kind, Name = "program", Text = text, Links = generator.Links() };
        }

        /// <summary>
        /// The linker's arguments for the outside libraries the program calls: a framework
        /// ('AppKit.framework', or its path) is -framework, a library ('libobjc.A.dylib') -l;
        /// libSystem is every program's already.
        /// </summary>
        List<string> Links()
        {
            var links = new List<string>();
            foreach (string path in unit.OutsideFunctions.Select(f => f.LibraryPath).Distinct())
            {
                string name = Path.GetFileName(path);
                var framework = Regex.Match(path, @"([^/]+)\.framework");
                if (framework.Success) links.AddRange(new[] { "-framework", framework.Groups[1].Value });
                else if (Regex.Match(name, @"^lib([^.]+)") is { Success: true } lib)
                {
                    if (lib.Groups[1].Value != "System") links.Add("-l" + lib.Groups[1].Value);
                }
                else links.Add(path);
            }
            return links;
        }

        /// <summary>Registers for values: see the class's summary.</summary>
        internal static readonly RegisterAllocator.Target Registers = new(
            new[] { "x10", "x11", "x12", "x13", "x14", "x15" },
            new[] { "x19", "x20", "x21", "x22", "x23", "x24", "x25", "x26", "x27" },
            Enumerable.Range(16, 12).Select(i => $"v{i}").ToArray(),
            // (libm's sin, cos and fmod: a call, which may change what a call changes)
            i => i is X87I { Instruction: "fsin" or "fcos" } or X87I { B: not null });

        // ---------------------------------------------------------------- the program

        string Build()
        {
            if (unit.Kind != UnitKind.Program) return NotYet(program.File, default, "a kernel or a boot sector");
            if (program.Library != null) return NotYet(program.File, default, "a compiled library's build (phi build --library)");
            if (unit.CompiledLibraries.Count > 0) return NotYet(program.File, default, $"a compiled library ({unit.CompiledLibraries[0].Name}.philib)");
            if (unit.RawBlocks.FirstOrDefault(r => r.Language != "arm") is RawBlockDecl raw)
                return NotYet(raw.File, raw.Span, $"an x86 assembly block ({raw.Language}.{raw.Name}; ARM's is arm.{raw.Name})");
            if (unit.ForeignEntries.FirstOrDefault() is MethodSymbol foreign)
                return NotYet(foreign.Decl.File, foreign.Decl.Span, "addr foreign (a method other code calls)");

            // each class's own statements, a method of their own, called in order from _main
            var starts = new List<string>();
            foreach (ClassDecl cls in unit.Classes)
            {
                if (cls.Body.Count == 0) continue;
                file = cls.File;
                string label = $"phi_class_{starts.Count}";
                var decl = new MethodDecl { Name = cls.Name, Owner = cls, Span = cls.Span };
                decl.Body.AddRange(cls.Body);
                var m = new MethodSymbol { Name = $"{cls.Name} (its own statements)", Label = label, Decl = decl, Unit = unit.Kind };
                if (EmitMethod(m, $"class {cls.Name}'s own statements ({Path.GetFileName(cls.File.Path)})")) starts.Add(label);
            }
            foreach (MethodSymbol m in unit.Methods.Where(m => !m.IsOutside))
            {
                file = m.Decl.File;
                EmitMethod(m, $"{m.Name} ({Path.GetFileName(m.Decl.File.Path)})");
            }
            foreach (RawBlockDecl block in unit.RawBlocks) EmitRawBlock(block);
            if (failed) return "";
            // what follows isn't PHI code: a fault in it has no line
            code.AppendLine("phi_line_end:");
            lineMarks.Add(("phi_line_end", 0, "0"));
            EmitObjectRoutines();
            EmitHeapEntries(code);
            if (failed) return "";

            needed.Add("phi_exit");
            needed.Add("phi_fault_register");
            needed.Add("phi_main_thread");
            // (the program's own Heap, which the runtime calls by these names: EmitHeapEntries)
            needed.ExceptWith(new[] { "phi_heap_alloc", "phi_heap_free" });
            foreach (string symbol in needed)
                if (!Library.Provides(Target, symbol))
                    return NotYet(program.File, default, $"the library's {symbol}");
            List<LibraryFile> library = Library.Resolve(Target, needed);

            var data = new StringBuilder();
            EmitData(data);
            if (failed) return "";

            var output = new StringBuilder();
            output.AppendLine($"; macOS program (Apple Silicon) generated by phi from {Path.GetFileName(program.File.Path)}");
            output.AppendLine("\t.section __TEXT,__text,regular,pure_instructions");
            output.AppendLine("\t.p2align 2");
            output.AppendLine("phi_image_base:");
            output.AppendLine("\t.globl _main");
            output.AppendLine("_main:");
            output.AppendLine("\tstp x29, x30, [sp, #-32]!");
            output.AppendLine("\tmov x29, sp");
            output.AppendLine("\tstp x19, x20, [sp, #16]");
            output.AppendLine("\tmov x19, x0                 ; argc");
            output.AppendLine("\tmov x20, x1                 ; argv");
            output.AppendLine("\tadrp x28, phi_main_thread@PAGE");
            output.AppendLine("\tadd x28, x28, phi_main_thread@PAGEOFF   ; this thread's block");
            output.AppendLine("\tstr x28, [x28]");
            foreach (string init in library.SelectMany(f => f.Init))
            {
                output.AppendLine("\tmov x0, x19");
                output.AppendLine("\tmov x1, x20");
                output.AppendLine($"\tbl {init}");
            }
            foreach (string start in starts) output.AppendLine($"\tbl {start}");
            output.AppendLine("phi_halt:");
            output.AppendLine("\tmov w0, #0");
            output.AppendLine("\tb phi_exit");
            output.Append(code);
            foreach (LibraryFile lib in library)
            {
                output.AppendLine();
                output.AppendLine($"; ---- library: {lib.Name}");
                output.Append(lib.Text);
                if (!lib.Text.EndsWith('\n')) output.AppendLine();
            }
            output.AppendLine();
            output.Append(data);
            return output.ToString();
        }

        /// <summary>A program that catches errors (if call ... throws): what it means is the library's catch records.</summary>
        bool Catches => catches ??= unit.Classes.SelectMany(c => c.Body).Concat(unit.Methods.SelectMany(m => m.Decl.Body))
            .SelectMany(SyntaxWalker.Nodes).Any(n => n is ThrowsExpr);
        bool? catches;

        /// <summary>The heap's Alloc is the program's: a method's str result grows to fit (the generator's GrowResults).</summary>
        bool GrowResults => unit.Methods.Any(m => m.Name == "Heap.Alloc");

        /// <summary>One method, from the intermediate form; false (and an error) when it can't be.</summary>
        bool EmitMethod(MethodSymbol m, string what)
        {
            IrFunction? fn = IrBuilder.Build(m, _ => false, Catches, GrowResults, true, _ => false, out string why, arm: true);
            if (fn == null)
            {
                NotYet(m.Decl.File, m.Decl.Span, why, m);
                return false;
            }
            IrPeephole.Run(fn);
            if (Unsupported(fn) is string not)
            {
                NotYet(m.Decl.File, m.Decl.Span, not, m);
                return false;
            }
            RegisterAllocator regs = RegisterAllocator.Allocate(fn, target: Registers);
            code.AppendLine();
            code.AppendLine($"; ---- {what}");
            code.AppendLine("\t.p2align 2");
            code.AppendLine($"{Sym(m.Label)}:");
            new Emitter(this, fn, regs, m.Label).Emit();
            return true;
        }

        string NotYet(SourceFile? where, Span span, string why, MethodSymbol? m = null)
        {
            failed = true;
            string who = m == null ? "the program" : m.Decl.Name;
            diagnostics.Error(where ?? program.File, span, $"{who} uses {why}, which macOS programs can't do yet");
            return "";
        }

        // ---------------------------------------------------------------- what the emitter writes (so far)

        /// <summary>The first thing in the method the emitter doesn't write yet, or null.</summary>
        static string? Unsupported(IrFunction fn)
        {
            if (fn.Values.Any(v => v.Vec)) return "a vec";
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    string? not = i switch
                    {
                        LoadI { Kind: MemKind.V128 } or StoreI { Kind: MemKind.V128 } or LoadIdxI { Kind: MemKind.V128 } or StoreIdxI { Kind: MemKind.V128 } => "a vec",
                        CallI c when c.Result == CallResult.Xmm0 || c.ArgKinds.Contains(MemKind.V128) => "a vec given to a call or given back",
                        PrintI p when p.Kind is not (PrintKind.Text or PrintKind.Int or PrintKind.Uint or PrintKind.Char or PrintKind.Int64 or PrintKind.Uint64 or PrintKind.Float) => $"a log of a {p.Kind}",
                        X87I x when x.B == null && x.Instruction is not ("fsqrt" or "fabs" or "frndint" or "fsin" or "fcos") => $"the FPU's {x.Instruction}",
                        LineMark or ConstI or CopyI or UnI or ExtendI or SetCcI or AddrI or LeaI or ExitI or RoutineI or BinI or NarrowI
                            or LoadI or StoreI or LoadIdxI or StoreIdxI or CallI or PrintI or TextI or CheckI or SignCheckI or StopI or ThrowI
                            or HasTextI or FixedStoreI or ResultTextI or NumberTextI or ZeroI or CopyBytesI or CompareBytesI or AddrOfI
                            or FConstI or FBinI or FNegI or X87I or RoundF32I or ToFloatI or ToWholeI or LockI or UnlockI or AtomicI or RawCallI
                            or CatchI or UncatchI or CaughtI or ObjCallI or ArrayRoutineI or ObjectLineI or FreeTextI or ArrayGrowI
                            or GrowStoreI or InterfaceMethodI or UnwindI => null,
                        _ => KindName(i),
                    };
                    if (not != null) return not;
                }
                string? end = b.End switch
                {
                    JumpT or SwitchT or BranchT or TryT => null,
                    RetT r => r.Vec ? "a vec given back" : null,
                    _ => KindName(b.End),
                };
                if (end != null) return end;
            }
            return null;
        }

        static bool IntKind(MemKind k) => k is MemKind.U8 or MemKind.S8 or MemKind.U16 or MemKind.S16 or MemKind.D32 or MemKind.Q64;

        /// <summary>What an instruction the emitter doesn't write yet is, in words: its kind without the I.</summary>
        static string KindName(object? i)
        {
            string name = i?.GetType().Name ?? "nothing";
            if (name.EndsWith('I') || name.EndsWith('T')) name = name[..^1];
            return $"what the intermediate form calls {name}";
        }

        // ---------------------------------------------------------------- assembly blocks

        /// <summary>
        /// An arm. block, as it's written: called (bl) from a method, with x29 that method's
        /// frame (its arguments at x29 + 16 up, as x64. blocks have them at rbp + 16), it may change
        /// any register but x28 (the thread's block), x29, x30 (where it returns to) and sp: the
        /// method that calls one keeps x19-x27 for its caller, as one that calls an x64. block
        /// keeps r12-r15. It gives back what's in w0. {name} is a class
        /// variable's label (adrp x0, {name}@PAGE...) or a constant's value.
        /// </summary>
        void EmitRawBlock(RawBlockDecl raw)
        {
            file = raw.File;
            code.AppendLine();
            code.AppendLine($"; ---- arm.{raw.Name}");
            code.AppendLine("\t.p2align 2");
            code.AppendLine($"{Sym(raw.Name)}:");
            var text = new StringBuilder();
            int i = 0;
            while (i < raw.Text.Length)
            {
                int open = raw.Text.IndexOf('{', i);
                int close = open < 0 ? -1 : raw.Text.IndexOf('}', open);
                if (close < 0) { text.Append(raw.Text, i, raw.Text.Length - i); break; }
                text.Append(raw.Text, i, open - i);
                string name = raw.Text[(open + 1)..close].Trim();
                if (AsmName(name) is string replacement) text.Append(replacement);
                else
                {
                    failed = true;
                    diagnostics.Error(raw.File, new Span(raw.TextSpan.Start + open, close - open + 1),
                        $"arm.{raw.Name} uses {{{name}}}, but there is no class variable or constant named '{name}'");
                }
                i = close + 1;
            }
            foreach (string line in text.ToString().Replace("\r\n", "\n").Split('\n'))
                code.AppendLine(line.TrimEnd());
        }

        /// <summary>{name} in an arm. block: a class variable's label (a field's: label + offset), or a constant's value.</summary>
        string? AsmName(string name)
        {
            string[] parts = name.Split('.');
            if (!unit.ClassNames.TryGetValue(parts[0], out Symbol? symbol))
                return Builtins.Constants.TryGetValue(name, out long builtin) ? builtin.ToString() : null;
            if (symbol is ConstantSymbol k) return parts.Length == 1 ? k.Value.ToString() : null;
            if (symbol is not VariableSymbol v) return null;
            int offset = 0;
            PhiType type = v.Type;
            foreach (string member in parts.Skip(1))
            {
                FieldSymbol? f = type.IsStruct ? type.Struct!.Find(member) : null;
                if (f == null) return null;
                offset += f.Offset;
                type = f.Type;
            }
            return offset == 0 ? Sym(v.Label) : $"{Sym(v.Label)}+{offset}";
        }

        // ---------------------------------------------------------------- labels

        static readonly Regex NotLabel = new(@"[^A-Za-z0-9_$.]");

        /// <summary>A label as the assembler takes it (a template's, say, has characters NASM takes and it doesn't).</summary>
        internal static string Sym(string label) => NotLabel.Replace(label, "_");

        internal string NewLabel(string what) => $"Lphi_{what}_{labels++}";

        internal void Need(string symbol) => needed.Add(symbol);

        /// <summary>Text written in the program: its label, where its zero-terminated bytes are.</summary>
        internal string StringLabel(string text)
        {
            if (!strings.TryGetValue(text, out string? label))
            {
                label = $"phi_text_{strings.Count}";
                strings[text] = label;
            }
            return label;
        }

        internal void Line(string text) => code.AppendLine(text);

        /// <summary>Where each line's code starts: its label, its line, its file's name's label (phi_lines, for an error's line).</summary>
        readonly List<(string Label, int Line, string File)> lineMarks = new();

        internal void MarkLine(Node node)
        {
            string label = $"phi_line_{lineMarks.Count}";
            code.AppendLine($"{label}:");
            lineMarks.Add((label, file.GetLocation(node.Span.Start).Line, StringLabel(Path.GetFileName(file.Path))));
        }

        internal SourceFile File => file;

        // ---------------------------------------------------------------- data

        void EmitData(StringBuilder output)
        {
            output.AppendLine("; ---- data");
            EmitTypeTables(output);
            // each line's code: a count, then its address, its line and its file's name, 8 bytes each
            // (absolute addresses: in __DATA, which the loader moves with the program)
            output.AppendLine("\t.section __DATA,__const");
            output.AppendLine("\t.p2align 3");
            output.AppendLine("phi_lines:");
            output.AppendLine($"\t.quad {lineMarks.Count}");
            foreach (var (label, line, name) in lineMarks) output.AppendLine($"\t.quad {label}, {line}, {name}");
            // (the texts written in the program are writable, as x86's are: code may write through a ptr<u8> to one)
            output.AppendLine("\t.section __DATA,__data");
            foreach (var (text, label) in strings)
            {
                output.AppendLine($"{label}:");
                output.AppendLine($"\t.byte {Bytes(text)}");
            }
            output.AppendLine("\t.section __DATA,__data");
            foreach (VariableSymbol v in unit.Variables.Distinct())
            {
                if (v.Type.IsError || v.IsLocal || v.IsOutside) continue;
                output.AppendLine(v.Type.Alignment >= 16 ? "\t.p2align 4" : "\t.p2align 3");
                if (DataFor(v) is string d) output.Append(d);
                else NotYet(program.File, default, $"a class's variable of its kind ({v.Name}, {v.Type})");
            }
        }

        /// <summary>A class's variable in the program's data, laid out as x86's generator lays it out (its DataFor).</summary>
        string? DataFor(VariableSymbol v)
        {
            var sb = new StringBuilder();
            List<object>? values = v.StaticValues;
            PhiType t = v.Type;
            string label = Sym(v.Label);
            if (t.Kind == TypeKind.Vec) return null;

            if (v.GrowingArray)
            {
                // an array that grows: its elements (on the heap), how many, its room
                sb.AppendLine($"{label}:");
                sb.AppendLine("\t.quad 0");
                sb.AppendLine("\t.long 0, 0");
                return sb.ToString();
            }

            if (t.Kind is TypeKind.F16 or TypeKind.F32 or TypeKind.F64)
            {
                var bits = (values ?? new List<object>()).Select(x => x is double d ? d : (double)(long)x).Select(d => t.Kind switch
                {
                    TypeKind.F64 => $"0x{BitConverter.DoubleToInt64Bits(d):X}",
                    TypeKind.F32 => $"0x{BitConverter.SingleToInt32Bits((float)d):X}",
                    _ => $"0x{BitConverter.HalfToInt16Bits((Half)d):X}",
                }).ToList();
                string kind = t.Kind switch { TypeKind.F64 => ".quad", TypeKind.F32 => ".long", _ => ".short" };
                int size = t.Kind switch { TypeKind.F64 => 8, TypeKind.F32 => 4, _ => 2 };
                sb.AppendLine($"{label}:");
                if (bits.Count > 0) sb.AppendLine($"\t{kind} {string.Join(", ", bits)}");
                if (v.Count - bits.Count > 0) sb.AppendLine($"\t.space {size * (v.Count - bits.Count)}");
                if (t.Filled) sb.AppendLine("\t.long 0                     ; how many are in it");
                return sb.ToString();
            }

            if (v.Grows)
            {
                // a str that grows: its text, its room (its first text here, in the data, until it
                // needs more room than that: the room's top bit says so)
                if (values is [var first])
                {
                    string text = AsText(first);
                    Text(sb, $"{label}__text", text, text.Length + 1);
                    sb.AppendLine("\t.p2align 3");
                    sb.AppendLine($"{label}:");
                    sb.AppendLine($"\t.quad {label}__text");
                    sb.AppendLine($"\t.long {(text.Length + 1) | unchecked((int)0x80000000)}, 0");
                }
                else
                {
                    sb.AppendLine($"{label}:");
                    sb.AppendLine("\t.quad 0");
                    sb.AppendLine("\t.long 0, 0");
                }
                return sb.ToString();
            }

            if (t.IsString)
            {
                Text(sb, label, values is [var first] ? AsText(first) : "", v.Capacity);
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Str && t.Room > 0)
            {
                // texts of up to Room - 1 each, in place
                sb.AppendLine($"{label}:");
                sb.AppendLine($"\t.space {Math.Max(t.ByteSize, 1)}");
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Str)
            {
                // each element's text in a buffer of its own; then each one's address, then (when
                // they grow) each one's room, 8 bytes each
                var pointers = new List<string>();
                for (int i = 0; i < v.Count; i++)
                {
                    string element = $"{label}__{i}";
                    pointers.Add(element);
                    Text(sb, element, values != null && i < values.Count ? AsText(values[i]) : "", v.Capacity);
                }
                sb.AppendLine("\t.p2align 3");
                sb.AppendLine($"{label}:");
                if (pointers.Count > 0) sb.AppendLine($"\t.quad {string.Join(", ", pointers)}");
                if (v.GrowingElements)
                    foreach (string _ in pointers) sb.AppendLine($"\t.long {v.Capacity | unchecked((int)0x80000000)}, 0");
                if (t.Filled) sb.AppendLine("\t.long 0                     ; how many are in it");
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Struct)
            {
                sb.AppendLine($"{label}:");
                sb.AppendLine($"\t.space {Math.Max(t.ByteSize, 1)}");
                return sb.ToString();
            }

            string directive = t.ElementSize switch { 1 => ".byte", 2 => ".short", 8 => ".quad", _ => ".long" };
            long mask = t.ElementSize switch { 1 => 0xFF, 2 => 0xFFFF, 8 => -1, _ => 0xFFFFFFFF };
            sb.AppendLine($"{label}:");
            var numbers = (values ?? new List<object>()).Select(x => x is long n ? n & mask : 0).ToList();
            if (numbers.Count > 0) sb.AppendLine($"\t{directive} {string.Join(", ", numbers.Select(n => t.ElementSize == 8 ? $"0x{n:X}" : n.ToString()))}");
            if (v.Count - numbers.Count > 0) sb.AppendLine($"\t.space {t.ElementSize * (v.Count - numbers.Count)}");
            if (t.Filled) sb.AppendLine("\t.long 0                     ; how many are in it");
            return sb.ToString();
        }

        /// <summary>A text's bytes and zero, in capacity bytes (cut to fit), as x86's AppendString.</summary>
        static void Text(StringBuilder sb, string label, string text, int capacity)
        {
            if (text.Length + 1 > capacity) text = text[..Math.Max(capacity - 1, 0)];
            sb.AppendLine($"{label}:");
            sb.AppendLine($"\t.byte {Bytes(text)}");
            int padding = capacity - text.Length - 1;
            if (padding > 0) sb.AppendLine($"\t.space {padding}");
        }

        static string AsText(object value) => value switch
        {
            string s => s,
            long n => ((int)n).ToString(),
            _ => "",
        };

        /// <summary>A text's bytes, then its zero (as the x86 generator's: a character past 255 is '?').</summary>
        static string Bytes(string text) =>
            string.Join(", ", text.Select(c => c < 256 ? (int)c : '?').Append(0));
    }
}

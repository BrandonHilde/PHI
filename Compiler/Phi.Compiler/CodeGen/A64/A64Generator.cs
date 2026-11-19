using System.Text;
using System.Text.RegularExpressions;
using Phi.Compiler.CodeGen.Ir;
using Phi.Compiler.Libraries;
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

        /// <summary>--debug: each line's code says which line it is (.loc), for a debugger's DWARF.</summary>
        bool debug;

        /// <summary>Each method's name, and where its code starts and ends, for --debug's DWARF.</summary>
        readonly List<(string Name, string Start, string End)> debugMethods = new();

        /// <summary>Each source file's number in the DWARF line table (.file), with --debug.</summary>
        readonly Dictionary<string, int> debugFiles = new();
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

        public static AsmUnit Generate(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics, bool debug = false)
        {
            var generator = new A64Generator(program, unit, diagnostics) { debug = debug };
            string text = generator.Build();
            return new AsmUnit
            {
                Kind = unit.Kind, Name = program.Library != null ? "library" : "program", Text = text, Links = generator.Links(),
                Library = generator.libraryUnit,
                Objects = unit.CompiledLibraries.Select(c => (c.Name, c.Sections.Count > 0 ? c.Sections[0].Bytes : Array.Empty<byte>())).ToList(),
            };
        }

        /// <summary>
        /// The linker's arguments for the outside libraries the program calls: a framework
        /// ('AppKit.framework', or its path) is -framework, a library ('libobjc.A.dylib') -l
        /// (the linker looks where the system's are, and Builder adds where others are put),
        /// one named with its folder that file; libSystem is every program's already.
        /// </summary>
        List<string> Links()
        {
            var links = new List<string>();
            foreach (string path in unit.OutsideFunctions.Select(f => f.LibraryPath).Distinct())
            {
                string name = Path.GetFileName(path);
                var framework = Regex.Match(path, @"([^/]+)\.framework");
                if (framework.Success) links.AddRange(new[] { "-framework", framework.Groups[1].Value });
                else if (path.Contains('/') && !path.StartsWith("/usr/lib/", StringComparison.Ordinal)) links.Add(path);   // (a file somewhere said: that file)
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
            i => i is X87I { Instruction: "fsin" or "fcos" } or X87I { B: not null },
            // (each of these writes its result in its last instruction, every operand read by then)
            i => i is BinI or UnI or ExtendI or NarrowI or LeaI or CopyI { Dst: { Float: false, Vec: false } } or SetCcI { Float: false }
                 or LoadIdxI { Kind: not (MemKind.F64 or MemKind.F32 or MemKind.F16 or MemKind.V128) });

        // ---------------------------------------------------------------- the program

        string Build()
        {
            if (unit.Kind != UnitKind.Program) return NotYet(program.File, default, "a kernel or a boot sector");
            if (program.Library != null) return BuildLibrary();
            if (unit.RawBlocks.FirstOrDefault(r => r.Language != "arm" && r.File.Compiled == null) is RawBlockDecl raw)
                return NotYet(raw.File, raw.Span, $"an x86 assembly block ({raw.Language}.{raw.Name}; ARM's is arm.{raw.Name})");
            // each class's own statements, a method of their own, called in order from _main
            var starts = new List<string>();
            var startedLibraries = new HashSet<CompiledTarget>();
            foreach (ClassDecl cls in unit.Classes)
            {
                // a compiled library's classes run together, in the order they're in in it
                if (cls.File.Compiled is CompiledTarget compiled)
                {
                    if (startedLibraries.Add(compiled)) starts.Add(compiled.StartLabel);
                    continue;
                }
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
            foreach (MethodSymbol m in unit.ForeignEntries.Where(m => !m.IsOutside)) EmitForeignEntry(m);
            foreach (RawBlockDecl block in unit.RawBlocks) EmitRawBlock(block);
            if (failed) return "";
            // what follows isn't PHI code: a fault in it has no line
            code.AppendLine("phi_line_end:");
            lineMarks.Add(("phi_line_end", 0, "0"));
            // what the compiled libraries' code uses, which nothing here would ask for
            foreach (CompiledTarget compiled in unit.CompiledLibraries) needed.UnionWith(compiled.RuntimeSymbols);
            foreach (StructSymbol s in unit.LibraryTypeTables) TypeTable(s);
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
            if (debug) DebugUnit(output);
            if (unit.CompiledLibraries.Count == 0) return output.ToString();
            // the compiled libraries' objects are linked with this one: what they leave to the
            // program (its methods and variables, the runtime's, type tables) is its to give, so
            // every label here is global (theirs are, but for what they give, their own)
            string text = output.ToString();
            var globals = System.Text.RegularExpressions.Regex.Matches(text, @"^([A-Za-z_$][\w$.]*):", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value).Where(l => !l.StartsWith('L') && l != "_main").Distinct();
            return string.Join("\n", globals.Select(l => $"\t.globl {l}")) + "\n" + text;
        }

        /// <summary>A program that catches errors (if call ... throws): what it means is the library's catch records.</summary>
        bool Catches => catches ??= program.Library != null || unit.CompiledLibraries.Count > 0
            || unit.Classes.SelectMany(c => c.Body).Concat(unit.Methods.SelectMany(m => m.Decl.Body)).SelectMany(SyntaxWalker.Nodes).Any(n => n is ThrowsExpr);
        bool? catches;

        RememberedLengths? lengths;

        /// <summary>The heap's Alloc is the program's: a method's str result grows to fit (the generator's GrowResults).</summary>
        bool GrowResults => unit.Methods.Any(m => m.Name == "Heap.Alloc");

        /// <summary>
        /// An index made 64 bits only to be an element's index goes into the read or write itself
        /// ([base, w1, uxtw #2]), where the element's size is the scale (or the scale is 1).
        /// </summary>
        static void FoldIndexExtends(IrFunction fn)
        {
            var uses = new Dictionary<VReg, int>();
            var defs = new Dictionary<VReg, int>();
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    foreach (VReg v in RegisterAllocator.Uses(i)) uses[v] = uses.GetValueOrDefault(v) + 1;
                    if (RegisterAllocator.Def(i) is VReg d) defs[d] = defs.GetValueOrDefault(d) + 1;
                }
                foreach (VReg v in RegisterAllocator.Uses(b.End)) uses[v] = uses.GetValueOrDefault(v) + 1;
            }
            static int Size(MemKind k) => k switch
            {
                MemKind.U8 or MemKind.S8 => 1, MemKind.U16 or MemKind.S16 or MemKind.F16 => 2,
                MemKind.D32 or MemKind.F32 => 4, MemKind.Q64 or MemKind.F64 => 8, _ => 16,
            };
            foreach (Block b in fn.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    if (b.Instrs[k] is not ExtendI e || e.Dst.Pinned || uses.GetValueOrDefault(e.Dst) != 1 || defs.GetValueOrDefault(e.Dst) != 1) continue;
                    for (int j = k + 1; j < b.Instrs.Count; j++)
                    {
                        Instr at = b.Instrs[j];
                        bool takes = at switch
                        {
                            LoadIdxI l => l.Index.Reg == e.Dst && l.Base != e.Dst && l.IndexSigned == null && (l.Scale == 1 || l.Scale == Size(l.Kind)),
                            StoreIdxI st => st.Index.Reg == e.Dst && st.Base != e.Dst && st.Src.Reg != e.Dst && st.IndexSigned == null
                                            && (st.Scale == 1 || st.Scale == Size(st.Kind)),
                            _ => false,
                        };
                        if (takes)
                        {
                            if (at is LoadIdxI load) { load.Index = e.Src; load.IndexSigned = e.Signed; }
                            else if (at is StoreIdxI store) { store.Index = e.Src; store.IndexSigned = e.Signed; }
                            b.Instrs.RemoveAt(k--);
                            break;
                        }
                        // (its one use is somewhere else, or what it's made from changes first)
                        if (RegisterAllocator.Uses(at).Contains(e.Dst) || RegisterAllocator.Def(at) == e.Src) break;
                    }
                }
        }

        /// <summary>
        /// A value made by one instruction and read only by the next (or by the return after it)
        /// is made in x0: a call's result is there already and a method's result goes there, so
        /// (F: n - 1) + (F: n - 2) given back is one add, with no copies round it. Only where
        /// neither instruction uses x0 for anything else.
        /// </summary>
        static void ThroughX0(IrFunction fn, RegisterAllocator regs)
        {
            var uses = new Dictionary<VReg, int>();
            var defs = new Dictionary<VReg, int>();
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    foreach (VReg v in RegisterAllocator.Uses(i)) uses[v] = uses.GetValueOrDefault(v) + 1;
                    if (RegisterAllocator.Def(i) is VReg d) defs[d] = defs.GetValueOrDefault(d) + 1;
                }
                foreach (VReg v in RegisterAllocator.Uses(b.End)) uses[v] = uses.GetValueOrDefault(v) + 1;
            }
            static bool Whole(MemKind k) => k is not (MemKind.F64 or MemKind.F32 or MemKind.F16 or MemKind.V128);
            static bool Simple(Instr i) => i is BinI or UnI or ExtendI or NarrowI or LeaI or CopyI { Dst: { Float: false, Vec: false } }
                or SetCcI { Float: false } || i is LoadIdxI l && Whole(l.Kind) || i is LoadI ld && Whole(ld.Kind);
            foreach (Block b in fn.Blocks)
            {
                var code = b.Instrs.Where(i => i is not LineMark).ToList();
                for (int k = 0; k < code.Count; k++)
                {
                    Instr made = code[k];
                    if (RegisterAllocator.Def(made) is not VReg d || d.Float || d.Vec || d.Pinned) continue;
                    if (uses.GetValueOrDefault(d) != 1 || defs.GetValueOrDefault(d) != 1) continue;
                    if (!(Simple(made) || made is CallI { Outside: false, Result: not (CallResult.St0 or CallResult.Xmm0) })) continue;
                    bool read = k + 1 < code.Count
                        ? RegisterAllocator.Uses(code[k + 1]).Contains(d)
                          && (Simple(code[k + 1]) || code[k + 1] is StoreI st && Whole(st.Kind) || code[k + 1] is StoreIdxI sx && Whole(sx.Kind))
                        : b.End is RetT { Float: false, Vec: false, Value: Opnd given } && given.Reg == d;
                    if (!read) continue;
                    regs.Registers[d] = "x0";
                    regs.Slots.Remove(d);
                }
            }
        }

        HashSet<VariableSymbol>? reachedByAddress;

        /// <summary>
        /// A class's variable a loop that changes it may keep in a register (as x86's loops do,
        /// by the same rules: X86Generator's "a method's loops"): reading one where it is takes
        /// three instructions here, the address in two of them. Only in a method: a class's own
        /// statements' loops aren't fenced as x86's are (a method may name what they'd hold).
        /// </summary>
        bool Holdable(VariableSymbol v) =>
            !v.IsLocal && !v.IsParameter && !v.IsOutside && v.Unit == unit.Kind && !v.Decl.Volatile
            && !(reachedByAddress ??= X86Generator.ReachedByAddress(unit)).Contains(v);

        /// <summary>One method, from the intermediate form; false (and an error) when it can't be.</summary>
        bool EmitMethod(MethodSymbol m, string what, bool hold = true)
        {
            // (a method's own str that grows keeps its length beside it, as x86's does: an append starts at its end)
            IrFunction? fn = IrBuilder.Build(m, v => hold && Holdable(v), Catches, GrowResults, true, (lengths ??= new RememberedLengths(unit)).Remembers, out string why, arm: true);
            if (fn == null)
            {
                NotYet(m.Decl.File, m.Decl.Span, why, m);
                return false;
            }
            Inline(fn, m);
            IrPeephole.Run(fn, foldOffsets: true);
            // (after the peephole, which makes a test of x & mask one instruction)
            Selects(fn);
            FoldIndexExtends(fn);
            foreach (StructSymbol tested in fn.TypeTables) TypeTable(tested);
            if (Unsupported(fn) is string not)
            {
                NotYet(m.Decl.File, m.Decl.Span, not, m);
                return false;
            }
            RegisterAllocator regs = RegisterAllocator.Allocate(fn, target: Registers);
            if (regs.UsedCalleeSaved.Count > 0 && EarlyReturn(fn) != null)
            {
                // (kept registers to save, and a way that returns at once: that way on values of its own)
                SplitEntry(fn);
                regs = RegisterAllocator.Allocate(fn, target: Registers);
            }
            ThroughX0(fn, regs);
            code.AppendLine();
            code.AppendLine($"; ---- {what}");
            code.AppendLine("\t.p2align 2");
            code.AppendLine($"{Sym(m.Label)}:");
            new Emitter(this, fn, regs, m.Label).Emit();
            if (debug)
            {
                string end = NewLabel("end");
                code.AppendLine($"{end}:");
                debugMethods.Add((m.Name, Sym(m.Label), end));
            }
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
            foreach (Block b in fn.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    string? not = i switch
                    {
                        PrintI p when p.Kind is not (PrintKind.Text or PrintKind.Int or PrintKind.Uint or PrintKind.Char or PrintKind.Int64 or PrintKind.Uint64 or PrintKind.Float) => $"a log of a {p.Kind}",
                        X87I x when x.B == null && x.Instruction is not ("fsqrt" or "fabs" or "frndint" or "fsin" or "fcos") => $"the FPU's {x.Instruction}",
                        LineMark or ConstI or CopyI or UnI or ExtendI or SetCcI or SelectI or AddrI or LeaI or ExitI or RoutineI or BinI or NarrowI
                            or LoadI or StoreI or LoadIdxI or StoreIdxI or CallI or PrintI or TextI or CheckI or SignCheckI or StopI or ThrowI
                            or HasTextI or FixedStoreI or ResultTextI or NumberTextI or ZeroI or CopyBytesI or CompareBytesI or AddrOfI
                            or FConstI or FBinI or FNegI or X87I or RoundF32I or ToFloatI or ToWholeI or LockI or UnlockI or AtomicI or RawCallI
                            or CatchI or UncatchI or CaughtI or ObjCallI or ArrayRoutineI or ObjectLineI or FreeTextI or ArrayGrowI
                            or GrowStoreI or InterfaceMethodI or UnwindI or VecI or VecMakeI or VecGetI or VecSetI or VecReduceI => null,
                        _ => KindName(i),
                    };
                    if (not != null) return not;
                }
                string? end = b.End switch
                {
                    JumpT or SwitchT or BranchT or TryT => null,
                    RetT => null,
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

        /// <summary>
        /// --debug: a DWARF compile unit for the program's code, from phi_image_base to
        /// phi_line_end, whose lines are the table the .loc lines make (the only one in
        /// __debug_line, so at 0). The assembler makes the table; a debugger finds it through a
        /// unit, which only this says (the assembler's own, with -g, is for assembly's lines).
        /// </summary>
        void DebugUnit(StringBuilder output)
        {
            string name = Path.GetFullPath(program.File.Path).Replace('\\', '/');
            output.AppendLine("; ---- debug information (--debug)");
            output.AppendLine("\t.section __DWARF,__debug_abbrev,regular,debug");
            output.AppendLine("Lphi_debug_abbrev:");
            output.AppendLine("\t.byte 1, 0x11, 1            ; 1: a compile unit, with children");
            output.AppendLine("\t.byte 0x25, 0x08            ; producer, a string");
            output.AppendLine("\t.byte 0x13, 0x05            ; language, 2 bytes");
            output.AppendLine("\t.byte 0x03, 0x08            ; name, a string");
            output.AppendLine("\t.byte 0x10, 0x17            ; its lines, an offset in __debug_line");
            output.AppendLine("\t.byte 0x1B, 0x08            ; the folder, a string");
            output.AppendLine("\t.byte 0x11, 0x01            ; where its code starts, an address");
            output.AppendLine("\t.byte 0x12, 0x01            ; where it ends, an address");
            output.AppendLine("\t.byte 0, 0");
            output.AppendLine("\t.byte 2, 0x2E, 0            ; 2: a method (a subprogram), without children");
            output.AppendLine("\t.byte 0x03, 0x08            ; name, a string");
            output.AppendLine("\t.byte 0x11, 0x01            ; where its code starts");
            output.AppendLine("\t.byte 0x12, 0x01            ; where it ends");
            output.AppendLine("\t.byte 0, 0");
            output.AppendLine("\t.byte 0");
            output.AppendLine("\t.section __DWARF,__debug_info,regular,debug");
            output.AppendLine("Lphi_debug_info:");
            output.AppendLine("\t.long Lphi_debug_info_end - Lphi_debug_info_start");
            output.AppendLine("Lphi_debug_info_start:");
            output.AppendLine("\t.short 4                    ; DWARF 4");
            output.AppendLine("\t.long 0                     ; its abbreviations: the first");
            output.AppendLine("\t.byte 8                     ; addresses are 8 bytes");
            output.AppendLine("\t.byte 1");
            output.AppendLine("\t.asciz \"phi\"");
            output.AppendLine("\t.short 0x0C                 ; (C's, the nearest a debugger knows)");
            output.AppendLine($"\t.asciz \"{name.Replace("\"", "\\\"")}\"");
            output.AppendLine("\t.long 0");
            output.AppendLine($"\t.asciz \"{Path.GetDirectoryName(name)!.Replace('\\', '/').Replace("\"", "\\\"")}\"");
            output.AppendLine("\t.quad phi_image_base");
            output.AppendLine("\t.quad phi_line_end");
            foreach (var (method, start, end) in debugMethods)
            {
                output.AppendLine("\t.byte 2");
                output.AppendLine($"\t.asciz \"{method.Replace("\"", "\\\"")}\"");
                output.AppendLine($"\t.quad {start}");
                output.AppendLine($"\t.quad {end}");
            }
            output.AppendLine("\t.byte 0                     ; (the unit's children end)");
            output.AppendLine("Lphi_debug_info_end:");
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
        /// <summary>
        /// Where outside code calls a method (addr foreign): its values come as C gives them
        /// (the first eight whole ones in x0 to x7, floats in d0 to d7, the rest on the stack,
        /// each at a multiple of its own size as Apple packs them) and go where a PHI method
        /// reads them, 8 bytes each with the first highest. What it gives is in x0 or d0 either
        /// way (an f32, which PHI keeps as an f64, made one). The registers C expects kept are
        /// the ones PHI's methods keep. x28 (the thread's block) is found for the thread it's
        /// called on, which may be the library's own (foreign.s), and put back after.
        /// </summary>
        void EmitForeignEntry(MethodSymbol m)
        {
            int n = m.Parameters.Count;
            int room = (n * 8 + 15) / 16 * 16;
            code.AppendLine();
            code.AppendLine($"; where outside code calls {m.Name}");
            code.AppendLine("\t.p2align 2");
            if (unit.ForeignSystem.Contains(m)) code.AppendLine($"{Sym(Binder.ForeignEntry(m))}:");
            if (unit.ForeignC.Contains(m)) code.AppendLine($"{Sym(Binder.ForeignEntry(m, c: true))}:");
            void E(string line) => code.AppendLine("\t" + line);
            // (x28 is C's to have kept: the thread's block goes in it for the call, and it's put back)
            E("stp x29, x30, [sp, #-32]!");
            E("mov x29, sp");
            E("str x28, [sp, #16]");
            if (room > 0) E($"sub sp, sp, #{room}");
            int whole = 0, floats = 0, stack = 0;
            for (int i = 0; i < n; i++)
            {
                MemKind kind = IrBuilder.KindOf(m.Parameters[i].Type);
                int to = 8 * (n - 1 - i);
                bool isFloat = kind is MemKind.F64 or MemKind.F32;
                int size = kind switch { MemKind.U8 or MemKind.S8 => 1, MemKind.U16 or MemKind.S16 => 2, MemKind.D32 or MemKind.F32 => 4, _ => 8 };
                if (isFloat && floats < 8)
                {
                    E($"str {(kind == MemKind.F32 ? "s" : "d")}{floats++}, [sp, #{to}]");
                    continue;
                }
                if (!isFloat && whole < 8)
                {
                    E($"str x{whole++}, [sp, #{to}]");
                    continue;
                }
                stack = (stack + size - 1) / size * size;
                int from = 32 + stack;
                stack += size;
                if (isFloat)
                {
                    string r = kind == MemKind.F32 ? "s31" : "d31";
                    E($"ldr {r}, [x29, #{from}]");
                    E($"str {r}, [sp, #{to}]");
                    continue;
                }
                E(kind switch
                {
                    MemKind.U8 => $"ldrb w9, [x29, #{from}]", MemKind.S8 => $"ldrsb x9, [x29, #{from}]",
                    MemKind.U16 => $"ldrh w9, [x29, #{from}]", MemKind.S16 => $"ldrsh x9, [x29, #{from}]",
                    MemKind.D32 => $"ldr w9, [x29, #{from}]", _ => $"ldr x9, [x29, #{from}]",
                });
                E($"str x9, [sp, #{to}]");
            }
            // the thread's block: its own if it's one of the program's, and one made for it the
            // first time if it's the library's (foreign.s)
            Need("phi_foreign_block");
            E("bl phi_foreign_block");
            E($"bl {Sym(m.Label)}");
            if (m.ReturnType.Kind == TypeKind.F32) E("fcvt s0, d0");
            E("mov sp, x29");
            E("ldr x28, [sp, #16]");
            E("ldp x29, x30, [sp], #32");
            E("ret");
        }

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
            string label = program.Library != null ? $"{libraryPrefix}_line_{lineMarks.Count}" : $"phi_line_{lineMarks.Count}";
            code.AppendLine($"{label}:");
            int line = file.GetLocation(node.Span.Start).Line;
            lineMarks.Add((label, line, StringLabel(Path.GetFileName(file.Path))));
            if (debug)
            {
                string path = Path.GetFullPath(file.Path);
                if (!debugFiles.TryGetValue(path, out int number))
                {
                    number = debugFiles.Count + 1;
                    debugFiles[path] = number;
                    code.AppendLine($"\t.file {number} \"{path.Replace("\\", "/").Replace("\"", "\\\"")}\"");
                }
                code.AppendLine($"\t.loc {number} {line} 0");
            }
        }

        internal SourceFile File => file;

        // ---------------------------------------------------------------- data

        void EmitData(StringBuilder output)
        {
            output.AppendLine("; ---- data");
            EmitTypeTables(output);
            EmitVecConstants(output);
            // each line's code: a count, then its address, its line and its file's name, 8 bytes each
            // (absolute addresses: in __DATA, which the loader moves with the program)
            output.AppendLine("\t.section __DATA,__const");
            output.AppendLine("\t.p2align 3");
            output.AppendLine("phi_lines:");
            // (then each compiled library's, which the linker puts after the program's code, in order)
            var marks = lineMarks.ToList();
            foreach (CompiledTarget compiled in unit.CompiledLibraries.Where(c => c.Lines.Count > 0))
            {
                foreach (LibraryLine l in compiled.Lines) marks.Add(($"{compiled.Prefix}_line_{l.Offset}", l.Line, StringLabel(l.File)));
                marks.Add(($"{compiled.Prefix}_line_end", 0, "0"));
            }
            output.AppendLine($"\t.quad {marks.Count}");
            foreach (var (label, line, name) in marks) output.AppendLine($"\t.quad {label}, {line}, {name}");
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
            if (t.Kind == TypeKind.Vec)
            {
                // a vec starts as zeros (a class's is given its lanes in a method)
                sb.AppendLine($"{label}:");
                sb.AppendLine($"\t.space {t.ByteSize}");
                return sb.ToString();
            }

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

using System.Text;
using Phi.Compiler.Semantics;
using Phi.Compiler.Syntax;

namespace Phi.Compiler.CodeGen
{
    public sealed class AsmUnit
    {
        public UnitKind Kind { get; init; }
        public string Name { get; init; } = "";
        public string Text { get; init; } = "";
    }

    /// <summary>
    /// Generates NASM source: 16-bit real mode for the Boot and Os units, 32-bit protected
    /// mode for the Kernel unit. Values are computed in eax. Class variables live at fixed
    /// labels; a method's parameters and variables live in its stack frame (ebp), so methods
    /// can call themselves and be used from interrupt handlers. Library routines may change any
    /// general-purpose register except ebp, so nothing is kept in registers between statements,
    /// which also means every memory access really happens (as device memory needs).
    /// </summary>
    public sealed class X86Generator
    {
        /// <summary>Where the boot sector loads the 16-bit OS classes, right after itself.</summary>
        public const int OsAddress = 0x7E00;

        /// <summary>Where the loader puts a 32-bit kernel (see docs/memory-map.md).</summary>
        public const int KernelAddress = 0x10000;

        /// <summary>Where a user program runs, in its own address space.</summary>
        public const uint ProgramAddress = 0x40000000;

        readonly BoundProgram program;
        readonly BoundUnit unit;
        readonly DiagnosticBag diagnostics;
        readonly bool bits32;
        readonly string libraryTarget;

        StringBuilder code = new();
        readonly HashSet<string> librarySymbols = new();
        readonly Dictionary<string, string> stringLabels = new();
        int labelCount;
        SourceFile file;
        (SourceFile?, int) lastComment;

        X86Generator(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics)
        {
            this.program = program;
            this.unit = unit;
            this.diagnostics = diagnostics;
            bits32 = unit.Kind is UnitKind.Kernel or UnitKind.Program;
            libraryTarget = Binder.TargetOf(unit.Kind);
            file = program.File;
        }

        public static AsmUnit Generate(BoundProgram program, BoundUnit unit, DiagnosticBag diagnostics)
        {
            var gen = new X86Generator(program, unit, diagnostics);
            return new AsmUnit
            {
                Kind = unit.Kind,
                Name = unit.Kind switch { UnitKind.Boot => "boot", UnitKind.Os => "os", UnitKind.Program => "program", _ => "kernel" },
                Text = gen.Build(),
            };
        }

        // ================================================================ layout

        string Build()
        {
            // every 32-bit kernel gets an interrupt table, so exceptions show the panic screen
            if (unit.Kind == UnitKind.Kernel) Need("phi_interrupts_init");

            // the program's own code first: it decides which library routines are needed
            foreach (ClassDecl cls in unit.Classes)
            {
                file = cls.File;
                Comment($"---- class {cls.Name}");
                foreach (Stmt s in cls.Body) EmitStatement(s);
            }

            Line("");
            Label("phi_halt");
            switch (unit.Kind)
            {
                case UnitKind.Program:
                    // the end of a program: exit with 0
                    Need("phi_exit");
                    Emit("xor ebx, ebx");
                    Emit("jmp phi_exit");
                    break;
                case UnitKind.Kernel:
                    // wait for interrupts, and let any programs run (int 48 = OS_Yield; a no-op until
                    // multitasking starts)
                    Emit("hlt");
                    Emit("int 48");
                    Emit("jmp phi_halt");
                    break;
                default:
                    Emit("hlt");
                    Emit("jmp phi_halt");
                    break;
            }

            foreach (MethodSymbol m in unit.Methods) EmitMethod(m);
            foreach (RawBlockDecl raw in unit.RawBlocks) EmitRawBlock(raw);
            EmitObjectRoutines();
            EmitStrRoomRoutine();

            List<LibraryFile> library = Library.Resolve(libraryTarget, librarySymbols.OrderBy(s => s));

            // events used by the library that the program doesn't handle
            var defined = unit.Methods.Select(m => m.Label).ToHashSet();
            foreach (string hook in library.SelectMany(f => f.Hooks).Distinct().Where(h => !defined.Contains(h)))
            {
                Line("");
                Label(hook);
                Emit("ret");
            }

            var output = new StringBuilder();
            string what = unit.Kind switch { UnitKind.Boot => "Boot sector", UnitKind.Os => "OS classes", UnitKind.Program => "User program", _ => "32-bit kernel" };
            output.AppendLine($"; {what} generated by phi from {Path.GetFileName(program.File.Path)}");

            switch (unit.Kind)
            {
                case UnitKind.Boot:
                    output.AppendLine("bits 16");
                    output.AppendLine("org 0x7C00");
                    output.AppendLine();
                    output.AppendLine("%ifndef PHI_KERNEL_SECTORS");
                    output.AppendLine("%define PHI_KERNEL_SECTORS 0");
                    output.AppendLine("%endif");
                    output.AppendLine();
                    output.AppendLine("    jmp 0x0000:phi_start    ; some BIOSes start us at 07C0:0000");
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cli");
                    output.AppendLine("    xor ax, ax");
                    output.AppendLine("    mov ds, ax");
                    output.AppendLine("    mov es, ax");
                    output.AppendLine("    mov ss, ax");
                    output.AppendLine("    mov sp, 0x7C00          ; the stack grows down from just below us");
                    output.AppendLine("    sti");
                    output.AppendLine("    cld");
                    output.AppendLine("    mov [phi_boot_drive], dl");
                    break;

                case UnitKind.Os:
                    output.AppendLine("bits 16");
                    output.AppendLine($"org 0x{OsAddress:X}");
                    output.AppendLine();
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;

                case UnitKind.Program:
                    // the kernel starts us in ring 3 with our own address space and stack
                    output.AppendLine("bits 32");
                    output.AppendLine($"org 0x{ProgramAddress:X}");
                    output.AppendLine();
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;

                default:
                    // the loader has switched to protected mode with flat segments, set up a
                    // stack, and left the BootInfo address in ebx (see docs/memory-map.md)
                    output.AppendLine("bits 32");
                    output.AppendLine($"org 0x{KernelAddress:X}");
                    output.AppendLine();
                    output.AppendLine("phi_start:");
                    output.AppendLine("    cld");
                    break;
            }

            foreach (string init in library.SelectMany(f => f.Init))
                output.AppendLine($"    call {init}");

            output.Append(code);

            foreach (LibraryFile lib in library)
            {
                output.AppendLine();
                output.AppendLine($"; ---- library: {lib.Name}");
                output.Append(lib.Text);
            }

            output.AppendLine();
            // variables start on a page of their own: when code and the variables it writes share
            // a 4 KB page, QEMU throws away its translation of that code on every write, which made
            // hot loops run up to ~200 times slower
            if (bits32) output.AppendLine("align 4096, db 0");
            output.AppendLine("; ---- data");
            EmitData(output);

            if (unit.Kind == UnitKind.Boot)
            {
                output.AppendLine("phi_boot_drive: db 0");
                output.AppendLine();
                output.AppendLine("times 510 - ($ - $$) db 0   ; a negative size here means the boot sector is too big");
                output.AppendLine("dw 0xAA55");
            }

            return output.ToString();
        }

        // ================================================================ emit helpers

        void Emit(string instruction) => code.Append("    ").AppendLine(instruction);
        void Label(string label) => code.Append(label).AppendLine(":");
        void Line(string text) => code.AppendLine(text);
        string NewLabel(string hint) => $"phi_{hint}_{labelCount++}";

        void Comment(string text) => code.Append("    ; ").AppendLine(text);

        int LineOf(Node node) => file.GetLocation(node.Span.Start).Line;

        /// <summary>Puts the source line above the code it produced, which helps when reading the .asm.</summary>
        void SourceComment(Node node)
        {
            int line = LineOf(node);
            if (lastComment == (file, line)) return;
            lastComment = (file, line);

            string[] lines = file.Text.Split('\n');
            string text = line - 1 < lines.Length ? lines[line - 1].Trim() : "";
            code.AppendLine($"    ; {line}: {text}");
        }

        void Need(string librarySymbol) => librarySymbols.Add(librarySymbol);

        string StringLabel(string value)
        {
            if (!stringLabels.TryGetValue(value, out string? label))
            {
                label = $"phi_string_{stringLabels.Count}";
                stringLabels[value] = label;
            }
            return label;
        }

        // ================================================================ methods and asm blocks

        void EmitMethod(MethodSymbol m)
        {
            file = m.Decl.Owner.File;
            Line("");
            Comment(m.IsHook ? $"event {m.Name}" : m.IsInterruptHandler ? $"interrupt handler {m.Name}" : $"method {m.Name}");
            Label(m.Label);

            if (m.IsInterruptHandler)
            {
                // the interrupted code must find every register as it left it
                Emit("pushad");
                if (!bits32)
                {
                    Emit("push ds");
                    Emit("push es");
                    Emit("push fs");
                    Emit("xor ax, ax");
                    Emit("mov ds, ax");
                    Emit("mov es, ax");
                }
                Emit("cld");
            }

            Frame frame = LayOutFrame(m);
            currentMethod = m;
            frameSize = frame.LocalsSize;
            returnLabel = NewLabel("return");
            returnUsed = false;

            // the body first: the temporary space it needs decides the frame's size
            StringBuilder outer = code;
            code = new StringBuilder();

            foreach (Stmt s in m.Decl.Body) EmitStatement(s);

            if (m.Decl.Result != null)
            {
                SourceComment(m.Decl.Result);
                if (m.ReturnType.IsObject) EmitObjectResult(m.Decl.Result);
                else EmitExpr(m.Decl.Result); // the result goes back in eax
            }

            if (returnUsed) Label(returnLabel); // return jumps here with the result in eax

            if (m.ReturnType.IsString)
            {
                // copy the result into the caller's buffer before this frame goes away
                Need("phi_strcpy");
                Emit("mov esi, eax");
                Emit($"mov edi, [ebp + {frame.ResultSlot}]");
                Emit($"mov ecx, {m.ResultCapacity}");
                Emit("call phi_strcpy");
                Emit($"mov eax, [ebp + {frame.ResultSlot}]");
            }

            // its strs that grow give their text back
            var growing = m.Parameters.Concat(m.Locals).Where(v => v.Grows).ToList();
            if (growing.Count > 0)
            {
                Emit("push eax                ; the result");
                foreach (VariableSymbol v in growing)
                {
                    Emit($"push dword [{v.Label}]   ; {v.Name}'s text");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit("add esp, 4");
                }
                Emit("pop eax");
            }

            // the method's new objects go away with it (one it returns was handed over already)
            var owned = m.Locals.Where(v => v.Ownership == Ownership.Owned).ToList();
            if (owned.Count > 0)
            {
                Emit("push eax                ; the result");
                foreach (VariableSymbol v in owned)
                {
                    Emit($"mov eax, dword [{v.Label}]   ; {v.Name}");
                    Emit($"call {ObjectRoutine("free", v.Type.Struct!)}");
                }
                Emit("pop eax");
            }

            StringBuilder body = code;
            code = outer;
            bool hasFrame = frameSize > 0 || m.Parameters.Count > 0 || m.ReturnType.IsString;
            if (hasFrame) EmitPrologue(frame);
            code.Append(body);
            if (hasFrame)
            {
                Emit("mov esp, ebp");
                Emit("pop ebp");
            }

            if (frameSize > MaxFrame)
                diagnostics.Error(file, m.Decl.Span, $"{m.Decl.Name} needs {frameSize} bytes of stack for its variables (at most {MaxFrame}); declare large buffers in the class instead");
            currentMethod = null;

            if (m.IsInterruptHandler)
            {
                if (!bits32)
                {
                    Emit("pop fs");
                    Emit("pop es");
                    Emit("pop ds");
                }
                Emit("popad");
                Emit(bits32 ? "iretd" : "iret");
            }
            else
            {
                Emit("ret");
            }
        }

        /// <summary>
        /// The most stack one method may use for its variables. A program's kernel stack is
        /// 8 KB, and calls nest.
        /// </summary>
        const int MaxFrame = 4096;

        /// <summary>The method being generated (null for class code), and its frame's size so far.</summary>
        MethodSymbol? currentMethod;
        int frameSize;

        /// <summary>Space for class code's temporary values, which has no frame.</summary>
        readonly List<(string Label, int Size)> staticTemps = new();

        sealed class Frame
        {
            public int LocalsSize;
            public int ResultSlot;
            public readonly List<(VariableSymbol Param, int Slot)> StrParams = new();
            public readonly List<(VariableSymbol Array, List<string> Elements)> StrArrays = new();
        }

        /// <summary>
        /// Gives every parameter and variable of the method its place, relative to ebp:
        ///
        ///   ebp + ...   the arguments, the last one nearest (each 4 bytes, pushed by the caller)
        ///   ebp + 8/6   where to copy a str result, for methods that return one
        ///   ebp + 4/2   the return address (4 bytes in 32-bit code, 2 in 16-bit)
        ///   ebp         the caller's ebp
        ///   ebp - ...   the variables, then temporary space for the calls' results
        ///
        /// A str parameter's argument is the address of the text, which the method copies into
        /// a buffer of its own when it starts.
        /// </summary>
        Frame LayOutFrame(MethodSymbol m)
        {
            var frame = new Frame();
            int slot = bits32 ? 8 : 6;
            if (m.ReturnType.IsString)
            {
                frame.ResultSlot = slot;
                slot += 4;
            }

            int size = 0;
            string Reserve(int bytes)
            {
                size += (bytes + 3) & ~3;
                return $"ebp - {size}";
            }

            for (int i = m.Parameters.Count - 1; i >= 0; i--, slot += 4)
            {
                VariableSymbol p = m.Parameters[i];
                if (p.Type.IsString)
                {
                    p.Label = Reserve(p.Grows ? 8 : p.Capacity);
                    frame.StrParams.Add((p, slot));
                }
                else p.Label = $"ebp + {slot}";
            }

            foreach (VariableSymbol v in m.Locals)
            {
                if (v.Type.Kind == TypeKind.Str && v.Type.IsArray)
                {
                    // a table of pointers, one per element, each to a buffer of its own
                    v.Label = Reserve(4 * v.Count);
                    var elements = new List<string>();
                    for (int i = 0; i < v.Count; i++) elements.Add(Reserve(v.Capacity));
                    frame.StrArrays.Add((v, elements));
                }
                else v.Label = Reserve(StorageSize(v));
            }

            frame.LocalsSize = size;
            return frame;
        }

        void EmitPrologue(Frame frame)
        {
            Emit("push ebp");
            // in 16-bit code only sp is the stack pointer; the top of esp may hold anything
            Emit(bits32 ? "mov ebp, esp" : "movzx ebp, sp");
            if (frameSize > 0) Emit($"sub esp, {frameSize}");

            // variables start at zero each call, as they always have
            int locals = frame.LocalsSize;
            if (locals is > 0 and <= 16)
            {
                for (int k = 4; k <= locals; k += 4) Emit($"mov dword [ebp - {k}], 0");
            }
            else if (locals > 16)
            {
                Emit($"lea edi, [ebp - {locals}]");
                Emit($"mov ecx, {locals / 4}");
                Emit("xor eax, eax");
                Emit("rep stosd");
            }

            foreach (var (array, elements) in frame.StrArrays)
                for (int i = 0; i < elements.Count; i++)
                {
                    Emit($"lea eax, [{elements[i]}]");
                    Emit($"mov [{array.Label} + {4 * i}], eax");
                }

            foreach (var (p, slot) in frame.StrParams)
            {
                if (p.Grows)
                {
                    Emit($"mov eax, [ebp + {slot}]   ; {p.Name}");
                    EmitGrowStore(p, sourceIsString: true, append: false, unsigned: false);
                    continue;
                }
                Need("phi_strcpy");
                Emit($"mov esi, [ebp + {slot}]   ; {p.Name}");
                Emit($"lea edi, [{p.Label}]");
                Emit($"mov ecx, {p.Capacity}");
                Emit("call phi_strcpy");
            }
        }

        /// <summary>Room for a temporary value: in the frame inside a method, or a fixed place in class code.</summary>
        string AllocTemp(int bytes)
        {
            bytes = (bytes + 3) & ~3;
            if (currentMethod != null)
            {
                frameSize += bytes;
                return $"ebp - {frameSize}";
            }

            string label = $"phi_temp_{staticTemps.Count}";
            staticTemps.Add((label, bytes));
            return label;
        }

        static bool InFrame(string home) => home.StartsWith("ebp");

        /// <summary>Loads the address of a variable (a label, or a place in the frame) into a register.</summary>
        void LoadAddress(string register, string home, int offset = 0)
        {
            string plus = offset == 0 ? "" : offset > 0 ? $" + {offset}" : $" - {-offset}";
            Emit(InFrame(home) ? $"lea {register}, [{home}{plus}]" : $"mov {register}, {home}{plus}");
        }

        void EmitRawBlock(RawBlockDecl raw)
        {
            Line("");
            Comment($"asm.{raw.Name}");
            Label(raw.Name);

            var text = new StringBuilder();
            int i = 0;
            while (i < raw.Text.Length)
            {
                int open = raw.Text.IndexOf('{', i);
                int close = open < 0 ? -1 : raw.Text.IndexOf('}', open);
                if (close < 0) { text.Append(raw.Text, i, raw.Text.Length - i); break; }

                text.Append(raw.Text, i, open - i);
                string name = raw.Text[(open + 1)..close].Trim();

                string? replacement = ResolveAsmName(name);
                if (replacement != null) text.Append(replacement);
                else diagnostics.Error(raw.File, new Span(raw.TextSpan.Start + open, close - open + 1),
                        $"asm.{raw.Name} uses {{{name}}}, but there is no class variable or constant named '{name}'");

                i = close + 1;
            }

            foreach (string line in text.ToString().Replace("\r\n", "\n").Split('\n'))
                Line(line.TrimEnd());
        }

        /// <summary>{name} in an asm block: a variable's address, a field's address, or a constant's value.</summary>
        string? ResolveAsmName(string name)
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
            return offset == 0 ? v.Label : $"{v.Label} + {offset}";
        }

        // ================================================================ statements

        int unsafeDepth;

        /// <summary>Where return jumps to in the method being generated.</summary>
        string returnLabel = "";
        bool returnUsed;

        /// <summary>The loops around the current statement, innermost last, for break and continue.</summary>
        sealed class LoopLabels
        {
            public string Next = "", End = "";
            public bool NextUsed;
        }
        readonly List<LoopLabels> loops = new();

        void EmitStatement(Stmt stmt)
        {
            if (stmt is not (VarDecl { Symbol.NeedsInitCode: false } or ConstDecl or UnsafeStmt)) SourceComment(stmt);

            switch (stmt)
            {
                case VarDecl { IsNew: true, Symbol: { Type.IsObject: true } newObject } newDecl:
                    EmitNewDecl(newDecl, newObject);
                    break;

                case VarDecl decl:
                    if (decl.Symbol is { NeedsInitCode: true } declared) EmitInitialize(declared, decl.Values);
                    break;

                case ObjectAssignStmt assign:
                    EmitObjectAssign(assign);
                    break;

                case TakesStmt takes:
                    EmitTakes(takes);
                    break;

                case ConstDecl:
                    break;

                case LogStmt log:
                    EmitLog(log);
                    if (unit.Kind == UnitKind.Program && log.Target == LogTarget.ScreenAndSerial)
                    {
                        // a whole log statement reaches the screen at once, even with other programs running
                        Need("phi_flush");
                        Emit("call phi_flush");
                    }
                    break;

                case AskStmt ask:
                {
                    var v = (VariableSymbol)ask.Target.Symbol!;
                    Need("phi_read_line");
                    if (v.Grows)
                    {
                        // a line of up to AskRoom - 1 characters
                        LoadAddress("edi", v.Label);
                        Emit($"mov ecx, {AskRoom}");
                        EmitStrRoomCall();
                        Emit("push edx");
                        Emit("dec ecx");
                        Emit("call phi_read_line");
                        EmitFreeOld();
                        break;
                    }
                    LoadAddress("edi", v.Label);
                    Emit($"mov ecx, {v.Capacity - 1}");
                    Emit("call phi_read_line");
                    break;
                }

                case ExitStmt exit when unit.Kind == UnitKind.Program:
                    if (exit.Code != null) EmitExpr(exit.Code);
                    else Emit("xor eax, eax");
                    Need("phi_exit");
                    Emit("mov ebx, eax            ; the exit code goes back to whoever waits for us");
                    Emit("jmp phi_exit");
                    break;

                case ExitStmt exit:
                    if (exit.Code != null) EmitExpr(exit.Code);
                    else Emit("xor eax, eax");
                    Emit("mov dx, 0xF4            ; QEMU isa-debug-exit");
                    Emit("out dx, al");
                    Emit("jmp phi_halt            ; not running in QEMU");
                    break;

                case OutStmt o:
                    if (ConstantFolder.Evaluate(o.Port) is long port)
                    {
                        EmitExpr(o.Value);
                        Emit($"mov dx, 0x{port & 0xFFFF:X}");
                    }
                    else
                    {
                        EmitExpr(o.Value);
                        Emit("push eax");
                        EmitExpr(o.Port);
                        Emit("mov dx, ax");
                        Emit("pop eax");
                    }
                    Emit(o.Size switch { 1 => "out dx, al", 2 => "out dx, ax", _ => "out dx, eax" });
                    break;

                case FreeStmt { Owner: StructSymbol owner } f:
                    // a struct that owns objects: they go with it
                    EmitExpr(f.Pointer);
                    Emit($"call {ObjectRoutine("free", owner)}");
                    break;

                case FreeStmt f:
                    EmitHeapCall(f.Releaser!, f.Pointer);
                    break;

                case UnsafeStmt u:
                    unsafeDepth++;
                    foreach (Stmt s in u.Body) EmitStatement(s);
                    unsafeDepth--;
                    break;

                case CallStmt call:
                    EmitCall(call);
                    break;

                case IfStmt ifs:
                {
                    string elseLabel = NewLabel("else");
                    string endLabel = NewLabel("endif");

                    EmitBranch(ifs.Condition, jumpIf: false, ifs.Else != null ? elseLabel : endLabel);
                    foreach (Stmt s in ifs.Then) EmitStatement(s);

                    if (ifs.Else != null)
                    {
                        Emit($"jmp {endLabel}");
                        Label(elseLabel);
                        foreach (Stmt s in ifs.Else) EmitStatement(s);
                    }

                    Label(endLabel);
                    break;
                }

                case WhileStmt loop:
                {
                    if (loop.Init?.Symbol != null) EmitInitialize(loop.Init.Symbol, loop.Init.Values);

                    string top = NewLabel("while");
                    string end = NewLabel("endwhile");
                    var labels = new LoopLabels { Next = NewLabel("next"), End = end };

                    Label(top);
                    EmitBranch(loop.Condition, jumpIf: false, end);
                    loops.Add(labels);
                    foreach (Stmt s in loop.Body) EmitStatement(s);
                    loops.RemoveAt(loops.Count - 1);
                    if (labels.NextUsed) Label(labels.Next); // continue runs the step, then tests again
                    if (loop.Step != null) EmitStatement(loop.Step);
                    Emit($"jmp {top}");
                    Label(end);
                    break;
                }

                case BreakStmt:
                    Emit($"jmp {loops[^1].End}");
                    break;

                case ContinueStmt:
                    loops[^1].NextUsed = true;
                    Emit($"jmp {loops[^1].Next}");
                    break;

                case ReturnStmt ret:
                    if (ret.Value != null && currentMethod is { ReturnType.IsObject: true }) EmitObjectResult(ret.Value);
                    else if (ret.Value != null) EmitExpr(ret.Value); // the result goes back in eax
                    returnUsed = true;
                    Emit($"jmp {returnLabel}");
                    break;

                case AssignStmt assign:
                    EmitAssign(assign.Target, assign.Value);
                    break;

                case AppendStmt append:
                    if (append.Values.Count == 0) EmitAppend(append, null);
                    foreach (Expr value in append.Values) EmitAppend(append, value);
                    break;

                case SetLengthStmt shrink:
                {
                    EmitExpr(shrink.Value);
                    Emit("push eax");
                    EmitFilledCount(shrink.Array);
                    Emit("mov edx, eax");
                    Emit("pop eax");
                    EmitIndexCheck(shrink, "jbe", "edx");
                    Emit($"mov dword {Operand(new Place { Computed = true, Linear = true, Offset = shrink.Array.Type.CountOffset })}, eax");
                    break;
                }

                case StringBuildStmt build:
                    for (int i = 0; i < build.Parts.Count; i++)
                    {
                        Expr part = build.Parts[i];
                        bool text = IsText(part.Type);
                        if (text && StaticAddress(part) is string label) LoadAddress("eax", label);
                        else EmitExpr(part);
                        EmitStringStore(build.Target, text, append: build.Append || i > 0, unsigned: part.Type.IsUnsigned32);
                    }
                    break;

                default:
                    throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}");
            }
        }

        static NameExpr NameOf(VariableSymbol v) => new() { Name = v.Name, Symbol = v, Type = v.Type };

        void EmitInitialize(VariableSymbol v, List<Expr> values)
        {
            // no value: zero, every time the declaration runs
            if (values.Count == 0)
            {
                if (v.Type.IsNumeric)
                {
                    Emit("xor eax, eax");
                    EmitStoreEax(NameOf(v));
                }
                else
                {
                    int size = StorageSize(v);
                    LoadAddress("edi", v.Label);
                    Emit($"mov ecx, {size}");
                    Emit("xor al, al");
                    Emit("rep stosb");
                }
                return;
            }

            if (!v.Type.IsArray)
            {
                EmitAssign(NameOf(v), values[0]);
                return;
            }

            for (int i = 0; i < values.Count; i++)
            {
                var element = new IndexExpr
                {
                    Target = NameOf(v),
                    Index = new NumberExpr { Value = i, Type = PhiType.Int },
                    Type = v.Type.Element,
                };
                EmitAssign(element, values[i]);
            }
        }

        void EmitLog(LogStmt log)
        {
            bool serialOnly = log.Target == LogTarget.SerialOnly;
            string suffix = serialOnly ? "_serial" : "";

            foreach (Expr value in log.Values)
            {
                // a ptr<u8> prints as the zero-terminated text it points to, like a str
                if (IsText(value.Type))
                {
                    string print = "phi_print" + suffix;
                    Need(print);
                    if (StaticAddress(value) is string label) LoadAddress("esi", label);
                    else
                    {
                        EmitExpr(value);
                        Emit("mov esi, eax");
                    }
                    Emit($"call {print}");
                }
                else
                {
                    string print = (value.Type.IsUnsigned32 ? "phi_print_uint" : "phi_print_int") + suffix;
                    Need(print);
                    EmitExpr(value);
                    Emit($"call {print}");
                }
            }
        }

        /// <summary>A str, or a ptr&lt;u8&gt;: the address of zero-terminated text, copied or printed as text.</summary>
        static bool IsText(PhiType t) => t.IsString || (t.IsPointer && t.Pointee == PhiType.U8);

        /// <summary>The label of a string whose address is known without running code.</summary>
        string? StaticAddress(Expr e) => e switch
        {
            StringExpr s => StringLabel(s.Value),
            NameExpr { Symbol: VariableSymbol { Type.IsString: true, Grows: false } v } => v.Label,
            _ => null,
        };

        void EmitCall(CallStmt call)
        {
            EmitCallTo(call.Resolved, call.Arguments);
            if (call.ResultTarget == null) return;

            PhiType result = call.Resolved switch
            {
                MethodSymbol method => method.ReturnType,
                PointerCall pointer => pointer.Signature.Result,
                _ => PhiType.Int,
            };
            if (result.IsObject)
            {
                // the call made a new object: the target takes it over
                Emit("push eax");
                EmitTakeOver(call.ResultTarget, result.Struct!);
                return;
            }
            EmitStoreResult(call.ResultTarget, result);
        }

        /// <summary>Calls a method, built-in or asm block; a result comes back in eax.</summary>
        void EmitCallTo(object? callee, List<Expr> arguments)
        {
            switch (callee)
            {
                case MethodSymbol method:
                {
                    // push each argument in order (a missing one is the parameter's default, or zero);
                    // a str parameter gets the address of the text
                    int count = method.Parameters.Count;
                    for (int i = 0; i < count; i++)
                    {
                        VariableSymbol p = method.Parameters[i];
                        Expr? value = i < arguments.Count ? arguments[i] : p.Decl.Values.FirstOrDefault();

                        if (value == null) Emit(p.Type.IsString ? $"mov eax, {StringLabel("")}" : "xor eax, eax");
                        else EmitExpr(value);

                        if (p.Type.IsString && value != null && !IsText(value.Type))
                        {
                            // a number for a str parameter is written out as text first
                            string text = AllocTemp(12);
                            Need("phi_int_to_str");
                            LoadAddress("edi", text);
                            Emit("mov ecx, 12");
                            Emit("call phi_int_to_str");
                            LoadAddress("eax", text);
                        }
                        Emit("push eax");
                    }

                    int pushed = count;
                    if (method.ReturnType.IsString)
                    {
                        // somewhere for the method to put its result
                        LoadAddress("eax", AllocTemp(method.ResultCapacity));
                        Emit("push eax");
                        pushed++;
                    }

                    Emit($"call {method.Label}");
                    if (pushed > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {pushed * 4}");
                    break;
                }

                case BuiltinFunction builtin:
                {
                    for (int i = arguments.Count - 1; i >= 0; i--)
                    {
                        EmitExpr(arguments[i]);
                        Emit("push eax");
                    }
                    Need(builtin.Label);
                    Emit($"call {builtin.Label}");
                    if (arguments.Count > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {arguments.Count * 4}");
                    break;
                }

                case RawBlockDecl raw:
                    Emit($"call {raw.Name}");
                    break;

                case PointerCall pointer:
                {
                    // the same as calling the method by name, through the address the variable holds
                    IReadOnlyList<PhiType> types = pointer.Signature.Parameters;
                    for (int i = 0; i < types.Count; i++)
                    {
                        Expr value = arguments[i];
                        EmitExpr(value);
                        if (types[i].IsString && !IsText(value.Type))
                        {
                            string text = AllocTemp(12);
                            Need("phi_int_to_str");
                            LoadAddress("edi", text);
                            Emit("mov ecx, 12");
                            Emit("call phi_int_to_str");
                            LoadAddress("eax", text);
                        }
                        Emit("push eax");
                    }
                    EmitExpr(pointer.Target);
                    Emit(bits32 ? "call eax" : "call ax");
                    if (types.Count > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {types.Count * 4}");
                    break;
                }
            }
        }

        /// <summary>Calls a one-parameter heap method (Heap.Alloc or Heap.Free) with a value.</summary>
        void EmitHeapCall(MethodSymbol method, Expr argument) =>
            EmitCallTo(method, new List<Expr> { argument });

        /// <summary>Stores a call result (in eax) into a variable.</summary>
        void EmitStoreResult(Expr target, PhiType resultType)
        {
            if (target.Type.IsString) EmitStringStore(target, sourceIsString: IsText(resultType));
            else EmitStoreEax(target);
        }

        // ================================================================ memory places

        /// <summary>
        /// Where a value lives. Either a fixed address (Label + Offset), or Label + ebx + Offset
        /// when part of the address is computed, or, for memory reached through a pointer, a
        /// linear address in ebx (+ Offset) that may be anywhere in the first megabyte.
        /// </summary>
        sealed class Place
        {
            public string? Label;
            public int Offset;
            public bool Computed;   // ebx holds part of the address
            public bool Linear;     // ebx holds a linear address, not an offset from Label
        }

        Place EmitPlace(Expr e)
        {
            switch (e)
            {
                case NameExpr { Symbol: VariableSymbol v }:
                    return new Place { Label = v.Label };

                case NameExpr { Symbol: FieldPathSymbol f }:
                    return new Place { Label = f.Root.Label, Offset = f.Offset };

                case NameExpr { Symbol: SelfFieldSymbol s }:
                    // through the struct the method was called on
                    Emit($"mov ebx, dword [{s.Self.Label}]");
                    return new Place { Computed = true, Linear = true, Offset = s.Offset };

                case NameExpr { Symbol: PathSymbol path }:
                {
                    // through objects: read the address each one is at, then go on from there
                    Emit($"mov ebx, dword {Operand(new Place { Label = path.Root.Label, Offset = path.BaseOffset })}");
                    for (int i = 0; i < path.Hops.Count - 1; i++)
                        Emit($"mov ebx, dword {Operand(new Place { Computed = true, Linear = true, Offset = path.Hops[i] })}");
                    return new Place { Computed = true, Linear = true, Offset = path.Hops[^1] };
                }

                case MemberExpr m when m.Target.Type.IsObject || m.Target.Type.IsPointer:
                    EmitExpr(m.Target); // where the object (or the struct a pointer points at) is
                    Emit("mov ebx, eax");
                    return new Place { Computed = true, Linear = true, Offset = m.Field!.Offset };

                case MemberExpr m:
                {
                    Place p = EmitPlace(m.Target);
                    p.Offset += m.Field!.Offset;
                    return p;
                }

                case IndexExpr ix:
                    return EmitIndexPlace(ix);
            }

            throw new InvalidOperationException($"{e.GetType().Name} has no address");
        }

        Place EmitIndexPlace(IndexExpr ix)
        {
            PhiType target = ix.Target.Type;
            int size = target.IsString ? 1 : target.IsPointer ? target.Pointee!.ElementSize : target.ElementSize;
            long? constant = ConstantFolder.Evaluate(ix.Index);

            // the characters of a str that grows: checked against its room, which can change
            if (ix.Target is NameExpr { Symbol: VariableSymbol { Grows: true } text })
            {
                EmitExpr(ix.Index);
                if (unsafeDepth == 0)
                {
                    Emit($"mov edx, [{text.Label} + 4]");
                    Emit($"and edx, ~{InData}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit($"mov ebx, dword [{text.Label}]");
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            // through a pointer, or the characters of a str that isn't a plain variable
            bool viaValue = target.IsPointer || (target.IsString && ix.Target is not NameExpr { Symbol: VariableSymbol });

            // p:i with a pointer and an index in variables: one scaled lea, nothing on the stack
            if (target.IsPointer && constant == null && size is 1 or 2 or 4 or 8
                && TryLoadSimple(ix.Target, "ebx", dryRun: true) && TryLoadSimple(ix.Index, "eax", dryRun: true))
            {
                TryLoadSimple(ix.Target, "ebx", dryRun: false);
                TryLoadSimple(ix.Index, "eax", dryRun: false);
                Emit(size == 1 ? "add ebx, eax" : $"lea ebx, [ebx + eax*{size}]");
                return new Place { Computed = true, Linear = true };
            }

            if (viaValue)
            {
                EmitExpr(ix.Target);

                if (constant != null)
                {
                    Emit("mov ebx, eax");
                    return new Place { Computed = true, Linear = true, Offset = (int)constant.Value * size };
                }
                Emit("push eax");
                EmitIndexValue(ix, size);
                Emit("pop ebx");
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            // an [atmost N] array: only the elements that are in it, however many fit
            if (target.Filled && unsafeDepth == 0)
            {
                EmitExpr(ix.Index);
                Emit("push eax");
                EmitFilledCount(ix.Target);
                Emit("mov edx, eax");
                Emit("pop eax");
                EmitIndexCheck(ix, "jb", "edx");
                EmitScale(size);
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            Place p = EmitPlace(ix.Target);

            if (constant != null)
            {
                p.Offset += (int)constant.Value * size;
                return p;
            }

            if (p.Computed)
            {
                Emit("push ebx");
                EmitIndexValue(ix, size);
                Emit("pop ebx");
                Emit("add ebx, eax");
            }
            else
            {
                EmitIndexValue(ix, size);
                Emit("mov ebx, eax");
                p.Computed = true;
            }
            return p;
        }

        /// <summary>eax = index * size, after checking the index when needed.</summary>
        void EmitIndexValue(IndexExpr ix, int size)
        {
            EmitExpr(ix.Index);

            if (ix.CheckLimit is int limit && unsafeDepth == 0)
                EmitIndexCheck(ix, "jb", limit.ToString());

            EmitScale(size);
        }

        /// <summary>Stops on an index error unless eax passes the unsigned jump against limit.</summary>
        void EmitIndexCheck(Node at, string jump, string limit)
        {
            Need("phi_index_error");
            string ok = NewLabel("inbounds");
            Emit($"cmp eax, {limit}");
            Emit($"{jump} {ok}                ; unsigned, so negative indexes fail too");
            Emit($"mov eax, {LineOf(at)}");
            Emit("jmp phi_index_error");
            Label(ok);
        }

        /// <summary>eax = eax * size, for an element's offset.</summary>
        void EmitScale(int size)
        {
            switch (size)
            {
                case 1: break;
                case 2: Emit("shl eax, 1"); break;
                case 4: Emit("shl eax, 2"); break;
                default: Emit($"imul eax, eax, {size}"); break;
            }
        }

        /// <summary>ebx = where an [atmost N] array is (linear), eax = how many elements are in it.</summary>
        void EmitFilledCount(Expr array)
        {
            EmitPlaceAddress(array);
            Emit("mov ebx, eax");
            if (!bits32) Emit("push ebx");  // reaching memory in 16-bit code changes ebx
            Emit($"mov eax, dword {Operand(new Place { Computed = true, Linear = true, Offset = array.Type.CountOffset })}");
            if (!bits32) Emit("pop ebx");
        }

        /// <summary>One element at the end of an [atmost N] array: value (or zeros, for null).</summary>
        void EmitAppend(AppendStmt append, Expr? value)
        {
            PhiType t = append.Target.Type;
            PhiType element = t.Element;
            int size = element.ElementSize;

            if (value != null)
            {
                EmitExpr(value);
                Emit("push eax");
            }
            EmitFilledCount(append.Target);
            EmitIndexCheck(append, "jb", t.Length.ToString());   // full: no room for another

            Emit("mov edx, eax");
            Emit("inc eax");
            if (!bits32) Emit("push ebx");
            Emit($"mov dword {Operand(new Place { Computed = true, Linear = true, Offset = t.CountOffset })}, eax");
            if (!bits32) Emit("pop ebx");
            Emit("mov eax, edx");
            EmitScale(size);
            Emit("add ebx, eax");

            if (value != null)
            {
                Emit("pop eax");
                Emit(StoreInstruction(element, Operand(new Place { Computed = true, Linear = true })));
            }
            else if (element.IsNumeric || element.IsMethod)
            {
                Emit("xor eax, eax");
                Emit(StoreInstruction(element, Operand(new Place { Computed = true, Linear = true })));
            }
            else
            {
                // a struct: every byte of it, since one dropped earlier may have been here
                Emit("mov edi, ebx");
                Emit($"mov ecx, {size}");
                Emit("xor al, al");
                Emit("rep stosb");
            }
        }

        /// <summary>
        /// The memory operand for a place. In 16-bit code a linear address is reached through fs
        /// (which changes ecx); 32-bit memory is flat.
        /// </summary>
        string Operand(Place p)
        {
            string offset = p.Offset == 0 ? "" : p.Offset > 0 ? $" + {p.Offset}" : $" - {-p.Offset}";

            if (!p.Computed) return $"[{p.Label}{offset}]";
            if (!p.Linear) return $"[{p.Label} + ebx{offset}]";

            if (bits32) return $"[ebx{offset}]"; // memory is flat

            if (p.Offset != 0) Emit($"add ebx, {p.Offset}");
            Emit("mov ecx, ebx");
            Emit("shr ecx, 4");
            Emit("mov fs, cx               ; reach any address below 1 MB");
            Emit("and ebx, 0xF");
            return "[fs:ebx]";
        }

        /// <summary>How a value of this type sits in memory: str array elements are 32-bit pointers.</summary>
        static PhiType StorageType(PhiType t) => t.IsString ? PhiType.U32 : t;

        string LoadInstruction(PhiType type, string register, string operand)
        {
            PhiType t = StorageType(type);
            return t.ElementSize switch
            {
                1 => $"{(t.IsSigned ? "movsx" : "movzx")} {register}, byte {operand}",
                2 => $"{(t.IsSigned ? "movsx" : "movzx")} {register}, word {operand}",
                _ => $"mov {register}, dword {operand}",
            };
        }

        static string StoreInstruction(PhiType type, string operand) => StorageType(type).ElementSize switch
        {
            1 => $"mov byte {operand}, al",
            2 => $"mov word {operand}, ax",
            _ => $"mov dword {operand}, eax",
        };

        void EmitLoad(Expr e)
        {
            Place p = EmitPlace(e);
            Emit(LoadInstruction(e.Type, "eax", Operand(p)));
        }

        int StorageSize(VariableSymbol v) =>
            v.Grows ? 8 : v.Type.IsString ? v.Capacity : v.Type.ByteSize;

        // ================================================================ assignment

        void EmitAssign(Expr target, Expr value)
        {
            if (!target.Type.IsString)
            {
                // x++, x-- and x++ 5 on a variable: change it where it is
                if (value is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract } step
                    && ReferenceEquals(step.Left, target) && target is NameExpr && IsStaticPlace(target)
                    && ConstantFolder.Evaluate(step.Right) is long amount)
                {
                    string size = StorageType(target.Type).ElementSize switch { 1 => "byte", 2 => "word", _ => "dword" };
                    string op = step.Op == BinaryOp.Add ? "add" : "sub";
                    Emit($"{op} {size} {Operand(EmitPlace(target))}, {(int)amount}");
                    return;
                }

                // a simple value into a computed place: the address first, then the value,
                // which needs no registers the address is in
                if (!IsStaticPlace(target) && TryLoadSimple(value, "eax", dryRun: true))
                {
                    string operand = Operand(EmitPlace(target));
                    TryLoadSimple(value, "eax", dryRun: false);
                    Emit(StoreInstruction(target.Type, operand));
                    return;
                }
            }

            EmitExpr(value);
            if (target.Type.IsString) EmitStringStore(target, sourceIsString: IsText(value.Type));
            else EmitStoreEax(target);
        }

        /// <summary>Stores eax into a number variable, field or element.</summary>
        void EmitStoreEax(Expr target)
        {
            bool needsCode = !IsStaticPlace(target); // computing the address uses eax

            if (needsCode) Emit("push eax");
            Place p = EmitPlace(target);
            string operand = Operand(p);
            if (needsCode) Emit("pop eax");
            Emit(StoreInstruction(target.Type, operand));
        }

        /// <summary>True when the place's address is fixed, so computing it emits no code.</summary>
        static bool IsStaticPlace(Expr e) => e switch
        {
            NameExpr { Symbol: VariableSymbol or FieldPathSymbol } => true,
            MemberExpr m => !m.Target.Type.IsObject && !m.Target.Type.IsPointer && IsStaticPlace(m.Target),
            IndexExpr ix => !ix.Target.Type.IsPointer && !ix.Target.Type.Filled && ConstantFolder.Evaluate(ix.Index) != null && IsStaticPlace(ix.Target)
                            && !(ix.Target.Type.IsString && ix.Target is not NameExpr { Symbol: VariableSymbol { Grows: false } }),
            _ => false,
        };

        /// <summary>
        /// eax holds a string address (sourceIsString) or a number to write out as text;
        /// put it in a str variable or str array element, cut to fit.
        /// </summary>
        void EmitStringStore(Expr target, bool sourceIsString, bool append = false, bool unsigned = false)
        {
            if (target is NameExpr { Symbol: VariableSymbol { Grows: true } growing })
            {
                EmitGrowStore(growing, sourceIsString, append, unsigned);
                return;
            }

            int capacity;
            if (target is NameExpr { Symbol: VariableSymbol v })
            {
                capacity = v.Capacity;
                LoadAddress("edi", v.Label);
            }
            else
            {
                // an element of a str array: its address is the element's value
                capacity = ((VariableSymbol)((NameExpr)((IndexExpr)target).Target).Symbol!).Capacity;
                Emit("push eax");
                EmitExpr(target);
                Emit("mov edi, eax");
                Emit("pop eax");
            }

            Emit($"mov ecx, {capacity}");
            if (append)
            {
                // past the text already there, with that much less room
                Need("phi_str_end");
                Emit("call phi_str_end");
            }
            if (sourceIsString)
            {
                Need("phi_strcpy");
                Emit("mov esi, eax");
                Emit("call phi_strcpy");
            }
            else
            {
                string convert = unsigned ? "phi_uint_to_str" : "phi_int_to_str";
                Need(convert);
                Emit($"call {convert}");
            }
        }

        // ================================================================ expressions

        /// <summary>Leaves the value of <paramref name="e"/> in eax (strings: their address).</summary>
        void EmitExpr(Expr e)
        {
            if (e is BinaryExpr or UnaryExpr && ConstantFolder.Evaluate(e) is long folded)
            {
                LoadConstant("eax", folded);
                return;
            }

            switch (e)
            {
                case CallExpr call:
                    EmitCallTo(call.Resolved, call.Arguments);
                    break;

                case NameExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    break;

                case MemberExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    break;

                case NumberExpr n:
                    LoadConstant("eax", n.Value);
                    break;

                case BoolExpr b:
                    LoadConstant("eax", b.Value ? 1 : 0);
                    break;

                case StringExpr s:
                    Emit($"mov eax, {StringLabel(s.Value)}");
                    break;

                case NameExpr { Symbol: VariableSymbol { Grows: true } v }:
                    LoadText("eax", v);
                    break;

                case NameExpr { Symbol: VariableSymbol { Type.IsString: true } v }:
                    LoadAddress("eax", v.Label);
                    break;

                case NameExpr { Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol }:
                    EmitLoad(e);
                    break;

                case EmptyExpr:
                    Emit("xor eax, eax");
                    break;

                case NameExpr { Symbol: ConstantSymbol k }:
                    LoadConstant("eax", k.Value);
                    break;

                case NameExpr { Symbol: LengthSymbol { Count: int count } }:
                    LoadConstant("eax", count);
                    break;

                case MemberExpr { Count: int count }:
                    LoadConstant("eax", count);
                    break;

                case NameExpr { Symbol: LengthSymbol { Filled: Expr array } }:
                    EmitFilledCount(array);
                    break;

                case MemberExpr { FilledLength: true } m:
                    EmitFilledCount(m.Target);
                    break;

                case NameExpr { Symbol: LengthSymbol { Str: VariableSymbol str } }:
                    Need("phi_strlen");
                    if (str.Grows) LoadText("esi", str);
                    else LoadAddress("esi", str.Label);
                    Emit("call phi_strlen");
                    break;

                case IndexExpr or MemberExpr:
                    EmitLoad(e);
                    break;

                case AddrExpr addr:
                    EmitAddress(addr);
                    break;

                case SizeofExpr size:
                    LoadConstant("eax", size.Value);
                    break;

                case CastExpr cast:
                    EmitExpr(cast.Operand);
                    // a smaller number keeps the low bytes, like storing it would
                    switch (cast.Type.Kind)
                    {
                        case TypeKind.U8 or TypeKind.Bool: Emit("movzx eax, al"); break;
                        case TypeKind.I8: Emit("movsx eax, al"); break;
                        case TypeKind.U16: Emit("movzx eax, ax"); break;
                        case TypeKind.I16: Emit("movsx eax, ax"); break;
                    }
                    break;

                case NewExpr n:
                    EmitHeapCall(n.Allocator!, n.Bytes); // the address comes back in eax
                    break;

                case InExpr input:
                    if (ConstantFolder.Evaluate(input.Port) is long port) Emit($"mov dx, 0x{port & 0xFFFF:X}");
                    else
                    {
                        EmitExpr(input.Port);
                        Emit("mov dx, ax");
                    }
                    switch (input.Size)
                    {
                        case 1: Emit("in al, dx"); Emit("movzx eax, al"); break;
                        case 2: Emit("in ax, dx"); Emit("movzx eax, ax"); break;
                        default: Emit("in eax, dx"); break;
                    }
                    break;

                case UnaryExpr unary:
                    EmitExpr(unary.Operand);
                    switch (unary.Op)
                    {
                        case UnaryOp.Negate: Emit("neg eax"); break;
                        case UnaryOp.BitNot: Emit("not eax"); break;
                        default:
                            Emit("test eax, eax");
                            Emit("sete al");
                            Emit("movzx eax, al");
                            break;
                    }
                    break;

                case BinaryExpr { IsComparison: true } or BinaryExpr { IsLogical: true }:
                {
                    string falseLabel = NewLabel("false");
                    string endLabel = NewLabel("bool");
                    EmitBranch(e, jumpIf: false, falseLabel);
                    Emit("mov eax, 1");
                    Emit($"jmp {endLabel}");
                    Label(falseLabel);
                    Emit("xor eax, eax");
                    Label(endLabel);
                    break;
                }

                case BinaryExpr binary:
                    EmitArithmetic(binary);
                    break;

                default:
                    throw new InvalidOperationException($"unexpected expression {e.GetType().Name}");
            }
        }

        void EmitAddress(AddrExpr addr)
        {
            if (addr.CodeLabel != null)
            {
                Emit($"mov eax, {addr.CodeLabel}");
                return;
            }

            // an object is at the address it holds
            if (addr.OfObject) EmitExpr(addr.Operand);
            else EmitPlaceAddress(addr.Operand);
        }

        /// <summary>eax = the address of a variable, field or element.</summary>
        void EmitPlaceAddress(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol { Grows: true } text })
            {
                LoadText("eax", text);  // addr s: where its text is now
                return;
            }
            if (e is NameExpr { Symbol: VariableSymbol v })
            {
                LoadAddress("eax", v.Label);
                return;
            }

            Place p = EmitPlace(e);
            string offset = p.Offset == 0 ? "" : $" + {p.Offset}";
            if (!p.Computed) LoadAddress("eax", p.Label!, p.Offset);
            else if (!p.Linear) Emit($"lea eax, [{p.Label} + ebx{offset}]");
            else Emit($"lea eax, [ebx{offset}]");
        }

        void EmitArithmetic(BinaryExpr binary)
        {
            bool unsigned = binary.Type.IsUnsigned32;
            EmitOperands(binary);

            switch (binary.Op)
            {
                case BinaryOp.Add: Emit("add eax, ecx"); break;
                case BinaryOp.Subtract: Emit("sub eax, ecx"); break;
                case BinaryOp.Multiply: Emit("imul eax, ecx"); break;
                case BinaryOp.BitAnd: Emit("and eax, ecx"); break;
                case BinaryOp.BitOr: Emit("or eax, ecx"); break;
                case BinaryOp.BitXor: Emit("xor eax, ecx"); break;
                case BinaryOp.ShiftLeft: Emit("shl eax, cl"); break;
                case BinaryOp.ShiftRight: Emit(unsigned ? "shr eax, cl" : "sar eax, cl"); break;
                case BinaryOp.Divide:
                case BinaryOp.Modulo:
                    if (unsigned)
                    {
                        Emit("xor edx, edx");
                        Emit("div ecx");
                    }
                    else
                    {
                        Emit("cdq");
                        Emit("idiv ecx");
                    }
                    if (binary.Op == BinaryOp.Modulo) Emit("mov eax, edx");
                    break;
            }
        }

        void LoadConstant(string register, long value)
        {
            if (value == 0) Emit($"xor {register}, {register}");
            else Emit($"mov {register}, {(int)value}");
        }

        /// <summary>Left operand in eax, right operand in ecx.</summary>
        void EmitOperands(BinaryExpr b)
        {
            if (TryLoadSimple(b.Right, "ecx", dryRun: true))
            {
                EmitExpr(b.Left);
                TryLoadSimple(b.Right, "ecx", dryRun: false);
                return;
            }

            EmitExpr(b.Right);
            Emit("push eax");
            EmitExpr(b.Left);
            Emit("pop ecx");
        }

        /// <summary>Loads values that need no other registers straight into a register.</summary>
        bool TryLoadSimple(Expr e, string register, bool dryRun)
        {
            string? instruction = ConstantFolder.Evaluate(e) is long k
                ? (k == 0 ? $"xor {register}, {register}" : $"mov {register}, {(int)k}")
                : e switch
                {
                    StringExpr s => $"mov {register}, {StringLabel(s.Value)}",
                    NameExpr { Symbol: VariableSymbol { Type.IsString: true, Grows: false } v } =>
                        InFrame(v.Label) ? $"lea {register}, [{v.Label}]" : $"mov {register}, {v.Label}",
                    NameExpr { Symbol: VariableSymbol { Type.IsNumeric: true } v } => LoadInstruction(v.Type, register, $"[{v.Label}]"),
                    NameExpr { Symbol: FieldPathSymbol { Type.IsNumeric: true } f } =>
                        LoadInstruction(f.Type, register, f.Offset == 0 ? $"[{f.Root.Label}]" : $"[{f.Root.Label} + {f.Offset}]"),
                    _ => null,
                };

            if (instruction == null) return false;
            if (!dryRun) Emit(instruction);
            return true;
        }

        /// <summary>Jumps to <paramref name="target"/> when the condition's truth equals <paramref name="jumpIf"/>.</summary>
        void EmitBranch(Expr condition, bool jumpIf, string target)
        {
            if (ConstantFolder.Evaluate(condition) is long known)
            {
                if ((known != 0) == jumpIf) Emit($"jmp {target}");
                return;
            }

            switch (condition)
            {
                case UnaryExpr { Op: UnaryOp.Not } not:
                    EmitBranch(not.Operand, !jumpIf, target);
                    return;

                case BinaryExpr { Op: BinaryOp.And } and:
                    if (jumpIf)
                    {
                        string skip = NewLabel("and");
                        EmitBranch(and.Left, false, skip);
                        EmitBranch(and.Right, true, target);
                        Label(skip);
                    }
                    else
                    {
                        EmitBranch(and.Left, false, target);
                        EmitBranch(and.Right, false, target);
                    }
                    return;

                case BinaryExpr { Op: BinaryOp.Or } or:
                    if (jumpIf)
                    {
                        EmitBranch(or.Left, true, target);
                        EmitBranch(or.Right, true, target);
                    }
                    else
                    {
                        string skip = NewLabel("or");
                        EmitBranch(or.Left, true, skip);
                        EmitBranch(or.Right, false, target);
                        Label(skip);
                    }
                    return;

                case BinaryExpr { ObjectCompare: StructSymbol compared } cmp:
                {
                    bool equalWanted = (cmp.Op == BinaryOp.Equal) == jumpIf;
                    if (cmp.Left is EmptyExpr || cmp.Right is EmptyExpr)
                    {
                        EmitExpr(cmp.Left is EmptyExpr ? cmp.Right : cmp.Left);
                        Emit("test eax, eax          ; 0 = empty");
                        Emit($"{(equalWanted ? "jz" : "jnz")} {target}");
                        return;
                    }
                    EmitObjectAddress(cmp.Right);
                    Emit("push eax");
                    EmitObjectAddress(cmp.Left);
                    Emit("pop edx");
                    Emit($"call {ObjectRoutine("equal", compared)}   ; eax = 1 when the values match");
                    Emit("test eax, eax");
                    Emit($"{(equalWanted ? "jnz" : "jz")} {target}");
                    return;
                }

                case BinaryExpr { IsComparison: true, Left.Type.IsString: true, Right.Type.IsString: true } cmp:
                {
                    Need("phi_strcmp");
                    EmitExpr(cmp.Right);
                    Emit("push eax");
                    EmitExpr(cmp.Left);
                    Emit("mov esi, eax");
                    Emit("pop edi");
                    Emit("call phi_strcmp          ; eax = 0 when equal");
                    Emit("test eax, eax");
                    bool equalWanted = (cmp.Op == BinaryOp.Equal) == jumpIf;
                    Emit($"{(equalWanted ? "jz" : "jnz")} {target}");
                    return;
                }

                case BinaryExpr { IsComparison: true } cmp:
                {
                    bool unsigned = cmp.Left.Type.IsUnsigned32 || cmp.Right.Type.IsUnsigned32;
                    EmitOperands(cmp);
                    Emit("cmp eax, ecx");
                    BinaryOp op = jumpIf ? cmp.Op : Invert(cmp.Op);
                    string jump = op switch
                    {
                        BinaryOp.Equal => "je",
                        BinaryOp.NotEqual => "jne",
                        BinaryOp.Less => unsigned ? "jb" : "jl",
                        BinaryOp.LessEqual => unsigned ? "jbe" : "jle",
                        BinaryOp.Greater => unsigned ? "ja" : "jg",
                        _ => unsigned ? "jae" : "jge",
                    };
                    Emit($"{jump} {target}");
                    return;
                }
            }

            EmitExpr(condition);
            Emit("test eax, eax");
            Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
        }

        static BinaryOp Invert(BinaryOp op) => op switch
        {
            BinaryOp.Equal => BinaryOp.NotEqual,
            BinaryOp.NotEqual => BinaryOp.Equal,
            BinaryOp.Less => BinaryOp.GreaterEqual,
            BinaryOp.LessEqual => BinaryOp.Greater,
            BinaryOp.Greater => BinaryOp.LessEqual,
            _ => BinaryOp.Less,
        };

        // ================================================================ objects
        //
        // An object variable or field holds the address of the struct's memory (0 = empty).
        // Each object has one owner, so `is` copies values, `takes` hands memory over, and an
        // owner frees what it owns. The work is done by routines generated for each struct:
        //
        //   phi_free_S    eax = object or 0         frees it and everything it owns
        //   phi_clone_S   eax = object or 0         eax = a new copy (or 0)
        //   phi_assign_S  eax = into, edx = from    copies the values, keeping into's memory
        //   phi_set_S     eax = slot, edx = from    copies into the slot's object (a new copy if empty)
        //   phi_renew_S   eax = slot                a zeroed object in the slot (old one emptied)
        //   phi_equal_S   eax = a, edx = b          eax = 1 when the values match
        //
        // Like library routines, they may change any register but ebp. A chain through a field
        // of the struct's own type (a list's next) is followed with a loop, not recursion, so a
        // long list can't use up the stack.

        readonly HashSet<(string Kind, StructSymbol Struct)> objectRoutines = new();
        bool usesObjects;

        string ObjectRoutine(string kind, StructSymbol s)
        {
            usesObjects = true;
            objectRoutines.Add((kind, s));
            return $"phi_{kind}_{s.Name}";
        }

        /// <summary>Remembers the source line, for the message if the heap runs out.</summary>
        void ObjectLine(Node node)
        {
            usesObjects = true;
            Emit($"mov dword [phi_object_line], {LineOf(node)}");
        }

        /// <summary>eax = where an object's values are: what an object holds, or a struct's own place.</summary>
        void EmitObjectAddress(Expr e)
        {
            if (e.Type.IsObject) EmitExpr(e);
            else EmitPlaceAddress(e);
        }

        void EmitNewDecl(VarDecl decl, VariableSymbol v)
        {
            if (decl.AtAddress != null)
            {
                // a view of memory that's already there
                EmitExpr(decl.AtAddress);
                EmitStoreEax(NameOf(v));
                return;
            }

            if (decl.Source != null)
            {
                EmitObjectAssign(decl.Source);
                return;
            }

            // a zeroed object (the first time; after that the old one is emptied and reused),
            // then its fields from the values, in order
            ObjectLine(decl);
            LoadAddress("eax", v.Label);
            Emit($"call {ObjectRoutine("renew", v.Type.Struct!)}");
            foreach (Stmt field in decl.FieldValues) EmitStatement(field);
        }

        void EmitObjectAssign(ObjectAssignStmt o)
        {
            StructSymbol s = o.Struct;
            switch (o.Kind)
            {
                case ObjectAssignKind.Clear:
                    EmitPlaceAddress(o.Target);
                    Emit("mov edx, eax");
                    Emit("mov eax, [edx]");
                    Emit("mov dword [edx], 0       ; empty");
                    Emit($"call {ObjectRoutine("free", s)}");
                    break;

                case ObjectAssignKind.Copy:
                    ObjectLine(o);
                    EmitObjectAddress(o.Value);
                    Emit("push eax");
                    if (o.TargetOwned)
                    {
                        EmitPlaceAddress(o.Target);
                        Emit("pop edx");
                        Emit($"call {ObjectRoutine("set", s)}");
                    }
                    else
                    {
                        EmitObjectAddress(o.Target);
                        Emit("pop edx");
                        Emit($"call {ObjectRoutine("assign", s)}");
                    }
                    break;

                case ObjectAssignKind.Move:
                    ObjectLine(o);
                    EmitExpr(o.Value); // the call's new object
                    Emit("push eax");
                    EmitTakeOver(o.Target, s);
                    break;
            }
        }

        /// <summary>
        /// A new object (on the stack) goes to the target: an owner takes it and frees what it
        /// had; anything else gets a copy of its values, and the new object is freed.
        /// </summary>
        void EmitTakeOver(Expr target, StructSymbol s)
        {
            if (Binder.IsOwnedPlace(target))
            {
                EmitPlaceAddress(target);
                Emit("pop ecx");
                Emit("mov edx, [eax]          ; what it had");
                Emit("mov [eax], ecx");
                Emit("mov eax, edx");
                Emit($"call {ObjectRoutine("free", s)}");
                return;
            }

            EmitObjectAddress(target);
            Emit("mov edx, [esp]");
            Emit($"call {ObjectRoutine("assign", s)}");
            Emit("pop eax");
            Emit($"call {ObjectRoutine("free", s)}");
        }

        void EmitTakes(TakesStmt t)
        {
            EmitPlaceAddress(t.Source);
            Emit("push eax");
            EmitPlaceAddress(t.Target);
            Emit("pop edx");
            Emit("mov ecx, [edx]          ; the object handed over");
            Emit("mov dword [edx], 0       ; leaves the source empty");
            Emit("mov ebx, [eax]          ; what the target had");
            Emit("mov [eax], ecx");
            string kept = NewLabel("taken");
            Emit("cmp ebx, ecx");
            Emit($"je {kept}");
            Emit("mov eax, ebx");
            Emit($"call {ObjectRoutine("free", t.Struct!)}");
            Label(kept);
        }

        /// <summary>
        /// The object a method returns, in eax: one of its own new variables is handed over
        /// (and so not freed when the method ends), a call's new object passes straight
        /// through, and anything else is copied, so the caller always gets one of its own.
        /// </summary>
        void EmitObjectResult(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol { Ownership: Ownership.Owned, IsLocal: true, IsParameter: false } v })
            {
                EmitExpr(e);
                Emit($"mov dword {Operand(new Place { Label = v.Label })}, 0   ; handed to the caller");
                return;
            }
            if (Binder.IsObjectCall(e))
            {
                EmitExpr(e);
                return;
            }
            ObjectLine(e);
            EmitObjectAddress(e);
            Emit($"call {ObjectRoutine("clone", currentMethod!.ReturnType.Struct!)}");
        }

        // ---------------------------------------------------------------- the routines

        void EmitObjectRoutines()
        {
            var done = new HashSet<(string, StructSymbol)>();
            while (objectRoutines.FirstOrDefault(r => !done.Contains(r)) is { Struct: not null } next)
            {
                done.Add(next);
                Line("");
                Comment($"{next.Kind} for {next.Struct.Name} objects");
                Label($"phi_{next.Kind}_{next.Struct.Name}");
                switch (next.Kind)
                {
                    case "free": EmitFreeRoutine(next.Struct); break;
                    case "clone": EmitCloneRoutine(next.Struct); break;
                    case "assign": EmitAssignRoutine(next.Struct); break;
                    case "set": EmitSetRoutine(next.Struct); break;
                    case "renew": EmitRenewRoutine(next.Struct); break;
                    case "equal": EmitEqualRoutine(next.Struct); break;
                }
            }
        }

        string HeapMethodLabel(string name) => unit.Methods.First(m => m.Name == name).Label;

        /// <summary>The room <c>ask</c> makes in a str that grows: a line of up to 255 characters.</summary>
        const int AskRoom = 256;

        /// <summary>The bit of a growing str's room that says its text is in the program's data, not the heap.</summary>
        const string InData = "0x80000000";

        bool usesStrRoom;

        /// <summary>A register = the text of a str that grows; "" while nothing has been written to it.</summary>
        void LoadText(string register, VariableSymbol v)
        {
            string done = NewLabel("text");
            Emit($"mov {register}, dword [{v.Label}]");
            Emit($"test {register}, {register}");
            Emit($"jnz {done}");
            Emit($"mov {register}, {StringLabel("")}");
            Label(done);
        }

        /// <summary>
        /// edi = a str that grows, ecx = the bytes wanted in it (with the zero). After: edi = its
        /// text, with room for that many (or as many as the heap had), ecx = its room, and edx = its
        /// old text, to free once nothing reads from it (0 if it didn't move).
        /// </summary>
        void EmitStrRoomCall()
        {
            usesStrRoom = true;
            Emit("call phi_str_room");
        }

        /// <summary>Frees the old text EmitStrRoomCall left, which was pushed after it.</summary>
        void EmitFreeOld()
        {
            Emit($"call {HeapMethodLabel("Heap.Free")}   ; its old text (freeing 0 does nothing)");
            Emit("add esp, 4");
        }

        /// <summary>
        /// eax holds a string address (sourceIsString) or a number to write out as text; put it
        /// in a str that grows, making room first, up to its max.
        /// </summary>
        void EmitGrowStore(VariableSymbol v, bool sourceIsString, bool append, bool unsigned)
        {
            Emit("push eax                ; the value");
            if (sourceIsString)
            {
                Need("phi_strlen");
                Emit("mov esi, eax");
                Emit("call phi_strlen");
                Emit("inc eax                 ; with its zero");
            }
            else Emit("mov eax, 12             ; the most a number takes, with its zero");
            if (append)
            {
                Need("phi_strlen");
                Emit("push eax");
                LoadText("esi", v);
                Emit("call phi_strlen");
                Emit("pop ecx");
                Emit("add eax, ecx            ; after what's there");
            }
            Emit("mov ecx, eax");
            LoadAddress("edi", v.Label);
            EmitStrRoomCall();
            Emit("push edx");
            Emit("mov eax, [esp + 4]      ; the value");
            if (append)
            {
                Need("phi_str_end");
                Emit("call phi_str_end");
            }
            if (sourceIsString)
            {
                Need("phi_strcpy");
                Emit("mov esi, eax");
                Emit("call phi_strcpy");
            }
            else
            {
                string convert = unsigned ? "phi_uint_to_str" : "phi_int_to_str";
                Need(convert);
                Emit($"call {convert}");
            }
            EmitFreeOld();
            Emit("add esp, 4");
        }

        /// <summary>phi_str_room, for strs that grow (see EmitStrRoomCall).</summary>
        void EmitStrRoomRoutine()
        {
            if (!usesStrRoom) return;
            Need("phi_strcpy");
            string max = (Binder.StrMax + 1).ToString();
            Line("");
            Comment("room in a str that grows: edi = the str, ecx = bytes wanted");
            Label("phi_str_room");
            Emit($"cmp ecx, {max}");
            Emit("jbe .capped");
            Emit($"mov ecx, {max}               ; never more than its max");
            Label(".capped");
            Emit("xor edx, edx");
            Emit("mov eax, [edi + 4]");
            Emit($"and eax, ~{InData}      ; its room (the top bit: its text is in the data)");
            Emit("cmp ecx, eax");
            Emit("jbe .fits");
            // the new room: twice the old, and at least what's wanted (and 16), up to the max
            Emit("add eax, eax");
            Emit("cmp eax, ecx");
            Emit("jae .twice");
            Emit("mov eax, ecx");
            Label(".twice");
            Emit("cmp eax, 16");
            Emit("jae .small");
            Emit("mov eax, 16");
            Label(".small");
            Emit($"cmp eax, {max}");
            Emit("jbe .room");
            Emit($"mov eax, {max}");
            Label(".room");
            Emit("push edi");
            Emit("push eax                ; [esp] the new room, [esp + 4] the str");
            Emit("push eax");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit("add esp, 4");
            Emit("pop ecx");
            Emit("pop edi");
            Emit("test eax, eax");
            Emit("jz .fits                ; no memory: it keeps the room it has");
            // the text so far goes along, then the old place is the caller's to free
            Emit("push edi");
            Emit("push ecx");
            Emit("push eax");
            Emit("mov esi, [edi]");
            Emit("test esi, esi");
            Emit("jz .copied");
            Emit("mov edi, eax");
            Emit("call phi_strcpy");
            Label(".copied");
            Emit("pop eax");
            Emit("pop ecx");
            Emit("pop edi");
            Emit("mov edx, [edi]          ; the old text, to free (unless it was in the data)");
            Emit($"test dword [edi + 4], {InData}");
            Emit("jz .heap");
            Emit("xor edx, edx");
            Label(".heap");
            Emit("mov [edi], eax");
            Emit("mov [edi + 4], ecx");
            Label(".fits");
            Emit("mov ecx, [edi + 4]");
            Emit($"and ecx, ~{InData}");
            Emit("mov edi, [edi]");
            Emit("ret");
        }

        /// <summary>eax = size bytes of zeroed memory from the heap; stops the kernel if there's none.</summary>
        void EmitObjectAlloc(StructSymbol s)
        {
            Need("phi_out_of_memory");
            Emit($"push dword {s.Size}");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit("add esp, 4");
            Emit("test eax, eax");
            Emit("jz phi_out_of_memory");
        }

        /// <summary>The owned slot followed with a loop: one of the struct's own type (a list's next).</summary>
        static OwnedSlot? Chain(StructSymbol s) =>
            s.OwnedSlots.Where(o => o.Of == s).Select(o => (OwnedSlot?)o).LastOrDefault();

        static IEnumerable<OwnedSlot> NotChain(StructSymbol s) =>
            s.OwnedSlots.Where(o => o != Chain(s));

        void EmitFreeRoutine(StructSymbol s)
        {
            OwnedSlot? chain = Chain(s);
            Emit("push ebp");
            Emit("mov ebp, esp");
            Label(".next");
            Emit("test eax, eax");
            Emit("jz .done");
            Emit("push eax                ; [ebp - 4] the object");
            foreach (OwnedSlot slot in NotChain(s))
            {
                Emit("mov eax, [ebp - 4]");
                Emit($"mov eax, [eax + {slot.Offset}]");
                Emit($"call {ObjectRoutine("free", slot.Of)}");
            }
            if (chain is OwnedSlot c)
            {
                Emit("mov eax, [ebp - 4]");
                Emit($"push dword [eax + {c.Offset}]   ; [ebp - 8] the next one, freed by going round again");
            }
            Emit("push dword [ebp - 4]");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit("add esp, 4");
            if (chain != null)
            {
                Emit("pop eax");
                Emit("mov esp, ebp");
                Emit("jmp .next");
            }
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitCloneRoutine(StructSymbol s)
        {
            OwnedSlot? chain = Chain(s);
            Emit("test eax, eax");
            Emit("jnz .some");
            Emit("ret                     ; a copy of nothing is nothing");
            Label(".some");
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit("push eax                ; [ebp - 4] the source");
            EmitObjectAlloc(s);
            Emit("push eax                ; [ebp - 8] the copy, returned");
            Emit("push eax                ; [ebp - 12] the copy being filled");
            Label(".fill");
            Emit("mov esi, [ebp - 4]");
            Emit("mov edi, [ebp - 12]");
            Emit($"mov ecx, {s.Size}");
            Emit("rep movsb");
            foreach (OwnedSlot slot in NotChain(s))
            {
                Emit("mov eax, [ebp - 4]");
                Emit($"mov eax, [eax + {slot.Offset}]");
                Emit($"call {ObjectRoutine("clone", slot.Of)}");
                Emit("mov edx, [ebp - 12]");
                Emit($"mov [edx + {slot.Offset}], eax");
            }
            if (chain is OwnedSlot c)
            {
                Emit("mov edx, [ebp - 12]");
                Emit($"mov dword [edx + {c.Offset}], 0");
                Emit("mov eax, [ebp - 4]");
                Emit($"mov eax, [eax + {c.Offset}]");
                Emit("test eax, eax");
                Emit("jz .done");
                Emit("mov [ebp - 4], eax      ; the next source");
                EmitObjectAlloc(s);
                Emit("mov edx, [ebp - 12]");
                Emit($"mov [edx + {c.Offset}], eax");
                Emit("mov [ebp - 12], eax");
                Emit("jmp .fill");
            }
            Label(".done");
            Emit("mov eax, [ebp - 8]");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitAssignRoutine(StructSymbol s)
        {
            if (!s.OwnsObjects)
            {
                Emit("mov edi, eax");
                Emit("mov esi, edx");
                Emit($"mov ecx, {s.Size}");
                Emit("rep movsb");
                Emit("ret");
                return;
            }

            // copies of what the source owns are made before anything changes, because the
            // source may be inside the target (head is head.next); the target's old objects are
            // freed last
            int n = s.OwnedSlots.Count;
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit("push eax                ; [ebp - 4] into");
            Emit("push edx                ; [ebp - 8] from");
            foreach (OwnedSlot slot in s.OwnedSlots)
            {
                Emit("mov eax, [ebp - 8]");
                Emit($"mov eax, [eax + {slot.Offset}]");
                Emit($"call {ObjectRoutine("clone", slot.Of)}");
                Emit("push eax");
            }
            foreach (OwnedSlot slot in s.OwnedSlots)
            {
                Emit("mov eax, [ebp - 4]");
                Emit($"push dword [eax + {slot.Offset}]");
            }
            Emit("mov esi, [ebp - 8]");
            Emit("mov edi, [ebp - 4]");
            Emit($"mov ecx, {s.Size}");
            Emit("rep movsb");
            for (int i = 0; i < n; i++)
            {
                Emit("mov edx, [ebp - 4]");
                Emit($"mov eax, [ebp - {12 + 4 * i}]");
                Emit($"mov [edx + {s.OwnedSlots[i].Offset}], eax");
            }
            for (int i = 0; i < n; i++)
            {
                Emit($"mov eax, [ebp - {12 + 4 * n + 4 * i}]");
                Emit($"call {ObjectRoutine("free", s.OwnedSlots[i].Of)}");
            }
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitSetRoutine(StructSymbol s)
        {
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit("push eax                ; [ebp - 4] the slot");
            Emit("push edx                ; [ebp - 8] the source");
            Emit("test edx, edx");
            Emit("jnz .copy");
            Emit("mov edx, [ebp - 4]      ; a copy of nothing: the slot is emptied");
            Emit("mov eax, [edx]");
            Emit("mov dword [edx], 0");
            Emit($"call {ObjectRoutine("free", s)}");
            Emit("jmp .done");
            Label(".copy");
            Emit("mov edx, [ebp - 4]");
            Emit("mov eax, [edx]");
            Emit("test eax, eax");
            Emit("jnz .have");
            // an empty slot gets a fresh copy, made before the slot changes: the source may
            // contain it (a.next is a)
            Emit("mov eax, [ebp - 8]");
            Emit($"call {ObjectRoutine("clone", s)}");
            Emit("mov edx, [ebp - 4]");
            Emit("mov [edx], eax");
            Emit("jmp .done");
            Label(".have");
            Emit("mov edx, [ebp - 8]");
            Emit($"call {ObjectRoutine("assign", s)}");
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitRenewRoutine(StructSymbol s)
        {
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit("push eax                ; [ebp - 4] the slot");
            Emit("mov eax, [eax]");
            Emit("test eax, eax");
            Emit("jnz .reuse");
            EmitObjectAlloc(s);
            Emit("mov edx, [ebp - 4]");
            Emit("mov [edx], eax");
            Emit("jmp .done");
            Label(".reuse");
            Emit("push eax                ; [ebp - 8] the object, emptied to be used again");
            foreach (OwnedSlot slot in s.OwnedSlots)
            {
                Emit("mov eax, [ebp - 8]");
                Emit($"mov eax, [eax + {slot.Offset}]");
                Emit($"call {ObjectRoutine("free", slot.Of)}");
            }
            Emit("mov edi, [ebp - 8]");
            Emit($"mov ecx, {s.Size}");
            Emit("xor al, al");
            Emit("rep stosb");
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        void EmitEqualRoutine(StructSymbol s)
        {
            OwnedSlot? chain = Chain(s);
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit("push eax                ; [ebp - 4] a");
            Emit("push edx                ; [ebp - 8] b");
            Label(".next");
            Emit("mov eax, [ebp - 4]");
            Emit("mov edx, [ebp - 8]");
            Emit("cmp eax, edx");
            Emit("je .same                ; the same object (or both empty)");
            Emit("test eax, eax");
            Emit("jz .different");
            Emit("test edx, edx");
            Emit("jz .different");

            // the bytes between the owned slots, which hold addresses rather than values
            int start = 0;
            foreach (int end in s.OwnedSlots.Select(o => o.Offset).Append(s.Size))
            {
                if (end > start)
                {
                    Emit($"lea esi, [eax + {start}]");
                    Emit($"lea edi, [edx + {start}]");
                    Emit($"mov ecx, {end - start}");
                    Emit("repe cmpsb");
                    Emit("jne .different");
                }
                start = end + 4;
            }

            foreach (OwnedSlot slot in NotChain(s))
            {
                Emit("mov eax, [ebp - 4]");
                Emit($"mov eax, [eax + {slot.Offset}]");
                Emit("mov edx, [ebp - 8]");
                Emit($"mov edx, [edx + {slot.Offset}]");
                Emit($"call {ObjectRoutine("equal", slot.Of)}");
                Emit("test eax, eax");
                Emit("jz .different");
            }
            if (chain is OwnedSlot c)
            {
                Emit("mov eax, [ebp - 4]");
                Emit($"mov eax, [eax + {c.Offset}]");
                Emit("mov [ebp - 4], eax");
                Emit("mov edx, [ebp - 8]");
                Emit($"mov edx, [edx + {c.Offset}]");
                Emit("mov [ebp - 8], edx");
                Emit("jmp .next");
            }
            Label(".same");
            Emit("mov eax, 1");
            Emit("jmp .done");
            Label(".different");
            Emit("xor eax, eax");
            Label(".done");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Emit("ret");
        }

        // ================================================================ data

        void EmitData(StringBuilder output)
        {
            foreach (VariableSymbol v in unit.Variables.Distinct())
            {
                if (v.Type.IsError || v.IsLocal) continue; // a method's variables are in its frame
                output.Append(DataFor(v));
            }

            foreach (var (label, size) in staticTemps)
                output.AppendLine($"{label}: times {size} db 0");

            if (usesObjects) output.AppendLine("phi_object_line: dd 0");

            foreach (var (value, label) in stringLabels)
                output.AppendLine($"{label}: db {NasmBytes(value)}");
        }

        string DataFor(VariableSymbol v)
        {
            var sb = new StringBuilder();
            List<object>? values = v.StaticValues;
            PhiType t = v.Type;

            if (v.Grows)
            {
                if (values is [var first])
                {
                    // its first text is here, until it needs more room than that
                    string text = AsText(first);
                    AppendString(sb, $"{v.Label}__text", text, text.Length + 1);
                    sb.AppendLine($"{v.Label}: dd {v.Label}__text, {text.Length + 1} | {InData}   ; a str that grows: its text, its room");
                }
                else sb.AppendLine($"{v.Label}: dd 0, 0   ; a str that grows: its text (on the heap), its room");
                return sb.ToString();
            }

            if (t.IsString)
            {
                string text = values is [var first] ? AsText(first) : "";
                AppendString(sb, v.Label, text, v.Capacity);
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Str)
            {
                var pointers = new List<string>();
                for (int i = 0; i < v.Count; i++)
                {
                    string element = $"{v.Label}__{i}";
                    pointers.Add(element);
                    string text = values != null && i < values.Count ? AsText(values[i]) : "";
                    AppendString(sb, element, text, v.Capacity);
                }
                sb.AppendLine($"{v.Label}: dd {string.Join(", ", pointers)}");
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Struct)
            {
                sb.AppendLine($"{v.Label}: times {StorageSize(v)} db 0   ; {t}");
                return sb.ToString();
            }

            string directive = t.ElementSize switch { 1 => "db", 2 => "dw", _ => "dd" };
            if (values == null || values.Count == 0)
            {
                sb.AppendLine(v.Count == 1 ? $"{v.Label}: {directive} 0" : $"{v.Label}: times {v.Count} {directive} 0");
                if (t.Filled) sb.AppendLine("    dd 0   ; how many are in it");
                return sb.ToString();
            }

            long mask = t.ElementSize switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFFFFFF };
            var numbers = values.Select(x => x is long n ? n & mask : 0).Select(n => n.ToString());
            sb.AppendLine($"{v.Label}: {directive} {string.Join(", ", numbers)}");
            if (values.Count < v.Count) sb.AppendLine($"    times {v.Count - values.Count} {directive} 0");
            return sb.ToString();
        }

        /// <summary>A constant destined for a str: text as-is, numbers written in decimal.</summary>
        static string AsText(object value) => value switch
        {
            string s => s,
            long n => ((int)n).ToString(),
            _ => "",
        };

        static void AppendString(StringBuilder sb, string label, string text, int capacity)
        {
            if (text.Length + 1 > capacity) text = text[..Math.Max(capacity - 1, 0)];
            sb.AppendLine($"{label}: db {NasmBytes(text)}");
            int padding = capacity - text.Length - 1;
            if (padding > 0) sb.AppendLine($"    times {padding} db 0");
        }

        /// <summary>'Hello', 13, 10, 0 — printable text quoted, everything else as numbers.</summary>
        static string NasmBytes(string text)
        {
            var parts = new List<string>();
            var run = new StringBuilder();

            void Flush()
            {
                if (run.Length == 0) return;
                parts.Add($"'{run}'");
                run.Clear();
            }

            foreach (char c in text)
            {
                if (c >= 0x20 && c < 0x7F && c != '\'') run.Append(c);
                else
                {
                    Flush();
                    parts.Add(c < 256 ? ((int)c).ToString() : "'?'");
                }
            }

            Flush();
            parts.Add("0");
            return string.Join(", ", parts);
        }
    }
}

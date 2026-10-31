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
    /// can call themselves and be used from interrupt handlers. In 32-bit code a method's busiest
    /// variables live in esi and edi instead (see "variables in registers"); library routines may
    /// change any general-purpose register except ebp, so they're stored around those. Everything
    /// else is read and written in memory each time (as device memory needs).
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

            // the FPU (floating point numbers) starts clean
            if (bits32) Emit("fninit");

            // the program's own code first: it decides which library routines are needed
            foreach (ClassDecl cls in unit.Classes)
            {
                file = cls.File;
                Comment($"---- class {cls.Name} ({FileName(file)})");
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
            EmitArrayGrowRoutine();

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

        static string FileName(SourceFile f) => f.Path.Replace('\\', '/');

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
            // the class and file are what phi profile reports a method by
            string kind = m.IsHook ? "event" : m.IsInterruptHandler ? "interrupt handler" : "method";
            Comment($"{kind} {m.Name} ({FileName(file)})");
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

            // the variables used most go in registers
            currentMethod = m;
            resultType = m.ReturnType;
            inRegisters.Clear();
            addressTaken.Clear();
            foreach (Node n in m.Decl.Body.SelectMany(SyntaxWalker.Nodes))
                if (n is AddrExpr { Operand: NameExpr { Symbol: VariableSymbol taken } }) addressTaken.Add(taken);
            List<VariableSymbol> chosen = bits32
                ? VariablePlanner.Choose(m, VariableRegisters.Length, FitsRegister, RunsInRegisters)
                : new List<VariableSymbol>();
            Frame frame = LayOutFrame(m);
            for (int i = 0; i < chosen.Count; i++) inRegisters.Add((chosen[i], VariableRegisters[i], chosen[i].Label));
            bool keepsRegisters = inRegisters.Count > 0;

            resultSlots.Clear();
            frameSize = frame.LocalsSize;
            returnLabel = NewLabel("return");
            returnUsed = false;

            // the body first: the temporary space it needs decides the frame's size
            StringBuilder outer = code;
            code = new StringBuilder();
            SetRegistersLive(keepsRegisters);

            foreach (Stmt s in m.Decl.Body) EmitStatement(s);

            if (m.Decl.Result is Expr result)
            {
                SourceComment(result);
                void EmitResult()
                {
                    if (m.ReturnType.IsObject) EmitObjectResult(result);
                    else if (m.ReturnType.IsWide) EmitWide(result);   // in edx:eax
                    else if (m.ReturnType.IsFloat) EmitFloat(result); // in st0
                    else EmitExpr(result); // the result goes back in eax
                }
                if (OneRegister(m.ReturnType) && SafeValue(result)) EmitResult();
                else Fenced(EmitResult);
            }

            if (returnUsed) Label(returnLabel); // return jumps here with the result in eax

            // what's left only tidies up, and may use any register: the variables are done with
            if (keepsRegisters)
            {
                SetRegistersLive(false);
                Line(FenceStart);
            }

            if (m.ReturnType.IsString && GrowResults)
            {
                // copy the result into the caller's slot (it grows to fit) before this frame goes away
                Need("phi_strlen");
                Need("phi_strcpy");
                Emit("push eax                ; the result");
                Emit("mov esi, eax");
                Emit("call phi_strlen");
                Emit("lea ecx, [eax + 1]");
                Emit($"mov edi, [ebp + {frame.ResultSlot}]");
                EmitStrRoomCall();
                Emit("push edx");
                Emit("mov esi, [esp + 4]");
                Emit("call phi_strcpy");
                EmitFreeOld();
                Emit("add esp, 4");
                string text = NewLabel("result");
                Emit($"mov eax, [ebp + {frame.ResultSlot}]");
                Emit("mov eax, [eax]");
                Emit("test eax, eax");
                Emit($"jnz {text}");
                Emit($"mov eax, {StringLabel("")}");
                Label(text);
            }
            else if (m.ReturnType.IsString)
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
            var growingArrays = m.Locals.Where(v => v.GrowingElements).ToList();
            bool wideResult = m.ReturnType.IsWide;
            if (growing.Count > 0 || resultSlots.Count > 0 || growingArrays.Count > 0 || m.Locals.Any(v => v.GrowingArray))
            {
                if (wideResult) Emit("push edx");
                Emit("push eax                ; the result");
                foreach (VariableSymbol v in growing)
                {
                    Emit($"push dword [{v.Label}]   ; {v.Name}'s text");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit("add esp, 4");
                }
                foreach (string slot in resultSlots)
                {
                    Emit($"push dword [{slot}]   ; a str result it was given");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit("add esp, 4");
                }
                foreach (VariableSymbol array in m.Locals.Where(v => v.GrowingArray))
                    EmitGrowingArrayFree(array);
                foreach (VariableSymbol array in growingArrays)
                    for (int i = 0; i < array.Count; i++)
                    {
                        // an element that moved to the heap (one still in its own buffer stays)
                        string kept = NewLabel("own");
                        Emit($"test dword [{array.Label} + {4 * (array.Count + i)}], {InData}");
                        Emit($"jnz {kept}");
                        Emit($"push dword [{array.Label} + {4 * i}]   ; {array.Name}:{i}");
                        Emit($"call {HeapMethodLabel("Heap.Free")}");
                        Emit("add esp, 4");
                        Label(kept);
                    }
                Emit("pop eax");
                if (wideResult) Emit("pop edx");
            }

            // the method's new objects go away with it (one it returns was handed over already)
            var owned = m.Locals.Where(v => v.Ownership == Ownership.Owned).ToList();
            if (owned.Count > 0)
            {
                if (wideResult) Emit("push edx");
                Emit("push eax                ; the result");
                foreach (VariableSymbol v in owned)
                {
                    if (v.Type.IsObjectArray)
                    {
                        EmitFreeElements(NameOf(v), 0);
                        continue;
                    }
                    Emit($"mov eax, dword [{v.Label}]   ; {v.Name}");
                    Emit($"call {ObjectRoutine("free", v.Type.Struct!)}");
                }
                Emit("pop eax");
                if (wideResult) Emit("pop edx");
            }

            StringBuilder body = code;
            bool hasFrame = frameSize > 0 || m.Parameters.Count > 0 || m.ReturnType.IsString;
            code = new StringBuilder();
            if (hasFrame) EmitFrameStart(frame);
            string start = code.ToString();
            code = outer;
            string bodyText = keepsRegisters ? CheckRegisterCode(m, body.ToString()) : body.ToString();

            // callers keep variables in esi and edi, so a method that changes them (itself, or
            // through anything but a method) saves them first, just below its frame, and gives them
            // back at the end (an interrupt handler has saved every register already)
            var saved = bits32 && !m.IsInterruptHandler
                ? VariableRegisters.Where(r => MayChange(start + bodyText, r)).ToList()
                : new List<string>();
            if (hasFrame) EmitFrameSetup();
            foreach (string register in saved) Emit($"push {register}                ; the caller's");
            code.Append(start);
            foreach (var (v, register, home) in inRegisters)
                Emit(v.IsParameter ? $"mov {register}, [{home}]   ; {v.Name}" : $"xor {register}, {register}   ; {v.Name}");
            code.Append(bodyText);
            if (hasFrame)
            {
                for (int i = 0; i < saved.Count; i++) Emit($"mov {saved[i]}, [ebp - {frameSize + 4 * (i + 1)}]");
                Emit("mov esp, ebp");
                Emit("pop ebp");
            }
            else
            {
                for (int i = saved.Count - 1; i >= 0; i--) Emit($"pop {saved[i]}");
            }
            inRegisters.Clear();

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

        /// <summary>What return gives back: the method's result type, or an inlined method's.</summary>
        PhiType resultType = PhiType.Void;
        int frameSize;

        /// <summary>Space for class code's temporary values, which has no frame.</summary>
        readonly List<(string Label, int Size)> staticTemps = new();

        // ================================================================ variables in registers

        /// <summary>
        /// The registers a method's variables may live in (32-bit code only). A method that uses
        /// them saves them first and puts them back before it returns, so they're kept across calls
        /// to other methods. Everything else (library routines, asm blocks, str copies) may change
        /// them, so a statement that needs one of those runs with the variables in memory: they're
        /// stored to their places in the frame before it, and loaded again after it.
        /// </summary>
        static readonly string[] VariableRegisters = { "esi", "edi" };

        /// <summary>The method's variables in registers, with their places in the frame.</summary>
        readonly List<(VariableSymbol Variable, string Register, string Home)> inRegisters = new();

        /// <summary>True while the variables are in their registers (false inside a fence).</summary>
        bool registersLive;

        // Generated code marks: a register variable is written ‹esi› (so a stray esi is easy to
        // find), a fence's code sits between the two fence marks, and while the variables are in
        // registers their labels are «name», which nothing may use as memory. All are taken out
        // (or checked for) when the method is done.
        const string FenceStart = "    ; «memory»";
        const string FenceEnd = "    ; «registers»";

        /// <summary>A variable's register while it's in one, or null.</summary>
        string? RegisterOf(VariableSymbol v)
        {
            if (!registersLive) return null;
            foreach (var (variable, register, _) in inRegisters)
                if (variable == v) return $"‹{register}›";
            return null;
        }

        /// <summary>A 32-bit variable as an instruction's operand: its register, or its memory.</summary>
        string VariableOperand(VariableSymbol v) => RegisterOf(v) ?? $"dword [{v.Label}]";

        /// <summary>The variables in registers or in memory, by what their labels say.</summary>
        void SetRegistersLive(bool live)
        {
            registersLive = live;
            foreach (var (v, _, home) in inRegisters) v.Label = live ? $"«{v.Name}»" : home;
        }

        void StoreRegisters()
        {
            foreach (var (v, register, home) in inRegisters) Emit($"mov [{home}], {register}   ; {v.Name}");
        }

        void LoadRegisters()
        {
            foreach (var (v, register, home) in inRegisters) Emit($"mov {register}, [{home}]   ; {v.Name}");
        }

        /// <summary>Runs code that may change any register with the variables in memory.</summary>
        void Fenced(Action emit)
        {
            if (!registersLive)
            {
                emit();
                return;
            }
            Line(FenceStart);
            StoreRegisters();
            SetRegistersLive(false);
            emit();
            LoadRegisters();
            SetRegistersLive(true);
            Line(FenceEnd);
        }

        /// <summary>A variable that can live in a register: a 32-bit number, enum or pointer.</summary>
        bool FitsRegister(VariableSymbol v) =>
            v.Type.IsNumeric && StorageType(v.Type).ElementSize == 4 && v.Ownership == Ownership.None && v.Decl.AtAddress == null;

        /// <summary>A value that's worked on in eax alone: a number, bln, chr, enum or pointer.</summary>
        static bool OneRegister(PhiType t) => t.IsNumeric;

        /// <summary>
        /// True when a statement can run with the variables in registers: its code only uses eax,
        /// ebx, ecx and edx, and calls nothing but methods (which keep esi and edi). An if, while
        /// or when whose test is like that runs its body with them too, statement by statement.
        /// Anything else is fenced.
        /// </summary>
        bool RunsInRegisters(Stmt s) => s switch
        {
            UnsafeStmt => true,
            LockedStmt l => SafeRoot(l.Lock),
            IfStmt i => SafeValue(i.Condition),
            WhileStmt w => (w.Init == null || SafeStatement(w.Init)) && SafeValue(w.Condition) && (w.Step == null || SafeStatement(w.Step)),
            WhenStmt w => OneRegister(w.Subject.Type) && !(w.Subject.Type.IsPointer && w.Subject.Type.Pointee == PhiType.U8)
                          && SafeValue(w.Subject) && w.Branches.All(b => b.Values.All(SafeValue)),
            _ => SafeStatement(s),
        };

        bool SafeStatement(Stmt s) => s switch
        {
            ConstDecl or BreakStmt or ContinueStmt => true,
            VarDecl { Symbol.NeedsInitCode: false } => true,
            VarDecl { IsNew: false, Symbol: VariableSymbol v } d => OneRegister(v.Type) && d.Values.Count <= 1 && d.Values.All(SafeValue),
            AssignStmt a => OneRegister(a.Target.Type) && SafeValue(a.Target) && SafeValue(a.Value),
            CallStmt c => SafeCall(c.Resolved, c.Arguments)
                          && (c.ResultTarget == null || OneRegister(c.ResultTarget.Type) && SafeValue(c.ResultTarget)),
            ReturnStmt r => r.Value == null || OneRegister(r.Value.Type) && resultType.IsNumeric && SafeValue(r.Value),
            OutStmt o => SafeValue(o.Port) && SafeValue(o.Value),
            AtomicStmt a => SafeValue(a.Target) && SafeValue(a.Value) && (a.Expected == null || SafeValue(a.Expected)),
            _ => false,
        };

        bool SafeValue(Expr e) => e switch
        {
            NumberExpr or BoolExpr or SizeofExpr => true,
            _ when OneRegister(e.Type) && ConstantFolder.Evaluate(e) != null => true,
            NameExpr { Call: CallExpr c } => SafeCall(c.Resolved, c.Arguments),
            MemberExpr { Call: CallExpr c } => SafeCall(c.Resolved, c.Arguments),
            CallExpr c => SafeCall(c.Resolved, c.Arguments),
            NameExpr { Symbol: ConstantSymbol } => true,
            NameExpr { Symbol: LengthSymbol { Count: int } } => true,
            MemberExpr { Count: int } => true,
            NameExpr { Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol } => OneRegister(e.Type),
            MemberExpr m => OneRegister(m.Type) && !m.FilledLength && !m.TextLength && SafeRoot(m.Target),
            IndexExpr ix => OneRegister(ix.Type) && !ix.Target.Type.IsString && SafeRoot(ix.Target) && SafeValue(ix.Index),
            AddrExpr a => a.CodeLabel != null || !a.OfObject && !a.Operand.Type.IsHeapText && SafeRoot(a.Operand),
            CastExpr c => OneRegister(c.Type) && OneRegister(c.Operand.Type) && SafeValue(c.Operand),
            InExpr i => SafeValue(i.Port),
            AtomicExpr a => SafeValue(a.Target) && SafeValue(a.Value) && SafeValue(a.Expected),
            UnaryExpr u => OneRegister(u.Operand.Type) && SafeValue(u.Operand),
            BinaryExpr b => b.ObjectCompare == null && !b.ArrayCompare && OneRegister(b.Left.Type) && OneRegister(b.Right.Type)
                            && SafeValue(b.Left) && SafeValue(b.Right),
            _ => false,
        };

        /// <summary>What a field or element is reached through: a plain variable, field or element, or an address.</summary>
        bool SafeRoot(Expr e) => e switch
        {
            _ when e.Type.IsString => false,
            _ when e.Type.IsPointer => SafeValue(e),
            // an object is at the address its variable, field or element holds
            _ when e.Type.IsObject => e is NameExpr { Call: null, Symbol: VariableSymbol or FieldPathSymbol or SelfFieldSymbol or PathSymbol }
                                      || e is MemberExpr { Call: null } field && SafeRoot(field.Target)
                                      || e is IndexExpr element && SafeRoot(element.Target) && SafeValue(element.Index),
            NameExpr { Symbol: VariableSymbol v } => !v.GrowingArray && !v.GrowingElements && !v.Type.TextTable,
            NameExpr { Symbol: FieldPathSymbol or SelfFieldSymbol or PathSymbol } => true,
            MemberExpr m => m.Call == null && m.Count == null && !m.FilledLength && !m.TextLength && SafeRoot(m.Target),
            IndexExpr ix => SafeRoot(ix.Target) && SafeValue(ix.Index),
            _ => false,
        };

        /// <summary>A call to a method whose parameters and result are each one register.</summary>
        bool SafeCall(object? callee, List<Expr> arguments) =>
            callee is MethodSymbol method
            && (method.ReturnType.Kind == TypeKind.Void || OneRegister(method.ReturnType))
            && method.Parameters.All(p => OneRegister(p.Type) && !p.TextRef)
            && arguments.All(SafeValue)
            && method.Parameters.Skip(arguments.Count).All(p => p.Decl.Values.All(SafeValue));

        /// <summary>
        /// Checks a method's code: outside fences, nothing may use esi or edi except as the
        /// variables they hold, and no variable in a register may be read from memory.
        /// </summary>
        string CheckRegisterCode(MethodSymbol m, string body)
        {
            var kept = new StringBuilder();
            bool fenced = false;
            foreach (string line in body.Split('\n'))
            {
                string text = line.TrimEnd('\r');
                if (text == FenceStart) { fenced = true; continue; }
                if (text == FenceEnd) { fenced = false; continue; }
                if (!fenced && !text.Contains("it stops here") && System.Text.RegularExpressions.Regex.IsMatch(WithoutComment(text), StrayRegister))
                    throw new InvalidOperationException($"{m.Name}: generated code changes a variable's register: {text.Trim()}");
                if (text.Contains('«'))
                    throw new InvalidOperationException($"{m.Name}: generated code reads a variable in a register from memory: {text.Trim()}");
                kept.Append(text.Replace("‹", "").Replace("›", "")).Append('\n');
            }
            return kept.ToString();
        }

        static string WithoutComment(string line) => line.IndexOf(';') is int at and >= 0 ? line[..at] : line;

        /// <summary>
        /// True when a method's code may change the register: it names it, uses an instruction
        /// that changes it by itself, or calls something other than a method (a library routine,
        /// an asm block, an address), which may change any register.
        /// </summary>
        static bool MayChange(string code, string register)
        {
            string itself = register == "esi" ? "movs|lods|cmps" : "movs|stos|cmps|scas";
            var pattern = new System.Text.RegularExpressions.Regex($@"\b{register}\b|\b({itself})[bwd]\b|\bcall (?!M_)");
            return code.Split('\n').Any(line => pattern.IsMatch(WithoutComment(line)));
        }

        /// <summary>esi or edi named outside a ‹›, an instruction that uses them itself, or a library routine.</summary>
        const string StrayRegister = @"(?<!‹)\b(esi|edi)\b|\b(rep|repe|repne|movs[bwd]|stos[bwd]|lods[bwd]|cmps[bwd]|scas[bwd]|pushad|popad)\b|\bcall phi_";

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

            for (int i = m.Parameters.Count - 1; i >= 0; slot += ParameterBytes(m.Parameters[i].Type), i--)
            {
                VariableSymbol p = m.Parameters[i];
                if (p.Type.IsString && !p.TextRef)
                {
                    p.Label = Reserve(p.Grows ? 8 : p.Capacity);
                    frame.StrParams.Add((p, slot));
                }
                else p.Label = $"ebp + {slot}";
            }

            foreach (VariableSymbol v in m.Locals)
            {
                if (v.Type.TextTable)
                {
                    // a table of pointers, one per element, each to a buffer of its own (and then
                    // each one's room, when they grow, and a filled one's count)
                    v.Label = Reserve((v.GrowingElements ? 8 : 4) * v.Count + (v.Type.Filled ? 4 : 0));
                    var elements = new List<string>();
                    for (int i = 0; i < v.Count; i++) elements.Add(Reserve(v.Capacity));
                    frame.StrArrays.Add((v, elements));
                }
                else v.Label = Reserve(StorageSize(v));
            }

            frame.LocalsSize = size;
            return frame;
        }

        void EmitFrameSetup()
        {
            Emit("push ebp");
            // in 16-bit code only sp is the stack pointer; the top of esp may hold anything
            Emit(bits32 ? "mov ebp, esp" : "movzx ebp, sp");
            if (frameSize > 0) Emit($"sub esp, {frameSize}");
        }

        void EmitFrameStart(Frame frame)
        {
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

            // the slots for str results start empty
            foreach (string slot in resultSlots)
            {
                Emit($"mov dword [{slot}], 0");
                Emit($"mov dword [{slot} + 4], 0");
            }

            foreach (var (array, elements) in frame.StrArrays)
                for (int i = 0; i < elements.Count; i++)
                {
                    Emit($"lea eax, [{elements[i]}]");
                    Emit($"mov [{array.Label} + {4 * i}], eax");
                    if (array.GrowingElements)
                        Emit($"mov dword [{array.Label} + {4 * (array.Count + i)}], {array.Capacity} | {InData}");
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
            public bool RegistersLive;  // a break or continue from a fence inside loads them again
        }
        readonly List<LoopLabels> loops = new();

        void EmitStatement(Stmt stmt)
        {
            if (registersLive && !RunsInRegisters(stmt)) Fenced(() => EmitStatementCode(stmt));
            else EmitStatementCode(stmt);
        }

        void EmitStatementCode(Stmt stmt)
        {
            if (stmt is not (VarDecl { Symbol.NeedsInitCode: false } or ConstDecl or UnsafeStmt)) SourceComment(stmt);

            switch (stmt)
            {
                case VarDecl { IsNew: true, Symbol: { Type.IsObject: true } newObject } newDecl:
                    EmitNewDecl(newDecl, newObject);
                    break;

                case VarDecl { Symbol: { GrowingArray: true } growing }:
                    EmitGrowingArrayEmpty(growing);
                    break;

                case VarDecl { IsNew: true, Symbol: { Type.IsObjectArray: true } newArray } arrayDecl:
                    EmitNewArray(arrayDecl, newArray);
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

                case LockedStmt locked:
                    EmitLocked(locked);
                    break;

                case AtomicStmt atomic:
                    EmitAtomic(atomic);
                    break;

                case CallStmt call:
                    EmitCall(call);
                    break;

                case WhenStmt branches:
                    EmitWhen(branches);
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

                    // the test is at the bottom, so each time round ends in one jump back; the first
                    // time, a test that calls nothing is written out again, and any other is jumped to
                    string top = NewLabel("while");
                    string test = NewLabel("whiletest");
                    string end = NewLabel("endwhile");
                    var labels = new LoopLabels { Next = NewLabel("next"), End = end, RegistersLive = registersLive };

                    bool repeatTest = !HasCall(loop.Condition);
                    if (repeatTest) EmitBranch(loop.Condition, jumpIf: false, end);
                    else Emit($"jmp {test}");
                    Label(top);
                    loops.Add(labels);
                    var bound = InRangeIn(loop);
                    if (bound is var (counter, below)) inRange.Add((counter, below));
                    foreach (Stmt s in loop.Body) EmitStatement(s);
                    if (bound != null) inRange.RemoveAt(inRange.Count - 1);
                    loops.RemoveAt(loops.Count - 1);
                    if (labels.NextUsed) Label(labels.Next); // continue runs the step, then tests again
                    if (loop.Step != null) EmitStatement(loop.Step);
                    if (!repeatTest) Label(test);
                    EmitBranch(loop.Condition, jumpIf: true, top);
                    Label(end);
                    break;
                }

                case WhileEachStmt each when each.Current != null:
                {
                    // the count from 0, while it's under how many the array has now
                    string count = each.Index.Symbol!.Label;
                    string top = NewLabel("each");
                    string end = NewLabel("endeach");
                    var labels = new LoopLabels { Next = NewLabel("nexteach"), End = end, RegistersLive = registersLive };
                    // (tested before the first time round, then at the bottom, like a while)
                    Emit($"mov dword [{count}], 0");
                    EmitArrayCount(each.Array);
                    Emit($"cmp [{count}], eax");
                    Emit($"jae {end}");
                    Label(top);
                    if (each.Fetch != null) EmitStatement(each.Fetch);
                    else
                    {
                        // the element itself: an object's address, or where a struct is in the array
                        if (each.Current.Type.IsObject) EmitExpr(each.Current);
                        else EmitPlaceAddress(each.Current);
                        Emit($"mov [{each.Element.Symbol!.Label}], eax");
                    }
                    loops.Add(labels);
                    foreach (Stmt s in each.Body) EmitStatement(s);
                    loops.RemoveAt(loops.Count - 1);
                    if (labels.NextUsed) Label(labels.Next);
                    Emit($"inc dword [{count}]");
                    EmitArrayCount(each.Array);
                    Emit($"cmp [{count}], eax");
                    Emit($"jb {top}");
                    Label(end);
                    break;
                }

                case WhileEachStmt:
                    break;   // (an error was reported)

                case BreakStmt:
                    UnlockLeaving(loops.Count);
                    if (loops[^1].RegistersLive && !registersLive) LoadRegisters();  // out of a fence
                    Emit($"jmp {loops[^1].End}");
                    break;

                case ContinueStmt:
                    UnlockLeaving(loops.Count);
                    if (loops[^1].RegistersLive && !registersLive) LoadRegisters();
                    loops[^1].NextUsed = true;
                    Emit($"jmp {loops[^1].Next}");
                    break;

                case ReturnStmt ret:
                    if (ret.Value != null && resultType.IsObject) EmitObjectResult(ret.Value);
                    else if (ret.Value != null && resultType.IsWide) EmitWide(ret.Value);   // in edx:eax
                    else if (ret.Value != null && resultType.IsFloat) EmitFloat(ret.Value); // in st0
                    else if (ret.Value != null) EmitExpr(ret.Value); // the result goes back in eax
                    UnlockLeaving(-1);
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

                case SetLengthStmt { Array: NameExpr { Symbol: VariableSymbol { GrowingArray: true } growing } } shrink:
                {
                    EmitExpr(shrink.Value);
                    EmitIndexCheck(shrink, "jbe", $"dword [{growing.Label} + 4]");
                    Emit($"mov [{growing.Label} + 4], eax");
                    if (growing.Type.IsObjectArray) EmitFreeElements(shrink.Array, -1);   // the dropped ones
                    break;
                }

                case SetLengthStmt shrink:
                {
                    EmitExpr(shrink.Value);
                    Emit("push eax");
                    EmitFilledCount(shrink.Array);
                    Emit("mov edx, eax");
                    Emit("pop eax");
                    EmitIndexCheck(shrink, "jbe", "edx");
                    Emit($"mov dword {Operand(new Place { Computed = true, Linear = true, Offset = shrink.Array.Type.CountOffset })}, eax");
                    if (shrink.Array.Type.IsObjectArray) EmitFreeElements(shrink.Array, -1);   // the dropped ones
                    break;
                }

                case ArrayAssignStmt copy:
                    EmitArrayAssign(copy);
                    break;

                case StringBuildStmt build:
                    for (int i = 0; i < build.Parts.Count; i++)
                    {
                        Expr part = build.Parts[i];
                        bool text = IsText(part.Type) || part.Type.IsWide || part.Type.IsFloat || part.Type.IsChar;
                        if (part.Type.IsWide || part.Type.IsFloat || part.Type.IsChar) EmitNumberText(part);
                        else if (text && StaticAddress(part) is string label) LoadAddress("eax", label);
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
                // str s: f; with f a float or 64-bit number writes it out as text
                if (v.Type.IsString && (values[0].Type.IsFloat || values[0].Type.IsWide))
                {
                    var build = new StringBuildStmt { Target = NameOf(v) };
                    build.Parts.Add(values[0]);
                    EmitStatement(build);
                    return;
                }
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
                else if (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar)
                {
                    string print = "phi_print" + suffix;
                    Need(print);
                    EmitNumberText(value);
                    Emit("mov esi, eax");
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
            NameExpr { Symbol: VariableSymbol { Type.IsString: true, Grows: false, TextRef: false } v } => v.Label,
            _ => null,
        };

        void EmitCall(CallStmt call)
        {
            EmitCallTo(call.Resolved, call.Arguments);
            PhiType result = call.Resolved switch
            {
                MethodSymbol method => method.ReturnType,
                PointerCall pointer => pointer.Signature.Result,
                BuiltinFunction { FloatInstruction: not null } => PhiType.F64,
                _ => PhiType.Int,
            };
            if (call.ResultTarget == null)
            {
                if (result.IsFloat) Emit("fstp st0               ; the result isn't kept");
                return;
            }

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
                case MethodSymbol small when CanInline(small):
                    EmitInline(small, arguments);
                    break;

                case MethodSymbol method:
                {
                    // push each argument in order (a missing one is the parameter's default, or zero);
                    // a str parameter gets the address of the text, and a 64-bit one takes two slots
                    int count = method.Parameters.Count;
                    int pushedBytes = 0;
                    var writeBacks = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        VariableSymbol p = method.Parameters[i];
                        Expr? value = i < arguments.Count ? arguments[i] : p.Decl.Values.FirstOrDefault();

                        if (p.Type.IsFloat || p.Type.IsWide)
                        {
                            pushedBytes += PushWideOrFloat(p.Type, value);
                            continue;
                        }
                        if (p.Type.Open)
                        {
                            pushedBytes += PushArrayView(value);
                            continue;
                        }
                        if (p.TextRef)
                        {
                            PushTextRef(value!, writeBacks);
                            pushedBytes += 4;
                            continue;
                        }
                        if (bits32 && !p.Type.IsString && value != null && DirectOperand(value, constantOnly: false) is string asIs)
                        {
                            // a constant or a 32-bit variable is pushed as it is
                            Emit(asIs.Contains('[') || asIs.Contains('‹') ? $"push {asIs}" : $"push dword {asIs}");
                            pushedBytes += 4;
                            continue;
                        }
                        if (!p.Type.IsString && value != null && PlaceOperand(value))
                        {
                            Emit($"push dword {Operand(PlaceKeepingEax(value))}");
                            pushedBytes += 4;
                            continue;
                        }
                        if (value == null) Emit(p.Type.IsString ? $"mov eax, {StringLabel("")}" : "xor eax, eax");
                        else if (p.Type.IsString && (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar)) EmitNumberText(value);
                        else EmitExpr(value);

                        if (p.Type.IsString && value != null && !IsText(value.Type) && !value.Type.IsWide && !value.Type.IsFloat && !value.Type.IsChar)
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
                        pushedBytes += 4;
                    }

                    if (method.ReturnType.IsString)
                    {
                        // somewhere for the method to put its result: a slot that grows to fit
                        // (the text and its room, freed when this method ends), or a buffer
                        if (GrowResults)
                        {
                            string slot = AllocTemp(8);
                            if (currentMethod != null) resultSlots.Add(slot);
                            LoadAddress("eax", slot);
                        }
                        else LoadAddress("eax", AllocTemp(method.ResultCapacity));
                        Emit("push eax");
                        pushedBytes += 4;
                    }

                    Emit($"call {method.Label}");
                    if (pushedBytes > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {pushedBytes}");
                    foreach (string slots in writeBacks)
                    {
                        // an element of a str array that grows: where its text went, and its room
                        Emit("push eax");
                        Emit($"mov esi, [{slots} + 8]");
                        Emit($"mov eax, [{slots}]");
                        Emit("mov [esi], eax");
                        Emit($"mov esi, [{slots} + 12]");
                        Emit($"mov eax, [{slots} + 4]");
                        Emit("mov [esi], eax");
                        Emit("pop eax");
                    }
                    break;
                }

                case BuiltinFunction { FloatInstruction: not null } function:
                    EmitFloatFunction(function, arguments[0]);
                    break;

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
                    int pushed = 0;
                    for (int i = 0; i < types.Count; i++)
                    {
                        Expr value = arguments[i];
                        if (types[i].IsFloat || types[i].IsWide)
                        {
                            pushed += PushWideOrFloat(types[i], value);
                            continue;
                        }
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
                        pushed += 4;
                    }
                    EmitExpr(pointer.Target);
                    Emit(bits32 ? "call eax" : "call ax");
                    if (pushed > 0) Emit($"add {(bits32 ? "esp" : "sp")}, {pushed}");
                    break;
                }
            }
        }

        // ================================================================ indexes known to be in range

        /// <summary>
        /// The counters of the whiles around the code being generated, each with what it's known
        /// to stay below in its loop's body: a number of elements, or an array parameter's .len.
        /// </summary>
        readonly List<(VariableSymbol Counter, object Below)> inRange = new();

        /// <summary>The method's variables whose address is taken (anything could change them).</summary>
        readonly HashSet<VariableSymbol> addressTaken = new();

        /// <summary>
        /// What a while's counter stays below in its body, when the loop proves it: the test is
        /// i &lt; n (or i &lt;= n - 1), n is a constant or an array parameter's .len, nothing in the
        /// body changes i, and i can't be below zero (it's unsigned, or a signed i starts at zero
        /// or more and the step only adds to it).
        /// </summary>
        (VariableSymbol, object)? InRangeIn(WhileStmt loop)
        {
            if (loop.Condition is not BinaryExpr { Op: BinaryOp.Less or BinaryOp.LessEqual } cmp) return null;
            if (cmp.Left is not NameExpr { Call: null, Symbol: VariableSymbol { IsLocal: true } i }) return null;
            if (!i.Type.IsNumeric || i.Type.IsPointer || StorageType(i.Type).ElementSize != 4 || i.Decl.Volatile || addressTaken.Contains(i)) return null;

            object? below = null;
            long? known = ConstantFolder.Evaluate(cmp.Right)
                ?? cmp.Right switch
                {
                    NameExpr { Symbol: LengthSymbol { Count: int count } } => count,
                    MemberExpr { Count: int count } => count,
                    _ => null,
                };
            if (known is long n) below = cmp.Op == BinaryOp.Less ? n : n + 1;
            else if (cmp.Op == BinaryOp.Less && cmp.Right is NameExpr { Symbol: LengthSymbol { Open: VariableSymbol open, Max: false } }) below = open;
            if (below == null) return null;

            // nothing in the body sets it
            bool sets = loop.Body.SelectMany(SyntaxWalker.Nodes).Any(n => n switch
            {
                AssignStmt a => Names(a.Target, i),
                CallStmt { ResultTarget: Expr target } => Names(target, i),
                AskStmt a => Names(a.Target, i),
                StringBuildStmt b => Names(b.Target, i),
                VarDecl d => d.Symbol == i,
                _ => false,
            });
            if (sets) return null;

            // an array parameter the body only indexes (adding to it, or passing it on, could
            // change how many it has)
            if (below is VariableSymbol array)
            {
                var nodes = loop.Body.SelectMany(SyntaxWalker.Nodes).ToList();
                int named = nodes.Count(n => n is NameExpr { Symbol: VariableSymbol v } && v == array);
                int indexed = nodes.Count(n => n is IndexExpr { Target: NameExpr { Symbol: VariableSymbol v } } && v == array);
                if (named != indexed) return null;
            }

            bool unsigned = cmp.Left.Type.IsUnsigned32 || cmp.Right.Type.IsUnsigned32;
            if (!unsigned)
            {
                // a signed counter: from zero or more, only going up
                bool startsAtZeroOrMore = loop.Init?.Symbol == i
                    && (loop.Init.Values.Count == 0 || ConstantFolder.Evaluate(loop.Init.Values[0]) is >= 0);
                bool goesUp = loop.Step is AssignStmt { Value: BinaryExpr { Op: BinaryOp.Add } step } stepAssign
                    && Names(stepAssign.Target, i) && Names(step.Left, i) && ConstantFolder.Evaluate(step.Right) is >= 0;
                if (!startsAtZeroOrMore || !goesUp) return null;
            }
            return (i, below);
        }

        static bool Names(Expr e, VariableSymbol v) => e is NameExpr { Call: null, Symbol: VariableSymbol named } && named == v;

        /// <summary>True when the element's index is a counter its loop keeps below the array's length.</summary>
        bool ProvedInRange(IndexExpr ix)
        {
            if (ix.Index is not NameExpr { Call: null, Symbol: VariableSymbol i }) return false;
            for (int k = inRange.Count - 1; k >= 0; k--)
            {
                if (inRange[k].Counter != i) continue;
                return inRange[k].Below switch
                {
                    long n => ix.CheckLimit is int limit && n <= limit,
                    VariableSymbol open => ix.Target is NameExpr { Symbol: VariableSymbol same } && same == open,
                    _ => false,
                };
            }
            return false;
        }

        // ================================================================ atomic steps and locks

        /// <summary>
        /// The locked blocks around the code being generated, innermost last: where each keeps
        /// its lock's address and the flags from before it (so interrupts go back as they were),
        /// how many loops were around it, and how deep in inlining it is.
        /// </summary>
        readonly List<(string Slot, int Loops, int Inlining)> lockedBlocks = new();

        /// <summary>
        /// locked guard ... ;; takes the lock with xchg (spinning while another core has it,
        /// with interrupts back on), runs the body with this core's interrupts off, and gives it
        /// back: at the end, and on a return, break or continue that leaves the block.
        /// </summary>
        void EmitLocked(LockedStmt locked)
        {
            string slot = AllocTemp(8);
            string spin = NewLabel("lock"), wait = NewLabel("lockwait"), held = NewLabel("locked");
            EmitPlaceAddress(locked.Lock);
            Emit($"mov [{slot}], eax          ; the lock's address");
            Emit("pushfd");
            Emit($"pop dword [{slot} + 4]      ; interrupts as they were");
            Label(spin);
            Emit("cli");
            Emit("mov ecx, 1");
            Emit("xchg [eax], ecx           ; take it if it's free (0)");
            Emit("test ecx, ecx");
            Emit($"jz {held}");
            Emit($"push dword [{slot} + 4]");
            Emit("popfd                    ; another core has it: interrupts as they were while waiting");
            Label(wait);
            Emit("pause");
            Emit("cmp dword [eax], 0");
            Emit($"jne {wait}");
            Emit($"jmp {spin}");
            Label(held);

            lockedBlocks.Add((slot, loops.Count, inlining.Count));
            foreach (Stmt s in locked.Body) EmitStatement(s);
            lockedBlocks.RemoveAt(lockedBlocks.Count - 1);
            EmitUnlock(slot);
        }

        /// <summary>Gives a lock back (through ecx, so a result in eax or edx:eax stays).</summary>
        void EmitUnlock(string slot)
        {
            Emit($"mov ecx, [{slot}]");
            Emit("mov dword [ecx], 0        ; the lock back");
            Emit($"push dword [{slot} + 4]");
            Emit("popfd                    ; and interrupts as they were");
        }

        /// <summary>
        /// Gives back the locks a jump leaves, innermost first: the blocks inside the innermost
        /// loop for a break or continue (loops is how many loops there are), or every block of
        /// this method (or of the method being inlined) for a return (-1).
        /// </summary>
        void UnlockLeaving(int loopCount)
        {
            for (int i = lockedBlocks.Count - 1; i >= 0; i--)
            {
                var (slot, around, level) = lockedBlocks[i];
                if (level != inlining.Count) break;
                if (loopCount >= 0 && around < loopCount) break;
                EmitUnlock(slot);
            }
        }

        static string SizedRegister(string register, int bytes) => bytes switch
        {
            1 => register[1] + "l",
            2 => register[1..],
            _ => register,
        };

        /// <summary>atomic x++ n; atomic x-- n; atomic x is v; atomic x is v when old;</summary>
        void EmitAtomic(AtomicStmt atomic)
        {
            if (atomic.Op == AtomicOp.CompareSwap)
            {
                EmitCompareSwap(atomic.Target, atomic.Value, atomic.Expected!);
                return;
            }
            int bytes = StorageType(atomic.Target.Type).ElementSize;
            string size = bytes switch { 1 => "byte", 2 => "word", _ => "dword" };
            EmitExpr(atomic.Value);
            Emit("push eax");
            Place p = EmitPlace(atomic.Target);
            Emit("pop eax");
            string value = SizedRegister("eax", bytes);
            Emit(atomic.Op switch
            {
                AtomicOp.Add => $"lock add {size} {Operand(p)}, {value}",
                AtomicOp.Subtract => $"lock sub {size} {Operand(p)}, {value}",
                _ => $"xchg {size} {Operand(p)}, {value}            ; (an exchange with memory is always locked)",
            });
        }

        /// <summary>eax = 1 if target was expected and is value now (lock cmpxchg), else 0.</summary>
        void EmitCompareSwap(Expr target, Expr value, Expr expected)
        {
            int bytes = StorageType(target.Type).ElementSize;
            string size = bytes switch { 1 => "byte", 2 => "word", _ => "dword" };
            EmitExpr(value);
            Emit("push eax");
            EmitExpr(expected);
            Emit("push eax");
            Place p = EmitPlace(target);
            Emit("pop eax                  ; what it should be now");
            Emit("pop ecx                  ; and what it becomes");
            Emit($"lock cmpxchg {size} {Operand(p)}, {SizedRegister("ecx", bytes)}");
            Emit("sete al");
            Emit("movzx eax, al");
        }

        // ================================================================ inlining

        /// <summary>The methods being inlined, innermost last.</summary>
        readonly List<MethodSymbol> inlining = new();

        /// <summary>Whether each method is small and simple enough to inline (worked out once).</summary>
        readonly Dictionary<MethodSymbol, bool> inlinable = new();

        /// <summary>The most syntax nodes a method may have to be inlined.</summary>
        const int InlineNodes = 40;

        /// <summary>
        /// True when a call to the method is written out in place instead (32-bit code): a small
        /// method with no loops, whose parameters, variables and result are each one register,
        /// and whose every statement runs with the caller's variables in registers (so none needs
        /// a fence). Two levels at most, and never a method into itself.
        /// </summary>
        bool CanInline(MethodSymbol m)
        {
            if (!bits32 || m == currentMethod || inlining.Contains(m) || inlining.Count >= 2) return false;
            if (!inlinable.TryGetValue(m, out bool can))
            {
                inlinable[m] = false;   // (a method that reaches itself isn't inlined)
                can = Inlinable(m);
                inlinable[m] = can;
            }
            return can;
        }

        bool Inlinable(MethodSymbol m)
        {
            if (m.IsHook || m.IsInterruptHandler) return false;
            if (!(m.ReturnType.Kind == TypeKind.Void || OneRegister(m.ReturnType))) return false;
            if (!m.Parameters.All(p => OneRegister(p.Type) && !p.TextRef && p.Decl.AtAddress == null)) return false;
            if (!m.Locals.All(v => OneRegister(v.Type) && v.Ownership == Ownership.None && v.Decl.AtAddress == null && !v.Decl.IsNew)) return false;

            var nodes = m.Decl.Body.SelectMany(SyntaxWalker.Nodes)
                .Concat(m.Decl.Result != null ? SyntaxWalker.Nodes(m.Decl.Result) : Enumerable.Empty<Node>()).ToList();
            if (nodes.Count > InlineNodes) return false;
            // its variables live in the caller's frame, so nothing may take their address
            if (nodes.Any(n => n is AddrExpr { Operand: NameExpr { Symbol: VariableSymbol { IsLocal: true } } })) return false;

            PhiType outer = resultType;
            resultType = m.ReturnType;
            bool safe = InlineSafe(m.Decl.Body) && (m.Decl.Result == null || OneRegister(m.Decl.Result.Type) && SafeValue(m.Decl.Result));
            resultType = outer;
            return safe;
        }

        bool InlineSafe(List<Stmt> body) => body.All(s => s switch
        {
            IfStmt i => SafeValue(i.Condition) && InlineSafe(i.Then) && (i.Else == null || InlineSafe(i.Else)),
            UnsafeStmt u => InlineSafe(u.Body),
            WhileStmt or WhileEachStmt or WhenStmt or BreakStmt or ContinueStmt => false,
            _ => SafeStatement(s),
        });

        /// <summary>
        /// A call written out in place, its result in eax. The method's parameters and variables
        /// get room in this frame for the call (each call its own, so a call in an argument can't
        /// change another's); a parameter the method never changes, given a variable that's in a
        /// register, is that register.
        /// </summary>
        void EmitInline(MethodSymbol m, List<Expr> arguments)
        {
            var labels = m.Parameters.Concat(m.Locals).Select(v => (Variable: v, Label: v.Label)).ToList();
            var changed = m.Decl.Body.SelectMany(SyntaxWalker.Nodes).SelectMany(n => n switch
            {
                AssignStmt a => new[] { a.Target },
                CallStmt { ResultTarget: Expr target } => new[] { target },
                _ => Array.Empty<Expr>(),
            }).OfType<NameExpr>().Select(n => n.Symbol).OfType<VariableSymbol>().ToHashSet();

            // the arguments first, all of them, before any parameter's place changes
            var places = new List<(VariableSymbol Variable, string Label, string? Register)>();
            Comment($"{m.Name}, written out here");
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                VariableSymbol p = m.Parameters[i];
                Expr? value = i < arguments.Count ? arguments[i] : p.Decl.Values.FirstOrDefault();

                if (value is NameExpr { Symbol: VariableSymbol given } && !changed.Contains(p)
                    && StorageType(given.Type).ElementSize == 4 && StorageType(p.Type).ElementSize == 4
                    && inRegisters.FirstOrDefault(r => r.Variable == given) is { Variable: not null } held && registersLive)
                {
                    places.Add((p, held.Home, held.Register));
                    continue;
                }

                string slot = AllocTemp(4);
                if (value == null) Emit($"mov dword [{slot}], 0");
                else if (DirectOperand(value, constantOnly: false) is string asIs && !asIs.Contains('['))
                    Emit($"mov dword [{slot}], {asIs}");
                else
                {
                    EmitExpr(value);
                    Emit($"mov dword [{slot}], eax");
                }
                places.Add((p, slot, null));
            }
            foreach (VariableSymbol v in m.Locals)
            {
                string slot = AllocTemp(4);
                if (!v.Decl.Values.Any() && !v.NeedsInitCode) Emit($"mov dword [{slot}], 0");   // starts at zero, as it would
                places.Add((v, slot, null));
            }

            // the method's own code, in its own file, returning to the end of it
            var outer = (file, returnLabel, returnUsed, resultType, lastComment);
            int aliases = 0;
            foreach (var (v, label, register) in places)
            {
                if (register == null)
                {
                    v.Label = label;
                    continue;
                }
                inRegisters.Add((v, register, label));
                v.Label = $"«{v.Name}»";
                aliases++;
            }
            inlining.Add(m);
            file = m.Decl.Owner.File;
            returnLabel = NewLabel("inlined");
            returnUsed = false;
            resultType = m.ReturnType;

            foreach (Stmt s in m.Decl.Body) EmitStatement(s);
            if (m.Decl.Result != null)
            {
                SourceComment(m.Decl.Result);
                EmitExpr(m.Decl.Result);
            }
            if (returnUsed) Label(returnLabel);

            inlining.RemoveAt(inlining.Count - 1);
            inRegisters.RemoveRange(inRegisters.Count - aliases, aliases);
            foreach (var (v, label) in labels) v.Label = label;
            (file, returnLabel, returnUsed, resultType, _) = outer;
            lastComment = default;  // the caller's next line gets its comment again
        }

        /// <summary>
        /// Pushes the address of a str for a str parameter with no default (str into;): two u32s,
        /// where its text is and its room. A str that grows passes its own (so the method can make
        /// it bigger); an element of a str array that grows passes a copy that's written back after
        /// the call; text with a fixed size passes its room with the Fixed bit; and anything else
        /// (a literal, a ptr&lt;u8&gt;, a number), which the method only reads, passes no room.
        /// </summary>
        void PushTextRef(Expr value, List<string> writeBacks)
        {
            switch (value)
            {
                case NameExpr { Symbol: VariableSymbol { Grows: true } v }:
                    LoadAddress("eax", v.Label);
                    Emit("push eax");
                    return;

                case NameExpr { Symbol: VariableSymbol { TextRef: true } v }:
                    Emit($"push dword [{v.Label}]   ; the same str this method was given");
                    return;

                case Expr { Type.IsHeapText: true }:
                    EmitPlaceAddress(value);    // its slot: the text and its room, so it can grow
                    Emit("push eax");
                    return;

                case IndexExpr { Target: NameExpr { Symbol: VariableSymbol array } } when array.GrowingElements || array.Type is { Open: true, TextTable: true }:
                {
                    string slots = AllocTemp(16);
                    EmitPlaceAddress(value);
                    Emit($"mov [{slots} + 8], eax   ; the element's text slot");
                    Emit("mov esi, eax");
                    Emit("mov ecx, [esi]");
                    Emit($"mov [{slots}], ecx");
                    if (array.GrowingElements) Emit($"lea esi, [esi + {4 * array.Count}]");
                    else
                    {
                        Emit($"mov ecx, [{array.Label} + 8]");
                        Emit("lea esi, [esi + ecx*4]");
                    }
                    Emit("mov ecx, [esi]");
                    Emit($"mov [{slots} + 4], ecx");
                    Emit($"mov [{slots} + 12], esi   ; and its room");
                    LoadAddress("eax", slots);
                    Emit("push eax");
                    writeBacks.Add(slots);
                    return;
                }
            }

            int room = 0;
            if (value.Type.IsInlineText) room = value.Type.Room;
            else if (value is NameExpr { Symbol: VariableSymbol { Type.IsString: true, Type.IsArray: false } fixedStr }) room = fixedStr.Capacity;
            else if (value is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { Type.IsArray: true } table } } && value.Type.IsString) room = table.Capacity;

            string slot = AllocTemp(8);
            if (IsText(value.Type)) EmitExpr(value);
            else
            {
                // a number, written out as text first
                if (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar) EmitNumberText(value);
                else
                {
                    EmitExpr(value);
                    string text = AllocTemp(12);
                    Need("phi_int_to_str");
                    LoadAddress("edi", text);
                    Emit("mov ecx, 12");
                    Emit("call phi_int_to_str");
                    LoadAddress("eax", text);
                }
            }
            Emit($"mov [{slot}], eax");
            if (room > 0) Emit($"mov dword [{slot} + 4], {room} | {Fixed}");
            else
            {
                // text the method only reads: its room is what it has, so .max is its length
                Need("phi_strlen");
                Emit("mov esi, eax");
                Emit("call phi_strlen");
                Emit("inc eax");
                Emit($"or eax, {Fixed}");
                Emit($"mov [{slot} + 4], eax");
            }
            LoadAddress("eax", slot);
            Emit("push eax");
        }

        /// <summary>The bytes a parameter takes on the stack.</summary>
        static int ParameterBytes(PhiType t) => t.Open ? 12 : t.ElementSize == 8 ? 8 : 4;

        /// <summary>
        /// Pushes an array for an array parameter: the most it holds, how many it has, and where its
        /// elements are (so they're at [slot], [slot + 4] and [slot + 8] in the method).
        /// </summary>
        int PushArrayView(Expr? array)
        {
            if (array == null)
            {
                Emit("push dword 0");
                Emit("push dword 0");
                Emit("push dword 0            ; no array: none");
                return 12;
            }
            PhiType t = array.Type;
            if (t.Open)
            {
                string label = ((VariableSymbol)((NameExpr)array).Symbol!).Label;
                Emit($"push dword [{label} + 8]");
                Emit($"push dword [{label} + 4]");
                Emit($"push dword [{label}]");
                return 12;
            }
            Emit($"push dword {t.Length}     ; the most it holds");
            EmitArrayCount(array);
            Emit("push eax                ; how many it has");
            EmitPlaceAddress(array);
            Emit("push eax                ; its elements");
            return 12;
        }

        /// <summary>
        /// Pushes an argument for a 64-bit or floating point parameter (0 when there's no value):
        /// an i64 or f64 takes 8 bytes, the others 4 (an f16 in the low half). Gives the bytes pushed.
        /// </summary>
        int PushWideOrFloat(PhiType type, Expr? value)
        {
            if (type.IsWide)
            {
                if (value == null)
                {
                    Emit("xor eax, eax");
                    Emit("xor edx, edx");
                }
                else EmitWide(value);
                Emit("push edx");
                Emit("push eax");
                return 8;
            }

            if (value == null) Emit("fldz");
            else EmitFloat(value);
            if (type.Kind == TypeKind.F64)
            {
                Emit("sub esp, 8");
                Emit("fstp qword [esp]");
                return 8;
            }
            Emit("sub esp, 4");
            Emit("fstp dword [esp]");
            if (type.Kind == TypeKind.F16)
            {
                Need("phi_f32_to_f16");
                Emit("mov eax, [esp]");
                Emit("call phi_f32_to_f16");
                Emit("mov [esp], eax");
            }
            return 4;
        }

        /// <summary>Calls a one-parameter heap method (Heap.Alloc or Heap.Free) with a value.</summary>
        void EmitHeapCall(MethodSymbol method, Expr argument) =>
            EmitCallTo(method, new List<Expr> { argument });

        /// <summary>Stores a call result (in eax) into a variable.</summary>
        void EmitStoreResult(Expr target, PhiType resultType)
        {
            if (target.Type.IsFloat)
            {
                if (resultType.IsWide) PushAndLoad("qword", "edx", "eax");
                else if (!resultType.IsFloat) PushAndLoad("dword", "eax");
                EmitFloatStore(target);
                return;
            }
            if (target.Type.IsWide)
            {
                if (!resultType.IsWide) Emit(resultType.IsSigned ? "cdq" : "xor edx, edx");
                EmitWideStore(target);
                return;
            }
            if (target.Type.IsString) EmitStringStore(target, sourceIsString: IsText(resultType));
            else EmitStoreEax(target);
        }

        // ================================================================ memory places

        /// <summary>
        /// Where a value lives. Either a fixed address (Label + Offset), or Label + ebx + Offset
        /// when part of the address is computed, or, for memory reached through a pointer, a
        /// linear address in ebx (+ Offset) that may be anywhere in the first megabyte.
        ///
        /// With variables in registers (32-bit code) a computed place can be reached through them
        /// as they are: Base instead of ebx for the address, and Index times Scale added to it
        /// instead of ebx, so no code is needed to work the address out.
        /// </summary>
        sealed class Place
        {
            public string? Label;
            public int Offset;
            public bool Computed;   // ebx holds part of the address
            public bool Linear;     // ebx holds a linear address, not an offset from Label
            public string? Register; // a variable kept in a register: the value is there, not in memory
            public string Base = "ebx";
            public string? Index;
            public int Scale = 1;

            public Place Plus(int bytes) => new()
            {
                Label = Label, Offset = Offset + bytes, Computed = Computed, Linear = Linear,
                Register = Register, Base = Base, Index = Index, Scale = Scale,
            };

            /// <summary>What's added for the computed part: ebx, or a register variable times its scale.</summary>
            public string Added => Index == null ? "ebx" : Scale == 1 ? Index : $"{Index}*{Scale}";
        }

        Place EmitPlace(Expr e)
        {
            switch (e)
            {
                case NameExpr { Symbol: VariableSymbol v }:
                    return RegisterOf(v) is string register ? new Place { Register = register } : new Place { Label = v.Label };

                case NameExpr { Symbol: FieldPathSymbol f }:
                    return new Place { Label = f.Root.Label, Offset = f.Offset };

                case NameExpr { Symbol: SelfFieldSymbol s }:
                    // through the struct the method was called on
                    Emit($"mov ebx, {VariableOperand(s.Self)}");
                    return new Place { Computed = true, Linear = true, Offset = s.Offset };

                case NameExpr { Symbol: PathSymbol path }:
                {
                    // through objects: read the address each one is at, then go on from there (a
                    // pointer in a register is the first address already)
                    if (RegisterOf(path.Root) is string rootRegister && path.BaseOffset == 0)
                    {
                        if (path.Hops.Count == 1) return new Place { Computed = true, Linear = true, Base = rootRegister, Offset = path.Hops[0] };
                        Emit($"mov ebx, {rootRegister}");
                    }
                    else Emit($"mov ebx, dword {Operand(new Place { Label = path.Root.Label, Offset = path.BaseOffset })}");
                    for (int i = 0; i < path.Hops.Count - 1; i++)
                        Emit($"mov ebx, dword {Operand(new Place { Computed = true, Linear = true, Offset = path.Hops[i] })}");
                    return new Place { Computed = true, Linear = true, Offset = path.Hops[^1] };
                }

                case MemberExpr m when m.Target.Type.IsObject || m.Target.Type.IsPointer:
                    // where the object (or the struct a pointer points at) is
                    if (HeldIn(m.Target) is string held)
                        return new Place { Computed = true, Linear = true, Base = held, Offset = m.Field!.Offset };
                    if (bits32 && TryLoadSimple(m.Target, "ebx", dryRun: true)) TryLoadSimple(m.Target, "ebx", dryRun: false);
                    else
                    {
                        EmitExpr(m.Target);
                        Emit("mov ebx, eax");
                    }
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

            // an element of an array parameter: checked against how many it has, when it runs
            if (ix.Target is NameExpr { Symbol: VariableSymbol { Type.Open: true } open })
            {
                bool proved = ProvedInRange(ix);
                if ((proved || unsafeDepth > 0) && size is 1 or 2 or 4 or 8 && HeldIn(ix.Index) is string heldIndex)
                {
                    // known to be in range, with the index in a register: [elements + i*size]
                    Emit($"mov ebx, [{open.Label}]");
                    return new Place { Computed = true, Linear = true, Index = heldIndex, Scale = size };
                }
                EmitExpr(ix.Index);
                if (unsafeDepth == 0 && !proved) EmitIndexCheck(ix, "jb", $"dword [{open.Label} + 4]");
                EmitScale(size);
                Emit($"mov ebx, [{open.Label}]");
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of an element of a str array that grows: checked against its room
            if (ix.Target is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { GrowingElements: true } array } } element)
            {
                EmitExpr(ix.Index);
                Emit("push eax");
                EmitPlaceAddress(element);
                Emit("mov ebx, eax");
                Emit("pop eax");
                if (unsafeDepth == 0)
                {
                    Emit($"mov edx, [ebx + {4 * array.Count}]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit("mov ebx, [ebx]");
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of a new str field: checked against its room
            if (ix.Target.Type.IsHeapText)
            {
                EmitExpr(ix.Index);
                Emit("push eax");
                EmitPlaceAddress(ix.Target);
                Emit("mov ebx, eax");
                Emit("pop eax");
                if (unsafeDepth == 0)
                {
                    Emit("mov edx, [ebx + 4]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit("mov ebx, [ebx]");
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of the caller's str: checked against its room
            if (ix.Target is NameExpr { Symbol: VariableSymbol { TextRef: true } caller })
            {
                EmitExpr(ix.Index);
                Emit($"mov ebx, [{caller.Label}]   ; where {caller.Name}'s text and room are");
                if (unsafeDepth == 0)
                {
                    Emit("mov edx, [ebx + 4]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit("mov ebx, [ebx]");
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            // the characters of a str that grows: checked against its room, which can change
            if (ix.Target is NameExpr { Symbol: VariableSymbol { Grows: true } text })
            {
                EmitExpr(ix.Index);
                if (unsafeDepth == 0)
                {
                    Emit($"mov edx, [{text.Label} + 4]");
                    Emit($"and edx, {RoomMask}      ; its room");
                    EmitIndexCheck(ix, "jb", "edx");
                }
                Emit($"mov ebx, dword [{text.Label}]");
                Emit("add ebx, eax");
                return new Place { Computed = true, Linear = true };
            }

            // through a pointer, or the characters of a str that isn't a plain variable
            bool viaValue = target.IsPointer || (target.IsString && ix.Target is not NameExpr { Symbol: VariableSymbol });

            // p:i with the index in a register variable (or a constant), and the pointer in one or in
            // memory: [p + i*size] as it is, with the pointer loaded into ebx at most
            if (PointerElementKeepsEax(ix))
            {
                string address = HeldIn(ix.Target) ?? "ebx";
                if (address == "ebx") TryLoadSimple(ix.Target, "ebx", dryRun: false);
                return constant != null
                    ? new Place { Computed = true, Linear = true, Base = address, Offset = (int)constant.Value * size }
                    : new Place { Computed = true, Linear = true, Base = address, Index = HeldIn(ix.Index), Scale = size };
            }

            // the same with a pointer that has to be worked out (map:0.keys:place)
            if (bits32 && target.IsPointer && IndexAsIs(ix))
            {
                // a pointer kept in a field or an element is loaded straight into ebx
                if (PlaceKeepsEax(ix.Target)) Emit($"mov ebx, dword {Operand(PlaceKeepingEax(ix.Target))}");
                else
                {
                    EmitExpr(ix.Target);
                    Emit("mov ebx, eax");
                }
                return constant != null
                    ? new Place { Computed = true, Linear = true, Offset = (int)constant.Value * size }
                    : new Place { Computed = true, Linear = true, Index = HeldIn(ix.Index), Scale = size };
            }

            // the pointer in a register variable, and an index worked out: one lea
            if (target.IsPointer && constant == null && size is 1 or 2 or 4 or 8 && HeldIn(ix.Target) is string pointer)
            {
                EmitExpr(ix.Index);
                Emit($"lea ebx, [{pointer} + eax*{size}]");
                return new Place { Computed = true, Linear = true };
            }

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

            // the index in a register variable: [array + i*size], once it's checked
            if (!p.Computed && size is 1 or 2 or 4 or 8 && HeldIn(ix.Index) is string index)
            {
                if (ix.CheckLimit is int checkedLimit && unsafeDepth == 0 && !ProvedInRange(ix)) EmitIndexCheck(ix, "jb", checkedLimit.ToString(), index);
                p.Computed = true;
                p.Index = index;
                p.Scale = size;
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

            if (ix.CheckLimit is int limit && unsafeDepth == 0 && !ProvedInRange(ix))
                EmitIndexCheck(ix, "jb", limit.ToString());

            EmitScale(size);
        }

        /// <summary>
        /// when x: x is worked out once and kept in a temporary; each value is compared with it in
        /// order, and the first that's equal picks its branch.
        /// </summary>
        void EmitWhen(WhenStmt when)
        {
            PhiType t = when.Subject.Type;
            bool text = !t.IsNumeric && !t.IsWide && !t.IsFloat || t.IsPointer && t.Pointee == PhiType.U8;
            string subject = AllocTemp(t.IsWide || t.IsFloat ? 8 : 4);
            string end = NewLabel("endwhen");
            string otherwise = when.Else != null ? NewLabel("whenelse") : end;
            var bodies = when.Branches.Select(_ => NewLabel("whenis")).ToList();

            if (t.IsFloat)
            {
                EmitFloat(when.Subject);
                Emit($"fstp qword [{subject}]");
            }
            else if (t.IsWide)
            {
                EmitWide(when.Subject);
                Emit($"mov dword [{subject}], eax");
                Emit($"mov dword [{subject} + 4], edx");
            }
            else
            {
                EmitExpr(when.Subject);
                Emit($"mov dword [{subject}], eax");
            }
            for (int i = 0; i < when.Branches.Count; i++)
            {
                foreach (Expr value in when.Branches[i].Values)
                {
                    if (t.IsFloat)
                    {
                        string differs = NewLabel("whennot");
                        EmitFloat(value);
                        Emit($"fld qword [{subject}]");
                        Emit("fcompp");
                        Emit("fnstsw ax");
                        Emit("sahf");
                        Emit($"jp {differs}              ; not a number is equal to nothing");
                        Emit($"je {bodies[i]}");
                        Label(differs);
                    }
                    else if (t.IsWide)
                    {
                        string differs = NewLabel("whennot");
                        EmitWide(value);
                        Emit($"cmp eax, [{subject}]");
                        Emit($"jne {differs}");
                        Emit($"cmp edx, [{subject} + 4]");
                        Emit($"je {bodies[i]}");
                        Label(differs);
                    }
                    else if (text)
                    {
                        Need("phi_strcmp");
                        EmitExpr(value);
                        Emit("mov edi, eax");
                        Emit($"mov esi, [{subject}]");
                        Emit("call phi_strcmp          ; eax = 0 when equal");
                        Emit("test eax, eax");
                        Emit($"jz {bodies[i]}");
                    }
                    else if (ConstantFolder.Evaluate(value) is long k)
                    {
                        Emit($"cmp dword [{subject}], {(int)k}");
                        Emit($"je {bodies[i]}");
                    }
                    else
                    {
                        EmitExpr(value);
                        Emit($"cmp eax, [{subject}]");
                        Emit($"je {bodies[i]}");
                    }
                }
            }
            Emit($"jmp {otherwise}");

            for (int i = 0; i < when.Branches.Count; i++)
            {
                Label(bodies[i]);
                foreach (Stmt s in when.Branches[i].Body) EmitStatement(s);
                Emit($"jmp {end}");
            }
            if (when.Else != null)
            {
                Label(otherwise);
                foreach (Stmt s in when.Else) EmitStatement(s);
            }
            Label(end);
        }

        /// <summary>Stops on an index error unless the index (in eax) passes the unsigned jump against limit.</summary>
        void EmitIndexCheck(Node at, string jump, string limit, string index = "eax")
        {
            Need("phi_index_error");
            string ok = NewLabel("inbounds");
            Emit($"cmp {index}, {limit}");
            Emit($"{jump} {ok}                ; unsigned, so negative indexes fail too");
            EmitIndexError(at);
            Label(ok);
        }

        /// <summary>Stops with an index error naming the line and file of a node.</summary>
        void EmitIndexError(Node at)
        {
            Emit($"mov eax, {LineOf(at)}   ; it stops here, so eax is free");
            Emit($"mov {(bits32 ? "esi" : "si")}, {StringLabel(System.IO.Path.GetFileName(file.Path))}   ; it stops here, so esi is free");
            Emit("jmp phi_index_error");
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

            if (element.IsObject)
            {
                ObjectLine(append);
                if (value != null)
                {
                    EmitObjectAddress(value);
                    Emit("push eax                ; what to copy");
                }
            }
            else if (element.IsWide)
            {
                if (value == null) Emit("xor eax, eax");
                if (value == null) Emit("xor edx, edx");
                else EmitWide(value);
                Emit("push edx");
                Emit("push eax");
            }
            else if (element.IsFloat)
            {
                if (value == null) Emit("fldz");
                else EmitFloat(value);   // stays in st0 until it's stored
            }
            else if (element.IsStruct && value != null)
            {
                EmitPlaceAddress(value);
                Emit("push eax                ; the struct to copy");
            }
            else if (element.IsString && value != null && (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar))
            {
                EmitNumberText(value);
                Emit("push eax                ; the number, as text");
            }
            else if (value != null)
            {
                EmitExpr(value);
                Emit("push eax");
            }
            if (append.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true } growing })
            {
                // room for one more (moving its elements when there isn't), then at its end
                ObjectLine(append);
                LoadAddress("eax", growing.Label);
                Emit("mov edx, [eax + 4]");
                Emit("inc edx");
                Emit($"mov ecx, {size}");
                EmitArrayGrowCall();
                Emit($"mov eax, [{growing.Label} + 4]");
                Emit($"inc dword [{growing.Label} + 4]");
                EmitScale(size);
                Emit($"mov ebx, [{growing.Label}]");
                Emit("add ebx, eax");
            }
            else
            {
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
            }

            if (element.IsObject)
            {
                // the slot is empty (dropped ones are emptied): a zeroed object, or a copy
                Emit("mov eax, ebx");
                if (value == null) Emit($"call {ObjectRoutine("renew", element.Struct!)}");
                else
                {
                    Emit("pop edx");
                    Emit($"call {ObjectRoutine("set", element.Struct!)}");
                }
            }
            else if (element.IsWide)
            {
                Emit("pop eax");
                Emit("pop edx");
                Emit("mov [ebx], eax");
                Emit("mov [ebx + 4], edx");
            }
            else if (element.IsFloat)
            {
                switch (element.Kind)
                {
                    case TypeKind.F64: Emit("fstp qword [ebx]"); break;
                    case TypeKind.F32: Emit("fstp dword [ebx]"); break;
                    default:
                        Need("phi_f32_to_f16");
                        Emit("sub esp, 4");
                        Emit("fstp dword [esp]");
                        Emit("pop eax");
                        Emit("call phi_f32_to_f16");
                        Emit("mov [ebx], ax");
                        break;
                }
            }
            else if (element.IsStruct && value != null)
            {
                Emit("pop esi");
                Emit("mov edi, ebx");
                Emit($"mov ecx, {size}");
                Emit("cld");
                Emit("rep movsb");
            }
            else if (element.IsString)
            {
                bool text = value != null && (IsText(value.Type) || value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar);
                if (element.IsInlineText)
                {
                    // the text goes where the element is, cut to fit
                    if (value == null) Emit("mov byte [ebx], 0        ; empty");
                    else
                    {
                        Emit("pop eax");
                        Emit("mov edi, ebx");
                        Emit($"mov ecx, {element.Room}");
                        if (text)
                        {
                            Need("phi_strcpy");
                            Emit("mov esi, eax");
                            Emit("call phi_strcpy");
                        }
                        else
                        {
                            string convert = value!.Type.IsUnsigned32 ? "phi_uint_to_str" : "phi_int_to_str";
                            Need(convert);
                            Emit($"call {convert}");
                        }
                    }
                }
                else if (value == null)
                {
                    // the element's text (a dropped one's, if it was used before) starts empty
                    Emit("mov eax, [ebx]");
                    Emit("mov byte [eax], 0        ; empty");
                }
                else
                {
                    Emit("push ebx                ; the element's slot");
                    EmitGrowStoreAt(4 * t.Length, sourceIsString: text, append: false, unsigned: value.Type.IsUnsigned32);
                }
            }
            else if (value != null)
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
            if (p.Register != null) return p.Register;
            string offset = p.Offset == 0 ? "" : p.Offset > 0 ? $" + {p.Offset}" : $" - {-p.Offset}";

            if (!p.Computed) return $"[{p.Label}{offset}]";
            if (!p.Linear) return $"[{p.Label} + {p.Added}{offset}]";

            if (bits32) return p.Index == null ? $"[{p.Base}{offset}]" : $"[{p.Base} + {p.Added}{offset}]"; // memory is flat

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
            if (target.Type.IsFloat)
            {
                EmitFloat(value);
                EmitFloatStore(target);
                return;
            }
            if (target.Type.IsWide)
            {
                EmitWide(value);
                EmitWideStore(target);
                return;
            }
            if (target.Type.IsString && (value.Type.IsWide || value.Type.IsFloat || value.Type.IsChar))
            {
                EmitNumberText(value);
                EmitStringStore(target, sourceIsString: true);
                return;
            }

            if (!target.Type.IsString)
            {
                // x++, x-- 5, x is x + y, a:i is a:i ^ t: change it where it is (the place is worked out
                // after the amount, so the amount mustn't call anything that could change it)
                if (value is BinaryExpr { Op: BinaryOp.Add or BinaryOp.Subtract or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor } step
                    && (ReferenceEquals(step.Left, target) || SameValue(step.Left, target)) && PlaceKeepsEax(target) && !HasCall(step.Right))
                {
                    int bytes = StorageType(target.Type).ElementSize;
                    string size = bytes switch { 1 => "byte", 2 => "word", _ => "dword" };
                    string op = step.Op switch
                    {
                        BinaryOp.Add => "add", BinaryOp.Subtract => "sub", BinaryOp.BitAnd => "and", BinaryOp.BitOr => "or", _ => "xor",
                    };
                    string? amount = ConstantFolder.Evaluate(step.Right) is long k ? ((int)k).ToString() : bytes == 4 ? HeldIn(step.Right) : null;
                    if (amount == null) EmitExpr(step.Right);
                    string operand = Operand(PlaceKeepingEax(target));
                    Emit($"{op} {size} {operand}, {amount ?? (bytes switch { 1 => "al", 2 => "ax", _ => "eax" })}");
                    return;
                }

                // into a register variable: straight there, when the value needs no other registers
                if (HeldIn(target) is string register && TryLoadSimple(value, register, dryRun: true))
                {
                    TryLoadSimple(value, register, dryRun: false);
                    return;
                }

                // a constant, or a register variable into a 32-bit place: stored as it is
                if (PlaceKeepsEax(target) && HeldIn(target) == null && DirectOperand(value, constantOnly: false) is string stored && !stored.Contains('['))
                {
                    int bytes = StorageType(target.Type).ElementSize;
                    bool constant = ConstantFolder.Evaluate(value) is long;
                    if (constant || bytes == 4)
                    {
                        long k = constant ? ConstantFolder.Evaluate(value)!.Value : 0;
                        string amount = !constant ? stored : bytes switch { 1 => (k & 0xFF).ToString(), 2 => (k & 0xFFFF).ToString(), _ => stored };
                        string size = bytes switch { 1 => "byte", 2 => "word", _ => "dword" };
                        Emit($"mov {size} {Operand(PlaceKeepingEax(target))}, {amount}");
                        return;
                    }
                }

                // an element or a field into a register variable: loaded straight there
                if (HeldIn(target) is string into && value is IndexExpr or MemberExpr { Call: null } && OneRegister(value.Type) && PlaceKeepsEax(value))
                {
                    Emit(LoadInstruction(value.Type, into, Operand(PlaceKeepingEax(value))));
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
            bool needsCode = !PlaceKeepsEax(target); // computing the address uses eax

            if (needsCode) Emit("push eax");
            Place p = needsCode ? EmitPlace(target) : PlaceKeepingEax(target);
            string operand = Operand(p);
            if (needsCode) Emit("pop eax");
            Emit(StoreInstruction(target.Type, operand));
        }

        /// <summary>The register a variable is in, for an expression that's only that variable.</summary>
        string? HeldIn(Expr e) => e is NameExpr { Symbol: VariableSymbol v } ? RegisterOf(v) : null;

        /// <summary>
        /// True when working out where the place is leaves eax alone: it's fixed, or it's reached
        /// through register variables, constants and at most one pointer loaded into ebx.
        /// </summary>
        bool PlaceKeepsEax(Expr e) => e switch
        {
            _ when IsStaticPlace(e) => true,
            NameExpr { Symbol: SelfFieldSymbol or PathSymbol } => true,
            MemberExpr m when m.Target.Type.IsObject || m.Target.Type.IsPointer =>
                HeldIn(m.Target) != null || bits32 && TryLoadSimple(m.Target, "ebx", dryRun: true),
            MemberExpr { Call: null } m when !m.Target.Type.IsObject && !m.Target.Type.IsPointer && m.Target is IndexExpr => PlaceKeepsEax(m.Target),
            IndexExpr ix when ix.Target.Type.IsPointer => PointerElementKeepsEax(ix),
            IndexExpr ix => HeldIn(ix.Index) != null && ElementSize(ix) is 1 or 2 or 4 or 8 && !ix.Target.Type.Filled && !ix.Target.Type.Open
                            && !ix.Target.Type.IsString && !ix.Target.Type.TextTable
                            && ix.Target is NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false, GrowingElements: false, GrowingArray: false } or FieldPathSymbol },
            _ => false,
        };

        /// <summary>A 32-bit element or field that's an instruction's operand as it is, its address in ebx at most.</summary>
        bool PlaceOperand(Expr e) =>
            bits32 && e is IndexExpr or MemberExpr { Call: null, Count: null, FilledLength: false, TextLength: false }
            && OneRegister(e.Type) && StorageType(e.Type).ElementSize == 4 && PlaceKeepsEax(e);

        /// <summary>p:i reached as [p + i*size], with i a register variable or a constant (see EmitIndexPlace).</summary>
        bool PointerElementKeepsEax(IndexExpr ix) =>
            bits32 && ix.Target.Type.IsPointer && IndexAsIs(ix)
            && (HeldIn(ix.Target) != null || TryLoadSimple(ix.Target, "ebx", dryRun: true));

        /// <summary>An index that's part of the address as it is: a constant, or a register variable times 1, 2, 4 or 8.</summary>
        bool IndexAsIs(IndexExpr ix) =>
            ConstantFolder.Evaluate(ix.Index) != null || HeldIn(ix.Index) != null && ElementSize(ix) is 1 or 2 or 4 or 8;

        static int ElementSize(IndexExpr ix) =>
            ix.Target.Type.IsString ? 1 : ix.Target.Type.IsPointer ? ix.Target.Type.Pointee!.ElementSize : ix.Target.Type.ElementSize;

        /// <summary>EmitPlace for a place that leaves eax alone, checking that it does.</summary>
        Place PlaceKeepingEax(Expr target)
        {
            int start = code.Length;
            Place p = EmitPlace(target);
            string emitted = code.ToString(start, code.Length - start);
            foreach (string line in emitted.Split('\n'))
                if (!line.Contains("it stops here") && System.Text.RegularExpressions.Regex.IsMatch(WithoutComment(line), @"\b(eax|ax|al|ah)\b"))
                    throw new InvalidOperationException($"working out a place changed eax: {line.Trim()}");
            return p;
        }

        /// <summary>True when two expressions are the same variable, field or element (so x is x + 1 can add in place).</summary>
        static bool SameValue(Expr a, Expr b) => (a, b) switch
        {
            (NameExpr { Call: null, Symbol: VariableSymbol x }, NameExpr { Call: null, Symbol: VariableSymbol y }) => x == y,
            (IndexExpr x, IndexExpr y) => x.Type == y.Type && SameValue(x.Target, y.Target) && SameValue(x.Index, y.Index),
            (MemberExpr { Call: null } x, MemberExpr { Call: null } y) => x.Field != null && x.Field == y.Field && SameValue(x.Target, y.Target),
            (NumberExpr x, NumberExpr y) => x.Value == y.Value,
            _ => false,
        };

        /// <summary>True when working out the expression may call something (which could change memory).</summary>
        static bool HasCall(Expr e) => SyntaxWalker.Nodes(e).Any(n => n is CallExpr or NewExpr or NameExpr { Call: not null } or MemberExpr { Call: not null });

        /// <summary>True when the place's address is fixed, so computing it emits no code.</summary>
        static bool IsStaticPlace(Expr e) => e switch
        {
            NameExpr { Symbol: VariableSymbol or FieldPathSymbol } => true,
            MemberExpr m => !m.Target.Type.IsObject && !m.Target.Type.IsPointer && IsStaticPlace(m.Target),
            IndexExpr ix => !ix.Target.Type.IsPointer && !ix.Target.Type.Filled && !ix.Target.Type.Open && ConstantFolder.Evaluate(ix.Index) != null && IsStaticPlace(ix.Target)
                            && !(ix.Target.Type.IsString && ix.Target is not NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false } }),
            _ => false,
        };

        /// <summary>eax = how many characters the text kept at a str field is.</summary>
        void EmitTextLength(Expr text)
        {
            EmitExpr(text);     // where its text is
            Need("phi_strlen");
            Emit("mov esi, eax");
            Emit("call phi_strlen");
        }

        /// <summary>
        /// eax holds a string address (sourceIsString) or a number to write out as text;
        /// put it in a str variable, a str field or a str array element, cut to fit.
        /// </summary>
        void EmitStringStore(Expr target, bool sourceIsString, bool append = false, bool unsigned = false)
        {
            if (target is NameExpr { Symbol: VariableSymbol { Grows: true } growing })
            {
                EmitGrowStore(growing, sourceIsString, append, unsigned);
                return;
            }

            if (target.Type.IsHeapText)
            {
                // a new str field: it grows like a str variable, through its slot
                Emit("push eax                ; the value");
                EmitPlaceAddress(target);
                Emit("push eax                ; the field's text slot (its room is after it)");
                EmitGrowStoreAt(4, sourceIsString, append, unsigned);
                return;
            }

            if (target is NameExpr { Symbol: VariableSymbol { TextRef: true } caller })
            {
                // into the caller's str: it grows if it can, or is cut to fit
                Emit("push eax                ; the value");
                Emit($"push dword [{caller.Label}]   ; where {caller.Name}'s text and room are");
                EmitGrowStoreAt(4, sourceIsString, append, unsigned);
                return;
            }

            if (target is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { Type: { Open: true, TextTable: true } } view } })
            {
                Emit("push eax                ; the value");
                EmitPlaceAddress(target);
                Emit("push eax                ; the element's slot");
                EmitGrowStoreAt(0, sourceIsString, append, unsigned, view.Label);
                return;
            }

            if (target is IndexExpr { Target: NameExpr { Symbol: VariableSymbol { GrowingElements: true } array } })
            {
                // the element's text slot, and its room after the table
                Emit("push eax                ; the value");
                EmitPlaceAddress(target);
                Emit("push eax                ; the element's slot");
                EmitGrowStoreAt(4 * array.Count, sourceIsString, append, unsigned);
                return;
            }

            int capacity;
            if (target.Type.IsInlineText)
            {
                // a str field: the text goes where the field is
                capacity = target.Type.Room;
                Emit("push eax");
                EmitPlaceAddress(target);
                Emit("mov edi, eax");
                Emit("pop eax");
            }
            else if (target is NameExpr { Symbol: VariableSymbol v })
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
            if (e.Type.IsWide)
            {
                EmitWide(e);    // the low half is in eax too
                return;
            }
            if (e.Type.IsFloat)
                throw new InvalidOperationException($"a floating point value where a whole number goes: {e.GetType().Name}");

            if (e is BinaryExpr or UnaryExpr && ConstantFolder.Evaluate(e) is long folded)
            {
                LoadConstant("eax", folded);
                return;
            }

            // text kept in place (a str field): its value is where it is
            if (e.Type.IsInlineText && e is NameExpr { Symbol: FieldPathSymbol or SelfFieldSymbol or PathSymbol } or MemberExpr { Call: null } or IndexExpr)
            {
                EmitPlaceAddress(e);
                return;
            }

            // a new str field: the text its slot points at ('' while it has none)
            if (e.Type.IsHeapText && e is NameExpr { Symbol: FieldPathSymbol or SelfFieldSymbol or PathSymbol } or MemberExpr { Call: null })
            {
                string done = NewLabel("text");
                EmitPlaceAddress(e);
                Emit("mov eax, [eax]");
                Emit("test eax, eax");
                Emit($"jnz {done}");
                Emit($"mov eax, {StringLabel("")}");
                Label(done);
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

                case NameExpr { Symbol: VariableSymbol { TextRef: true } v }:
                    LoadCallerText("eax", v);
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

                case NameExpr { Symbol: LengthSymbol { Open: VariableSymbol open } length }:
                    Emit($"mov eax, [{open.Label} + {(length.Max ? 8 : 4)}]");
                    break;

                case MemberExpr { FilledLength: true } m:
                    EmitFilledCount(m.Target);
                    break;

                case MemberExpr { TextLength: true } m:
                    EmitTextLength(m.Target);
                    break;

                case NameExpr { Symbol: LengthSymbol { Text: Expr text } }:
                    EmitTextLength(text);
                    break;

                case NameExpr { Symbol: LengthSymbol { TextRef: VariableSymbol caller } }:
                {
                    // the caller's room when its str has a size, or the most a str grows to
                    string grows = NewLabel("grows");
                    Emit($"mov eax, [{caller.Label}]");
                    Emit("mov eax, [eax + 4]");
                    Emit($"test eax, {Fixed}");
                    Emit($"mov eax, {Binder.StrMax}");
                    Emit($"jz {grows}");
                    Emit($"mov eax, [{caller.Label}]");
                    Emit("mov eax, [eax + 4]");
                    Emit($"and eax, {RoomMask}");
                    Emit("dec eax                 ; less the zero");
                    Label(grows);
                    break;
                }

                case NameExpr { Symbol: LengthSymbol { Str: VariableSymbol str } }:
                    Need("phi_strlen");
                    if (str.TextRef) LoadCallerText("esi", str);
                    else if (str.Grows) LoadText("esi", str);
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
                    if (cast.Operand.Type.IsFloat) EmitFloatToWhole(cast.Operand);
                    else EmitExpr(cast.Operand);
                    // a smaller number keeps the low bytes, like storing it would
                    switch (cast.Type.Kind)
                    {
                        case TypeKind.U8 or TypeKind.Bool or TypeKind.Chr: Emit("movzx eax, al"); break;
                        case TypeKind.I8: Emit("movsx eax, al"); break;
                        case TypeKind.U16: Emit("movzx eax, ax"); break;
                        case TypeKind.I16: Emit("movsx eax, ax"); break;
                    }
                    break;

                case NewExpr n:
                    EmitHeapCall(n.Allocator!, n.Bytes); // the address comes back in eax
                    break;

                case AtomicExpr swap:
                    EmitCompareSwap(swap.Target, swap.Value, swap.Expected);
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

                case UnaryExpr { Op: UnaryOp.Not, Operand.Type.IsFloat: true } notFloat:
                    EmitFloatIsZero(notFloat.Operand);
                    Emit("sete al");
                    Emit("movzx eax, al");
                    break;

                case UnaryExpr unary:
                    EmitExpr(unary.Operand);
                    if (unary.Operand.Type.IsWide) Emit("or eax, edx            ; not: all 64 bits are zero");
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

            // an object is at the address it holds, and a new str field's text where its slot says
            if (addr.OfObject || addr.Operand.Type.IsHeapText) EmitExpr(addr.Operand);
            else EmitPlaceAddress(addr.Operand);
        }

        /// <summary>eax = the address of a variable, field or element.</summary>
        void EmitPlaceAddress(Expr e)
        {
            if (e is NameExpr { Symbol: VariableSymbol { Type.Open: true } open })
            {
                Emit($"mov eax, [{open.Label}]   ; where {open.Name}'s elements are");
                return;
            }
            if (e is NameExpr { Symbol: VariableSymbol { Grows: true } text })
            {
                LoadText("eax", text);  // addr s: where its text is now
                return;
            }
            if (e is NameExpr { Symbol: VariableSymbol { TextRef: true } caller })
            {
                LoadCallerText("eax", caller);
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
            else if (bits32 || !p.Linear) Emit($"lea eax, {Operand(p)}");
            else Emit($"lea eax, [ebx{offset}]");   // (Operand would reach it through fs)
        }

        void EmitArithmetic(BinaryExpr binary)
        {
            bool unsigned = binary.Type.IsUnsigned32;
            if (TryDirectArithmetic(binary, unsigned)) return;
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

        /// <summary>
        /// The operation with one side used as the instruction's operand, as it is: a constant or a
        /// 32-bit variable (add eax, 5 / cmp eax, dword [x]), instead of loading it into ecx first.
        /// When the left side is the simple one, the right is worked out first. False when neither
        /// side is simple, or for division (which needs its divisor in a register).
        /// </summary>
        bool TryDirectArithmetic(BinaryExpr binary, bool unsigned)
        {
            string? op = binary.Op switch
            {
                BinaryOp.Add => "add", BinaryOp.Subtract => "sub", BinaryOp.Multiply => "imul",
                BinaryOp.BitAnd => "and", BinaryOp.BitOr => "or", BinaryOp.BitXor => "xor",
                BinaryOp.ShiftLeft => "shl", BinaryOp.ShiftRight => unsigned ? "shr" : "sar",
                _ => null,
            };
            if (op == null) return false;
            bool shift = binary.Op is BinaryOp.ShiftLeft or BinaryOp.ShiftRight;

            string? right = DirectOperand(binary.Right, constantOnly: shift);
            if (right != null)
            {
                EmitExpr(binary.Left);
                Emit($"{op} eax, {right}");
                return true;
            }

            // the left is simple: the right first, then the left goes with it
            string? left = DirectOperand(binary.Left, constantOnly: false);
            if (left == null || shift) return false;
            EmitExpr(binary.Right);
            if (binary.Op is BinaryOp.Add or BinaryOp.Multiply or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor)
            {
                Emit($"{op} eax, {left}");         // the order doesn't matter
            }
            else
            {
                Emit("mov ecx, eax");
                Emit($"mov eax, {left}");
                Emit($"{op} eax, ecx");
            }
            return true;
        }

        /// <summary>
        /// A constant, or a 32-bit number variable or field, written as an instruction's second
        /// operand; null for anything else. Reading it there reads memory once, as loading it would.
        /// </summary>
        string? DirectOperand(Expr e, bool constantOnly)
        {
            if (ConstantFolder.Evaluate(e) is long k && k >= int.MinValue && k <= uint.MaxValue)
                return ((int)k).ToString();
            if (constantOnly) return null;
            return e switch
            {
                NameExpr { Symbol: VariableSymbol { Type.IsNumeric: true } v } when StorageType(v.Type).ElementSize == 4 && !v.Type.IsFloat
                    => VariableOperand(v),
                NameExpr { Symbol: FieldPathSymbol { Type.IsNumeric: true } f } when StorageType(f.Type).ElementSize == 4 && !f.Type.IsFloat
                    => f.Offset == 0 ? $"dword [{f.Root.Label}]" : $"dword [{f.Root.Label} + {f.Offset}]",
                _ => null,
            };
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

            if (TryLoadSimple(b.Left, "eax", dryRun: true))
            {
                EmitExpr(b.Right);
                Emit("mov ecx, eax");
                TryLoadSimple(b.Left, "eax", dryRun: false);
                return;
            }

            // a left side worked out in eax alone: the right waits in ecx instead of on the stack
            if (OnlyEax(b.Left))
            {
                EmitExpr(b.Right);
                Emit("mov ecx, eax");
                int start = code.Length;
                EmitExpr(b.Left);
                foreach (string line in code.ToString(start, code.Length - start).Split('\n'))
                    if (!line.Contains("it stops here") && System.Text.RegularExpressions.Regex.IsMatch(WithoutComment(line), @"\b(ecx|cx|cl|ch|call)\b"))
                        throw new InvalidOperationException($"working out a value in eax alone changed ecx: {line.Trim()}");
                return;
            }

            EmitExpr(b.Right);
            Emit("push eax");
            EmitExpr(b.Left);
            Emit("pop ecx");
        }

        /// <summary>
        /// True when EmitExpr works the value out in eax (and ebx for a place) alone, leaving ecx
        /// and edx: a constant, a variable, an element or field reached without eax, or arithmetic
        /// whose right side is an operand as it is (see TryDirectArithmetic). 32-bit code only,
        /// since 16-bit code reaches memory through fs with ecx.
        /// </summary>
        bool OnlyEax(Expr e)
        {
            if (!bits32 || !OneRegister(e.Type)) return false;
            if (e is BinaryExpr or UnaryExpr && ConstantFolder.Evaluate(e) != null) return true;
            return e switch
            {
                NumberExpr or BoolExpr => true,
                NameExpr { Call: null, Symbol: VariableSymbol { Grows: false, TextRef: false } or FieldPathSymbol or ConstantSymbol } => true,
                NameExpr { Call: null, Symbol: SelfFieldSymbol or PathSymbol } => true,
                IndexExpr or MemberExpr { Call: null, Count: null, FilledLength: false, TextLength: false } => PlaceKeepsEax(e),
                BinaryExpr { IsComparison: false, IsLogical: false, Op: not (BinaryOp.Divide or BinaryOp.Modulo) } b =>
                    DirectOperand(b.Right, constantOnly: b.Op is BinaryOp.ShiftLeft or BinaryOp.ShiftRight) != null && OnlyEax(b.Left),
                UnaryExpr { Op: UnaryOp.Negate or UnaryOp.BitNot } u => OnlyEax(u.Operand),
                CastExpr c => OneRegister(c.Operand.Type) && OnlyEax(c.Operand),
                _ => false,
            };
        }

        /// <summary>Loads values that need no other registers straight into a register.</summary>
        bool TryLoadSimple(Expr e, string register, bool dryRun)
        {
            string? instruction = ConstantFolder.Evaluate(e) is long k
                ? (k == 0 ? $"xor {register}, {register}" : $"mov {register}, {(int)k}")
                : e switch
                {
                    StringExpr s => $"mov {register}, {StringLabel(s.Value)}",
                    NameExpr { Symbol: VariableSymbol { Type.IsString: true, Grows: false, TextRef: false } v } =>
                        InFrame(v.Label) ? $"lea {register}, [{v.Label}]" : $"mov {register}, {v.Label}",
                    NameExpr { Symbol: VariableSymbol { Type.IsNumeric: true } v } =>
                        RegisterOf(v) is string held ? $"mov {register}, {held}" : LoadInstruction(v.Type, register, $"[{v.Label}]"),
                    NameExpr { Call: null, Symbol: VariableSymbol { Type.IsObject: true } v } => $"mov {register}, dword [{v.Label}]",
                    AddrExpr { CodeLabel: string code } => $"mov {register}, {code}",
                    AddrExpr { OfObject: false, Operand: NameExpr { Symbol: VariableSymbol { Grows: false, TextRef: false, Type.Open: false, Type.IsHeapText: false } v } } =>
                        InFrame(v.Label) ? $"lea {register}, [{v.Label}]" : $"mov {register}, {v.Label}",
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

                case BinaryExpr { ArrayCompare: true } arrays:
                {
                    EmitArrayCompare(arrays);
                    Emit("test eax, eax");
                    bool equalWanted = (arrays.Op == BinaryOp.Equal) == jumpIf;
                    Emit($"{(equalWanted ? "jnz" : "jz")} {target}");
                    return;
                }

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

                case BinaryExpr { IsComparison: true } cmp when cmp.Left.Type.IsFloat || cmp.Right.Type.IsFloat:
                    EmitFloatCompare(cmp);
                    Emit("test eax, eax");
                    Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
                    return;

                case BinaryExpr { IsComparison: true } cmp when cmp.Left.Type.IsWide || cmp.Right.Type.IsWide:
                    EmitWideCompare(cmp);
                    Emit("test eax, eax");
                    Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
                    return;

                case BinaryExpr { IsComparison: true } cmp:
                {
                    bool unsigned = cmp.Left.Type.IsUnsigned32 || cmp.Right.Type.IsUnsigned32;
                    string? leftAsIs = HeldIn(cmp.Left) ?? (ConstantFolder.Evaluate(cmp.Left) == null ? DirectOperand(cmp.Left, constantOnly: false) : null);
                    string? rightAsIs = DirectOperand(cmp.Right, constantOnly: false);
                    if (leftAsIs == null && rightAsIs != null && !rightAsIs.Contains('[') && PlaceOperand(cmp.Left))
                        leftAsIs = $"dword {Operand(PlaceKeepingEax(cmp.Left))}";
                    if (leftAsIs != null && rightAsIs != null && !(leftAsIs.Contains('[') && rightAsIs.Contains('[')))
                    {
                        Emit($"cmp {leftAsIs}, {rightAsIs}");
                    }
                    else if (DirectOperand(cmp.Right, constantOnly: false) is string right)
                    {
                        EmitExpr(cmp.Left);
                        Emit($"cmp eax, {right}");
                    }
                    else
                    {
                        EmitOperands(cmp);
                        Emit("cmp eax, ecx");
                    }
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

            if (condition.Type.IsFloat)
            {
                EmitFloatIsZero(condition);
                Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
                return;
            }
            EmitExpr(condition);
            if (condition.Type.IsWide) Emit("or eax, edx");
            else Emit("test eax, eax");
            Emit($"{(jumpIf ? "jnz" : "jz")} {target}");
        }

        // ================================================================ floating point
        //
        // An f16, f32 or f64 is worked on in the FPU: EmitFloat leaves one value in st0 and
        // nothing else on the FPU's stack. The right side of an operation is worked out first
        // and kept on the CPU's stack, so a call in the middle (which may use the FPU itself)
        // never finds more than one value there.

        readonly Dictionary<double, string> floatLabels = new();

        string FloatLabel(double value)
        {
            if (!floatLabels.TryGetValue(value, out string? label))
            {
                label = $"phi_float_{floatLabels.Count}";
                floatLabels[value] = label;
            }
            return label;
        }

        /// <summary>st0 = e: a floating point value, or a whole number made one.</summary>
        void EmitFloat(Expr e)
        {
            if (!e.Type.IsFloat)
            {
                if (ConstantFolder.Evaluate(e) is long k)
                {
                    if (!e.Type.IsWide && !e.Type.IsSigned) k = (uint)k;
                    if (k == 0) Emit("fldz");
                    else if (k == 1) Emit("fld1");
                    else Emit($"fld qword [{FloatLabel(k)}]");
                }
                else if (e.Type.IsWide)
                {
                    EmitWide(e);
                    PushAndLoad("qword", "edx", "eax");
                }
                else if (e.Type.IsUnsigned32)
                {
                    EmitExpr(e);
                    Emit("push dword 0");
                    Emit("push eax");
                    Emit("fild qword [esp]");
                    Emit("add esp, 8");
                }
                else
                {
                    EmitExpr(e);
                    PushAndLoad("dword", "eax");
                }
                return;
            }

            switch (e)
            {
                case DecimalExpr d:
                    if (d.Value == 0 && !double.IsNegative(d.Value)) Emit("fldz");
                    else if (d.Value == 1) Emit("fld1");
                    else Emit($"fld qword [{FloatLabel(d.Value)}]");
                    return;

                case CallExpr call:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case NameExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case MemberExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;

                case CastExpr cast:
                    EmitFloat(cast.Operand);
                    return;

                case UnaryExpr unary:
                    EmitFloat(unary.Operand);
                    Emit("fchs");
                    return;

                case BinaryExpr { Op: BinaryOp.Modulo } modulo:
                {
                    // what's left over, with the left's sign (like C's fmod): fprem goes until it's done
                    EmitFloatPair(modulo.Left, modulo.Right);
                    Emit("fxch                    ; st0 = the left, st1 = the right");
                    string again = NewLabel("fprem");
                    Label(again);
                    Emit("fprem");
                    Emit("fnstsw ax");
                    Emit("sahf");
                    Emit($"jp {again}              ; c2: not finished");
                    Emit("fstp st1");
                    return;
                }

                case BinaryExpr binary:
                    EmitFloatPair(binary.Left, binary.Right);
                    Emit(binary.Op switch
                    {
                        BinaryOp.Add => "faddp st1, st0",
                        BinaryOp.Subtract => "fsubp st1, st0",
                        BinaryOp.Multiply => "fmulp st1, st0",
                        _ => "fdivp st1, st0",
                    });
                    return;
            }

            // a variable, field or element
            Place place = EmitPlace(e);
            switch (e.Type.Kind)
            {
                case TypeKind.F64: Emit($"fld qword {Operand(place)}"); break;
                case TypeKind.F32: Emit($"fld dword {Operand(place)}"); break;
                default:
                    Need("phi_f16_to_f32");
                    Emit($"movzx eax, word {Operand(place)}");
                    Emit("call phi_f16_to_f32");
                    PushAndLoad("dword", "eax", isFloat: true);
                    break;
            }
        }

        /// <summary>Pushes registers (the last is the low half) and loads them into st0 as a whole number (or as an f32).</summary>
        void PushAndLoad(string size, string first, string? second = null, bool isFloat = false)
        {
            Emit($"push {first}");
            if (second != null) Emit($"push {second}");
            Emit($"{(isFloat ? "fld" : "fild")} {size} [esp]");
            Emit($"add esp, {(second != null ? 8 : 4)}");
        }

        /// <summary>st0 = right, st1 = left.</summary>
        void EmitFloatPair(Expr left, Expr right)
        {
            EmitFloat(right);
            Emit("sub esp, 8");
            Emit("fstp qword [esp]");
            EmitFloat(left);
            Emit("fld qword [esp]");
            Emit("add esp, 8");
        }

        /// <summary>Takes st0 off the FPU's stack into a floating point place.</summary>
        void EmitFloatStore(Expr target)
        {
            Place place = EmitPlace(target);
            switch (target.Type.Kind)
            {
                case TypeKind.F64: Emit($"fstp qword {Operand(place)}"); break;
                case TypeKind.F32: Emit($"fstp dword {Operand(place)}"); break;
                default:
                    Need("phi_f32_to_f16");
                    Emit("sub esp, 4");
                    Emit("fstp dword [esp]");
                    Emit("pop eax");
                    Emit("call phi_f32_to_f16");
                    Emit($"mov word {Operand(place)}, ax");
                    break;
            }
        }

        /// <summary>edx:eax = a floating point value's whole part (rounded toward zero, as `as` does).</summary>
        void EmitFloatToWhole(Expr e)
        {
            EmitFloat(e);
            Emit("sub esp, 12");
            Emit("fnstcw [esp]");
            Emit("mov ax, [esp]");
            Emit("or ax, 0x0C00            ; round toward zero");
            Emit("mov [esp + 2], ax");
            Emit("fldcw [esp + 2]");
            Emit("fistp qword [esp + 4]");
            Emit("fldcw [esp]");
            Emit("mov eax, [esp + 4]");
            Emit("mov edx, [esp + 8]");
            Emit("add esp, 12");
        }

        /// <summary>Flags as for `cmp`: ZF set when the floating point value is zero.</summary>
        void EmitFloatIsZero(Expr e)
        {
            EmitFloat(e);
            Emit("ftst");
            Emit("fnstsw ax");
            Emit("fstp st0");
            Emit("sahf");
        }

        /// <summary>eax = 1 when a comparison of floating point values holds (never, with not-a-number, except !=).</summary>
        void EmitFloatCompare(BinaryExpr cmp)
        {
            EmitFloatPair(cmp.Left, cmp.Right);
            Emit("fxch                    ; st0 = the left, st1 = the right");
            Emit("fcompp");
            Emit("fnstsw ax");
            Emit("sahf");
            string unordered = NewLabel("nan");
            string done = NewLabel("fcompared");
            Emit($"jp {unordered}");
            Emit(cmp.Op switch
            {
                BinaryOp.Equal => "sete al",
                BinaryOp.NotEqual => "setne al",
                BinaryOp.Less => "setb al",
                BinaryOp.LessEqual => "setbe al",
                BinaryOp.Greater => "seta al",
                _ => "setae al",
            });
            Emit("movzx eax, al");
            Emit($"jmp {done}");
            Label(unordered);
            Emit($"mov eax, {(cmp.Op == BinaryOp.NotEqual ? 1 : 0)}");
            Label(done);
        }

        /// <summary>st0 = Float.Sqrt and the others, worked out on st0.</summary>
        void EmitFloatFunction(BuiltinFunction f, Expr argument)
        {
            EmitFloat(argument);
            if (f.FloatRounding < 0)
            {
                Emit(f.FloatInstruction!);
                return;
            }
            Emit("sub esp, 4");
            Emit("fnstcw [esp]");
            Emit("mov ax, [esp]");
            Emit("and ax, 0xF3FF");
            Emit($"or ax, {f.FloatRounding << 10}         ; how frndint rounds");
            Emit("mov [esp + 2], ax");
            Emit("fldcw [esp + 2]");
            Emit("frndint");
            Emit("fldcw [esp]");
            Emit("add esp, 4");
        }

        /// <summary>eax = the address of a 64-bit or floating point value written out, or of a chr's character (in a temporary).</summary>
        void EmitNumberText(Expr e)
        {
            if (e.Type.IsChar)
            {
                string one = AllocTemp(4);
                EmitExpr(e);
                Emit($"mov byte [{one}], al");
                Emit($"mov byte [{one} + 1], 0");
                LoadAddress("eax", one);
                return;
            }
            if (e.Type.IsWide)
            {
                EmitWideText(e);
                return;
            }
            string text = AllocTemp(48);
            EmitFloat(e);
            Need("phi_float_to_str");
            LoadAddress("edi", text);
            Emit("mov ecx, 48");
            Emit("call phi_float_to_str");
            LoadAddress("eax", text);
        }

        // ================================================================ 64-bit numbers
        //
        // An i64 or u64 is in edx:eax (edx the high half). The other side of an operation is
        // in ebx:ecx, and the library's int64.asm does multiplying, dividing and text.

        /// <summary>edx:eax = e, made 64 bits if it's a smaller number.</summary>
        void EmitWide(Expr e)
        {
            if (ConstantFolder.Evaluate(e) is long k)
            {
                if (!e.Type.IsWide && !e.Type.IsSigned) k = (uint)k;   // a u32 stays positive
                Emit($"mov eax, {(int)k}");
                Emit($"mov edx, {(int)(k >> 32)}");
                return;
            }
            if (!e.Type.IsWide)
            {
                EmitExpr(e);
                Emit(e.Type.IsSigned ? "cdq" : "xor edx, edx");
                return;
            }

            switch (e)
            {
                case CallExpr call:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case NameExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;
                case MemberExpr { Call: CallExpr call }:
                    EmitCallTo(call.Resolved, call.Arguments);
                    return;

                case CastExpr cast:
                    if (cast.Operand.Type.IsFloat) EmitFloatToWhole(cast.Operand);
                    else EmitWide(cast.Operand);
                    return;

                case UnaryExpr unary:
                    EmitWide(unary.Operand);
                    if (unary.Op == UnaryOp.Negate)
                    {
                        Emit("neg eax");
                        Emit("adc edx, 0");
                        Emit("neg edx");
                    }
                    else
                    {
                        Emit("not eax");
                        Emit("not edx");
                    }
                    return;

                case BinaryExpr { Op: BinaryOp.ShiftLeft or BinaryOp.ShiftRight } shift:
                {
                    EmitExpr(shift.Right);
                    Emit("push eax");
                    EmitWide(shift.Left);
                    Emit("pop ecx");
                    string small = NewLabel("shift");
                    Emit("test cl, 32");
                    Emit($"jz {small}");
                    if (shift.Op == BinaryOp.ShiftLeft)
                    {
                        Emit("mov edx, eax");
                        Emit("xor eax, eax");
                        Label(small);
                        Emit("shld edx, eax, cl");
                        Emit("shl eax, cl");
                    }
                    else if (shift.Type.Kind == TypeKind.U64)
                    {
                        Emit("mov eax, edx");
                        Emit("xor edx, edx");
                        Label(small);
                        Emit("shrd eax, edx, cl");
                        Emit("shr edx, cl");
                    }
                    else
                    {
                        Emit("mov eax, edx");
                        Emit("sar edx, 31");
                        Label(small);
                        Emit("shrd eax, edx, cl");
                        Emit("sar edx, cl");
                    }
                    return;
                }

                case BinaryExpr binary:
                {
                    EmitWideOperands(binary);
                    bool unsigned = binary.Type.Kind == TypeKind.U64;
                    switch (binary.Op)
                    {
                        case BinaryOp.Add: Emit("add eax, ecx"); Emit("adc edx, ebx"); break;
                        case BinaryOp.Subtract: Emit("sub eax, ecx"); Emit("sbb edx, ebx"); break;
                        case BinaryOp.BitAnd: Emit("and eax, ecx"); Emit("and edx, ebx"); break;
                        case BinaryOp.BitOr: Emit("or eax, ecx"); Emit("or edx, ebx"); break;
                        case BinaryOp.BitXor: Emit("xor eax, ecx"); Emit("xor edx, ebx"); break;
                        case BinaryOp.Multiply: CallWide("phi_mul64"); break;
                        case BinaryOp.Divide: CallWide(unsigned ? "phi_udiv64" : "phi_div64"); break;
                        case BinaryOp.Modulo: CallWide(unsigned ? "phi_umod64" : "phi_mod64"); break;
                        default: throw new InvalidOperationException($"no 64-bit {binary.Op}");
                    }
                    return;
                }
            }

            // a variable, field or element: its two halves
            Place place = EmitPlace(e);
            Emit($"mov eax, dword {Operand(place)}");
            Emit($"mov edx, dword {Operand(place.Plus(4))}");
        }

        void CallWide(string routine)
        {
            Need(routine);
            Emit($"call {routine}");
        }

        /// <summary>edx:eax = the left side, ebx:ecx = the right.</summary>
        void EmitWideOperands(BinaryExpr b)
        {
            EmitWide(b.Right);
            Emit("push edx");
            Emit("push eax");
            EmitWide(b.Left);
            Emit("pop ecx");
            Emit("pop ebx");
        }

        /// <summary>Stores edx:eax in a 64-bit place.</summary>
        void EmitWideStore(Expr target)
        {
            Emit("push edx");
            Emit("push eax");
            Place place = EmitPlace(target);
            Emit("pop eax");
            Emit("pop edx");
            Emit($"mov dword {Operand(place)}, eax");
            Emit($"mov dword {Operand(place.Plus(4))}, edx");
        }

        /// <summary>eax = 1 when a comparison of two 64-bit values holds, 0 when it doesn't.</summary>
        void EmitWideCompare(BinaryExpr cmp)
        {
            bool unsigned = cmp.Left.Type.Kind == TypeKind.U64 || cmp.Right.Type.Kind == TypeKind.U64;
            EmitWideOperands(cmp);
            // what the comparison gives when the left is less, equal and greater
            var (less, equal, greater) = cmp.Op switch
            {
                BinaryOp.Equal => (0, 1, 0),
                BinaryOp.NotEqual => (1, 0, 1),
                BinaryOp.Less => (1, 0, 0),
                BinaryOp.LessEqual => (1, 1, 0),
                BinaryOp.Greater => (0, 0, 1),
                _ => (0, 1, 1),
            };
            string lessLabel = NewLabel("wless");
            string greaterLabel = NewLabel("wgreater");
            string done = NewLabel("wcompared");
            Emit("cmp edx, ebx              ; the high halves decide, unless they're equal");
            Emit($"{(unsigned ? "jb" : "jl")} {lessLabel}");
            Emit($"{(unsigned ? "ja" : "jg")} {greaterLabel}");
            Emit("cmp eax, ecx");
            Emit($"jb {lessLabel}");
            Emit($"ja {greaterLabel}");
            Emit($"mov eax, {equal}");
            Emit($"jmp {done}");
            Label(lessLabel);
            Emit($"mov eax, {less}");
            Emit($"jmp {done}");
            Label(greaterLabel);
            Emit($"mov eax, {greater}");
            Label(done);
        }

        /// <summary>eax = the address of a 64-bit value written out in decimal (in a temporary).</summary>
        void EmitWideText(Expr e)
        {
            string text = AllocTemp(24);
            EmitWide(e);
            string convert = e.Type.Kind == TypeKind.U64 ? "phi_uint64_to_str" : "phi_int64_to_str";
            Need(convert);
            LoadAddress("edi", text);
            Emit("mov ecx, 24");
            Emit($"call {convert}");
            LoadAddress("eax", text);
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

        // ================================================================ whole arrays

        /// <summary>eax = how many elements an array has: its length, or an atmost one's count.</summary>
        void EmitArrayCount(Expr array)
        {
            if (array is NameExpr { Symbol: VariableSymbol { Type.Open: true } open })
                Emit($"mov eax, [{open.Label} + 4]");
            else if (array.Type.Filled) EmitFilledCount(array);
            else Emit($"mov eax, {array.Type.Length}");
        }

        /// <summary>b is a: every element copied (objects and strs too); an atmost b takes a's count.</summary>
        void EmitArrayAssign(ArrayAssignStmt copy)
        {
            PhiType t = copy.Target.Type;
            PhiType element = t.Element;
            int size = element.ElementSize;

            if (element.IsObject)
            {
                // an atmost target first lets go of the objects past the source's count
                EmitArrayCount(copy.Source);
                EmitCopyFits(copy);
                if (t.Filled)
                {
                    Emit("push eax");
                    EmitFreeElements(copy.Target, -1);
                    EmitPlaceAddress(copy.Target);
                    Emit("pop ecx");
                    Emit($"mov [eax + {t.CountOffset}], ecx");
                    Emit("mov eax, ecx");
                }
                EmitEachElement(() =>
                {
                    EmitPlaceAddress(copy.Source);
                    Emit("mov ecx, [esp]");
                    Emit("mov edx, [eax + ecx * 4]");
                    Emit("push edx                ; the source's object (or 0)");
                    EmitPlaceAddress(copy.Target);
                    Emit("mov ecx, [esp + 4]");
                    Emit("lea eax, [eax + ecx * 4]");
                    Emit("pop edx");
                    Emit($"call {ObjectRoutine("set", element.Struct!)}");
                });
                return;
            }

            if (element.Kind == TypeKind.Str)
            {
                var array = (VariableSymbol)((NameExpr)copy.Target).Symbol!;
                EmitArrayCount(copy.Source);
                EmitCopyFits(copy);
                EmitEachElement(() =>
                {
                    EmitPlaceAddress(copy.Source);
                    Emit("mov ecx, [esp]");
                    Emit("mov edx, [eax + ecx * 4]");
                    Emit("push edx                ; its text");
                    EmitPlaceAddress(copy.Target);
                    Emit("mov ecx, [esp + 4]");
                    Emit("lea eax, [eax + ecx * 4]");
                    if (array.GrowingElements)
                    {
                        Emit("push eax                ; the element's slot");
                        EmitGrowStoreAt(4 * array.Count, sourceIsString: true, append: false, unsigned: false);
                    }
                    else
                    {
                        Need("phi_strcpy");
                        Emit("mov edi, [eax]");
                        Emit("pop esi");
                        Emit($"mov ecx, {array.Capacity}");
                        Emit("call phi_strcpy");
                    }
                });
                return;
            }

            // anything else is its bytes
            EmitArrayCount(copy.Source);
            EmitCopyFits(copy);
            Emit("push eax                ; how many");
            EmitPlaceAddress(copy.Source);
            Emit("push eax");
            EmitPlaceAddress(copy.Target);
            Emit("mov edi, eax");
            Emit("pop esi");
            Emit("pop ecx");
            if (t.Filled) Emit($"mov [edi + {t.CountOffset}], ecx");
            if (size != 1) Emit($"imul ecx, ecx, {size}");
            Emit("cld");
            Emit("rep movsb");
        }

        /// <summary>
        /// eax = how many the source has: when either array is a parameter (its length known only
        /// when it runs), an index error unless they fit as the binder checks for the others.
        /// </summary>
        void EmitCopyFits(ArrayAssignStmt copy)
        {
            PhiType t = copy.Target.Type;
            if (copy.Target is NameExpr { Symbol: VariableSymbol { GrowingArray: true } growing })
            {
                // it grows to fit, and has that many (letting go of objects past them)
                ObjectLine(copy);
                Emit("push eax");
                Emit("mov edx, eax");
                LoadAddress("eax", growing.Label);
                Emit($"mov ecx, {t.Element.ElementSize}");
                EmitArrayGrowCall();
                Emit("mov eax, [esp]");
                if (t.IsObjectArray) EmitFreeElements(copy.Target, -1);
                Emit("pop eax");
                Emit($"mov [{growing.Label} + 4], eax");
                return;
            }
            if (!t.Open && !copy.Source.Type.Open) return;
            Need("phi_index_error");
            string ok = NewLabel("fits");
            if (t.Open)
                Emit($"cmp eax, [{((VariableSymbol)((NameExpr)copy.Target).Symbol!).Label} + 4]");
            else Emit($"cmp eax, {t.Length}");
            Emit($"{(t.Filled ? "jbe" : "je")} {ok}");
            EmitIndexError(copy);
            Label(ok);
        }

        bool usesArrayGrow;

        /// <summary>
        /// eax = an array that grows (where its elements are, how many, its room), edx = how many
        /// it needs room for, ecx = the bytes of one; after, it has room for them.
        /// </summary>
        void EmitArrayGrowCall()
        {
            usesArrayGrow = true;
            Need("phi_out_of_memory");
            Emit("call phi_array_grow");
        }

        /// <summary>An array that grows, empty again: its objects and its room let go.</summary>
        void EmitGrowingArrayEmpty(VariableSymbol v)
        {
            EmitGrowingArrayFree(v);
            Emit($"mov dword [{v.Label}], 0");
            Emit($"mov dword [{v.Label} + 4], 0");
            Emit($"mov dword [{v.Label} + 8], 0");
        }

        void EmitGrowingArrayFree(VariableSymbol v)
        {
            if (v.Type.IsObjectArray) EmitFreeElements(NameOf(v), 0);
            Emit($"push dword [{v.Label}]   ; {v.Name}'s elements");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit("add esp, 4");
        }

        /// <summary>phi_array_grow, for arrays without a size (see EmitArrayGrowCall).</summary>
        void EmitArrayGrowRoutine()
        {
            if (!usesArrayGrow) return;
            Line("");
            Comment("room in an array that grows: eax = the array, edx = how many, ecx = bytes each");
            Label("phi_array_grow");
            Emit("cmp edx, [eax + 8]");
            Emit("jbe .fits");
            Emit("push ebp");
            Emit("mov ebp, esp");
            Emit("push eax                ; [ebp - 4] the array");
            Emit("push ecx                ; [ebp - 8] bytes each");
            // the new room: twice the old, at least what's needed, and at least 4
            Emit("mov ebx, [eax + 8]");
            Emit("add ebx, ebx");
            Emit("cmp ebx, edx");
            Emit("jae .twice");
            Emit("mov ebx, edx");
            Label(".twice");
            Emit("cmp ebx, 4");
            Emit("jae .room");
            Emit("mov ebx, 4");
            Label(".room");
            Emit("push ebx                ; [ebp - 12] the new room");
            Emit("mov eax, ebx");
            Emit("mul dword [ebp - 8]");
            Emit("push eax");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit("add esp, 4");
            Emit("test eax, eax");
            Emit("jz phi_out_of_memory");
            Emit("push eax                ; [ebp - 16] where the elements go now");
            // the elements so far move over (their objects, if any, go with them)
            Emit("mov edx, [ebp - 4]");
            Emit("mov esi, [edx]");
            Emit("mov edi, eax");
            Emit("mov ecx, [edx + 4]");
            Emit("imul ecx, [ebp - 8]");
            Emit("cld");
            Emit("rep movsb");
            Emit("mov edx, [ebp - 4]");
            Emit("push dword [edx]");
            Emit($"call {HeapMethodLabel("Heap.Free")}");
            Emit("add esp, 4");
            Emit("mov edx, [ebp - 4]");
            Emit("mov eax, [ebp - 16]");
            Emit("mov [edx], eax");
            Emit("mov eax, [ebp - 12]");
            Emit("mov [edx + 8], eax");
            Emit("mov esp, ebp");
            Emit("pop ebp");
            Label(".fits");
            Emit("ret");
        }

        /// <summary>
        /// Runs `body` for each element, 0 to eax - 1: the element's number is in dword [esp] when
        /// it starts, and it leaves the stack as it found it.
        /// </summary>
        void EmitEachElement(Action body)
        {
            string next = NewLabel("each");
            string done = NewLabel("eachdone");
            Emit("push eax                ; how many");
            Emit("push dword 0            ; which one");
            Label(next);
            Emit("mov eax, [esp]");
            Emit("cmp eax, [esp + 4]");
            Emit($"jae {done}");
            body();
            Emit("inc dword [esp]");
            Emit($"jmp {next}");
            Label(done);
            Emit("add esp, 8");
        }

        /// <summary>eax = 1 when two whole arrays have the same number of elements, all equal.</summary>
        void EmitArrayCompare(BinaryExpr cmp)
        {
            PhiType element = cmp.Left.Type.Element;
            string differ = NewLabel("differ");
            string done = NewLabel("compared");

            EmitArrayCount(cmp.Left);
            Emit("push eax");
            EmitArrayCount(cmp.Right);
            Emit("pop ecx");
            Emit("cmp eax, ecx");
            Emit($"jne {differ}             ; different lengths");

            if (element.IsObject || element.Kind == TypeKind.Str)
            {
                string unequal = NewLabel("unequal");
                EmitEachElement(() =>
                {
                    EmitPlaceAddress(cmp.Right);
                    Emit("mov ecx, [esp]");
                    Emit("mov edx, [eax + ecx * 4]");
                    Emit("push edx");
                    EmitPlaceAddress(cmp.Left);
                    Emit("mov ecx, [esp + 4]");
                    Emit("mov eax, [eax + ecx * 4]");
                    Emit("pop edx");
                    if (element.IsObject)
                    {
                        Emit($"call {ObjectRoutine("equal", element.Struct!)}");
                        Emit("test eax, eax");
                        Emit($"jz {unequal}");
                    }
                    else
                    {
                        Need("phi_strcmp");
                        Emit("mov esi, eax");
                        Emit("mov edi, edx");
                        Emit("call phi_strcmp          ; eax = 0 when equal");
                        Emit("test eax, eax");
                        Emit($"jnz {unequal}");
                    }
                });
                Emit("mov eax, 1");
                Emit($"jmp {done}");
                Label(unequal);
                Emit("add esp, 8              ; the loop's count and number");
                Emit($"jmp {differ}");
            }
            else
            {
                if (element.ElementSize != 1) Emit($"imul eax, eax, {element.ElementSize}");
                Emit("push eax                ; bytes");
                EmitPlaceAddress(cmp.Right);
                Emit("push eax");
                EmitPlaceAddress(cmp.Left);
                Emit("mov esi, eax");
                Emit("pop edi");
                Emit("pop ecx");
                Emit("cld");
                Emit("xor eax, eax            ; equal so far (ZF set), even for none");
                Emit("repe cmpsb");
                Emit("sete al");
                Emit($"jmp {done}");
            }
            Label(differ);
            Emit("xor eax, eax");
            Label(done);
        }

        /// <summary>
        /// new T list[N]: N zeroed objects (each one emptied and used again when the declaration
        /// runs again). new T list[atmost N]: none, freeing any from before.
        /// </summary>
        void EmitNewArray(VarDecl decl, VariableSymbol v)
        {
            ObjectLine(decl);
            StructSymbol s = v.Type.Struct!;
            if (v.Type.Filled)
            {
                EmitFreeElements(NameOf(v), 0);
                LoadAddress("eax", v.Label);
                Emit($"mov dword [eax + {v.Type.CountOffset}], 0   ; none in it");
                return;
            }
            string next = NewLabel("element");
            Emit("push dword 0            ; which element");
            Label(next);
            LoadAddress("eax", v.Label);
            Emit("mov edx, [esp]");
            Emit("lea eax, [eax + edx * 4]");
            Emit($"call {ObjectRoutine("renew", s)}");
            Emit("inc dword [esp]");
            Emit($"cmp dword [esp], {v.Type.Length}");
            Emit($"jb {next}");
            Emit("add esp, 4");
        }

        /// <summary>
        /// Frees the objects of an array of objects from element `from` on (each slot becomes empty);
        /// for an atmost array, from a number in eax instead when from is -1.
        /// </summary>
        void EmitFreeElements(Expr array, int from)
        {
            StructSymbol s = array.Type.Struct!;
            string next = NewLabel("free");
            string done = NewLabel("freed");
            if (from >= 0) Emit($"push dword {from}");
            else Emit("push eax");
            Label(next);
            if (array.Type.Open)
            {
                // past how many it has, up to its room, the slots are empty already
                string label = ((VariableSymbol)((NameExpr)array).Symbol!).Label;
                Emit($"mov eax, [{label} + 8]");
                Emit("cmp [esp], eax");
            }
            else Emit($"cmp dword [esp], {array.Type.Length}");
            Emit($"jae {done}");
            EmitPlaceAddress(array);
            Emit("mov edx, [esp]");
            Emit("lea edx, [eax + edx * 4]");
            Emit("mov eax, [edx]");
            Emit("mov dword [edx], 0       ; empty");
            Emit($"call {ObjectRoutine("free", s)}");
            Emit("inc dword [esp]");
            Emit($"jmp {next}");
            Label(done);
            Emit("add esp, 4");
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
                if (next.Struct == StructSymbol.Text)
                {
                    EmitTextRoutine(next.Kind);
                    continue;
                }
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

        /// <summary>
        /// A method's str result grows to fit (up to a str's max) when there's a heap: the caller
        /// passes a slot like a growing str's, which it frees when it ends. Without a heap (16-bit
        /// code), it's a buffer as big as the method can return.
        /// </summary>
        bool GrowResults => bits32 && unit.Methods.Any(m => m.Name == "Heap.Alloc");

        /// <summary>The current method's slots for the str results of the calls it makes.</summary>
        readonly List<string> resultSlots = new();

        /// <summary>The room <c>ask</c> makes in a str that grows: a line of up to 255 characters.</summary>
        const int AskRoom = 256;

        /// <summary>The bit of a growing str's room that says its text is in the program's data, not the heap.</summary>
        const string InData = "0x80000000";

        /// <summary>
        /// In the room passed for a str parameter that's the caller's text: the caller's str has a
        /// size, so its text never moves and what's written is cut to fit.
        /// </summary>
        const string Fixed = "0x40000000";

        /// <summary>A room without its flags.</summary>
        const string RoomMask = "0x3FFFFFFF";

        bool usesStrRoom;

        /// <summary>A register = the text of a str that grows; "" while nothing has been written to it.</summary>
        /// <summary>register = where the text of the caller's str (a str parameter with no default) is.</summary>
        void LoadCallerText(string register, VariableSymbol v)
        {
            string done = NewLabel("text");
            Emit($"mov {register}, dword [{v.Label}]");
            Emit($"mov {register}, dword [{register}]");
            Emit($"test {register}, {register}");
            Emit($"jnz {done}");
            Emit($"mov {register}, {StringLabel("")}");
            Label(done);
        }

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
        /// edi = where a str that grows keeps its text's address (its room right after, or at ebx
        /// with roomInEbx), ecx = the bytes wanted in it (with the zero). After: edi = its text,
        /// with room for that many (or as many as the heap had), ecx = its room, and edx = its old
        /// text, to free once nothing reads from it (0 if it didn't move).
        /// </summary>
        void EmitStrRoomCall(bool roomInEbx = false)
        {
            usesStrRoom = true;
            if (!roomInEbx) Emit("lea ebx, [edi + 4]");
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
            LoadAddress("eax", v.Label);
            Emit("push eax                ; where its text is (its room is after it)");
            EmitGrowStoreAt(4, sourceIsString, append, unsigned);
        }

        /// <summary>
        /// The value is on the stack, and above it the slot that holds the text's address; the
        /// room is roomOffset bytes after the slot. Writes the value in, making room first.
        /// </summary>
        void EmitGrowStoreAt(int roomOffset, bool sourceIsString, bool append, bool unsigned, string? view = null)
        {
            if (sourceIsString)
            {
                Need("phi_strlen");
                Emit("mov esi, [esp + 4]");
                Emit("call phi_strlen");
                Emit("inc eax                 ; with its zero");
            }
            else Emit("mov eax, 12             ; the most a number takes, with its zero");
            if (append)
            {
                Need("phi_strlen");
                string text = NewLabel("text");
                Emit("push eax");
                Emit("mov esi, [esp + 4]");
                Emit("mov esi, [esi]          ; what's there now");
                Emit("test esi, esi");
                Emit($"jnz {text}");
                Emit($"mov esi, {StringLabel("")}");
                Label(text);
                Emit("call phi_strlen");
                Emit("pop ecx");
                Emit("add eax, ecx            ; after what's there");
            }
            Emit("mov ecx, eax");
            Emit("mov edi, [esp]");
            if (view != null)
            {
                // an element of a str rows[] parameter: its room is the caller's .max slots on
                Emit($"mov ebx, [{view} + 8]");
                Emit("lea ebx, [edi + ebx*4]");
            }
            else Emit($"lea ebx, [edi + {roomOffset}]");
            EmitStrRoomCall(roomInEbx: true);
            Emit("push edx");
            Emit("mov eax, [esp + 8]      ; the value");
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
            Emit("add esp, 8");
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
            Emit($"test dword [ebx], {Fixed}");
            Emit("jnz .fits               ; a caller's str with a size: it stays, and the text is cut to fit");
            Emit("mov eax, [ebx]");
            Emit($"and eax, {RoomMask}      ; its room (the top bits: its text isn't on the heap, or never moves)");
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
            Emit("push ebx");
            Emit("push edi");
            Emit("push eax                ; [esp] the new room, [esp + 4] the text's slot, [esp + 8] the room's");
            Emit("push eax");
            Emit($"call {HeapMethodLabel("Heap.Alloc")}");
            Emit("add esp, 4");
            Emit("pop ecx");
            Emit("pop edi");
            Emit("pop ebx");
            Emit("test eax, eax");
            Emit("jz .fits                ; no memory: it keeps the room it has");
            // the text so far goes along, then the old place is the caller's to free
            Emit("push ebx");
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
            Emit("pop ebx");
            Emit("mov edx, [edi]          ; the old text, to free (unless it wasn't on the heap)");
            Emit($"test dword [ebx], {InData}");
            Emit("jz .heap");
            Emit("xor edx, edx");
            Label(".heap");
            Emit("mov [edi], eax");
            Emit("mov [ebx], ecx");
            Label(".fits");
            Emit("mov ecx, [ebx]");
            Emit($"and ecx, {RoomMask}");
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

        /// <summary>
        /// The routines for a new str field's text, called the way an owned object's are:
        /// free (eax = the text), clone (eax = the text; eax = a copy on the heap, or 0) and
        /// equal (eax, edx = the texts; eax = 1 when they match). No text is the same as ''.
        /// </summary>
        void EmitTextRoutine(string kind)
        {
            switch (kind)
            {
                case "free":
                    Emit("test eax, eax");
                    Emit("jz .done");
                    Emit("push eax");
                    Emit($"call {HeapMethodLabel("Heap.Free")}");
                    Emit("add esp, 4");
                    Label(".done");
                    Emit("ret");
                    break;

                case "clone":
                    Need("phi_strlen");
                    Need("phi_strcpy");
                    Need("phi_out_of_memory");
                    Emit("test eax, eax");
                    Emit("jnz .some");
                    Emit("ret                     ; no text: a copy has none either");
                    Label(".some");
                    Emit("push ebp");
                    Emit("mov ebp, esp");
                    Emit("push eax                ; [ebp - 4] the text");
                    Emit("mov esi, eax");
                    Emit("call phi_strlen");
                    Emit("inc eax                 ; with its zero");
                    Emit("push eax                ; [ebp - 8] the room it takes");
                    Emit("push eax");
                    Emit($"call {HeapMethodLabel("Heap.Alloc")}");
                    Emit("add esp, 4");
                    Emit("test eax, eax");
                    Emit("jz phi_out_of_memory");
                    Emit("push eax");
                    Emit("mov edi, eax");
                    Emit("mov esi, [ebp - 4]");
                    Emit("mov ecx, [ebp - 8]");
                    Emit("call phi_strcpy");
                    Emit("pop eax");
                    Emit("mov esp, ebp");
                    Emit("pop ebp");
                    Emit("ret");
                    break;

                case "equal":
                {
                    Need("phi_strcmp");
                    string empty = StringLabel("");
                    Emit("test eax, eax");
                    Emit("jnz .a");
                    Emit($"mov eax, {empty}");
                    Label(".a");
                    Emit("test edx, edx");
                    Emit("jnz .b");
                    Emit($"mov edx, {empty}");
                    Label(".b");
                    Emit("mov esi, eax");
                    Emit("mov edi, edx");
                    Emit("call phi_strcmp");
                    Emit("xor eax, 1              ; 1 when they match");
                    Emit("ret");
                    break;
                }

                default:
                    throw new InvalidOperationException($"no {kind} routine for text");
            }
        }

        /// <summary>
        /// After a struct's owned texts were copied: each copy's room is what its text takes (the
        /// struct's bytes still hold the source's). target is where the struct is, like [ebp - 12].
        /// </summary>
        void EmitTextRooms(StructSymbol s, string target)
        {
            foreach (OwnedSlot slot in s.OwnedSlots.Where(o => o.Of == StructSymbol.Text))
            {
                string none = $".room{labelCount++}";
                Need("phi_strlen");
                Emit($"mov edx, {target}");
                Emit($"mov esi, [edx + {slot.Offset}]");
                Emit("xor eax, eax");
                Emit("test esi, esi");
                Emit($"jz {none}");
                Emit("call phi_strlen");
                Emit("inc eax");
                Label(none);
                Emit($"mov edx, {target}");
                Emit($"mov [edx + {slot.Offset + 4}], eax   ; the copy's room");
            }
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
            EmitTextRooms(s, "[ebp - 12]");
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
            EmitTextRooms(s, "[ebp - 4]");
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
                // past the slot: an object's address, or a text's address and its room
                start = end + (s.OwnedSlots.Any(o => o.Offset == end && o.Of == StructSymbol.Text) ? 8 : 4);
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

            foreach (var (value, label) in floatLabels)
                output.AppendLine($"{label}: dq 0x{BitConverter.DoubleToInt64Bits(value):X}   ; {value}");
        }

        string DataFor(VariableSymbol v)
        {
            var sb = new StringBuilder();
            List<object>? values = v.StaticValues;
            PhiType t = v.Type;

            if (v.GrowingArray)
            {
                sb.AppendLine($"{v.Label}: dd 0, 0, 0   ; an array that grows: its elements (on the heap), how many, its room");
                return sb.ToString();
            }

            if (t.Kind is TypeKind.F16 or TypeKind.F32 or TypeKind.F64)
            {
                // floating point: each constant's bits
                var bits = (values ?? new List<object>()).Select(x => x is double d ? d : (double)(long)x).Select(d => t.Kind switch
                {
                    TypeKind.F64 => $"0x{BitConverter.DoubleToInt64Bits(d):X}",
                    TypeKind.F32 => $"0x{BitConverter.SingleToInt32Bits((float)d):X}",
                    _ => $"0x{BitConverter.HalfToInt16Bits((Half)d):X}",
                }).ToList();
                string kind = t.Kind switch { TypeKind.F64 => "dq", TypeKind.F32 => "dd", _ => "dw" };
                if (bits.Count == 0) sb.AppendLine($"{v.Label}: times {v.Count} {kind} 0   ; {t}");
                else
                {
                    sb.AppendLine($"{v.Label}: {kind} {string.Join(", ", bits)}   ; {t}");
                    if (bits.Count < v.Count) sb.AppendLine($"    times {v.Count - bits.Count} {kind} 0");
                }
                if (t.Filled) sb.AppendLine("    dd 0   ; how many are in it");
                return sb.ToString();
            }

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

            if (t.Kind == TypeKind.Str && t.Room > 0)
            {
                sb.AppendLine($"{v.Label}: times {t.ByteSize} db 0   ; {t.Length} texts of up to {t.Room - 1}");
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
                if (v.GrowingElements)
                    sb.AppendLine($"    dd {string.Join(", ", Enumerable.Repeat($"{v.Capacity} | {InData}", v.Count))}   ; each one's room: its own buffer");
                if (t.Filled) sb.AppendLine("    dd 0   ; how many are in it");
                return sb.ToString();
            }

            if (t.Kind == TypeKind.Struct)
            {
                sb.AppendLine($"{v.Label}: times {StorageSize(v)} db 0   ; {t}");
                return sb.ToString();
            }

            string directive = t.ElementSize switch { 1 => "db", 2 => "dw", 8 => "dq", _ => "dd" };
            if (values == null || values.Count == 0)
            {
                sb.AppendLine(v.Count == 1 ? $"{v.Label}: {directive} 0" : $"{v.Label}: times {v.Count} {directive} 0");
                if (t.Filled) sb.AppendLine("    dd 0   ; how many are in it");
                return sb.ToString();
            }

            long mask = t.ElementSize switch { 1 => 0xFF, 2 => 0xFFFF, 8 => -1, _ => 0xFFFFFFFF };
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

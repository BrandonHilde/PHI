using Phi.Compiler.CodeGen.Ir;
using Phi.Compiler.Semantics;

namespace Phi.Compiler.CodeGen.A64
{
    // What PHI allocates itself (objects, text and arrays that grow), for macOS programs. The x86
    // generator writes a routine for each struct's objects in its own assembly (free, clone,
    // assign, set, renew, equal: X86Generator's EmitObjectRoutines); here each is a function in
    // the intermediate form, built below and written by the same emitter as the program's
    // methods, so it's PHI's own contract: the arguments at [sp] when it's called (the last
    // lowest, 8 bytes each), what it gives in x0. A struct with interfaces has its type table's
    // address in the 8 bytes before each object (x86 keeps 4 there); a type table is 8-byte
    // addresses: free, clone and equal, then each interface's methods at its place.
    public sealed partial class A64Generator
    {
        readonly List<(string Kind, StructSymbol Struct)> objectRoutines = new();
        readonly Dictionary<string, (string Kind, PhiType Element)> arrayRoutines = new();
        readonly List<StructSymbol> typeTables = new();

        /// <summary>A type table's methods start after its free, clone and equal.</summary>
        const int TypeTableMethods = 24;

        static string Struct(StructSymbol s) => Sym(Syntax.Templates.Label(s.Name));

        string ObjectRoutine(string kind, StructSymbol s)
        {
            if (!objectRoutines.Contains((kind, s))) objectRoutines.Add((kind, s));
            return $"phi_{kind}_{Struct(s)}";
        }

        string ArrayRoutine(string kind, PhiType element)
        {
            string label = $"phi_array_{kind}_{(element.IsObject ? Struct(element.Struct!) : element.Kind == TypeKind.Str ? "text" : $"b{element.ElementSize}")}";
            arrayRoutines.TryAdd(label, (kind, element));
            return label;
        }

        string TypeTable(StructSymbol s)
        {
            if (!typeTables.Contains(s))
            {
                typeTables.Add(s);
                ObjectRoutine("free", s);
                ObjectRoutine("clone", s);
                ObjectRoutine("equal", s);
            }
            return $"phi_type_{Struct(s)}";
        }

        /// <summary>The bytes before an object: its type table's address, for a struct with interfaces.</summary>
        static int ObjectHeader(StructSymbol s) => s.Typed ? 8 : 0;

        /// <summary>The program's Heap.Alloc or Heap.Free: phi_heap_alloc or phi_heap_free, which go to it.</summary>
        string Heap(string name)
        {
            string routine = name == "Heap.Alloc" ? "phi_heap_alloc" : "phi_heap_free";
            needed.Add(routine);
            return routine;
        }

        /// <summary>phi_heap_alloc and phi_heap_free, as the runtime calls them: the program's Heap's methods.</summary>
        void EmitHeapEntries(System.Text.StringBuilder output)
        {
            // (objects.s calls both, whichever of its routines the program has)
            if (needed.Overlaps(new[] { "phi_str_room", "phi_grow_store", "phi_array_grow", "phi_text_array_grow" }))
            {
                needed.Add("phi_heap_alloc");
                needed.Add("phi_heap_free");
            }
            foreach (var (routine, name) in new[] { ("phi_heap_alloc", "Heap.Alloc"), ("phi_heap_free", "Heap.Free") })
            {
                if (!needed.Contains(routine)) continue;
                MethodSymbol? m = unit.Methods.FirstOrDefault(m => m.Name == name);
                if (m == null)
                {
                    NotYet(program.File, default, $"objects or text that grows without {name} (use memory.program_heap;)");
                    return;
                }
                output.AppendLine($"{routine}:");
                output.AppendLine($"\tb {Sym(m.Label)}");
            }
        }

        // ---------------------------------------------------------------- the routines

        /// <summary>Every routine the code asked for, and those they ask for, until there are no more.</summary>
        void EmitObjectRoutines()
        {
            var done = new HashSet<string>();
            while (true)
            {
                if (arrayRoutines.Keys.FirstOrDefault(l => !done.Contains(l)) is string pending)
                {
                    done.Add(pending);
                    var (kind, element) = arrayRoutines[pending];
                    EmitRoutine(pending, $"{kind} for an array of {element} without a size", ArrayRoutineBody(kind, element));
                    continue;
                }
                if (objectRoutines.FirstOrDefault(r => !done.Contains($"phi_{r.Kind}_{Struct(r.Struct)}")) is not { Struct: not null } next) break;
                string label = $"phi_{next.Kind}_{Struct(next.Struct)}";
                done.Add(label);
                if (next.Struct.IsInterface && next.Kind is "free" or "clone" or "equal")
                {
                    EmitInterfaceDispatch(label, next.Kind);
                    continue;
                }
                IrFunction fn = next.Struct == StructSymbol.Text ? TextRoutine(next.Kind)
                    : next.Struct.IsInterface ? InterfaceRoutine(next.Kind, next.Struct)
                    : next.Kind switch
                    {
                        "free" => FreeRoutine(next.Struct),
                        "clone" => CloneRoutine(next.Struct),
                        "assign" => AssignRoutine(next.Struct),
                        "set" => SetRoutine(next.Struct),
                        "renew" => RenewRoutine(next.Struct),
                        _ => EqualRoutine(next.Struct),
                    };
                EmitRoutine(label, $"{next.Kind} for {next.Struct.Name} objects", fn);
            }
        }

        void EmitRoutine(string label, string what, IrFunction fn)
        {
            IrPeephole.Run(fn);
            RegisterAllocator regs = RegisterAllocator.Allocate(fn, target: Registers);
            code.AppendLine();
            code.AppendLine($"; ---- {what}");
            code.AppendLine("\t.p2align 2");
            code.AppendLine($"{label}:");
            new Emitter(this, fn, regs, label).Emit();
        }

        /// <summary>
        /// free, clone and equal for an interface's objects: to the routine of whichever struct the
        /// object is, from its type table (the arguments stay where they are, at [sp]).
        /// </summary>
        void EmitInterfaceDispatch(string label, string kind)
        {
            code.AppendLine();
            code.AppendLine($"; ---- {kind} for an interface's objects: the struct's own");
            code.AppendLine("\t.p2align 2");
            code.AppendLine($"{label}:");
            if (kind == "equal")
            {
                string same = NewLabel("same"), different = NewLabel("different");
                code.AppendLine("\tldr x9, [sp, #8]            ; a");
                code.AppendLine("\tldr x10, [sp]               ; b");
                code.AppendLine("\tcmp x9, x10");
                code.AppendLine($"\tb.eq {same}                  ; the same object (or both empty)");
                code.AppendLine($"\tcbz x9, {different}");
                code.AppendLine($"\tcbz x10, {different}");
                code.AppendLine("\tldur x11, [x9, #-8]");
                code.AppendLine("\tldur x12, [x10, #-8]");
                code.AppendLine("\tcmp x11, x12");
                code.AppendLine($"\tb.ne {different}             ; different structs");
                code.AppendLine("\tldr x11, [x11, #16]");
                code.AppendLine("\tbr x11");
                code.AppendLine($"{same}:");
                code.AppendLine("\tmov w0, #1");
                code.AppendLine("\tret");
                code.AppendLine($"{different}:");
                code.AppendLine("\tmov w0, #0");
                code.AppendLine("\tret");
                return;
            }
            string none = NewLabel("none");
            code.AppendLine("\tldr x9, [sp]");
            code.AppendLine($"\tcbz x9, {none}                ; nothing to free or copy");
            code.AppendLine("\tldur x9, [x9, #-8]");
            code.AppendLine($"\tldr x9, [x9, #{(kind == "free" ? 0 : 8)}]");
            code.AppendLine("\tbr x9");
            code.AppendLine($"{none}:");
            code.AppendLine("\tmov x0, #0");
            code.AppendLine("\tret");
        }

        /// <summary>Each struct's type table, in the program's data (addresses: the loader moves them with it).</summary>
        void EmitTypeTables(System.Text.StringBuilder output)
        {
            if (typeTables.Count == 0) return;
            output.AppendLine("\t.section __DATA,__const");
            output.AppendLine("\t.p2align 3");
            foreach (StructSymbol s in typeTables)
            {
                int count = s.Interfaces.Count == 0 ? 0 : s.Interfaces.Max(i => i.TableStart + i.Decl.Methods.Count);
                var methods = Enumerable.Repeat("0", count).ToArray();
                foreach (StructSymbol iface in s.Interfaces)
                    for (int i = 0; i < iface.Decl.Methods.Count; i++)
                    {
                        string wanted = iface.Decl.Methods[i].Name;
                        MethodSymbol? m = unit.Methods.FirstOrDefault(m => m.Decl.Owner.ForStruct?.Symbol == s && m.Decl.Name == wanted);
                        if (m != null) methods[iface.TableStart + i] = Sym(m.Label);
                    }
                output.AppendLine($"phi_type_{Struct(s)}:");
                output.AppendLine($"\t.quad phi_free_{Struct(s)}, phi_clone_{Struct(s)}, phi_equal_{Struct(s)}");
                if (count > 0) output.AppendLine($"\t.quad {string.Join(", ", methods)}");
            }
        }

        // ---------------------------------------------------------------- building a routine

        /// <summary>A routine's function in the intermediate form, a block at a time.</summary>
        sealed class Routine
        {
            public readonly IrFunction Fn;
            public Block Current;
            readonly int parameters;

            public Routine(string name, int parameters)
            {
                Fn = new IrFunction { Name = name };
                Current = Fn.NewBlock();
                this.parameters = parameters;
            }

            void Emit(Instr i) => Current.Instrs.Add(i);

            /// <summary>Parameter k of the routine's (the last at x29 + 16, as a method's are).</summary>
            public VReg Param(int k)
            {
                VReg v = Fn.NewAddress();
                Emit(new LoadI { Dst = v, Place = $"rbp + {16 + 8 * (parameters - 1 - k)}", Kind = MemKind.Q64 });
                return v;
            }

            public VReg Address() => Fn.NewAddress();

            public VReg Load(VReg at, long offset, MemKind kind = MemKind.Q64)
            {
                VReg v = kind == MemKind.Q64 ? Fn.NewAddress() : Fn.NewValue(false);
                Emit(new LoadIdxI { Dst = v, Base = at, Index = Opnd.Const(offset), Scale = 1, Kind = kind });
                return v;
            }

            public void Store(VReg at, long offset, Opnd value, MemKind kind = MemKind.Q64) =>
                Emit(new StoreIdxI { Base = at, Index = Opnd.Const(offset), Scale = 1, Src = value, Kind = kind });

            public VReg Add(VReg a, Opnd b, bool wide = true)
            {
                VReg v = Fn.NewValue(wide);
                Emit(new BinI { Op = BinOp.Add, Dst = v, A = a, B = b });
                return v;
            }

            public VReg Op(BinOp op, Opnd a, Opnd b, bool wide)
            {
                VReg v = Fn.NewValue(wide);
                Emit(new BinI { Op = op, Dst = v, A = a, B = b });
                return v;
            }

            public void Set(VReg v, Opnd value)
            {
                if (value.IsImm) Emit(new ConstI { Dst = v, Value = value.Imm });
                else Emit(new CopyI { Dst = v, Src = value });
            }

            public VReg Const(long n, bool wide = true)
            {
                VReg v = Fn.NewValue(wide);
                Emit(new ConstI { Dst = v, Value = n });
                return v;
            }

            /// <summary>A call: CallResult.None, Eax (32 bits) or Rax (an address).</summary>
            public VReg? Call(string label, CallResult result, params Opnd[] args)
            {
                var call = new CallI
                {
                    Label = label,
                    Result = result,
                    Dst = result == CallResult.None ? null : result == CallResult.Rax ? Fn.NewAddress() : Fn.NewValue(false),
                };
                foreach (Opnd a in args)
                {
                    call.Args.Add(a);
                    call.ArgKinds.Add(MemKind.Q64);
                }
                Emit(call);
                return call.Dst;
            }

            public void Instr(Instr i) => Emit(i);

            public Block Block() => Fn.NewBlock();

            public void Enter(Block b) => Current = b;

            /// <summary>To then when a cond b, else to otherwise; then carries on in a new block unless it's given.</summary>
            public void If(VReg a, Cond cond, Opnd b, Block then, Block otherwise) =>
                Current.End = new BranchT { A = a, Cond = cond, B = b, Then = then, Else = otherwise };

            public void Jump(Block to) => Current.End = new JumpT { Target = to };

            public void Return(Opnd? value = null, bool pointer = false) =>
                Current.End = new RetT { Value = value, Pointer = pointer };

            /// <summary>The rest only when a cond b holds: what's left of the routine runs in the block after.</summary>
            public void Unless(VReg a, Cond cond, Opnd b, Action leave)
            {
                Block leaving = Fn.NewBlock(), on = Fn.NewBlock();
                If(a, cond, b, leaving, on);
                Enter(leaving);
                leave();
                Enter(on);
            }
        }

        /// <summary>A new object of s, zeroed (the heap's), its type table set; out of memory stops.</summary>
        VReg Alloc(Routine r, StructSymbol s)
        {
            needed.Add("phi_out_of_memory");
            int header = ObjectHeader(s);
            VReg a = r.Call(Heap("Heap.Alloc"), CallResult.Rax, Opnd.Const(s.Size + header))!;
            r.Unless(a, Cond.Eq, Opnd.Const(0), () =>
            {
                r.Call("phi_out_of_memory", CallResult.None);
                r.Return();
            });
            if (s.Typed)
            {
                VReg table = r.Address();
                r.Instr(new AddrI { Dst = table, Label = TypeTable(s) });
                r.Store(a, 0, table);
                return r.Add(a, Opnd.Const(header));
            }
            return a;
        }

        void HeapFree(Routine r, Opnd at) => r.Call(Heap("Heap.Free"), CallResult.None, at);

        static OwnedSlot? Chain(StructSymbol s) =>
            s.OwnedSlots.Where(o => o.Of == s && o.Array == null).Select(o => (OwnedSlot?)o).LastOrDefault();

        static IEnumerable<OwnedSlot> NotChain(StructSymbol s) => s.OwnedSlots.Where(o => o != Chain(s));

        /// <summary>A slot's free routine for the struct at obj: an object's (or text's) with what it holds, an array's with where it is.</summary>
        void SlotFree(Routine r, OwnedSlot slot, VReg obj)
        {
            if (slot.Array != null)
            {
                r.Call(ArrayRoutine("free", slot.Array), CallResult.None, r.Add(obj, Opnd.Const(slot.Offset)));
                return;
            }
            r.Call(ObjectRoutine("free", slot.Of), CallResult.None, r.Load(obj, slot.Offset));
        }

        /// <summary>After a struct's owned texts were copied: each copy's room is what its text takes.</summary>
        void TextRooms(Routine r, StructSymbol s, VReg target)
        {
            foreach (OwnedSlot slot in s.OwnedSlots.Where(o => o.Of == StructSymbol.Text))
            {
                VReg text = r.Load(target, slot.Offset);
                VReg room = r.Fn.NewValue(false);
                r.Set(room, Opnd.Const(0));
                Block some = r.Block(), after = r.Block();
                r.If(text, Cond.Ne, Opnd.Const(0), some, after);
                r.Enter(some);
                VReg length = r.Fn.NewValue(false);
                r.Instr(new TextI { Op = TextOp.Length, Dst = length, A = text });
                r.Instr(new BinI { Op = BinOp.Add, Dst = room, A = length, B = Opnd.Const(1) });
                r.Jump(after);
                r.Enter(after);
                r.Store(target, slot.Offset + 8, room, MemKind.D32);
            }
        }

        IrFunction FreeRoutine(StructSymbol s)
        {
            var r = new Routine($"free {s.Name}", 1);
            OwnedSlot? chain = Chain(s);
            VReg obj = r.Param(0);
            Block loop = r.Block();
            r.Jump(loop);
            r.Enter(loop);
            r.Unless(obj, Cond.Eq, Opnd.Const(0), () => r.Return());
            foreach (OwnedSlot slot in NotChain(s)) SlotFree(r, slot, obj);
            VReg? next = chain is OwnedSlot c ? r.Load(obj, c.Offset) : null;
            HeapFree(r, s.Typed ? r.Add(obj, Opnd.Const(-ObjectHeader(s))) : obj);
            if (next != null)
            {
                // the next one, freed by going round again
                r.Set(obj, next);
                r.Jump(loop);
            }
            else r.Return();
            return r.Fn;
        }

        IrFunction CloneRoutine(StructSymbol s)
        {
            var r = new Routine($"clone {s.Name}", 1);
            OwnedSlot? chain = Chain(s);
            VReg source = r.Param(0);
            r.Unless(source, Cond.Eq, Opnd.Const(0), () => r.Return(Opnd.Const(0), pointer: true));
            VReg copy = Alloc(r, s);
            VReg filling = r.Address();
            r.Set(filling, copy);
            Block fill = r.Block();
            r.Jump(fill);
            r.Enter(fill);
            r.Instr(new CopyBytesI { Dst = filling, Src = source, Count = r.Const(s.Size) });
            foreach (OwnedSlot slot in NotChain(s))
            {
                if (slot.Array != null)
                {
                    // its own room, with copies of the elements
                    r.Call(ArrayRoutine("clone", slot.Array), CallResult.None, r.Add(source, Opnd.Const(slot.Offset)), r.Add(filling, Opnd.Const(slot.Offset)));
                    continue;
                }
                VReg made = r.Call(ObjectRoutine("clone", slot.Of), CallResult.Rax, r.Load(source, slot.Offset))!;
                r.Store(filling, slot.Offset, made);
            }
            TextRooms(r, s, filling);
            if (chain is OwnedSlot c)
            {
                r.Store(filling, c.Offset, Opnd.Const(0));
                VReg next = r.Load(source, c.Offset);
                r.Unless(next, Cond.Eq, Opnd.Const(0), () => r.Return(copy, pointer: true));
                r.Set(source, next);
                VReg another = Alloc(r, s);
                r.Store(filling, c.Offset, another);
                r.Set(filling, another);
                r.Jump(fill);
            }
            else r.Return(copy, pointer: true);
            return r.Fn;
        }

        IrFunction AssignRoutine(StructSymbol s)
        {
            var r = new Routine($"assign {s.Name}", 2);
            VReg into = r.Param(0), from = r.Param(1);
            if (!s.OwnsObjects)
            {
                r.Instr(new CopyBytesI { Dst = into, Src = from, Count = r.Const(s.Size) });
                r.Return();
                return r.Fn;
            }
            if (s.OwnedSlots.Any(o => o.Array != null))
            {
                // a whole copy of the source first (it may be inside the target); then the target
                // lets go of what it owns and takes the copy's values, and the copy's memory goes back
                VReg copy = r.Call(ObjectRoutine("clone", s), CallResult.Rax, from)!;
                foreach (OwnedSlot slot in s.OwnedSlots) SlotFree(r, slot, into);
                r.Instr(new CopyBytesI { Dst = into, Src = copy, Count = r.Const(s.Size) });
                HeapFree(r, s.Typed ? r.Add(copy, Opnd.Const(-ObjectHeader(s))) : copy);
                r.Return();
                return r.Fn;
            }
            // copies of what the source owns are made before anything changes, because the
            // source may be inside the target (head is head.next); the target's old objects are
            // freed last
            var copies = s.OwnedSlots.Select(slot => r.Call(ObjectRoutine("clone", slot.Of), CallResult.Rax, r.Load(from, slot.Offset))!).ToList();
            var old = s.OwnedSlots.Select(slot => r.Load(into, slot.Offset)).ToList();
            r.Instr(new CopyBytesI { Dst = into, Src = from, Count = r.Const(s.Size) });
            for (int i = 0; i < copies.Count; i++) r.Store(into, s.OwnedSlots[i].Offset, copies[i]);
            TextRooms(r, s, into);
            for (int i = 0; i < old.Count; i++) r.Call(ObjectRoutine("free", s.OwnedSlots[i].Of), CallResult.None, old[i]);
            r.Return();
            return r.Fn;
        }

        IrFunction SetRoutine(StructSymbol s)
        {
            var r = new Routine($"set {s.Name}", 2);
            VReg slot = r.Param(0), source = r.Param(1);
            r.Unless(source, Cond.Eq, Opnd.Const(0), () =>
            {
                // a copy of nothing: the slot is emptied
                VReg had = r.Load(slot, 0);
                r.Store(slot, 0, Opnd.Const(0));
                r.Call(ObjectRoutine("free", s), CallResult.None, had);
                r.Return();
            });
            VReg have = r.Load(slot, 0);
            r.Unless(have, Cond.Eq, Opnd.Const(0), () =>
            {
                // an empty slot gets a fresh copy, made before the slot changes: the source may contain it (a.next is a)
                VReg made = r.Call(ObjectRoutine("clone", s), CallResult.Rax, source)!;
                r.Store(slot, 0, made);
                r.Return();
            });
            r.Call(ObjectRoutine("assign", s), CallResult.None, have, source);
            r.Return();
            return r.Fn;
        }

        IrFunction RenewRoutine(StructSymbol s)
        {
            var r = new Routine($"renew {s.Name}", 1);
            VReg slot = r.Param(0);
            VReg have = r.Load(slot, 0);
            r.Unless(have, Cond.Eq, Opnd.Const(0), () =>
            {
                VReg made = Alloc(r, s);
                r.Store(slot, 0, made);
                r.Return();
            });
            // the object emptied, to be used again
            foreach (OwnedSlot owned in s.OwnedSlots) SlotFree(r, owned, have);
            VReg count = r.Const(s.Size);
            VReg k = r.Const(0);
            Block loop = r.Block(), body = r.Block(), done = r.Block();
            r.Jump(loop);
            r.Enter(loop);
            r.If(k, Cond.LtU, count, body, done);
            r.Enter(body);
            r.Instr(new StoreIdxI { Base = have, Index = k, Scale = 1, Src = Opnd.Const(0), Kind = MemKind.U8 });
            r.Instr(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
            r.Jump(loop);
            r.Enter(done);
            r.Return();
            return r.Fn;
        }

        IrFunction EqualRoutine(StructSymbol s)
        {
            var r = new Routine($"equal {s.Name}", 2);
            OwnedSlot? chain = Chain(s);
            VReg a = r.Param(0), b = r.Param(1);
            Block same = r.Block(), different = r.Block(), next = r.Block();
            r.Jump(next);
            r.Enter(next);
            Block on1 = r.Block();
            r.If(a, Cond.Eq, b, same, on1);                   // the same object (or both empty)
            r.Enter(on1);
            Block on2 = r.Block();
            r.If(a, Cond.Eq, Opnd.Const(0), different, on2);
            r.Enter(on2);
            Block on3 = r.Block();
            r.If(b, Cond.Eq, Opnd.Const(0), different, on3);
            r.Enter(on3);
            // the bytes between the owned slots, which hold addresses rather than values
            int start = 0;
            foreach (int end in s.OwnedSlots.Select(o => o.Offset).Append(s.Size))
            {
                if (end > start)
                {
                    VReg match = r.Fn.NewValue(false);
                    r.Instr(new CompareBytesI { Dst = match, A = r.Add(a, Opnd.Const(start)), B = r.Add(b, Opnd.Const(start)), Count = r.Const(end - start) });
                    Block on = r.Block();
                    r.If(match, Cond.Eq, Opnd.Const(0), different, on);
                    r.Enter(on);
                }
                // past the slot: an object's address, an array's view, or a text's address and its room
                start = end + (s.OwnedSlots.Any(o => o.Offset == end && o.Array != null) ? 16
                               : s.OwnedSlots.Any(o => o.Offset == end && o.Of == StructSymbol.Text) ? 16 : 8);
            }
            foreach (OwnedSlot slot in NotChain(s))
            {
                VReg match = slot.Array != null
                    ? r.Call(ArrayRoutine("equal", slot.Array), CallResult.Eax, r.Add(a, Opnd.Const(slot.Offset)), r.Add(b, Opnd.Const(slot.Offset)))!
                    : r.Call(ObjectRoutine("equal", slot.Of), CallResult.Eax, r.Load(a, slot.Offset), r.Load(b, slot.Offset))!;
                Block on = r.Block();
                r.If(match, Cond.Eq, Opnd.Const(0), different, on);
                r.Enter(on);
            }
            if (chain is OwnedSlot c)
            {
                r.Set(a, r.Load(a, c.Offset));
                r.Set(b, r.Load(b, c.Offset));
                r.Jump(next);
            }
            else r.Jump(same);
            r.Enter(same);
            r.Return(Opnd.Const(1));
            r.Enter(different);
            r.Return(Opnd.Const(0));
            return r.Fn;
        }

        /// <summary>
        /// The routines for a new str field's text, called as an owned object's are: free (the
        /// text), clone (a copy on the heap, or 0) and equal (1 when they match; none is '').
        /// </summary>
        IrFunction TextRoutine(string kind)
        {
            var r = new Routine($"{kind} text", kind == "equal" ? 2 : 1);
            switch (kind)
            {
                case "free":
                {
                    VReg text = r.Param(0);
                    r.Unless(text, Cond.Eq, Opnd.Const(0), () => r.Return());
                    HeapFree(r, text);
                    r.Return();
                    break;
                }
                case "clone":
                {
                    needed.Add("phi_out_of_memory");
                    VReg text = r.Param(0);
                    r.Unless(text, Cond.Eq, Opnd.Const(0), () => r.Return(Opnd.Const(0), pointer: true));
                    VReg length = r.Fn.NewValue(false);
                    r.Instr(new TextI { Op = TextOp.Length, Dst = length, A = text });
                    VReg room = r.Add(length, Opnd.Const(1), wide: false);
                    VReg wide = r.Address();
                    r.Instr(new ExtendI { Dst = wide, Src = room, Signed = false });
                    VReg made = r.Call(Heap("Heap.Alloc"), CallResult.Rax, wide)!;
                    r.Unless(made, Cond.Eq, Opnd.Const(0), () =>
                    {
                        r.Call("phi_out_of_memory", CallResult.None);
                        r.Return();
                    });
                    r.Instr(new CopyBytesI { Dst = made, Src = text, Count = wide });
                    r.Return(made, pointer: true);
                    break;
                }
                default:
                {
                    VReg a = r.Param(0), b = r.Param(1);
                    VReg ta = r.Address(), tb = r.Address();
                    r.Instr(new TextI { Op = TextOp.OrEmpty, Dst = ta, A = a });
                    r.Instr(new TextI { Op = TextOp.OrEmpty, Dst = tb, A = b });
                    VReg differ = r.Fn.NewValue(false);
                    r.Instr(new TextI { Op = TextOp.Equal, Dst = differ, A = ta, B = tb });
                    r.Return(r.Op(BinOp.Xor, differ, Opnd.Const(1), false));
                    break;
                }
            }
            return r.Fn;
        }

        /// <summary>set, renew and assign for an interface's objects (free, clone and equal go to the struct's own).</summary>
        IrFunction InterfaceRoutine(string kind, StructSymbol iface)
        {
            var r = new Routine($"{kind} {iface.Name}", kind == "renew" ? 1 : 2);
            switch (kind)
            {
                case "set":
                {
                    // a copy of the source (made first: it may be inside what the slot had), and what it had freed
                    VReg slot = r.Param(0), source = r.Param(1);
                    VReg made = r.Call(ObjectRoutine("clone", iface), CallResult.Rax, source)!;
                    VReg had = r.Load(slot, 0);
                    r.Store(slot, 0, made);
                    r.Call(ObjectRoutine("free", iface), CallResult.None, had);
                    break;
                }
                case "renew":
                {
                    // a new one starts empty
                    VReg slot = r.Param(0);
                    VReg had = r.Load(slot, 0);
                    r.Store(slot, 0, Opnd.Const(0));
                    r.Call(ObjectRoutine("free", iface), CallResult.None, had);
                    break;
                }
            }
            // (assign: the binder only lets a slot that owns its object get another one)
            r.Return();
            return r.Fn;
        }

        /// <summary>
        /// free, clone and equal for a struct's array without a size (its view: elements, how many
        /// (+8), room (+12)): its elements' room, and the objects or texts in it.
        /// </summary>
        IrFunction ArrayRoutineBody(string kind, PhiType element)
        {
            var r = new Routine($"array {kind}", kind == "free" ? 1 : 2);
            VReg array = r.Param(0);
            VReg? other = kind == "free" ? null : r.Param(1);
            bool texts = element.Kind == TypeKind.Str, objects = element.IsObject;
            int size = texts ? 8 : element.ElementSize;

            // runs body for each element from 0 to the count at count's place, k its number
            void Each(VReg limitAt, int limitOffset, Action<VReg> body, Block? exit = null)
            {
                VReg k = r.Address();
                r.Set(k, Opnd.Const(0));
                Block loop = r.Block(), inside = r.Block(), done = exit ?? r.Block();
                r.Jump(loop);
                r.Enter(loop);
                VReg limit32 = r.Load(limitAt, limitOffset, MemKind.D32);
                VReg limit = r.Address();
                r.Instr(new ExtendI { Dst = limit, Src = limit32, Signed = false });
                r.If(k, Cond.LtU, limit, inside, done);
                r.Enter(inside);
                body(k);
                r.Instr(new BinI { Op = BinOp.Add, Dst = k, A = k, B = Opnd.Const(1) });
                r.Jump(loop);
                if (exit == null) r.Enter(done);
            }

            VReg Element(VReg view, VReg k, long plus = 0)
            {
                VReg elements = r.Load(view, 0);
                VReg v = r.Address();
                r.Instr(new LoadIdxI { Dst = v, Base = plus == 0 ? elements : r.Add(elements, Opnd.Const(plus)), Index = k, Scale = 8, Kind = MemKind.Q64 });
                return v;
            }

            switch (kind)
            {
                case "free":
                {
                    if (objects)
                        Each(array, 8, k => r.Call(ObjectRoutine("free", element.Struct!), CallResult.None, Element(array, k)));
                    else if (texts)
                        Each(array, 8, k =>
                        {
                            // (a text in the program's data, an empty one, is never freed)
                            VReg max = r.Load(array, 12, MemKind.D32);
                            VReg roomsAt = r.Address();
                            r.Instr(new ExtendI { Dst = roomsAt, Src = max, Signed = false });
                            VReg rooms = r.Add(r.Load(array, 0), r.Op(BinOp.Shl, roomsAt, Opnd.Const(3), true));
                            VReg room = r.Fn.NewValue(false);
                            r.Instr(new LoadIdxI { Dst = room, Base = rooms, Index = k, Scale = 8, Kind = MemKind.D32 });
                            Block free = r.Block(), kept = r.Block();
                            r.Current.End = new BranchT { A = room, Cond = Cond.Eq, B = Opnd.Const(0), TestMask = 0x80000000, Then = free, Else = kept };
                            r.Enter(free);
                            HeapFree(r, Element(array, k));
                            r.Jump(kept);
                            r.Enter(kept);
                        });
                    HeapFree(r, r.Load(array, 0));      // (freeing 0 does nothing)
                    r.Store(array, 0, Opnd.Const(0));
                    r.Store(array, 8, Opnd.Const(0), MemKind.D32);
                    r.Store(array, 12, Opnd.Const(0), MemKind.D32);
                    r.Return();
                    break;
                }

                case "clone":
                {
                    needed.Add("phi_out_of_memory");
                    VReg copy = other!;
                    r.Store(copy, 8, r.Load(array, 8, MemKind.D32), MemKind.D32);
                    VReg max = r.Load(array, 12, MemKind.D32);
                    r.Store(copy, 12, max, MemKind.D32);
                    r.Store(copy, 0, Opnd.Const(0));
                    r.Unless(max, Cond.Eq, Opnd.Const(0), () => r.Return());      // no room: nothing to copy
                    VReg maxWide = r.Address();
                    r.Instr(new ExtendI { Dst = maxWide, Src = max, Signed = false });
                    VReg bytes = r.Op(BinOp.Mul, maxWide, Opnd.Const(texts ? 16 : size), true);
                    VReg made = r.Call(Heap("Heap.Alloc"), CallResult.Rax, bytes)!;
                    r.Unless(made, Cond.Eq, Opnd.Const(0), () =>
                    {
                        r.Call("phi_out_of_memory", CallResult.None);
                        r.Return();
                    });
                    r.Store(copy, 0, made);
                    if (texts)
                    {
                        // every slot up to the room: one in the data stays as it is, one on the heap is copied
                        Each(array, 12, k =>
                        {
                            VReg rooms = r.Op(BinOp.Shl, maxWide, Opnd.Const(3), true);
                            VReg text = Element(array, k);
                            VReg room = r.Fn.NewValue(false);
                            r.Instr(new LoadIdxI { Dst = room, Base = r.Add(r.Load(array, 0), rooms), Index = k, Scale = 8, Kind = MemKind.D32 });
                            VReg copyText = r.Address();
                            r.Set(copyText, text);
                            Block heap = r.Block(), keep = r.Block();
                            r.Current.End = new BranchT { A = room, Cond = Cond.Eq, B = Opnd.Const(0), TestMask = 0x80000000, Then = heap, Else = keep };
                            r.Enter(heap);
                            VReg cloned = r.Call(ObjectRoutine("clone", StructSymbol.Text), CallResult.Rax, text)!;
                            r.Set(copyText, cloned);
                            VReg length = r.Fn.NewValue(false);
                            r.Instr(new TextI { Op = TextOp.Length, Dst = length, A = cloned });
                            r.Instr(new BinI { Op = BinOp.Add, Dst = room, A = length, B = Opnd.Const(1) });
                            r.Jump(keep);
                            r.Enter(keep);
                            VReg into = r.Load(copy, 0);
                            r.Instr(new StoreIdxI { Base = into, Index = k, Scale = 8, Src = copyText, Kind = MemKind.Q64 });
                            r.Instr(new StoreIdxI { Base = r.Add(into, rooms), Index = k, Scale = 8, Src = room, Kind = MemKind.Q64 });
                        });
                        r.Return();
                        break;
                    }
                    VReg count = r.Load(array, 8, MemKind.D32);
                    VReg countWide = r.Address();
                    r.Instr(new ExtendI { Dst = countWide, Src = count, Signed = false });
                    r.Instr(new CopyBytesI { Dst = made, Src = r.Load(array, 0), Count = r.Op(BinOp.Mul, countWide, Opnd.Const(size), true) });
                    if (objects)
                        // each object copied too, so the copy has its own
                        Each(copy, 8, k =>
                        {
                            VReg cloned = r.Call(ObjectRoutine("clone", element.Struct!), CallResult.Rax, Element(copy, k))!;
                            r.Instr(new StoreIdxI { Base = r.Load(copy, 0), Index = k, Scale = 8, Src = cloned, Kind = MemKind.Q64 });
                        });
                    r.Return();
                    break;
                }

                default:
                {
                    VReg b = other!;
                    Block same = r.Block(), different = r.Block();
                    VReg ca = r.Load(array, 8, MemKind.D32), cb = r.Load(b, 8, MemKind.D32);
                    Block on = r.Block();
                    r.If(ca, Cond.Ne, cb, different, on);         // not as many
                    r.Enter(on);
                    if (objects || texts)
                    {
                        StructSymbol of = texts ? StructSymbol.Text : element.Struct!;
                        Each(array, 8, k =>
                        {
                            VReg match = r.Call(ObjectRoutine("equal", of), CallResult.Eax, Element(array, k), Element(b, k))!;
                            Block next = r.Block();
                            r.If(match, Cond.Eq, Opnd.Const(0), different, next);
                            r.Enter(next);
                        }, same);
                    }
                    else
                    {
                        VReg n = r.Address();
                        r.Instr(new ExtendI { Dst = n, Src = ca, Signed = false });
                        VReg match = r.Fn.NewValue(false);
                        r.Instr(new CompareBytesI { Dst = match, A = r.Load(array, 0), B = r.Load(b, 0), Count = r.Op(BinOp.Mul, n, Opnd.Const(size), true) });
                        r.If(match, Cond.Eq, Opnd.Const(0), different, same);
                    }
                    r.Enter(same);
                    r.Return(Opnd.Const(1));
                    r.Enter(different);
                    r.Return(Opnd.Const(0));
                    break;
                }
            }
            return r.Fn;
        }
    }
}

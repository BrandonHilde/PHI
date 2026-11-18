using Phi.Compiler.CodeGen.Ir;

namespace Phi.Compiler.CodeGen.A64
{
    public sealed partial class A64Generator
    {
        /// <summary>ErrorKind (lib/phi/lang/error.phi): what a throw; is, and a number that doesn't fit the other kind.</summary>
        const int ErrorThrown = 0;
        const int ErrorConvert = 7;

        /// <summary>An Error's bytes (lib/phi/lang/error.phi), as x86's: what a catch copies into err.</summary>
        static int ErrorBytes => X86Generator.ErrorBytes;

        sealed partial class Emitter
        {
            /// <summary>The instructions of objects, catches and vecs (written as their milestones come); false for one that isn't.</summary>
            bool More(Instr i)
            {
                switch (i)
                {
                    case CatchI c:
                    {
                        // the generator's EmitIfThrows' record (throw.s has its fields): the catch
                        // around it, sp, x29, the methods' records, where a throw goes, how deep in
                        // the log's lock; then it's the innermost catch
                        g.Need("phi_throw");
                        g.Need("phi_log_held");
                        PlaceAddress("x9", c.Record);
                        E("ldr x10, [x28, #8]");
                        E("str x10, [x9]");
                        E("mov x10, sp");
                        E("str x10, [x9, #8]");
                        E("str x29, [x9, #16]");
                        E("ldr x10, [x28, #16]");
                        E("str x10, [x9, #24]");
                        E($"adr x10, {blockLabels[c.Caught]}");
                        E("str x10, [x9, #32]");
                        E("bl phi_log_held");
                        PlaceAddress("x9", c.Record);
                        E("str x0, [x9, #40]");
                        E("str x9, [x28, #8]");
                        return true;
                    }
                    case UncatchI u:
                        // the call came back: the catch around this one is the innermost again
                        PlaceAddress("x9", u.Record);
                        E("ldr x10, [x9]");
                        E("str x10, [x28, #8]");
                        return true;
                    case ObjCallI oc:
                    {
                        // a struct's routine (A64Generator.Objects.cs): the object, or where it's kept,
                        // then the one copied from
                        var args = new List<Opnd>();
                        if (oc.Arg != null) args.Add(oc.Arg);
                        else
                        {
                            PlaceAddress("x9", oc.Home!);
                            args.Add(Opnd.Const(long.MinValue));     // (x9: see Arguments)
                        }
                        if (oc.Second != null) args.Add(oc.Second);
                        Arguments(args);
                        E($"bl {g.ObjectRoutine(oc.Kind, oc.Struct)}");
                        if (oc.Dst != null) Result(oc.Dst);
                        return true;
                    }
                    case ArrayRoutineI ar:
                    {
                        var args = new List<Opnd> { ar.View };
                        if (ar.Made != null) args.Add(ar.Made);
                        Arguments(args);
                        E($"bl {g.ArrayRoutine(ar.Kind, ar.Element)}");
                        return true;
                    }
                    case ObjectLineI ol:
                        // the line an object is made on, for the message if the heap runs out
                        g.Need("phi_object_line");
                        E("adrp x16, phi_object_line@PAGE");
                        MovImm("x17", LineOf(ol.At), false);
                        E("str w17, [x16, phi_object_line@PAGEOFF]");
                        return true;
                    case FreeTextI ft:
                    {
                        if (ft.Value != null) Arguments(new List<Opnd> { ft.Value });
                        else
                        {
                            var (b, off) = Place(ft.Place);
                            Mem("ldr", "x9", b, off, 8);
                            Arguments(new List<Opnd> { Opnd.Const(long.MinValue) });
                        }
                        E($"bl {g.Heap("Heap.Free")}");
                        return true;
                    }
                    case ArrayGrowI grow:
                        g.Need("phi_out_of_memory");
                        Into("x1", grow.Count, false);
                        Into("x0", grow.View, true);
                        if (grow.Texts)
                        {
                            g.Need("phi_text_array_grow");
                            E("bl phi_text_array_grow");
                        }
                        else
                        {
                            g.Need("phi_array_grow");
                            MovImm("x2", grow.Size, false);
                            E("bl phi_array_grow");
                        }
                        g.Heap("Heap.Alloc");
                        g.Heap("Heap.Free");
                        return true;
                    case GrowStoreI gs:
                    {
                        // phi_grow_store (objects.s): the slot, its room's word, the value, what it is
                        string? lengthPlace = gs.Length;
                        Into("x2", gs.Value, gs.SourceIsString);
                        Into("x0", gs.Slot, true);
                        if (gs.Rooms != null)
                        {
                            // an element of a str array without a most: its room is .max slots on
                            var (rb, ro) = Place(gs.Rooms);
                            Mem("ldr", "w9", rb, ro, 4);
                            E("add x1, x0, x9, lsl #3");
                        }
                        else E($"add x1, x0, #{gs.RoomOffset ?? 8}");
                        MovImm("x3", (gs.SourceIsString ? 1 : 0) | (gs.Append ? 2 : 0) | (gs.Unsigned ? 4 : 0), false);
                        if (lengthPlace != null) PlaceAddress("x4", lengthPlace);
                        else E("mov x4, #0");
                        MovImm("x5", gs.SourceIsString && gs.Known is int k ? k + 1 : 0, false);
                        GrowStore();
                        if (!gs.MayCut) E($"cbnz w0, {TextError(new List<StoreI>())}");
                        return true;
                    }
                    case ResultTextI { Grow: true } rt:
                    {
                        // as the generator's method end: into the caller's slot, which grows
                        Into("x2", rt.Value, true);
                        E("ldr x0, [x29, #16]");
                        E("add x1, x0, #8");
                        E("mov w3, #1");
                        E("mov x4, #0");
                        E("mov w5, #0");
                        GrowStore();
                        E($"cbnz w0, {TextError(rt.Held)}");
                        string t = Target(rt.Dst, "x9");
                        E("ldr x9, [x29, #16]");
                        E("ldr x9, [x9]");
                        Address("x16", g.StringLabel(""));
                        E("cmp x9, #0");
                        E($"csel {W(t, true)}, x9, x16, ne");
                        Put(rt.Dst, t);
                        return true;
                    }
                    case InterfaceMethodI im:
                    {
                        // from the object's type table, the 8 bytes before it
                        string recv = Get(im.Receiver, "x9", true);
                        string t = Target(im.Dst, "x9");
                        E($"ldur x16, [{recv}, #-8]");
                        Mem("ldr", W(t, true), "x16", TypeTableMethods + 8 * (im.Interface.TableStart + im.Index), 8);
                        Put(im.Dst, t);
                        return true;
                    }
                    case UnwindI { On: true }:
                    {
                        // onto the list a throw looks at: the record before, what frees this method's
                        // objects (called with x29 this frame), and this frame
                        int at = fn.UnwindRecord;
                        SubImm("x9", "x29", at);
                        E("ldr x10, [x28, #16]");
                        E("str x10, [x9]");
                        Address("x10", $"{methodLabel}_unwind");
                        E("str x10, [x9, #8]");
                        E("str x29, [x9, #16]");
                        E("str x9, [x28, #16]");
                        unwinds = true;
                        return true;
                    }
                    case UnwindI { On: false }:
                        // off it: the record before is the one a throw looks at again
                        Mem("ldr", "x10", "x29", -fn.UnwindRecord, 8);
                        E("str x10, [x28, #16]");
                        return true;
                    case CaughtI caught:
                    {
                        // the thread's error, copied into err (nothing is in a register here)
                        PlaceAddress("x9", caught.Place);
                        E("add x10, x28, #24");
                        E($"mov x11, #{ErrorBytes}");
                        string copy = g.NewLabel("caught");
                        Label(copy);
                        E("ldrb w12, [x10], #1");
                        E("strb w12, [x9], #1");
                        E("subs x11, x11, #1");
                        E($"b.ne {copy}");
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Whether the method put itself on the unwind list: then its _unwind routine is written after it.</summary>
            bool unwinds;

            /// <summary>
            /// A routine's arguments, PHI's way: at [sp], the last lowest, 8 bytes each (an operand
            /// of long.MinValue is what's in x9 already: an address worked out first).
            /// </summary>
            void Arguments(List<Opnd> args)
            {
                for (int k = 0; k < args.Count; k++)
                {
                    Opnd a = args[k];
                    string r = a.IsImm && a.Imm == long.MinValue ? "x9" : GetOp(a, "x10", true);
                    Mem("str", r, "sp", 8 * (args.Count - 1 - k), 8);
                }
            }

            void GrowStore()
            {
                g.Need("phi_grow_store");
                g.Heap("Heap.Alloc");
                g.Heap("Heap.Free");
                E("bl phi_grow_store");
            }

            /// <summary>
            /// What a throw on its way past calls, with x29 at this method's frame (throw.s): its
            /// objects and texts freed, as the end of the method frees them.
            /// </summary>
            void EmitUnwind()
            {
                if (!unwinds) return;
                g.Line("");
                g.Line("\t.p2align 2");
                Label($"{methodLabel}_unwind");
                E("stp x30, xzr, [sp, #-32]!     ; ([sp + 24]: a count)");
                void Call(string routine, string reg)
                {
                    // (PHI's way: its argument at [sp])
                    E("sub sp, sp, #16");
                    E($"str {reg}, [sp]");
                    E($"bl {routine}");
                    E("add sp, sp, #16");
                }
                foreach (var (home, s) in fn.Owned)
                {
                    var (b, off) = Place(home);
                    Mem("ldr", "x9", b, off, 8);
                    Call(g.ObjectRoutine("free", s), "x9");
                }
                foreach (string slot in fn.OwnedTexts)
                {
                    var (b, off) = Place(slot);
                    Mem("ldr", "x9", b, off, 8);
                    Call(g.Heap("Heap.Free"), "x9");
                }
                foreach (var (slot, room) in fn.OwnedTableTexts)
                {
                    // an element that moved to the heap (one still in its own buffer stays)
                    string own = g.NewLabel("unwindown");
                    var (rb, ro) = Place(room);
                    Mem("ldr", "w9", rb, ro, 4);
                    E($"tbnz w9, #31, {own}");
                    var (b, off) = Place(slot);
                    Mem("ldr", "x9", b, off, 8);
                    Call(g.Heap("Heap.Free"), "x9");
                    Label(own);
                }
                foreach (var (view, objects) in fn.OwnedViews)
                {
                    // each element's object (up to its room) or heap text (up to how many), then the room
                    string next = g.NewLabel("unwindview"), done = g.NewLabel("unwoundview"), kept = g.NewLabel("unwindkept");
                    var (vb, vo) = Place(view);
                    E("str xzr, [sp, #24]");
                    Label(next);
                    E("ldr x11, [sp, #24]");
                    AddImm("x12", vb, vo);
                    E($"ldr w13, [x12, #{(objects != null ? 12 : 8)}]");
                    E("cmp x11, x13");
                    E($"b.hs {done}");
                    E("ldr x14, [x12]");
                    if (objects != null)
                    {
                        E("add x15, x14, x11, lsl #3");
                        E("ldr x9, [x15]");
                        E("str xzr, [x15]");
                        Call(g.ObjectRoutine("free", objects), "x9");
                    }
                    else
                    {
                        E("ldr w13, [x12, #12]");
                        E("add x15, x14, x13, lsl #3");
                        E("add x15, x15, x11, lsl #3");
                        E("ldr w13, [x15]");
                        E($"tbnz w13, #31, {kept}");
                        E("ldr x9, [x14, x11, lsl #3]");
                        Call(g.Heap("Heap.Free"), "x9");
                        Label(kept);
                    }
                    E("ldr x11, [sp, #24]");
                    E("add x11, x11, #1");
                    E("str x11, [sp, #24]");
                    E($"b {next}");
                    Label(done);
                    AddImm("x12", vb, vo);
                    E("ldr x9, [x12]");
                    Call(g.Heap("Heap.Free"), "x9");
                }
                E("ldp x30, xzr, [sp], #32");
                E("ret");
            }

            // ---------------------------------------------------------------- outside functions

            static bool IsFloatArg(CallI call, int k) =>
                k < call.ArgKinds.Count && call.ArgKinds[k] is MemKind.F64 or MemKind.F32 || !call.Args[k].IsImm && call.Args[k].Reg!.Float;

            /// <summary>
            /// Where each of an outside call's values goes, as Apple's arm64 convention has it: whole
            /// numbers and addresses in x0-x7, floats in d0-d7 (s for an f32), in order; past those,
            /// and every value of C's variadic part, on the stack at sp, 8 bytes each. The register's
            /// number, or -1 - the stack slot.
            /// </summary>
            static List<int> OutsidePlaces(CallI call)
            {
                var places = new List<int>();
                int ints = 0, floats = 0, stack = 0;
                for (int k = 0; k < call.Args.Count; k++)
                {
                    bool variadic = call.VariadicFrom is int from && k >= from;
                    bool f = IsFloatArg(call, k);
                    if (!variadic && f && floats < 8) places.Add(floats++);
                    else if (!variadic && !f && ints < 8) places.Add(ints++);
                    else places.Add(-1 - stack++);
                }
                return places;
            }

            static int OutsideStack(CallI call) => OutsidePlaces(call).Count(p => p < 0);

            void OutsideCall(CallI call)
            {
                List<int> places = OutsidePlaces(call);
                // the stack's first (the registers' values may be in x9-x17, which those use)
                for (int k = 0; k < call.Args.Count; k++)
                {
                    if (places[k] >= 0) continue;
                    int at = 8 * (-1 - places[k]);
                    if (IsFloatArg(call, k))
                    {
                        string d = FGet(call.Args[k].Reg!, "d31");
                        bool variadic = call.VariadicFrom is int from && k >= from;
                        if (call.ArgKinds[k] == MemKind.F32 && !variadic)
                        {
                            E($"fcvt s31, {d}");
                            Mem("str", "s31", "sp", at, 4);
                        }
                        else Mem("str", d, "sp", at, 8);     // (a variadic float is a double, as C promotes it)
                        continue;
                    }
                    Mem("str", GetOp(call.Args[k], "x9", true), "sp", at, 8);
                }
                for (int k = 0; k < call.Args.Count; k++)
                {
                    if (places[k] < 0) continue;
                    if (IsFloatArg(call, k))
                    {
                        string d = FGet(call.Args[k].Reg!, "d31");
                        if (call.ArgKinds[k] == MemKind.F32) E($"fcvt s{places[k]}, {d}");
                        else if (d != $"d{places[k]}") E($"fmov d{places[k]}, {d}");
                        continue;
                    }
                    MemKind kind = k < call.ArgKinds.Count ? call.ArgKinds[k] : MemKind.Q64;
                    bool wide = kind is MemKind.Q64 || !call.Args[k].IsImm && call.Args[k].Reg!.Wide;
                    Into($"x{places[k]}", call.Args[k], wide);
                    // (a signed byte or half: C's callee reads it as its 32 bits)
                    if (kind == MemKind.S8) E($"sxtb w{places[k]}, w{places[k]}");
                    else if (kind == MemKind.S16) E($"sxth w{places[k]}, w{places[k]}");
                }
                E($"bl {call.Label}");
                if (call.Dst == null) return;
                if (call.Result == CallResult.St0)
                {
                    string ft = FTarget(call.Dst);
                    if (call.ResultKind == MemKind.F32) E($"fcvt {ft}, s0");
                    else if (ft != "d0") E($"fmov {ft}, d0");
                    FPut(call.Dst, ft);
                    return;
                }
                // what a byte or a half gives is its low bits alone
                switch (call.ResultKind)
                {
                    case MemKind.U8: E("uxtb w0, w0"); break;
                    case MemKind.S8: E("sxtb w0, w0"); break;
                    case MemKind.U16: E("uxth w0, w0"); break;
                    case MemKind.S16: E("sxth w0, w0"); break;
                }
                Result(call.Dst);
            }

            void VecArgument(Opnd a, int at) =>
                throw new InvalidOperationException($"{methodLabel}: a vec given to a call, which the ARM code doesn't write yet");

            void VecResult(VReg dst) =>
                throw new InvalidOperationException($"{methodLabel}: a vec given back, which the ARM code doesn't write yet");

            void VecReturn(Opnd v) =>
                throw new InvalidOperationException($"{methodLabel}: a vec given back, which the ARM code doesn't write yet");
        }
    }
}

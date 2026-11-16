using Phi.Compiler.Syntax;

namespace Phi.Compiler.Semantics
{
    // vec<T>: several numbers worked on at once, by one instruction (Plan.md: "vec<T>: the
    // design"). The binder says what each use means; the intermediate form writes the code
    // (CodeGen/Ir), in 64-bit programs, and a method that uses a vec is always its.
    public sealed partial class Binder
    {
        /// <summary>A vec's own methods, and how many values each is given.</summary>
        static readonly Dictionary<string, int> VecMethods = new()
        {
            ["Min"] = 1, ["Max"] = 1, ["Abs"] = 0, ["Sqrt"] = 0,
            ["AddSat"] = 1, ["SubSat"] = 1, ["Average"] = 1,
            ["Sum"] = 0, ["Smallest"] = 0, ["Largest"] = 0,
            ["Less"] = 1, ["Equal"] = 1, ["Greater"] = 1,
            ["Pick"] = 2, ["Any"] = 0, ["All"] = 0,
            ["Widen"] = 0, ["WidenHigh"] = 0, ["Narrow"] = 1,
        };

        static IReadOnlyCollection<string> VecMethodNames => VecMethods.Keys;

        static int VecMethodTakes(string name) => VecMethods.GetValueOrDefault(name);

        /// <summary>value.Method, where value is a vec and Method one of its own.</summary>
        bool IsVecMethodName(string name, Scope scope)
        {
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || !VecMethods.ContainsKey(name[(dot + 1)..])) return false;
            PhiType? t = Lookup(name[..dot], scope) switch
            {
                VariableSymbol v => v.Type,
                FieldPathSymbol f => f.Type,
                SelfFieldSymbol s => s.Type,
                PathSymbol p => p.Type,
                _ => null,
            };
            return t is { IsVec: true };
        }

        /// <summary>
        /// vec&lt;f32&gt; v;  v: 2.0;  v: 1.0 2.0 3.0 4.0;  v: other;  its values made one VecExpr
        /// (zeros, one value in every lane, or one for each), or a vec of its kind.
        /// </summary>
        void BindVecDecl(VarDecl decl, VariableSymbol v, Scope scope, bool classLevel)
        {
            foreach (Expr value in decl.Values) BindExpr(value, scope);
            if (!classLevel || v.IsParameter) v.NeedsInitCode = true;
            if (decl.Values.Count == 0 || decl.Values.Any(x => x.Type.IsError)) return;
            if (classLevel && !v.IsParameter)
            {
                Error(decl.Values[0].Span, $"a class's vec starts as zeros; give '{decl.Name}' its lanes in a method ({decl.Name} is ...;)");
                return;
            }
            Expr made = MakeVec(v.Type, decl.Values, decl.NameSpan, $"'{decl.Name}'");
            decl.Values.Clear();
            decl.Values.Add(made);
        }

        /// <summary>A vec from what it's given: one value (a vec of its kind, or a number for every lane), or one for each lane.</summary>
        Expr MakeVec(PhiType vec, List<Expr> values, Span span, string what)
        {
            if (values.Count == 1) return ConvertVec(values[0], vec, what);
            if (values.Count != vec.Lanes)
            {
                Error(values[Math.Min(values.Count, vec.Lanes) - (values.Count > vec.Lanes ? 0 : 1)].Span,
                      $"{what} is {A(vec)}, which has {vec.Lanes} lanes here, and {values.Count} values were given; one value goes in every lane" +
                      " (a program that works on any width steps by .len rather than writing each lane)");
                return values[0];
            }
            var made = new VecExpr { Type = vec, Span = span };
            foreach (Expr value in values) made.Lanes.Add(Convert(value, vec.Lane!, what));
            return made;
        }

        /// <summary>
        /// Where a vec goes, or comes from: a vec of its kind as it is, a number made one in every
        /// lane; nothing else (another kind of vec needs `as`, and one lane is v:i).
        /// </summary>
        Expr ConvertVec(Expr e, PhiType target, string what)
        {
            if (e.Type == target) return e;
            if (target.IsVec && !e.Type.IsVec)
            {
                bool number = e.Type.IsNumeric && !e.Type.IsPointer && !e.Type.IsEnum || e.Type.IsWide || e.Type.IsFloat;
                if (!number)
                {
                    Error(e.Span, $"{what} is {A(target)}, and this is {A(e.Type)}");
                    return e;
                }
                // one number: in every lane
                var every = new VecExpr { Type = target, Span = e.Span };
                every.Lanes.Add(Convert(e, target.Lane!, what));
                return every;
            }
            if (target.IsVec)
            {
                string how = target.Lane!.IsFloat == e.Type.Lane!.IsFloat || target.Lanes == e.Type.Lanes
                    ? $"write ({Describe(e)} as {target})"
                    : "(v.Widen), (v.WidenHigh) and (low.Narrow: high) make lanes wider or narrower";
                Error(e.Span, $"{what} is {A(target)}, and this is {A(e.Type)}; {how}");
                return e;
            }
            Error(e.Span, $"{what} is {A(target)}, and this is {A(e.Type)}; for one lane, write {Describe(e)}:0, or one of its methods that gives a number, like ({Describe(e)}.Sum)");
            return e;
        }

        /// <summary>a + b, v * 2.0, v &lt;&lt; 3, a is b, where a side is a vec: lane by lane.</summary>
        void BindVecBinary(BinaryExpr b)
        {
            PhiType vec = b.Left.Type.IsVec ? b.Left.Type : b.Right.Type;
            bool whole = !vec.Lane!.IsFloat;
            b.Type = PhiType.Error;
            if (b.Exists)
            {
                Error(b.Span, "exists is for one number, an object or a pointer; (mask.Any) asks whether a mask has any lane set");
                return;
            }
            switch (b.Op)
            {
                case BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual:
                    Error(b.Span, "vectors are compared lane by lane with a method, which gives a mask: (a.Less: b), (a.Greater: b), (a.Equal: b); < and > are for one number and another");
                    return;
                case BinaryOp.And or BinaryOp.Or:
                    Error(b.Span, $"{(b.Op == BinaryOp.And ? "and" : "or")} is for blns; for each lane's bits, write {(b.Op == BinaryOp.And ? "&" : "|")}");
                    return;
                case BinaryOp.Modulo:
                    Error(b.Span, "a vec has no % (x86's vector instructions don't divide whole numbers); work it out a lane at a time");
                    return;
                case BinaryOp.Power:
                    Error(b.Span, "a vec has no **; multiply");
                    return;
                case BinaryOp.Divide when whole:
                    Error(b.Span, $"x86's vector instructions don't divide whole numbers, so {A(vec)} has no /: divide f32 or f64 lanes, a lane at a time, or shift (>> 1 halves)");
                    return;
                case BinaryOp.ShiftLeft or BinaryOp.ShiftRight:
                    if (!b.Left.Type.IsVec)
                    {
                        Error(b.Span, "a vector is shifted by a number (v << 2), not a number by a vector");
                        return;
                    }
                    if (!whole)
                    {
                        Error(b.Span, $"<< and >> shift whole numbers' bits, and {A(vec)}'s lanes are floats");
                        return;
                    }
                    if (b.Right.Type.IsVec)
                    {
                        Error(b.Right.Span, "every lane is shifted by the same count, one number (v << 2)");
                        return;
                    }
                    b.Right = Convert(b.Right, PhiType.Int, "the count");
                    b.Type = vec;
                    return;
            }
            b.Left = Convert(b.Left, vec, "the left side");
            b.Right = Convert(b.Right, vec, "the right side");
            if (b.Left.Type != vec || b.Right.Type != vec) return;
            b.Type = b.IsComparison ? PhiType.Bool : vec;
        }

        /// <summary>(a.Min: b), (v.Sum), (mask.Pick: x y): one of a vec's own methods, with what it's given.</summary>
        VecMethod? BindVecCall(Expr receiver, string name, List<Expr> arguments, Span span)
        {
            PhiType v = receiver.Type, lane = v.Lane!;
            if (!VecMethods.TryGetValue(name, out int takes))
            {
                Error(span, $"{A(v)} has no method {name}; its methods are {string.Join(", ", VecMethodNames.Order(StringComparer.Ordinal))}");
                return null;
            }
            if (arguments.Count != takes)
            {
                Error(span, $"{name} is given {Plural(takes, "value")}, and this gives it {arguments.Count}");
                return null;
            }
            if (arguments.Any(a => a.Type.IsError)) return null;
            bool whole = !lane.IsFloat;
            int bits = lane.ElementSize * 8;
            string? why = name switch
            {
                "Abs" when whole && !lane.IsSigned => $"{A(v)}'s lanes are never negative, so Abs would give them back as they are",
                "Sqrt" when whole => $"Sqrt is for f32 and f64 lanes, and these are {lane}s",
                "AddSat" or "SubSat" when !whole || bits > 16 => $"{name} stops at the ends of 8- and 16-bit lanes (as pixels need), and these are {lane}s",
                "Average" when lane.Kind is not (TypeKind.U8 or TypeKind.U16) => $"Average is for u8 and u16 lanes (rounding up, as blending pixels does), and these are {lane}s",
                "Widen" or "WidenHigh" when (whole ? bits > 32 : lane.Kind == TypeKind.F64) => $"{lane} lanes are as wide as lanes go",
                "Narrow" when (whole ? bits < 16 : lane.Kind == TypeKind.F32) => $"{lane} lanes are as narrow as lanes go",
                _ => null,
            };
            if (why != null)
            {
                Error(span, why);
                return null;
            }
            PhiType result = v;
            switch (name)
            {
                case "Pick":
                {
                    // mask.Pick: then otherwise: then and otherwise the same kind, as many lanes as the mask
                    PhiType kind = arguments[0].Type.IsVec ? arguments[0].Type : arguments[1].Type.IsVec ? arguments[1].Type : v;
                    arguments[0] = Convert(arguments[0], kind, "what's picked where the mask is set");
                    arguments[1] = Convert(arguments[1], kind, "what's picked where it isn't");
                    if (kind.Lanes != v.Lanes)
                    {
                        Error(span, $"a mask picks lane by lane: {A(v)} has {v.Lanes} lanes, and {A(kind)} {kind.Lanes}");
                        return null;
                    }
                    result = kind;
                    break;
                }
                case "Sum" or "Smallest" or "Largest":
                    result = lane;
                    break;
                case "Any" or "All":
                    result = PhiType.Bool;
                    break;
                case "Widen" or "WidenHigh":
                    result = PhiType.VecOf(Wider(lane));
                    break;
                case "Narrow":
                    arguments[0] = Convert(arguments[0], v, "the high half");
                    result = PhiType.VecOf(Narrower(lane));
                    break;
                default:
                    if (takes == 1) arguments[0] = Convert(arguments[0], v, name);
                    break;
            }
            arguments.Insert(0, receiver);
            return new VecMethod { Name = name, Vec = v, Result = result };
        }

        static PhiType Wider(PhiType lane) => lane.Kind switch
        {
            TypeKind.I8 => PhiType.I16,
            TypeKind.U8 => PhiType.U16,
            TypeKind.I16 => PhiType.I32,
            TypeKind.U16 => PhiType.U32,
            TypeKind.I32 => PhiType.I64,
            TypeKind.U32 => PhiType.U64,
            _ => PhiType.F64,
        };

        static PhiType Narrower(PhiType lane) => lane.Kind switch
        {
            TypeKind.I16 => PhiType.I8,
            TypeKind.U16 => PhiType.U8,
            TypeKind.I32 => PhiType.I16,
            TypeKind.U32 => PhiType.U16,
            TypeKind.I64 => PhiType.I32,
            TypeKind.U64 => PhiType.U32,
            _ => PhiType.F32,
        };

        /// <summary>
        /// v as vec&lt;T&gt;: whole numbers to whole numbers are the same 16 bytes seen as other
        /// lanes (as as keeps a number's bits); floats to whole numbers and back keep each lane's
        /// value (toward zero), so the number of lanes stays. Null when it can be.
        /// </summary>
        string? VecCastError(PhiType from, PhiType to)
        {
            if (!from.IsVec || !to.IsVec)
                return from.IsVec
                    ? $"as makes a {from} another kind of vec, not {A(to)}; for one lane, write v:0, or a method that gives one number, like (v.Sum)"
                    : $"{A(from)} goes into a {to} as it is (in every lane); as makes one vec another";
            bool fromWhole = !from.Lane!.IsFloat, toWhole = !to.Lane!.IsFloat;
            if (fromWhole && toWhole) return null;
            if (!fromWhole && !toWhole)
                return "(v.Widen) and (v.WidenHigh) make f64s of an f32 vec's low and high halves, and (low.Narrow: high) f32s of two f64 vecs";
            if (from.Lanes != to.Lanes)
                return $"as between floats and whole numbers keeps each lane's value, so the lanes stay as many: {A(from)} has {from.Lanes}, and {A(to)} {to.Lanes}";
            return null;
        }

        /// <summary>
        /// A class's own statements are the generator's, which has no vectors: a vec is used in
        /// methods (its class variables are kept there, as zeros, for them).
        /// </summary>
        void CheckNoVecAtClassLevel(Stmt s)
        {
            if (s is VarDecl { Symbol.Type.IsVec: true }) return;
            if (SyntaxWalker.Nodes(s).OfType<Expr>().FirstOrDefault(e => e.Type.IsVec) is Expr used)
                Error(used.Span, "vec works in methods for now, not in a class's own statements; put this in a method and call it");
        }
    }
}

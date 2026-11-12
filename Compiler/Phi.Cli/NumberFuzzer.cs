using System.Globalization;
using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// phi fuzz numbers: random PHI programs of 64-bit and floating point numbers, each written
    /// together with what it must print, as phi fuzz code's are of ints and u32s (CodeFuzzer runs
    /// them: it builds each for the systems this computer can run and compares).
    ///
    /// The programs are made of what a code generator does differently for these numbers: i64s,
    /// u64s and f64s in class variables, in a struct's fields, in arrays, and in the parameters
    /// and variables of methods; math among them and with ints and u32s (which become 64 bits
    /// or floating point by their own sign); shifts, constants of every size, casts between all
    /// of them; comparisons as conditions; loops whose counters are ints, with these in their
    /// bodies; and calls in the middle of expressions.
    ///
    /// What a program prints is worked out here in C#'s long, ulong and double, which is what PHI
    /// says its own are: 64-bit math wraps, and an f64 operation gives the nearest f64. A float
    /// is printed as its 64 bits, so every bit is compared. A program that would do what PHI
    /// stops for or leaves open is never written (no division by a variable, no index out of
    /// range), or is dropped when working it out finds it: a float that isn't a number, or one
    /// so large or small that where it's rounded could matter.
    /// </summary>
    public static class NumberFuzzer
    {
        /// <summary>The program for a seed and what it prints, or null if working it out went too long or too far.</summary>
        public static CodeFuzzer.Case? Make(int seed)
        {
            var maker = new Maker(new Random(seed));
            Tree tree = maker.MakeProgram();
            var machine = new Machine(tree);
            try
            {
                foreach (Stmt s in tree.Main) s.Run(machine, machine.Globals);
            }
            catch (Dropped)
            {
                return null;
            }
            return new CodeFuzzer.Case(tree.Print(), machine.Output.ToString());
        }

        // ================================================================ what a program is

        enum K { Int, U32, I64, U64, F64 }

        static bool Wide(K k) => k is K.I64 or K.U64;
        static bool Narrow(K k) => k is K.Int or K.U32;
        static string Name(K k) => k switch { K.Int => "int", K.U32 => "u32", K.I64 => "i64", K.U64 => "u64", _ => "f64" };

        /// <summary>A number's bits made 64, by its own sign: an int keeps its sign, a u32 stays positive.</summary>
        static ulong To64(K k, ulong bits) => k == K.Int ? (ulong)(long)(int)(uint)bits : k == K.U32 ? (uint)bits : bits;

        static double AsDouble(ulong bits) => BitConverter.UInt64BitsToDouble(bits);
        static ulong Bits(double value) => BitConverter.DoubleToUInt64Bits(value);

        sealed class Dropped : Exception { }

        sealed class Variable
        {
            public string Name = "";
            public K Kind;
            public bool Global;
            public int Slot;
            public ulong Start;
        }

        sealed class ArrayInfo
        {
            public string Name = "";
            public K Kind;
            public int Length;      // a power of two, so an index is masked into range
            public int Slot;
        }

        sealed class Method
        {
            public string Name = "";
            public K? Returns;      // null: it's called for what it does
            public List<Variable> Parameters = new();
            public List<Variable> Locals = new();
            public List<Stmt> Body = new();
            public Expr? Result;
            public int Slots;
        }

        sealed class Tree
        {
            public List<Variable> Globals = new();
            public List<ArrayInfo> Arrays = new();
            public List<Method> Methods = new();
            public List<Stmt> Main = new();

            public string Print()
            {
                var sb = new StringBuilder();
                sb.Append("# Made by phi fuzz numbers: what it prints is worked out as it's written.\n");
                sb.Append("struct.Rec\n{\n\ti64 big;\n\tu64 huge;\n\tf64 real;\n\tint small;\n}\n\n");
                sb.Append("phi.Fuzz:Program\n{\n");
                sb.Append("\tRec rec;\n");
                foreach (Variable v in Globals.Where(g => !g.Name.Contains('.') && !g.Name.StartsWith("idx")))
                    sb.Append($"\t{Name(v.Kind)} {v.Name}: {Literal(v.Kind, v.Start, bare: true)};\n");
                foreach (ArrayInfo a in Arrays) sb.Append($"\t{Name(a.Kind)} {a.Name}[{a.Length}];\n");
                sb.Append('\n');
                foreach (Stmt s in Main) s.Print(sb, 1);
                // an f64's 64 bits, as a number that's printed whole
                sb.Append("\n\t[Bits: f64 value: 0.0; : u64]\n\t\tf64 kept: value;\n\t\tptr<u64> bits: (addr kept) as ptr<u64>;\n\t[end: bits:0]\n");
                foreach (Method m in Methods)
                {
                    sb.Append($"\n\t[{m.Name}");
                    if (m.Parameters.Count > 0)
                        sb.Append(": ").Append(string.Join(" ", m.Parameters.Select(p => $"{Name(p.Kind)} {p.Name}: {(p.Kind == K.F64 ? "0.0" : "0")};")));
                    if (m.Returns is K returns) sb.Append($"{(m.Parameters.Count > 0 ? " " : "")}: {Name(returns)}");
                    sb.Append("]\n");
                    foreach (Variable v in m.Locals.Where(l => !l.Name.StartsWith("idx")))
                        sb.Append($"\t\t{Name(v.Kind)} {v.Name}: {Literal(v.Kind, v.Start, bare: true)};\n");
                    foreach (Stmt s in m.Body) s.Print(sb, 2);
                    sb.Append(m.Result != null ? $"\t[end: {m.Result.Print()}]\n" : "\t[end]\n");
                }
                sb.Append("}\n");
                return sb.ToString();
            }
        }

        /// <summary>
        /// A constant as PHI reads it. A negative one is written (0 - n), since -n after a space is
        /// a value of its own; a u64 past an i64's most is in hex; an f64 always has its point.
        /// </summary>
        static string Literal(K kind, ulong bits, bool bare = false)
        {
            string text;
            switch (kind)
            {
                case K.F64:
                {
                    double d = AsDouble(bits);
                    string digits = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
                    if (!digits.Contains('.') && !digits.Contains('E')) digits += ".0";
                    if (d >= 0 && !double.IsNegative(d)) return digits;
                    text = $"0.0 - {digits}";
                    break;
                }
                case K.U32: return ((uint)bits).ToString();
                case K.U64: return bits > long.MaxValue ? $"0x{bits:X}" : bits.ToString();
                case K.Int:
                    if ((int)(uint)bits >= 0) return ((uint)bits).ToString();
                    text = $"0 - {(uint)(-(long)(int)(uint)bits)}";
                    break;
                default:
                    if ((long)bits >= 0) return bits.ToString();
                    text = $"0 - {(ulong)(-(long)bits)}";
                    break;
            }
            return bare ? text : $"({text})";
        }

        // ================================================================ working a program out

        sealed class Machine
        {
            public readonly ulong[] Globals;
            public readonly ulong[][] Arrays;
            public readonly StringBuilder Output = new();
            public int Steps;

            public Machine(Tree tree)
            {
                Globals = new ulong[tree.Globals.Count];
                foreach (Variable v in tree.Globals) Globals[v.Slot] = v.Start;
                Arrays = tree.Arrays.Select(a => new ulong[a.Length]).ToArray();
            }

            public void Step()
            {
                if (++Steps > 200_000) throw new Dropped();
            }
        }

        /// <summary>
        /// A float PHI and C# agree on to the last bit: a number, and not so large or small that
        /// the FPU's wider range (it only rounds to an f64's when a value is stored) could show.
        /// </summary>
        static ulong Tame(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new Dropped();
            double size = Math.Abs(value);
            if (size > 1e60 || (size != 0 && size < 1e-60)) throw new Dropped();
            return Bits(value);
        }

        abstract class Expr
        {
            public K Kind;
            /// <summary>True when PHI gives it this kind whatever is around it: not a constant, whose type depends on its size.</summary>
            public virtual bool Typed => true;
            public abstract ulong Value(Machine m, ulong[] frame);
            public abstract string Print();
        }

        sealed class Constant : Expr
        {
            public ulong Number;
            public override bool Typed => false;
            public override ulong Value(Machine m, ulong[] frame) => Number;
            public override string Print() => Literal(Kind, Number);
        }

        sealed class Read : Expr
        {
            public Variable Variable = null!;
            public override ulong Value(Machine m, ulong[] frame) => Variable.Global ? m.Globals[Variable.Slot] : frame[Variable.Slot];
            public override string Print() => Variable.Name;
        }

        sealed class Element : Expr
        {
            public ArrayInfo Array = null!;
            public Expr Index = null!;
            public override ulong Value(Machine m, ulong[] frame) => m.Arrays[Array.Slot][Index.Value(m, frame) & (uint)(Array.Length - 1)];
            public override string Print() => $"{Array.Name}:({Index.Print()} & {Array.Length - 1})";
        }

        /// <summary>An operation in the kind it gives: each side is made that kind first, by its own sign.</summary>
        sealed class Binary : Expr
        {
            public string Op = "";
            public Expr Left = null!, Right = null!;
            public override ulong Value(Machine m, ulong[] frame)
            {
                m.Step();
                ulong a = Left.Value(m, frame), b = Right.Value(m, frame);
                if (Kind == K.F64)
                {
                    double x = Left.Kind == K.F64 ? AsDouble(a) : ToDouble(Left.Kind, a), y = Right.Kind == K.F64 ? AsDouble(b) : ToDouble(Right.Kind, b);
                    return Tame(Op switch { "+" => x + y, "-" => x - y, "*" => x * y, _ => x / y });
                }
                if (Wide(Kind))
                {
                    a = To64(Left.Kind, a);
                    // (a shift's count is a number of places, not a side made 64 bits)
                    if (Op is not ("<<" or ">>")) b = To64(Right.Kind, b);
                    return Op switch
                    {
                        "+" => unchecked(a + b),
                        "-" => unchecked(a - b),
                        "*" => unchecked(a * b),
                        "&" => a & b,
                        "|" => a | b,
                        "^" => a ^ b,
                        "<<" => a << (int)(b & 63),
                        ">>" => Kind == K.I64 ? (ulong)((long)a >> (int)(b & 63)) : a >> (int)(b & 63),
                        "/" => Kind == K.I64 ? (ulong)((long)a / (long)b) : a / b,
                        _ => Kind == K.I64 ? (ulong)((long)a % (long)b) : a % b,
                    };
                }
                uint p = (uint)a, q = (uint)b;
                return Op switch
                {
                    "+" => unchecked(p + q),
                    "-" => unchecked(p - q),
                    "*" => unchecked(p * q),
                    "&" => p & q,
                    "|" => p | q,
                    _ => p ^ q,
                };
            }
            public override string Print() => $"({Left.Print()} {Op} {Right.Print()})";
        }

        static double ToDouble(K kind, ulong bits) => kind switch
        {
            K.Int => (int)(uint)bits,
            K.U32 => (uint)bits,
            K.I64 => (long)bits,
            K.U64 => bits,
            _ => AsDouble(bits),
        };

        sealed class Unary : Expr
        {
            public string Op = "";      // -, ~
            public Expr Operand = null!;
            public override ulong Value(Machine m, ulong[] frame)
            {
                ulong a = Operand.Value(m, frame);
                if (Kind == K.F64) return Bits(-AsDouble(a));
                return Op == "-" ? unchecked(0 - a) : ~a;
            }
            public override string Print() => $"({Op}{Operand.Print()})";
        }

        /// <summary>(x as kind): made 64 bits by its own sign, cut to 32, or made an f64.</summary>
        sealed class As : Expr
        {
            public Expr Operand = null!;
            public override ulong Value(Machine m, ulong[] frame)
            {
                ulong a = Operand.Value(m, frame);
                if (Kind == K.F64) return Tame(ToDouble(Operand.Kind, a));
                if (Wide(Kind)) return To64(Operand.Kind, a);
                return (uint)a;
            }
            public override string Print() => $"({Operand.Print()} as {Name(Kind)})";
        }

        sealed class Call : Expr
        {
            public Method Method = null!;
            public List<Expr> Arguments = new();
            public override ulong Value(Machine m, ulong[] frame)
            {
                ulong[] callee = Enter(m, frame, Method, Arguments);
                foreach (Stmt s in Method.Body) s.Run(m, callee);
                return Method.Result!.Value(m, callee);
            }
            public override string Print() => $"({Method.Name}{PrintArguments(Arguments)})";
        }

        static ulong[] Enter(Machine m, ulong[] frame, Method method, List<Expr> arguments)
        {
            m.Step();
            var callee = new ulong[method.Slots];
            for (int i = 0; i < arguments.Count; i++) callee[method.Parameters[i].Slot] = arguments[i].Value(m, frame);
            foreach (Variable v in method.Locals) callee[v.Slot] = v.Start;
            return callee;
        }

        static string PrintArguments(List<Expr> arguments) =>
            arguments.Count == 0 ? "" : ": " + string.Join(" ", arguments.Select(a => Dotted(a) ? $"({a.Print()})" : a.Print()));

        /// <summary>An element or a field, which a list of values takes in parentheses.</summary>
        static bool Dotted(Expr e) => e is Element || e is Read read && read.Variable.Name.Contains('.');

        sealed class Condition
        {
            public string Op = "";
            public Expr Left = null!, Right = null!;
            public string? Joiner;      // and, or
            public Condition? Next;

            public bool Holds(Machine m, ulong[] frame)
            {
                m.Step();
                ulong a = Left.Value(m, frame), b = Right.Value(m, frame);
                int order;      // -1, 0, 1: the left is less, the same, more
                if (Left.Kind == K.F64) order = AsDouble(a).CompareTo(AsDouble(b));
                else if (Wide(Left.Kind) || Wide(Right.Kind))
                {
                    // as unsigned numbers when either is a u64
                    a = To64(Left.Kind, a);
                    b = To64(Right.Kind, b);
                    order = Left.Kind == K.U64 || Right.Kind == K.U64 ? a.CompareTo(b) : ((long)a).CompareTo((long)b);
                }
                else order = Left.Kind == K.Int ? ((int)(uint)a).CompareTo((int)(uint)b) : ((uint)a).CompareTo((uint)b);
                bool holds = Op switch
                {
                    "is" => order == 0,
                    "is not" => order != 0,
                    "<" => order < 0,
                    "<=" => order <= 0,
                    ">" => order > 0,
                    _ => order >= 0,
                };
                if (Next == null) return holds;
                return Joiner == "and" ? holds && Next.Holds(m, frame) : holds || Next.Holds(m, frame);
            }

            public string Print() => $"{Left.Print()} {Op} {Right.Print()}" + (Next != null ? $" {Joiner} {Next.Print()}" : "");
        }

        abstract class Stmt
        {
            public abstract void Run(Machine m, ulong[] frame);
            public abstract void Print(StringBuilder sb, int depth);
            protected static string Tabs(int depth) => new('\t', depth);
        }

        /// <summary>What's set: a variable (a field is one, by its whole name) or an array's element.</summary>
        sealed class Place
        {
            public K Kind;
            public Variable? Variable;
            public ArrayInfo? Array;
            public Expr? Index;

            public ulong Get(Machine m, ulong[] frame) =>
                Variable != null ? (Variable.Global ? m.Globals : frame)[Variable.Slot] : m.Arrays[Array!.Slot][Index!.Value(m, frame) & (uint)(Array.Length - 1)];

            public void Set(Machine m, ulong[] frame, ulong value)
            {
                if (Variable != null) (Variable.Global ? m.Globals : frame)[Variable.Slot] = value;
                else m.Arrays[Array!.Slot][Index!.Value(m, frame) & (uint)(Array.Length - 1)] = value;
            }

            public string Print() => Variable != null ? Variable.Name : $"{Array!.Name}:({Index!.Print()} & {Array.Length - 1})";
        }

        /// <summary>place is value;  place++ value;  place-- value;  place** value;</summary>
        sealed class Assign : Stmt
        {
            public Place Target = null!;
            public string Op = "is";
            public Expr Value = null!;
            public override void Run(Machine m, ulong[] frame)
            {
                m.Step();
                if (Op == "is")
                {
                    // (the value first, then where it goes)
                    ulong value = Convert(Value.Kind, Target.Kind, Value.Value(m, frame));
                    Target.Set(m, frame, value);
                    return;
                }
                // x++ v is x is x + v
                var sum = new Binary { Kind = Target.Kind, Op = Op[..1], Left = new Held { Kind = Target.Kind, Place = Target }, Right = Value };
                Target.Set(m, frame, sum.Value(m, frame));
            }
            public override void Print(StringBuilder sb, int depth) =>
                sb.Append(Tabs(depth)).Append(Op == "is" ? $"{Target.Print()} is {Value.Print()};\n" : $"{Target.Print()}{Op} {Value.Print()};\n");
        }

        /// <summary>What a place holds, for working out x++ v.</summary>
        sealed class Held : Expr
        {
            public Place Place = null!;
            public override ulong Value(Machine m, ulong[] frame) => Place.Get(m, frame);
            public override string Print() => Place.Print();
        }

        /// <summary>A value put where another kind is kept, as PHI does it without being asked: a smaller number into a 64-bit one.</summary>
        static ulong Convert(K from, K to, ulong bits) => Wide(to) ? To64(from, bits) : bits;

        sealed class If : Stmt
        {
            public Condition Condition = null!;
            public List<Stmt> Then = new();
            public List<Stmt>? Else;
            public override void Run(Machine m, ulong[] frame)
            {
                if (Condition.Holds(m, frame)) foreach (Stmt s in Then) s.Run(m, frame);
                else if (Else != null) foreach (Stmt s in Else) s.Run(m, frame);
            }
            public override void Print(StringBuilder sb, int depth)
            {
                sb.Append(Tabs(depth)).Append($"if {Condition.Print()}\n");
                foreach (Stmt s in Then) s.Print(sb, depth + 1);
                sb.Append(Tabs(depth)).Append(";;\n");
                if (Else == null) return;
                sb.Append(Tabs(depth)).Append("else\n");
                foreach (Stmt s in Else) s.Print(sb, depth + 1);
                sb.Append(Tabs(depth)).Append(";;\n");
            }
        }

        /// <summary>while int counter: 0; counter &lt; times; counter++; ... ;;</summary>
        sealed class Loop : Stmt
        {
            public Variable Counter = null!;
            public int Times;
            public List<Stmt> Body = new();
            public override void Run(Machine m, ulong[] frame)
            {
                ulong[] where = Counter.Global ? m.Globals : frame;
                for (where[Counter.Slot] = 0; (int)where[Counter.Slot] < Times; where[Counter.Slot]++)
                {
                    m.Step();
                    foreach (Stmt s in Body) s.Run(m, frame);
                }
            }
            public override void Print(StringBuilder sb, int depth)
            {
                sb.Append(Tabs(depth)).Append($"while int {Counter.Name}: 0; {Counter.Name} < {Times}; {Counter.Name}++;\n");
                foreach (Stmt s in Body) s.Print(sb, depth + 1);
                sb.Append(Tabs(depth)).Append(";;\n");
            }
        }

        /// <summary>log values, each a number as PHI writes it (an f64 by its bits).</summary>
        sealed class Log : Stmt
        {
            public List<Expr> Values = new();
            public override void Run(Machine m, ulong[] frame)
            {
                m.Step();
                for (int i = 0; i < Values.Count; i++)
                {
                    ulong v = Values[i].Value(m, frame);
                    if (i > 0) m.Output.Append(' ');
                    m.Output.Append(Values[i].Kind switch
                    {
                        K.Int => ((int)(uint)v).ToString(),
                        K.U32 => ((uint)v).ToString(),
                        K.I64 => ((long)v).ToString(),
                        _ => v.ToString(),      // a u64, and an f64's bits
                    });
                }
                m.Output.Append('\n');
            }
            public override void Print(StringBuilder sb, int depth)
            {
                sb.Append(Tabs(depth)).Append("log");
                for (int i = 0; i < Values.Count; i++)
                {
                    Expr v = Values[i];
                    string text = v.Kind == K.F64 ? $"(Bits: {(Dotted(v) ? $"({v.Print()})" : v.Print())})"
                                : v is Read ? v.Print() : $"({v.Print()})";
                    sb.Append(i > 0 ? " ' ' " : " ").Append(text);
                }
                sb.Append(" '\\n';\n");
            }
        }

        /// <summary>call Name: arguments;</summary>
        sealed class CallStmt : Stmt
        {
            public Method Method = null!;
            public List<Expr> Arguments = new();
            public override void Run(Machine m, ulong[] frame)
            {
                ulong[] callee = Enter(m, frame, Method, Arguments);
                foreach (Stmt s in Method.Body) s.Run(m, callee);
            }
            public override void Print(StringBuilder sb, int depth) =>
                sb.Append(Tabs(depth)).Append($"call {Method.Name}{PrintArguments(Arguments)};\n");
        }

        // ================================================================ writing a program

        sealed class Maker
        {
            readonly Random random;
            readonly Tree tree = new();
            int counters;

            // what the method being written can use
            Method? method;         // null: the class's own code
            bool pure;              // a method that gives a number back changes only its own variables
            readonly List<Variable> counterStack = new();

            public Maker(Random random) => this.random = random;

            int Next(int n) => random.Next(n);
            bool Chance(int percent) => random.Next(100) < percent;
            T From<T>(IReadOnlyList<T> list) => list[random.Next(list.Count)];
            T Pick<T>(params T[] list) => list[random.Next(list.Length)];

            static readonly ulong[] WideEdges =
            {
                0, 1, 2, 3, 7, 10, 255, 65535, 65536, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x100000000, 0x100000001,
                5000000000, 0x7FFFFFFFFFFFFFFF, 0x123456789ABCDEF, 1000000007,
            };

            static readonly double[] Floats = { 0.5, 1.5, 2.0, 0.25, 3.0, 10.0, 0.1, 7.0, 1.0, 0.75, 100.0, 0.125, 2.5, 1000.0, 0.3 };

            public Tree MakeProgram()
            {
                // class variables, a few of each kind
                foreach (K kind in new[] { K.Int, K.Int, K.U32, K.I64, K.I64, K.I64, K.U64, K.U64, K.F64, K.F64, K.F64 })
                    tree.Globals.Add(new Variable { Name = $"g{tree.Globals.Count}", Kind = kind, Global = true, Slot = tree.Globals.Count, Start = ConstantBits(kind, start: true) });
                // the fields of rec, which are variables by their whole names
                foreach (var (name, kind) in new[] { ("rec.big", K.I64), ("rec.huge", K.U64), ("rec.real", K.F64), ("rec.small", K.Int) })
                    tree.Globals.Add(new Variable { Name = name, Kind = kind, Global = true, Slot = tree.Globals.Count });
                foreach (K kind in new[] { K.I64, K.U64, K.F64 })
                    tree.Arrays.Add(new ArrayInfo { Name = $"a{tree.Arrays.Count}", Kind = kind, Length = Pick(4, 8), Slot = tree.Arrays.Count });

                // methods that give a number back, each calling only those before it; then some that do things
                for (int i = 0; i < 4; i++) MakeMethod(Pick(K.I64, K.I64, K.U64, K.F64, K.F64, K.Int));
                for (int i = 0; i < 2; i++) MakeMethod(null);

                method = null;
                pure = false;
                counterStack.Clear();
                tree.Main.AddRange(Block(2, 8 + Next(6)));
                foreach (Method m in tree.Methods.Where(m => m.Returns == null))
                    tree.Main.Add(new CallStmt { Method = m, Arguments = m.Parameters.Select(p => Value(p.Kind, 2)).ToList() });
                tree.Main.AddRange(Block(2, 3 + Next(4)));
                // everything at the end
                foreach (Variable v in tree.Globals.Where(g => !g.Name.StartsWith("idx")).ToList()) tree.Main.Add(new Log { Values = { new Read { Kind = v.Kind, Variable = v } } });
                foreach (ArrayInfo a in tree.Arrays)
                    for (int i = 0; i < a.Length; i++)
                        tree.Main.Add(new Log { Values = { new Element { Kind = a.Kind, Array = a, Index = new Constant { Kind = K.Int, Number = (ulong)i } } } });
                return tree;
            }

            void MakeMethod(K? returns)
            {
                var m = new Method { Name = $"{(returns != null ? "F" : "Do")}{tree.Methods.Count}", Returns = returns };
                foreach (K kind in Enumerable.Range(0, 1 + Next(3)).Select(_ => Pick(K.Int, K.U32, K.I64, K.I64, K.U64, K.F64, K.F64)))
                    m.Parameters.Add(new Variable { Name = $"p{m.Slots}", Kind = kind, Slot = m.Slots++ });
                foreach (K kind in Enumerable.Range(0, 2 + Next(4)).Select(_ => Pick(K.Int, K.U32, K.I64, K.I64, K.U64, K.F64, K.F64)))
                    m.Locals.Add(new Variable { Name = $"v{m.Slots}", Kind = kind, Slot = m.Slots++, Start = ConstantBits(kind, start: true) });
                method = m;
                pure = returns != null;
                counterStack.Clear();
                m.Body.AddRange(Block(2, 3 + Next(5)));
                if (returns is K kind2) m.Result = Value(kind2, 2);
                method = null;
                tree.Methods.Add(m);
            }

            // ---------------------------------------------------------------- statements

            List<Stmt> Block(int depth, int count)
            {
                var list = new List<Stmt>();
                for (int i = 0; i < count; i++) list.Add(Statement(depth));
                return list;
            }

            Stmt Statement(int depth)
            {
                int roll = Next(100);
                if (depth > 0 && roll < 14)
                {
                    var i = new If { Condition = MakeCondition(), Then = Block(depth - 1, 1 + Next(3)) };
                    if (Chance(50)) i.Else = Block(depth - 1, 1 + Next(2));
                    return i;
                }
                if (depth > 0 && roll < 26)
                {
                    var counter = NewCounter();
                    var loop = new Loop { Counter = counter, Times = 2 + Next(5) };
                    counterStack.Add(counter);
                    loop.Body = Block(depth - 1, 1 + Next(3));
                    counterStack.RemoveAt(counterStack.Count - 1);
                    return loop;
                }
                if (!pure && roll < 38)
                {
                    var log = new Log();
                    for (int n = 1 + Next(3); n > 0; n--) log.Values.Add(Value(Pick(K.I64, K.I64, K.U64, K.F64, K.F64, K.Int, K.U32), 2));
                    return log;
                }
                Place target = MakePlace();
                string op = Chance(70) ? "is" : Pick("++", "--", "**");
                Expr value = Value(target.Kind, 3);
                // (x** a constant alone is fine; so is x is a constant)
                return new Assign { Target = target, Op = op, Value = value };
            }

            Variable NewCounter()
            {
                var v = new Variable { Name = $"idx{counters++}", Kind = K.Int, Global = method == null };
                if (method == null)
                {
                    v.Slot = tree.Globals.Count;
                    tree.Globals.Add(v);
                }
                else
                {
                    v.Slot = method.Slots++;
                    method.Locals.Add(v);
                }
                return v;
            }

            /// <summary>Somewhere to store: one of the method's own variables, or (when it may) a class variable, a field or an element.</summary>
            Place MakePlace()
            {
                var own = (method?.Locals ?? new List<Variable>()).Concat(method?.Parameters ?? new List<Variable>()).Where(v => !v.Name.StartsWith("idx")).ToList();
                if (pure || (own.Count > 0 && Chance(45)))
                {
                    Variable v = From(own);
                    return new Place { Kind = v.Kind, Variable = v };
                }
                if (Chance(30))
                {
                    ArrayInfo a = From(tree.Arrays);
                    return new Place { Kind = a.Kind, Array = a, Index = Value(Pick(K.Int, K.U32), 1) };
                }
                Variable g = From(tree.Globals.Where(v => !v.Name.StartsWith("idx")).ToList());
                return new Place { Kind = g.Kind, Variable = g };
            }

            Condition MakeCondition()
            {
                var c = new Condition { Op = Pick("is", "is not", "<", "<=", ">", ">=") };
                K kind = Pick(K.I64, K.I64, K.U64, K.F64, K.F64, K.Int, K.U32);
                c.Left = Typed(kind, 2);
                if (kind == K.F64) c.Right = Chance(30) ? FloatConstant() : Value(K.F64, 2);
                else if (Wide(kind))
                {
                    // against its own kind, a smaller number, or a constant of any size
                    int roll = Next(100);
                    c.Right = roll < 40 ? Value(kind, 2) : roll < 65 ? Typed(Pick(K.Int, K.U32), 1) : WideConstant(kind);
                }
                else c.Right = Chance(50) ? Value(kind, 1) : NarrowConstant(kind);
                if (Chance(25))
                {
                    c.Joiner = Pick("and", "or");
                    c.Next = MakeCondition();
                    c.Next.Next = null;
                    c.Next.Joiner = null;
                }
                return c;
            }

            // ---------------------------------------------------------------- values

            /// <summary>A value of a kind: an expression, or a constant.</summary>
            Expr Value(K kind, int depth)
            {
                if (Chance(12)) return kind == K.F64 ? FloatConstant() : Wide(kind) ? WideConstant(kind) : NarrowConstant(kind);
                return Typed(kind, depth);
            }

            /// <summary>A value PHI gives this kind by itself: never a constant alone.</summary>
            Expr Typed(K kind, int depth)
            {
                if (depth <= 0 || Chance(22)) return Leaf(kind);
                return kind == K.F64 ? FloatExpr(depth) : Wide(kind) ? WideExpr(kind, depth) : NarrowExpr(kind, depth);
            }

            Expr Leaf(K kind)
            {
                var readable = new List<Variable>();
                if (method != null) readable.AddRange(method.Parameters.Concat(method.Locals).Where(v => v.Kind == kind && !v.Name.StartsWith("idx")));
                readable.AddRange(tree.Globals.Where(v => v.Kind == kind && !v.Name.StartsWith("idx")));
                // (a loop's counter, for an int)
                if (kind == K.Int && counterStack.Count > 0 && Chance(40)) return new Read { Kind = K.Int, Variable = From(counterStack) };
                ArrayInfo? array = tree.Arrays.FirstOrDefault(a => a.Kind == kind);
                if (array != null && Chance(25))
                    return new Element { Kind = kind, Array = array, Index = counterStack.Count > 0 && Chance(50) ? new Read { Kind = K.Int, Variable = From(counterStack) } : Leaf(Pick(K.Int, K.U32)) };
                Variable v = From(readable);
                return new Read { Kind = kind, Variable = v };
            }

            Expr WideExpr(K kind, int depth)
            {
                int roll = Next(100);
                if (roll < 45)
                {
                    // one side is this kind; the other is too, or a smaller number, or a constant
                    string op = Pick("+", "-", "*", "&", "|", "^", "+", "-", "*");
                    Expr typed = Typed(kind, depth - 1);
                    int other = Next(100);
                    Expr second = other < 45 ? Typed(kind, depth - 1) : other < 70 ? Typed(Pick(K.Int, K.U32), depth - 1) : WideConstant(kind);
                    return Chance(50) ? new Binary { Kind = kind, Op = op, Left = typed, Right = second } : new Binary { Kind = kind, Op = op, Left = second, Right = typed };
                }
                if (roll < 62)
                {
                    // a shift: by a constant, or by a number held under 64
                    Expr count = Chance(60)
                        ? new Constant { Kind = K.Int, Number = (ulong)Next(64) }
                        : new Binary { Kind = K.Int, Op = "&", Left = Typed(K.Int, 1), Right = new Constant { Kind = K.Int, Number = 63 } };
                    return new Binary { Kind = kind, Op = Pick("<<", ">>"), Left = Typed(kind, depth - 1), Right = count };
                }
                if (roll < 72)
                {
                    // divided by a constant that isn't nothing (and is positive: the least i64 over -1 has no answer)
                    ulong by = Pick<ulong>(1, 2, 3, 7, 10, 1000, 65536, 1000000007, 0x100000000, 5000000000);
                    return new Binary { Kind = kind, Op = Pick("/", "%"), Left = Typed(kind, depth - 1), Right = new Constant { Kind = kind, Number = by } };
                }
                if (roll < 80) return new Unary { Kind = kind, Op = Pick("-", "~"), Operand = Typed(kind, depth - 1) };
                if (roll < 92)
                {
                    // from the other 64-bit kind, or a smaller number made 64 bits
                    K from = Pick(kind == K.I64 ? K.U64 : K.I64, K.Int, K.U32);
                    return new As { Kind = kind, Operand = Typed(from, depth - 1) };
                }
                return MakeCall(kind, depth) ?? Leaf(kind);
            }

            Expr NarrowExpr(K kind, int depth)
            {
                int roll = Next(100);
                if (roll < 50)
                {
                    Expr typed = Typed(kind, depth - 1);
                    Expr second = Chance(60) ? Typed(kind, depth - 1) : NarrowConstant(kind);
                    string op = Pick("+", "-", "*", "&", "|", "^");
                    return Chance(50) ? new Binary { Kind = kind, Op = op, Left = typed, Right = second } : new Binary { Kind = kind, Op = op, Left = second, Right = typed };
                }
                if (roll < 85)
                {
                    // the low 32 bits of a 64-bit number, or the same bits as the other kind
                    K from = Pick(K.I64, K.U64, kind == K.Int ? K.U32 : K.Int);
                    return new As { Kind = kind, Operand = Typed(from, depth - 1) };
                }
                return MakeCall(kind, depth) ?? Leaf(kind);
            }

            Expr FloatExpr(int depth)
            {
                int roll = Next(100);
                if (roll < 62)
                {
                    // one side is an f64; the other is too, or a whole number, or a constant
                    string op = Pick("+", "-", "*", "/", "+", "-", "*");
                    Expr typed = Typed(K.F64, depth - 1);
                    int other = Next(100);
                    Expr second = other < 55 ? Typed(K.F64, depth - 1) : other < 70 ? Typed(Pick(K.Int, K.U32), depth - 1) : FloatConstant();
                    // (mostly divided by a constant: by a variable it's often by nothing, and the program is dropped)
                    if (op == "/" && Chance(75)) return new Binary { Kind = K.F64, Op = op, Left = typed, Right = FloatConstant() };
                    return Chance(50) ? new Binary { Kind = K.F64, Op = op, Left = typed, Right = second } : new Binary { Kind = K.F64, Op = op, Left = second, Right = typed };
                }
                if (roll < 70) return new Unary { Kind = K.F64, Op = "-", Operand = Typed(K.F64, depth - 1) };
                if (roll < 86) return new As { Kind = K.F64, Operand = Typed(Pick(K.Int, K.U32, K.I64, K.I64, K.U64), depth - 1) };
                return MakeCall(K.F64, depth) ?? Leaf(K.F64);
            }

            Expr? MakeCall(K kind, int depth)
            {
                // (only the methods written before this one, so none calls itself)
                List<Method> callable = tree.Methods.Where(m => m.Returns == kind).ToList();
                if (callable.Count == 0) return null;
                Method m = From(callable);
                return new Call { Kind = kind, Method = m, Arguments = m.Parameters.Select(p => Value(p.Kind, Math.Min(depth - 1, 1))).ToList() };
            }

            // ---------------------------------------------------------------- constants

            ulong ConstantBits(K kind, bool start = false) => kind switch
            {
                K.F64 => Bits(From(Floats) * (!start && Chance(30) ? -1 : 1)),
                K.Int => Chance(start ? 20 : 30) ? (uint)(-(1 + Next(1000))) : (uint)Next(Chance(50) ? 100 : 0x7FFFFFFF),
                K.U32 => Chance(50) ? (uint)Next(1000) : (uint)random.NextInt64(0, 0x100000000),
                K.U64 => Chance(60) ? From(WideEdges) : Chance(50) ? (ulong)random.NextInt64() : 0xFFFFFFFF00000000 | (uint)Next(1000),
                _ => Chance(55) ? From(WideEdges) : Chance(50) ? (ulong)random.NextInt64() : (ulong)(-random.NextInt64(1, 0x7FFFFFFFFFFFFFFF)),
            };

            Expr FloatConstant() => new Constant { Kind = K.F64, Number = ConstantBits(K.F64) };
            Expr WideConstant(K kind) => new Constant { Kind = kind, Number = ConstantBits(kind) };

            /// <summary>For an int, a constant an int holds (PHI reads a bigger one as a u32, which would compare as one).</summary>
            Expr NarrowConstant(K kind) => new Constant { Kind = kind, Number = kind == K.Int ? (ulong)(uint)Next(Chance(50) ? 100 : 0x7FFFFFFF) : ConstantBits(K.U32) };
        }
    }
}

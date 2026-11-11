using System.Text;
using Phi.Compiler;

namespace Phi.Cli
{
    /// <summary>
    /// phi fuzz code: random PHI programs, each written together with what it must print. The
    /// fuzzer works a program out itself as it writes it (32-bit math that wraps, as PHI's is),
    /// builds it for each system this computer can run, and compares. A difference is a bug in
    /// the code generator (or in this file's idea of the language), and the program is kept.
    ///
    /// The programs are made of what the optimizer works on: ints and u32s in class variables,
    /// parameters and variables of methods, arrays of ints, u32s and bytes (by index and through
    /// pointers), math with constants and with each other (division by every kind of constant),
    /// comparisons, ifs, loops in loops (in a class's own code and in methods), and calls. They
    /// never do what PHI stops for: indexes are in range, nothing divides by zero, and a number
    /// only changes type through `as`.
    /// </summary>
    public static class CodeFuzzer
    {
        public sealed record Options(int Count, int Seed, IReadOnlyList<Host> Hosts, string KeepFolder, bool KeepAll);

        /// <summary>Runs the programs; returns how many came out wrong.</summary>
        public static int Run(Options options, TextWriter report)
        {
            string work = Path.Combine(Path.GetTempPath(), "phi-fuzz-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            int wrong = 0, ran = 0, skipped = 0;
            try
            {
                for (int i = 0; ran < options.Count; i++)
                {
                    int seed = unchecked(options.Seed + i);
                    Case? made = Make(seed);
                    if (made == null) { skipped++; continue; }   // it ran too long to be worth building
                    ran++;

                    string file = Path.Combine(work, $"fuzz_{(uint)seed}.phi");
                    File.WriteAllText(file, made.Source);
                    var problems = new List<string>();
                    foreach (Host host in options.Hosts)
                    {
                        string? problem = Check(file, made.Expected, host, Path.Combine(work, $"{(uint)seed}.{host.FlagName()}"));
                        if (problem != null) problems.Add($"{host.FlagName()}: {problem}");
                    }
                    if (problems.Count > 0 || options.KeepAll)
                    {
                        Directory.CreateDirectory(options.KeepFolder);
                        string kept = Path.Combine(options.KeepFolder, Path.GetFileName(file));
                        File.WriteAllText(kept, made.Source);
                        File.WriteAllText(Path.ChangeExtension(kept, ".expected"), made.Expected);
                        if (problems.Count > 0)
                        {
                            wrong++;
                            report.WriteLine($"FAIL seed {seed} (kept as {kept})");
                            foreach (string p in problems) report.WriteLine("     " + p.Replace("\n", "\n     "));
                        }
                    }
                    if (ran % 25 == 0) report.WriteLine($"{ran} programs, {wrong} wrong");
                }
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { }
            }
            report.WriteLine($"{ran} programs on {string.Join(", ", options.Hosts.Select(h => h.FlagName()))}: {wrong} wrong" +
                             (skipped > 0 ? $" ({skipped} left out: too long to run)" : ""));
            return wrong;
        }

        static string? Check(string file, string expected, Host host, string outDir)
        {
            BuildResult build;
            try
            {
                build = Builder.Build(file, outDir, host: host);
            }
            catch (Exception e) when (e is InvalidOperationException or NullReferenceException or ArgumentException or KeyNotFoundException)
            {
                return "the compiler stopped: " + e.Message;
            }
            if (!build.Success) return "it didn't build:\n" + string.Join("\n", build.Errors.Take(6));

            HostRunner.Result run = HostRunner.Run(build.ExecutablePath!, host, TimeSpan.FromSeconds(20));
            if (run.TimedOut) return "it didn't end in 20 s";
            string actual = run.Output.Replace("\r\n", "\n");
            if (actual == expected) return null;

            string[] want = expected.Split('\n'), got = actual.Split('\n');
            int line = 0;
            while (line < want.Length && line < got.Length && want[line] == got[line]) line++;
            return $"line {line + 1} of its output: expected '{(line < want.Length ? want[line] : "(the end)")}', got '{(line < got.Length ? got[line] : "(the end)")}'";
        }

        public sealed record Case(string Source, string Expected);

        /// <summary>The program for a seed and what it prints, or null if working it out took too many steps.</summary>
        public static Case? Make(int seed)
        {
            var maker = new Maker(new Random(seed));
            ProgramTree tree = maker.MakeProgram();
            var machine = new Machine(tree);
            try
            {
                foreach (Stmt s in tree.Main) s.Run(machine, machine.Globals);
            }
            catch (TooLong)
            {
                return null;
            }
            return new Case(tree.Print(), machine.Output.ToString());
        }

        // ================================================================ what a program is

        enum Kind { Int, U32 }
        enum ArrayKind { Int, U32, U8 }

        sealed class TooLong : Exception { }
        sealed class Break : Exception { }

        sealed class Variable
        {
            public string Name = "";
            public Kind Kind;
            public bool Global;
            public int Slot;
            public uint Start;          // a class variable's first value
            public bool Own;            // a class variable only the class's own code uses (no method names it)
        }

        sealed class ArrayInfo
        {
            public string Name = "";
            public ArrayKind Kind;
            public int Length;          // a power of two, so an index is masked into range
            public int Slot;
            public string TypeName => Kind switch { ArrayKind.Int => "int", ArrayKind.U32 => "u32", _ => "u8" };
            public Kind Element => Kind == ArrayKind.U32 ? CodeFuzzer.Kind.U32 : CodeFuzzer.Kind.Int;
        }

        /// <summary>An array by its own name, or through a pointer parameter (whose slot says which array it was given).</summary>
        sealed class ArrayRef
        {
            public string Name = "";
            public ArrayInfo? Fixed;
            public int PointerSlot;
            public ArrayKind Kind;
            public int Length;
            public Kind Element => Kind == ArrayKind.U32 ? CodeFuzzer.Kind.U32 : CodeFuzzer.Kind.Int;
            public uint[] In(Machine m, uint[] frame) => m.Arrays[Fixed != null ? Fixed.Slot : (int)frame[PointerSlot]];
        }

        /// <summary>A Box&lt;int&gt; or Box&lt;u32&gt; class variable: what it keeps, its eight slots, and how often it was set.</summary>
        sealed class Box
        {
            public string Name = "";
            public Kind Kind;
            public int Slot;
            public const int Kept = 0, Slots = 1, Sets = 9, Size = 10;
        }

        sealed class Method
        {
            public string Name = "";
            public bool Function;       // gives a number back and changes nothing outside itself
            public List<Variable> Parameters = new();
            public List<ArrayRef> Pointers = new();
            public List<Variable> Locals = new();
            public List<Stmt> Body = new();
            public Expr? Result;
            public int Slots;
        }

        sealed class ProgramTree
        {
            public List<Variable> Globals = new();
            public List<ArrayInfo> Arrays = new();
            public List<Method> Methods = new();
            public List<Stmt> Main = new();
            public List<Box> Boxes = new();

            /// <summary>
            /// The structs every program has: one that takes a type, with fields, an array and
            /// methods that work on them through self, and one that takes two and is only fields.
            /// </summary>
            const string Structs = """
                struct.Box<Item>
                {
                	Item kept;
                	Item slots[8];
                	int sets;

                	[Set: Item value;]
                		kept is value;
                		sets++;
                	[end]

                	[Add: Item value;]
                		kept++ value;
                	[end]

                	[Get]
                	[end: kept]

                	[Store: int place; Item value;]
                		slots:(place & 7) is value;
                	[end]

                	[Load: int place;]
                	[end: slots:(place & 7)]

                	[Sum]
                		Item total: 0;
                		while int i: 0; i < 8; i++;
                			total++ slots:i;
                		;;
                	[end: total + kept]
                }

                struct.Pair<First Second>
                {
                	First first;
                	Second second;
                }


                """;

            public string Print()
            {
                var sb = new StringBuilder();
                sb.Append("# Made by phi fuzz code: what it prints is worked out as it's written.\n");
                sb.Append(Structs.Replace("\r\n", "\n"));
                sb.Append("phi.Fuzz:Program\n{\n");
                foreach (Box b in Boxes) sb.Append($"\tBox<{(b.Kind == Kind.Int ? "int" : "u32")}> {b.Name};\n");
                sb.Append("\tPair<int u32> duo;\n\tPair<u32 int> turned;\n");
                foreach (Variable v in Globals.Where(g => !g.Name.StartsWith("idx") && !g.Name.Contains('.')))
                    sb.Append($"\t{(v.Kind == Kind.Int ? "int" : "u32")} {v.Name}: {Literal(v.Kind, v.Start, bare: true)};\n");
                foreach (ArrayInfo a in Arrays) sb.Append($"\t{a.TypeName} {a.Name}[{a.Length}];\n");
                sb.Append('\n');
                foreach (Stmt s in Main) s.Print(sb, 1);
                foreach (Method m in Methods)
                {
                    sb.Append('\n');
                    sb.Append($"\t[{m.Name}");
                    var parameters = m.Parameters.Select(p => $"{(p.Kind == Kind.Int ? "int" : "u32")} {p.Name}: 0;")
                        .Concat(m.Pointers.Select(p => $"ptr<{(p.Kind == ArrayKind.Int ? "int" : p.Kind == ArrayKind.U32 ? "u32" : "u8")}> {p.Name}: 0;")).ToList();
                    if (parameters.Count > 0) sb.Append(": ").Append(string.Join(" ", parameters));
                    sb.Append("]\n");
                    foreach (Variable v in m.Locals.Where(l => !l.Name.StartsWith("idx")))
                        sb.Append($"\t\t{(v.Kind == Kind.Int ? "int" : "u32")} {v.Name}: {Literal(v.Kind, v.Start, bare: true)};\n");
                    foreach (Stmt s in m.Body) s.Print(sb, 2);
                    sb.Append(m.Result != null ? $"\t[end: {m.Result.Print()}]\n" : "\t[end]\n");
                }
                sb.Append("}\n");
                return sb.ToString();
            }
        }

        /// <summary>A constant as PHI reads it: a negative int is written (0 - n), since -n after a space is a value of its own.</summary>
        static string Literal(Kind kind, uint value, bool bare = false)
        {
            if (kind == Kind.U32 || (int)value >= 0) return value.ToString();
            string text = $"0 - {(uint)(-(long)(int)value)}";
            return bare ? text : $"({text})";
        }

        // ================================================================ working a program out

        sealed class Machine
        {
            public readonly uint[] Globals;
            public readonly uint[][] Arrays;
            public readonly uint[][] Boxes;
            public readonly StringBuilder Output = new();
            public int Steps;

            public Machine(ProgramTree tree)
            {
                Globals = new uint[tree.Globals.Count];
                foreach (Variable v in tree.Globals) Globals[v.Slot] = v.Start;
                Arrays = tree.Arrays.Select(a => new uint[a.Length]).ToArray();
                Boxes = tree.Boxes.Select(_ => new uint[Box.Size]).ToArray();
            }

            public void Step()
            {
                if (++Steps > 400_000) throw new TooLong();
            }
        }

        abstract class Expr
        {
            public Kind Kind;
            public abstract uint Value(Machine m, uint[] frame);
            public abstract string Print();
            public virtual bool Simple => false;
        }

        sealed class Constant : Expr
        {
            public uint Number;
            public override uint Value(Machine m, uint[] frame) => Number;
            public override string Print() => Literal(Kind, Number);
        }

        sealed class Read : Expr
        {
            public Variable Variable = null!;
            public override uint Value(Machine m, uint[] frame) => Variable.Global ? m.Globals[Variable.Slot] : frame[Variable.Slot];
            public override string Print() => Variable.Name;
            public override bool Simple => true;
        }

        /// <summary>array:(index &amp; mask), or array:name for a loop's counter that stays inside it.</summary>
        sealed class Element : Expr
        {
            public ArrayRef Array = null!;
            public Expr Index = null!;
            public bool Masked;
            public override uint Value(Machine m, uint[] frame)
            {
                uint i = Index.Value(m, frame);
                if (Masked) i &= (uint)(Array.Length - 1);
                return Array.In(m, frame)[i];
            }
            public override string Print() => Masked ? $"{Array.Name}:({Index.Print()} & {Array.Length - 1})" : $"{Array.Name}:{Index.Print()}";
        }

        sealed class Binary : Expr
        {
            public string Op = "";
            public Expr Left = null!, Right = null!;
            public override uint Value(Machine m, uint[] frame)
            {
                m.Step();
                uint a = Left.Value(m, frame), b = Right.Value(m, frame);
                bool signed = Kind == Kind.Int;
                return Op switch
                {
                    "+" => unchecked(a + b),
                    "-" => unchecked(a - b),
                    "*" => unchecked(a * b),
                    "&" => a & b,
                    "|" => a | b,
                    "^" => a ^ b,
                    "<<" => a << (int)b,
                    ">>" => signed ? (uint)((int)a >> (int)b) : a >> (int)b,
                    "/" => signed ? (uint)((int)a / (int)b) : a / b,
                    _ => signed ? (uint)((int)a % (int)b) : a % b,
                };
            }
            public override string Print() => $"({Left.Print()} {Op} {Right.Print()})";
        }

        /// <summary>The same bits as the other kind of number.</summary>
        sealed class As : Expr
        {
            public Expr Operand = null!;
            public override uint Value(Machine m, uint[] frame) => Operand.Value(m, frame);
            public override string Print() => $"({Operand.Print()} as {(Kind == Kind.Int ? "int" : "u32")})";
        }

        sealed class Call : Expr
        {
            public Method Method = null!;
            public List<Expr> Arguments = new();
            public List<ArrayRef> Pointers = new();
            public override uint Value(Machine m, uint[] frame)
            {
                uint[] callee = Enter(m, frame, Method, Arguments, Pointers);
                foreach (Stmt s in Method.Body) s.Run(m, callee);
                return Method.Result!.Value(m, callee);
            }
            public override string Print() => $"({Method.Name}{PrintArguments(Arguments, Pointers)})";
        }

        /// <summary>(box.Get), (box.Sum), (box.Load: place) and box.sets: what a Box holds, read through its methods.</summary>
        sealed class BoxRead : Expr
        {
            public Box Box = null!;
            public string What = "";        // Get, Sum, Load, sets
            public Expr? Place;
            public override uint Value(Machine m, uint[] frame)
            {
                m.Step();
                uint[] box = m.Boxes[Box.Slot];
                switch (What)
                {
                    case "Get": return box[Box.Kept];
                    case "sets": return box[Box.Sets];
                    case "Load": return box[Box.Slots + (Place!.Value(m, frame) & 7)];
                    default:
                        uint total = 0;
                        for (int i = 0; i < 8; i++) total = unchecked(total + box[Box.Slots + i]);
                        return unchecked(total + box[Box.Kept]);
                }
            }
            public override string Print() =>
                What == "sets" ? $"{Box.Name}.sets" : $"({Box.Name}.{What}{(Place != null ? PrintArguments(new List<Expr> { Place }, new List<ArrayRef>()) : "")})";
            public override bool Simple => What == "sets";
        }

        /// <summary>call box.Set: value; call box.Add: value; call box.Store: place value;</summary>
        sealed class BoxWrite : Stmt
        {
            public Box Box = null!;
            public string What = "";
            public Expr? Place;
            public Expr Value = null!;
            public override void Run(Machine m, uint[] frame)
            {
                m.Step();
                uint[] box = m.Boxes[Box.Slot];
                uint place = Place?.Value(m, frame) ?? 0;
                uint value = Value.Value(m, frame);
                switch (What)
                {
                    case "Set":
                        box[Box.Kept] = value;
                        box[Box.Sets]++;
                        break;
                    case "Add": box[Box.Kept] = unchecked(box[Box.Kept] + value); break;
                    default: box[Box.Slots + (place & 7)] = value; break;
                }
            }
            public override void Print(StringBuilder sb, int depth)
            {
                var arguments = new List<Expr>();
                if (Place != null) arguments.Add(Place);
                arguments.Add(Value);
                sb.Append(Tabs(depth)).Append($"call {Box.Name}.{What}{PrintArguments(arguments, new List<ArrayRef>())};\n");
            }
        }

        static uint[] Enter(Machine m, uint[] frame, Method method, List<Expr> arguments, List<ArrayRef> pointers)
        {
            m.Step();
            var callee = new uint[method.Slots];
            for (int i = 0; i < arguments.Count; i++) callee[method.Parameters[i].Slot] = arguments[i].Value(m, frame);
            for (int i = 0; i < pointers.Count; i++)
                callee[method.Pointers[i].PointerSlot] = pointers[i].Fixed != null ? (uint)pointers[i].Fixed!.Slot : frame[pointers[i].PointerSlot];
            foreach (Variable v in method.Locals) callee[v.Slot] = v.Start;
            return callee;
        }

        static string PrintArguments(List<Expr> arguments, List<ArrayRef> pointers)
        {
            var parts = arguments.Select(a => a is Element ? $"({a.Print()})" : a.Print()).Concat(pointers.Select(p => p.Name)).ToList();
            return parts.Count == 0 ? "" : ": " + string.Join(" ", parts);
        }

        sealed class Condition
        {
            public string Op = "";
            public Expr Left = null!, Right = null!;
            public string? Joiner;      // and, or
            public Condition? Next;

            public bool Holds(Machine m, uint[] frame)
            {
                m.Step();
                uint a = Left.Value(m, frame), b = Right.Value(m, frame);
                bool signed = Left.Kind == Kind.Int;
                bool holds = Op switch
                {
                    "is" => a == b,
                    "is not" => a != b,
                    "<" => signed ? (int)a < (int)b : a < b,
                    "<=" => signed ? (int)a <= (int)b : a <= b,
                    ">" => signed ? (int)a > (int)b : a > b,
                    _ => signed ? (int)a >= (int)b : a >= b,
                };
                if (Next == null) return holds;
                // (PHI's and and or stop at the first side that decides it; nothing here has an effect, so either way is the same)
                return Joiner == "and" ? holds && Next.Holds(m, frame) : holds || Next.Holds(m, frame);
            }

            public string Print() => $"{Left.Print()} {Op} {Right.Print()}" + (Next != null ? $" {Joiner} {Next.Print()}" : "");
        }

        abstract class Stmt
        {
            public abstract void Run(Machine m, uint[] frame);
            public abstract void Print(StringBuilder sb, int depth);
            protected static string Tabs(int depth) => new('\t', depth);
        }

        sealed class Assign : Stmt
        {
            public Variable Target = null!;
            public string Op = "is";    // is, ++, --
            public Expr Value = null!;
            public override void Run(Machine m, uint[] frame)
            {
                m.Step();
                uint[] where = Target.Global ? m.Globals : frame;
                uint v = Value.Value(m, frame);
                where[Target.Slot] = Op switch { "++" => unchecked(where[Target.Slot] + v), "--" => unchecked(where[Target.Slot] - v), _ => v };
            }
            public override void Print(StringBuilder sb, int depth) =>
                sb.Append(Tabs(depth)).Append(Op == "is" ? $"{Target.Name} is {Value.Print()};\n" : $"{Target.Name}{Op} {Value.Print()};\n");
        }

        sealed class Store : Stmt
        {
            public ArrayRef Array = null!;
            public Expr Index = null!;
            public bool Masked;
            public Expr Value = null!;
            public override void Run(Machine m, uint[] frame)
            {
                m.Step();
                uint i = Index.Value(m, frame);
                if (Masked) i &= (uint)(Array.Length - 1);
                uint v = Value.Value(m, frame);
                Array.In(m, frame)[i] = Array.Kind == ArrayKind.U8 ? v & 255 : v;
            }
            public override void Print(StringBuilder sb, int depth)
            {
                string index = Masked ? $"({Index.Print()} & {Array.Length - 1})" : Index.Print();
                string value = Array.Kind == ArrayKind.U8 ? $"({Value.Print()} & 255)" : Value.Print();
                sb.Append(Tabs(depth)).Append($"{Array.Name}:{index} is {value};\n");
            }
        }

        sealed class If : Stmt
        {
            public Condition Condition = null!;
            public List<Stmt> Then = new();
            public List<Stmt>? Else;
            public override void Run(Machine m, uint[] frame)
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

        /// <summary>when subject / is values ... / else ...: the first branch with the subject's value, or else.</summary>
        sealed class When : Stmt
        {
            public Expr Subject = null!;
            public List<(List<int> Values, List<Stmt> Body)> Branches = new();
            public List<Stmt>? Else;
            public override void Run(Machine m, uint[] frame)
            {
                m.Step();
                int subject = (int)Subject.Value(m, frame);
                foreach (var (values, body) in Branches)
                    if (values.Contains(subject))
                    {
                        foreach (Stmt s in body) s.Run(m, frame);
                        return;
                    }
                if (Else != null) foreach (Stmt s in Else) s.Run(m, frame);
            }
            public override void Print(StringBuilder sb, int depth)
            {
                sb.Append(Tabs(depth)).Append($"when {Subject.Print()}\n");
                foreach (var (values, body) in Branches)
                {
                    sb.Append(Tabs(depth + 1)).Append("is ").Append(string.Join(" ", values)).Append('\n');
                    foreach (Stmt s in body) s.Print(sb, depth + 2);
                    sb.Append(Tabs(depth + 1)).Append(";;\n");
                }
                if (Else != null)
                {
                    sb.Append(Tabs(depth + 1)).Append("else\n");
                    foreach (Stmt s in Else) s.Print(sb, depth + 2);
                    sb.Append(Tabs(depth + 1)).Append(";;\n");
                }
                sb.Append(Tabs(depth)).Append(";;\n");
            }
        }

        sealed class BreakIf : Stmt
        {
            public Condition Condition = null!;
            public override void Run(Machine m, uint[] frame)
            {
                if (Condition.Holds(m, frame)) throw new Break();
            }
            public override void Print(StringBuilder sb, int depth) => sb.Append(Tabs(depth)).Append($"if {Condition.Print()} break;\n");
        }

        /// <summary>while int idx: 0; idx &lt; limit; idx++; with a limit of a few rounds.</summary>
        sealed class Loop : Stmt
        {
            public Variable Counter = null!;
            public Expr Limit = null!;
            public List<Stmt> Body = new();
            public override void Run(Machine m, uint[] frame)
            {
                uint[] where = Counter.Global ? m.Globals : frame;
                where[Counter.Slot] = 0;
                try
                {
                    while ((int)where[Counter.Slot] < (int)Limit.Value(m, frame))
                    {
                        m.Step();
                        foreach (Stmt s in Body) s.Run(m, frame);
                        where[Counter.Slot]++;
                    }
                }
                catch (Break) { }
            }
            public override void Print(StringBuilder sb, int depth)
            {
                sb.Append(Tabs(depth)).Append($"while int {Counter.Name}: 0; {Counter.Name} < {Limit.Print()}; {Counter.Name}++;\n");
                foreach (Stmt s in Body) s.Print(sb, depth + 1);
                sb.Append(Tabs(depth)).Append(";;\n");
            }
        }

        sealed class Log : Stmt
        {
            public string Tag = "";
            public List<Expr> Values = new();
            public override void Run(Machine m, uint[] frame)
            {
                m.Step();
                m.Output.Append(Tag);
                foreach (Expr e in Values)
                {
                    uint v = e.Value(m, frame);
                    m.Output.Append(' ').Append(e.Kind == Kind.Int ? ((int)v).ToString() : v.ToString());
                }
                m.Output.Append('\n');
                if (m.Output.Length > 200_000) throw new TooLong();
            }
            public override void Print(StringBuilder sb, int depth)
            {
                sb.Append(Tabs(depth)).Append($"log '{Tag}'");
                foreach (Expr e in Values) sb.Append(" ' ' ").Append(e.Simple ? e.Print() : e is Binary or Call or As or BoxRead ? e.Print() : $"({e.Print()})");
                sb.Append(" '\\n';\n");
            }
        }

        sealed class CallStatement : Stmt
        {
            public Method Method = null!;
            public List<Expr> Arguments = new();
            public List<ArrayRef> Pointers = new();
            public override void Run(Machine m, uint[] frame)
            {
                uint[] callee = Enter(m, frame, Method, Arguments, Pointers);
                foreach (Stmt s in Method.Body) s.Run(m, callee);
            }
            public override void Print(StringBuilder sb, int depth) =>
                sb.Append(Tabs(depth)).Append($"call {Method.Name}{PrintArguments(Arguments, Pointers)};\n");
        }

        // ================================================================ making a program

        sealed class Maker
        {
            readonly Random random;
            readonly ProgramTree tree = new();
            int counters, tags;

            // where statements are being made now
            Method? method;                              // null in the class's own code
            readonly List<(Variable Counter, int Rounds)> loops = new();   // the loops around, with a constant limit's rounds (0 if it isn't one)

            static readonly uint[] Interesting =
            {
                0, 1, 2, 3, 4, 5, 7, 8, 9, 10, 15, 16, 31, 32, 60, 100, 127, 128, 255, 256, 641, 1000, 4095, 4096, 65535, 65536,
                1000000, 0x7FFFFFFE, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFF00, 0xFFFFFFFE, 0xFFFFFFFF,
            };

            public Maker(Random random) => this.random = random;

            bool Chance(int percent) => random.Next(100) < percent;
            T Pick<T>(IReadOnlyList<T> from) => from[random.Next(from.Count)];

            public ProgramTree MakeProgram()
            {
                for (int i = 0; i < 5; i++) tree.Globals.Add(new Variable { Name = $"gv{i}", Kind = Kind.Int, Global = true, Start = SomeNumber(Kind.Int) });
                for (int i = 0; i < 3; i++) tree.Globals.Add(new Variable { Name = $"hv{i}", Kind = Kind.U32, Global = true, Start = SomeNumber(Kind.U32) });
                // ones no method names: a loop in the class's own code may keep these in registers
                for (int i = 0; i < 4; i++) tree.Globals.Add(new Variable { Name = $"own{i}", Kind = Kind.Int, Global = true, Own = true, Start = SomeNumber(Kind.Int) });
                for (int i = 0; i < 2; i++) tree.Globals.Add(new Variable { Name = $"mine{i}", Kind = Kind.U32, Global = true, Own = true, Start = SomeNumber(Kind.U32) });
                // the fields of two Pairs, which are variables like any other but for their names
                tree.Globals.Add(new Variable { Name = "duo.first", Kind = Kind.Int, Global = true });
                tree.Globals.Add(new Variable { Name = "duo.second", Kind = Kind.U32, Global = true });
                tree.Globals.Add(new Variable { Name = "turned.first", Kind = Kind.U32, Global = true });
                tree.Globals.Add(new Variable { Name = "turned.second", Kind = Kind.Int, Global = true });
                for (int i = 0; i < tree.Globals.Count; i++) tree.Globals[i].Slot = i;
                tree.Boxes.Add(new Box { Name = "numbers", Kind = Kind.Int, Slot = 0 });
                tree.Boxes.Add(new Box { Name = "counts", Kind = Kind.U32, Slot = 1 });
                tree.Arrays.Add(new ArrayInfo { Name = "ints", Kind = ArrayKind.Int, Length = 16, Slot = 0 });
                tree.Arrays.Add(new ArrayInfo { Name = "words", Kind = ArrayKind.U32, Length = 8, Slot = 1 });
                tree.Arrays.Add(new ArrayInfo { Name = "bytes", Kind = ArrayKind.U8, Length = 32, Slot = 2 });

                // the methods, last first: one calls only those made before it, so none reaches itself
                int functions = random.Next(2, 5), procedures = random.Next(1, 4);
                for (int i = 0; i < functions; i++) tree.Methods.Add(MakeMethod($"Fn{i}", function: true));
                for (int i = 0; i < procedures; i++) tree.Methods.Add(MakeMethod($"Do{i}", function: false));

                method = null;
                tree.Main.AddRange(Block(random.Next(8, 16), depth: 0));

                // at the end, everything there is: each class variable, and each array added up
                tree.Main.Add(new Log { Tag = $"end", Values = tree.Globals.Where(g => !g.Name.StartsWith("idx")).Select(g => (Expr)new Read { Variable = g, Kind = g.Kind }).ToList() });
                foreach (Box b in tree.Boxes)
                    tree.Main.Add(new Log
                    {
                        Tag = b.Name,
                        Values =
                        {
                            new BoxRead { Box = b, What = "Get", Kind = b.Kind },
                            new BoxRead { Box = b, What = "Sum", Kind = b.Kind },
                            new BoxRead { Box = b, What = "sets", Kind = Kind.Int },
                        },
                    });
                foreach (ArrayInfo a in tree.Arrays)
                {
                    Variable sum = tree.Globals.First(g => g.Kind == a.Element && g.Own);
                    Variable counter = NewCounter();
                    var element = new Element { Array = Whole(a), Index = new Read { Variable = counter, Kind = Kind.Int }, Kind = a.Element };
                    var times = new Binary { Op = "*", Kind = a.Element, Left = new Read { Variable = sum, Kind = a.Element }, Right = new Constant { Kind = a.Element, Number = 31 } };
                    tree.Main.Add(new Loop
                    {
                        Counter = counter,
                        Limit = new Constant { Kind = Kind.Int, Number = (uint)a.Length },
                        Body = { new Assign { Target = sum, Value = new Binary { Op = "+", Kind = a.Element, Left = times, Right = element } } },
                    });
                    tree.Main.Add(new Log { Tag = a.Name, Values = { new Read { Variable = sum, Kind = a.Element } } });
                }
                return tree;
            }

            static ArrayRef Whole(ArrayInfo a) => new() { Name = a.Name, Fixed = a, Kind = a.Kind, Length = a.Length };

            Variable NewCounter()
            {
                var counter = new Variable { Name = $"idx{counters++}", Kind = Kind.Int, Global = method == null };
                if (method == null)
                {
                    counter.Slot = tree.Globals.Count;
                    tree.Globals.Add(counter);
                }
                else
                {
                    counter.Slot = method.Slots++;
                    method.Locals.Add(counter);
                }
                return counter;
            }

            Method MakeMethod(string name, bool function)
            {
                var made = new Method { Name = name, Function = function };
                method = made;
                int parameters = random.Next(0, 4);
                for (int i = 0; i < parameters; i++)
                    made.Parameters.Add(new Variable { Name = $"par{i}", Kind = Chance(70) ? Kind.Int : Kind.U32, Slot = made.Slots++ });
                int pointers = random.Next(0, 3);
                for (int i = 0; i < pointers; i++)
                {
                    ArrayInfo like = Pick(tree.Arrays);
                    made.Pointers.Add(new ArrayRef { Name = $"ptr{i}", Kind = like.Kind, Length = like.Length, PointerSlot = made.Slots++ });
                }
                int locals = random.Next(1, 5);
                for (int i = 0; i < locals; i++)
                {
                    Kind kind = Chance(70) ? Kind.Int : Kind.U32;
                    made.Locals.Add(new Variable { Name = $"loc{i}", Kind = kind, Slot = made.Slots++, Start = SomeNumber(kind) });
                }
                made.Body.AddRange(Block(random.Next(3, 9), depth: 0));
                if (function) made.Result = Expression(Kind.Int, 2);
                method = null;
                return made;
            }

            uint SomeNumber(Kind kind)
            {
                uint any = Chance(55) ? (uint)random.Next(0, 20) : Chance(50) ? Pick(Interesting) : (uint)random.NextInt64(0, 0x100000000);
                // (an int's smallest number has no literal: 0 - 2147483648 doesn't fit on the way)
                return kind == Kind.Int && any == 0x80000000 ? 0x80000001 : any;
            }

            // ---------------------------------------------------------------- what can be read and written here

            List<Variable> Readable(Kind kind)
            {
                var all = tree.Globals.Where(g => g.Kind == kind && (!g.Own || method == null)
                                                  && (!g.Name.StartsWith("idx") || loops.Any(l => l.Counter == g))).ToList();
                // (in the class's own code, its own variables as often as all the others)
                if (method == null && Chance(50) && all.Any(g => g.Own)) all = all.Where(g => g.Own).ToList();
                if (method != null)
                    all.AddRange(method.Parameters.Concat(method.Locals).Where(v => v.Kind == kind && (!v.Name.StartsWith("idx") || loops.Any(l => l.Counter == v))));
                return all;
            }

            /// <summary>A function changes only its own variables; nothing changes a loop's counter.</summary>
            List<Variable> Writable(Kind kind)
            {
                var all = new List<Variable>();
                if (method == null || !method.Function)
                    all.AddRange(tree.Globals.Where(g => g.Kind == kind && !g.Name.StartsWith("idx") && (!g.Own || method == null)));
                if (method == null && Chance(50) && all.Any(g => g.Own)) all = all.Where(g => g.Own).ToList();
                if (method != null) all.AddRange(method.Parameters.Concat(method.Locals).Where(v => v.Kind == kind && !v.Name.StartsWith("idx")));
                return all;
            }

            List<ArrayRef> ArraysHere()
            {
                var all = tree.Arrays.Select(Whole).ToList();
                if (method != null) all.AddRange(method.Pointers);
                return all;
            }

            List<Method> Callable(bool function) =>
                tree.Methods.Where(m => m != method && m.Function == function).ToList();   // (the one being made isn't in the list yet)

            // ---------------------------------------------------------------- expressions

            Expr Leaf(Kind kind)
            {
                List<Variable> readable = Readable(kind);
                List<ArrayRef> arrays = ArraysHere().Where(a => a.Element == kind).ToList();
                if (arrays.Count > 0 && Chance(25)) return ElementOf(Pick(arrays), 1);
                if (Chance(12) && tree.Boxes.FirstOrDefault(b => b.Kind == kind) is Box box)
                {
                    string what = Pick(new[] { "Get", "Sum", "Load" });
                    return new BoxRead { Box = box, What = what, Kind = kind, Place = what == "Load" ? Expression(Kind.Int, 0) : null };
                }
                if (readable.Count > 0) return new Read { Variable = Pick(readable), Kind = kind };
                if (arrays.Count > 0) return ElementOf(Pick(arrays), 1);
                // nothing of this kind to read: the other kind's bits
                return new As { Kind = kind, Operand = Leaf(kind == Kind.Int ? Kind.U32 : Kind.Int) };
            }

            Expr ElementOf(ArrayRef array, int depth)
            {
                // a loop's counter that stays inside the array is the index as it is
                var inside = loops.Where(l => l.Rounds > 0 && l.Rounds <= array.Length).ToList();
                if (inside.Count > 0 && Chance(50))
                    return new Element { Array = array, Kind = array.Element, Index = new Read { Variable = Pick(inside).Counter, Kind = Kind.Int } };
                return new Element { Array = array, Kind = array.Element, Masked = true, Index = Expression(Kind.Int, Math.Max(0, depth - 1)) };
            }

            Expr Expression(Kind kind, int depth)
            {
                if (depth <= 0 || Chance(15)) return Leaf(kind);
                int roll = random.Next(100);
                if (roll < 8) return new As { Kind = kind, Operand = Expression(kind == Kind.Int ? Kind.U32 : Kind.Int, depth - 1) };
                if (roll < 18 && kind == Kind.Int && Callable(function: true) is { Count: > 0 } functions)
                {
                    Method callee = Pick(functions);
                    return new Call { Kind = Kind.Int, Method = callee, Arguments = Arguments(callee, depth - 1), Pointers = PointersFor(callee) };
                }

                Expr left = Expression(kind, depth - 1);
                string op = Pick(new[] { "+", "+", "-", "-", "*", "*", "&", "|", "^", "<<", ">>", "/", "/", "%", "%" });
                Expr right;
                switch (op)
                {
                    case "<<" or ">>":
                        right = new Constant { Kind = Kind.Int, Number = (uint)random.Next(0, 32) };
                        break;
                    case "/" or "%":
                        if (Chance(75))
                        {
                            // a constant: any but zero, and but -1 (the smallest int over it has no answer)
                            uint d = Chance(50) ? (uint)random.Next(1, 70) : SomeNumber(kind);
                            if (d == 0 || kind == Kind.Int && d == 0xFFFFFFFF) d = 7;
                            right = new Constant { Kind = kind, Number = d };
                        }
                        else
                        {
                            // 1 to 256, from anything
                            var low = new Binary { Op = "&", Kind = kind, Left = Expression(kind, depth - 1), Right = new Constant { Kind = kind, Number = 255 } };
                            right = new Binary { Op = "+", Kind = kind, Left = low, Right = new Constant { Kind = kind, Number = 1 } };
                        }
                        break;
                    default:
                        right = Chance(40) ? new Constant { Kind = kind, Number = SomeNumber(kind) } : Expression(kind, depth - 1);
                        break;
                }
                return new Binary { Op = op, Kind = kind, Left = left, Right = right };
            }

            List<Expr> Arguments(Method callee, int depth) =>
                callee.Parameters.Select(p => Expression(p.Kind, Math.Min(depth, 1))).ToList();

            /// <summary>An array of the kind and length each pointer parameter was made for.</summary>
            List<ArrayRef> PointersFor(Method callee) =>
                callee.Pointers.Select(p => Pick(ArraysHere().Where(a => a.Kind == p.Kind && a.Length == p.Length).ToList())).ToList();

            Condition MakeCondition(bool more = true)
            {
                Kind kind = Chance(70) ? Kind.Int : Kind.U32;
                var made = new Condition
                {
                    Op = Pick(new[] { "is", "is not", "<", "<=", ">", ">=" }),
                    Left = Expression(kind, 1),
                    Right = Chance(50) ? new Constant { Kind = kind, Number = SomeNumber(kind) } : Expression(kind, 1),
                };
                if (more && Chance(25))
                {
                    made.Joiner = Chance(50) ? "and" : "or";
                    made.Next = MakeCondition(more: false);
                }
                return made;
            }

            // ---------------------------------------------------------------- statements

            List<Stmt> Block(int count, int depth)
            {
                var block = new List<Stmt>();
                for (int i = 0; i < count; i++) block.Add(Statement(depth));
                return block;
            }

            Stmt Statement(int depth)
            {
                bool function = method is { Function: true };
                int roll = random.Next(100);

                if (roll < 14 && depth < 3)
                {
                    Variable counter = NewCounter();
                    int rounds = 0;
                    Expr limit;
                    if (Chance(70))
                    {
                        rounds = random.Next(1, 9);
                        limit = new Constant { Kind = Kind.Int, Number = (uint)rounds };
                    }
                    else
                    {
                        var low = new Binary { Op = "&", Kind = Kind.Int, Left = Expression(Kind.Int, 1), Right = new Constant { Kind = Kind.Int, Number = 7 } };
                        limit = new Binary { Op = "+", Kind = Kind.Int, Left = low, Right = new Constant { Kind = Kind.Int, Number = 1 } };
                    }
                    loops.Add((counter, rounds));
                    var loop = new Loop { Counter = counter, Limit = limit, Body = Block(random.Next(1, 5), depth + 1) };
                    if (Chance(20)) loop.Body.Insert(random.Next(loop.Body.Count + 1), new BreakIf { Condition = MakeCondition() });
                    loops.RemoveAt(loops.Count - 1);
                    return loop;
                }
                if (roll < 19 && depth < 3)
                {
                    // a number from 0 to 15, or from -6 to 9: values close together (a table) or
                    // far apart (compared one at a time), some branches with several
                    Expr subject = new Binary { Op = "&", Kind = Kind.Int, Left = Expression(Kind.Int, 1), Right = new Constant { Kind = Kind.Int, Number = 15 } };
                    int shift = Chance(30) ? 6 : 0;
                    if (shift != 0) subject = new Binary { Op = "-", Kind = Kind.Int, Left = subject, Right = new Constant { Kind = Kind.Int, Number = (uint)shift } };
                    var pool = Enumerable.Range(-shift, 16).OrderBy(_ => random.Next()).Take(random.Next(2, 13)).ToList();
                    if (Chance(20)) pool.Add(random.Next(100, 100000));   // one far away: no table then
                    var made = new When { Subject = subject };
                    while (pool.Count > 0)
                    {
                        int take = Math.Min(pool.Count, Chance(70) ? 1 : random.Next(2, 4));
                        made.Branches.Add((pool.Take(take).ToList(), Block(random.Next(1, 3), depth + 1)));
                        pool.RemoveRange(0, take);
                    }
                    if (Chance(60)) made.Else = Block(random.Next(1, 3), depth + 1);
                    return made;
                }
                if (roll < 26 && depth < 3)
                {
                    var made = new If { Condition = MakeCondition(), Then = Block(random.Next(1, 4), depth + 1) };
                    if (Chance(40)) made.Else = Block(random.Next(1, 3), depth + 1);
                    return made;
                }
                if (roll < 40 && !function)
                {
                    ArrayRef array = Pick(ArraysHere());
                    var inside = loops.Where(l => l.Rounds > 0 && l.Rounds <= array.Length).ToList();
                    if (inside.Count > 0 && Chance(50))
                        return new Store { Array = array, Index = new Read { Variable = Pick(inside).Counter, Kind = Kind.Int }, Value = Expression(array.Element, 2) };
                    return new Store { Array = array, Masked = true, Index = Expression(Kind.Int, 1), Value = Expression(array.Element, 2) };
                }
                if (roll < 46 && !function)
                {
                    Box box = Pick(tree.Boxes);
                    string what = Pick(new[] { "Set", "Add", "Store", "Store" });
                    return new BoxWrite { Box = box, What = what, Place = what == "Store" ? Expression(Kind.Int, 1) : null, Value = Expression(box.Kind, 2) };
                }
                if (roll < 54 && !function)
                {
                    var log = new Log { Tag = $"L{tags++}" };
                    int values = random.Next(1, 4);
                    for (int i = 0; i < values; i++) log.Values.Add(Expression(Chance(70) ? Kind.Int : Kind.U32, 2));
                    return log;
                }
                if (roll < 61 && !function && Callable(function: false) is { Count: > 0 } procedures && loops.Count < 2)
                {
                    Method callee = Pick(procedures);
                    return new CallStatement { Method = callee, Arguments = Arguments(callee, 2), Pointers = PointersFor(callee) };
                }

                Kind kind = Chance(70) ? Kind.Int : Kind.U32;
                List<Variable> writable = Writable(kind);
                if (writable.Count == 0)
                {
                    kind = kind == Kind.Int ? Kind.U32 : Kind.Int;
                    writable = Writable(kind);
                }
                if (writable.Count == 0)
                {
                    // (a function with nothing of its own to change: a test that does nothing)
                    return new If { Condition = MakeCondition(), Then = new List<Stmt>() };
                }
                return new Assign { Target = Pick(writable), Op = Pick(new[] { "is", "is", "is", "++", "--" }), Value = Expression(kind, 3) };
            }
        }
    }
}

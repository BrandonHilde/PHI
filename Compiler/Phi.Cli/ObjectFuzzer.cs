using System.Text;

namespace Phi.Cli
{
    /// <summary>
    /// phi fuzz objects: random PHI programs of what PHI allocates and frees itself, each written
    /// together with what it must print, as phi fuzz code's are of numbers (CodeFuzzer runs them:
    /// it builds each for the systems this computer can run and compares).
    ///
    /// The programs are made of what owns memory: objects, text that grows, arrays that grow (of
    /// numbers, texts, structs in place and objects), structs that hold all of those, a struct
    /// that holds itself (a chain, and a tree), structs that take types (a Line&lt;Item&gt; written
    /// here, and data.map's Map&lt;Key Value&gt;) and structs made of those. What's done to them is
    /// what moves ownership: copies (is), handing over (takes), emptying, comparing, appending,
    /// shortening, declaring in a method, in a block and in a loop, lending to a method as a
    /// parameter, and getting one back as a method's result. Where two names in one statement
    /// can be the same thing, or one inside the other (a list appended with its own element, a
    /// tree copied into its own branch), they sometimes are.
    ///
    /// A program is written a statement at a time, and each statement is done here as it's
    /// written, on values that are copied whole and owned once, which is what PHI says its own
    /// are. So what a program holds is known at every line: nothing it does is what PHI stops
    /// for (an empty object's fields, an index out of range). Every so often it prints something
    /// it holds, all the way down. It does its work three times over, and after the second says
    /// how many blocks the heap has in use: the third must leave as many, or something was
    /// never freed.
    ///
    /// Each program is run twice on each system: on the standard heap, and on the checking heap
    /// (samples/own_heap), where a block used after it's freed, or freed twice, stops the
    /// program at that line.
    ///
    /// Left alone, as the compiler refuses it: an object taking what it's inside of. (Another
    /// element of the same array isn't, and is written: `list:0.left takes list:1;`.)
    /// </summary>
    public static class ObjectFuzzer
    {
        /// <summary>The program for a seed and what it prints.</summary>
        public static CodeFuzzer.Case? Make(int seed) => Make(seed, trace: false);

        /// <summary>
        /// The same program for the seed, which with trace prints every variable after every
        /// statement: the first line that differs is right after the statement that went wrong.
        /// </summary>
        public static CodeFuzzer.Case? Make(int seed, bool trace) => new Maker(new Random(seed)) { Trace = trace }.MakeProgram();

        /// <summary>The checking heap's files (samples/own_heap/memory), by name: put beside a program, they are its heap.</summary>
        public static IEnumerable<(string Name, string Text)> CheckingHeap()
        {
            var assembly = typeof(ObjectFuzzer).Assembly;
            foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("own_heap/")).OrderBy(n => n))
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                yield return (name.Substring("own_heap/".Length), reader.ReadToEnd());
            }
        }

        // ================================================================ what a program is made of

        enum K { Int, Text, Struct, Obj, List }

        /// <summary>
        /// What a variable, a field or an element is: a number, a text that grows, a struct kept in
        /// place, an object (which can be empty), or an array of one of those (one that grows unless
        /// Fixed says how many it always has; Cap is the most a `[atmost N]` one holds).
        /// </summary>
        sealed class Ty
        {
            public K Kind;
            public Def? Def;
            public Ty? Elem;
            public int Fixed;
            public int Cap;

            public static readonly Ty Int = new() { Kind = K.Int };
            public static readonly Ty Text = new() { Kind = K.Text };
            public static Ty List(Ty elem, int fixedLength = 0, int cap = 0) => new() { Kind = K.List, Elem = elem, Fixed = fixedLength, Cap = cap };

            public bool Same(Ty other) =>
                Kind == other.Kind && Def == other.Def && Fixed == other.Fixed && Cap == other.Cap
                && (Elem == null ? other.Elem == null : other.Elem != null && Elem.Same(other.Elem));

            public bool IsStructLike => Kind is K.Struct or K.Obj;
        }

        sealed class Field
        {
            public string Name = "";
            public Ty Ty = null!;
        }

        /// <summary>A type a struct that takes types is given: how it's written, and what a field and an array element of it are.</summary>
        sealed record TypeArg(string Name, string Short, Ty AsField, Ty AsElem);

        sealed class Def
        {
            public string Name = "";            // as it's written: Leaf, Line<Leaf>, Map<int Leaf>
            public string Short = "";           // in a method's name: Leaf, LineOfLeaf, MapOfIntLeaf
            public List<Field> Fields = new();
            public bool Generic;                // made from a struct that takes types: not written out
            public TypeArg? Item;               // a Line<Item>'s
            public Ty? MapKey, MapValue;        // a Map<Key Value>'s: a key, and an element of its values
            public bool Managed;                // PHI manages it: it owns text, arrays that grow or objects
            public bool Comparable = true;      // (a Map's tables depend on what was done to it, not only on what it holds)
            public string? StampInt, StampText; // the fields a loop writes its counter into
            public string? Self;                // a field that's an object of this struct: a chain

            public readonly Ty AsStruct, AsObj;
            public Def() { AsStruct = new Ty { Kind = K.Struct, Def = this }; AsObj = new Ty { Kind = K.Obj, Def = this }; }

            public bool IsMap => MapKey != null;
            public bool IsLine => Item != null;
            public int IndexOf(string field) => Fields.FindIndex(f => f.Name == field);
            public TypeArg AsArg => new(Name, Short, AsStruct, Managed ? AsObj : AsStruct);
        }

        // ---------------------------------------------------------------- values

        /// <summary>
        /// Where a value is kept. Each is one place in the program's memory: a statement changes
        /// what's in it, and a copy is a new one all the way down.
        /// </summary>
        abstract class Val
        {
            public abstract Val Copy();
            public abstract bool Same(Val other);
        }

        sealed class IntVal : Val
        {
            public int V;
            public override Val Copy() => new IntVal { V = V };
            public override bool Same(Val other) => other is IntVal o && o.V == V;
        }

        sealed class TextVal : Val
        {
            public string V = "";
            public override Val Copy() => new TextVal { V = V };
            public override bool Same(Val other) => other is TextVal o && o.V == V;
        }

        sealed class Entry
        {
            public Val Key = null!, Value = null!;
        }

        sealed class StructVal : Val
        {
            public Def Def = null!;
            public Val[] F = Array.Empty<Val>();
            public List<Entry>? Entries;        // a Map's keys and values, in the order its arrays have them

            public override Val Copy() => new StructVal
            {
                Def = Def,
                F = F.Select(f => f.Copy()).ToArray(),
                Entries = Entries?.Select(e => new Entry { Key = e.Key.Copy(), Value = e.Value.Copy() }).ToList(),
            };

            public override bool Same(Val other)
            {
                if (other is ObjVal obj) return obj.Held != null && Same(obj.Held);
                if (other is not StructVal o || o.Def != Def) return false;
                for (int i = 0; i < F.Length; i++)
                    if (!F[i].Same(o.F[i])) return false;
                return true;
            }
        }

        sealed class ObjVal : Val
        {
            public StructVal? Held;
            public override Val Copy() => new ObjVal { Held = (StructVal?)Held?.Copy() };
            public override bool Same(Val other) =>
                other is StructVal s ? Held != null && Held.Same(s)
                : other is ObjVal o && (Held == null ? o.Held == null : o.Held != null && Held.Same(o.Held));
        }

        sealed class ListVal : Val
        {
            public List<Val> Items = new();
            public override Val Copy() => new ListVal { Items = Items.Select(i => i.Copy()).ToList() };
            public override bool Same(Val other) =>
                other is ListVal o && o.Items.Count == Items.Count && Items.Zip(o.Items).All(p => p.First.Same(p.Second));
        }

        sealed class Var
        {
            public string Name = "";
            public Ty Ty = null!;
            public Val Storage = null!;
            public bool Global;
            public bool Param;
            public int Frozen;      // a class variable a method was lent part of: the method doesn't name it
        }

        /// <summary>Something a statement can name: a variable, or a field or an element reached from one.</summary>
        sealed class Place
        {
            public string Text = "";
            public Ty Ty = null!;
            public Val Storage = null!;
            public Var Root = null!;
            public List<Val> Chain = null!;     // every value from the variable's own down to this one
            public bool ReadOnly;               // a Map's key: read, never changed
            public int Depth;

            public bool IsEmpty => Storage is ObjVal { Held: null };
            public StructVal? Struct => Storage as StructVal ?? (Storage as ObjVal)?.Held;
            public bool Inside(Place other) => Storage != other.Storage && Chain.Contains(other.Storage);
            public bool Overlaps(Place other) => Chain.Contains(other.Storage) || other.Chain.Contains(Storage);
        }

        sealed class Frame
        {
            public string Name = "";
            public StringBuilder Body = new();
            public int Indent = 2;
            public List<List<Var>> Blocks = new() { new List<Var>() };
            public int Locals;
        }

        // ================================================================ writing a program

        sealed class Maker
        {
            readonly Random rng;
            readonly List<Def> defs = new();
            readonly Def tag, leaf, twig, tree, shelf;
            readonly List<Var> globals = new();
            readonly Stack<Frame> frames = new();
            readonly List<string> methods = new();
            readonly StringBuilder output = new();
            List<Place>? places;
            int names, loops, madeMethods, budget;
            public bool Trace;
            readonly int longChain;

            const string LineStruct = """
                struct.Line<Item>
                {
                	Item items[];
                	Item kept;
                	int first;

                	[Add: Item item;]
                		items++ item;
                	[end]

                	[Count]
                	[end: items.len - first]

                	[Take]
                		first++;
                	[end: items:(first - 1)]

                	[Put: int spot; Item item;]
                		items:spot is item;
                	[end]

                	[Keep: Item item;]
                		kept is item;
                	[end]

                	[Kept]
                	[end: kept]
                }


                """;

            public Maker(Random rng)
            {
                this.rng = rng;
                longChain = rng.Next(12) == 0 ? 250 : 0;

                tag = Add(new Def { Name = "Tag", Short = "Tag", StampInt = "a" }, ("a", Ty.Int), ("b", Ty.Int));
                leaf = Add(new Def { Name = "Leaf", Short = "Leaf", StampInt = "id", StampText = "name" },
                    ("id", Ty.Int), ("name", Ty.Text), ("parts", Ty.List(Ty.Int)));
                tree = new Def { Name = "Tree", Short = "Tree", StampInt = "value", StampText = "label", Self = "left" };
                Add(tree, ("value", Ty.Int), ("label", Ty.Text), ("left", tree.AsObj), ("kids", Ty.List(tree.AsObj)));
                twig = Add(new Def { Name = "Twig", Short = "Twig", StampInt = "id", StampText = "note" },
                    ("id", Ty.Int), ("note", Ty.Text), ("tag", tag.AsStruct), ("inner", leaf.AsStruct), ("one", leaf.AsObj),
                    ("many", Ty.List(leaf.AsObj)), ("pair", Ty.List(leaf.AsObj, fixedLength: 2)), ("names", Ty.List(Ty.Text)),
                    ("tags", Ty.List(tag.AsStruct)), ("mark", tag.AsObj));

                var intArg = new TypeArg("int", "Int", Ty.Int, Ty.Int);
                var strArg = new TypeArg("str", "Str", Ty.Text, Ty.Text);
                Line(intArg);
                Def lineTag = Line(tag.AsArg);
                Def lineLeaf = Line(leaf.AsArg);
                Def lineTree = Line(tree.AsArg);
                Line(lineLeaf.AsArg);
                Def mapLeaf = Map(intArg, leaf.AsArg);
                Def mapTwig = Map(strArg, twig.AsArg);
                Map(intArg, tree.AsArg);
                Map(strArg, intArg);
                Map(intArg, tag.AsArg);
                Map(intArg, lineLeaf.AsArg);
                shelf = Add(new Def { Name = "Shelf", Short = "Shelf", StampInt = "id" },
                    ("id", Ty.Int), ("line", lineLeaf.AsObj), ("index", mapLeaf.AsStruct), ("named", mapTwig.AsObj),
                    ("tags", lineTag.AsStruct), ("rows", Ty.List(lineTree.AsObj)));

                var seen = new HashSet<Def>();
                foreach (Def d in defs) d.Comparable = !HoldsMap(d, seen);
            }

            Def Add(Def d, params (string Name, Ty Ty)[] fields)
            {
                foreach (var (name, ty) in fields) d.Fields.Add(new Field { Name = name, Ty = ty });
                d.Managed = d.Generic || d.Fields.Any(f => f.Ty.Kind is K.Text or K.Obj or K.List || f.Ty.Kind == K.Struct && f.Ty.Def!.Managed);
                defs.Add(d);
                return d;
            }

            Def Line(TypeArg item) =>
                Add(new Def { Name = $"Line<{item.Name}>", Short = "LineOf" + item.Short, Generic = true, Item = item },
                    ("items", Ty.List(item.AsElem)), ("kept", item.AsField), ("first", Ty.Int));

            Def Map(TypeArg key, TypeArg value) =>
                Add(new Def { Name = $"Map<{key.Name} {value.Name}>", Short = $"MapOf{key.Short}{value.Short}", Generic = true, MapKey = key.AsField, MapValue = value.AsElem });

            static bool HoldsMap(Def d, HashSet<Def> on)
            {
                if (d.IsMap) return true;
                if (!on.Add(d)) return false;
                bool holds = d.Fields.Any(f => (f.Ty.Def ?? f.Ty.Elem?.Def) is { } inner && HoldsMap(inner, on));
                on.Remove(d);
                return holds;
            }

            // ---------------------------------------------------------------- small things

            bool Chance(int percent) => rng.Next(100) < percent;
            T Pick<T>(IReadOnlyList<T> from) => from[rng.Next(from.Count)];
            Frame Now => frames.Peek();

            void Emit(string line) => Now.Body.Append('\t', Now.Indent).Append(line).Append('\n');

            void Touch() => places = null;

            static readonly string[] Words =
            {
                "ab", "phi", "golden", "spiral", "q r", "no", "0.618", "ratio", "five places", "",
                "a text long enough to need a block of its own on the heap",
            };

            string SomeText(bool canBeEmpty = true)
            {
                string word = Pick(Words);
                return word.Length == 0 && !canBeEmpty ? "phi" : word;
            }

            static string Quote(string text) => "'" + text + "'";

            int SomeInt() => rng.Next(6) == 0 ? rng.Next(1000, 99999) : rng.Next(0, 60);

            // ---------------------------------------------------------------- values from nothing

            /// <summary>What a variable of a type starts as; an object that's a field (or a fixed array's, in a struct) starts empty.</summary>
            static Val Zero(Ty ty, bool variable)
            {
                switch (ty.Kind)
                {
                    case K.Int: return new IntVal();
                    case K.Text: return new TextVal();
                    case K.Struct: return ZeroStruct(ty.Def!);
                    case K.Obj: return new ObjVal { Held = variable ? ZeroStruct(ty.Def!) : null };
                    default:
                        var list = new ListVal();
                        for (int i = 0; i < ty.Fixed; i++) list.Items.Add(Zero(ty.Elem!, variable));
                        return list;
                }
            }

            static StructVal ZeroStruct(Def d) => new()
            {
                Def = d,
                F = d.Fields.Select(f => Zero(f.Ty, variable: false)).ToArray(),
                Entries = d.IsMap ? new List<Entry>() : null,
            };

            /// <summary>A struct's value as what an array of a type holds: an object of its own, or the struct in place.</summary>
            static Val AsElement(Ty elem, StructVal value) => elem.Kind == K.Obj ? new ObjVal { Held = value } : value;

            /// <summary>Puts a copy where something already is, as `is` does: an object that's there keeps its memory.</summary>
            static void Assign(Val to, Val copy)
            {
                switch (to)
                {
                    case IntVal i: i.V = ((IntVal)copy).V; break;
                    case TextVal t: t.V = ((TextVal)copy).V; break;
                    case ListVal l: l.Items = ((ListVal)copy).Items; break;
                    case StructVal s:
                    {
                        StructVal from = copy as StructVal ?? ((ObjVal)copy).Held!;
                        s.F = from.F;
                        s.Entries = from.Entries;
                        break;
                    }
                    case ObjVal o:
                    {
                        StructVal? from = copy as StructVal ?? ((ObjVal)copy).Held;
                        if (from == null) o.Held = null;
                        else if (o.Held == null) o.Held = from;
                        else
                        {
                            o.Held.F = from.F;
                            o.Held.Entries = from.Entries;
                        }
                        break;
                    }
                }
            }

            // ---------------------------------------------------------------- printing what's held

            void Print(Ty ty, Val value)
            {
                switch (ty.Kind)
                {
                    case K.Int: output.Append(' ').Append(((IntVal)value).V); break;
                    case K.Text: output.Append(" \"").Append(((TextVal)value).V).Append('"'); break;
                    case K.Struct: PrintStruct((StructVal)value); break;
                    case K.Obj:
                        if (((ObjVal)value).Held is { } held) PrintStruct(held);
                        else output.Append(" -");
                        break;
                    default:
                        output.Append(" [");
                        foreach (Val item in ((ListVal)value).Items) Print(ty.Elem!, item);
                        output.Append(" ]");
                        break;
                }
            }

            void PrintStruct(StructVal s)
            {
                Def d = s.Def;
                output.Append(' ').Append(d.Short).Append('(');
                if (d.IsMap)
                {
                    foreach (Entry e in s.Entries!)
                    {
                        Print(d.MapKey!, e.Key);
                        output.Append(" =>");
                        Print(d.MapValue!, e.Value);
                    }
                }
                else
                {
                    for (int i = 0; i < d.Fields.Count; i++) Print(d.Fields[i].Ty, s.F[i]);
                }
                output.Append(')');
            }

            /// <summary>The statements that print what an expression holds, as Print does.</summary>
            void Show(StringBuilder sb, int indent, string expr, Ty ty)
            {
                void Out(string text) => sb.Append('\t', indent).Append(text).Append('\n');
                switch (ty.Kind)
                {
                    case K.Int: Out($"log ' ' {expr};"); break;
                    case K.Text: Out($"log ' \"' {expr} '\"';"); break;
                    case K.Struct: Out($"call Show{ty.Def!.Short}: {expr};"); break;
                    case K.Obj:
                        Out($"if {expr} is empty");
                        Out("\tlog ' -';");
                        Out(";;");
                        Out("else");
                        Out($"\tcall Show{ty.Def!.Short}: {expr};");
                        Out(";;");
                        break;
                    default:
                    {
                        string i = $"k{loops++}";
                        Out("log ' [';");
                        Out($"while int {i}: 0; {i} < {expr}.len; {i}++;");
                        Show(sb, indent + 1, $"{expr}:{i}", ty.Elem!);
                        Out(";;");
                        Out("log ' ]';");
                        break;
                    }
                }
            }

            string ShowMethod(Def d)
            {
                var sb = new StringBuilder();
                sb.Append($"\t[Show{d.Short}: {d.Name} v;]\n");
                sb.Append($"\t\tlog ' {d.Short}(';\n");
                if (d.IsMap)
                {
                    string i = $"k{loops++}";
                    sb.Append($"\t\twhile int {i}: 0; {i} < v.keys.len; {i}++;\n");
                    Show(sb, 3, $"v.keys:{i}", d.MapKey!);
                    sb.Append("\t\t\tlog ' =>';\n");
                    Show(sb, 3, $"v.values:{i}", d.MapValue!);
                    sb.Append("\t\t;;\n");
                }
                else
                {
                    foreach (Field f in d.Fields) Show(sb, 2, "v." + f.Name, f.Ty);
                }
                sb.Append("\t\tlog ')';\n");
                sb.Append("\t[end]\n\n");
                return sb.ToString();
            }

            /// <summary>A line of output: a name, and all that it holds.</summary>
            void Dump(string expr, Ty ty, Val value)
            {
                Emit($"log '{expr.Replace("'", "")}:';");
                Show(Now.Body, Now.Indent, expr, ty);
                Emit("log '\\n';");
                output.Append(expr).Append(':');
                Print(ty, value);
                output.Append('\n');
            }

            void Dump(Var v) => Dump(v.Name, v.Ty, v.Storage);

            // ---------------------------------------------------------------- what can be named

            List<Place> All() => places ??= Enumerate();

            List<Place> Enumerate()
            {
                var list = new List<Place>();
                foreach (List<Var> block in Now.Blocks)
                    foreach (Var v in block) Walk(list, v, v.Name, v.Ty, v.Storage, new List<Val>(), 0, false);
                foreach (Var g in globals)
                    if (g.Frozen == 0) Walk(list, g, g.Name, g.Ty, g.Storage, new List<Val>(), 0, false);
                return list;
            }

            void Walk(List<Place> list, Var root, string text, Ty ty, Val storage, List<Val> above, int depth, bool readOnly)
            {
                if (list.Count >= 500) return;
                var chain = new List<Val>(above) { storage };
                list.Add(new Place { Text = text, Ty = ty, Storage = storage, Root = root, Chain = chain, Depth = depth, ReadOnly = readOnly });
                if (depth >= 6) return;
                switch (ty.Kind)
                {
                    case K.Struct:
                        WalkFields(list, root, text, (StructVal)storage, chain, depth);
                        break;
                    case K.Obj:
                        if (((ObjVal)storage).Held is { } held) WalkFields(list, root, text, held, new List<Val>(chain) { held }, depth);
                        break;
                    case K.List:
                    {
                        List<Val> items = ((ListVal)storage).Items;
                        foreach (int i in Sample(items.Count)) Walk(list, root, $"{text}:{i}", ty.Elem!, items[i], chain, depth + 1, false);
                        break;
                    }
                }
            }

            void WalkFields(List<Place> list, Var root, string text, StructVal s, List<Val> chain, int depth)
            {
                if (s.Def.IsMap)
                {
                    foreach (int i in Sample(s.Entries!.Count))
                    {
                        Walk(list, root, $"{text}.keys:{i}", s.Def.MapKey!, s.Entries[i].Key, chain, depth + 1, true);
                        Walk(list, root, $"{text}.values:{i}", s.Def.MapValue!, s.Entries[i].Value, chain, depth + 1, false);
                    }
                    return;
                }
                for (int i = 0; i < s.Def.Fields.Count; i++)
                    Walk(list, root, $"{text}.{s.Def.Fields[i].Name}", s.Def.Fields[i].Ty, s.F[i], chain, depth + 1, false);
            }

            /// <summary>Which of an array's elements are worth naming: all of a short one, and both ends and one between of a long one.</summary>
            IEnumerable<int> Sample(int count)
            {
                if (count <= 4) return Enumerable.Range(0, count);
                return new[] { 0, 1, rng.Next(2, count - 1), count - 1 };
            }

            Place? Find(Func<Place, bool> want)
            {
                List<Place> some = All().Where(want).ToList();
                return some.Count == 0 ? null : Pick(some);
            }

            /// <summary>A struct that's there to be read: one in place, or an object that isn't empty.</summary>
            static bool Holds(Place p, Def d) => p.Ty.IsStructLike && p.Ty.Def == d && !p.IsEmpty;

            (string Text, int Value) IntSource()
            {
                if (Chance(35) && Find(p => p.Ty.Kind == K.Int) is { } from) return (from.Text, ((IntVal)from.Storage).V);
                if (Chance(15) && Find(p => p.Ty.Kind == K.List) is { } list) return ($"{list.Text}.len", ((ListVal)list.Storage).Items.Count);
                int n = SomeInt();
                return (n.ToString(), n);
            }

            (string Text, string Value) TextSource(Func<Place, bool>? allowed = null)
            {
                if (Chance(40) && Find(p => p.Ty.Kind == K.Text && (allowed == null || allowed(p))) is { } from)
                    return (from.Text, ((TextVal)from.Storage).V);
                string word = SomeText();
                return (Quote(word), word);
            }

            // ---------------------------------------------------------------- variables

            Var NewVar(Ty ty, Val storage)
            {
                var v = new Var { Name = $"v{names++}", Ty = ty, Storage = storage };
                Now.Blocks[^1].Add(v);
                Now.Locals++;
                Touch();
                return v;
            }

            static string Declaration(Ty ty, string name)
            {
                switch (ty.Kind)
                {
                    case K.Int: return $"int {name}";
                    case K.Text: return $"str {name}";
                    case K.Struct: return $"{ty.Def!.Name} {name}";
                    case K.Obj: return $"new {ty.Def!.Name} {name}";
                    default:
                    {
                        string size = ty.Fixed > 0 ? $"[{ty.Fixed}]" : ty.Cap > 0 ? $"[atmost {ty.Cap}]" : "[]";
                        Ty e = ty.Elem!;
                        return e.Kind switch
                        {
                            K.Int => $"int {name}{size}",
                            K.Text => $"str {name}{size}",
                            K.Struct => $"{e.Def!.Name} {name}{size}",
                            _ => $"new {e.Def!.Name} {name}{size}",
                        };
                    }
                }
            }

            static string FieldDeclaration(Field f) =>
                f.Ty.Kind == K.Text ? $"new str {f.Name}" : Declaration(f.Ty, f.Name);

            Def SomeDef(bool managedMostly = true)
            {
                Def d = Pick(defs);
                if (managedMostly && !d.Managed && Chance(60)) d = Pick(defs);
                return d;
            }

            // ================================================================ statements

            bool OpDeclare()
            {
                if (Now.Locals >= 14) return false;
                switch (rng.Next(12))
                {
                    case 0:
                    {
                        int n = SomeInt();
                        Var v = NewVar(Ty.Int, new IntVal { V = n });
                        Emit($"int {v.Name}: {n};");
                        return true;
                    }
                    case 1:
                    {
                        var (text, value) = TextSource();
                        if (value.Length == 1) (text, value) = ("'phi'", "phi");
                        Var v = NewVar(Ty.Text, new TextVal { V = value });
                        Emit($"str {v.Name}: {text};");
                        return true;
                    }
                    case 2:
                    {
                        Ty ty = Chance(50) ? Ty.List(Ty.Int) : Chance(50) ? Ty.List(Ty.Text) : Ty.List(tag.AsStruct);
                        Var v = NewVar(ty, Zero(ty, true));
                        Emit(Declaration(ty, v.Name) + ";");
                        return true;
                    }
                    case 3:
                    case 4:
                    {
                        Def d = SomeDef();
                        Ty ty = rng.Next(4) switch
                        {
                            0 => Ty.List(d.AsObj, fixedLength: rng.Next(2, 4)),
                            1 => Ty.List(d.AsObj, cap: rng.Next(3, 9)),
                            _ => Ty.List(d.AsObj),
                        };
                        Var v = NewVar(ty, Zero(ty, true));
                        Emit(Declaration(ty, v.Name) + ";");
                        return true;
                    }
                    case 5:
                    {
                        Var v = NewVar(tag.AsStruct, ZeroStruct(tag));
                        Emit($"Tag {v.Name};");
                        return true;
                    }
                    default:
                    {
                        Def d = SomeDef();
                        Place? from = Chance(45) ? Find(p => Holds(p, d)) : null;
                        if (from != null)
                        {
                            var copy = (StructVal)from.Struct!.Copy();
                            Var v = NewVar(d.AsObj, new ObjVal { Held = copy });
                            Emit($"new {d.Name} {v.Name}: {from.Text};");
                        }
                        else if (Chance(15))
                        {
                            Var v = NewVar(d.AsObj, new ObjVal());
                            Emit($"new {d.Name} {v.Name}: empty;");
                        }
                        else
                        {
                            Var v = NewVar(d.AsObj, new ObjVal { Held = ZeroStruct(d) });
                            Emit($"new {d.Name} {v.Name};");
                        }
                        return true;
                    }
                }
            }

            bool OpInt()
            {
                Place? p = Find(x => x.Ty.Kind == K.Int && !x.ReadOnly);
                if (p == null) return false;
                var v = (IntVal)p.Storage;
                if (Chance(35) && v.V < 100000)
                {
                    int n = rng.Next(1, 20);
                    Emit($"{p.Text}++ {n};");
                    v.V += n;
                }
                else
                {
                    var (text, value) = IntSource();
                    Emit($"{p.Text} is {text};");
                    v.V = value;
                }
                return true;
            }

            bool OpText()
            {
                Place? p = Find(x => x.Ty.Kind == K.Text && !x.ReadOnly);
                if (p == null) return false;
                var v = (TextVal)p.Storage;
                bool room = v.V.Length < 300;
                switch (rng.Next(5))
                {
                    case 0:
                    {
                        var (text, value) = TextSource();     // (itself too: s is s; leaves it as it is)
                        Emit($"{p.Text} is {text};");
                        v.V = value;
                        return true;
                    }
                    case 1:
                    {
                        if (!room) return false;
                        var (text, value) = TextSource(q => ((TextVal)q.Storage).V.Length < 300);     // (itself too: s++ s; doubles it)
                        if (text == "''") return false;
                        Emit($"{p.Text}++ {text};");
                        v.V += value;
                        return true;
                    }
                    case 2:
                    {
                        string word = SomeText(canBeEmpty: false);
                        var (number, n) = IntSource();
                        if (room && Chance(30))
                        {
                            // the str among its own values, after the first: read as it was
                            Emit($"{p.Text} is {Quote(word)} {p.Text} {number} {p.Text};");
                            v.V = word + v.V + n + v.V;
                            return true;
                        }
                        Emit($"{p.Text} is ({Quote(word)} {number});");
                        v.V = word + n;
                        return true;
                    }
                    case 3:
                    {
                        if (!room) return false;
                        string word = SomeText(canBeEmpty: false);
                        var (number, n) = IntSource();
                        Emit($"{p.Text}++ {Quote(word)} {number};");
                        v.V += word + n;
                        return true;
                    }
                    default:
                    {
                        string word = SomeText();
                        Emit($"{p.Text} is {Quote(word)};");
                        v.V = word;
                        return true;
                    }
                }
            }

            static bool Full(Place list, int more = 1) =>
                list.Ty.Cap > 0 && ((ListVal)list.Storage).Items.Count + more > list.Ty.Cap || ((ListVal)list.Storage).Items.Count + more > 60;

            /// <summary>Shortens an array, or copies another whole one into it: the same for every kind of element.</summary>
            bool ShortenOrCopy(Place p)
            {
                var list = (ListVal)p.Storage;
                if (p.Ty.Fixed == 0 && list.Items.Count > 0 && Chance(60))
                {
                    if (Chance(50))
                    {
                        Emit($"{p.Text}.len--;");
                        list.Items.RemoveAt(list.Items.Count - 1);
                    }
                    else
                    {
                        int keep = rng.Next(list.Items.Count);
                        Emit($"{p.Text}.len is {keep};");
                        list.Items.RemoveRange(keep, list.Items.Count - keep);
                    }
                    return true;
                }
                // (an array that holds this one in one of its elements too, `tree.kids:0.kids is tree.kids;`,
                // one this one holds, and now and then itself: the copy is of what the source was)
                Place? from = Chance(5) ? p : Find(q => q.Ty.Kind == K.List && q.Ty.Same(p.Ty) && q.Storage != p.Storage);
                if (from == null) return false;
                Val copy = from.Storage.Copy();
                Emit($"{p.Text} is {from.Text};");
                Assign(list, copy);
                return true;
            }

            /// <summary>An array of numbers, texts or structs in place: more on its end (its own elements among them), fewer, or another's.</summary>
            bool OpListInPlace()
            {
                Place? p = Find(x => x.Ty.Kind == K.List && x.Ty.Elem!.Kind != K.Obj && !x.ReadOnly);
                if (p == null) return false;
                var list = (ListVal)p.Storage;
                Ty elem = p.Ty.Elem!;
                if (Chance(30)) return ShortenOrCopy(p);

                // (x++ a b c; adds them one at a time, so each is read as the array is by then)
                int count = rng.Next(1, 4);
                if (Full(p, count)) return false;
                var texts = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    Touch();
                    switch (elem.Kind)
                    {
                        case K.Int:
                        {
                            var (text, value) = IntSource();
                            texts.Add(text);
                            list.Items.Add(new IntVal { V = value });
                            break;
                        }
                        case K.Text:
                        {
                            var (text, value) = TextSource();
                            if (value.Length == 1) (text, value) = ("'phi'", "phi");
                            texts.Add(text);
                            list.Items.Add(new TextVal { V = value });
                            break;
                        }
                        default:
                        {
                            Place? from = Find(q => Holds(q, elem.Def!));
                            if (from == null) break;
                            texts.Add(from.Text);
                            list.Items.Add(from.Struct!.Copy());
                            break;
                        }
                    }
                }
                if (texts.Count == 0) return false;
                Emit($"{p.Text}++ {string.Join(" ", texts)};");
                return true;
            }

            /// <summary>An array of objects: a new one on its end, copies of others (its own among them), fewer, or another array's.</summary>
            bool OpListOfObjects()
            {
                Place? p = Find(x => x.Ty.Kind == K.List && x.Ty.Elem!.Kind == K.Obj && !x.ReadOnly);
                if (p == null) return false;
                var list = (ListVal)p.Storage;
                Def d = p.Ty.Elem!.Def!;
                if (p.Ty.Fixed > 0 || Chance(30)) return ShortenOrCopy(p);

                if (Chance(30))
                {
                    if (Full(p)) return false;
                    Emit($"{p.Text}++;");
                    list.Items.Add(new ObjVal { Held = ZeroStruct(d) });
                    return true;
                }
                int count = rng.Next(1, 3);
                if (Full(p, count)) return false;
                var texts = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    Touch();
                    Place? from = Find(q => Holds(q, d));
                    if (from == null) break;
                    texts.Add(from.Text);
                    list.Items.Add(new ObjVal { Held = (StructVal)from.Struct!.Copy() });
                }
                if (texts.Count == 0) return false;
                Emit($"{p.Text}++ {string.Join(" ", texts)};");
                return true;
            }

            /// <summary>p is q: a copy, of all that q owns. Now and then q is p, or one is inside the other.</summary>
            bool OpCopy()
            {
                Place? p = Find(x => x.Ty.IsStructLike && !x.ReadOnly);
                if (p == null) return false;
                Def d = p.Ty.Def!;
                bool itself = Chance(4) && !p.IsEmpty;
                Place? q = itself ? p : Find(x => x.Ty.IsStructLike && x.Ty.Def == d && x.Storage != p.Storage && (p.Ty.Kind == K.Obj || !x.IsEmpty));
                if (q == null) return false;
                Val copy = q.Storage.Copy();
                Emit($"{p.Text} is {q.Text};");
                Assign(p.Storage, copy);
                return true;
            }

            bool OpEmpty()
            {
                Place? p = Find(x => x.Ty.Kind == K.Obj && !x.ReadOnly && !x.IsEmpty);
                if (p == null) return false;
                Emit($"{p.Text} is empty;");
                ((ObjVal)p.Storage).Held = null;
                return true;
            }

            /// <summary>p takes q: q's object is p's now. What takes can't be inside what's taken, which would then own itself.</summary>
            bool OpTake()
            {
                Place? p = Find(x => x.Ty.Kind == K.Obj && !x.ReadOnly);
                if (p == null) return false;
                bool itself = Chance(3);
                Place? q = itself ? p : Find(x => x.Ty.Kind == K.Obj && x.Ty.Def == p.Ty.Def && !x.ReadOnly && x.Storage != p.Storage && !p.Inside(x));
                if (q == null) return false;
                Emit($"{p.Text} takes {q.Text};");
                var to = (ObjVal)p.Storage;
                var from = (ObjVal)q.Storage;
                StructVal? held = from.Held;
                from.Held = null;
                to.Held = held;
                return true;
            }

            bool OpCompare()
            {
                Place? p = Find(x => x.Ty.IsStructLike && x.Ty.Def!.Comparable
                                     || x.Ty.Kind == K.List && (x.Ty.Elem!.Def?.Comparable ?? true));
                if (p == null) return false;
                bool Fits(Place x) =>
                    x.Storage != p.Storage
                    && (p.Ty.Kind == K.List
                        ? x.Ty.Kind == K.List && x.Ty.Same(p.Ty)
                        : x.Ty.IsStructLike && x.Ty.Def == p.Ty.Def && (x.Ty.Kind == K.Obj && p.Ty.Kind == K.Obj || !x.IsEmpty && !p.IsEmpty));
                Place? q = Chance(50) ? Find(x => Fits(x) && x.Storage.Same(p.Storage)) : null;
                q ??= Find(Fits);
                if (q == null) return false;
                bool same = p.Storage.Same(q.Storage);
                Emit($"if {p.Text} is {q.Text}");
                Emit("\tlog 'same\\n';");
                Emit(";;");
                Emit("else");
                Emit("\tlog 'differs\\n';");
                Emit(";;");
                output.Append(same ? "same\n" : "differs\n");
                return true;
            }

            /// <summary>An if whose condition is known: what's inside is done, or it's the else that is. Its variables end with it.</summary>
            bool OpBlock()
            {
                if (Now.Indent >= 4) return false;
                Place? number = Find(x => x.Ty.Kind == K.Int);
                bool taken = Chance(60);
                if (number == null) Emit(taken ? "if true" : "if false");
                else Emit($"if {number.Text} is {((IntVal)number.Storage).V + (taken ? 0 : 1)}");
                if (!taken)
                {
                    Emit("\tlog 'never\\n';");
                    Emit(";;");
                    Emit("else");
                }
                Now.Indent++;
                Now.Blocks.Add(new List<Var>());
                Touch();
                Ops(rng.Next(2, 6));
                Now.Locals -= Now.Blocks[^1].Count;
                Now.Blocks.RemoveAt(Now.Blocks.Count - 1);
                Now.Indent--;
                Emit(";;");
                Touch();
                return true;
            }

            // ---------------------------------------------------------------- loops

            void Stamp(Def d, StructVal value, int i)
            {
                ((IntVal)value.F[d.IndexOf(d.StampInt!)]).V = i;
                if (d.StampText != null) ((TextVal)value.F[d.IndexOf(d.StampText)]).V += "w" + i;
            }

            /// <summary>A loop that makes an object each time round, in a variable of its own, and adds a copy of it to something.</summary>
            bool LoopAppend()
            {
                bool Stamped(Def? d) => d is { StampInt: not null };
                Place? p = Find(x => !x.ReadOnly && !x.IsEmpty
                    && (x.Ty.Kind == K.List && x.Ty.Fixed == 0 && Stamped(x.Ty.Elem!.Def)
                        || x.Ty.IsStructLike && x.Ty.Def!.IsLine && Stamped(x.Ty.Def.Item!.AsField.Def)
                        || x.Ty.IsStructLike && x.Ty.Def!.IsMap && x.Ty.Def.MapKey!.Kind == K.Int && Stamped(x.Ty.Def.MapValue!.Def)));
                if (p == null) return false;
                int count = Chance(12) ? rng.Next(20, 46) : rng.Next(2, 9);
                int start = rng.Next(0, 5);
                string i = $"k{loops++}", t = $"t{names++}";

                Def d;
                Action<StructVal, int> add;
                string append;
                if (p.Ty.Kind == K.List)
                {
                    var list = (ListVal)p.Storage;
                    if (Full(p, count)) return false;
                    Ty elem = p.Ty.Elem!;
                    d = elem.Def!;
                    add = (value, _) => list.Items.Add(AsElement(elem, value));
                    append = $"{p.Text}++ {t};";
                }
                else if (p.Ty.Def!.IsLine)
                {
                    StructVal line = p.Struct!;
                    Ty elem = p.Ty.Def.Item!.AsElem;
                    d = elem.Def!;
                    var items = (ListVal)line.F[0];
                    if (items.Items.Count + count > 60) return false;
                    add = (value, _) => ((ListVal)line.F[0]).Items.Add(AsElement(elem, value));
                    append = $"call {p.Text}.Add: {t};";
                }
                else
                {
                    StructVal map = p.Struct!;
                    Ty elem = p.Ty.Def.MapValue!;
                    d = elem.Def!;
                    if (map.Entries!.Count + count > 60) return false;
                    add = (value, key) => MapSet(map, new IntVal { V = key }, value);
                    append = $"call {p.Text}.Set: {i} {t};";
                }

                Emit($"while int {i}: {start}; {i} < {start + count}; {i}++;");
                Emit($"\tnew {d.Name} {t};");
                Emit($"\t{t}.{d.StampInt} is {i};");
                if (d.StampText != null) Emit($"\t{t}.{d.StampText}++ 'w' {i};");
                Emit("\t" + append);
                Emit(";;");
                for (int n = start; n < start + count; n++)
                {
                    StructVal made = ZeroStruct(d);
                    Stamp(d, made, n);
                    add(made, n);
                }
                return true;
            }

            /// <summary>A chain built by handing over: each new one takes what there was, and is taken in its turn.</summary>
            bool LoopChain()
            {
                Place? p = Find(x => x.Ty.Kind == K.Obj && x.Ty.Def!.Self != null && !x.ReadOnly);
                if (p == null) return false;
                Def d = p.Ty.Def!;
                int count = longChain > 0 && Chance(30) ? longChain : rng.Next(2, 12);
                if (budget < count / 4) return false;
                budget -= count / 4;
                string i = $"k{loops++}", t = $"t{names++}";
                Emit($"while int {i}: 0; {i} < {count}; {i}++;");
                Emit($"\tnew {d.Name} {t};");
                Emit($"\t{t}.{d.StampInt} is {i};");
                Emit($"\t{t}.{d.Self} takes {p.Text};");
                Emit($"\t{p.Text} takes {t};");
                Emit(";;");
                var head = (ObjVal)p.Storage;
                int self = d.IndexOf(d.Self!);
                for (int n = 0; n < count; n++)
                {
                    StructVal made = ZeroStruct(d);
                    ((IntVal)made.F[d.IndexOf(d.StampInt!)]).V = n;
                    ((ObjVal)made.F[self]).Held = head.Held;
                    head.Held = made;
                }
                return true;
            }

            /// <summary>Loops over an array that's there: every element changed, all of them moved down one, or the last ones dropped.</summary>
            bool LoopOver()
            {
                Place? p = Find(x => x.Ty.Kind == K.List && !x.ReadOnly && ((ListVal)x.Storage).Items.Count > 0);
                if (p == null) return false;
                var list = (ListVal)p.Storage;
                Ty elem = p.Ty.Elem!;
                string i = $"k{loops++}";
                int kind = rng.Next(3);
                if (kind == 0 && elem.Def is { StampInt: not null } d)
                {
                    Emit($"while int {i}: 0; {i} < {p.Text}.len; {i}++;");
                    if (elem.Kind == K.Obj)
                    {
                        Emit($"\tif {p.Text}:{i} is not empty");
                        Emit($"\t\t{p.Text}:{i}.{d.StampInt}++ {i};");
                        Emit("\t;;");
                    }
                    else Emit($"\t{p.Text}:{i}.{d.StampInt}++ {i};");
                    Emit(";;");
                    for (int n = 0; n < list.Items.Count; n++)
                    {
                        StructVal? s = list.Items[n] as StructVal ?? ((ObjVal)list.Items[n]).Held;
                        if (s != null) ((IntVal)s.F[d.IndexOf(d.StampInt)]).V += n;
                    }
                    return true;
                }
                if (kind == 1 && p.Ty.Fixed == 0)
                {
                    // the one at the front goes, and the rest move along
                    Emit($"while int {i}: 1; {i} < {p.Text}.len; {i}++;");
                    Emit($"\t{p.Text}:({i} - 1) is {p.Text}:{i};");
                    Emit(";;");
                    Emit($"{p.Text}.len--;");
                    for (int n = 1; n < list.Items.Count; n++) Assign(list.Items[n - 1], list.Items[n].Copy());
                    list.Items.RemoveAt(list.Items.Count - 1);
                    return true;
                }
                if (p.Ty.Fixed > 0) return false;
                int keep = rng.Next(list.Items.Count);
                Emit($"while {p.Text}.len > {keep}");
                Emit($"\t{p.Text}.len--;");
                Emit(";;");
                list.Items.RemoveRange(keep, list.Items.Count - keep);
                return true;
            }

            // ---------------------------------------------------------------- a method's result

            /// <summary>Where a call's result goes: an object takes it over, a new variable starts with it, or a struct or number gets its value.</summary>
            void TakeResult(string call, Ty ty, Val result)
            {
                Touch();
                if (ty.Kind == K.Int)
                {
                    Place? number = Chance(70) ? Find(p => p.Ty.Kind == K.Int && !p.ReadOnly) : null;
                    if (number != null)
                    {
                        Emit($"{number.Text} is ({call});");
                        ((IntVal)number.Storage).V = ((IntVal)result).V;
                    }
                    else
                    {
                        Var v = NewVar(Ty.Int, result);
                        Emit($"int {v.Name}: ({call});");
                    }
                    return;
                }
                Def d = ty.Def!;
                var value = (StructVal)result;
                Place? to = Chance(75) || Now.Locals >= 16
                    ? Find(p => !p.ReadOnly && p.Ty.Def == d && (p.Ty.Kind == K.Obj || p.Ty.Kind == K.Struct && !d.Managed))
                    : null;
                if (to == null)
                {
                    Var v = NewVar(d.AsObj, new ObjVal { Held = value });
                    Emit($"new {d.Name} {v.Name}: ({call});");
                }
                else if (to.Ty.Kind == K.Obj)
                {
                    Emit(Chance(50) ? $"call {to.Text} is {call};" : $"{to.Text} is ({call});");
                    ((ObjVal)to.Storage).Held = value;
                }
                else
                {
                    Emit($"{to.Text} is ({call});");
                    ((StructVal)to.Storage).F = value.F;
                }
                Touch();
            }

            // ---------------------------------------------------------------- Line<Item>

            /// <summary>Something to give a Line or a Map's method as an item: a number, or a struct that's no part of the Line or Map itself.</summary>
            (string Text, Val Copy)? ItemSource(Ty elem, Place apart)
            {
                if (elem.Kind == K.Int)
                {
                    var (text, value) = IntSource();
                    return (text, new IntVal { V = value });
                }
                Place? from = Find(q => Holds(q, elem.Def!) && !q.Overlaps(apart));
                if (from == null) return null;
                return (from.Text, AsElement(elem, (StructVal)from.Struct!.Copy()));
            }

            bool OpLine()
            {
                Place? p = Find(x => x.Ty.IsStructLike && x.Ty.Def!.IsLine && !x.IsEmpty);
                if (p == null) return false;
                StructVal line = p.Struct!;
                Def d = p.Ty.Def!;
                TypeArg item = d.Item!;
                var items = (ListVal)line.F[0];
                var first = (IntVal)line.F[2];
                switch (rng.Next(6))
                {
                    case 0:
                    case 1:
                    {
                        if (items.Items.Count >= 60) return false;
                        if (ItemSource(item.AsElem, p) is not var (text, copy)) return false;
                        Emit($"call {p.Text}.Add: {text};");
                        items.Items.Add(copy);
                        return true;
                    }
                    case 2:
                    {
                        // the next one out: a copy of it is the method's result
                        if (first.V < 0 || first.V >= items.Items.Count || items.Items[first.V] is ObjVal { Held: null }) return false;
                        Val taken = items.Items[first.V];
                        Val result = item.AsElem.Kind == K.Int ? taken.Copy() : (taken as StructVal ?? ((ObjVal)taken).Held!).Copy();
                        first.V++;
                        TakeResult($"{p.Text}.Take", item.AsField, result);
                        return true;
                    }
                    case 3:
                    {
                        if (items.Items.Count == 0) return false;
                        int place = rng.Next(items.Items.Count);
                        if (ItemSource(item.AsElem, p) is not var (text, copy)) return false;
                        Emit($"call {p.Text}.Put: {place} {text};");
                        Assign(items.Items[place], copy);
                        return true;
                    }
                    case 4:
                    {
                        if (ItemSource(item.AsElem, p) is not var (text, copy)) return false;
                        Emit($"call {p.Text}.Keep: {text};");
                        Assign(line.F[1], copy);
                        return true;
                    }
                    default:
                    {
                        if (Chance(50))
                        {
                            Emit($"log 'count ' ({p.Text}.Count) '\\n';");
                            output.Append("count ").Append(items.Items.Count - first.V).Append('\n');
                        }
                        else TakeResult($"{p.Text}.Kept", item.AsField, line.F[1].Copy());
                        return true;
                    }
                }
            }

            // ---------------------------------------------------------------- Map<Key Value>

            static int MapFind(StructVal map, Val key) => map.Entries!.FindIndex(e => e.Key.Same(key));

            void MapSet(StructVal map, Val key, Val value)
            {
                Ty elem = map.Def.MapValue!;
                int at = MapFind(map, key);
                if (at >= 0) Assign(map.Entries![at].Value, value);
                else map.Entries!.Add(new Entry { Key = key, Value = value is StructVal s ? AsElement(elem, s) : value });
            }

            (string Text, Val Value) KeySource(StructVal map, Place apart)
            {
                Ty key = map.Def.MapKey!;
                if (map.Entries!.Count > 0 && Chance(55))
                {
                    // one that's there
                    Val there = Pick(map.Entries).Key;
                    return key.Kind == K.Int ? (((IntVal)there).V.ToString(), there.Copy()) : (Quote(((TextVal)there).V), there.Copy());
                }
                if (key.Kind == K.Int)
                {
                    int n = rng.Next(0, 24);
                    return (n.ToString(), new IntVal { V = n });
                }
                if (Chance(30) && Find(q => q.Ty.Kind == K.Text && !q.Overlaps(apart) && ((TextVal)q.Storage).V.Length is > 1 and < 40) is { } from)
                    return (from.Text, from.Storage.Copy());
                string word = "k" + rng.Next(0, 24);
                return (Quote(word), new TextVal { V = word });
            }

            bool OpMap()
            {
                Place? p = Find(x => x.Ty.IsStructLike && x.Ty.Def!.IsMap && !x.IsEmpty);
                if (p == null) return false;
                StructVal map = p.Struct!;
                Ty elem = map.Def.MapValue!;
                Ty asValue = elem.Kind == K.Obj ? elem.Def!.AsStruct : elem;
                var (keyText, key) = KeySource(map, p);
                int at = MapFind(map, key);
                switch (rng.Next(8))
                {
                    case 0:
                    case 1:
                    case 2:
                    {
                        if (at < 0 && map.Entries!.Count >= 60) return false;
                        if (ItemSource(elem, p) is not var (text, copy)) return false;
                        Emit($"call {p.Text}.Set: {keyText} {text};");
                        MapSet(map, key, copy is ObjVal o ? o.Held! : copy);
                        return true;
                    }
                    case 3:
                    case 4:
                    {
                        // the key's value, or the one given when it isn't there: a copy either way
                        if (at >= 0 && map.Entries![at].Value is ObjVal { Held: null }) return false;
                        if (ItemSource(elem, p) is not var (text, otherwise)) return false;
                        Val found = at >= 0 ? map.Entries![at].Value.Copy() : otherwise;
                        TakeResult($"{p.Text}.Get: {keyText} {text}", asValue, found is ObjVal o ? o.Held! : found);
                        return true;
                    }
                    case 5:
                    {
                        Emit($"call {p.Text}.Remove: {keyText};");
                        if (at >= 0)
                        {
                            // the last one moves into the gap
                            int last = map.Entries!.Count - 1;
                            if (at != last)
                            {
                                Assign(map.Entries[at].Key, map.Entries[last].Key.Copy());
                                Assign(map.Entries[at].Value, map.Entries[last].Value.Copy());
                            }
                            map.Entries.RemoveAt(last);
                        }
                        return true;
                    }
                    case 6:
                    {
                        if (Chance(80)) return false;
                        Emit($"call {p.Text}.Clear;");
                        map.Entries!.Clear();
                        return true;
                    }
                    default:
                    {
                        Emit($"log 'count ' ({p.Text}.Count) ' at ' ({p.Text}.Find: {keyText}) '\\n';");
                        output.Append("count ").Append(map.Entries!.Count).Append(" at ").Append(at).Append('\n');
                        return true;
                    }
                }
            }

            // ---------------------------------------------------------------- methods

            /// <summary>
            /// A method written for one call: it's lent structs and objects as parameters, has
            /// variables of its own that end with it, and may hand one of them back, or a copy of
            /// something else. A class variable that part of was lent isn't named inside it, since
            /// changing it there could free what the parameter still is.
            /// </summary>
            bool OpCall()
            {
                if (frames.Count >= 3 || madeMethods >= 9 || budget < 6) return false;
                int id = madeMethods++;

                var arguments = new List<Place>();
                int wanted = rng.Next(0, 4);
                for (int i = 0; i < wanted; i++)
                {
                    Place? a = Find(x => x.Ty.IsStructLike && !x.IsEmpty && arguments.All(o => !o.Overlaps(x)));
                    if (a != null) arguments.Add(a);
                }
                int number = rng.Next(0, 50);
                foreach (Place a in arguments.Where(a => a.Root.Global)) a.Root.Frozen++;

                var frame = new Frame { Name = $"M{id}" };
                var header = new StringBuilder($"\t[M{id}:");
                for (int i = 0; i < arguments.Count; i++)
                {
                    var parameter = new Var { Name = $"m{id}p{i}", Ty = arguments[i].Ty.Def!.AsStruct, Storage = arguments[i].Struct!, Param = true };
                    frame.Blocks[0].Add(parameter);
                    header.Append($" {parameter.Ty.Def!.Name} {parameter.Name};");
                }
                var count = new Var { Name = $"n{id}", Ty = Ty.Int, Storage = new IntVal { V = number } };
                frame.Blocks[0].Add(count);
                header.Append($" int {count.Name}: 0;]\n");
                string call = $"M{id}: {string.Join(" ", arguments.Select(a => a.Text).Append(number.ToString()))}";

                frames.Push(frame);
                Touch();
                bool gives = Chance(50);
                int steps = rng.Next(3, 9);
                int leaves = gives ? -1 : rng.Next(steps);
                for (int i = 0; i < steps; i++)
                {
                    if (i == leaves) Emit($"if {count.Name} is {((IntVal)count.Storage).V + 1} return;");
                    Ops(1);
                }
                foreach (Var v in frame.Blocks[0].Where(v => !v.Param && v != count).ToList())
                    if (Chance(60)) Dump(v);

                // what it gives back: a variable of its own, handed over, or a copy of anything else
                StructVal? result = null;
                Def? resultDef = null;
                string ending = "[end]";
                if (gives)
                {
                    Touch();
                    List<Var> own = frame.Blocks[0].Where(v => !v.Param && v.Ty.Kind == K.Obj && v.Storage is ObjVal { Held: not null }).ToList();
                    string? source = null;
                    if (own.Count > 0 && Chance(60))
                    {
                        Var v = Pick(own);
                        source = v.Name;
                        resultDef = v.Ty.Def;
                        result = ((ObjVal)v.Storage).Held!;
                    }
                    else if (Find(x => x.Ty.IsStructLike && !x.IsEmpty) is { } from)
                    {
                        source = from.Text;
                        resultDef = from.Ty.Def;
                        result = (StructVal)from.Struct!.Copy();
                    }
                    if (source != null)
                    {
                        if (Chance(30)) Emit($"if {count.Name} is {((IntVal)count.Storage).V} return {source};");
                        ending = $"[end: {source}]";
                    }
                }
                frames.Pop();
                foreach (Place a in arguments.Where(a => a.Root.Global)) a.Root.Frozen--;
                methods.Add(header.ToString() + frame.Body + "\t" + ending + "\n\n");
                Touch();

                if (result == null) Emit($"call {call};");
                else TakeResult(call, resultDef!.AsStruct, result);
                return true;
            }

            // ---------------------------------------------------------------- the program

            void Ops(int count)
            {
                for (int done = 0; done < count && budget > 0; done++)
                {
                    budget--;
                    for (int tries = 0; tries < 30; tries++)
                    {
                        Touch();
                        int roll = rng.Next(124);
                        bool did =
                            roll < 12 ? OpDeclare()
                            : roll < 18 ? OpInt()
                            : roll < 28 ? OpText()
                            : roll < 36 ? OpListInPlace()
                            : roll < 47 ? OpListOfObjects()
                            : roll < 62 ? OpCopy()
                            : roll < 71 ? OpTake()
                            : roll < 74 ? OpEmpty()
                            : roll < 80 ? OpCompare()
                            : roll < 85 ? OpCall()
                            : roll < 89 ? OpBlock()
                            : roll < 93 ? LoopAppend()
                            : roll < 96 ? LoopChain()
                            : roll < 100 ? LoopOver()
                            : roll < 112 ? OpLine()
                            : OpMap();
                        if (did) break;
                    }
                    Touch();
                    if (Chance(30))
                    {
                        List<Place> roots = All().Where(p => p.Depth == 0).ToList();
                        if (roots.Count > 0)
                        {
                            Place shown = Pick(roots);
                            Dump(shown.Text, shown.Ty, shown.Storage);
                        }
                    }
                    if (Trace)
                    {
                        // (the same statements as without it: nothing here is random)
                        foreach (Var v in Now.Blocks.SelectMany(b => b).Concat(globals.Where(g => g.Frozen == 0)).ToList()) Dump(v);
                    }
                }
            }

            public CodeFuzzer.Case MakeProgram()
            {
                budget = rng.Next(40, 110);

                // the class's own variables: empty at the start of each round, so every round is the same
                var reset = new List<string>();
                var declared = new StringBuilder();
                int count = rng.Next(4, 9);
                for (int i = 0; i < count; i++)
                {
                    Def d = SomeDef();
                    Ty ty = rng.Next(10) switch
                    {
                        0 => Ty.Int,
                        1 => Ty.Text,
                        2 => Ty.List(Ty.Int),
                        3 => Ty.List(Ty.Text),
                        4 or 5 => Ty.List(d.AsObj),
                        _ => d.AsObj,
                    };
                    var g = new Var { Name = $"g{i}", Ty = ty, Global = true };
                    g.Storage = ty.Kind == K.Obj ? new ObjVal() : Zero(ty, true);
                    globals.Add(g);
                    declared.Append('\t').Append(Declaration(ty, g.Name)).Append(ty.Kind == K.Int ? ": 0" : ty.Kind == K.Text ? ": ''" : "").Append(";\n");
                    reset.Add(ty.Kind switch
                    {
                        K.Int => $"{g.Name} is 0;",
                        K.Text => $"{g.Name} is '';",
                        K.Obj => $"{g.Name} is empty;",
                        _ => $"{g.Name}.len is 0;",
                    });
                }

                var work = new Frame { Name = "Work" };
                frames.Push(work);
                foreach (string line in reset) Emit(line);
                Ops(int.MaxValue);
                Touch();
                foreach (Var v in work.Blocks[0]) Dump(v);
                foreach (Var g in globals) Dump(g);
                frames.Pop();

                var sb = new StringBuilder();
                sb.Append("use data.map;\n\n");
                foreach (Def d in defs.Where(d => !d.Generic))
                {
                    sb.Append($"struct.{d.Name}\n{{\n");
                    foreach (Field f in d.Fields) sb.Append('\t').Append(FieldDeclaration(f)).Append(";\n");
                    sb.Append("}\n\n");
                }
                sb.Append(LineStruct);
                sb.Append("phi.Objects:Program\n{\n");
                sb.Append(declared);
                sb.Append("\n\tcall Work;\n\tcall Work;\n\tu32 before: Heap.used_blocks;\n\tcall Work;\n");
                sb.Append("\tlog 'left behind: ' Heap.used_blocks - before '\\n';\n\texit 0;\n\n");
                foreach (Def d in defs) sb.Append(ShowMethod(d));
                sb.Append("\t[Work]\n").Append(work.Body).Append("\t[end]\n\n");
                foreach (string method in methods) sb.Append(method);
                sb.Length -= 1;
                sb.Append("}\n");

                string round = output.ToString();
                return new CodeFuzzer.Case(sb.ToString(), round + round + round + "left behind: 0\n", CheckedHeap: true);
            }
        }
    }
}

#!/usr/bin/env python3
"""Writes a vendor library's PHI module from its C headers.

    python3 vendor/bindgen.py raylib          # vendor/raylib/package.json -> vendor/raylib/raylib.phi
    python3 vendor/bindgen.py --all

What a package's "bindings" in its package.json say is in vendor/README.md. The headers are
read by clang (its JSON syntax tree: every function, struct, enum and typedef as C sees
them), and the #defines by this script, since clang's tree has none. Each C type becomes the
PHI type that is the same on every system PHI builds for; what can't be said in PHI (a
bitfield, a va_list) is left out and listed, so that nothing is declared wrongly. It needs
python3 and clang, and is run by whoever updates a library: a release has its output.
"""
import json
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))

RESERVED = set("""int i32 u32 i16 u16 i8 u8 i64 u64 lng ulng isz usz f16 f32 f64 byt bln chr str var ptr vec method lock
phi struct enum interface asm x64 arm sys foreign use const volatile isr end into
if elif else while each when unsafe break continue return call exit atomic locked throw throws
is not and or has exists below above atmost atleast true false
new free addr sizeof as takes empty at in inw ind out outw outd log debug ask self len""".split())

# C's own types, and the names every header has for the sized ones
BUILTIN = {
    "char": "u8", "signed char": "i8", "unsigned char": "u8",
    "short": "i16", "unsigned short": "u16",
    "int": "int", "unsigned int": "u32", "unsigned": "u32",
    "long": "lng", "unsigned long": "ulng",
    "long long": "i64", "unsigned long long": "u64",
    "float": "f32", "double": "f64", "_Bool": "bln", "bool": "bln",
    "int8_t": "i8", "uint8_t": "u8", "int16_t": "i16", "uint16_t": "u16",
    "int32_t": "int", "uint32_t": "u32", "int64_t": "i64", "uint64_t": "u64",
    "size_t": "usz", "ssize_t": "isz", "intptr_t": "isz", "uintptr_t": "usz", "ptrdiff_t": "isz",
    "wchar_t": None, "va_list": None, "__builtin_va_list": None, "long double": None,
}
QUALIFIERS = re.compile(r"\b(const|volatile|restrict|__restrict|_Nullable|_Nonnull|_Null_unspecified|struct|union|enum)\b")


STUB_HEADERS = """math.h stdio.h stdlib.h string.h strings.h wchar.h ctype.h errno.h limits.h time.h assert.h signal.h
setjmp.h locale.h inttypes.h float.h sys/types.h malloc.h memory.h sal.h intrin.h process.h io.h direct.h
windows.h crtdefs.h _mingw.h""".split()


class Unsupported(Exception):
    pass


class Package:
    def __init__(self, folder):
        self.folder = folder
        with open(os.path.join(folder, "package.json")) as f:
            self.info = json.load(f)
        self.bind = self.info["bindings"]
        self.alias = self.bind["alias"]
        self.typedefs = {}       # name -> C type it stands for
        self.records = {}        # name -> the RecordDecl with its fields (the complete one)
        self.enums = []          # (name, [(constant, value, comment)])
        self.enum_names = set()
        self.unsigned_enums = set()
        self.functions = []      # FunctionDecl nodes of the package's own headers
        self.constants = {}      # every constant's value, for working out #defines
        self.order = []          # ("struct", name) in the order C declares them
        self.used_opaque = []
        self.left_out = []
        self.written = []
        self.own_typedefs = []
        self.typedef_names = {}  # a struct's tag -> the typedef C code names it by (z_stream_s -> z_stream)
        self.lines = {}          # header -> its lines, for the comment at the end of one

    # ---- reading ----

    def header_paths(self):
        return [os.path.join(self.folder, h) for h in self.bind["headers"]]

    def own_files(self):
        """The package's own headers: those named, and every one in the folder "own" names."""
        own = [os.path.realpath(h) for h in self.header_paths()]
        if "own" in self.bind:
            folder = os.path.join(self.folder, self.bind["own"])
            own += sorted(os.path.realpath(os.path.join(folder, n)) for n in os.listdir(folder) if n.endswith(".h"))
        return list(dict.fromkeys(own))

    def parse(self):
        own = set(self.own_files())
        for h in own:
            with open(h, encoding="utf-8", errors="replace") as f:
                self.lines[h] = f.read().split("\n")
        self.index_comments()
        includes = "".join(f'#include "{os.path.realpath(h)}"\n' for h in self.header_paths())
        args = ["clang", "-x", "c", "-std=c11", "-fsyntax-only", "-Xclang", "-ast-dump=json", "-"] + self.bind.get("clang", [])
        args += ["-I" + os.path.join(self.folder, i) for i in self.bind.get("include", ["include"])]
        done = subprocess.run(args, input=includes.encode(), capture_output=True)
        if done.returncode != 0:
            sys.exit(f"clang couldn't read {self.info['name']}'s headers:\n{done.stderr.decode()}")
        tree = json.loads(done.stdout)
        current = [None]

        def place(loc):
            # (clang names the file only where it changes, in the order it writes the tree)
            for part in (loc.get("spellingLoc"), loc.get("expansionLoc"), loc):
                if part and "file" in part:
                    current[0] = os.path.realpath(part["file"])

        def walk(node):
            if "loc" in node:
                place(node["loc"])
            if "range" in node:
                place(node["range"].get("begin", {}))
                place(node["range"].get("end", {}))
            for child in node.get("inner", []):
                if isinstance(child, dict):
                    walk(child)

        named = {}               # a struct or enum with no name of its own: its typedef's
        for node in tree["inner"]:
            if node["kind"] == "TypedefDecl":
                for inner in node.get("inner", []):
                    owned = inner.get("ownedTagDecl")
                    if owned and not owned.get("name"):
                        named[owned["id"]] = node["name"]
        for node in tree["inner"]:
            if "loc" in node:
                place(node["loc"])
            mine = current[0] in own
            node["_file"] = current[0]
            kind = node["kind"]
            if kind == "TypedefDecl":
                self.typedefs[node["name"]] = node["type"]["qualType"]
                if mine:
                    self.own_typedefs.append(node["name"])
            elif kind == "RecordDecl":
                name = node.get("name") or named.get(node["id"])
                if name and node.get("completeDefinition"):
                    node["_name"] = name
                    self.records[name] = node
                    if mine:
                        self.order.append(name)
            elif kind == "EnumDecl":
                name = node.get("name") or named.get(node["id"]) or ""
                if name:
                    self.enum_names.add(name)
                values, at = [], 0
                for c in node.get("inner", []):
                    if c["kind"] != "EnumConstantDecl":
                        continue
                    value = self.find_value(c)
                    at = value if value is not None else at
                    values.append((c["name"], at, self.comment(node["_file"], c)))
                    self.constants[c["name"]] = at
                    at += 1
                if name and any(v >= 2**31 for _, v, _ in values) and not any(v < 0 for _, v, _ in values):
                    self.unsigned_enums.add(name)
                if mine:
                    self.enums.append((name, values))
            elif kind == "FunctionDecl" and mine and node.get("storageClass") != "static" or \
                    kind == "FunctionDecl" and mine and self.bind.get("static_inline"):
                self.functions.append(node)
            walk(node)
        for name, target in self.typedefs.items():
            tag = re.sub(r"\s+", " ", QUALIFIERS.sub("", target)).strip()
            if tag != name and tag in self.records and name not in self.records and "*" not in target and "[" not in target \
                    and self.records[tag].get("name") and tag not in self.typedef_names:
                self.typedef_names[tag] = name

    @staticmethod
    def find_value(node):
        for child in node.get("inner", []):
            if "value" in child and child["kind"] in ("ConstantExpr", "IntegerLiteral"):
                return int(child["value"])
            found = Package.find_value(child)
            if found is not None:
                return found
        return None

    def index_comments(self):
        """Where each name is declared in the headers, and the comment at the end of its line."""
        self.declared, self.after_name, self.after_function = {}, {}, {}
        for lines in self.lines.values():
            for i, line in enumerate(lines):
                start = line.lstrip()
                if not start or start[0] in "*/#":
                    continue
                code = re.split(r"//|/\*", line)[0]             # (not the names a comment mentions)
                called = re.findall(r"\b([A-Za-z_]\w*)\s*\(", code)
                for name in called:
                    self.declared.setdefault(name, (lines, i))
                c = re.search(r"/(?:/|\*\*?<)\s*(.*?)\s*(?:\*/)?\s*$", line)
                if not c or not c.group(1):
                    continue
                first = re.match(r"\s*([A-Za-z_]\w*)", line)
                if first:
                    self.after_name.setdefault(first.group(1), c.group(1))
                if re.search(r"\)\s*;\s*/", line):
                    for name in called:
                        self.after_function.setdefault(name, c.group(1))

    def comment(self, file, node):
        return self.after_name.get(node["name"], "")

    def said_before(self, name):
        """The first sentence of the /** comment above a function's declaration."""
        if name not in self.declared:
            return ""
        lines, i = self.declared[name]
        j = i - 1
        while j >= 0 and "*/" not in lines[j] and lines[j].strip() and i - j < 6:
            j -= 1
        if j < 0 or "*/" not in lines[j] or not lines[j].lstrip().startswith(("*", "/")):
            return ""                                # (a comment of its own lines, not one after code)
        while j >= 0 and "/**" not in lines[j] and "/*" not in lines[j]:
            j -= 1
        text = []
        for doc in lines[max(j, 0):i]:
            doc = doc.strip().lstrip("/").strip("*").strip().rstrip("/").rstrip("*").strip()
            if not doc and text:
                break
            if doc.startswith(("\\", "@")):
                break
            if doc:
                text.append(doc)
        said = " ".join(text)
        for drop in self.bind.get("comment_drop", []):
            said = re.sub(drop, "", said).strip()
        return (said[:said.index(". ") + 1] if ". " in said else said)[:160]

    # ---- types ----

    def phi_type(self, c, pointer_depth=0):
        """The PHI type for a C type written as clang writes it; raises Unsupported."""
        c = c.strip()
        if "(*" in c or "(^" in c:
            return "ptr<u8>"                         # a function's address
        if c.endswith("]"):
            raise Unsupported(f"an array ({c}) where a value goes")
        if c.endswith("*"):
            return self.pointer_to(c[:-1])
        if c.endswith(")"):
            return "ptr<u8>"
        base = QUALIFIERS.sub("", c).strip()
        base = re.sub(r"\s+", " ", base)
        if base in self.bind.get("types", {}):
            return self.bind["types"][base]
        if base in BUILTIN:
            if BUILTIN[base] is None:
                raise Unsupported(base)
            return BUILTIN[base]
        if base in self.records:
            if base not in self.order:
                raise Unsupported(f"a struct of the system's own ({base})")
            return self.struct_name(base)
        if base in self.enum_names:
            return "u32" if base in self.unsigned_enums else "int"
        if base in self.typedefs and self.typedefs[base] != c:
            return self.phi_type(self.typedefs[base])
        if base == "void":
            raise Unsupported("void as a value")
        raise Unsupported(f"a type that isn't whole here ({base})")

    def pointer_to(self, c):
        c = c.strip()
        base = re.sub(r"\s+", " ", QUALIFIERS.sub("", c)).strip()
        if base in ("void", "char", "signed char", "unsigned char"):
            return "ptr<u8>"
        if "(" in base:
            return "ptr<u8>"
        seen = set()
        while base in self.typedefs and base not in self.records and base not in BUILTIN and base not in seen \
                and base not in self.bind.get("types", {}):
            seen.add(base)
            target = self.typedefs[base]
            if "(" in target:
                return "ptr<u8>"
            if target.strip().endswith("*"):
                return f"ptr<{self.pointer_to(target.strip()[:-1])}>"
            base = re.sub(r"\s+", " ", QUALIFIERS.sub("", target)).strip()
        if base in ("void", "char", "signed char", "unsigned char"):
            return "ptr<u8>"
        if base.endswith("*"):
            return f"ptr<{self.pointer_to(base[:-1])}>"
        try:
            return f"ptr<{self.phi_type(base)}>"
        except Unsupported:
            if base in self.records and base not in self.order:
                return "ptr<u8>"                     # (C's FILE and the like: an address, and no more)
        # a struct only ever handled by its address: the library's own business what's in it
        if re.fullmatch(r"\w+", base):
            if base not in self.used_opaque:
                self.used_opaque.append(base)
            return f"ptr<{self.struct_name(base)}>"
        raise Unsupported(f"a pointer to {c}")

    def struct_name(self, c_name):
        return self.bind.get("rename", {}).get(c_name) or self.typedef_names.get(c_name, c_name)

    def c_name(self, name):
        """A struct as C names it: by its tag (struct z_stream_s), or its typedef when it has none."""
        node = self.records[name]
        return f"{node.get('tagUsed', 'struct')} {name}" if node.get("name") else name

    @staticmethod
    def safe(name, taken=()):
        if not name:
            name = "value"
        while name in RESERVED or name in taken:
            name += "_"
        return name

    # ---- #defines ----

    def defines(self):
        """(name, kind, value, comment) for each #define that's a number or a struct's values."""
        found, seen, names, notes = [], set(), [], {}
        skip = set(self.bind.get("skip", []))
        skip_like = [re.compile(p) for p in self.bind.get("skip_like", [])]
        for header in self.own_files():
            text = "\n".join(self.lines[header])
            text = re.sub(r"\\\n", " ", text)
            for line in text.split("\n"):
                m = re.match(r"\s*#\s*define\s+([A-Za-z_]\w*)(\(?)(.*)$", line)
                if not m or m.group(2) == "(":
                    continue
                name, rest = m.group(1), m.group(3)
                note = ""
                c = re.search(r"//(.*)$|/\*\*?<?(.*?)\*/\s*$", rest)
                if c:
                    note = (c.group(1) or c.group(2) or "").strip()
                    rest = rest[:c.start()]
                rest = rest.strip()
                if not rest or name in seen or name in skip or name.startswith("_") or name in self.constants \
                        or any(p.search(name) for p in skip_like):
                    continue
                literal = re.fullmatch(r"(?:CLITERAL\((\w+)\)|\((\w+)\))\s*\{(.*)\}", rest)
                if literal:
                    struct = literal.group(1) or literal.group(2)
                    if struct in self.records:
                        seen.add(name)
                        found.append((name, "struct", (struct, [v.strip() for v in literal.group(3).split(",")]), note))
                    continue
                seen.add(name)
                names.append(name)
                notes[name] = note
        values = self.worked_out(names)
        numbers = []
        for name in names:
            if name in values:
                kind, value = values[name]
                self.constants[name] = value
                numbers.append((name, kind, value, notes[name]))
        return numbers + found

    KINDS = ('_Bool:"int", char:"int", signed char:"int", unsigned char:"int", short:"int", unsigned short:"int", int:"int", '
             'unsigned int:"u32", long:"i64", unsigned long:"u64", long long:"i64", unsigned long long:"u64", float:"f32", double:"f64"')

    def worked_out(self, names):
        """Each #define that is a number, with its C type, as C itself works it out: a small
        program that prints them is compiled and run, without the lines C won't take
        (a #define that's text, a type, a struct's values, or part of a declaration)."""
        if not names:
            return {}
        head = "".join(f'#include "{os.path.realpath(h)}"\n' for h in self.header_paths())
        head += "#include <stdio.h>\nint main(void)\n{\n"
        lines = [f'    printf("{n}\\t%s\\t%lld\\t%llu\\t%.17g\\n", _Generic(({n}), {self.KINDS}), (long long)({n}), (unsigned long long)({n}), (double)({n}));'
                 for n in names]
        first = head.count("\n") + 1
        args = ["clang", "-x", "c", "-std=c11", "-w", "-ferror-limit=0"] + self.bind.get("clang", [])
        args += ["-I" + os.path.join(self.folder, i) for i in self.bind.get("include", ["include"])]
        with tempfile.TemporaryDirectory() as work:
            source, program = os.path.join(work, "values.c"), os.path.join(work, "values")
            for _ in range(8):
                with open(source, "w") as f:
                    f.write(head + "\n".join(lines) + "\n    return 0;\n}\n")
                done = subprocess.run(args + ["-fsyntax-only", source], capture_output=True, text=True)
                bad = {int(m.group(1)) for m in re.finditer(rf"{re.escape(source)}:(\d+):\d+: (?:fatal )?error", done.stderr)}
                if done.returncode == 0:
                    break
                if not bad:
                    sys.exit(f"clang couldn't work out {self.info['name']}'s #defines:\n{done.stderr[:2000]}")
                lines = [l if first + k not in bad else "" for k, l in enumerate(lines)]
            lines = [l for l in lines]
            # (one that names a function of the library's would need the library: left out by the linker's word)
            for _ in range(8):
                with open(source, "w") as f:
                    f.write(head + "\n".join(lines) + "\n    return 0;\n}\n")
                done = subprocess.run(args + [source, "-o", program], capture_output=True, text=True)
                if done.returncode == 0:
                    break
                missing = set(re.findall(r'"_(\w+)", referenced from', done.stderr)) | set(re.findall(r"undefined reference to `(\w+)'", done.stderr))
                if not missing:
                    sys.exit(f"clang couldn't build {self.info['name']}'s #defines:\n{done.stderr[:2000]}")
                # (which #define used it isn't said: leave out each whose text, after the preprocessor, names it)
                expanded = subprocess.run(args + ["-E", "-P", source], capture_output=True, text=True).stdout.split("\n")
                body = [l for l in expanded if l.lstrip().startswith("printf(")]
                kept = [l for l in lines if l]
                drop = {k for k, l in enumerate(body) if any(re.search(rf"\b{re.escape(fn)}\b", l) for fn in missing)}
                kept = [l for k, l in enumerate(kept) if k not in drop]
                lines = kept
            out = subprocess.run([program], capture_output=True, text=True).stdout
        values = {}
        for line in out.split("\n"):
            parts = line.split("\t")
            if len(parts) != 5:
                continue
            name, kind, signed, unsigned, real = parts
            if kind in ("f32", "f64"):
                values[name] = (kind, float(real))
            elif kind in ("u32", "u64"):
                value = int(unsigned)
                values[name] = ("u32" if value < 2**32 else "u64", value) if kind == "u64" or value >= 2**31 else ("int", value)
                if kind == "u32":
                    values[name] = ("u32", value)
            else:
                value = int(signed)
                values[name] = ("int" if -2**31 <= value < 2**31 else "i64", value)
        return values

    def evaluate(self, text):
        """A #define's value as (PHI type, number), when it's numbers and constants; else None."""
        if '"' in text or "'" in text or "{" in text:
            return None
        is_float = False
        is_f32 = False
        unsigned = False
        out = []
        for token in re.findall(r"0[xX][0-9a-fA-F]+[uUlL]*|\d+\.\d*(?:[eE][-+]?\d+)?[fFlL]?|\.\d+[fF]?|\d+[eE][-+]?\d+[fF]?|\d+[uUlLfF]*|[A-Za-z_]\w*|<<|>>|[-+*/|&~()^]|\S", text):
            if re.fullmatch(r"0[xX][0-9a-fA-F]+[uUlL]*", token):
                unsigned = unsigned or "u" in token.lower()
                out.append(str(int(token.rstrip("uUlL"), 16)))
            elif re.fullmatch(r"\d+[uUlL]*", token):
                unsigned = unsigned or "u" in token.lower()
                out.append(token.rstrip("uUlL"))
            elif re.fullmatch(r"(\d+\.\d*(?:[eE][-+]?\d+)?|\.\d+|\d+[eE][-+]?\d+|\d+)[fFlL]?", token):
                is_float = True
                is_f32 = is_f32 or token[-1] in "fF"
                out.append(token.rstrip("fFlL"))
            elif re.fullmatch(r"[A-Za-z_]\w*", token):
                if token not in self.constants:
                    return None
                value = self.constants[token]
                if isinstance(value, float):
                    is_float = True
                    is_f32 = True
                out.append(repr(value))
            elif token in ("<<", ">>", "-", "+", "*", "/", "|", "&", "~", "(", ")", "^"):
                out.append(token)
            else:
                return None
        expression = " ".join(out)
        if not is_float:
            expression = expression.replace("/", "//")
        try:
            value = eval(expression, {"__builtins__": {}}, {})
        except Exception:
            return None
        if is_float:
            return ("f32" if is_f32 else "f64", float(value))
        if not isinstance(value, int):
            return None
        if -2**31 <= value < 2**31 and not (unsigned and value >= 2**31):
            return ("int", value)
        if 0 <= value < 2**32:
            return ("u32", value)
        if -2**63 <= value < 2**63:
            return ("i64", value)
        return ("u64", value) if value < 2**64 else None

    # ---- writing ----

    def function_line(self, node):
        name = node["name"]
        fn_type = node["type"]["qualType"]
        result_c = fn_type[:fn_type.index("(")].strip()
        params, taken = [], set()
        for p in node.get("inner", []):
            if p["kind"] != "ParmVarDecl":
                continue
            pname = self.safe(p.get("name") or f"p{len(params) + 1}", taken)
            taken.add(pname)
            params.append(f"{self.phi_type(p['type']['qualType'])} {pname}")
        result = "" if result_c == "void" else self.phi_type(result_c)
        strip = self.bind.get("strip", "")
        short = name[len(strip):] if strip and name.startswith(strip) and len(name) > len(strip) else name
        short = self.bind.get("functions", {}).get(name, short)
        head = f"{self.alias}.{short}" + (f" = '{name}'" if short != name else "")
        variadic = node.get("variadic") or fn_type.rstrip(")").endswith("...")
        body = " ".join(params) + (" ..." if variadic else "")
        if result in ("", "int"):
            return head + (f": {body};" if body else ";")
        return f"{head}: {body}: {result};" if body else f"{head}: : {result};"

    def struct_text(self, name):
        node = self.records[name]
        if node.get("tagUsed") == "union":
            return self.union_text(name)
        fields, taken = [], set()
        for f in node.get("inner", []):
            if f["kind"] != "FieldDecl":
                continue
            if f.get("isBitfield"):
                raise Unsupported("a bitfield")
            c = f["type"]["qualType"].strip()
            if "(unnamed" in c or "(anonymous" in c:
                # a union or struct written inside it: its room, with nothing of it named
                if not f.get("name"):
                    raise Unsupported("a union with no name inside it")
                fields.append(self.filler(f"(({self.c_name(name)} *)0)->{f['name']}", f["name"], taken) + "    # (a union in C: read through a pointer)")
                taken.add(self.safe(f["name"], taken))
                continue
            counts = re.findall(r"\[(\d+)\]", c) if c.endswith("]") and "(" not in c else []
            if c.endswith("]") and "(" not in c and not counts:
                raise Unsupported(f"an array with no size ({c})")
            base = c[:c.index("[")] if counts else c
            count = 1
            for n in counts:
                count *= int(n)
            fname = self.safe(f.get("name") or f"field{len(fields) + 1}", taken)
            taken.add(fname)
            kind = self.bind.get("fields", {}).get(f"{name}.{f.get('name')}") or self.phi_type(base)
            fields.append(f"\t{kind} {fname}" + (f"[{count}];" if counts else ";")
                          + (f"    # {'x'.join(counts)}" if len(counts) > 1 else ""))
        if not fields:
            raise Unsupported("nothing in it")
        return f"struct.{self.struct_name(name)} foreign\n{{\n" + "\n".join(fields) + "\n}\n"

    def union_text(self, name):
        """A union: what all of its forms start with, when that's one number, and its room."""
        node = self.records[name]
        members = [f for f in node.get("inner", []) if f["kind"] == "FieldDecl"]
        first = members[0] if members else None
        lead = None
        if first is not None:
            try:
                kind = self.phi_type(first["type"]["qualType"])
                if kind in ("int", "u32", "i16", "u16", "u8", "i8", "i64", "u64"):
                    lead = (kind, first["name"])
            except Unsupported:
                pass
        whole = self.filler(self.c_name(name), "data", set())
        m = re.match(r"\t(u\d+) data\[(\d+)\];", whole)
        text = f"struct.{self.struct_name(name)} foreign    # a union in C: each of its forms is read through a pointer to that form\n{{\n"
        if lead and m:
            unit, count = int(m.group(1)[1:]) // 8, int(m.group(2))
            mine = {"int": 4, "u32": 4, "i16": 2, "u16": 2, "u8": 1, "i8": 1, "i64": 8, "u64": 8}[lead[0]]
            if mine <= unit and count > 1:
                text += f"\t{lead[0]} {self.safe(lead[1])};\n"
                if unit > mine:
                    text += f"\tu8 unused[{unit - mine}];\n"
                text += f"\t{m.group(1)} rest[{count - 1}];\n}}\n"
                return text
        return text + whole + "\n}\n"

    def write(self):
        info, bind = self.info, self.bind
        out = []
        say = out.append
        title = f"{info['name']} {info['version']}"
        say(f"# {title}: {info.get('about', '')}".rstrip(": ").rstrip())
        say(f"# {info.get('homepage', '')}  ({info.get('license', '')} licence: see LICENSE beside this file)")
        say("#")
        say(f"# Written by vendor/bindgen.py from {', '.join(os.path.basename(h) for h in bind['headers'])}: change package.json")
        say("# and run it again, rather than this file. Functions, structs and constants have C's names.")
        for line in bind.get("notes", []):
            say(f"# {line}")
        files = info["libraries"]
        say("use " + " ".join(f"'{files[s]}'" for s in ("windows", "macos", "linux") if s in files) + f" as {self.alias};")
        say("")

        defines = self.defines()
        structs, skipped_structs = [], set()
        for name in self.order:
            try:
                structs.append(self.struct_text(name))
                self.written.append(name)
            except Unsupported as why:
                skipped_structs.add(name)
                self.left_out.append(f"struct {name}: {why}")
        functions = []
        for node in self.functions:
            if node["name"] in bind.get("skip", []):
                continue
            try:
                note = self.after_function.get(node["name"]) or self.said_before(node["name"])
                functions.append(self.function_line(node) + (f"    # {note}" if note else ""))
            except Unsupported as why:
                self.left_out.append(f"{node['name']}: {why}")

        say(f"phi.{self.alias}:Library")
        say("{")
        strip = bind.get("strip_constants", "")
        emitted = set()

        def constant(name, kind, value, note=""):
            note = f"    # {note}" if note else ""
            short = name[len(strip):] if strip and name.startswith(strip) and len(name) > len(strip) else name
            short = self.safe(short, emitted)
            emitted.add(short)
            if kind in ("f32", "f64"):
                text = repr(value)
                if "inf" in text or "nan" in text:
                    self.left_out.append(f"#define {name}: {text} isn't a number PHI's constants write")
                    return
                if "e" in text:
                    text = f"{value:.60f}".rstrip("0")
                say(f"\tconst {kind} {short}: {text if '.' in text else text + '.0'};{note}")
            elif kind in ("i64", "u64"):
                self.left_out.append(f"#define {name}: {value} is past 32 bits, which a PHI constant isn't yet")
                emitted.discard(short)
            else:
                say(f"\tconst {kind} {short}: {value};{note}")

        numbers = [d for d in defines if d[1] != "struct"]
        if numbers:
            for name, kind, value, note in numbers:
                constant(name, kind, value, note)
            say("")
        for name, values in self.enums:
            if name in bind.get("skip", []):
                continue
            say(f"\t# {name}" if name else "\t# (an enum with no name)")
            for cname, value, note in values:
                constant(cname, "int" if -2**31 <= value < 2**31 else "u32", value, note)
            say("")
        literals = [d for d in defines if d[1] == "struct" and d[2][0] not in skipped_structs]
        if literals:
            say("\t# values of a struct C has as constants: here variables, set when the program starts")
            for name, kind, (struct, values), note in literals:
                say(f"\t{self.struct_name(struct)} {self.safe(name, emitted)};")
            for name, kind, (struct, values), note in literals:
                fields = [f for f in self.records[struct].get("inner", []) if f["kind"] == "FieldDecl"]
                for f, v in zip(fields, values):
                    number = self.evaluate(v)
                    if number is not None and number[1] != 0:
                        say(f"\t{self.safe(name, emitted)}.{f['name']} is {number[1]};")
        for line in bind.get("startup", []):
            say("\t" + line)
        inside = os.path.join(self.folder, "class.phi")
        if os.path.exists(inside):
            # (written by hand: what C has as macros, as methods of the library's)
            say("")
            with open(inside) as f:
                say(f.read().rstrip())
        while out and out[-1] == "":
            out.pop()
        say("}")
        say("")
        for name in self.used_opaque + sorted(skipped_structs):
            if name in self.records and name not in skipped_structs:
                continue
            say(f"struct.{self.struct_name(name)} foreign    # only ever held by its address: what's in it is {info['name']}'s own")
            say("{\n\tint opaque;\n}\n")
        for text in structs:
            say(text)
        # a typedef of a struct is the same struct under another name
        others = []
        for name in self.own_typedefs:
            target, seen = name, set()
            while target in self.typedefs and target not in self.records and target not in seen:
                seen.add(target)
                target = re.sub(r"\s+", " ", QUALIFIERS.sub("", self.typedefs[target])).strip()
            if target != name and target in self.written and name not in self.records and self.struct_name(target) != name:
                others.append(f"struct.{self.struct_name(name)} is {self.struct_name(target)};")
        for line in others:
            say(line)
        if others:
            say("")
        for line in functions:
            say(line)
        extra = os.path.join(self.folder, bind.get("extra", "extra.phi"))
        if os.path.exists(extra):
            say("")
            with open(extra) as f:
                say(f.read().rstrip())
        target = os.path.join(self.folder, bind.get("output", info["name"] + ".phi"))
        with open(target, "w", newline="\n") as f:
            f.write("\n".join(out) + "\n")
        print(f"{os.path.relpath(target)}: {len(functions)} functions, {len(structs)} structs, "
              f"{len(numbers) + sum(len(v) for _, v in self.enums)} constants, {len(literals)} struct values")
        for line in self.left_out:
            print(f"  left out: {line}")
        self.write_layout_test(set(self.written))
        report = os.path.join(self.folder, "left-out.txt")
        if self.left_out:
            with open(report, "w", newline="\n") as f:
                f.write("What vendor/bindgen.py couldn't declare in PHI, and why:\n\n" + "\n".join(self.left_out) + "\n")
        elif os.path.exists(report):
            os.remove(report)


    # ---- a test of the structs' sizes ----

    MODELS = [("macOS and 64-bit Linux", "arm64-apple-macos11", 8, 8), ("64-bit Windows", "x86_64-w64-mingw32", 8, 4),
              ("32-bit Windows", "i686-w64-mingw32", 4, 4)]

    def sizes(self, triple, names):
        """sizeof each struct as C has it for a system, asked of clang."""
        found = self.measure(triple, [f"sizeof({self.c_name(n)})" for n in names])
        return {n: found[f"sizeof({self.c_name(n)})"] for n in names}

    def measure(self, triple, expressions):
        """Each of some C expressions that are whole numbers when it's compiled, for a system."""
        source = "".join(f'#include "{os.path.realpath(h)}"\n' for h in self.header_paths())
        source += "enum {" + ", ".join(f"PHI_SIZE_{i} = (int)({e})" for i, e in enumerate(expressions)) + "};\n"
        args = ["clang", "-x", "c", "-std=c11", "-fsyntax-only", "-target", triple, "-Xclang", "-ast-dump=json", "-"] + self.bind.get("clang", [])
        args += ["-I" + os.path.join(self.folder, i) for i in self.bind.get("include", ["include"])]
        with tempfile.TemporaryDirectory() as stubs:
            if "apple" not in triple:
                # (this computer has no other system's C library: what a header takes from one is
                # declared here, enough for the sizes, and the rest comes with clang itself)
                for name in STUB_HEADERS:
                    os.makedirs(os.path.dirname(os.path.join(stubs, name)), exist_ok=True)
                    with open(os.path.join(stubs, name), "w") as f:
                        f.write("#include <stddef.h>\n#include <stdint.h>\n#include <stdarg.h>\ntypedef struct FILE FILE;\n")
                args += ["-D__int64=long long", "-ffreestanding", "-Wno-implicit-function-declaration", "-Wno-error=implicit-function-declaration", "-isystem", stubs]
            done = subprocess.run(args, input=source.encode(), capture_output=True)
        if done.returncode != 0:
            sys.exit(f"clang couldn't read {self.info['name']}'s headers for {triple}:\n{done.stderr.decode()[:2000]}")
        found = {}
        for node in json.loads(done.stdout)["inner"]:
            if node["kind"] == "EnumDecl":
                for c in node.get("inner", []):
                    if c.get("name", "").startswith("PHI_SIZE_"):
                        found[expressions[int(c["name"][9:])]] = self.find_value(c)
        return found

    def filler(self, what, name, taken):
        """Fields that take up the room of something C has and PHI can't say (a union): as
        large, and on the same multiple, on every system. `what` is the C type's expression."""
        per = [self.measure(triple, [f"sizeof({what})", f"_Alignof({what})"]) for _, triple, _, _ in self.MODELS]
        shapes = [(p[f"sizeof({what})"], p[f"_Alignof({what})"]) for p in per]
        pointer = [m[2] for m in self.MODELS]
        name = self.safe(name, taken)
        if len(set(shapes)) == 1:
            size, align = shapes[0]
            return f"\t{ {1: 'u8', 2: 'u16', 4: 'u32', 8: 'u64'}[align]} {name}[{size // align}];"
        # (as many addresses' worth on each system; where C wants it on a larger multiple than an
        # address, the struct's size in the test of sizes says whether it still falls right)
        if all(size % p == 0 for (size, align), p in zip(shapes, pointer)) and len({size // p for (size, _), p in zip(shapes, pointer)}) == 1:
            return f"\tusz {name}[{shapes[0][0] // pointer[0]}];"
        # some of it as wide as an address, and the rest 4 bytes at a time
        wide, narrow = next(s for s, p in zip(shapes, pointer) if p == 8), next(s for s, p in zip(shapes, pointer) if p == 4)
        addresses, fours = (wide[0] - narrow[0]) // 4, 0
        fours = (narrow[0] - 4 * addresses) // 4
        if addresses > 0 and fours >= 0 and all(size == addresses * p + 4 * fours and align == p for (size, align), p in zip(shapes, pointer)):
            return f"\tusz {name}[{addresses}];" + (f"\n\tu32 {self.safe(name + '_more', taken)}[{fours}];" if fours else "")
        raise Unsupported(f"a union whose size differs between systems in a way PHI's types don't follow ({shapes})")

    def write_layout_test(self, written):
        """tests/vendor/NAME_layout_program.phi: each struct's size beside C's, on the system it's built for."""
        names = [n for n in self.order if n in written]
        if not names:
            return
        per = [self.sizes(triple, names) for _, triple, _, _ in self.MODELS]
        program = "".join(w.capitalize() for w in re.split(r"\W+", self.info["name"])) + "LayoutProgram"
        out = [f"# Every struct of vendor/{self.info['name']} is as large as C has it, on the system this is built for:",
               "# written by vendor/bindgen.py, with C's sizes as clang gives them for " + ", ".join(m[0] for m in self.MODELS) + ".",
               f"use vendor.{self.info['name']};", "", f"phi.{program}:Program", "{",
               "\tint wrong: 0;", "\tint which: 2;",
               "\tif (sizeof ptr<u8>) is 8", "\t\twhich is 1;", "\t\tif (sizeof lng) is 8", "\t\t\twhich is 0;", "\t\t;;", "\t;;"]
        for n in names:
            out.append(f"\tcall Same: '{self.struct_name(n)}' (sizeof {self.struct_name(n)}) {per[0][n]} {per[1][n]} {per[2][n]};")
        out += [f"\tlog '{len(names)} structs, ' wrong ' wrong\\n';", "\texit wrong;", "",
                "\t[Same: ptr<u8> name; int here; int unix; int windows64; int windows32;]",
                "\t\tint c: unix;", "\t\tif which is 1", "\t\t\tc is windows64;", "\t\t;;",
                "\t\tif which is 2", "\t\t\tc is windows32;", "\t\t;;",
                "\t\tif here is not c", "\t\t\twrong++;", "\t\t\tlog name ' is ' here ' bytes here and ' c ' in C\\n';", "\t\t;;",
                "\t[end]", "}"]
        folder = os.path.join(HERE, "..", "tests", "vendor")
        os.makedirs(folder, exist_ok=True)
        base = os.path.join(folder, f"{self.info['name']}_layout_program")
        with open(base + ".phi", "w", newline="\n") as f:
            f.write("\n".join(out) + "\n")
        with open(base + ".expected", "w", newline="\n") as f:
            f.write(f"{len(names)} structs, 0 wrong\n")


def main():
    names = sys.argv[1:]
    if names == ["--all"]:
        names = sorted(n for n in os.listdir(HERE) if os.path.exists(os.path.join(HERE, n, "package.json")))
    if not names:
        sys.exit(__doc__)
    for name in names:
        package = Package(os.path.join(HERE, name))
        if "bindings" not in package.info:
            continue
        package.parse()
        package.write()


if __name__ == "__main__":
    main()

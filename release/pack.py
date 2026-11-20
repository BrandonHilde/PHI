#!/usr/bin/env python3
"""Packs PHI for a release: one .zip for each system, with nothing else to install but the
assembler and emulator PHI drives (NASM, QEMU; on a Mac, Apple's Command Line Tools).

    python3 release/pack.py 0.1.0                 # dist/phi-0.1.0-windows-x64.zip, -linux-x64, -macos-arm64
    python3 release/pack.py 0.1.0 macos-arm64     # one of them

Each has: phi, as one file that needs no .NET; the vendor folder with its libraries for
every system PHI builds for (run vendor/fetch.py first); the documentation as HTML; the
standard library's source to read (it's inside phi already); the samples; the licences.
It needs the .NET SDK. Nothing here uploads anything.
"""
import html
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")
SYSTEMS = {"windows-x64": "win-x64", "linux-x64": "linux-x64", "macos-arm64": "osx-arm64"}
LIBRARY_FOLDERS = ("windows64", "windows", "macos", "linux64")

STYLE = """
body { font: 16px/1.55 -apple-system, "Segoe UI", Helvetica, Arial, sans-serif; color: #1c1e21; background: #fff;
       max-width: 860px; margin: 0 auto; padding: 24px 20px 80px; }
h1, h2, h3, h4 { line-height: 1.25; margin: 1.6em 0 0.5em; }
h1 { font-size: 2em; } h2 { font-size: 1.5em; border-bottom: 1px solid #ddd; padding-bottom: 0.2em; } h3 { font-size: 1.2em; }
a { color: #0b5cad; } img { max-width: 100%; }
code { font: 0.9em ui-monospace, Menlo, Consolas, monospace; background: #f2f3f5; padding: 0.1em 0.3em; border-radius: 4px; }
pre { background: #f6f7f9; border: 1px solid #e2e4e8; border-radius: 6px; padding: 12px 14px; overflow-x: auto; tab-size: 4; }
pre code { background: none; padding: 0; font-size: 0.88em; }
table { border-collapse: collapse; margin: 1em 0; display: block; overflow-x: auto; }
th, td { border: 1px solid #d5d8dc; padding: 6px 10px; text-align: left; vertical-align: top; }
th { background: #f2f3f5; }
blockquote { margin: 1em 0; padding: 0 1em; color: #555; border-left: 4px solid #ddd; }
@media (prefers-color-scheme: dark) {
  body { color: #e3e5e8; background: #17191c; } a { color: #6cb2ff; }
  code { background: #262a2f; } pre { background: #1f2226; border-color: #33373d; }
  th { background: #262a2f; } th, td { border-color: #3a3f46; } h2 { border-color: #33373d; } blockquote { color: #aaa; border-color: #444; }
}
"""


# ---- Markdown, as PHI's documents use it, to HTML ----

def inline(text):
    """A line's code, links, pictures and emphasis."""
    kept = []

    def keep(made):
        kept.append(made)
        return f"\x00{len(kept) - 1}\x00"

    text = re.sub(r"`([^`]+)`", lambda m: keep(f"<code>{html.escape(m.group(1))}</code>"), text)
    text = re.sub(r"<(https?://[^>\s]+)>", lambda m: keep(f'<a href="{m.group(1)}">{html.escape(m.group(1))}</a>'), text)
    text = re.sub(r"</?[a-zA-Z][^<>]*>", lambda m: keep(m.group(0)), text)          # (HTML written in the document)
    text = html.escape(text, quote=False)
    text = re.sub(r"!\[([^\]]*)\]\(([^)\s]+)\)", lambda m: f'<img alt="{m.group(1)}" src="{link(m.group(2))}">', text)
    text = re.sub(r"\[([^\]]+)\]\(([^)\s]+)\)", lambda m: f'<a href="{link(m.group(2))}">{m.group(1)}</a>', text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"(?<![\w*])\*([^*\s][^*]*)\*(?![\w*])", r"<em>\1</em>", text)
    return re.sub(r"\x00(\d+)\x00", lambda m: kept[int(m.group(1))], text)


def link(target):
    """A link to another document is to its HTML."""
    if re.match(r"[a-z]+:", target) or target.startswith("#"):
        return target
    path, _, anchor = target.partition("#")
    if path.endswith(".md"):
        path = path[:-3] + ".html"
    return path + ("#" + anchor if anchor else "")


def anchor(heading):
    plain = re.sub(r"<[^>]+>", "", heading)
    plain = html.unescape(plain).lower()
    return re.sub(r"\s", "-", re.sub(r"[^\w\s-]", "", plain))


def to_html(markdown, title):
    lines = markdown.replace("\r\n", "\n").split("\n")
    out, i, lists = [], 0, []            # lists: (indent, tag) of each list that's open

    def close_lists(to=0):
        while len(lists) > to:
            out.append(f"</li></{lists.pop()[1]}>")

    while i < len(lines):
        line = lines[i]
        fence = re.match(r"\s*```(\w*)\s*$", line)
        if fence:
            close_lists()
            body = []
            i += 1
            while i < len(lines) and not re.match(r"\s*```\s*$", lines[i]):
                body.append(lines[i])
                i += 1
            out.append(f"<pre><code>{html.escape(chr(10).join(body))}</code></pre>")
            i += 1
            continue
        if not line.strip():
            i += 1
            # (a list goes on past an empty line when its next line is indented or another item)
            if lists and i < len(lines) and not re.match(r"\s*([-*]|\d+\.)\s|\s{2,}\S", lines[i]):
                close_lists()
            continue
        heading = re.match(r"(#{1,6})\s+(.*?)\s*#*$", line)
        if heading:
            close_lists()
            text = inline(heading.group(2))
            level = len(heading.group(1))
            out.append(f'<h{level} id="{anchor(text)}">{text}</h{level}>')
            i += 1
            continue
        if line.lstrip().startswith("|") and i + 1 < len(lines) and re.match(r"\s*\|[\s:|-]+\|\s*$", lines[i + 1]):
            close_lists()
            cells = lambda row: [c.strip() for c in re.split(r"(?<!\\)\|", row.strip().strip("|"))]
            out.append("<table><thead><tr>" + "".join(f"<th>{inline(c)}</th>" for c in cells(line)) + "</tr></thead><tbody>")
            i += 2
            while i < len(lines) and lines[i].lstrip().startswith("|"):
                out.append("<tr>" + "".join(f"<td>{inline(c.replace(chr(92) + '|', '|'))}</td>" for c in cells(lines[i])) + "</tr>")
                i += 1
            out.append("</tbody></table>")
            continue
        item = re.match(r"(\s*)([-*]|\d+\.)\s+(.*)$", line)
        if item:
            indent, tag = len(item.group(1)), "ol" if item.group(2)[0].isdigit() else "ul"
            while lists and lists[-1][0] > indent:
                out.append(f"</li></{lists.pop()[1]}>")
            if lists and lists[-1][0] == indent:
                out.append("</li><li>")
            else:
                lists.append((indent, tag))
                out.append(f"<{tag}><li>")
            text = [item.group(3)]
            i += 1
            while i < len(lines) and lines[i].strip() and not re.match(r"\s*([-*]|\d+\.)\s|\s*```|\s*\|", lines[i]) and lines[i].startswith(" "):
                text.append(lines[i].strip())
                i += 1
            out.append(inline(" ".join(text)))
            continue
        if line.startswith(">"):
            close_lists()
            quote = []
            while i < len(lines) and lines[i].startswith(">"):
                quote.append(lines[i][1:].strip())
                i += 1
            out.append(f"<blockquote><p>{inline(' '.join(quote))}</p></blockquote>")
            continue
        if re.match(r"\s*<(h\d|img|div|p|table|details|summary|br|hr)\b", line) and not lists:
            out.append(line)                 # (HTML written in the document, as it is)
            i += 1
            continue
        text = []
        while i < len(lines) and lines[i].strip() and not re.match(r"\s*```|#{1,6}\s|\s*\|.*\|\s*$|>", lines[i]) \
                and not (re.match(r"\s*([-*]|\d+\.)\s", lines[i]) and text):
            text.append(lines[i].strip())
            i += 1
        if not text:                         # (a line nothing above took: as a paragraph of its own)
            text, i = [line.strip()], i + 1
        if lists and line.startswith(" "):
            out.append(f"<p>{inline(' '.join(text))}</p>")
        else:
            close_lists()
            out.append(f"<p>{inline(' '.join(text))}</p>")
    close_lists()
    return (f'<!doctype html>\n<html lang="en">\n<head>\n<meta charset="utf-8">\n<meta name="viewport" content="width=device-width, initial-scale=1">\n'
            f"<title>{html.escape(title)}</title>\n<style>{STYLE}</style>\n</head>\n<body>\n" + "\n".join(out) + "\n</body>\n</html>\n")


def title_of(markdown, fallback):
    m = re.search(r"^#\s+(.+)$", markdown, re.M)
    return re.sub(r"[`*]", "", m.group(1)) if m else fallback


# ---- what goes in ----

def copy_documents(source, target, skip=()):
    """A folder of documents: each .md as .html beside whatever else is there."""
    for folder, folders, files in os.walk(source):
        folders[:] = [f for f in folders if f not in ("build", "bin", "obj", ".cache", "src", "include") + tuple(skip) and not f.startswith(".")]
        for name in files:
            if name.startswith(".") or name.endswith((".actual", ".img")):
                continue
            path = os.path.join(folder, name)
            to = os.path.join(target, os.path.relpath(path, source))
            os.makedirs(os.path.dirname(to), exist_ok=True)
            if name.endswith(".md"):
                with open(path, encoding="utf-8") as f:
                    text = f.read()
                with open(to[:-3] + ".html", "w", encoding="utf-8", newline="\n") as f:
                    f.write(to_html(text, title_of(text, name[:-3])))
            else:
                shutil.copy2(path, to)


def publish(rid):
    out = os.path.join(DIST, "build", rid)
    done = subprocess.run(["dotnet", "publish", os.path.join(ROOT, "Compiler", "Phi.Cli"), "-c", "Release", "-r", rid, "--self-contained", "true",
                           "-p:PublishSingleFile=true", "-p:DebugType=none", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", out],
                          capture_output=True, text=True)
    if done.returncode != 0:
        sys.exit(f"dotnet publish failed for {rid}:\n{done.stdout[-3000:]}{done.stderr[-2000:]}")
    return os.path.join(out, "phi.exe" if rid.startswith("win") else "phi")


def pack(version, system):
    rid = SYSTEMS[system]
    name = f"phi-{version}-{system}"
    folder = os.path.join(DIST, name)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    program = publish(rid)
    shutil.copy2(program, os.path.join(folder, os.path.basename(program)))

    # the vendor folder: each library whole, for every system a program is built for
    missing = []
    for library in sorted(os.listdir(os.path.join(ROOT, "vendor"))):
        source = os.path.join(ROOT, "vendor", library)
        if not os.path.isdir(source) or library.startswith("."):
            continue
        with open(os.path.join(source, "package.json")) as f:
            fetched = "downloads" in f.read() or "build" in open(os.path.join(source, "package.json")).read()
        if fetched and not all(os.path.isdir(os.path.join(source, s)) for s in ("windows64", "windows", "macos")):
            missing.append(library)
    if missing:
        sys.exit(f"the vendor folder hasn't got {', '.join(missing)}'s own files: run python3 vendor/fetch.py first")
    copy_documents(os.path.join(ROOT, "vendor"), os.path.join(folder, "vendor"))
    # (the compiler knows the vendor folder by its README.md: kept beside the HTML)
    shutil.copy2(os.path.join(ROOT, "vendor", "README.md"), os.path.join(folder, "vendor", "README.md"))

    copy_documents(os.path.join(ROOT, "docs"), os.path.join(folder, "docs"))
    copy_documents(os.path.join(ROOT, "samples"), os.path.join(folder, "samples"))
    copy_documents(os.path.join(ROOT, "lib", "phi"), os.path.join(folder, "lib", "phi"))
    shutil.copytree(os.path.join(ROOT, "resources"), os.path.join(folder, "resources"), ignore=shutil.ignore_patterns(".*"))
    for document in ("README.md", "CONTRIBUTING.md"):
        with open(os.path.join(ROOT, document), encoding="utf-8") as f:
            text = f.read()
        # (pictures the README names from the repository's root)
        text = text.replace('src="/resources/', 'src="resources/')
        with open(os.path.join(folder, document[:-3] + ".html"), "w", encoding="utf-8", newline="\n") as f:
            f.write(to_html(text, title_of(text, "PHI")))
    for plain in ("LICENSE", "NOTICE"):
        shutil.copy2(os.path.join(ROOT, plain), os.path.join(folder, plain))

    archive = os.path.join(DIST, name + ".zip")
    if os.path.exists(archive):
        os.remove(archive)
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for at, _, files in os.walk(folder):
            for f in sorted(files):
                path = os.path.join(at, f)
                inside = os.path.join(name, os.path.relpath(path, folder)).replace(os.sep, "/")
                info = zipfile.ZipInfo.from_file(path, inside)
                info.compress_type = zipfile.ZIP_DEFLATED
                # (what a Mac or Linux runs keeps its permission to be run)
                runs = f == "phi" or f.endswith((".dylib", ".py")) or ".so" in f
                info.external_attr = (0o755 if runs else 0o644) << 16
                with open(path, "rb") as data:
                    z.writestr(info, data.read(), compresslevel=9)
    print(f"{os.path.relpath(archive, ROOT)}: {os.path.getsize(archive) / 1e6:.1f} MB")


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    version = sys.argv[1]
    for system in sys.argv[2:] or SYSTEMS:
        if system not in SYSTEMS:
            sys.exit(f"{system} isn't one of {', '.join(SYSTEMS)}")
        pack(version, system)


if __name__ == "__main__":
    main()

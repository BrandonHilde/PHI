#!/usr/bin/env python3
"""Fetches each vendor library's own files: the library for each system, its headers, its licence.
A library that has no builds of its own to fetch (zlib, Lua, SQLite) is compiled here from its source.

    python3 vendor/fetch.py            # every library
    python3 vendor/fetch.py raylib

Each package.json's "downloads" names an archive of the library's own release, the SHA-256
it must have, and which of its files go where in the library's folder (windows64/,
windows/, macos/, linux64/: where a build looks for a library to put beside a program).
The archives are kept in vendor/.cache, so a second run fetches nothing. The libraries
themselves aren't in PHI's repository; a release is made with them in place.
"""
import glob
import hashlib
import io
import json
import os
import posixpath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, ".cache")


def archive(url, sha256):
    path = os.path.join(CACHE, sha256[:16] + "-" + url.rsplit("/", 1)[1])
    if not os.path.exists(path):
        os.makedirs(CACHE, exist_ok=True)
        print(f"  fetching {url}")
        with urllib.request.urlopen(url) as response:
            data = response.read()
        with open(path + ".part", "wb") as f:
            f.write(data)
        os.replace(path + ".part", path)
    with open(path, "rb") as f:
        data = f.read()
    got = hashlib.sha256(data).hexdigest()
    if got != sha256:
        os.remove(path)
        sys.exit(f"{url} isn't the file package.json expects: its SHA-256 is {got}, not {sha256}")
    return data


def members(name, data):
    """Each file of an archive (or the one file itself), by its path in it."""
    if name.endswith(".zip"):
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            return {n: z.read(n) for n in z.namelist() if not n.endswith("/")}
    if ".tar" in name or name.endswith(".tgz"):
        files = {}
        with tarfile.open(fileobj=io.BytesIO(data)) as t:
            for m in t.getmembers():
                target = m
                # (a library's short name is often a link to the full one; a link to a folder, or
                # to nothing in the archive, is no file)
                try:
                    while target.issym() or target.islnk():
                        # (a tar's names have / in them on every system: not os.path, which makes \ of it on Windows)
                        link = target.linkname if target.islnk() else posixpath.normpath(posixpath.join(posixpath.dirname(target.name), target.linkname))
                        target = t.getmember(link)
                except KeyError:
                    continue
                if target.isfile():
                    files[m.name] = t.extractfile(target).read()
        return files
    if name.endswith(".dmg"):
        return disk_image(data)
    return {os.path.basename(name): data}


def disk_image(data):
    """The files of a macOS disk image, which only a Mac opens."""
    if sys.platform != "darwin":
        sys.exit("a .dmg is opened on a Mac: run vendor/fetch.py there for this library's macOS files")
    files = {}
    with tempfile.TemporaryDirectory() as work:
        image, mount = os.path.join(work, "image.dmg"), os.path.join(work, "mount")
        with open(image, "wb") as f:
            f.write(data)
        os.mkdir(mount)
        subprocess.run(["hdiutil", "attach", "-nobrowse", "-readonly", "-mountpoint", mount, image], check=True, capture_output=True)
        try:
            for folder, _, names in os.walk(mount):
                if any(part in folder for part in ("ios-", "tvos-", "dSYM", "xros-")):
                    continue
                for n in names:
                    path = os.path.join(folder, n)
                    if os.path.isfile(path):          # (a link to a file is that file)
                        with open(path, "rb") as f:
                            files[os.path.relpath(path, mount)] = f.read()
        finally:
            subprocess.run(["hdiutil", "detach", mount], capture_output=True)
    return files


def fetch(name):
    folder = os.path.join(HERE, name)
    with open(os.path.join(folder, "package.json")) as f:
        info = json.load(f)
    print(f"{name} {info['version']}")
    for download in info.get("downloads", []):
        if download["url"].endswith(".dmg") and sys.platform != "darwin":
            # (a macOS disk image is opened on a Mac: the library's other files are fetched all the same)
            print(f"  its macOS files come in a .dmg, which a Mac opens: left out here ({', '.join(download['files'].values())})")
            continue
        files = members(download["url"], archive(download["url"], download["sha256"]))
        for inside, target in download["files"].items():
            # (a name that ends in / is a folder: every file in it, to the folder named)
            wanted = {n: target + n[len(inside):] for n in files if n.startswith(inside)} if inside.endswith("/") else {inside: target}
            if not wanted or any(n not in files for n in wanted):
                sys.exit(f"{download['url']} has no {inside}")
            for name, to in wanted.items():
                path = os.path.join(folder, to)
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "wb") as f:
                    f.write(files[name])
                if any(part in to for part in (".dylib", ".so", ".dll")):
                    os.chmod(path, 0o755)
                if to.endswith(".dylib") and sys.platform == "darwin":
                    # a macOS library says where it's to be found: beside the program
                    subprocess.run(["install_name_tool", "-id", "@rpath/" + os.path.basename(to), path], check=True, capture_output=True)
                    subprocess.run(["codesign", "--force", "--sign", "-", path], check=True, capture_output=True)
    build(name, info, folder)


COMPILERS = {
    # system: (the compiler, when this computer has it), how a shared library is made, its file
    "macos": (["clang", "-arch", "arm64", "-arch", "x86_64", "-mmacosx-version-min=11.0"], ["-dynamiclib"]),
    "windows64": (["x86_64-w64-mingw32-gcc"], ["-shared", "-static", "-static-libgcc"]),
    "windows": (["i686-w64-mingw32-gcc"], ["-shared", "-static", "-static-libgcc"]),
    "linux64": (None, ["-shared", "-fPIC"]),
}


def linux_compiler():
    """gcc for 64-bit Linux: this computer's own there, or the one in the Linux machine a Mac keeps (Lima)."""
    if sys.platform.startswith("linux") and os.uname().machine == "x86_64" and shutil.which("gcc"):
        return ["gcc"]
    if shutil.which("lima") and subprocess.run(["lima", "which", "x86_64-linux-gnu-gcc"], capture_output=True).returncode == 0:
        return ["lima", "x86_64-linux-gnu-gcc"]
    return None


# The oldest C library a Linux library built here is to run on: 2.34 (August 2021), which
# a PHI program that calls C needs anyway.
GLIBC_FLOOR = (2, 34)


def glibc_past_floor(compiler, library):
    """The C functions a built Linux library asks for in a version past GLIBC_FLOOR."""
    objdump = compiler[:-1] + ["objdump"] if compiler[0] == "lima" else ["objdump"]
    listing = subprocess.run(objdump + ["-T", library], capture_output=True, text=True).stdout
    newer = set()
    for line in listing.split("\n"):
        m = re.search(r"\(?GLIBC_(\d+)\.(\d+)(?:\.\d+)?\)?\s+(\w+)$", line)
        if m and "*UND*" in line and (int(m.group(1)), int(m.group(2))) > GLIBC_FLOOR:
            newer.add(m.group(3))
    return newer


def build(name, info, folder):
    """A library that comes as C source: compiled here into one shared library for each system
    this computer has a compiler for (clang on a Mac, MinGW for Windows, gcc for Linux)."""
    recipe = info.get("build")
    if not recipe:
        return
    work = os.path.join(folder, "src")
    for download in recipe.get("sources", []):
        files = members(download["url"], archive(download["url"], download["sha256"]))
        for inside, target in download["files"].items():
            wanted = {n: target + n[len(inside):] for n in files if n.startswith(inside)} if inside.endswith("/") else {inside: target}
            if not wanted or any(n not in files for n in wanted):
                sys.exit(f"{download['url']} has no {inside}")
            for member, to in wanted.items():
                path = os.path.join(folder, to)
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "wb") as f:
                    f.write(files[member])
    sources = []
    for pattern in recipe["compile"]:
        sources += sorted(glob.glob(os.path.join(folder, pattern)))
    sources = [s for s in sources if os.path.basename(s) not in recipe.get("without", [])]
    for system, (compiler, shared) in COMPILERS.items():
        if system not in recipe.get("systems", COMPILERS):
            continue
        compiler = compiler if system != "linux64" else linux_compiler()
        if system == "macos" and sys.platform != "darwin":
            compiler = None     # (clang on another system doesn't make a Mac's library)
        library = info["libraries"][{"windows64": "windows", "windows": "windows", "macos": "macos", "linux64": "linux"}[system]]
        if compiler is None or not shutil.which(compiler[0]):
            print(f"  {system}: no compiler here for it, so {library} isn't built")
            continue
        out = os.path.join(folder, system, library)
        os.makedirs(os.path.dirname(out), exist_ok=True)
        args = compiler + ["-O2", "-w"] + shared + recipe.get("flags", []) + recipe.get(system, [])
        if system == "macos":
            args += ["-install_name", "@rpath/" + library]
        args += ["-I" + os.path.join(folder, i) for i in recipe.get("include", [])]
        tail = ["-o", out] + sources + recipe.get("link", []) + recipe.get(system + "_link", [])
        if system == "linux64":
            args += ["-std=gnu17"]      # (not C23, whose strtol and the like are names only a new glibc has)
        done = subprocess.run(args + tail, capture_output=True, text=True)
        if done.returncode != 0:
            sys.exit(f"{name} didn't build for {system}:\n{done.stderr[-3000:]}")
        note = ""
        if system == "linux64":
            # A library built on a new Linux asks for the newest version of each C function it
            # calls, which an older Linux hasn't got, though it has the function. Each one that's
            # past GLIBC_FLOOR is asked for by its first version instead, and it's built again.
            newer = glibc_past_floor(compiler, out)
            if newer:
                header = os.path.join(folder, "src", "glibc-floor.h") if os.path.isdir(os.path.join(folder, "src")) else os.path.join(folder, "glibc-floor.h")
                with open(header, "w") as f:
                    f.write("/* written by vendor/fetch.py: C functions asked for by their first version */\n"
                            + "".join(f'__asm__(".symver {n},{n}@GLIBC_2.2.5");\n' for n in sorted(newer)))
                done = subprocess.run(args + ["-include", header] + tail, capture_output=True, text=True)
                if done.returncode != 0:
                    sys.exit(f"{name} didn't build for {system} on older C libraries:\n{done.stderr[-3000:]}")
                if not header.startswith(os.path.join(folder, "src")):
                    os.remove(header)
                still = glibc_past_floor(compiler, out)
                if still:
                    sys.exit(f"{name}'s {library} needs a C library newer than glibc {GLIBC_FLOOR[0]}.{GLIBC_FLOOR[1]} for: {', '.join(sorted(still))}")
            note = f" (for glibc {GLIBC_FLOOR[0]}.{GLIBC_FLOOR[1]} and later)"
        print(f"  built {system}/{library}{note}")


def main():
    names = sys.argv[1:] or sorted(n for n in os.listdir(HERE) if os.path.exists(os.path.join(HERE, n, "package.json")))
    for name in names:
        fetch(name)


if __name__ == "__main__":
    main()

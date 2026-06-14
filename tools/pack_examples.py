#!/usr/bin/env python3
"""Build the per-example download zips under docs/api/{cpp,csharp}/downloads/.

Each zip = the example's source file + the language's _shared scaffolding (build files + README +
the OS-specific run script). Run from the repo root:  python tools/pack_examples.py
"""
import os
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

SLUGS = ["01_collision", "02_nfp", "03_live", "04_offset", "05_attributes",
         "06_pack_array", "07_pack_distance", "08_text"]

# language -> (example source filename, shared files common to both OSes)
LANGS = {
    "cpp":    ("main.cpp",   ["CMakeLists.txt", "README.md"]),
    "csharp": ("Program.cs", ["example.csproj", "README.md"]),
}
# OS -> the run script taken from _shared
OS_SCRIPT = {"win": "run.bat", "mac": "run.command"}


def main():
    n = 0
    for lang, (srcname, shared_files) in LANGS.items():
        ex_dir = os.path.join(ROOT, "examples", "downloads", lang)
        shared = os.path.join(ex_dir, "_shared")
        out_dir = os.path.join(ROOT, "docs", "api", lang, "downloads")
        os.makedirs(out_dir, exist_ok=True)
        for slug in SLUGS:
            src = os.path.join(ex_dir, slug, srcname)
            for osname, script in OS_SCRIPT.items():
                zpath = os.path.join(out_dir, f"{slug}_{osname}.zip")
                with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
                    z.write(src, srcname)
                    for f in shared_files:
                        z.write(os.path.join(shared, f), f)
                    z.write(os.path.join(shared, script), script)
                n += 1
    print(f"wrote {n} zips")


if __name__ == "__main__":
    main()

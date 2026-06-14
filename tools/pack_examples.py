#!/usr/bin/env python3
"""Build the per-example download zips under docs/api/{cpp,csharp}/downloads/.

ONE zip per example (not per OS): CMake/.NET build natively on every platform from the SAME sources,
so a single project serves Windows, macOS and Linux. The zip just carries both convenience launchers
(run.bat for Windows, run.command for macOS/Linux). Run from the repo root: python tools/pack_examples.py
"""
import os
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

SLUGS = ["01_collision", "02_nfp", "03_live", "04_offset", "05_attributes",
         "06_pack_array", "07_pack_distance", "08_text"]

# language -> (example source filename, shared scaffolding files — same on every OS)
LANGS = {
    "cpp":    ("main.cpp",   ["CMakeLists.txt", "README.md", "run.bat", "run.command"]),
    "csharp": ("Program.cs", ["example.csproj", "README.md", "run.bat", "run.command"]),
}


def main():
    n = 0
    for lang, (srcname, shared_files) in LANGS.items():
        ex_dir = os.path.join(ROOT, "examples", "downloads", lang)
        shared = os.path.join(ex_dir, "_shared")
        out_dir = os.path.join(ROOT, "docs", "api", lang, "downloads")
        os.makedirs(out_dir, exist_ok=True)
        for slug in SLUGS:
            src = os.path.join(ex_dir, slug, srcname)
            zpath = os.path.join(out_dir, f"{slug}.zip")
            with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
                z.write(src, srcname)
                for f in shared_files:
                    # mark run.command executable so it double-clicks on macOS
                    info = zipfile.ZipInfo(f)
                    data = open(os.path.join(shared, f), "rb").read()
                    info.compress_type = zipfile.ZIP_DEFLATED
                    info.external_attr = (0o755 if f == "run.command" else 0o644) << 16
                    z.writestr(info, data)
            n += 1
    print(f"wrote {n} zips")


if __name__ == "__main__":
    main()

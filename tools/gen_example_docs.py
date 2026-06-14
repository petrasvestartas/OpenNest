#!/usr/bin/env python3
"""Generate docs/api/{cpp,csharp}/*.md (one page per download example) + the two index pages,
from the verified example sources under examples/downloads/. Keeps the docs in lockstep with the
code that actually compiles + runs. Run from the repo root:  python tools/gen_example_docs.py
"""
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# (number, slug, title, one-line description) — the 8 examples, mirroring the compas_nest set.
EXAMPLES = [
    ("01", "01_collision",     "Collision",            "Nest parts (one with a hole) into a sheet (with a hole) with the physics (collision) engine."),
    ("02", "02_nfp",           "NFP + GA",             "Nest parts into a sheet with the NFP + genetic-algorithm engine (handles concave parts + holes)."),
    ("03", "03_live",          "Live animation (NFP)", "Run the NFP engine on a background thread and poll the evolving best layout (progress, fitness, live poses)."),
    ("04", "04_offset",        "Clearance offset",     "Grow every part and shrink the sheet by a clearance with nfp_offset_polygon, then nest the offset geometry — the spacing-via-offset workflow (the solver `spacing` param is ignored)."),
    ("05", "05_attributes",    "Attributes",           "Carry a point (a part's centroid) and read it at the placed pose: attributes move with the part."),
    ("06", "06_pack_array",    "Pack (array)",         "Lay parts out in a deterministic grid: a fixed number of columns per row (no nesting)."),
    ("07", "07_pack_distance", "Pack (distance)",      "Lay parts out in a grid, wrapping to a new row once a row reaches a maximum width."),
    ("08", "08_text",          "Text (font)",          "Render a label to single-stroke engraving polylines using the bundled OpenNest VDA font."),
]

LANGS = {
    "cpp":    ("main.cpp",   "cpp",    "C++"),
    "csharp": ("Program.cs", "csharp", "C#"),
}

def page(num, slug, title, desc, src_rel, fence, code):
    # one project for every OS (CMake/.NET build natively); the zip carries both run scripts.
    return (f"# {num} · {title}\n\n{desc}\n\n"
            f"Project: [Download `{slug}.zip`](downloads/{slug}.zip) — Windows `run.bat`, macOS/Linux `./run.command`.\n\n"
            f"```{fence}\n{code.rstrip()}\n```\n")

def index(lang_dir, lang_name, blurb):
    rows = "\n".join(f"| {num} | [{title}]({slug}.md) — {desc.split('—')[0].strip().rstrip('.')} |"
                     for (num, slug, title, desc) in EXAMPLES)
    return (f"# {lang_name} Examples\n\n{blurb}\n\n"
            "Each example below is a self-contained project — download it, run `run.bat` (Windows) or\n"
            "`run.command` (macOS); the first build compiles the OpenNest engines from source.\n\n"
            "| # | Example |\n| --- | --- |\n" + rows + "\n")

def main():
    for lang, (fname, fence, lang_name) in LANGS.items():
        for (num, slug, title, desc) in EXAMPLES:
            src = os.path.join(ROOT, "examples", "downloads", lang, slug, fname)
            with open(src, encoding="utf-8") as f:
                code = f.read()
            out = os.path.join(ROOT, "docs", "api", lang, slug + ".md")
            with open(out, "w", encoding="utf-8", newline="\n") as f:
                f.write(page(num, slug, title, desc, fname, fence, code))
    cpp_blurb = ("Two native engines through a plain **C ABI**. You give part + sheet polygons; you get back, per\n"
                 "part, *move by (tx, ty)*, *rotate by an angle*, and *which sheet*. Angle is **degrees** for\n"
                 "`nfp_nest`, **radians** for `np_nest`; `sheet_id == -1` means the part didn't fit.")
    cs_blurb = ("Two native engines via P/Invoke. You give part + sheet polygons; you get back, per part, *move by\n"
                "(tx, ty)*, *rotate by an angle*, and *which sheet*. The native libraries are resolved by bare name,\n"
                "so `nfp_nest`/`nest_physics` (`.dll`/`.dylib`/`.so`) must sit next to the executable.")
    with open(os.path.join(ROOT, "docs", "api", "cpp", "index.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(index("cpp", "C++", cpp_blurb))
    with open(os.path.join(ROOT, "docs", "api", "csharp", "index.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write(index("csharp", "C#", cs_blurb))
    print("wrote 16 example pages + 2 index pages")

if __name__ == "__main__":
    main()

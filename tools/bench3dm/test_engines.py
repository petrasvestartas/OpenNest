"""Smoke + determinism test for tools/bench3dm/engines.py.

    python -I tools/bench3dm/test_engines.py

Builds a small synthetic instance (6 rectangles + 1 rectangle-with-hole, qty 1-2, sheet 400x300,
clearance 4, margin 6) under tools/bench3dm/out/selftest/, runs both engines twice through the CLI
(subprocess, like run_matrix) and checks: success, everything placed, n_sheets >= 1, schema-valid
placements, np_last_quality reports 0 overlaps, an independent shapely check of the solve rings
(no overlaps deeper than a hair, inside the solve sheet) which also pins the transform convention,
and byte-identical `instances` across the two runs.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))   # -I drops the script dir from sys.path

import engines  # noqa: E402
from shapely import affinity  # noqa: E402
from shapely.geometry import Polygon, box  # noqa: E402
from shapely.geometry.polygon import orient  # noqa: E402

OUT = HERE / "out" / "selftest"
SHEET_W, SHEET_H, CLEARANCE, MARGIN = 400.0, 300.0, 4.0, 6.0
INSET = MARGIN - CLEARANCE / 2.0          # solve sheet = sheet shrunk by margin, grown back by clearance/2
CAP = 120.0
FAILS: list[str] = []


def check(cond: bool, msg: str) -> None:
    print(("  ok   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _ring_list(coords) -> list:
    pts = [[float(x), float(y)] for x, y in coords]
    return pts[:-1] if pts and pts[0] == pts[-1] else pts


def make_part(pid: int, w: float, h: float, qty: int, hole=None) -> dict:
    """Rectangle (optionally with a centred rectangular hole) in the local frame: first outer vertex at
    the origin. `solve` = outward clearance/2 mitre offset (outer CCW, holes CW)."""
    outer = [[0.0, 0.0], [w, 0.0], [w, h], [0.0, h]]
    holes = []
    if hole:
        hw, hh = hole
        x0, y0 = (w - hw) / 2, (h - hh) / 2
        holes = [[[x0, y0], [x0, y0 + hh], [x0 + hw, y0 + hh], [x0 + hw, y0]]]   # CW
    poly = Polygon(outer, holes)
    grown = orient(poly.buffer(CLEARANCE / 2.0, join_style="mitre"), sign=1.0)
    return {
        "id": pid, "name": f"P{pid:03d}", "guids": [], "layer": "selftest", "qty": qty, "rotations": 0,
        "local_origin": [0.0, 0.0], "outer": outer, "holes": holes, "area_net": poly.area,
        "solve": {"outer": _ring_list(grown.exterior.coords),
                  "holes": [_ring_list(r.coords) for r in grown.interiors], "dropped_holes": 0},
    }


def build_instance() -> Path:
    specs = [(120, 80, 2, None), (100, 60, 2, None), (150, 40, 1, None), (80, 80, 1, None),
             (60, 30, 2, None), (90, 50, 1, None), (140, 100, 1, (100, 60))]
    inst = {
        "schema": engines.INSTANCE_SCHEMA, "units": "mm",
        "source": {"generator": "test_engines.py (synthetic)"}, "sampling": {},
        "sheet": {"w": SHEET_W, "h": SHEET_H, "count": 3, "world_origin": [0.0, 0.0], "pitch_y": SHEET_H + 50},
        "margin": MARGIN, "clearance": CLEARANCE,
        "solve_sheet": {"w": SHEET_W - 2 * INSET, "h": SHEET_H - 2 * INSET, "inset": INSET},
        "parts": [make_part(i, *s) for i, s in enumerate(specs)],
    }
    OUT.mkdir(parents=True, exist_ok=True)
    p = OUT / "instance.json"
    p.write_text(json.dumps(inst, indent=1), encoding="utf-8")
    return p


def run_cli(instance: Path, config: str, seed: int, out: Path, sets: list) -> tuple:
    cmd = [sys.executable, "-I", str(HERE / "engines.py"), "--instance", str(instance), "--config", config,
           "--seed", str(seed), "--cap", str(CAP), "--out", str(out)]
    for s in sets:
        cmd += ["--set", s]
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=CAP + 60)
    (out / "engine_stdout.txt").write_text((p.stdout or "") + (p.stderr or ""), encoding="utf-8")
    doc = None
    pj = out / "placements.json"
    if pj.is_file():
        doc = json.loads(pj.read_text(encoding="utf-8"))
    return p.returncode, doc, (p.stdout or "") + (p.stderr or "")


def geometric_check(inst: dict, doc: dict) -> tuple:
    """Independent check on the SOLVE rings (which are allowed to touch): returns
    (worst pair overlap area, worst out-of-sheet area)."""
    parts = {p["id"]: p for p in inst["parts"]}
    sw, sh = inst["solve_sheet"]["w"], inst["solve_sheet"]["h"]
    sheet = box(0, 0, sw, sh)
    placed = []
    for it in doc["instances"]:
        if it["sheet"] < 0:
            continue
        s = parts[it["part"]]["solve"]
        g = Polygon(s["outer"], s["holes"])
        g = affinity.rotate(g, it["angle_rad"], origin=(0, 0), use_radians=True)
        g = affinity.translate(g, it["tx"], it["ty"])
        placed.append((it["sheet"], g))
    worst_ovl = 0.0
    for i in range(len(placed)):
        for j in range(i + 1, len(placed)):
            if placed[i][0] == placed[j][0]:
                worst_ovl = max(worst_ovl, placed[i][1].intersection(placed[j][1]).area)
    worst_oob = max((g.difference(sheet).area for _, g in placed), default=0.0)
    return worst_ovl, worst_oob


def test_engine(inst_path: Path, inst: dict, config: str, seed: int, sets: list) -> None:
    print(f"\n== {config} seed={seed} {' '.join(sets)}")
    docs = []
    for r in (1, 2):
        out = OUT / f"{config}_s{seed}_r{r}"
        rc, doc, log = run_cli(inst_path, config, seed, out, sets)
        check(rc == 0, f"run {r}: exit code {rc} == 0")
        check(doc is not None, f"run {r}: placements.json written")
        if doc is None:
            print(log[-2000:])
            return
        docs.append(doc)
    doc = docs[0]
    errs = engines.validate_placements(doc, inst)
    check(not errs, f"placements schema valid {errs if errs else ''}")
    total = sum(p["qty"] for p in inst["parts"])
    placed = sum(1 for i in doc["instances"] if i["sheet"] >= 0)
    check(placed == total, f"all instances placed ({placed}/{total})")
    check(doc["n_sheets"] >= 1, f"n_sheets {doc['n_sheets']} >= 1")
    check(not doc["cancelled"], "not cancelled")
    if doc["engine"] == engines.ENGINE_NP:
        q = doc["engine_report"]["np_last_quality"]
        check(q["overlap_pairs"] == 0, f"np_last_quality overlap_pairs {q['overlap_pairs']} == 0 ({q})")
    ovl, oob = geometric_check(inst, doc)
    # solve rings may touch; allow a hair of numerical overlap (relative to the ~1e4 mm^2 parts)
    check(ovl < 1.0, f"independent check: worst pair overlap area {ovl:.4g} mm^2 < 1")
    check(oob < 1.0, f"independent check: worst out-of-sheet area {oob:.4g} mm^2 < 1")
    a = json.dumps(docs[0]["instances"], sort_keys=True)
    b = json.dumps(docs[1]["instances"], sort_keys=True)
    check(a == b, "second identical run gives byte-identical instances (determinism)")
    print(f"  wall {docs[0]['wall_s']}s / {docs[1]['wall_s']}s  n_sheets={doc['n_sheets']}  "
          f"report={json.dumps(doc['engine_report'])}")


def main() -> int:
    inst_path = build_instance()
    inst = json.loads(inst_path.read_text(encoding="utf-8"))
    print(f"instance: {inst_path}  parts={len(inst['parts'])} "
          f"instances={sum(p['qty'] for p in inst['parts'])}  solve_sheet={inst['solve_sheet']}")
    check(not engines.check_struct_layout(), "struct sizes/offsets")
    test_engine(inst_path, inst, "gh_np", 100, ["iter_budget=600"])
    test_engine(inst_path, inst, "gh_nfp", 100, ["generations=3"])
    print("\nRESULT:", "PASS" if not FAILS else f"FAIL ({len(FAILS)}): " + "; ".join(FAILS))
    return 0 if not FAILS else 1


if __name__ == "__main__":
    sys.exit(main())

"""Independent constructive solver adapter; no legacy solver calls.

Geometry must already be in pre-offset solve space. Explicit allowed_angles are
radians; mirror_allowed permits local X reflection. Export converts this to the
benchmark evaluator's local Y reflection convention without moving geometry.
"""
import argparse
import ctypes as C
import hashlib
import json
import math
from pathlib import Path
import sys
import threading
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
DEFAULT_DLL = HERE.parent.parent / "src/nest_constructive_cpp/build/Release/nest_constructive.dll"


class NcParams(C.Structure):
    _fields_ = [(n, C.c_int) for n in ("struct_size", "max_sheets", "beam_width", "branch_parts",
        "position_limit", "restarts", "workers", "seed", "rotations", "reserved")] + [
        (n, C.c_double) for n in ("time_limit", "scan_step", "scale")]


class NcPlacement(C.Structure):
    _fields_ = [(n, C.c_int) for n in ("part", "copy", "sheet", "mirrored")] + [
        (n, C.c_double) for n in ("tx", "ty", "angle")]


class NcStats(C.Structure):
    _fields_ = [(n, C.c_int) for n in ("struct_size", "placed", "requested", "sheets", "iterations", "cancelled")] + [
        ("nfp_hits", C.c_int64), ("nfp_misses", C.c_int64), ("elapsed", C.c_double)]


assert (C.sizeof(NcParams), C.sizeof(NcPlacement), C.sizeof(NcStats)) == (64, 40, 48)
_solve_lock = threading.Lock()


def load_dll(path=DEFAULT_DLL):
    lib = C.CDLL(str(Path(path).resolve()))
    I, D = C.POINTER(C.c_int), C.POINTER(C.c_double)
    lib.nc_solve.argtypes = [D, C.c_int, I, I, I, I, C.c_int, I, I, C.c_int,
        D, I, I, C.c_int, C.c_double, C.c_double, C.POINTER(NcParams),
        C.POINTER(NcPlacement), C.c_int, C.POINTER(NcStats)]
    lib.nc_solve.restype = C.c_int
    for name in ("nc_version", "nc_last_error"):
        getattr(lib, name).argtypes = []
        getattr(lib, name).restype = C.c_char_p
    lib.nc_cancel.argtypes = []
    lib.nc_cancel.restype = None
    return lib


def flatten(instance, rotations=8, allow_mirror=False):
    from shapely.geometry import Polygon
    parts = instance["parts"]
    if not parts or rotations < 1:
        raise ValueError("nonempty parts and positive rotations required")
    if len({p["id"] for p in parts}) != len(parts):
        raise ValueError("duplicate part IDs")
    sheet = instance.get("solve_sheet")
    if not sheet or min(float(sheet["w"]), float(sheet["h"])) <= 0:
        raise ValueError("positive pre-offset solve_sheet required")
    data = dict(xy=[], ring_starts=[], ring_sizes=[], ring_parts=[], ring_holes=[],
                quantities=[], mirror_flags=[], angles=[], angle_starts=[], angle_counts=[],
                part_ids=[p["id"] for p in parts], shapes=[], allowed=[])

    def ring(points, part, hole):
        points = [tuple(map(float, p[:2])) for p in points]
        if len(points) > 1 and points[0] == points[-1]:
            points.pop()
        if len(points) < 3 or any(not math.isfinite(v) for p in points for v in p):
            raise ValueError("ring needs at least three finite points")
        data["ring_starts"].append(len(data["xy"]) // 2)
        data["ring_sizes"].append(len(points))
        data["ring_parts"].append(part)
        data["ring_holes"].append(hole)
        data["xy"].extend(v for p in points for v in p)

    for index, part in enumerate(parts):
        quantity = part.get("qty", 1)
        if int(quantity) != quantity or quantity < 1:
            raise ValueError("quantities must be positive integers")
        shape = part.get("solve")
        if shape is None:
            if instance.get("clearance", 0) or instance.get("margin", 0):
                raise ValueError("clearance/margin requires pre-offset part.solve rings")
            shape = part
        polygon = Polygon(shape["outer"], shape.get("holes", []))
        if not polygon.is_valid or polygon.is_empty or polygon.area <= 0:
            raise ValueError(f"invalid solve polygon for part {part['id']}")
        data["shapes"].append(polygon)
        ring(shape["outer"], index, 0)
        for hole in shape.get("holes", []):
            ring(hole, index, 1)
        allowed = part.get("allowed_angles")
        if allowed is None:
            count = int(part.get("rotations", 0) or rotations)
            if count < 1:
                raise ValueError("rotation count must be positive")
            allowed = [2 * math.pi * k / count for k in range(count)]
        allowed = list(map(float, allowed))
        if not allowed or not all(math.isfinite(a) for a in allowed):
            raise ValueError("allowed_angles must be a nonempty finite radians list")
        data["allowed"].append(allowed)
        data["angle_starts"].append(len(data["angles"]))
        data["angle_counts"].append(len(allowed))
        data["angles"].extend(allowed)
        data["quantities"].append(int(quantity))
        data["mirror_flags"].append(int(bool(part.get("mirror_allowed", allow_mirror))))
    data["keepouts"] = [Polygon(h) for h in sheet.get("holes", [])]
    for hole, poly in zip(sheet.get("holes", []), data["keepouts"]):
        if not poly.is_valid or poly.area <= 0:
            raise ValueError("invalid solve-sheet keepout")
        ring(hole, -1, 0)
    for name in ("xy", "angles", "ring_starts", "ring_sizes", "ring_parts", "ring_holes",
                 "quantities", "mirror_flags", "angle_starts", "angle_counts"):
        element = C.c_double if name in ("xy", "angles") else C.c_int
        data[name] = (element * len(data[name]))(*data[name])
    data["width"], data["height"] = float(sheet["w"]), float(sheet["h"])
    return data


def solve(lib, instance, *, seconds=60, max_sheets=5, rotations=8, beam=8, branch=4,
          positions=32, restarts=4, workers=1, seed=100, scan_step=100, scale=100000, allow_mirror=False):
    if seconds < 0 or not math.isfinite(seconds) or min(max_sheets, rotations, beam, branch, positions, restarts, workers, scan_step, scale) <= 0:
        raise ValueError("positive search, time and geometry budgets required")
    data = flatten(instance, rotations, allow_mirror)
    params = NcParams(C.sizeof(NcParams), max_sheets, beam, branch, positions,
                      restarts, workers, seed, rotations, 0, seconds, scan_step, scale)
    count = sum(data["quantities"])
    output = (NcPlacement * count)()
    stats = NcStats()
    stats.struct_size = C.sizeof(NcStats)
    with _solve_lock:
        timer = threading.Timer(seconds + .5, lib.nc_cancel) if seconds else None
        if timer:
            timer.daemon = True
        started = time.perf_counter()
        if timer:
            timer.start()
        try:
            rc = lib.nc_solve(data["xy"], len(data["xy"]) // 2, data["ring_starts"], data["ring_sizes"],
                data["ring_parts"], data["ring_holes"], len(data["ring_sizes"]), data["quantities"],
                data["mirror_flags"], len(data["part_ids"]), data["angles"], data["angle_starts"],
                data["angle_counts"], len(data["angles"]), data["width"], data["height"],
                C.byref(params), output, count, C.byref(stats))
        finally:
            if timer:
                timer.cancel()
                timer.join()
    if rc < 0:
        raise RuntimeError(f"nc_solve rc={rc}: {(lib.nc_last_error() or b'').decode(errors='replace')}")
    doc = {"schema": "opennest.bench3dm.placements/1", "engine": "nest_constructive",
        "config": "constructive", "seed": seed, "wall_s": time.perf_counter() - started,
        "cancelled": bool(stats.cancelled), "params": {n: getattr(params, n) for n, _ in params._fields_},
        "engine_report": {"rc": rc, **{n: getattr(stats, n) for n, _ in stats._fields_}},
        "frame": "p_world = sheet_world_origin[sid] + (inset,inset) + R(angle_rad)*p_local + (tx,ty)",
        "sheets_given": max_sheets, "instances": []}
    seen = set()
    for p in output:
        if not 0 <= p.part < len(data["part_ids"]) or not 0 <= p.copy < data["quantities"][p.part]:
            raise RuntimeError("native returned unknown part/copy")
        if (p.part, p.copy) in seen or p.sheet < -1 or p.sheet >= max_sheets:
            raise RuntimeError("native returned duplicate copy or invalid sheet")
        seen.add((p.part, p.copy))
        if not all(math.isfinite(v) for v in (p.tx, p.ty, p.angle)):
            raise RuntimeError("nonfinite native placement")
        if p.mirrored not in (0, 1) or (p.mirrored and not data["mirror_flags"][p.part]):
            raise RuntimeError("native violated mirror restriction")
        if p.sheet >= 0 and min(abs(math.remainder(p.angle-a, 2*math.pi)) for a in data["allowed"][p.part]) > 1e-7:
            raise RuntimeError("native violated allowed angles")
        doc["instances"].append({"part": data["part_ids"][p.part], "copy": p.copy, "sheet": p.sheet,
            "tx": p.tx, "ty": p.ty, "angle_rad": p.angle + (math.pi if p.mirrored else 0),
            "mirrored": bool(p.mirrored), "native_angle_rad": p.angle})
    doc["sheets_used"] = sorted({p["sheet"] for p in doc["instances"] if p["sheet"] >= 0})
    doc["n_sheets"] = len(doc["sheets_used"])
    return doc, validate(instance, doc, data, max_sheets)


def validate(instance, doc, data, max_sheets):
    from shapely import affinity
    from shapely.geometry import box
    from validate_strict import validate_strict
    solids = []
    errors = []
    sheet = box(0, 0, data["width"], data["height"])
    for row in doc["instances"]:
        if row["sheet"] < 0:
            continue
        p = data["shapes"][data["part_ids"].index(row["part"])]
        if row["mirrored"]:
            p = affinity.scale(p, xfact=1, yfact=-1, origin=(0, 0))
        p = affinity.translate(affinity.rotate(p, row["angle_rad"], origin=(0, 0), use_radians=True), row["tx"], row["ty"])
        if p.difference(sheet).area > 1e-8 or any(p.intersection(h).area > 1e-8 for h in data["keepouts"]):
            errors.append({"part": row["part"], "copy": row["copy"], "reason": "solve bounds/keepout"})
        for other_row, other in solids:
            if row["sheet"] == other_row["sheet"] and p.intersection(other).area > 1e-8:
                errors.append({"part": row["part"], "other": other_row["part"], "reason": "solve intersection"})
        solids.append((row, p))
    strict = validate_strict(instance, doc, max_sheets=max_sheets)
    strict["solve_violations"] = errors
    strict["geometry_valid_partial"] = not (errors or strict["clearance_violations"] or strict["margin_violations"] or strict["overlap_pairs"]) and strict["sheet_limit_passed"]
    strict["passed_sampled_polygons"] &= not bool(errors)
    return strict


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--dll", default=str(DEFAULT_DLL))
    for name, default in (("max-sheets", 5), ("rotations", 8), ("beam", 8), ("branch", 4),
                          ("positions", 32), ("restarts", 4), ("workers", 1), ("seed", 100)):
        ap.add_argument("--"+name, type=int, default=default)
    for name, default in (("seconds", 60), ("scan-step", 100), ("scale", 100000)):
        ap.add_argument("--"+name, type=float, default=default)
    ap.add_argument("--allow-mirror", action="store_true")
    args = ap.parse_args()
    instance = json.loads(Path(args.instance).read_text(encoding="utf-8"))
    lib = load_dll(args.dll)
    options = {k: v for k, v in vars(args).items() if k not in ("instance", "out", "dll")}
    doc, strict = solve(lib, instance, **options)
    doc.update(instance=str(Path(args.instance).resolve()), dll={"path": str(Path(args.dll).resolve()),
        "sha256": hashlib.sha256(Path(args.dll).read_bytes()).hexdigest(), "version": lib.nc_version().decode()})
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "placements.json").write_text(json.dumps(doc, indent=2))
    (out / "strict_metrics.json").write_text(json.dumps(strict, indent=2))
    from evaluate import evaluate_files
    evaluate_files(args.instance, str(out / "placements.json"), str(out), write_3dm=False, write_svg=True)
    print(json.dumps({"rc": doc["engine_report"]["rc"], "placed": strict["placed"], "total": strict["total"],
                      "sheets": doc["n_sheets"], "geometry_valid_partial": strict["geometry_valid_partial"],
                      "complete_valid": strict["passed_sampled_polygons"]}))
    return 0 if strict["passed_sampled_polygons"] else 2 if strict["geometry_valid_partial"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

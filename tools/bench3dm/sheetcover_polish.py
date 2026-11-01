"""Repair tiny numerical boundary/overlap errors using verified translations only.

No clearance is reduced: all checks use the original offset solve polygons.
A result is returned only if every original part remains and independent polygon
validation succeeds. This does not search alternative part assignments.
"""
import argparse
import copy
import json
import math
import sys
from pathlib import Path
from types import SimpleNamespace

from shapely import affinity
from shapely.geometry import Polygon, box

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_repair import validate_candidate, validate_source_candidate


def polish_source_candidate(pk, candidate, max_shift=.5, tolerance=1e-8, rounds=12, padding=.001):
    """Use padded source polygons as a search aid, then recheck both original geometries.

    Padding tightens the search constraint; final acceptance still measures actual
    source gaps and margins directly. The shifted proxy frame handles any inset.
    """
    if not hasattr(pk, "instance"):
        return candidate
    from evaluate import part_rings
    clearance = float(pk.instance.get("clearance", 0))
    margin = float(pk.instance.get("margin", 0))
    inset = float(pk.instance.get("solve_sheet", {}).get("inset", 0))
    radius = clearance / 2 + padding
    shift = margin - radius - inset
    polygons = {}
    for raw_id in candidate["placed"]:
        pid = int(raw_id)
        outer, holes, _ = part_rings(pk.parts[pid], 0, "true")
        # Retain the existing solve outline too: correcting a true-ring contact
        # must not slide a part into a neighbour's more conservative solve outline.
        polygons[pid] = Polygon(outer, holes).buffer(radius, quad_segs=128).union(pk.solve_poly[pid])
    proxy = SimpleNamespace(parts=pk.parts, solve_poly=polygons,
                            W=float(pk.instance["sheet"]["w"]) - 2 * margin + 2 * radius,
                            H=float(pk.instance["sheet"]["h"]) - 2 * margin + 2 * radius)
    shifted = copy.deepcopy(candidate)
    shifted["placed"] = {str(p): [v[0] - shift, v[1] - shift, v[2]] for p, v in candidate["placed"].items()}
    repaired = polish_candidate(proxy, shifted, max_shift=max_shift, tolerance=tolerance, rounds=rounds)
    if repaired is None:
        return None
    repaired["placed"] = {str(p): [v[0] + shift, v[1] + shift, v[2]] for p, v in repaired["placed"].items()}
    repaired["source_polish_padding_mm"] = padding
    valid, _ = validate_candidate(pk, repaired, tolerance=tolerance)
    return repaired if valid else None


def polish_candidate(pk, candidate, max_shift=.5, tolerance=1e-8, rounds=12):
    original = {int(p): tuple(v) for p, v in candidate["placed"].items()}
    transforms = dict(original)
    geometries = {}
    padding = 1e-6
    sheet = box(0, 0, pk.W, pk.H)

    def projected(pid, tx, ty, angle):
        shape = affinity.translate(affinity.rotate(pk.solve_poly[pid], math.degrees(angle), origin=(0, 0)), tx, ty)
        x0, y0, x1, y1 = shape.bounds
        dx = max(0, padding - x0) - max(0, x1 - pk.W + padding)
        dy = max(0, padding - y0) - max(0, y1 - pk.H + padding)
        if math.hypot(tx + dx - original[pid][0], ty + dy - original[pid][1]) > max_shift:
            return None
        return (tx + dx, ty + dy, angle), affinity.translate(shape, dx, dy)

    for pid, transform in transforms.items():
        result = projected(pid, *transform)
        if result is None:
            return None
        transforms[pid], geometries[pid] = result

    def score(pid, shape):
        return shape.difference(sheet).area + sum(shape.intersection(other).area
                                                 for p, other in geometries.items() if p != pid)

    def output():
        result = copy.deepcopy(candidate)
        result["placed"] = {str(p): list(v) for p, v in transforms.items()}
        result["polish_max_translation_mm"] = max((math.hypot(v[0] - original[p][0], v[1] - original[p][1])
                                                   for p, v in transforms.items()), default=0)
        result["polish_validation_area_tolerance_mm2"] = tolerance
        valid, _ = validate_candidate(pk, result, tolerance=tolerance, check_source=False)
        if not valid:
            return None
        source_valid, _ = validate_source_candidate(pk, result)
        if source_valid:
            return result
        repaired = polish_source_candidate(pk, result, max_shift=max_shift, tolerance=tolerance, rounds=rounds)
        if repaired is not None and any(math.hypot(v[0] - original[int(p)][0], v[1] - original[int(p)][1]) > max_shift
                                        for p, v in repaired["placed"].items()):
            return None
        return repaired

    valid = output()
    if valid is not None:
        return valid
    steps = [x for x in (1e-5, 1e-4, 1e-3, .01, .1, .5) if x <= max_shift]
    for _ in range(rounds):
        improved = False
        for pid in sorted(geometries):
            current = score(pid, geometries[pid])
            if current <= tolerance:
                continue
            tx, ty, angle = transforms[pid]
            best = (current, transforms[pid], geometries[pid])
            for step in steps:
                for direction in range(16):
                    a = direction * math.tau / 16
                    result = projected(pid, tx + step * math.cos(a), ty + step * math.sin(a), angle)
                    if result is None:
                        continue
                    transform, shape = result
                    value = score(pid, shape)
                    if value < best[0]:
                        best = value, transform, shape
                    if best[0] <= tolerance:
                        break
                if best[0] <= tolerance:
                    break
            # Avoid accumulating large partial slides on a boundary-pinned part.
            # Another participant may resolve this contact with a tiny move.
            if best[0] <= tolerance:
                transforms[pid], geometries[pid] = best[1], best[2]
                improved = True
        valid = output()
        if valid is not None:
            return valid
        if not improved:
            break
    return None


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--pool", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--max-shift", type=float, default=.5)
    args = ap.parse_args()
    instance = json.loads(Path(args.instance).read_text(encoding="utf-8"))
    parts = {int(p["id"]): p for p in instance["parts"]}
    polys = {pid: Polygon((p.get("solve") or p)["outer"], (p.get("solve") or p).get("holes", []))
             for pid, p in parts.items()}
    pk = SimpleNamespace(parts=parts, solve_poly=polys, W=instance["solve_sheet"]["w"], H=instance["solve_sheet"]["h"])
    source = json.loads(Path(args.pool).read_text(encoding="utf-8"))
    accepted = []
    for candidate in source:
        valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
        polished = candidate if valid else polish_candidate(pk, candidate, max_shift=args.max_shift)
        if polished is not None:
            accepted.append(polished)
        if not valid:
            print(candidate.get("tag"), why, "recovered" if polished else "rejected",
                  polished.get("polish_max_translation_mm") if polished else "", flush=True)
    output = Path(args.out)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(accepted, indent=1), encoding="utf-8")
    print(f"saved {len(accepted)}/{len(source)} candidates to {output}")


if __name__ == "__main__":
    main()

"""Export a bench3dm instance to NestFab command-line inputs: one DXF per part (closed LWPOLYLINEs, outer + holes,
UN-offset rings in model units) and a parts CSV (Type=File rows), so NestFab can be benchmarked headlessly:
  NestFab.exe -s W:H:N -i parts.csv -a -n gap:edge:rot -c seconds -o result.dxf result.csv
Usage: python -I instance_to_nestfab.py --instance <instance.json> --out <dir> [--rotation 1] [--tilt 0] [--qty-from-instance]"""
import argparse, json, csv
from pathlib import Path


def _pairs(*items):
    return [str(x) for x in items]


def write_dxf(path, outer, holes):
    """R12-style DXF (HEADER + TABLES/LAYER + ENTITIES with POLYLINE/VERTEX/SEQEND): the most widely accepted form
    for CAD importers. Units mm ($INSUNITS 4). Outer contour on layer OUTER, holes on layer HOLES."""
    L = []
    L += _pairs(0, "SECTION", 2, "HEADER", 9, "$ACADVER", 1, "AC1009", 9, "$INSUNITS", 70, 4, 0, "ENDSEC")
    L += _pairs(0, "SECTION", 2, "TABLES", 0, "TABLE", 2, "LAYER", 70, 2)
    for name in ("OUTER", "HOLES"):
        L += _pairs(0, "LAYER", 2, name, 70, 0, 62, 7, 6, "CONTINUOUS")
    L += _pairs(0, "ENDTAB", 0, "ENDSEC")
    L += _pairs(0, "SECTION", 2, "ENTITIES")
    for layer, ring in [("OUTER", outer)] + [("HOLES", h) for h in holes]:
        L += _pairs(0, "POLYLINE", 8, layer, 66, 1, 70, 1)
        for x, y in ring:
            L += _pairs(0, "VERTEX", 8, layer, 10, f"{x:.6f}", 20, f"{y:.6f}", 30, "0.0")
        L += _pairs(0, "SEQEND", 8, layer)
    L += _pairs(0, "ENDSEC", 0, "EOF")
    Path(path).write_text("\n".join(L) + "\n", encoding="ascii")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instance", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--rotation", type=int, default=1, help="NestFab rotation increment (deg); -1 = Any (Ultra)")
    ap.add_argument("--tilt", type=float, default=0.0, help="Ultra tilt tolerance (deg)")
    ap.add_argument("--mirror", type=int, default=0)
    a = ap.parse_args()
    inst = json.load(open(a.instance, encoding="utf-8"))
    out = Path(a.out); (out / "parts").mkdir(parents=True, exist_ok=True)
    rows = []
    for p in inst["parts"]:
        name = f"P{int(p['id']):03d}"
        rel = f"parts/{name}.dxf"
        write_dxf(out / rel, p["outer"], p.get("holes", []))
        rows.append({"Type": "File", "Name": name, "Quantity": int(p.get("qty", 1)), "Rotation": a.rotation,
                     "Tilt": a.tilt, "CanMirror": a.mirror, "Priority": 2, "IsSheet": 0, "Value1": rel.replace("/", "\\")})
    with open(out / "parts.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["Type", "Name", "Quantity", "Rotation", "Tilt", "CanMirror", "Priority", "IsSheet", "Value1"])
        w.writeheader(); w.writerows(rows)
    sh = inst["sheet"]
    meta = {"sheet": f"{sh['w']:g}:{sh['h']:g}:{int(sh.get('count', 10))}", "clearance": inst["clearance"],
            "margin": inst["margin"], "nestparams": f"{inst['clearance']:g}:{inst['margin']:g}:{a.rotation}:{a.mirror}:{a.tilt:g}",
            "parts": len(rows), "copies": sum(r["Quantity"] for r in rows)}
    (out / "job.json").write_text(json.dumps(meta, indent=1))
    print(json.dumps(meta))


if __name__ == "__main__":
    main()

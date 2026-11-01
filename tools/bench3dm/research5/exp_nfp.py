# Experiment (iii): the NFP engine (BLF/contact constructive + GA over the sequence = NestFab Standard
# architecture) as the per-sheet packer for NestFab's sheet-0 composition on the ROUND-offset instance.
import sys, json, time
from pathlib import Path
ROOT = Path(r"C:/pc/3_code/OpenNest/tools/bench3dm"); sys.path.insert(0, str(ROOT))
import groupfill_nfp as gf
inst = json.load(open(ROOT / "out/job1_fixed_round/instance.json", encoding="utf-8"))
dll = str(ROOT.parent.parent / "src/opennest_cpp/build/Release/nfp_nest.dll")
G = [2, 9, 15, 25]
from shapely.geometry import Polygon
SA = float(inst["solve_sheet"]["w"]) * float(inst["solve_sheet"]["h"])
area = {}
for p_ in inst["parts"]:
    s_ = p_.get("solve") or {}
    area[int(p_["id"])] = Polygon(s_.get("outer", p_["outer"]), s_.get("holes", p_.get("holes", []))).buffer(0).area
for rot, tilt in ((36, 0.0), (36, 2.0), (72, 2.0)):
    pk = gf.Packer(inst, "gh_nfp", dll, rot, 0.3, tilt=tilt)
    for ids, tag in ((G + [7, 8], "plates+strips"), (G + [7, 8, 35, 42, 43, 56], "NestFab sheet0 set")):
        t = time.time(); placed, res = pk.pack(ids, 10, 30, 100, 900)
        print(f"nfp rot {rot} tilt {tilt} {tag}: placed {len(placed)}/{len(ids)} missing {[i for i in ids if i not in placed]} "
              f"util {100*sum(area[i] for i in placed)/SA:.1f}% w {pk.used_width(placed) if placed else 0:.0f} {time.time()-t:.0f}s", flush=True)
print("done")

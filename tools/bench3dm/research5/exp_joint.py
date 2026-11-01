# Experiments (collision engine): can plates + long strips be packed JOINTLY on one sheet, and does a bigger
# insertion budget place a strip into the frozen forced8 sheet 4 (geometry says: no fit anywhere)?
import sys, os, json, time
from pathlib import Path
ROOT = Path(r"C:/pc/3_code/OpenNest/tools/bench3dm"); sys.path.insert(0, str(ROOT))
for kv in ["NP_SMART_ROT=1", "NP_EXPL_STRIKES=2", "NP_EXPL_NOIMPR=50", "NP_SIMPL_MAXDEV=0.5"]:
    k, v = kv.split("="); os.environ[k] = v
import groupfill_nfp as gf, engines as eng
gf.ENGINE = eng.ENGINE_NP
inst = json.load(open(ROOT / "out/job1_fixed_round/instance.json", encoding="utf-8"))
pk = gf.PackerNp(inst, "gh_np", str(ROOT.parent.parent / "src/nest_physics_cpp/build/Release/nest_physics.dll"), 3600, 0.0)
SA = pk.SA
def run(tag, ids, gens, pop, seed, cap, fixed=None):
    t = time.time(); placed, res = pk.pack(ids, gens, pop, seed, cap, fixed=fixed)
    ok = all(i in placed for i in ids)
    print(f"{tag}: ids {ids} gens {gens} starts {pop} seed {seed} -> placed {len(placed)}/{len(ids)} {'ALL' if ok else 'missing ' + str([i for i in ids if i not in placed])} "
          f"util {100*sum(pk.solve_area[i] for i in placed)/SA:.1f}% w {pk.used_width(placed) if placed else 0:.0f} {time.time()-t:.0f}s", flush=True)
    return placed
G = [2, 9, 15, 25]
# C: bigger insertion budget into the frozen forced8 sheet 4 (expected: impossible)
plc = json.load(open(ROOT / "out/groupfill_np_forced8_round_insert/placements.json", encoding="utf-8"))
s4 = {it["part"]: (it["tx"], it["ty"], it["angle_rad"]) for it in plc["instances"] if it["sheet"] == 4}
fixed = [pk.placed_ring(i, *s4[i]) for i in s4]
for seed in (100, 7):
    run("C insert 7 into frozen sheet4", [7], 4000, 2, seed, 240, fixed=fixed)
run("C insert 43 into frozen sheet4", [43], 4000, 2, 100, 240, fixed=fixed)
# A: plates + the two long strips jointly
for seed in (100, 101):
    run("A joint plates+strips", G + [7, 8], 8000, 2, seed, 400)
# B: NestFab's sheet-0 composition exactly (plates + strips + 4 smalls)
for seed in (100, 101):
    run("B NestFab sheet0 set", G + [7, 8, 35, 42, 43, 56], 8000, 2, seed, 400)
# D: plates + strips + 2 more strips (the other leftovers)
run("D plates+4 strips", G + [7, 8, 18, 21], 8000, 2, 100, 400)
print("done")

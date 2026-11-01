# Experiment (iv): pack the plate group (2,9,15,25) alone with axis-aligned big-part angles (NP_SMART_ROT_BIG=1,
# +-3 deg tilt) vs free tilt, freeze, then (a) geometric fit test of the long strips in the free space and (b) engine
# insertion of the strips. Tests the hypothesis "tilted plates shred the free space; axis-aligned plates leave lanes".
import sys, os, json, time, subprocess
from pathlib import Path
ROOT = Path(r"C:/pc/3_code/OpenNest/tools/bench3dm"); sys.path.insert(0, str(ROOT))
mode = sys.argv[1] if len(sys.argv) > 1 else "big"
env = {"NP_SMART_ROT": "1", "NP_EXPL_STRIKES": "2", "NP_EXPL_NOIMPR": "50", "NP_SIMPL_MAXDEV": "0.5"}
if mode == "big":
    env["NP_SMART_ROT_BIG"] = "1"
os.environ.update(env)
import groupfill_nfp as gf, engines as eng
gf.ENGINE = eng.ENGINE_NP
inst = json.load(open(ROOT / "out/job1_fixed_round/instance.json", encoding="utf-8"))
pk = gf.PackerNp(inst, "gh_np", str(ROOT.parent.parent / "src/nest_physics_cpp/build/Release/nest_physics.dll"), 3600, 0.0)
G = [2, 9, 15, 25]; strips = [7, 8, 18, 21, 43]
out = ROOT / "research5" / f"axis_{mode}"; out.mkdir(parents=True, exist_ok=True)
for seed in (100, 101):
    t = time.time(); placed, res = pk.pack(G, 8000, 2, seed, 400)
    ok = all(i in placed for i in G)
    print(f"{mode} seed {seed}: plates placed {len(placed)}/4 {'ALL' if ok else 'FAIL'} w {pk.used_width(placed) if placed else 0:.0f} "
          f"angles {[round(__import__('math').degrees(placed[i][2]) % 360, 1) for i in G if i in placed]} {time.time()-t:.0f}s", flush=True)
    if not ok:
        continue
    inst_list = [{"part": i, "copy": 0, "sheet": 0, "tx": placed[i][0], "ty": placed[i][1], "angle_rad": placed[i][2]} for i in G]
    pj = out / f"plates_seed{seed}.json"
    json.dump({"schema": eng.PLACEMENTS_SCHEMA, "engine": "np_nest", "n_sheets": 1, "instances": inst_list}, open(pj, "w"))
    r = subprocess.run([sys.executable, "-I", "-X", "utf8", str(ROOT / "research5/diag_free.py"), "--instance", str(ROOT / "out/job1_fixed_round/instance.json"),
                        "--placements", str(pj), "--sheets", "0", "--leftovers", "7,8,18,21,43", "--out", str(out / f"diag_seed{seed}"), "--px", "5", "--angle-step", "2"],
                       capture_output=True, text=True)
    print("\n".join(l for l in r.stdout.splitlines() if l.startswith("sheet") or "leftover" in l), flush=True)
    fixed = [pk.placed_ring(i, *placed[i]) for i in G]
    ins = {}
    for i in strips:
        t = time.time(); p2, _ = pk.pack([i], 1500, 2, seed, 120, fixed=fixed)
        if i in p2:
            ins[i] = p2[i]; fixed.append(pk.placed_ring(i, *p2[i]))
        print(f"   engine insert {i}: {'placed' if i in p2 else 'refused'} {time.time()-t:.0f}s", flush=True)
    tot = sum(pk.solve_area[i] for i in list(G) + list(ins))
    print(f"{mode} seed {seed}: plates + {len(ins)} strips -> util {100*tot/pk.SA:.1f}%", flush=True)
print("done")

# Experiment (v): 2D compaction of the plate group = pack plates on a REDUCED-HEIGHT strip (h = f*H) so they form a
# compact block and the remaining band (H - h) is a coherent lane; then geometric fit test + engine insertion of the
# long strips into the frozen plate block. Technique under test: "plates-first with two-axis compaction".
import sys, os, json, time, subprocess, math
from pathlib import Path
ROOT = Path(r"C:/pc/3_code/OpenNest/tools/bench3dm"); sys.path.insert(0, str(ROOT))
os.environ.update({"NP_SMART_ROT": "1", "NP_EXPL_STRIKES": "2", "NP_EXPL_NOIMPR": "50", "NP_SIMPL_MAXDEV": "0.5"})
import groupfill_nfp as gf, engines as eng
gf.ENGINE = eng.ENGINE_NP
inst = json.load(open(ROOT / "out/job1_fixed_round/instance.json", encoding="utf-8"))
pk = gf.PackerNp(inst, "gh_np", str(ROOT.parent.parent / "src/nest_physics_cpp/build/Release/nest_physics.dll"), 3600, 0.0)
groups = {"g2": ([2, 9, 15, 25], [7, 8, 18, 21, 43]), "g1": ([1, 3, 13, 28], [7, 8, 18, 21, 43])}
out = ROOT / "research5" / "band"; out.mkdir(parents=True, exist_ok=True)
for tag, (G, strips) in groups.items():
    g_area = sum(pk.solve_area[i] for i in G)
    for frac in (0.85, 0.78, 0.72, 0.66):
        h = frac * pk.H
        if g_area / pk.W > 0.97 * h:
            continue
        t = time.time(); placed, res = pk.pack(G, 8000, 2, 100, 400, height=h)
        ok = all(i in placed for i in G)
        print(f"{tag} h={frac:.2f}H ({h:.0f}): plates {len(placed)}/{len(G)} {'ALL' if ok else 'FAIL'} w {pk.used_width(placed) if placed else 0:.0f} "
              f"plate area/strip area {100*g_area/(pk.W*h):.0f}% {time.time()-t:.0f}s", flush=True)
        if not ok:
            continue
        ub = gf.used_bbox(pk, placed)
        print(f"   used bbox {ub[0]:.0f} x {ub[1]:.0f} -> free band {pk.H - ub[1]:.0f} mm high", flush=True)
        inst_list = [{"part": i, "copy": 0, "sheet": 0, "tx": placed[i][0], "ty": placed[i][1], "angle_rad": placed[i][2]} for i in G]
        pj = out / f"{tag}_h{frac:.2f}.json"
        json.dump({"schema": eng.PLACEMENTS_SCHEMA, "engine": "np_nest", "n_sheets": 1, "instances": inst_list}, open(pj, "w"))
        r = subprocess.run([sys.executable, "-I", "-X", "utf8", str(ROOT / "research5/diag_free.py"), "--instance", str(ROOT / "out/job1_fixed_round/instance.json"),
                            "--placements", str(pj), "--sheets", "0", "--leftovers", ",".join(map(str, strips)), "--out", str(out / f"{tag}_h{frac:.2f}_diag"),
                            "--px", "5", "--angle-step", "2"], capture_output=True, text=True)
        print("\n".join("   " + l for l in r.stdout.splitlines() if l.startswith("sheet") or "leftover" in l), flush=True)
        fixed = [pk.placed_ring(i, *placed[i]) for i in G]; ins = {}
        for i in strips:
            t = time.time(); p2, _ = pk.pack([i], 1500, 2, 100, 120, fixed=fixed)
            if i in p2:
                ins[i] = p2[i]; fixed.append(pk.placed_ring(i, *p2[i]))
            print(f"   engine insert {i}: {'placed' if i in p2 else 'refused'} {time.time()-t:.0f}s", flush=True)
        tot = sum(pk.solve_area[i] for i in list(G) + list(ins))
        print(f"{tag} h={frac:.2f}H: plates + {len(ins)} strips -> util {100*tot/pk.SA:.1f}%", flush=True)
        break  # first feasible reduced height is enough per group
print("done")

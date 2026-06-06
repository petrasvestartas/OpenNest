#!/usr/bin/env python3
"""Compare C++ (parity_runner) vs C# (opennest_console --parity) placement dumps.

Tiered pragmatic parity (the bar agreed for "same results as the C# component"):
  Tier A (exact):   sheets used, placed count, per-source placed/unplaced counts
  Tier B (tol):     per placed part, |dx|,|dy| <= 0.5*spacing ; rotation equal (discrete)
  Tier C (tol):     |fit_cpp - fit_cs| / max(1,|fit_cs|) <= 1e-3

Parts within a source are interchangeable, so placements are matched per-source by
nearest-neighbour (rotation-equal) rather than by instance index.

Usage: python parity_diff.py parity_cpp.txt parity_cs.txt [spacing]
"""
import sys, math

def load(path):
    sheets = placed = total = None
    fitness = None
    rows = []  # (source, x, y, rot, sheetId, fitted)
    with open(path) as f:
        for line in f:
            t = line.split()
            if not t: continue
            if t[0] == "SHEETS_USED": sheets = int(t[1])
            elif t[0] == "FITNESS":   fitness = float(t[1])
            elif t[0] == "PLACED":    placed, total = int(t[1]), int(t[2])
            else:
                rows.append((int(t[0]), float(t[1]), float(t[2]), float(t[3]), int(t[4]), int(t[5])))
    return dict(sheets=sheets, fitness=fitness, placed=placed, total=total, rows=rows)

def by_source(rows, fitted_only=True):
    d = {}
    for (src, x, y, rot, sid, fit) in rows:
        if fitted_only and not fit: continue
        d.setdefault(src, []).append((x, y, rot, sid, fit))
    return d

def rot_eq(a, b):
    d = abs((a - b) % 360.0)
    return min(d, 360.0 - d) <= 0.5

def main():
    if len(sys.argv) < 3:
        print("usage: parity_diff.py parity_cpp.txt parity_cs.txt [spacing]"); sys.exit(2)
    cpp = load(sys.argv[1]); cs = load(sys.argv[2])
    spacing = float(sys.argv[3]) if len(sys.argv) > 3 else 5.0
    tol = 0.5 * spacing

    print("=== Parity report (C++ vs C#) ===")
    print(f"  sheets_used : cpp={cpp['sheets']}  cs={cs['sheets']}")
    print(f"  placed      : cpp={cpp['placed']}/{cpp['total']}  cs={cs['placed']}/{cs['total']}")
    print(f"  fitness     : cpp={cpp['fitness']:.6g}  cs={cs['fitness']:.6g}")

    # --- Tier A ---
    tierA = (cpp['sheets'] == cs['sheets']) and (cpp['placed'] == cs['placed'])
    cppP, csP = by_source(cpp['rows']), by_source(cs['rows'])
    srcs = sorted(set(cppP) | set(csP))
    for s in srcs:
        if len(cppP.get(s, [])) != len(csP.get(s, [])):
            tierA = False

    # --- Tier B: nearest-neighbour match per source ---
    max_d = 0.0; sum_d = 0.0; n_match = 0; n_total = 0; n_bad = 0
    for s in srcs:
        a = list(cppP.get(s, [])); b = list(csP.get(s, []))
        used = [False] * len(b)
        for (x, y, rot, sid, fit) in a:
            n_total += 1
            best = -1; bestd = 1e18
            for j, (bx, by, brot, bsid, bfit) in enumerate(b):
                if used[j] or not rot_eq(rot, brot): continue
                d = math.hypot(x - bx, y - by)
                if d < bestd: bestd = d; best = j
            if best >= 0:
                used[best] = True
                max_d = max(max_d, bestd); sum_d += bestd
                if bestd <= tol: n_match += 1
                else: n_bad += 1
            else:
                n_bad += 1
    tierB = (n_bad == 0) and (max_d <= tol)

    # --- Tier C ---
    denom = max(1.0, abs(cs['fitness']))
    relfit = abs(cpp['fitness'] - cs['fitness']) / denom
    tierC = relfit <= 1e-3

    print(f"  position    : matched_within_tol={n_match}/{n_total}  max_delta={max_d:.4f}  "
          f"mean_delta={(sum_d/max(1,n_total)):.4f}  (tol={tol})")
    print(f"  fitness_rel : {relfit:.3e}  (tol=1e-3)")
    print(f"  Tier A (structural exact) : {'PASS' if tierA else 'FAIL'}")
    print(f"  Tier B (position <= {tol}) : {'PASS' if tierB else 'FAIL'}")
    print(f"  Tier C (fitness <= 1e-3)  : {'PASS' if tierC else 'FAIL'}")
    ok = tierA and tierB and tierC
    print(f"=== {'PARITY PASS' if ok else 'PARITY PARTIAL/FAIL'} ===")
    sys.exit(0 if ok else 1)

if __name__ == "__main__":
    main()

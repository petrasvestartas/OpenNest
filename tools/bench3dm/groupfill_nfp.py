"""Group-and-fill multi-sheet driver prototype on top of nfp_nest.dll (the OpenNest2 engine).

Why: on jobs where a few large parts hold most of the material, the sheet count is decided by how the
large parts are grouped per sheet, not by the filler packing.  Sequential greedy fill (what both engines
do today) decides that grouping by accident and leaves a poor tail.

Phases
  A. big set B = parts with solve-area >= --big-frac of the solve sheet (capped at --big-max by area).
     Feasibility tests of groups g of B (pairs -> triples -> quads, pruned by area and by sub-group
     feasibility) using single-sheet packs; for feasible groups record the used width w_g.
  B. exact partition of B into k groups (scipy milp): every big part in exactly one group, k groups,
     maximise the summed remnant width (W - w_g); k starts at the area lower bound.
  C. fill: for each group (least free area first) pack group + all remaining fillers on ONE sheet;
     the engine places big parts first (area order), fillers take what fits.  Leftover fillers are
     offered again to every sheet (re-pack), and finally packed on an extra sheet if any remain.

Usage:
  python -I tools/bench3dm/groupfill_nfp.py --instance <instance.json> --out <dir> [--dll <nfp_nest.dll>]
      [--k 5] [--big-frac 0.05] [--big-max 24] [--max-group 4] [--seed 100]
      [--test-gens 1 --test-pop 1] [--fill-gens 3 --fill-pop 10] [--rotations 4]
      [--curve-tol 0.05] [--cap-test 120] [--cap-fill 900]
"""
import argparse
import itertools
import json
import math
import sys
import time
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import engines as eng  # noqa: E402
from shapely.geometry import Polygon  # noqa: E402
from shapely import affinity  # noqa: E402

ENGINE = getattr(eng, "ENGINE_NFP", "nfp_nest")


def log(*a):
    print(time.strftime("[%H:%M:%S]"), *a, flush=True)


class Packer:
    """Single-sheet packs with nfp_nest on a sub-instance of parts (all qty 1)."""

    def __init__(self, instance, cfg_name, dll_override, rotations, curve_tol, tilt=0.0):
        self.instance = instance
        self.cfg = eng.resolve_config(cfg_name)
        self.cfg = dict(self.cfg)
        self.cfg["sheets"] = 1
        self.base = dict(self.cfg["params"])
        self.base.update({"exactNfp": 0, "curveTolerance": float(curve_tol), "rotations": int(rotations),
                          "tryAllRotations": 1, "maxSheets": 1, "smartRotations": 1, "rotationTiltDeg": float(tilt)})
        self.dll = eng.find_dll(ENGINE, dll_override)
        self.lib = eng.load_engine(ENGINE, self.dll["path"])
        self.parts = {int(p["id"]): p for p in instance["parts"]}
        self.W = float(instance["solve_sheet"]["w"])
        self.H = float(instance["solve_sheet"]["h"])
        self.SA = self.W * self.H
        self.solve_poly = {}
        self.solve_area = {}
        for pid, p in self.parts.items():
            s = p.get("solve") or {}
            outer = s.get("outer", p["outer"])
            holes = s.get("holes", p.get("holes", []))
            poly = Polygon(outer, holes)
            if not poly.is_valid:
                poly = poly.buffer(0)
            self.solve_poly[pid] = poly
            self.solve_area[pid] = poly.area
        self.calls = 0
        self.wall = 0.0

    def pack(self, ids, gens, pop, seed, cap_s):
        sub = dict(self.instance)
        sub["parts"] = [dict(self.parts[i], qty=1) for i in ids]
        sub["sheet"] = dict(self.instance["sheet"], count=1)
        inputs = eng.flatten_nfp(sub, self.cfg)
        pv = dict(self.base)
        pv.update({"generations": int(gens), "populationSize": int(pop), "seed": int(seed)})
        params = eng.params_struct(ENGINE, pv)
        res = eng.solve_nfp(self.lib, inputs, params, cap_s)
        self.calls += 1
        self.wall += res["wall_s"]
        placed = {}
        for k in range(inputs.instance_count):
            pi = int(res["part_index"][k])
            if pi < 0:
                continue
            pid = inputs.part_ids[pi]
            if int(res["sheet"][k]) == 0:
                placed[pid] = (float(res["tx"][k]), float(res["ty"][k]), float(res["angle_rad"][k]))
        return placed, res

    def used_width(self, placed):
        w = 0.0
        for pid, (tx, ty, ang) in placed.items():
            g = affinity.translate(affinity.rotate(self.solve_poly[pid], math.degrees(ang), origin=(0, 0)), tx, ty)
            w = max(w, g.bounds[2])
        return w


class PackerNp(Packer):
    """Same interface on nest_physics.dll (OpenNestCollision engine) in fit_mode 1 (ONE sheet, MAX FILL):
    GLS overlap minimisation with continuous rotation decides feasibility instead of a BLF constructive pass.
    `gens` is the relaxation-round budget (iter_budget, >= 500) and `pop` the number of multi-starts."""

    def __init__(self, instance, cfg_name, dll_override, rotations, curve_tol, tilt=0.0):
        self.instance = instance
        self.cfg = dict(eng.resolve_config(cfg_name))
        self.cfg["sheets"] = 1
        self.base = dict(self.cfg["params"])
        self.base.update({"num_rotations": int(rotations), "fit_mode": 1, "max_sheets": 1, "iter_mode": 1,
                          "simplify_tolerance": float(curve_tol)})
        self.dll = eng.find_dll(ENGINE, dll_override)
        self.lib = eng.load_engine(ENGINE, self.dll["path"])
        self.parts = {int(p["id"]): p for p in instance["parts"]}
        self.W = float(instance["solve_sheet"]["w"])
        self.H = float(instance["solve_sheet"]["h"])
        self.SA = self.W * self.H
        self.solve_poly = {}
        self.solve_area = {}
        for pid, p in self.parts.items():
            s = p.get("solve") or {}
            poly = Polygon(s.get("outer", p["outer"]), s.get("holes", p.get("holes", [])))
            if not poly.is_valid:
                poly = poly.buffer(0)
            self.solve_poly[pid] = poly
            self.solve_area[pid] = poly.area
        self.calls = 0
        self.wall = 0.0

    def placed_ring(self, pid, tx, ty, ang):
        g = affinity.translate(affinity.rotate(self.solve_poly[pid], math.degrees(ang), origin=(0, 0)), tx, ty)
        return [list(c) for c in g.exterior.coords[:-1]]

    def pack(self, ids, gens, pop, seed, cap_s, fixed=None, height=None, mandatory=None):
        """fixed: list of rings (already placed parts, solve space) registered as sheet holes = forbidden
        regions, so this pack only fills the free space around them (two-stage fill).
        height: pack on a strip of this height instead of the full sheet (remnant compaction).
        mandatory: set of part ids that must be placed (engine E1: `np_nest_prio`, placed-AREA objective,
        smallest normal parts dropped until the sheet fits)."""
        sub = dict(self.instance)
        sub["parts"] = [dict(self.parts[i], qty=1) for i in ids]
        sub["sheet"] = dict(self.instance["sheet"], count=1)
        if height is not None:
            sub["solve_sheet"] = dict(self.instance["solve_sheet"], h=float(height))
        inputs = eng.flatten_np(sub, self.cfg, mandatory=set(mandatory)) if mandatory else eng.flatten_np(sub, self.cfg)
        if fixed:
            rings = [np.asarray(r, dtype=float) for r in fixed]
            inputs.shc = eng._i32([len(rings)])
            inputs.hvc = eng._i32([len(r) for r in rings])
            inputs.hxy = eng._f64(np.concatenate([r.ravel() for r in rings]))
        pv = dict(self.base)
        pv.update({"iter_budget": max(500, int(gens)), "n_starts": max(1, int(pop)), "seed": int(seed)})
        params = eng.params_struct(ENGINE, pv)
        res = eng.solve_np(self.lib, inputs, params, cap_s)
        self.calls += 1
        self.wall += res["wall_s"]
        q = res["quality"]
        placed = {}
        if q["overlap_pairs"] > 0 or q["oob"] > 0:
            log(f"  warn: np pack not clean ({q}) -> treated as empty")
            return placed, res
        for k, (pid, c) in enumerate(inputs.copies):
            if int(res["sheet"][k]) == 0:
                placed[pid] = (float(res["tx"][k]), float(res["ty"][k]), float(res["angle_rad"][k]))
        return placed, res


def group_tests(pk, big, args, cache_path):
    cache = {}
    if cache_path.exists():
        cache = json.loads(cache_path.read_text())
    SA = pk.SA

    def key(g):
        return ",".join(str(i) for i in sorted(g))

    def test(g):
        k = key(g)
        if k in cache:
            return cache[k]
        if sum(pk.solve_area[i] for i in g) > args.area_cap * SA:
            cache[k] = {"feasible": False, "why": "area"}
            return cache[k]
        best = None
        for s in range(args.test_seeds):
            placed, res = pk.pack(list(g), args.test_gens, args.test_pop, args.seed + s, args.cap_test)
            if len(placed) == len(g):
                w = pk.used_width(placed)
                if best is None or w < best["w"]:
                    best = {"feasible": True, "w": w, "placed": {str(p): v for p, v in placed.items()}}
        cache[k] = best or {"feasible": False, "why": "pack"}
        if len(cache) % 25 == 0:
            cache_path.write_text(json.dumps(cache))
        return cache[k]

    feasible = {}
    for i in big:
        r = test((i,))
        if r["feasible"]:
            feasible[(i,)] = r
    log(f"singletons feasible {len(feasible)}/{len(big)}")
    shard_i, shard_n = (0, 1)
    if args.shard:
        a_, b_ = args.shard.split("/")
        shard_i, shard_n = int(a_), int(b_)
    for size in range(2, args.max_group + 1):
        n_tested = 0
        idx = 0
        for g in itertools.combinations(big, size):
            # all sub-groups of size-1 must be feasible
            if any(tuple(sorted(sg)) not in feasible for sg in itertools.combinations(g, size - 1)):
                continue
            # sharding (only meaningful for the LARGEST size of this run, sub-groups must all be cached):
            # untested combos of other shards are skipped and left out of the cache
            if shard_n > 1 and size == args.max_group and key(g) not in cache:
                idx += 1
                if (idx - 1) % shard_n != shard_i:
                    continue
            r = test(g)
            n_tested += 1
            if r["feasible"]:
                feasible[tuple(sorted(g))] = r
        log(f"size {size}: tested {n_tested}, feasible so far {sum(1 for k in feasible if len(k) == size)}; "
            f"calls {pk.calls} wall {pk.wall:.0f}s")
        cache_path.write_text(json.dumps(cache))
    cache_path.write_text(json.dumps(cache))
    return feasible


def partition(pk, big, feasible, k, exclude=()):
    from scipy.optimize import milp, LinearConstraint, Bounds
    groups = list(feasible.keys())
    n = len(groups)
    if n == 0:
        return None
    W = pk.W
    c = np.array([-(W - feasible[g]["w"]) for g in groups])  # maximise remnant width
    A = np.zeros((len(big) + 1 + len(exclude), n))
    lo = np.zeros(len(big) + 1 + len(exclude))
    hi = np.zeros(len(big) + 1 + len(exclude))
    idx = {p: r for r, p in enumerate(big)}
    for j, g in enumerate(groups):
        for p in g:
            A[idx[p], j] = 1.0
    lo[:len(big)] = 1.0
    hi[:len(big)] = 1.0
    A[len(big), :] = 1.0
    lo[len(big)] = k
    hi[len(big)] = k
    for e, sol in enumerate(exclude):  # no-good cut: do not repeat a previous partition
        row = len(big) + 1 + e
        for j in sol:
            A[row, j] = 1.0
        lo[row] = 0
        hi[row] = len(sol) - 1
    res = milp(c, constraints=LinearConstraint(A, lo, hi), integrality=np.ones(n), bounds=Bounds(0, 1))
    if res.status != 0 or res.x is None:
        return None
    chosen = [j for j in range(n) if res.x[j] > 0.5]
    return chosen, groups



def long_dim(pk, i):
    b = pk.solve_poly[i].bounds
    return max(b[2] - b[0], b[3] - b[1])


def assign_long(pk, groups, pool, args):
    """Fillers whose longest dimension >= joint_long_frac x max(W, H) cannot be inserted into a plates-only layout
    (they need a lane that only a joint placement leaves); assign them to groups longest-first, each to the group
    with the most free area left (worst-fit), within joint_free_cap of that free area."""
    out = {g: [] for g in groups}
    if args.joint_long_frac <= 0:
        return out
    thr = args.joint_long_frac * max(pk.W, pk.H)
    free = {g: pk.SA - sum(pk.solve_area[i] for i in g) for g in groups}
    longs = sorted([i for i in pool if long_dim(pk, i) >= thr], key=lambda i: -long_dim(pk, i))
    for i in longs:
        for g in sorted(groups, key=lambda g: -free[g]):
            if pk.solve_area[i] <= args.joint_free_cap * free[g]:
                out[g].append(i)
                free[g] -= pk.solve_area[i]
                break
    log(f"  joint long fillers (>= {thr:.0f} mm): {len(longs)} -> " +
        "; ".join(f"{g}:{out[g]}" for g in groups if out[g]))
    return out


def pack_plates(pk, g, extra, args, seed):
    """Stage 1: pack the plate group together with its assigned long fillers; every plate must land. If a plate
    is missing, drop the smallest extra filler and retry. Returns (placed, dropped_extras) or (None, extras)."""
    extra = sorted(extra, key=lambda i: -pk.solve_area[i])
    while True:
        best = None
        for s in range(args.fill_seeds):
            placed, res = pk.pack(list(g) + extra, args.fill_gens, args.fill_pop, seed + s, args.cap_fill)
            if all(i in placed for i in g):
                keep = {i: placed[i] for i in list(g) + extra if i in placed}
                score = (len(keep), -pk.used_width(keep))
                if best is None or score > best[0]:
                    best = (score, keep)
        if best is not None:
            got = best[1]
            dropped = [i for i in extra if i not in got]
            if extra:
                log(f"    stage 1: group {g} + long {extra} -> {len(got) - len(g)}/{len(extra)} long placed")
            return got, dropped
        if not extra:
            return None, []
        extra = extra[:-1]


def fill_knap(pk, g, pool, args, seed, extra=()):
    """Whole-sheet knapsack fill (engine E1): ONE pack of the plates (mandatory) + assigned long parts + fillers by
    area down to a target utilisation; the engine keeps the maximum area that fits, never dropping a plate. Then
    (optional) single-part insertion of the remaining small fillers into the frozen layout."""
    # step A (robust, plain engine): plates + assigned long parts, drop-smallest-and-retry; this is the floor
    base, _dropped = pack_plates(pk, g, [i for i in extra if i in pool], args, seed)
    if base is None:
        return None
    base_area = sum(pk.solve_area[i] for i in base)
    # step B (engine E1): the step-A set mandatory + fillers by area to the target in ONE pack; kept only if it
    # keeps every step-A part and adds area (the E1 retry path is budget-limited today)
    sel = []
    acc = base_area
    for i in sorted([i for i in pool if i not in base], key=lambda i: -pk.solve_area[i]):
        if acc + pk.solve_area[i] <= args.fill_target * pk.SA and len(sel) < args.knap_max:
            sel.append(i)
            acc += pk.solve_area[i]
    best = (base_area, base)
    if sel:
        for s in range(args.fill_seeds):
            placed, res = pk.pack(list(base) + sel, args.fill_gens, args.fill_pop, seed + s, args.cap_fill,
                                  mandatory=set(base))
            if not all(i in placed for i in base):
                log(f"  warn: knap pack of {g} lost {[i for i in base if i not in placed]} (seed {seed + s})")
                continue
            area = sum(pk.solve_area[i] for i in placed)
            if area > best[0]:
                best = (area, placed)
    placed = dict(best[1])
    log(f"    knap: step A {len(base) - len(g)} long ({100 * base_area / pk.SA:.1f}%), offered {len(sel)} fillers "
        f"({100 * acc / pk.SA:.1f}% incl.) -> kept {len(placed) - len(base)} more ({100 * best[0] / pk.SA:.1f}%)")
    if args.fill_insert:
        fixed = [pk.placed_ring(i, *placed[i]) for i in placed]
        free = pk.SA - best[0]
        n0 = len(placed)
        for i in sorted([i for i in pool if i not in placed], key=lambda i: -pk.solve_area[i]):
            if pk.solve_area[i] > free:
                continue
            got, res = pk.pack([i], args.insert_gens, 1, seed, args.insert_cap, fixed=fixed)
            if i in got:
                placed[i] = got[i]
                fixed.append(pk.placed_ring(i, *got[i]))
                free -= pk.solve_area[i]
        log(f"    insert stage: +{len(placed) - n0} fillers -> {100 * sum(pk.solve_area[i] for i in placed) / pk.SA:.1f}%")
    return placed

def fill_two_stage(pk, g, pool, args, seed, extra=()):
    """Stage 1: pack the big group alone (all must land). Stage 2: freeze those plates as sheet holes and
    pack fillers (largest first, up to a fraction of the free area) into the remaining space."""
    plates, _dropped = pack_plates(pk, g, list(extra), args, seed)
    if plates is None:
        log(f"  warn: group {g} not packable alone")
        return None
    pool = [i for i in pool if i not in plates]
    fixed = [pk.placed_ring(i, *plates[i]) for i in plates]
    free = pk.SA - sum(pk.solve_area[i] for i in plates)
    best = (0.0, {})
    if args.fill_insert:
        # constructive stage: insert fillers one at a time, largest first, each frozen once placed (a GLS
        # single-part feasibility check per insertion); then one whole-pool pack for the remaining smalls
        ins = {}
        for i in sorted(pool, key=lambda i: -pk.solve_area[i]):
            if pk.solve_area[i] > free - sum(pk.solve_area[j] for j in ins):
                continue
            placed, res = pk.pack([i], args.insert_gens, 1, seed, args.cap_fill, fixed=fixed)
            if i in placed:
                ins[i] = placed[i]
                fixed.append(pk.placed_ring(i, *placed[i]))
        rest = [i for i in pool if i not in ins]
        if rest:
            placed, res = pk.pack(rest, args.fill_gens, args.fill_pop, seed, args.cap_fill, fixed=fixed)
            ins.update(placed)
        area = sum(pk.solve_area[i] for i in ins)
        log(f"    insert stage: {len(ins)} fillers ({100 * area / pk.SA:.1f}% of sheet)")
        best = (area, ins)
        out = dict(plates)
        out.update(ins)
        return out
    for T in [None] + [args.fill_target - args.fill_step * j for j in range(args.fill_tries)]:
        if T is None:
            sel = list(pool)
            acc = sum(pk.solve_area[i] for i in sel)
        else:
            if T <= 0:
                break
            sel, acc = [], 0.0
            for i in sorted(pool, key=lambda i: -pk.solve_area[i]):
                if acc + pk.solve_area[i] <= T * free:
                    sel.append(i)
                    acc += pk.solve_area[i]
        if not sel:
            break
        if T is not None and best[0] >= acc:
            continue  # this subset cannot beat what the whole-pool pack already placed
        placed, res = pk.pack(sel, args.fill_gens, args.fill_pop, seed, args.cap_fill, fixed=fixed)
        area = sum(pk.solve_area[i] for i in placed)
        log(f"    fillers target {'all' if T is None else f'{T:.2f}'}: offered {len(sel)} ({100 * acc / pk.SA:.1f}% of sheet) -> placed {len(placed)} "
            f"({100 * area / pk.SA:.1f}%)")
        if area > best[0]:
            best = (area, placed)
        if len(placed) == len(sel):
            break
    out = dict(plates)
    out.update(best[1])
    return out


def fill_balanced(pk, groups, pool, args, seed):
    """Two-stage fill with ALL k sheets open at once. Stage 1: pack every plate group alone and freeze the
    plates as sheet holes. Stage 2: insert fillers one at a time, largest first, into the sheet with the most
    free area that accepts it (worst-fit decreasing across bins; a sequential fill gives the first sheet the
    best fillers and leaves sheets 4-5 with parts that no longer fit). Stage 3: one whole-pool pack per sheet
    (most free first) for the small remainder."""
    sheets = []
    longs = assign_long(pk, groups, pool, args)
    for g in groups:
        placed, _dropped = pack_plates(pk, g, longs[g], args, seed)
        if placed is None:
            log(f"  warn: group {g} not packable alone")
            placed, res = pk.pack(list(g), args.fill_gens, args.fill_pop, seed, args.cap_fill)
        pool = [i for i in pool if i not in placed]
        sheets.append({"group": list(g), "placed": dict(placed),
                       "fixed": [pk.placed_ring(i, *placed[i]) for i in placed]})
        log(f"  plates sheet {len(sheets)}: group {g} -> {len(placed)}/{len(g)} placed, "
            f"free {100 * (pk.SA - sum(pk.solve_area[i] for i in placed)) / pk.SA:.1f}%")

    def free(sh):
        return pk.SA - sum(pk.solve_area[i] for i in sh["placed"])

    pool = sorted(pool, key=lambda i: -pk.solve_area[i])
    rest = []
    for i in pool:
        done = False
        for sh in sorted(sheets, key=free, reverse=True):
            if pk.solve_area[i] > free(sh) * args.balanced_free_cap:
                continue
            placed, res = pk.pack([i], args.insert_gens, 1, seed, args.insert_cap, fixed=sh["fixed"])
            if i in placed:
                sh["placed"][i] = placed[i]
                sh["fixed"].append(pk.placed_ring(i, *placed[i]))
                done = True
                break
        if not done:
            rest.append(i)
        log(f"    insert {i} (area {100 * pk.solve_area[i] / pk.SA:.1f}%): "
            + ("sheet " + str(1 + sheets.index(sh)) if done else "no sheet")
            + "; free " + "/".join(f"{100 * free(x) / pk.SA:.0f}" for x in sheets))
    for sh in sorted(sheets, key=free, reverse=True):
        if not rest:
            break
        placed, res = pk.pack(rest, args.fill_gens, args.fill_pop, seed, args.cap_fill, fixed=sh["fixed"])
        for i, tr in placed.items():
            sh["placed"][i] = tr
            sh["fixed"].append(pk.placed_ring(i, *tr))
        rest = [i for i in rest if i not in placed]
        log(f"    pool pack sheet {1 + sheets.index(sh)}: +{len(placed)}, rest {len(rest)}")
    for si, sh in enumerate(sheets):
        sh.pop("fixed", None)
        nf = len([i for i in sh["placed"] if i not in sh["group"]])
        log(f"  sheet {si + 1}: group {tuple(sh['group'])} + {nf} fillers, util(solve) "
            f"{100 * sum(pk.solve_area[i] for i in sh['placed']) / pk.SA:.1f}%")
    return sheets, rest


def fill_phase(pk, groups_chosen, fillers, args, seed):
    sheets = []  # list of dict(group=..., placed={pid: (tx,ty,ang)})
    pool = list(fillers)
    order = sorted(groups_chosen, key=lambda g: sum(pk.solve_area[i] for i in g), reverse=True)  # tightest first
    if args.fill_balanced:
        return fill_balanced(pk, order, pool, args, seed)
    longs = assign_long(pk, order, pool, args)
    long_all = {i for g in longs for i in longs[g]}
    done_groups = []
    for g in order:
        best = None
        if args.two_stage:
            # long parts assigned to this group, plus long parts an earlier group had to drop (carry forward:
            # a long part that misses its joint pack can never be inserted into a frozen layout later)
            carried = [i for i in pool if i in long_all and i not in longs[g]
                       and any(i in longs[h] for h in done_groups)]
            if args.fill_knap:
                placed = fill_knap(pk, g, pool, args, seed, extra=[i for i in longs[g] if i in pool] + carried)
            else:
                placed = fill_two_stage(pk, g, pool, args, seed, extra=[i for i in longs[g] if i in pool] + carried)
            done_groups.append(g)
            if placed is not None:
                best = (sum(pk.solve_area[i] for i in placed), placed)
            else:
                placed, res = pk.pack(list(g), args.fill_gens, args.fill_pop, seed, args.cap_fill)
                best = (sum(pk.solve_area[i] for i in placed), placed)
            placed = best[1]
            sheets.append({"group": list(g), "placed": placed})
            pool = [i for i in pool if i not in placed]
            log(f"  sheet {len(sheets)}: group {g} + {len(placed) - len([i for i in g if i in placed])} fillers, "
                f"util(solve) {100 * sum(pk.solve_area[i] for i in placed) / pk.SA:.1f}%, pool left {len(pool)}")
            continue
        g_area = sum(pk.solve_area[i] for i in g)
        if args.fill_target > 0:
            # knapsack-style: offer only as many fillers (largest first) as fit an area target, so the
            # single-sheet packer can compress everything onto the sheet instead of dropping big parts
            targets = [args.fill_target - args.fill_step * j for j in range(args.fill_tries)]
        else:
            targets = [None]
        for T in targets:
            if T is None:
                sel = list(pool)
            else:
                budget = T * pk.SA - g_area
                sel, acc = [], 0.0
                for i in sorted(pool, key=lambda i: -pk.solve_area[i]):
                    if acc + pk.solve_area[i] <= budget:
                        sel.append(i)
                        acc += pk.solve_area[i]
            ids = list(g) + sel
            for s in range(args.fill_seeds):
                placed, res = pk.pack(ids, args.fill_gens, args.fill_pop, seed + s, args.cap_fill)
                if not all(i in placed for i in g):
                    log(f"  warn: group {g} not fully placed (target {T}, seed {seed + s}, offered {len(sel)} fillers) "
                        f"missing {[i for i in g if i not in placed]}")
                    continue
                score = sum(pk.solve_area[i] for i in placed)
                if best is None or score > best[0]:
                    best = (score, placed)
            if best is not None:
                break
        if best is None:
            placed, res = pk.pack(list(g), args.fill_gens, args.fill_pop, seed, args.cap_fill)
            best = (sum(pk.solve_area[i] for i in placed), placed)
        placed = best[1]
        sheets.append({"group": list(g), "placed": placed})
        pool = [i for i in pool if i not in placed]
        log(f"  sheet {len(sheets)}: group {g} + {len(placed) - len(g)} fillers, util(solve) "
            f"{100 * sum(pk.solve_area[i] for i in placed) / pk.SA:.1f}%, pool left {len(pool)}")
    return sheets, pool


def repack_leftovers(pk, sheets, pool, args, seed):
    """Offer leftovers to each sheet again (re-pack group + its fillers + leftovers)."""
    for si, sh in enumerate(sheets):
        if not pool:
            break
        if args.repack_knap:
            cur = list(sh["placed"])
            placed, res = pk.pack(cur + pool, args.fill_gens, args.fill_pop, seed + 7 * (si + 1), args.cap_fill,
                                  mandatory=set(cur))
            if all(i in placed for i in cur) and len(placed) > len(cur):
                gained = [i for i in placed if i not in cur]
                sh["placed"] = placed
                pool = [i for i in pool if i not in placed]
                log(f"  repack(knap) sheet {si + 1}: +{len(gained)} -> pool {len(pool)}")
            else:
                log(f"  repack(knap) sheet {si + 1}: no gain")
            continue
        if args.two_stage:
            plates = [i for i in sh["group"] if i in sh["placed"]]
            fixed = [pk.placed_ring(i, *sh["placed"][i]) for i in plates]
            old_fill = [i for i in sh["placed"] if i not in sh["group"]]
            placed, res = pk.pack(old_fill + pool, args.fill_gens, args.fill_pop, seed + 7 * (si + 1), args.cap_fill,
                                  fixed=fixed)
            if sum(pk.solve_area[i] for i in placed) > sum(pk.solve_area[i] for i in old_fill):
                gained = [i for i in placed if i not in old_fill]
                lost = [i for i in old_fill if i not in placed]
                sh["placed"] = {i: sh["placed"][i] for i in plates} | placed
                pool = [i for i in pool if i not in placed] + lost
                log(f"  repack sheet {si + 1}: +{len(gained)} -{len(lost)} -> pool {len(pool)}")
            continue
        ids = list(sh["placed"].keys()) + pool
        placed, res = pk.pack(ids, args.fill_gens, args.fill_pop, seed + 7 * (si + 1), args.cap_fill)
        if all(i in placed for i in sh["group"]) and len(placed) > len(sh["placed"]):
            gained = [i for i in placed if i not in sh["placed"]]
            lost = [i for i in sh["placed"] if i not in placed]
            sh["placed"] = placed
            pool = [i for i in pool if i not in placed] + lost
            log(f"  repack sheet {si + 1}: +{len(gained)} -{len(lost)} -> pool {len(pool)}")
    return sheets, pool



def hole_fill(pk, sheets, pool, args, extra_sheets=None):
    """Part-in-hole post pass: for each leftover (largest
    first) try every plate hole on every sheet (largest first) whose bbox can contain the part; sample rotations and a
    position grid and accept the first placement whose solve ring lies inside the hole's solve ring (both already
    carry clearance/2, so containment = clearance respected). One part per hole."""
    from shapely.geometry import Polygon
    from shapely import affinity
    used = set()
    holes = []  # (area, sheet index, poly)
    for si, sh in enumerate(sheets):
        for pid, (tx, ty, ang) in sh["placed"].items():
            src = pk.parts[pid].get("solve") or {}
            for hi, h in enumerate(src.get("holes", [])):
                hp = affinity.translate(affinity.rotate(Polygon(h), math.degrees(ang), origin=(0, 0)), tx, ty)
                if hp.is_valid and hp.area > 0:
                    holes.append((hp.area, si, (pid, hi), hp))
    holes.sort(key=lambda t: -t[0])
    placed_any = []
    for i in sorted(pool, key=lambda i: -pk.solve_area[i]):
        part = pk.solve_poly[i]
        pb = part.bounds
        pw, ph = pb[2] - pb[0], pb[3] - pb[1]
        pmin = min(pw, ph)
        done = False
        for area, si, key, hp in holes:
            if key in used or area < part.area * 1.02:
                continue
            hb = hp.bounds
            if min(hb[2] - hb[0], hb[3] - hb[1]) < pmin:
                continue
            step = max(5.0, min(hb[2] - hb[0], hb[3] - hb[1]) / 40.0)
            angles = list(range(0, 360, int(args.hole_angle_step)))
            for a in angles:
                rp = affinity.rotate(part, a, origin=(0, 0))
                rb = rp.bounds
                rw, rh = rb[2] - rb[0], rb[3] - rb[1]
                if rw > hb[2] - hb[0] or rh > hb[3] - hb[1]:
                    continue
                x = hb[0] - rb[0]
                while x + rb[0] + rw <= hb[2] + 1e-6 and not done:
                    y = hb[1] - rb[1]
                    while y + rb[1] + rh <= hb[3] + 1e-6:
                        cand = affinity.translate(rp, x, y)
                        if hp.contains(cand):
                            sheets[si]["placed"][i] = (x, y, math.radians(a))
                            used.add(key)
                            placed_any.append(i)
                            log(f"  hole fill: part {i} ({100 * part.area / pk.SA:.1f}%) into hole {key} on sheet {si + 1} at {a} deg")
                            done = True
                            break
                        y += step
                    x += step
                if done:
                    break
            if done:
                break
    pool = [i for i in pool if i not in placed_any]
    log(f"  hole fill: {len(placed_any)} parts seated in holes, pool left {len(pool)}")
    return sheets, pool

def used_bbox(pk, placed):
    xs, ys = [], []
    for pid, (tx, ty, ang) in placed.items():
        r = np.asarray(pk.placed_ring(pid, tx, ty, ang))
        xs.append(r[:, 0].max()); ys.append(r[:, 1].max())
    return (max(xs) if xs else 0.0), (max(ys) if ys else 0.0)


def compact_remnant(pk, placed, args, seed):
    """Re-pack one sheet's parts into the smallest bounding rectangle (min used_w x used_h), so the rest of
    the sheet stays a usable rectangular offcut. Sweeps strip heights h <= H (the strip packer minimises the
    width for each h) and keeps the feasible layout with the least bbox area; the original layout competes."""
    ids = list(placed)
    if not ids:
        return placed
    w0, h0 = used_bbox(pk, placed)
    best = (w0 * h0, placed, w0, h0)
    hmin = max(min(pk.solve_poly[i].bounds[2] - pk.solve_poly[i].bounds[0],
                   pk.solve_poly[i].bounds[3] - pk.solve_poly[i].bounds[1]) for i in ids)
    area = sum(pk.solve_area[i] for i in ids)
    # a strip of height h needs width >= the widest part's smaller dimension and area >= the parts' area
    w_lb = hmin
    tried = 0
    for frac in args.remnant_heights:
        h = frac * pk.H
        if h < hmin or h < area / pk.W or h * max(w_lb, area / h) >= best[0]:
            continue  # cannot fit / cannot beat the current best bbox
        tried += 1
        cand, res = pk.pack(ids, args.fill_gens, args.fill_pop, seed + 31 * tried, args.cap_fill, height=h)
        if len(cand) != len(ids):
            continue
        w, hh = used_bbox(pk, cand)
        if w * hh < best[0]:
            best = (w * hh, cand, w, hh)
    log(f"  remnant: bbox {w0:.0f}x{h0:.0f} ({100 * w0 * h0 / pk.SA:.1f}% of sheet) -> "
        f"{best[2]:.0f}x{best[3]:.0f} ({100 * best[0] / pk.SA:.1f}%), {tried} strip packs")
    return best[1]


def compact_last_sheet(pk, sheets, extra, args, seed):
    """Apply compact_remnant to the least-filled sheet (the remnant sheet) of the final layout."""
    cands = [("sheet", i, sh["placed"]) for i, sh in enumerate(sheets)] + [("extra", i, e) for i, e in enumerate(extra)]
    if not cands:
        return
    kind, idx, pl = min(cands, key=lambda c: sum(pk.solve_area[i] for i in c[2]))
    new = compact_remnant(pk, pl, args, seed)
    if kind == "sheet":
        sheets[idx]["placed"] = new
    else:
        extra[idx] = new


def write_placements(pk, sheets, pool, out_dir, instance_path, args, extra_sheets):
    instances = []
    for si, sh in enumerate(sheets):
        for pid, (tx, ty, ang) in sh["placed"].items():
            instances.append({"part": int(pid), "copy": 0, "sheet": si, "tx": tx, "ty": ty, "angle_rad": ang})
    base = len(sheets)
    for si, sh in enumerate(extra_sheets):
        for pid, (tx, ty, ang) in sh.items():
            instances.append({"part": int(pid), "copy": 0, "sheet": base + si, "tx": tx, "ty": ty, "angle_rad": ang})
    placed_ids = {i["part"] for i in instances}
    for pid in pk.parts:
        if pid not in placed_ids:
            instances.append({"part": int(pid), "copy": 0, "sheet": -1, "tx": 0.0, "ty": 0.0, "angle_rad": 0.0})
    n_sheets = len(sheets) + len(extra_sheets)
    doc = {"schema": eng.PLACEMENTS_SCHEMA, "instance": str(Path(instance_path).resolve()),
           "engine": ENGINE, "config": "groupfill_nfp", "seed": int(args.seed),
           "dll": {"path": pk.dll["path"], "sha256": pk.dll["sha256"]},
           "wall_s": round(pk.wall, 1), "cancelled": False,
           "engine_report": {"calls": pk.calls, "driver": "groupfill_nfp"},
           "frame": eng.FRAME, "n_sheets": n_sheets, "instances": instances}
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    p = out / "placements.json"
    p.write_text(json.dumps(doc, indent=1))
    return str(p)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instance", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--dll", default=None)
    ap.add_argument("--config", default="gh_nfp")
    ap.add_argument("--k", type=int, default=0, help="target sheets (0 = area lower bound)")
    ap.add_argument("--big-frac", type=float, default=0.05)
    ap.add_argument("--big-max", type=int, default=24)
    ap.add_argument("--max-group", type=int, default=4)
    ap.add_argument("--area-cap", type=float, default=0.98)
    ap.add_argument("--seed", type=int, default=100)
    ap.add_argument("--test-gens", type=int, default=1)
    ap.add_argument("--test-pop", type=int, default=1)
    ap.add_argument("--test-seeds", type=int, default=1)
    ap.add_argument("--fill-gens", type=int, default=3)
    ap.add_argument("--fill-pop", type=int, default=10)
    ap.add_argument("--fill-seeds", type=int, default=1)
    ap.add_argument("--rotations", type=int, default=4)
    ap.add_argument("--curve-tol", type=float, default=0.05)
    ap.add_argument("--tilt", type=float, default=0.0)
    ap.add_argument("--cap-test", type=float, default=120)
    ap.add_argument("--cap-fill", type=float, default=900)
    ap.add_argument("--partitions", type=int, default=3, help="alternative partitions to try")
    ap.add_argument("--fill-target", type=float, default=0.0,
                    help="if > 0: offer fillers (largest first) only up to this fraction of the sheet area per pack, "
                         "retrying with --fill-step smaller targets up to --fill-tries times (knapsack-style fill)")
    ap.add_argument("--fill-step", type=float, default=0.03)
    ap.add_argument("--fill-insert", action="store_true",
                    help="two-stage fill: insert fillers one by one (largest first, frozen once placed), then one "
                         "whole-pool pack for the rest")
    ap.add_argument("--insert-gens", type=int, default=600, help="rounds per single-part insertion pack")
    ap.add_argument("--insert-cap", type=float, default=60.0, help="wall cap (s) per single-part insertion pack")
    ap.add_argument("--joint-long-frac", type=float, default=0.0,
                    help="two-stage fill: fillers whose longest dimension >= this fraction of max(W,H) are packed "
                         "JOINTLY with the plates in stage 1 (assigned to groups worst-fit), since they cannot be "
                         "inserted into a plates-only layout afterwards; 0 = off")
    ap.add_argument("--joint-free-cap", type=float, default=0.6,
                    help="a long filler is assigned to a group only if its area <= this fraction of the group's free area")
    ap.add_argument("--fill-knap", action="store_true",
                    help="whole-sheet knapsack fill (engine E1 np_nest_prio): plates mandatory + fillers by area to "
                         "--fill-target in ONE pack; the engine drops the smallest normal parts until the sheet fits")
    ap.add_argument("--knap-max", type=int, default=24, help="max fillers offered per knapsack pack")
    ap.add_argument("--repack-knap", action="store_true",
                    help="leftover repack with E1: every part on the sheet mandatory, leftovers offered as normal")
    ap.add_argument("--hole-fill", action="store_true",
                    help="seat leftover parts in plate holes (geometric containment test in solve space) before "
                         "opening an extra sheet")
    ap.add_argument("--hole-angle-step", type=float, default=10.0)
    ap.add_argument("--remnant-compact", action="store_true",
                    help="re-pack the least-filled sheet into the smallest bounding rectangle (strip-height sweep) "
                         "so the rest of that sheet is a usable rectangular offcut")
    ap.add_argument("--remnant-heights", type=lambda v: [float(x) for x in v.split(",")],
                    default=[1.0, 0.875, 0.75, 0.625, 0.5, 0.4375, 0.375, 0.3125, 0.25, 0.1875, 0.125],
                    help="strip heights to try for --remnant-compact, as fractions of the sheet height")
    ap.add_argument("--fill-balanced", action="store_true",
                    help="two-stage fill with all sheets open: plates first on every sheet, then fillers inserted "
                         "largest-first into the sheet with the most free area (implies --two-stage)")
    ap.add_argument("--balanced-free-cap", type=float, default=0.98,
                    help="skip a sheet for a filler whose area exceeds this fraction of the sheet's free area")
    ap.add_argument("--shard", default=None, help="i/n: test only every n-th untested group of size --max-group "
                    "(sub-groups must already be cached); merge the shard caches afterwards")
    ap.add_argument("--cache", default=None, help="group cache path (default <out>/group_cache.json)")
    ap.add_argument("--tests-only", action="store_true", help="stop after the group feasibility tests")
    ap.add_argument("--two-stage", action="store_true",
                    help="np engine: pack the big group alone, then freeze it as sheet holes and pack fillers "
                         "into the free space (targets are fractions of the FREE area)")
    ap.add_argument("--fill-tries", type=int, default=6)
    ap.add_argument("--force-groups", default=None, help="skip phases A/B: groups as '1,2;3,4,5;...' part ids")
    ap.add_argument("--engine", default="nfp", choices=["nfp", "np"],
                    help="single-sheet packer: nfp = nfp_nest.dll (GA+NFP), np = nest_physics.dll fit_mode 1 (GLS); "
                         "with np, --test-gens/--fill-gens are relaxation rounds and --test-pop/--fill-pop multi-starts")
    ap.add_argument("--env", action="append", default=[], help="KEY=VALUE set in the process environment before "
                    "loading the engine (e.g. NP_SMART_ROT=1), repeatable")
    args = ap.parse_args()
    if args.fill_balanced or args.fill_knap:
        args.two_stage = True

    instance = json.load(open(args.instance))
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    import os
    for kv in args.env:
        k_, _, v_ = kv.partition("=")
        os.environ[k_] = v_
    global ENGINE
    if args.engine == "np":
        ENGINE = eng.ENGINE_NP
        if args.config == "gh_nfp":
            args.config = "gh_np"
        pk = PackerNp(instance, args.config, args.dll, args.rotations, args.curve_tol, args.tilt)
    else:
        pk = Packer(instance, args.config, args.dll, args.rotations, args.curve_tol, args.tilt)
    SA = pk.SA
    ids_by_area = sorted(pk.parts, key=lambda i: -pk.solve_area[i])
    big = [i for i in ids_by_area if pk.solve_area[i] >= args.big_frac * SA][:args.big_max]
    fillers = [i for i in ids_by_area if i not in big]
    total = sum(pk.solve_area.values())
    k_lb = max(1, math.ceil(total / SA))
    k = args.k or k_lb
    log(f"parts {len(pk.parts)}: big {len(big)} ({100 * sum(pk.solve_area[i] for i in big) / total:.1f}% of material), "
        f"fillers {len(fillers)}; total {total / SA:.3f} sheets (solve space); k_lb {k_lb}; target k {k}")
    log(f"engine {pk.dll['path']} sha {pk.dll['sha256'][:12]}; rotations {args.rotations} curveTol {args.curve_tol}")

    t0 = time.time()
    if args.force_groups:
        gs = [tuple(int(x) for x in grp.split(",")) for grp in args.force_groups.split(";")]
        covered = {i for g in gs for i in g}
        fillers = [i for i in ids_by_area if i not in covered]
        log(f"forced groups: {gs}; fillers {len(fillers)}")
        sheets, pool = fill_phase(pk, gs, fillers, args, args.seed)
        if pool:
            sheets, pool = repack_leftovers(pk, sheets, pool, args, args.seed)
        if pool and args.hole_fill:
            sheets, pool = hole_fill(pk, sheets, pool, args)
        extra = []
        rest = list(pool)
        while rest:
            placed, res = pk.pack(rest, args.fill_gens, args.fill_pop, args.seed, args.cap_fill)
            if not placed:
                break
            extra.append(placed)
            rest = [i for i in rest if i not in placed]
        if args.remnant_compact:
            compact_last_sheet(pk, sheets, extra, args, args.seed)
        ppath = write_placements(pk, sheets, pool, out, args.instance, args, extra)
        log(f"forced: {len(sheets) + len(extra)} sheets, leftovers {len(pool)}; wrote {ppath}")
        from evaluate import evaluate_files
        m = evaluate_files(args.instance, ppath, str(out), write_3dm=False, write_svg=True)
        log(f"evaluate: sheets {m['n_sheets']} util {[round(u, 1) for u in m['util_per_sheet']]} overlaps {m['overlap_pairs']} viol {m['clearance_violations']} placed {m['placed']}/{m['total']}")
        return
    feasible = group_tests(pk, big, args, Path(args.cache) if args.cache else out / "group_cache.json")
    if args.tests_only:
        log(f"tests only: {len(feasible)} feasible groups, {pk.calls} packs, {time.time() - t0:.0f}s")
        return
    log(f"phase A done: {len(feasible)} feasible groups, {pk.calls} packs, {time.time() - t0:.0f}s")
    sizes = {}
    for g in feasible:
        sizes[len(g)] = sizes.get(len(g), 0) + 1
    log(f"feasible by size: {sizes}")

    filler_area = sum(pk.solve_area[i] for i in fillers)
    exclude = []
    best_result = None
    for attempt in range(args.partitions):
        part = partition(pk, big, feasible, k, exclude)
        while part is None and k < k_lb + 3:
            k += 1
            log(f"no partition with {k - 1} groups -> trying k={k}")
            part = partition(pk, big, feasible, k, exclude)
        if part is None:
            log("no partition found")
            break
        chosen, groups = part
        exclude.append(chosen)
        gs = [groups[j] for j in chosen]
        free = [SA - sum(pk.solve_area[i] for i in g) for g in gs]
        log(f"partition {attempt + 1} (k={k}): " + "; ".join(
            f"{g} w={feasible[g]['w']:.0f} free={100 * f / SA:.0f}%" for g, f in zip(gs, free)))
        log(f"  free area {sum(free) / SA:.2f} sheets vs fillers {filler_area / SA:.2f} sheets")
        sheets, pool = fill_phase(pk, gs, fillers, args, args.seed + 100 * attempt)
        if pool:
            sheets, pool = repack_leftovers(pk, sheets, pool, args, args.seed + 100 * attempt)
        if pool and args.hole_fill:
            sheets, pool = hole_fill(pk, sheets, pool, args)
        extra = []
        if pool:
            log(f"  leftovers {len(pool)} -> extra sheet(s)")
            rest = list(pool)
            while rest:
                placed, res = pk.pack(rest, args.fill_gens, args.fill_pop, args.seed, args.cap_fill)
                if not placed:
                    break
                extra.append(placed)
                rest = [i for i in rest if i not in placed]
        n_sheets = len(sheets) + len(extra)
        log(f"attempt {attempt + 1}: {n_sheets} sheets, leftovers {len(pool)}, packs {pk.calls}, wall {pk.wall:.0f}s")
        if best_result is None or n_sheets < best_result[0]:
            best_result = (n_sheets, sheets, pool, extra)
        if n_sheets <= k and not pool:
            break

    n_sheets, sheets, pool, extra = best_result
    if args.remnant_compact:
        compact_last_sheet(pk, sheets, extra, args, args.seed)
    ppath = write_placements(pk, sheets, pool, out, args.instance, args, extra)
    log(f"wrote {ppath}: {n_sheets} sheets")
    try:
        from evaluate import evaluate_files
        m = evaluate_files(args.instance, ppath, str(out), write_3dm=False, write_svg=True)
        log(f"evaluate: sheets {m['n_sheets']} util {[round(u, 1) for u in m['util_per_sheet']]} "
            f"overlaps {m['overlap_pairs']} viol {m['clearance_violations']} placed {m['placed']}/{m['total']}")
    except Exception as e:  # noqa: BLE001
        log(f"evaluate failed: {e}")


if __name__ == "__main__":
    main()

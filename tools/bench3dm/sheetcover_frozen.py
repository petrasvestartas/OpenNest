"""Insert small missing parts without moving incumbents; validate solve and source geometry.

Example: python -I tools/bench3dm/sheetcover_frozen.py --instance instance.json
  --baseline placements.json --dll nest_physics.dll --out output --cavities
Only qty=1 instances and contiguous sheet labels are supported. This is an opt-in
research harness; candidate columns and raw proposals are saved for review.
"""
import argparse
import copy
import json
import math
import os
import sys
from multiprocessing import Pool
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))
import engines as eng
import groupfill_nfp as gf
from sheetcover_repair import validate_candidate, save_json
from validate_strict import validate_strict


def accept_additions(pk, incumbent, proposed, allowed):
    """Accept additions one at a time, retaining exact incumbent transforms.

    Validate against every occupant, including parts already seated in holes.
    A proposal cannot overwrite incumbents or introduce an unrequested part.
    """
    current = {int(p): tuple(t) for p, t in incumbent.items()}
    seed = dict(current)
    if not hasattr(pk, 'instance'):
        raise ValueError('source instance is required for strict acceptance')
    if current:
        ok, reason = validate_candidate(pk, {'placed': current, 'group': list(seed)}, tolerance=1e-8)
        if not ok:
            raise ValueError('invalid incumbent: ' + reason)
    accepted, rejected = [], []
    for p, transform in sorted(((int(p), t) for p, t in proposed.items()), key=lambda x: x[0]):
        if p in current:
            if tuple(transform) != current[p]:
                rejected.append([p, 'incumbent overwrite'])
            continue
        if p not in allowed or p not in pk.parts:
            rejected.append([p, 'unrequested part'])
            continue
        count = int(pk.parts[p].get('rotations', 0) or 0)
        if len(transform) == 3 and count > 0 and math.isfinite(transform[2]):
            turns = transform[2] * count / (2 * math.pi)
            if abs(turns - round(turns)) > 1e-5:
                rejected.append([p, 'disallowed discrete rotation'])
                continue
        trial = dict(current)
        trial[p] = tuple(transform)
        ok, why = validate_candidate(pk, {'placed': trial, 'group': list(seed)}, tolerance=1e-8)
        if ok:
            current = trial
            accepted.append(p)
        else:
            rejected.append([p, why])
    assert all(current[p] == t for p, t in seed.items())
    return current, accepted, rejected


def _init(instance, dll, native_workers):
    global _PK
    os.environ['NP_WORKERS'] = str(native_workers)
    os.environ['NP_SIMPL_MAXDEV'] = '0.5'
    gf.ENGINE = eng.ENGINE_NP
    _PK = gf.PackerNp(json.loads(Path(instance).read_text(encoding='utf-8')), 'gh_np', dll, 3600, 0)


def _run(job):
    si, incumbent, missing, iters, cap, seed = job
    fixed = [_PK.placed_ring(p, *t) for p, t in incumbent.items()]
    proposals, raw = _PK.pack(missing, iters, 1, seed, cap, fixed=fixed)
    placed, accepted, rejected = accept_additions(_PK, incumbent, proposals, set(missing))
    return dict(sheet=si, placed=placed, proposed=proposals, accepted=accepted, rejected=rejected,
                wall=raw['wall_s'], cancelled=raw.get('cancelled'), quality=raw.get('quality'))


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ('instance', 'baseline', 'dll', 'out'):
        ap.add_argument('--' + name, required=True)
    ap.add_argument('--cap', type=float, default=30)
    ap.add_argument('--iters', type=int, default=1000)
    ap.add_argument('--workers', type=int, default=5)
    ap.add_argument('--native-workers', type=int, default=4)
    ap.add_argument('--small-frac', type=float, default=.05)
    ap.add_argument('--seed', type=int, default=81000)
    ap.add_argument('--cavities', action='store_true')
    ap.add_argument('--cavities-only', action='store_true')
    ap.add_argument('--hole-angle-step', type=int, default=15)
    args = ap.parse_args()
    if min(args.cap, args.iters, args.workers, args.native_workers, args.small_frac, args.hole_angle_step) <= 0:
        ap.error('budgets and sampling steps must be positive')
    _init(args.instance, args.dll, args.native_workers)
    pk = _PK
    if any(int(p.get('qty', 1)) != 1 for p in pk.parts.values()):
        ap.error('only unique qty=1 parts are supported')
    baseline = json.loads(Path(args.baseline).read_text(encoding='utf-8'))
    sheets, seen = {}, set()
    for p in baseline['instances']:
        if p['sheet'] < 0:
            continue
        pid = int(p['part'])
        if pid in seen or pid not in pk.parts or int(p.get('copy', 0)) != 0:
            ap.error('unknown or duplicate baseline part/copy')
        seen.add(pid)
        sheets.setdefault(int(p['sheet']), {})[pid] = (p['tx'], p['ty'], p['angle_rad'])
    if sorted(sheets) != list(range(len(sheets))) or not sheets:
        ap.error('baseline must contain contiguous nonempty sheets starting at zero')
    for seed in sheets.values():
        accept_additions(pk, seed, {}, set())
    missing = sorted(p for p in pk.parts if p not in seen and pk.solve_area[p] < args.small_frac * pk.SA)
    reports = []
    if missing and not args.cavities_only:
        jobs = [(si, inc, missing, args.iters, args.cap, args.seed + si) for si, inc in sorted(sheets.items())]
        with Pool(args.workers, initializer=_init, initargs=(args.instance, args.dll, args.native_workers)) as pool:
            reports = list(pool.imap_unordered(_run, jobs))
        for r in sorted(reports, key=lambda r: r['sheet']):
            allowed = set(missing) - seen
            sheets[r['sheet']], _, _ = accept_additions(pk, sheets[r['sheet']], r['placed'], allowed)
            seen.update(sheets[r['sheet']])
    if args.cavities or args.cavities_only:
        proposal, _ = gf.hole_fill(pk, [dict(placed=copy.deepcopy(sheets[i])) for i in sorted(sheets)],
                                   [p for p in missing if p not in seen], args)
        for si, proposed in enumerate(proposal):
            sheets[si], accepted, rejected = accept_additions(pk, sheets[si], proposed['placed'], set(missing) - seen)
            seen.update(sheets[si])
            reports.append(dict(sheet=si, stage='cavities', accepted=accepted, rejected=rejected))
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    columns = [dict(tag=f'frozen_s{args.seed}_{si}', placed={str(p): t for p, t in s.items()}, group=sorted(s),
                    plates_ok=True, validated=True, seed=args.seed, area=sum(pk.solve_area[p] for p in s)/pk.SA)
               for si, s in sorted(sheets.items())]
    save_json(out/'candidates.json', columns)
    save_json(out/'proposals.json', reports)
    gf.write_placements(pk, [dict(placed=s) for _, s in sorted(sheets.items())], sorted(set(pk.parts)-seen),
                        str(out), args.instance, args, [])
    metrics = validate_strict(pk.instance, json.loads((out/'placements.json').read_text()), max_sheets=len(sheets))
    save_json(out/'strict_metrics.json', metrics)
    save_json(out/'run.json', dict(arguments=vars(args), remaining=sorted(set(pk.parts)-seen)))
    print(f"Frozen result: {len(seen)}/{len(pk.parts)} parts, {len(sheets)} sheets; missing {sorted(set(pk.parts)-seen)}")


if __name__ == '__main__':
    main()

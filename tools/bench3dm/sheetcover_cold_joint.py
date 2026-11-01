"""Prepare finite mandatory cold repacks of incumbent sheets plus one missing part."""
import argparse
import copy
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_ownership import schedule_ownership


def schedule_cold_joint(sheets, pool, missing, required, areas, sheet_area, *,
                        recipients=2, seed=85000, gens=16000, starts=1, cap=105):
    if min(recipients, gens, starts, cap) <= 0:
        raise ValueError('positive limits required')
    layouts = [{'placed': {int(p): list(v) for p, v in s['placed'].items()}} for s in sheets]
    # This also checks exact-once ownership and preservation of required parts.
    variants = schedule_ownership(layouts, pool, missing, required, areas, sheet_area,
                                  limit=max(1, len(areas) * len(sheets) ** 2 * len(missing)))
    jobs = []
    for incoming in sorted(set(missing), key=lambda p: (areas[p], p)):
        choices = []
        for source, layout in enumerate(layouts):
            candidates = [(layout['placed'], layouts, None)]
            candidates.extend((v['placed'], v['incumbent_partition'], v)
                              for v in variants if v['incoming'] == incoming and v['source_sheet'] == source)
            placed, partition, relocation = min(candidates, key=lambda c: sum(areas[p] for p in c[0]))
            area = sum(areas[p] for p in placed) + areas[incoming]
            if area <= sheet_area:
                choices.append((area, source, placed, partition, relocation))
        for area, source, placed, partition, relocation in sorted(choices, key=lambda c: (c[0], c[1]))[:recipients]:
            group = sorted(set(placed) | {incoming})
            job = {'group': group, 'fillers': [], 'incoming': [incoming],
                   'source_sheet': source, 'incumbent_partition': copy.deepcopy(partition),
                   'required_parts': sorted(required), 'core_area': area / sheet_area,
                   'seed': seed + len(jobs), 'gens': gens, 'starts': starts, 'cap': cap,
                   'tag': f'cold_joint_s{seed}_j{len(jobs)}',
                   'status': 'hypothetical; complete mandatory group and strict validation required'}
            if relocation:
                job['ownership_change'] = {k: relocation[k] for k in
                    ('moved_part', 'destination_sheet', 'support_tag', 'support_pool_index')}
            jobs.append(job)
    return jobs


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ('instance', 'pool', 'placements', 'progress', 'out'):
        ap.add_argument('--' + name, required=True)
    ap.add_argument('--recipients', type=int, default=2)
    ap.add_argument('--seed', type=int, default=85000)
    ap.add_argument('--gens', type=int, default=16000)
    ap.add_argument('--starts', type=int, default=1)
    ap.add_argument('--cap', type=float, default=105)
    ap.add_argument('--native-seconds', type=float, default=90)
    args = ap.parse_args()
    if args.native_seconds <= 0:
        ap.error('native time budget must be positive')
    from shapely.geometry import Polygon
    read = lambda path: json.loads(Path(path).read_text())
    instance, pool, placements, progress = map(read, (args.instance, args.pool, args.placements, args.progress))
    pool = [c for c in pool if c['tag'] in progress['chosen']]
    layouts = [{} for _ in range(placements['n_sheets'])]
    for row in placements['instances']:
        if row['sheet'] >= 0:
            layouts[row['sheet']][row['part']] = [row['tx'], row['ty'], row['angle_rad']]
    areas = {p['id']: Polygon((p.get('solve') or p)['outer'], (p.get('solve') or p).get('holes', [])).area
             for p in instance['parts']}
    jobs = schedule_cold_joint([{'placed': s} for s in layouts], pool, progress['uncovered'],
        progress['required_parts'], areas, instance['solve_sheet']['w'] * instance['solve_sheet']['h'],
        recipients=args.recipients, seed=args.seed, gens=args.gens, starts=args.starts, cap=args.cap)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / 'jobs.json').write_text(json.dumps(jobs, indent=2))
    (out / 'provenance.json').write_text(json.dumps(dict(vars(args),
        runner_flags=['--core-only', '--native-seconds', str(args.native_seconds)],
        preserve_required_parts=True, native_runs=0), indent=2))
    print(json.dumps({'jobs': len(jobs), 'native_runs': 0}))


if __name__ == '__main__':
    main()

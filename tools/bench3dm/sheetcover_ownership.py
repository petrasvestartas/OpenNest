"""Plan bounded warm insertions after supported optional-part ownership changes.

Input pool must contain independently validated actual layouts. Planning never
certifies a new placement: every emitted warm result still needs strict checks.
"""
import argparse
import copy
import hashlib
import json
from pathlib import Path


def schedule_ownership(sheets, pool, missing, required, areas, sheet_area, *,
                       limit=6, seed=85000, gens=4000, starts=1, cap=90):
    """Move one optional part into an actual supported recipient, then insert.

    Each job carries the complete alternative incumbent partition. Consumers
    must use that partition, not the original first-owner assignment.
    """
    if min(limit, gens, starts, cap, sheet_area) <= 0:
        raise ValueError("positive limits and area required")
    layouts = [{int(p): list(v) for p, v in s['placed'].items()} for s in sheets]
    flat = [p for s in layouts for p in s]
    if len(flat) != len(set(flat)):
        raise ValueError("incumbent sheets must assign each part exactly once")
    incumbent, required, missing = set(flat), set(required), set(missing)
    if not required <= incumbent or missing & incumbent:
        raise ValueError("required parts must be present and incoming parts absent")
    supports = [{int(p): list(v) for p, v in c['placed'].items()} for c in pool]
    ranked = []
    seen = set()
    for source, layout in enumerate(layouts):
        for moved in sorted(set(layout) - required):
            for destination, recipient in enumerate(layouts):
                if destination == source:
                    continue
                wanted = set(recipient) | {moved}
                support = next((i for i, s in enumerate(supports) if wanted <= set(s)), None)
                if support is None:
                    continue
                partition = copy.deepcopy(layouts)
                del partition[source][moved]
                partition[destination] = {p: supports[support][p] for p in sorted(wanted)}
                for incoming in sorted(missing):
                    area = sum(areas[p] for p in partition[source]) + areas[incoming]
                    if area > sheet_area:
                        continue
                    data = [incoming, sorted((p, v) for p, v in partition[source].items())]
                    key = hashlib.sha256(json.dumps(data, separators=(',', ':')).encode()).hexdigest()
                    if key in seen:
                        continue
                    seen.add(key)
                    proposal = {'placed': partition[source], 'incoming': incoming, 'key': key,
                        'source_sheet': source, 'destination_sheet': destination, 'moved_part': moved,
                        'support_tag': pool[support].get('tag'), 'support_pool_index': support,
                        'incumbent_partition': [{'placed': s} for s in partition],
                        'preserved_parts': sorted(incumbent), 'required_parts': sorted(required),
                        'status': 'unpacked proposal; strict warm validation required'}
                    ranked.append(((areas[incoming] / max(areas[moved], 1e-12), area,
                                    source, destination, moved, incoming), proposal))
    ranked.sort(key=lambda item: item[0])
    jobs = []
    for _, proposal in ranked[:limit]:
        proposal.update(seed=seed + len(jobs), gens=gens, starts=starts, cap=cap,
                        tag=f'ownership_s{seed}_j{len(jobs)}')
        jobs.append(proposal)
    return jobs


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ('instance', 'pool', 'placements', 'progress', 'out'):
        ap.add_argument('--' + name, required=True)
    ap.add_argument('--limit', type=int, default=6)
    ap.add_argument('--seed', type=int, default=85000)
    ap.add_argument('--gens', type=int, default=4000)
    ap.add_argument('--starts', type=int, default=1)
    ap.add_argument('--cap', type=float, default=90)
    ap.add_argument('--all-pool', action='store_true', help='also consider unselected actual layouts')
    args = ap.parse_args()
    from shapely.geometry import Polygon
    read = lambda path: json.loads(Path(path).read_text())
    instance, pool, placements, progress = map(read, (args.instance, args.pool, args.placements, args.progress))
    if not args.all_pool:
        pool = [c for c in pool if c['tag'] in progress['chosen']]
    layouts = [{} for _ in range(placements['n_sheets'])]
    for row in placements['instances']:
        if row['sheet'] >= 0:
            layouts[row['sheet']][row['part']] = [row['tx'], row['ty'], row['angle_rad']]
    areas = {p['id']: Polygon((p.get('solve') or p)['outer'], (p.get('solve') or p).get('holes', [])).area
             for p in instance['parts']}
    jobs = schedule_ownership([{'placed': s} for s in layouts], pool, progress['uncovered'],
        progress['required_parts'], areas, instance['solve_sheet']['w'] * instance['solve_sheet']['h'],
        limit=args.limit, seed=args.seed, gens=args.gens, starts=args.starts, cap=args.cap)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / 'jobs.json').write_text(json.dumps(jobs, indent=2))
    (out / 'provenance.json').write_text(json.dumps(vars(args), indent=2))
    print(json.dumps({'jobs': len(jobs), 'native_runs': 0}))


if __name__ == '__main__':
    main()

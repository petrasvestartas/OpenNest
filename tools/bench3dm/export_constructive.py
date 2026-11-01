"""Export pre-offset geometry once; native constructive_bench executes the search."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from constructive import flatten


def export(instance, destination, args):
    data = flatten(instance, args.rotations, args.allow_mirror)
    guard = args.stock_guard
    if not math.isfinite(guard) or guard < 0 or 2*guard >= min(data['width'], data['height']):
        raise ValueError('stock guard must be finite, nonnegative, and leave positive stock dimensions')
    # Native origin moves into the original solve stock by (guard,guard).
    # Part-local rings stay unchanged; stock defects move into that new frame.
    for start, size, owner in zip(data['ring_starts'], data['ring_sizes'], data['ring_parts']):
        if owner == -1:
            for j in range(2*start, 2*(start+size)):
                data['xy'][j] -= guard
    angles, starts, counts = [], [], []
    for part, allowed in zip(instance['parts'], data['allowed']):
        starts.append(len(angles))
        # Empty native angle sets enable native regular/smart orientations.
        values = allowed if part.get('allowed_angles') is not None or part.get('rotations', 0) else []
        counts.append(len(values))
        angles.extend(values)
    lines = ['NCB1', f"{data['width']-2*guard:.17g} {data['height']-2*guard:.17g}",
             f'{args.sheets} {args.beam} {args.branch} {args.positions} {args.restarts} {args.workers} {args.seed} {args.rotations} {int(args.smart) | (2 if args.conservative_proxies else 0)}',
             f'{args.seconds:.17g} {args.scan_step:.17g} {args.scale:.17g}',
             f"{len(instance['parts'])} {len(data['ring_starts'])} {len(data['xy'])//2} {len(angles)}"]
    # Original IDs stay in JSON sidecar, allowing nonnumeric identifiers too.
    for i in range(len(instance['parts'])):
        lines.append(f"{data['quantities'][i]} {data['mirror_flags'][i]} {starts[i]} {counts[i]}")
    for row in zip(data['ring_starts'], data['ring_sizes'], data['ring_parts'], data['ring_holes']):
        lines.append(' '.join(map(str, row)))
    for i in range(0, len(data['xy']), 2):
        lines.append(f"{data['xy'][i]:.17g} {data['xy'][i+1]:.17g}")
    lines.extend(format(a, '.17g') for a in angles)
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text('\n'.join(lines)+'\n', encoding='ascii')
    metadata = {'format': 'NCB1', 'part_ids': data['part_ids'], 'instance': str(Path(args.instance).resolve()),
                'instance_sha256': hashlib.sha256(Path(args.instance).read_bytes()).hexdigest(),
                'options': vars(args), 'native_reflection': 'local X before rotation',
                'native_to_solve_translation': [guard, guard],
                'stock_guard_mm': guard, 'native_stock_size': [data['width']-2*guard, data['height']-2*guard],
                'stock_defects_transform': 'native XY = solve XY - (stock_guard,stock_guard); part-local XY unchanged',
                'validation': 'Native output requires independent source-geometry validation before claiming feasibility.'}
    destination.with_suffix(destination.suffix+'.json').write_text(json.dumps(metadata, indent=2), encoding='utf8')
    return data


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--instance', required=True)
    p.add_argument('--out', required=True)
    for name, default in [('sheets',5), ('beam',8), ('branch',4), ('positions',32), ('restarts',1000), ('workers',12), ('seed',100), ('rotations',8)]:
        p.add_argument('--'+name, type=int, default=default)
    for name, default in [('seconds',60), ('scan-step',100), ('scale',100000)]:
        p.add_argument('--'+name, type=float, default=default)
    p.add_argument('--smart', action='store_true')
    p.add_argument('--conservative-proxies', action='store_true')
    p.add_argument('--allow-mirror', action='store_true')
    p.add_argument('--stock-guard', type=float, default=.01,
                   help='additional stock inset in mm; restored to output translations (default .01)')
    args = p.parse_args()
    data = export(json.loads(Path(args.instance).read_text(encoding='utf8')), args.out, args)
    print(json.dumps({'out': args.out, 'parts': len(data['part_ids']), 'copies': sum(data['quantities']), 'points':len(data['xy'])//2}))


if __name__ == '__main__':
    main()

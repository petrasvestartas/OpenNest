"""Independently validate native NCB1 placement CSV against source polygons."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from constructive import flatten, validate


def convert(instance, rows, options, native_to_solve_translation=None):
    data = flatten(instance, options['rotations'], options['allow_mirror'])
    offset = native_to_solve_translation if native_to_solve_translation is not None else [options.get('stock_guard',0)]*2
    if len(offset)!=2 or not all(math.isfinite(v) and v>=0 for v in offset):
        raise ValueError('invalid native-to-solve translation')
    seen, placements = set(), []
    for row in rows:
        p, copy, sheet, mirror = (int(row[k]) for k in ('part','copy','sheet','mirrored'))
        if not 0 <= p < len(data['part_ids']) or not 0 <= copy < data['quantities'][p]:
            raise ValueError('unknown native part/copy')
        if (p, copy) in seen or not -1 <= sheet < options['sheets']:
            raise ValueError('duplicate copy/invalid sheet')
        seen.add((p, copy))
        tx, ty, angle = (float(row[k]) for k in ('tx','ty','angle'))
        if not all(map(math.isfinite, (tx,ty,angle))):
            raise ValueError('nonfinite placement')
        if mirror not in (0,1) or (mirror and not data['mirror_flags'][p]):
            raise ValueError('unauthorized mirror')
        part = instance['parts'][p]
        explicit = part.get('allowed_angles') is not None or part.get('rotations', 0)
        if sheet >= 0 and (explicit or not options['smart']):
            if min(abs(math.remainder(angle-a, 2*math.pi)) for a in data['allowed'][p]) > 1e-7:
                raise ValueError('unauthorized angle')
        placements.append({'part':data['part_ids'][p], 'copy':copy, 'sheet':sheet, 'mirrored':bool(mirror),
                           'tx':tx+offset[0], 'ty':ty+offset[1], 'angle_rad':angle+(math.pi if mirror else 0), 'native_angle_rad':angle})
    if len(seen) != sum(data['quantities']):
        raise ValueError('CSV must enumerate every requested copy, including unplaced')
    doc = {'schema':'opennest.bench3dm.placements/1', 'engine':'nest_constructive_native',
           'instances':placements, 'sheets_given':options['sheets'],
           'native_to_solve_translation':offset, 'stock_guard_mm':options.get('stock_guard',0),
           'n_sheets':len({r['sheet'] for r in placements if r['sheet']>=0})}
    return doc, validate(instance, doc, data, options['sheets'])


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--input',required=True,help='NCB1 input file with .json metadata sidecar')
    p.add_argument('--csv',required=True)
    p.add_argument('--out',required=True)
    args=p.parse_args()
    meta=json.loads(Path(args.input+'.json').read_text(encoding='utf8'))
    raw=Path(meta['instance']).read_bytes()
    if hashlib.sha256(raw).hexdigest()!=meta['instance_sha256']:
        raise ValueError('source instance changed since export')
    instance=json.loads(raw)
    with open(args.csv,newline='') as stream:
        doc, metrics=convert(instance,list(csv.DictReader(stream)),meta['options'],meta.get('native_to_solve_translation'))
    out=Path(args.out);out.mkdir(parents=True,exist_ok=True)
    (out/'placements.json').write_text(json.dumps(doc,indent=2))
    (out/'strict_metrics.json').write_text(json.dumps(metrics,indent=2))
    from evaluate import evaluate_files
    evaluate_files(meta['instance'],str(out/'placements.json'),str(out),write_3dm=False,write_svg=True)
    print(json.dumps(metrics))
    return 0 if metrics['passed_sampled_polygons'] else 2 if metrics['geometry_valid_partial'] else 1


if __name__=='__main__':
    raise SystemExit(main())

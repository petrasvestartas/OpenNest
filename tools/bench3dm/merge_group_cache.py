"""Merge group-test cache shards: python -I tools/bench3dm/merge_group_cache.py <out.json> <in1.json> <in2.json> ..."""
import json, sys
from pathlib import Path
out = Path(sys.argv[1])
merged = {}
for f in sys.argv[2:]:
    p = Path(f)
    if not p.is_file():
        continue
    for k, v in json.loads(p.read_text()).items():
        if k not in merged or (v.get("feasible") and not merged[k].get("feasible")):
            merged[k] = v
out.write_text(json.dumps(merged))
print("merged", len(merged), "entries ->", out, "feasible", sum(1 for v in merged.values() if v.get("feasible")))

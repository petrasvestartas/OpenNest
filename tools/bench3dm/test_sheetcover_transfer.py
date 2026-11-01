import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from shapely.geometry import box

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_transfer import schedule_transfers, shape_descriptor, init_transfer
import sheetcover as sc


class TransferTests(unittest.TestCase):
    def test_similarity_ranking_and_mandatory_pair(self):
        polygons = {0: box(0, 0, 80, 5), 1: box(0, 0, 80, 5), 2: box(0, 0, 60, 8),
                    3: box(0, 0, 10, 10), 4: box(0, 0, 4, 4)}
        pk = SimpleNamespace(parts=dict.fromkeys(polygons), solve_poly=polygons,
                             solve_area={p: poly.area for p, poly in polygons.items()}, W=100, H=100, SA=10000)
        sheets = [{"placed": {1: (0, 0, 0), 4: (0, 0, 0)}}, {"placed": {2: (0, 0, 0), 3: (0, 0, 0)}}]
        jobs = schedule_transfers(pk, sheets, [0], limit=2)
        self.assertEqual(jobs[0]["transferred"], 1)
        self.assertEqual(jobs[0]["similarity"], 0)
        self.assertTrue({0, 1} <= set(jobs[0]["group"]))
        self.assertNotIn("1", jobs[0]["donor_subset"])
        self.assertEqual(set(jobs[0]["donor_subset"]), {"4"})
        self.assertEqual(shape_descriptor(polygons[0]), (80, 5, 400))

    def test_time_mode_opt_in_preserves_default(self):
        pk = SimpleNamespace(base={"iter_mode": 1, "time_budget_secs": 20})
        with patch.object(sc, "_init"), patch.object(sc, "_PK", pk):
            init_transfer("instance", "dll", 3600, 0, [])
            self.assertEqual(pk.base, {"iter_mode": 1, "time_budget_secs": 20})
            init_transfer("instance", "dll", 3600, 0, [], 165)
            self.assertEqual(pk.base, {"iter_mode": 0, "time_budget_secs": 165})


if __name__ == "__main__":
    unittest.main()

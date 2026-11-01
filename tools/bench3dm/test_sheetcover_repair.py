"""Repair scheduling and geometry acceptance tests without native solver calls."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from shapely.geometry import box, Polygon

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_repair import offer_key, schedule_repairs, validate_candidate


def packer(scale=1):
    polys = {0: box(0, 0, 4 * scale, 4 * scale), 1: box(0, 0, scale, scale),
             2: box(0, 0, 2 * scale, scale), 3: box(0, 0, 3 * scale, scale)}
    return SimpleNamespace(W=10 * scale, H=10 * scale, SA=100 * scale ** 2,
                           solve_poly=polys, solve_area={p: g.area for p, g in polys.items()},
                           parts=dict.fromkeys(polys))


class RepairTests(unittest.TestCase):
    def test_missing_parts_mandatory_existing_smalls_can_move(self):
        for scale in (1, 1000):
            pk = packer(scale)
            sheets = [{"placed": {0: (0, 0, 0), 1: (0, 0, 0)}}]
            attempted = set()
            jobs = schedule_repairs(pk, sheets, [2, 3], attempted, pairs=True)
            self.assertEqual(len(jobs), 3)
            for job in jobs:
                self.assertIn(0, job["group"])
                self.assertIn(1, job["fillers"])
                self.assertTrue(set(job["incoming"]) <= set(job["group"]))
                self.assertEqual(set(job["group"]) & set(job["fillers"]), set())
            self.assertEqual(schedule_repairs(pk, sheets, [2, 3], attempted, pairs=True), [])

    def test_round_robin_and_budget(self):
        sheets = [{"placed": {0: (0, 0, 0)}}, {"placed": {1: (0, 0, 0)}}]
        jobs = schedule_repairs(packer(), sheets, [2, 3], set(), limit=2)
        self.assertEqual(len(jobs), 2)
        self.assertEqual({tuple(j["incoming"]) for j in jobs}, {(2,), (3,)})
        self.assertEqual({next(iter((set(j["group"]) | set(j["fillers"])) - set(j["incoming"])))
                          for j in jobs}, {0, 1})
        self.assertEqual(offer_key([1, 0], [1]), offer_key([0, 1], [1]))

    def test_pairs_follow_singles_across_all_recipients(self):
        sheets = [{"placed": {0: (0, 0, 0)}}, {"placed": {1: (0, 0, 0)}}]
        jobs = schedule_repairs(packer(), sheets, [2, 3], set(), pairs=True, limit=4)
        self.assertEqual(len(jobs), 4)
        self.assertTrue(all(len(j["incoming"]) == 1 for j in jobs))
        self.assertEqual(len({offer_key(j["group"] + j["fillers"], j["group"]) for j in jobs}), 4)

    def test_explicit_pair_quota_then_singles(self):
        sheets = [{"placed": {0: (0, 0, 0)}}, {"placed": {1: (0, 0, 0)}}]
        jobs = schedule_repairs(packer(), sheets, [2, 3], set(), pairs=True, pair_quota=2, limit=4)
        self.assertEqual([len(j["incoming"]) for j in jobs], [2, 2, 1, 1])

    def test_geometry_and_mandatory_gate(self):
        pk = packer()
        self.assertTrue(validate_candidate(pk, {"placed": {"0": [0, 0, 0], "1": [4, 0, 0]}, "group": [0, 1]})[0])
        for candidate in [
            {"placed": {"0": [0, 0, 0], "1": [1, 1, 0]}},
            {"placed": {"0": [9, 0, 0]}},
            {"placed": {"0": [0, 0, 0]}, "group": [1]},
            {"placed": {"9": [0, 0, 0]}},
            {"placed": {"0": [float("nan"), 0, 0]}},
            {"placed": {}},
        ]:
            self.assertFalse(validate_candidate(pk, candidate)[0])

    def test_hole_seating_is_valid(self):
        pk = packer()
        pk.solve_poly[0] = Polygon([(0, 0), (5, 0), (5, 5), (0, 5)],
                                   holes=[[(1, 1), (4, 1), (4, 4), (1, 4)]])
        self.assertTrue(validate_candidate(pk, {"placed": {"0": [0, 0, 0], "1": [2, 2, 0]}})[0])


if __name__ == "__main__":
    unittest.main()

import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from shapely.geometry import box

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_warm import schedule_warm, warm_key, run_warm
import sheetcover as sc
import engines as eng


class WarmScheduleTests(unittest.TestCase):
    def test_required_cover_keeps_hard_part_when_small_parts_offer_more_area(self):
        pk = SimpleNamespace(SA=100, solve_area={0: 20, 1: 15, 2: 15})
        candidates = [{"placed": {0: (0, 0, 0)}},
                      {"placed": {1: (0, 0, 0), 2: (2, 0, 0)}}]
        _, missing = sc.exact_cover(candidates, pk.solve_area, 1, pk)
        self.assertEqual(missing, [0])
        chosen, missing = sc.exact_cover(candidates, pk.solve_area, 1, pk, required_ids=[0])
        self.assertEqual(missing, [1, 2])
        jobs = schedule_warm(pk, sc.assign_candidates(candidates, chosen), missing, set())
        self.assertEqual({job["incoming"] for job in jobs}, {1, 2})
        self.assertTrue(all(set(job["placed"]) == {0} for job in jobs))

    def test_all_incumbents_preserved_and_recipients_interleaved(self):
        pk = SimpleNamespace(SA=100, solve_area={0: 20, 1: 10, 2: 5, 3: 4})
        sheets = [{"placed": {0: (1, 2, 0)}}, {"placed": {1: (3, 4, 0)}}]
        jobs = schedule_warm(pk, sheets, [2, 3], set(), limit=2)
        self.assertEqual({j["incoming"] for j in jobs}, {2, 3})
        self.assertEqual({next(iter(j["placed"])) for j in jobs}, {0, 1})
        self.assertEqual(schedule_warm(pk, sheets, [2, 3], {j["key"] for j in schedule_warm(pk, sheets, [2, 3], set())}), [])

    def test_warm_state_key_tracks_geometry(self):
        self.assertEqual(warm_key({0: (1, 2, 0)}, 1), warm_key({"0": [1, 2, 0]}, 1))
        self.assertNotEqual(warm_key({0: (1, 2, 0)}, 1), warm_key({0: (1.1, 2, 0)}, 1))
        pk = SimpleNamespace(SA=10, solve_area={0: 6, 1: 5})
        self.assertEqual(schedule_warm(pk, [{"placed": {0: (0, 0, 0)}}], [1], set()), [])
        self.assertEqual(schedule_warm(pk, [], [1], set()), [])

    def test_only_complete_success_is_accepted(self):
        parts = {p: {"id": p, "name": str(p), "qty": 1, "outer": [(0, 0), (1, 0), (1, 1), (0, 1)],
                     "holes": [], "area_net": 1} for p in (0, 1)}
        pk = SimpleNamespace(instance={"sheet": {"w": 10, "h": 10}, "parts": list(parts.values())}, cfg={}, base={}, lib=None,
                             parts=parts, W=10, H=10, SA=100,
                             solve_area={0: 1, 1: 1}, solve_poly={0: box(0, 0, 1, 1), 1: box(0, 0, 1, 1)})
        job = {"placed": {0: (1, 1, 0)}, "incoming": 1, "key": "test", "gens": 500,
               "starts": 1, "seed": 100, "cap": 1, "tag": "test"}
        for rc, sheets, accepted in [(1, [0, 0], False), (0, [0, -1], False), (0, [0, 0], True)]:
            result = {"rc": rc, "sheet": sheets, "tx": [1, 3], "ty": [1, 1], "angle_rad": [0, 0],
                      "quality": {}, "cancelled": False}
            with patch.object(sc, "_PK", pk), patch.object(eng, "flatten_np", return_value=SimpleNamespace(copies=[(0, 0), (1, 0)])), \
                    patch.object(eng, "params_struct", return_value=None), patch.object(eng, "solve_np_warm", return_value=result, create=True):
                candidate = run_warm(job)
                self.assertEqual(candidate["validated"], accepted)


if __name__ == "__main__":
    unittest.main()

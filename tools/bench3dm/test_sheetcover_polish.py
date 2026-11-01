import copy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from shapely.geometry import box

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_polish import polish_candidate
from sheetcover_repair import validate_candidate


class PolishTests(unittest.TestCase):
    def test_boundary_and_overlap_keep_all_parts(self):
        pk = SimpleNamespace(W=10, H=10, parts={0: None, 1: None},
                             solve_poly={0: box(0, 0, 1, 1), 1: box(0, 0, 1, 1)})
        candidate = {"placed": {"0": [-.0001, 0, 0], "1": [.9998, 0, 0]}, "group": [0, 1]}
        original = copy.deepcopy(candidate)
        polished = polish_candidate(pk, candidate)
        self.assertIsNotNone(polished)
        self.assertTrue(validate_candidate(pk, polished, tolerance=1e-8)[0])
        self.assertEqual(set(polished["placed"]), set(candidate["placed"]))
        self.assertEqual(candidate, original)
        self.assertLess(polished["polish_max_translation_mm"], .01)

    def test_oversize_cannot_be_polished(self):
        pk = SimpleNamespace(W=1, H=1, parts={0: None}, solve_poly={0: box(0, 0, 2, 2)})
        self.assertIsNone(polish_candidate(pk, {"placed": {"0": [0, 0, 0]}}))

    def test_source_undershoot_is_corrected_without_relaxing_clearance(self):
        from validate_strict import validate_strict
        outer = [(0, 0), (10, 0), (10, 10), (0, 10)]
        parts = {p: {"id": p, "qty": 1, "name": str(p), "outer": outer, "holes": [], "area_net": 100}
                 for p in (0, 1)}
        inst = {"sheet": {"w": 100, "h": 100}, "parts": list(parts.values()), "clearance": 10, "margin": 15}
        pk = SimpleNamespace(instance=inst, W=100, H=100, parts=parts,
                             solve_poly={p: box(0, 0, 10, 10) for p in parts})
        candidate = {"placed": {"0": [15, 14.9997, 0], "1": [34.99998, 15, 0]}, "group": [0, 1]}
        self.assertTrue(validate_candidate(pk, candidate, check_source=False)[0])
        self.assertFalse(validate_candidate(pk, candidate)[0])
        polished = polish_candidate(pk, candidate)
        self.assertIsNotNone(polished)
        self.assertTrue(validate_candidate(pk, polished, tolerance=1e-8)[0])
        plc = {"instances": [{"part": int(p), "copy": 0, "sheet": 0, "tx": v[0], "ty": v[1], "angle_rad": v[2]}
                             for p, v in polished["placed"].items()]}
        strict = validate_strict(inst, plc)
        self.assertGreaterEqual(strict["min_pair_distance_mm_including_holes"], 10 - 1e-6)
        self.assertGreaterEqual(strict["min_margin_mm"], 15 - 1e-6)


if __name__ == "__main__":
    unittest.main()

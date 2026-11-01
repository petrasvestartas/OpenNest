import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_hybrid import validate_pair_sets, baseline_sheet_for_seed


class HybridTests(unittest.TestCase):
    def test_exact_disjoint_pair_required(self):
        left = {"placed": {"0": (), "2": ()}}
        right = {"placed": {"1": (), "3": ()}}
        self.assertTrue(validate_pair_sets(left, right, [0, 1, 2, 3]))
        self.assertFalse(validate_pair_sets(left, right, [0, 1, 2, 3, 4]))
        right["placed"]["0"] = ()
        self.assertFalse(validate_pair_sets(left, right, [0, 1, 2, 3]))

    def test_baseline_geometry_must_be_unchanged(self):
        baseline = {"instances": [{"part": 4, "sheet": 2, "tx": 1, "ty": 2, "angle_rad": .3}]}
        self.assertEqual(baseline_sheet_for_seed({"4": [1, 2, .3]}, baseline), 2)
        with self.assertRaises(ValueError):
            baseline_sheet_for_seed({"4": [1.1, 2, .3]}, baseline)


if __name__ == "__main__":
    unittest.main()

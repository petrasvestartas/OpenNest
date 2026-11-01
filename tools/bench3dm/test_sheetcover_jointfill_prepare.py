import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_jointfill_prepare import allocate


class JointFillPreparationTests(unittest.TestCase):
    def test_exact_partition_and_demonstrated_support(self):
        cores = [[10], [20]]
        areas = {10: .7, 20: .6, 30: .1, 40: .1}
        fillers, support, loads = allocate(cores, areas, [{10, 30}, {20, 40}])
        self.assertEqual(fillers, [[30], [40]])
        self.assertTrue(all(support))
        self.assertEqual(sorted(p for group in cores + fillers for p in group), sorted(areas))
        self.assertAlmostEqual(sum(loads), sum(areas.values()))

    def test_hypothetical_core_marked_and_duplicate_rejected(self):
        _, support, _ = allocate([[1, 2], [3]], {1: .3, 2: .3, 3: .5}, [{1}, {2}, {3}])
        self.assertFalse(support[0])
        with self.assertRaises(ValueError):
            allocate([[1], [1]], {1: .2}, [{1}])


if __name__ == "__main__":
    unittest.main()

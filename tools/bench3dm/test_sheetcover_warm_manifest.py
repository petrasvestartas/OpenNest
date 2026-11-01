import copy
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest
from shapely.geometry import box
sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_warm_manifest import validate_partition


class PartitionValidationTests(unittest.TestCase):
    def setUp(self):
        self.pk = SimpleNamespace(parts={p: {} for p in range(4)}, W=100, H=100,
            solve_poly={p: box(0, 0, 1, 1) for p in range(4)})
        self.job = {"source_sheet": 0, "placed": {0: [1, 1, 0]}, "incoming": 3,
            "preserved_parts": [0, 1, 2], "required_parts": [0, 1],
            "incumbent_partition": [{"placed": {0: [1, 1, 0]}},
                                    {"placed": {1: [1, 1, 0], 2: [3, 1, 0]}}]}
        self.result = {"rc": 0, "validated": True, "placed": {0: [1, 1, 0], 3: [3, 1, 0]}}

    def test_supported_transfer_preserved_with_new_incoming(self):
        sheets, why = validate_partition(self.pk, self.job, self.result)
        self.assertIsNotNone(sheets, why)
        self.assertEqual(set(sheets[0]["placed"]), {0, 3})
        self.assertEqual(set(sheets[1]["placed"]), {1, 2})

    def test_missing_duplicate_or_colliding_destination_cannot_be_accepted(self):
        for mutation in ("missing", "duplicate", "collision"):
            job = copy.deepcopy(self.job)
            if mutation == "missing":
                del job["incumbent_partition"][1]["placed"][2]
            elif mutation == "duplicate":
                job["incumbent_partition"][1]["placed"][0] = [5, 1, 0]
            else:
                job["incumbent_partition"][1]["placed"][2] = [1, 1, 0]
            with self.subTest(mutation=mutation):
                self.assertIsNone(validate_partition(self.pk, job, self.result)[0])


if __name__ == "__main__":
    unittest.main()

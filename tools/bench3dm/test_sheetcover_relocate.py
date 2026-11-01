import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_relocate import enumerate_relocations


class RelocationTests(unittest.TestCase):
    def setUp(self):
        self.plan = [{"group": [101, 202, 303]}, {"group": [404]}, {"group": [505]}]
        self.pool = [{"tag": "left", "placed": {"404": [0, 0, 0], "202": [0, 0, 0]}},
                     {"tag": "right", "placed": {"505": [0, 0, 0], "303": [0, 0, 0]}}]
        self.areas = {101: .2, 202: .1, 303: .1, 404: .2, 505: .2}

    def test_composition_preserves_partition_and_actual_destination_evidence(self):
        before = copy.deepcopy((self.plan, self.pool))
        result = enumerate_relocations(self.plan, self.pool, self.areas)
        best = result["plans"][0]
        self.assertEqual(best["cores"], [[101], [202, 404], [303, 505]])
        self.assertEqual(best["unsupported_core_indices"], [0])
        self.assertEqual(best["support_candidate_indices"], [None, 0, 1])
        for proposed in result["plans"]:
            parts = [pid for core in proposed["cores"] for pid in core]
            self.assertEqual(sorted(parts), sorted(self.areas))
            self.assertEqual(len(parts), len(set(parts)))
            self.assertEqual(len(proposed["moves"]), len({move["target"] for move in proposed["moves"]}))
        self.assertEqual((self.plan, self.pool), before)

    def test_no_supported_augmentation_yields_no_proposal(self):
        pool = [{"placed": {"404": [0, 0, 0]}}, {"placed": {"505": [0, 0, 0]}}]
        self.assertEqual(enumerate_relocations(self.plan, pool, self.areas)["plans"], [])

    def test_area_and_enumeration_budgets_are_enforced(self):
        self.assertEqual(enumerate_relocations(self.plan, self.pool, self.areas, area_cap=.29)["plans"], [])
        result = enumerate_relocations(self.plan, self.pool, self.areas, max_examined=1, max_plans=1)
        self.assertEqual(result["combinations_examined"], 1)
        self.assertTrue(result["truncated"])
        self.assertLessEqual(len(result["plans"]), 1)
        with self.assertRaises(ValueError):
            enumerate_relocations(self.plan, self.pool, self.areas, max_moves=0)

    def test_duplicate_input_parts_rejected(self):
        with self.assertRaises(ValueError):
            enumerate_relocations(self.plan + [{"group": [101]}], self.pool, self.areas)

    def test_cli_keeps_hypothetical_jobs_separate_and_preserves_budgets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            instance = {"solve_sheet": {"w": 100, "h": 100}, "parts": [
                {"id": pid, "outer": [[0, 0], [fraction * 100, 0],
                                        [fraction * 100, 100], [0, 100]]}
                for pid, fraction in self.areas.items()]}
            for name, value in (("instance", instance), ("pool", self.pool), ("plan", {"plan": self.plan})):
                (root / (name + ".json")).write_text(json.dumps(value))
            before = (root / "pool.json").read_bytes()
            command = [sys.executable, "-I", str(Path(__file__).with_name("sheetcover_relocate.py"))]
            for name in ("instance", "pool", "plan"):
                command += ["--" + name, str(root / (name + ".json"))]
            command += ["--out", str(root / "result"), "--max-plans", "1",
                        "--cap", "7", "--gens", "9", "--starts", "2"]
            result = subprocess.run(command, capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr)
            jobs = json.loads((root / "result" / "jobs.json").read_text())
            self.assertEqual(len(jobs), 1)
            self.assertEqual(jobs[0]["group"], [101])
            self.assertEqual((jobs[0]["cap"], jobs[0]["gens"], jobs[0]["starts"]), (7, 9, 2))
            self.assertNotIn("placed", jobs[0])
            self.assertEqual((root / "pool.json").read_bytes(), before)


if __name__ == "__main__":
    unittest.main()

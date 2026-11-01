"""Small CLI integration checks: proposal evidence is never a packed layout."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


HERE = Path(__file__).resolve().parent


class PlannerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.instance = {"solve_sheet": {"w": 100, "h": 100}, "parts": [
            {"id": i, "outer": [[0, 0], [width, 0], [width, 100], [0, 100]]}
            for i, width in [(1, 32), (2, 32), (3, 32), (4, 32), (5, 2)]]}
        self.pool = [{"placed": {str(i): [0, 0, 0]}} for i in range(1, 6)]
        self.cache = {"1,3": {"feasible": True}}
        self.write("instance", self.instance)
        self.write("pool", self.pool)
        self.write("cache", self.cache)

    def write(self, name, obj):
        (self.root / (name + ".json")).write_text(json.dumps(obj))

    def run_planner(self, script, *args):
        return subprocess.run([sys.executable, "-I", str(HERE / script),
            "--instance", str(self.root / "instance.json"),
            "--pool", str(self.root / "pool.json"),
            "--cache", str(self.root / "cache.json"),
            "--out", str(self.root / "result"), *map(str, args)],
            capture_output=True, text=True, timeout=40)

    def read(self, name):
        return json.loads((self.root / "result" / name).read_text())

    def test_coordinated_plans_cover_each_hard_part_once_despite_cache_failures(self):
        # Failed packing attempts are soft evidence, not geometric proofs.
        self.write("cache", {f"{a},{b}": {"feasible": False}
                             for a in range(1, 6) for b in range(a + 1, 6)})
        before = (self.root / "pool.json").read_bytes()
        result = self.run_planner("sheetcover_binplan.py", "--k", 2,
                                  "--plans", 2, "--cap", 7, "--gens", 9, "--starts", 2)
        self.assertEqual(result.returncode, 0, result.stderr)
        plans = self.read("plans.json")["plans"]
        self.assertEqual(len(plans), 2)
        for plan in plans:
            self.assertEqual(sorted(i for job in plan for i in job["group"]), list(range(1, 6)))
            for job in plan:
                self.assertFalse(job["already_supported"])
                self.assertTrue(.62 <= job["core_area"] <= .90)
                self.assertEqual((job["cap"], job["gens"], job["starts"]), (7, 9, 2))
        self.assertNotEqual([j["group"] for j in plans[0]], [j["group"] for j in plans[1]])
        self.assertEqual((self.root / "pool.json").read_bytes(), before)

    def test_infeasible_bin_area_produces_no_jobs(self):
        # Four .20-area plates cannot satisfy two bins' .62 minimum.
        for part in self.instance["parts"][:4]:
            part["outer"][1][0] = part["outer"][2][0] = 20
        self.write("instance", self.instance)
        result = self.run_planner("sheetcover_binplan.py", "--k", 2, "--plans", 1)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.read("jobs.json"), [])
        self.assertEqual(self.read("plans.json")["plans"], [])

    def test_pricing_certificate_keeps_original_and_hypothetical_columns_distinct(self):
        before = (self.root / "pool.json").read_bytes()
        result = self.run_planner("sheetcover_pricing.py", "--jobs", 3,
            "--stop-lp", 3, "--integer-k", 3, "--cap", 7, "--gens", 9, "--starts", 2)
        self.assertEqual(result.returncode, 0, result.stderr)
        jobs = self.read("jobs.json")
        cert = self.read("pricing-certificate.json")
        self.assertEqual(len(jobs), 1)
        self.assertEqual(jobs[0]["group"], [1, 3, 5])
        self.assertEqual((jobs[0]["cap"], jobs[0]["gens"], jobs[0]["starts"]), (7, 9, 2))
        self.assertAlmostEqual(cert["original"]["fractional_min_sheets"], 5)
        self.assertIn(len(self.pool), cert["hypothetical_integer_plan_columns"])
        self.assertIn("hypothetical", cert["warning"])
        self.assertEqual((self.root / "pool.json").read_bytes(), before)

    def test_pricing_uncovered_hard_part_fails_without_emitting_certificate(self):
        self.write("pool", self.pool[:-1])
        result = self.run_planner("sheetcover_pricing.py", "--jobs", 1)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("infeasible", result.stderr.lower())
        self.assertFalse((self.root / "result" / "pricing-certificate.json").exists())

    def test_nonpositive_budgets_rejected_before_writing(self):
        for script, flag in [("sheetcover_binplan.py", "--plans"),
                             ("sheetcover_pricing.py", "--jobs")]:
            with self.subTest(script=script):
                result = self.run_planner(script, flag, 0)
                self.assertEqual(result.returncode, 2)
                self.assertIn("must be positive", result.stderr)
                self.assertFalse((self.root / "result").exists())


if __name__ == "__main__":
    unittest.main()

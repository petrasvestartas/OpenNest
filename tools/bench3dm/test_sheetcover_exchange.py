import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_exchange import schedule_exchanges, pair_preserved, exchange_key


class ExchangeTests(unittest.TestCase):
    def test_jobs_preserve_union_and_add_incoming(self):
        pk = SimpleNamespace(SA=100, solve_area={0: 40, 1: 15, 2: 30, 3: 10, 4: 10, 5: 5})
        sheets = [{"placed": {0: (), 1: ()}}, {"placed": {2: (), 3: ()}}]
        jobs = schedule_exchanges(pk, sheets, [4, 5], set(), limit=10)
        self.assertTrue(jobs)
        self.assertLessEqual(len(jobs), 10)
        self.assertEqual(len({j["key"] for j in jobs}), len(jobs))
        for job in jobs:
            cands = [{"placed": dict.fromkeys(job[side])} for side in ("left", "right")]
            self.assertTrue(pair_preserved(job, cands))
            self.assertLessEqual(sum(pk.solve_area[p] for p in job["left"]), pk.SA)
            self.assertLessEqual(sum(pk.solve_area[p] for p in job["right"]), pk.SA)
            del cands[0]["placed"][job["left"][0]]
            self.assertFalse(pair_preserved(job, cands))

    def test_key_is_unordered_pair_and_attempts_not_repeated(self):
        self.assertEqual(exchange_key([1, 0], [2]), exchange_key([2], [0, 1]))
        pk = SimpleNamespace(SA=100, solve_area={0: 40, 1: 40, 2: 5})
        sheets = [{"placed": {0: ()}}, {"placed": {1: ()}}]
        jobs = schedule_exchanges(pk, sheets, [2], set(), limit=100)
        self.assertEqual(schedule_exchanges(pk, sheets, [2], {j["key"] for j in jobs}), [])
        self.assertFalse(pair_preserved({"original": [0, 1], "incoming": 2},
                                        [{"placed": {0: (), 2: ()}}, {"placed": {0: (), 1: ()}}]))


if __name__ == "__main__":
    unittest.main()

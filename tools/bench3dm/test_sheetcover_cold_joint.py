import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_cold_joint import schedule_cold_joint


class ColdJointTests(unittest.TestCase):
    def test_supported_ownership_and_two_distinct_recipients(self):
        sheets = [{'placed': {0: [0, 0, 0], 3: [4, 0, 0]}},
                  {'placed': {1: [0, 0, 0]}}, {'placed': {2: [0, 0, 0]}}]
        pool = [{'tag': 'actual', 'placed': {1: [0, 0, 0], 3: [7, 0, 0]}}]
        jobs = schedule_cold_joint(sheets, pool, [4], [0, 1, 2],
                                  {0: 10, 1: 30, 2: 20, 3: 15, 4: 5}, 100)
        self.assertEqual([j['source_sheet'] for j in jobs], [0, 2])
        self.assertEqual(jobs[0]['group'], [0, 4])
        self.assertEqual(jobs[0]['ownership_change']['moved_part'], 3)
        for job in jobs:
            actual = job['incumbent_partition']
            flat = [p for s in actual for p in s['placed']]
            self.assertEqual(sorted(flat), [0, 1, 2, 3])
            self.assertEqual(job['fillers'], [])
            self.assertEqual(set(job['group']), set(actual[job['source_sheet']]['placed']) | {4})
        self.assertEqual(jobs[0]['incumbent_partition'][1]['placed'][3], [7, 0, 0])

    def test_impossible_area_emits_no_job(self):
        self.assertEqual(schedule_cold_joint([{'placed': {0: [0, 0, 0]}}], [], [1], [0],
                                             {0: 70, 1: 40}, 100), [])


if __name__ == '__main__':
    unittest.main()

import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_ownership import schedule_ownership


class OwnershipTests(unittest.TestCase):
    def setUp(self):
        self.sheets = [{'placed': {0: [0, 0, 0], 2: [3, 0, 0]}},
                       {'placed': {1: [0, 0, 0]}}]
        self.pool = [{'tag': 'supported', 'placed': {'1': [0, 0, 0], '2': [5, 0, 0]}}]
        self.areas = {0: 20, 1: 20, 2: 10, 3: 12, 4: 18}

    def call(self, **kw):
        return schedule_ownership(self.sheets, self.pool, [3, 4], [0, 1], self.areas, 100, **kw)

    def test_partition_and_actual_destination_transform(self):
        before = copy.deepcopy((self.sheets, self.pool))
        jobs = self.call()
        self.assertEqual([j['incoming'] for j in jobs], [3, 4])
        for job in jobs:
            partition = job['incumbent_partition']
            flat = [p for s in partition for p in s['placed']]
            self.assertEqual(sorted(flat), [0, 1, 2])
            self.assertEqual(partition[1]['placed'][2], [5, 0, 0])
            self.assertEqual(set(job['placed']), {0})
        self.assertEqual((self.sheets, self.pool), before)

    def test_no_actual_support_no_job(self):
        self.pool[0]['placed'].pop('2')
        self.assertEqual(self.call(), [])

    def test_hard_part_not_relocated(self):
        self.assertEqual(schedule_ownership(self.sheets, self.pool, [3], [0, 1, 2], self.areas, 100), [])

    def test_limits_and_area(self):
        jobs = self.call(limit=1, seed=123, gens=500, starts=2, cap=10)
        self.assertEqual(len(jobs), 1)
        self.assertEqual([jobs[0][k] for k in ('seed', 'gens', 'starts', 'cap')], [123, 500, 2, 10])
        self.areas[3] = 90
        self.assertEqual([j['incoming'] for j in self.call()], [4])
        with self.assertRaises(ValueError):
            self.call(limit=0)

    def test_duplicate_incumbent_rejected(self):
        self.sheets[1]['placed'][2] = [10, 0, 0]
        with self.assertRaises(ValueError):
            self.call()


if __name__ == '__main__':
    unittest.main()

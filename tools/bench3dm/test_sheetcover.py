"""Small combinatorial regressions; no native engine or customer geometry needed."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover import exact_cover, assign_candidates, select_groups


def candidate(*parts):
    return {"placed": {str(p): [p, 0, 0] for p in parts}, "group": list(parts), "area": 999}


class UnionCoverTests(unittest.TestCase):
    def test_shared_part_does_not_block_complete_cover(self):
        # Two individually feasible sheets sharing part 1 cover all three parts.
        # The previous disjoint-column ILP could select only one of these sheets.
        for width, height, scale in [(10, 10, 1), (30, 8, 1), (10000, 10000, 1000)]:
            with self.subTest(width=width, height=height, scale=scale):
                pk = SimpleNamespace(SA=width * height, solve_area={p: scale * scale for p in range(3)})
                cs = [candidate(0, 1), candidate(1, 2)]
                chosen, missing = exact_cover(cs, list(pk.solve_area), 2, pk)
                self.assertEqual(missing, [])
                sheets = assign_candidates(cs, chosen)
                ids = [p for s in sheets for p in s["placed"]]
                self.assertEqual(sorted(ids), [0, 1, 2])
                self.assertEqual(len(ids), len(set(ids)))
                self.assertEqual(sum(pk.solve_area[p] for p in ids), 3 * scale * scale)
                self.assertEqual(sheets, assign_candidates(cs, reversed(chosen)))

    def test_area_comes_from_instance(self):
        pk = SimpleNamespace(SA=100, solve_area={0: 10, 1: 1, 2: 1})
        cs = [candidate(0), candidate(1, 2)]
        chosen, missing = exact_cover(cs, [0, 1, 2], 1, pk)
        self.assertEqual(chosen, [0])
        self.assertEqual(missing, [1, 2])

    def test_empty_inputs(self):
        pk = SimpleNamespace(SA=100, solve_area={0: 1})
        self.assertEqual(exact_cover([], [0], 3, pk), ([], [0]))
        self.assertEqual(exact_cover([], [], 3, pk), ([], []))
        self.assertEqual(exact_cover([candidate(0)], [0], 0, pk), ([], [0]))

    def test_count_first_prefers_more_small_parts_with_required_hard_and_area_tiebreak(self):
        for scale in (1, 1000000):
            pk = SimpleNamespace(SA=100, solve_area={0: 10 * scale, 1: 50 * scale,
                2: 2 * scale, 3: scale, 4: .5 * scale})
            cs = [candidate(0, 1), candidate(0, 2, 3), candidate(0, 3, 4)]
            self.assertEqual(exact_cover(cs, pk.solve_area, 1, pk, required_ids={0})[0], [0])
            chosen, missing = exact_cover(cs, pk.solve_area, 1, pk, required_ids={0}, objective="count")
            self.assertEqual(chosen, [1])
            self.assertEqual(missing, [1, 4])

    def test_required_coverage_trades_area_for_hard_parts(self):
        pk = SimpleNamespace(SA=100, solve_area={0: 10, 1: 2, 2: 1})
        cs = [candidate(0), candidate(1, 2)]
        self.assertEqual(exact_cover(cs, [0, 1, 2], 1, pk), ([0], [1, 2]))
        self.assertEqual(exact_cover(cs, [0, 1, 2], 1, pk, required_ids={1}), ([1], [0]))
        self.assertEqual(exact_cover(cs, [0, 1, 2], 1, pk, required_ids={0, 1}), (None, [0, 1, 2]))
        self.assertEqual(exact_cover([], [0], 1, pk, required_ids={0}), (None, [0]))
        with self.assertRaisesRegex(ValueError, "unknown part"):
            exact_cover(cs, [0, 1, 2], 1, pk, required_ids={9})

    def test_invalid_input(self):
        pk = SimpleNamespace(SA=100, solve_area={0: 1})
        with self.assertRaisesRegex(ValueError, "unknown part"):
            exact_cover([candidate(4)], [0], 3, pk)
        with self.assertRaisesRegex(ValueError, "nonnegative"):
            exact_cover([], [0], -1, pk)

    def test_duplicate_candidates_and_empty_assignment(self):
        cs = [candidate(0, 1), candidate(0, 1), candidate(1, 2)]
        sheets = assign_candidates(cs, [2, 1, 0, 0])
        self.assertEqual(len(sheets), 2)
        self.assertEqual(sheets[0]["group"], [0, 1])
        self.assertEqual(sheets[1]["group"], [2])
        self.assertEqual(assign_candidates(cs, []), [])

    def test_capped_groups_prioritise_coverage(self):
        groups = [(10, (0, 1)), (9, (0, 2)), (8, (1, 2)), (1, (2, 3))]
        selected, counts = select_groups(groups, [0, 1, 2, 3], 3, 2)
        self.assertEqual(len(selected), 2)
        self.assertTrue(all(counts.values()))
        self.assertEqual(counts, {p: sum(p in g for g in selected) for p in counts})
        self.assertEqual(select_groups([], [0], 3, 2), ([], {0: 0}))


if __name__ == "__main__":
    unittest.main()

import copy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from shapely.geometry import Polygon, box
sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheetcover_frozen import accept_additions


def packer(polys, clearance=0):
    parts = {i: dict(id=i, qty=1, name=str(i), outer=list(p.exterior.coords)[:-1],
                     holes=[list(r.coords)[:-1] for r in p.interiors], area_net=p.area)
             for i, p in polys.items()}
    inst = dict(parts=list(parts.values()), sheet=dict(w=120, h=120), clearance=clearance, margin=0)
    return SimpleNamespace(parts=parts, instance=inst, W=120, H=120, solve_poly=polys)


class FrozenTests(unittest.TestCase):
    def test_incumbent_overwrite_rejected_and_valid_addition_retained(self):
        pk = packer({0: box(0, 0, 10, 10), 1: box(0, 0, 10, 10)})
        seed = {0: (5, 5, 0)}
        original = copy.deepcopy(seed)
        got, accepted, rejected = accept_additions(pk, seed, {0: (80, 80, 0), 1: (25, 5, 0)}, {1})
        self.assertEqual(seed, original)
        self.assertEqual(got[0], original[0])
        self.assertEqual(accepted, [1])
        self.assertEqual(rejected, [[0, 'incumbent overwrite']])

    def test_source_clearance_gate_rejects_nonoverlapping_solve_proposal(self):
        pk = packer({0: box(0, 0, 10, 10), 1: box(0, 0, 10, 10)}, clearance=10)
        got, accepted, rejected = accept_additions(pk, {0: (10, 10, 0)}, {1: (25, 10, 0)}, {1})
        self.assertEqual(set(got), {0})
        self.assertFalse(accepted)
        self.assertIn('source geometry', rejected[0][1])

    def test_hole_occupant_collision_rejected_empty_cavity_space_allowed(self):
        frame = Polygon([(0,0),(100,0),(100,100),(0,100)], [[(20,20),(20,80),(80,80),(80,20)]])
        pk = packer({0: frame, 1: box(0,0,10,10), 2: box(0,0,10,10)})
        seed = {0: (0,0,0), 1: (30,30,0)}
        got, accepted, _ = accept_additions(pk, seed, {2: (31,31,0)}, {2})
        self.assertEqual(got, seed)
        self.assertFalse(accepted)
        got, accepted, _ = accept_additions(pk, seed, {2: (50,50,0)}, {2})
        self.assertEqual(accepted, [2])
        self.assertEqual(got[1], seed[1])

    def test_unrequested_and_illegal_rotation_rejected(self):
        pk = packer({0: box(0,0,10,10), 1: box(0,0,10,10)})
        pk.parts[1]['rotations'] = 4
        self.assertFalse(accept_additions(pk, {0:(5,5,0)}, {1:(40,40,.1)}, {1})[1])
        self.assertFalse(accept_additions(pk, {0:(5,5,0)}, {1:(40,40,0)}, set())[1])


if __name__ == '__main__':
    unittest.main()

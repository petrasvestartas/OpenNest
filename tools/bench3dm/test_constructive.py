import copy
import ctypes
import math
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parent))
import constructive as nc


def part(pid, w, h, qty=1, holes=None):
    outer = [[0, 0], [w, 0], [w, h], [0, h]]
    return {"id": pid, "name": str(pid), "qty": qty, "outer": outer, "holes": holes or [],
            "solve": {"outer": outer, "holes": holes or []}, "area_net": w*h,
            "local_origin": [0, 0], "rotations": 1}


def instance(parts, w=20, h=10):
    return {"parts": parts, "sheet": {"w": w, "h": h, "count": 2},
            "solve_sheet": {"w": w, "h": h, "inset": 0}, "margin": 0, "clearance": 0}


class AdapterTests(unittest.TestCase):
    def test_abi_and_quantity_hole_angle_arrays(self):
        self.assertEqual([ctypes.sizeof(c) for c in (nc.NcParams, nc.NcPlacement, nc.NcStats)], [64, 40, 48])
        p = part(42, 10, 10, 2, [[[2, 2], [8, 2], [8, 8], [2, 8]]])
        p.update(allowed_angles=[.25, .75], mirror_allowed=True)
        data = nc.flatten(instance([p]))
        self.assertEqual(list(data["quantities"]), [2])
        self.assertEqual(list(data["ring_holes"]), [0, 1])
        self.assertEqual(list(data["angles"]), [.25, .75])
        self.assertEqual(list(data["mirror_flags"]), [1])

    def test_native_x_mirror_export_matches_source_y_mirror(self):
        inst = instance([part(2, 4, 2)])
        data = nc.flatten(inst)
        # Native local X reflection +tx4 gives rectangle [0,4]x[0,2].
        doc = {"instances": [{"part": 2, "copy": 0, "sheet": 0, "tx": 4, "ty": 0,
            "angle_rad": math.pi, "mirrored": True}], "n_sheets": 1}
        self.assertTrue(nc.validate(inst, doc, data, 1)["passed_sampled_polygons"])

    def test_missing_offsets_and_bad_angles_rejected(self):
        inst = instance([part(2, 4, 2)])
        inst["clearance"] = 10
        del inst["parts"][0]["solve"]
        with self.assertRaisesRegex(ValueError, "pre-offset"):
            nc.flatten(inst)
        inst = instance([part(2, 4, 2)])
        inst["parts"][0]["allowed_angles"] = [float("nan")]
        with self.assertRaisesRegex(ValueError, "finite"):
            nc.flatten(inst)


@unittest.skipUnless(nc.DEFAULT_DLL.exists(), "build independent constructive DLL first")
class NativeIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.lib = nc.load_dll()

    def run_case(self, inst, **options):
        return nc.solve(self.lib, inst, seconds=2, max_sheets=1, beam=4, branch=3,
                        positions=24, restarts=1, workers=1, scan_step=1, **options)

    def test_quantities_and_fixed_angles_complete_source_valid(self):
        doc, strict = self.run_case(instance([part(11, 4, 4, 3)]))
        self.assertEqual(doc["engine_report"]["rc"], 0)
        self.assertEqual({row["copy"] for row in doc["instances"]}, {0, 1, 2})
        self.assertTrue(strict["passed_sampled_polygons"])
        self.assertTrue(all(abs(row["native_angle_rad"]) < 1e-9 for row in doc["instances"]))

    def test_nonzero_explicit_orientation_and_origin(self):
        p = part(7, 2, 4)
        p["allowed_angles"] = [math.pi/2]
        doc, strict = self.run_case(instance([p], 4, 2))
        self.assertTrue(strict["passed_sampled_polygons"], strict)
        self.assertAlmostEqual(doc["instances"][0]["native_angle_rad"], math.pi/2)

    def test_part_in_hole_and_valid_partial_distinguished(self):
        frame = part(1, 10, 10, holes=[[[2, 2], [8, 2], [8, 8], [2, 8]]])
        _, strict = self.run_case(instance([frame, part(2, 3, 3)], 10, 10))
        self.assertTrue(strict["passed_sampled_polygons"], strict)
        doc, strict = self.run_case(instance([part(1, 100, 100)], 10, 10))
        self.assertEqual(doc["engine_report"]["rc"], 1)
        self.assertFalse(strict["complete"])
        self.assertTrue(strict["geometry_valid_partial"])


if __name__ == "__main__":
    unittest.main()

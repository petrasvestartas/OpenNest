import sys
import unittest
from pathlib import Path
from shapely.geometry import Polygon

sys.path.insert(0, str(Path(__file__).resolve().parent))
from validate_strict import validate_strict


def part(pid, outer, holes=()):
    return {"id": pid, "name": str(pid), "qty": 1, "outer": outer, "holes": list(holes),
            "area_net": Polygon(outer, holes).area}


def placement(pid, x, y):
    return {"part": pid, "copy": 0, "sheet": 0, "tx": x, "ty": y, "angle_rad": 0}


class StrictValidationTests(unittest.TestCase):
    def test_clearance_that_legacy_slack_allows_fails(self):
        square = [(0, 0), (10, 0), (10, 10), (0, 10)]
        inst = {"sheet": {"w": 100, "h": 100}, "clearance": 10, "margin": 15,
                "parts": [part(0, square), part(1, square)]}
        plc = {"instances": [placement(0, 15, 15), placement(1, 34.98, 15)]}
        self.assertFalse(validate_strict(inst, plc)["passed_sampled_polygons"])
        plc["instances"][1]["tx"] = 35
        self.assertTrue(validate_strict(inst, plc)["passed_sampled_polygons"])
        conditional = validate_strict(inst, plc, curve_error_bound=.005)
        self.assertFalse(conditional["conditional_curve_bounds_passed"])
        plc["instances"][0]["ty"] = 14.99
        self.assertFalse(validate_strict(inst, plc)["passed_sampled_polygons"])

    def test_hole_nesting_clearance_is_checked(self):
        outer = [(0, 0), (80, 0), (80, 80), (0, 80)]
        hole = [(10, 10), (70, 10), (70, 70), (10, 70)]
        small = [(0, 0), (10, 0), (10, 10), (0, 10)]
        inst = {"sheet": {"w": 120, "h": 120}, "clearance": 10, "margin": 15,
                "parts": [part(0, outer, [hole]), part(1, small)]}
        plc = {"instances": [placement(0, 15, 15), placement(1, 35, 35)]}
        self.assertTrue(validate_strict(inst, plc)["passed_sampled_polygons"])
        plc["instances"][1]["tx"] = 34.98
        result = validate_strict(inst, plc)
        self.assertFalse(result["passed_sampled_polygons"])
        self.assertTrue(result["clearance_violations"][0]["hole_pair"])


if __name__ == "__main__":
    unittest.main()

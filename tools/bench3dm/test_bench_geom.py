"""Plain-assert tests for bench_geom / rhino_sampling.   python -I tools/bench3dm/test_bench_geom.py"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench_geom as bg  # noqa: E402


def close(a, b, tol=1e-6):
    return abs(a - b) <= tol


def test_offset_part():
    outer = [(0, 0), (100, 0), (100, 50), (0, 50)]
    hole = [(20, 10), (20, 40), (80, 40), (80, 10)]  # CW
    # mitre (the old behaviour, kept for A/B): exact rectangles
    o, hs, nd = bg.offset_part(outer, [hole], 5.0, join="mitre")
    x0, y0, x1, y1 = bg.ring_bbox(o)
    assert close(x0, -5) and close(y0, -5) and close(x1, 105) and close(y1, 55), (x0, y0, x1, y1)
    assert bg.signed_area(o) > 0 and nd == 0 and len(hs) == 1
    hx0, hy0, hx1, hy1 = bg.ring_bbox(hs[0])
    assert close(hx0, 25) and close(hy0, 15) and close(hx1, 75) and close(hy1, 35), (hx0, hy0, hx1, hy1)
    assert bg.signed_area(hs[0]) < 0
    assert close(abs(bg.signed_area(o)), 110 * 60) and close(abs(bg.signed_area(hs[0])), 50 * 20)
    # shrink -5: outer 90x40, hole 70x40
    o2, hs2, _ = bg.offset_part(outer, [hole], -5.0, join="mitre")
    assert close(abs(bg.signed_area(o2)), 90 * 40) and close(abs(bg.signed_area(hs2[0])), 70 * 40)
    # a hole consumed by the shrink is dropped
    _, hs3, nd3 = bg.offset_part(outer, [[(40, 20), (40, 26), (46, 26), (46, 20)]], 5.0)
    assert nd3 == 1 and hs3 == []


def test_offset_part_round():
    """Default join is ROUND = the exact clearance region (Minkowski sum with a disk of radius d)."""
    outer = [(0, 0), (100, 0), (100, 50), (0, 50)]
    hole = [(20, 10), (20, 40), (80, 40), (80, 10)]  # CW
    d = 5.0
    sag = bg.round_sagitta_target(d)
    o, hs, nd = bg.offset_part(outer, [hole], d)
    assert bg.signed_area(o) > 0 and nd == 0 and len(hs) == 1 and bg.signed_area(hs[0]) < 0
    x0, y0, x1, y1 = bg.ring_bbox(o)
    assert close(x0, -5) and close(y0, -5) and close(x1, 105) and close(y1, 55), (x0, y0, x1, y1)
    # rounded corners: area = rect + 2*d*(w+h) + pi*d^2, minus at most the chord deficit
    exact = 100 * 50 + 2 * d * (100 + 50) + math.pi * d * d
    a = abs(bg.signed_area(o))
    assert a <= exact + 1e-6 and exact - a < 2 * math.pi * d * sag, (a, exact)
    # every vertex sits ON the true offset (distance d from the rectangle), every chord within the budget
    rect = bg.solid(outer)
    ring = bg.LinearRing(o)
    for i in range(len(o)):
        p, q = o[i], o[(i + 1) % len(o)]
        for t in (0.0, 0.25, 0.5, 0.75):
            pt = bg.Point(p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1]))
            dist = rect.exterior.distance(pt)
            assert d - sag - 1e-9 <= dist <= d + 1e-9, (pt, dist)
    assert ring.is_valid
    # a convex hole shrinks with SHARP corners (the inward offset of a convex corner has no fillet)
    hx0, hy0, hx1, hy1 = bg.ring_bbox(hs[0])
    assert close(hx0, 25) and close(hy0, 15) and close(hx1, 75) and close(hy1, 35)
    assert close(abs(bg.signed_area(hs[0])), 50 * 20)
    # round is always INSIDE mitre (never a bigger nesting outline), and strictly smaller at corners
    om, _, _ = bg.offset_part(outer, [hole], d, join="mitre")
    assert bg.solid(om).buffer(1e-9).contains(bg.solid(o)) and bg.solid(om).area - bg.solid(o).area > 20.0
    # shrink -5: the convex outer stays a sharp 90x40, the hole GROWS with rounded corners
    o2, hs2, _ = bg.offset_part(outer, [hole], -d)
    assert close(abs(bg.signed_area(o2)), 90 * 40)
    exact_h = 60 * 30 + 2 * d * (60 + 30) + math.pi * d * d
    ah = abs(bg.signed_area(hs2[0]))
    assert ah <= exact_h + 1e-6 and exact_h - ah < 2 * math.pi * d * sag, (ah, exact_h)


def test_round_rule():
    # chord budget: 0.4% of the offset, clamped to [0.01, 0.05] mm (polygon_offset.round_sagitta_target)
    assert close(bg.round_sagitta_target(5.0), 0.02) and close(bg.round_sagitta_target(-5.0), 0.02)
    assert close(bg.round_sagitta_target(0.5), 0.01) and close(bg.round_sagitta_target(100.0), 0.05)
    assert close(bg.round_sagitta_target(0.005, units_per_mm=0.001), 2e-5, 1e-12)  # 5 mm in a metre model
    # quad_segs: the worst fillet chord (1.5 nominal steps) stays within the budget
    for d in (0.5, 1.0, 5.0, 20.0, 100.0):
        n = bg.round_quad_segs(d)
        step = 0.5 * math.pi / n
        assert d * (1 - math.cos(1.5 * step / 2)) <= bg.round_sagitta_target(d) + 1e-12, (d, n)
    assert bg.round_quad_segs(5.0) == 14
    # measured on a sharp spike (the fillet spans almost 180 deg): every chord within budget
    spike = [(0, 0), (300, 0), (10, 40)]
    o = bg.offset_ring(spike, 5.0)
    poly = bg.solid(spike)
    worst = 0.0
    for i in range(len(o)):
        p, q = o[i], o[(i + 1) % len(o)]
        for t in (0.1, 0.3, 0.5, 0.7, 0.9):
            pt = bg.Point(p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1]))
            worst = max(worst, 5.0 - poly.exterior.distance(pt))
    assert -1e-9 <= worst <= bg.round_sagitta_target(5.0), worst
    try:
        bg.offset_ring(spike, 5.0, join="bevel")
        raise AssertionError("expected ValueError")
    except ValueError:
        pass


def test_classification():
    sq = lambda c, h: [(c - h, c - h), (c + h, c - h), (c + h, c + h), (c - h, c + h)]
    rings = [sq(0, 50), sq(0, 30), sq(0, 10), sq(200, 20)]  # outer, hole, island, separate part
    par = bg.classify_rings(rings, "parity")
    got = {g["outer"]: sorted(g["holes"]) for g in par}
    assert got == {0: [1], 2: [], 3: []}, got
    ig = bg.classify_rings(rings, "identify_groups")
    got2 = {g["outer"]: sorted(g["holes"]) for g in ig}
    assert got2 == {0: [1, 2], 3: []}, got2  # plugin behaviour: islands are swallowed as holes
    assert len(bg.classification_disagreements(rings)) > 0
    assert bg.classification_disagreements(rings[:2] + rings[3:]) == []


def test_rigid_match():
    ring = [(0, 0), (120, 0), (120, 30), (60, 30), (40, 70), (0, 70)]  # asymmetric
    ang, tx, ty = 0.7, 1234.5, -987.25
    moved = bg.place(ring, ang, tx, ty)
    shifted = moved[3:] + moved[:3]  # start-index shift
    m = bg.rigid_match(ring, shifted)
    assert m is not None and not m[3], m
    a2 = math.atan2(math.sin(m[0] - ang), math.cos(m[0] - ang))
    assert abs(a2) < 1e-6 and close(m[1], tx, 1e-4) and close(m[2], ty, 1e-4), m
    # resampled reference (extra midpoints) still matches
    res = []
    for i in range(len(shifted)):
        a, b = shifted[i], shifted[(i + 1) % len(shifted)]
        res += [a, ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)]
    m2 = bg.rigid_match(ring, res)
    assert m2 is not None and abs(math.atan2(math.sin(m2[0] - ang), math.cos(m2[0] - ang))) < 1e-6
    # mirror detected
    mir = bg.place(ring, 0.3, 10, 20, mirrored=True)
    m3 = bg.rigid_match(ring, mir)
    assert m3 is not None and m3[3], m3
    assert bg.rigid_match(ring, mir, allow_mirror=False) is None
    # placing with the returned transform reproduces the reference
    back = bg.place(ring, *m3)
    assert bg.solid(back).symmetric_difference(bg.solid(mir)).area < 1e-6


def test_polyline_roundtrip():
    import rhino3dm as r3
    import rhino_sampling as rs
    pts = [(0, 0), (300, 0), (300, 100), (150, 180.5), (0, 100)]
    pc = r3.PolylineCurve([r3.Point3d(x, y, 0) for x, y in pts + [pts[0]]])
    ring, seeds, segs = rs.curve_to_ring(pc)
    assert ring == [(float(x), float(y)) for x, y in pts], ring
    assert seeds == []
    assert rs.role_allowance(ring, segs, seeds, True) == ring  # no curved segment -> untouched
    # a full circle samples to >= 30 chords on the circle and gets an outward allowance
    circ = r3.ArcCurve.CreateFromCircle(r3.Circle(r3.Point3d(0, 0, 0), 100.0))
    cr, cs, cseg = rs.curve_to_ring(circ)
    assert len(cr) >= 30 and all(close(math.hypot(x, y), 100.0, 1e-6) for x, y in cr)
    grown = rs.role_allowance(bg.ensure_ccw(cr), cseg, cs, True)
    assert min(math.hypot(x, y) for x, y in grown) >= 100.0 * math.cos(math.pi / len(cr)) - 1e-9
    assert bg.solid(grown).buffer(1e-6).contains(bg.solid([(100 * math.cos(t / 720 * 2 * math.pi),
                                                             100 * math.sin(t / 720 * 2 * math.pi))
                                                            for t in range(720)]))
    assert close(abs(rs.curve_area(circ)), math.pi * 1e4, 1e-6)


def test_drop_closing_duplicate():
    assert bg.drop_closing_duplicate([(0, 0), (1, 0), (1, 1), (0, 0)]) == [(0, 0), (1, 0), (1, 1)]
    assert bg.drop_closing_duplicate([(0, 0), (1, 0), (1, 1)]) == [(0, 0), (1, 0), (1, 1)]
    assert bg.drop_closing_duplicate([(0, 0), (1, 0), (1, 1), (0, 1e-12)]) == [(0, 0), (1, 0), (1, 1)]
    assert bg.dedupe_consecutive([(0, 0), (0, 0), (1, 0), (1, 1), (0, 0)]) == [(0, 0), (1, 0), (1, 1)]


def test_inset_sheet():
    r = bg.inset_sheet(6000, 2000, 10)
    assert r == [(0.0, 0.0), (5980.0, 0.0), (5980.0, 1980.0), (0.0, 1980.0)]
    assert bg.signed_area(r) > 0
    try:
        bg.inset_sheet(10, 10, 5)
        raise AssertionError("expected ValueError")
    except ValueError:
        pass


def test_signature():
    a = [(0, 0), (100, 0), (100, 40), (0, 40)]
    b = bg.place(a, 1.1, 50, 60)
    assert bg.signatures_equal(bg.shape_signature(a), bg.shape_signature(b))
    assert not bg.signatures_equal(bg.shape_signature(a), bg.shape_signature([(0, 0), (101, 0), (101, 40), (0, 40)]))


if __name__ == "__main__":
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_") and callable(v)]
    for t in tests:
        t()
        print(f"ok  {t.__name__}")
    print(f"{len(tests)} tests passed")

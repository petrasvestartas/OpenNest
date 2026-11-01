// Faithful C++ port of nest-rs geometry/original_shape.rs
#pragma once
#include "polygon.hpp"
#include "shape_ops.hpp"

namespace nest {

// NP_SIMPL_MAXDEV (model units, default 0 = off): cap on how far the simplified / concavity-closed
// collision outline may deviate from the exact outline (see SourceShape::convert_to_internal). Set per
// solve by the C ABI BEFORE parts/containers are built. 0 => the original area-ratio simplification only.
inline f32 g_simpl_maxdev = 0.0f;

// One-sided outline deviation: max over points of `mod`'s boundary (vertices + samples every step/2 along
// each edge, at most 4096 per edge) of the distance to `exact`'s boundary. Distance is 1-Lipschitz along an
// edge, so sampling at step/2 under-reads the true maximum by at most step/4.
inline f32 outline_deviation(const Polygon& mod, const Polygon& exact, f32 step) {
    const auto& M = mod.vertices;
    const auto& E = exact.vertices;
    if (M.empty() || E.size() < 2) return 0.0f;
    auto dist_to_exact = [&](Point p) {
        f32 best = std::numeric_limits<f32>::max();
        for (usize j = 0; j < E.size(); ++j) {
            const Point& a = E[j]; const Point& b = E[(j + 1) % E.size()];
            f32 dx = b.x - a.x, dy = b.y - a.y, l2 = dx * dx + dy * dy;
            f32 t = l2 > 0.0f ? ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2 : 0.0f;
            t = std::min(1.0f, std::max(0.0f, t));
            f32 qx = a.x + t * dx - p.x, qy = a.y + t * dy - p.y;
            best = std::min(best, qx * qx + qy * qy);
        }
        return std::sqrt(best);
    };
    f32 dev = 0.0f;
    const f32 h = std::max(step * 0.5f, 1e-6f);
    for (usize i = 0; i < M.size(); ++i) {
        const Point& a = M[i]; const Point& b = M[(i + 1) % M.size()];
        f32 len = a.distance_to(b);
        usize n = (usize)std::min(4096.0f, std::ceil(len / h));
        if (n < 1) n = 1;
        for (usize k = 0; k < n; ++k) {
            f32 t = (f32)k / (f32)n;
            dev = std::max(dev, dist_to_exact(Point(a.x + t * (b.x - a.x), a.y + t * (b.y - a.y))));
        }
    }
    return dev;
}

struct SourceShape {
    Polygon shape;
    RigidTransform pre_transform;
    ShapeModifyMode modify_mode = ShapeModifyMode::Inflate;
    ShapeModifyConfig modify_config;

    Polygon convert_to_internal() const {
        Polygon internal = shape.transform_clone(pre_transform.compose());
        if (modify_config.offset && *modify_config.offset != 0.0f)
            internal = offset_shape(internal, modify_mode, *modify_config.offset);
        if (modify_config.simplify_tolerance) {
            const Polygon exact = g_simpl_maxdev > 0.0f ? internal : Polygon();   // only kept when capping
            auto modify = [&](const Polygon& in, f32 tol) {
                Polygon out = simplify_shape(in, modify_mode, tol);
                if (modify_config.narrow_concavity_cutoff) {
                    out = close_narrow_concavities(out, modify_mode, *modify_config.narrow_concavity_cutoff);
                    out = simplify_shape(out, modify_mode, tol / 10.0f);
                }
                return out;
            };
            f32 tol = *modify_config.simplify_tolerance;
            internal = modify(internal, tol);
            // NP_SIMPL_MAXDEV: the tolerance is an AREA ratio, so the absolute outline error grows with part
            // size (a 4.7 m^2 plate simplified at 1e-3 sits up to 9 mm proud of its true outline). Tighten
            // the tolerance 10x at a time until the modified outline stays within g_simpl_maxdev (model
            // units) of the exact one; below 1e-7 use the exact outline unmodified. Off => untouched.
            if (g_simpl_maxdev > 0.0f) {
                while (outline_deviation(internal, exact, g_simpl_maxdev) > g_simpl_maxdev) {
                    tol *= 0.1f;
                    if (tol < 1e-7f) { internal = exact; break; }
                    internal = modify(exact, tol);
                }
            }
        }
        return internal;
    }
    Point centroid() const { return shape.centroid(); }
    f32 area() const { return shape.area; }
    Rect bbox() const { return shape.bbox; }
    f32 diameter() const { return shape.diameter; }
};

} // namespace nest

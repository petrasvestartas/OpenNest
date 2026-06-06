// Faithful C++ port of nest-rs geometry/transformation.rs + d_transformation.rs
// A AffineTransform is a 3x3 affine matrix; RigidTransform is its decomposition
// into (rotation, translation).  Builder methods consume-and-return in Rust; here
// they are const and return a new value (same semantics).
#pragma once
#include "common.hpp"

namespace nest {

struct RigidTransform;

using Mat3 = std::array<std::array<f32, 3>, 3>;

inline Mat3 mat_dot(const Mat3& l, const Mat3& r) {
    Mat3 o{};
    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            o[i][j] = l[i][0] * r[0][j] + l[i][1] * r[1][j] + l[i][2] * r[2][j];
    return o;
}

inline Mat3 mat_inverse(const Mat3& m) {
    f32 det = m[0][0] * m[1][1] * m[2][2] + m[0][1] * m[1][2] * m[2][0] + m[0][2] * m[1][0] * m[2][1]
              - m[0][2] * m[1][1] * m[2][0] - m[0][1] * m[1][0] * m[2][2] - m[0][0] * m[1][2] * m[2][1];
    return Mat3{{
        {(m[1][1] * m[2][2] - m[1][2] * m[2][1]) / det,
         (m[0][2] * m[2][1] - m[0][1] * m[2][2]) / det,
         (m[0][1] * m[1][2] - m[0][2] * m[1][1]) / det},
        {(m[1][2] * m[2][0] - m[1][0] * m[2][2]) / det,
         (m[0][0] * m[2][2] - m[0][2] * m[2][0]) / det,
         (m[0][2] * m[1][0] - m[0][0] * m[1][2]) / det},
        {(m[1][0] * m[2][1] - m[1][1] * m[2][0]) / det,
         (m[0][1] * m[2][0] - m[0][0] * m[2][1]) / det,
         (m[0][0] * m[1][1] - m[0][1] * m[1][0]) / det},
    }};
}

inline Mat3 rot_m(f32 angle) {
    f32 s = std::sin(angle), c = std::cos(angle);
    return Mat3{{{c, -s, 0.0f}, {s, c, 0.0f}, {0.0f, 0.0f, 1.0f}}};
}
inline Mat3 transl_m(f32 tx, f32 ty) {
    return Mat3{{{1.0f, 0.0f, tx}, {0.0f, 1.0f, ty}, {0.0f, 0.0f, 1.0f}}};
}
inline Mat3 rot_transl_m(f32 angle, f32 tx, f32 ty) { // rotation then translation
    f32 s = std::sin(angle), c = std::cos(angle);
    return Mat3{{{c, -s, tx}, {s, c, ty}, {0.0f, 0.0f, 1.0f}}};
}
inline Mat3 transl_rot_m(f32 tx, f32 ty, f32 angle) { // translation then rotation
    f32 s = std::sin(angle), c = std::cos(angle);
    return Mat3{{{c, -s, tx * c - ty * s}, {s, c, tx * s + ty * c}, {0.0f, 0.0f, 1.0f}}};
}

inline const Mat3 EMPTY_MATRIX = Mat3{{{1, 0, 0}, {0, 1, 0}, {0, 0, 1}}};

struct AffineTransform {
    Mat3 m = EMPTY_MATRIX;

    AffineTransform() = default;
    explicit AffineTransform(const Mat3& mm) : m(mm) {}

    static AffineTransform empty() { return AffineTransform(); }
    static AffineTransform from_translation(f32 tx, f32 ty) { return AffineTransform(transl_m(tx, ty)); }
    static AffineTransform from_rotation(f32 angle) { return AffineTransform(rot_m(angle)); }

    AffineTransform rotate(f32 angle) const { return AffineTransform(mat_dot(rot_m(angle), m)); }
    AffineTransform translate(f32 tx, f32 ty) const { return AffineTransform(mat_dot(transl_m(tx, ty), m)); }
    AffineTransform rotate_translate(f32 angle, f32 tx, f32 ty) const {
        return AffineTransform(mat_dot(rot_transl_m(angle, tx, ty), m));
    }
    AffineTransform translate_rotate(f32 tx, f32 ty, f32 angle) const {
        return AffineTransform(mat_dot(transl_rot_m(tx, ty, angle), m));
    }
    AffineTransform transform(const AffineTransform& other) const { return AffineTransform(mat_dot(other.m, m)); }
    AffineTransform inverse() const { return AffineTransform(mat_inverse(m)); }
    bool is_empty() const { return m == EMPTY_MATRIX; }
    const Mat3& matrix() const { return m; }

    RigidTransform decompose() const; // defined after RigidTransform
    AffineTransform transform_from_decomposed(const RigidTransform& d) const;

    static AffineTransform from(const RigidTransform& d);
};

// d_transformation.rs
struct RigidTransform {
    f32 rotation = 0.0f;
    f32 tx = 0.0f, ty = 0.0f;

    RigidTransform() = default;
    RigidTransform(f32 r, f32 x, f32 y) : rotation(r), tx(x), ty(y) {}
    static RigidTransform empty() { return RigidTransform(); }

    f32 get_rotation() const { return rotation; }
    std::pair<f32, f32> translation() const { return {tx, ty}; }
    AffineTransform compose() const { return AffineTransform::from(*this); }

    bool operator==(const RigidTransform& o) const {
        return rotation == o.rotation && tx == o.tx && ty == o.ty;
    }
};

inline RigidTransform AffineTransform::decompose() const {
    f32 angle = std::atan2(m[1][0], m[0][0]);
    return RigidTransform(angle, m[0][2], m[1][2]);
}
inline AffineTransform AffineTransform::from(const RigidTransform& d) {
    return AffineTransform(rot_transl_m(d.rotation, d.tx, d.ty));
}
inline AffineTransform AffineTransform::transform_from_decomposed(const RigidTransform& d) const {
    return rotate_translate(d.rotation, d.tx, d.ty);
}

// normalize a rotation angle to [0, 2π)
inline f32 normalize_rotation(f32 r) {
    f32 n = std::fmod(r, 2.0f * PI_F);
    return n < 0.0f ? n + 2.0f * PI_F : n;
}

} // namespace nest

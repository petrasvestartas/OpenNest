// Faithful C++ port of nest-rs geometry/original_shape.rs
#pragma once
#include "polygon.hpp"
#include "shape_ops.hpp"

namespace nest {

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
            f32 tol = *modify_config.simplify_tolerance;
            internal = simplify_shape(internal, modify_mode, tol);
            if (modify_config.narrow_concavity_cutoff) {
                internal = close_narrow_concavities(internal, modify_mode, *modify_config.narrow_concavity_cutoff);
                internal = simplify_shape(internal, modify_mode, tol / 10.0f);
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

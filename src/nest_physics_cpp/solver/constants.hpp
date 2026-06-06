// Faithful C++ port of solver/src/consts.rs (algorithm tuning constants).
#pragma once
#include "../geometry/common.hpp"

namespace nest {
using nest::f32;
using nest::usize;
using nest::PI_F;

inline constexpr f32 to_radians(f32 deg) { return deg * (PI_F / 180.0f); }

inline constexpr f32 GLS_WEIGHT_MAX_INC_RATIO = 2.0f;
inline constexpr f32 GLS_WEIGHT_MIN_INC_RATIO = 1.2f;
inline constexpr f32 GLS_WEIGHT_DECAY = 0.95f;
inline constexpr f32 OVERLAP_PROXY_EPSILON_DIAM_RATIO = 0.01f;

inline constexpr f32 CD_STEP_SUCCESS = 1.1f;
inline constexpr f32 CD_STEP_FAIL = 0.5f;

inline constexpr std::pair<f32, f32> PRE_REFINE_CD_TL_RATIOS{0.25f, 0.02f};
inline const std::pair<f32, f32> PRE_REFINE_CD_R_STEPS{to_radians(5.0f), to_radians(1.0f)};
inline constexpr std::pair<f32, f32> SND_REFINE_CD_TL_RATIOS{0.01f, 0.001f};
inline const std::pair<f32, f32> SND_REFINE_CD_R_STEPS{to_radians(0.5f), to_radians(0.05f)};

inline constexpr f32 UNIQUE_SAMPLE_THRESHOLD = 0.05f;

inline constexpr f32 DEFAULT_EXPLORE_TIME_RATIO = 0.8f;
inline constexpr f32 DEFAULT_COMPRESS_TIME_RATIO = 0.2f;
inline constexpr usize DEFAULT_MAX_CONSEQ_FAILS_EXPL = 10;
inline constexpr f32 DEFAULT_FAIL_DECAY_RATIO_CMPR = 0.9f;

} // namespace nest

# nest_physics

2D irregular nesting — packs polygon parts into fixed-size sheets without overlaps.

## Build
    g++ -std=c++20 -O3 -march=native -pthread -fopenmp-simd nest_physics.cpp -o nest_physics.exe

## Run
    nest_physics.exe <input.json> <output.svg> <budget>

budget: `120` = 120 seconds (anytime) | `3000i` = 3000 iterations (deterministic, reproducible)

## Overlap signal
Overlaps are resolved by minimizing a per-pair overlap penalty. By default the penalty is a
**penetration-depth** measure — how far two parts must move to separate — which handles deep, thin
overlaps better than an area measure and packs tighter (≈44/47 vs 42/47 on the test sheet at equal
work). Build with `-DUSE_DEPTH_PROXY=0` for the area-based baseline.
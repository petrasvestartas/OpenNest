# Run without freezing

Both engines can solve for seconds. Run them on a **worker thread** and poll for progress + the current best
layout so the UI stays responsive and you can draw the layout tightening live.

1. Start `nfp_nest` / `np_nest` on a background thread.
2. On a timer, read **`*_progress()`** (round/generation reached) and **`*_poll_layout(...)`** (current best
   layout) to draw.
3. **`*_cancel()`** stops early and keeps the best so far; **`*_cancel_reset()`** re‑arms before the next run.

## Example

```cpp
#include "capi/nfp_nest_capi.h"
#include <thread>
#include <chrono>
#include <cstdio>

// ... build pvc/pxy/pqty/svc/sxy and NfpParams p as in the nfp_nest example ...

nfp_cancel_reset();                                   // arm
int placed = 0, nSheets = 0; double fitness = 0;

std::thread worker([&]{
    placed = nfp_nest(/* … args … */
        &p, tx, ty, ang, sid, pidx, &nSheets, &fitness);
});

// poll while it runs
double tmp_tx[12], tmp_ty[12], tmp_ang[12]; int tmp_sid[12], tmp_pidx[12], tmp_n;
while (!worker.joinable() == false && nfp_progress() >= 0) {
    long gen = nfp_progress();                         // generation reached
    int live = nfp_poll_layout(12, tmp_tx, tmp_ty, tmp_ang, tmp_sid, tmp_pidx, &tmp_n);
    printf("gen %ld, %d placed so far, fitness %.3f\n", gen, live, nfp_fitness());
    // … draw the live layout …
    if (/* user pressed cancel */ false) { nfp_cancel(); break; }
    std::this_thread::sleep_for(std::chrono::milliseconds(80));
    if (gen >= p.generations) break;
}
worker.join();
```

## Functions

```c
// NFP engine (nfp_nest.dll)
void      nfp_cancel(void);          // stop the running solve, keep the best so far
void      nfp_cancel_reset(void);    // re-arm before the next run
long long nfp_progress(void);        // GA generation reached so far
double    nfp_fitness(void);         // best fitness so far
int       nfp_poll_layout(int instance_count,
              double* out_tx, double* out_ty, double* out_angle,
              int* out_sheet_id, int* out_part_index, int* out_n_sheets);   // best layout snapshot

// Physics engine (nest_physics.dll) — same roles
void      np_cancel(void);
void      np_cancel_reset(void);
long long np_progress(void);         // relaxation rounds done
int       np_poll_layout(int part_count,
              double* out_tx, double* out_ty, double* out_angle,
              int* out_sheet_id, int* out_n_sheets);
```

`*_poll_layout` is UI‑thread safe: it copies the latest internal best into your buffers and returns how many
instances are placed (`0` if the solve hasn't produced a layout yet).

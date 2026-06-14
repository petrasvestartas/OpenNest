// 03 live — run the NFP engine on a background thread, poll the evolving best layout.
#include "capi/nfp_nest_capi.h"
#include <stdio.h>
#include <thread>
#include <chrono>

int main(void) {
    int    pvc[12]; double pxy[12 * 8];                  // 12 rectangles
    int    pqty[12], no_holes[12] = {0};
    for (int i = 0; i < 12; i++) {
        double w = 20 + (i % 4) * 6, h = 14;
        pvc[i] = 4; pqty[i] = 1;
        double r[8] = { 0,0, w,0, w,h, 0,h };
        for (int k = 0; k < 8; k++) pxy[i * 8 + k] = r[k];
    }
    int    svc[]    = { 4 };
    double sxy[]    = { 0,0, 200,0, 200,200, 0,200 };
    int    sheet_holes[1] = {0};

    NfpParams p = {0};
    p.placementType = 1; p.rotations = 8; p.populationSize = 10; p.mutationRate = 10;
    p.seed = 7; p.clipperScale = 1e7; p.curveTolerance = 0.3; p.mode = 1;
    p.generations = 200; p.useParallel = 1; p.numSeeds = 4;

    double tx[12], ty[12], ang[12]; int sid[12], pidx[12]; int n_sheets; double fitness;

    // launch the solve on a worker thread; the main thread polls the live best layout
    nfp_cancel_reset();
    std::thread worker([&]{
        nfp_nest(12, pvc, pxy, pqty, NULL, no_holes, NULL, NULL,
                 1, svc, sxy, sheet_holes, NULL, NULL,
                 &p, tx, ty, ang, sid, pidx, &n_sheets, &fitness);
    });

    for (int frame = 0; frame < 5; frame++) {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        double ptx[12], pty[12], pang[12]; int psid[12], ppidx[12], pns;
        int placed = nfp_poll_layout(12, ptx, pty, pang, psid, ppidx, &pns);
        printf("gen %lld, fitness %.3f, placed %d so far\n",
               nfp_progress(), nfp_fitness(), placed);
    }
    nfp_cancel();          // ask the solve to stop early
    worker.join();
    printf("done.\n");
    return 0;
}

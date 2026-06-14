// nest_spectral ABI smoke test: pack a handful of box meshes, then VERIFY the returned R*p+t poses by
// transforming each box's 8 corners and checking (a) every instance lies inside the container, and
// (b) no two placed instances' world boxes overlap. Because the parts are axis-aligned boxes and R is a
// 90-degree rotation, each transformed box stays axis-aligned, so its world bbox is exact -> the overlap
// check is a true correctness test of both the packer and the placement->world transform.
#include "../nest_spectral_capi.h"
#include <vector>
#include <cstdio>
#include <cmath>
#include <algorithm>

static void box_mesh(double a, double b, double c, std::vector<double>& V, std::vector<int>& T) {
    V = {0,0,0, a,0,0, a,b,0, 0,b,0, 0,0,c, a,0,c, a,b,c, 0,b,c};
    T = {0,1,2, 0,2,3,  4,6,5, 4,7,6,  0,4,5, 0,5,1,
         3,2,6, 3,6,7,  0,3,7, 0,7,4,  1,5,6, 1,6,2};
}

int main() {
    const double CX = 40, CY = 40, CZ = 40;
    const double sizes[][3] = {{16,12,10}, {10,10,20}, {24,8,8}, {14,14,14}, {8,8,8}};
    const int NP = 5;

    std::vector<int> vcount, tcount, quantities;
    std::vector<double> xyz;
    std::vector<int> tris;
    for (int p = 0; p < NP; p++) {
        std::vector<double> V; std::vector<int> T;
        box_mesh(sizes[p][0], sizes[p][1], sizes[p][2], V, T);
        vcount.push_back((int)V.size() / 3);
        tcount.push_back((int)T.size() / 3);
        quantities.push_back(2);                      // two of each
        xyz.insert(xyz.end(), V.begin(), V.end());
        tris.insert(tris.end(), T.begin(), T.end());
    }
    int inst = 0; for (int q : quantities) inst += q;

    NsParams prm{}; prm.voxel_resolution = 40; prm.num_orientations = 6; prm.height_penalty = 1e8;
    prm.sort_by_volume = 1; prm.threads = 4;

    std::vector<double> tx(inst), ty(inst), tz(inst), rot(9 * inst);
    std::vector<int> cid(inst), pidx(inst); int ncont = 0;

    int placed = nest_spectral(NP, vcount.data(), xyz.data(), tcount.data(), tris.data(),
                               quantities.data(), CX, CY, CZ, &prm,
                               tx.data(), ty.data(), tz.data(), rot.data(),
                               cid.data(), pidx.data(), &ncont);
    printf("nest_spectral: placed %d/%d instances, %d container(s)\n", placed, inst, ncont);
    if (placed < 0) { printf("ERROR code %d\n", placed); return 1; }

    // Per-instance world bbox from transformed corners; check containment + pairwise non-overlap.
    struct Box { double lo[3], hi[3]; };
    std::vector<Box> boxes;
    bool ok = true;
    size_t voff = 0;
    // rebuild per-part vertex offsets
    std::vector<size_t> partVoff(NP); { size_t o = 0; for (int p = 0; p < NP; p++){ partVoff[p]=o; o += vcount[p]; } }

    for (int i = 0; i < inst; i++) {
        if (cid[i] < 0) continue;
        const double* R = &rot[9 * i];
        size_t vo = partVoff[pidx[i]];
        Box b; for (int d = 0; d < 3; d++){ b.lo[d]=1e300; b.hi[d]=-1e300; }
        for (int v = 0; v < vcount[pidx[i]]; v++) {
            double px = xyz[(vo+v)*3+0], py = xyz[(vo+v)*3+1], pz = xyz[(vo+v)*3+2];
            double wx = R[0]*px+R[1]*py+R[2]*pz + tx[i];
            double wy = R[3]*px+R[4]*py+R[5]*pz + ty[i];
            double wz = R[6]*px+R[7]*py+R[8]*pz + tz[i];
            double w[3] = {wx, wy, wz};
            for (int d = 0; d < 3; d++){ b.lo[d]=std::min(b.lo[d],w[d]); b.hi[d]=std::max(b.hi[d],w[d]); }
        }
        const double eps = 1e-6;
        if (b.lo[0]<-eps||b.lo[1]<-eps||b.lo[2]<-eps||b.hi[0]>CX+eps||b.hi[1]>CY+eps||b.hi[2]>CZ+eps) {
            printf("  instance %d OUT OF CONTAINER: [%.2f,%.2f,%.2f]-[%.2f,%.2f,%.2f]\n",
                   i, b.lo[0],b.lo[1],b.lo[2], b.hi[0],b.hi[1],b.hi[2]); ok = false;
        }
        boxes.push_back(b);
    }
    // pairwise overlap (open-interval; touching faces are fine)
    const double tol = 1e-4;
    for (size_t a = 0; a < boxes.size(); a++)
        for (size_t c = a + 1; c < boxes.size(); c++) {
            const Box& A = boxes[a]; const Box& B = boxes[c];
            bool ov = true;
            for (int d = 0; d < 3; d++)
                if (A.hi[d] <= B.lo[d] + tol || B.hi[d] <= A.lo[d] + tol) ov = false;
            if (ov) { printf("  OVERLAP between placed boxes %zu and %zu\n", a, c); ok = false; }
        }
    printf("verify: %s\n", ok ? "OK (all inside container, no overlap)" : "FAILED");
    return ok ? 0 : 1;
}

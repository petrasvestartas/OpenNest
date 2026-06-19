using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace opennest_3d
{
    // OpenNest3D — 3D mesh-mesh nesting (voxel / FFT "spectral" packing). Sits beside OpenNest1 /
    // OpenNest2 / OpenNestCollision (same "OpenNest2" panel, primary exposure). Inputs: a list of Meshes
    // (parts), a container Box, voxel resolution, orientation count, height penalty, per-part quantities,
    // and a Run gate. Outputs: the parts moved into the container, their placement Transforms, the
    // container id per instance (-1 = did not fit), and the source part index. The native solve can take
    // seconds, so it only runs when Run = true.
    public class OpenNest3DComponent : GH_Component
    {
        public OpenNest3DComponent()
          : base("OpenNest3D", "OpenNest3D",
                 "3D mesh-mesh nesting (spectral / FFT packing). Packs meshes into a container box.",
                 "Params", "OpenNest2")
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.quinary;   // packing row, right after Sheets

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter("Parts", "Parts", "Meshes to nest into the container.", GH_ParamAccess.list);
            pManager.AddBoxParameter("Container", "Container", "Container box (its X/Y/Z size is the build volume; Z is 'up').", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Resolution", "Resolution", "Voxels along the container's longest axis (higher = finer + slower).", GH_ParamAccess.item, 48);
            pManager.AddIntegerParameter("Orientations", "Orientations", "Cube orientations tried per part: 1, 4, 6, or 24 (more = tighter + slower).", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Height Penalty", "Height Penalty", "Weight biasing parts toward the floor (z). <=0 uses the 1e8 default.", GH_ParamAccess.item, 1e8);
            pManager.AddIntegerParameter("Quantities", "Quantities", "Optional copies per part (defaults to 1 each).", GH_ParamAccess.list);
            pManager.AddBooleanParameter("Run", "Run", "Run the solve (can take seconds). Off by default.", GH_ParamAccess.item, false);
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
            pManager[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("Placed", "Placed", "The parts moved/rotated into the container (one per instance).", GH_ParamAccess.list);
            pManager.AddTransformParameter("Transform", "Transform", "Placement transform per instance (world = R*p + t).", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Container", "Container", "Container id per instance (0 = placed, -1 = did not fit).", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Part Index", "Part Index", "Source part index (into the input list) per instance.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            try
            {
                var parts = new List<Mesh>();
                if (!DA.GetDataList(0, parts)) return;

                Box box = Box.Unset;
                if (!DA.GetData(1, ref box) || !box.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "A valid container Box is required.");
                    return;
                }

                int resolution = 48, orientations = 1;
                double heightPenalty = 1e8;
                bool run = false;
                DA.GetData(2, ref resolution);
                DA.GetData(3, ref orientations);
                DA.GetData(4, ref heightPenalty);
                DA.GetData(6, ref run);
                var qty = new List<int>();
                DA.GetDataList(5, qty);

                if (!run) return;   // gated: don't run the (possibly slow) native solve until asked

                // Keep only valid meshes; remember each one's original input index for reporting.
                var src = new List<Mesh>();
                var srcOrig = new List<int>();
                for (int p = 0; p < parts.Count; p++)
                {
                    Mesh m = parts[p];
                    if (m == null || !m.IsValid || m.Faces.Count == 0 || m.Vertices.Count < 3)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Skipped invalid mesh at index " + p + ".");
                        continue;
                    }
                    src.Add(m);
                    srcOrig.Add(p);
                }
                if (src.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid meshes to nest.");
                    return;
                }

                // Flatten parts to the native layout: per-part vertex/triangle counts + concatenated
                // interleaved xyz and 0-based local triangle indices, plus per-part quantities.
                int partCount = src.Count;
                var pvc = new int[partCount];
                var ptc = new int[partCount];
                var pq = new int[partCount];
                var xyzList = new List<double>();
                var triList = new List<int>();
                int inst = 0;
                for (int k = 0; k < partCount; k++)
                {
                    MeshToFlat(src[k], out double[] vx, out int[] tr);
                    pvc[k] = vx.Length / 3;
                    ptc[k] = tr.Length / 3;
                    int q = (srcOrig[k] < qty.Count) ? Math.Max(1, qty[srcOrig[k]]) : 1;
                    pq[k] = q;
                    inst += q;
                    xyzList.AddRange(vx);
                    triList.AddRange(tr);
                }
                if (inst == 0) return;

                // Container box -> local frame mapping (the solver works in [0,cx]x[0,cy]x[0,cz] local).
                Plane bp = box.Plane;
                Point3d minCorner = bp.PointAt(box.X.Min, box.Y.Min, box.Z.Min);
                Plane minPlane = new Plane(minCorner, bp.XAxis, bp.YAxis);
                Transform containerXform = Transform.PlaneToPlane(Plane.WorldXY, minPlane);
                double cx = Math.Abs(box.X.Length), cy = Math.Abs(box.Y.Length), cz = Math.Abs(box.Z.Length);

                var prm = new NestSpectralWrapper.NsParams
                {
                    voxel_resolution = Math.Max(1, resolution),
                    num_orientations = orientations,
                    height_penalty = heightPenalty,
                    sort_by_volume = 1,
                    threads = Math.Max(1, Environment.ProcessorCount - 1),
                };

                var out_tx = new double[inst];
                var out_ty = new double[inst];
                var out_tz = new double[inst];
                var out_rot = new double[9 * inst];
                var out_cid = new int[inst];
                var out_pidx = new int[inst];
                int n_containers = 0, used_gpu = 0;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                int placed = NestSpectralWrapper.nest_spectral(
                    partCount, pvc, xyzList.ToArray(), ptc, triList.ToArray(), pq,
                    cx, cy, cz, ref prm,
                    out_tx, out_ty, out_tz, out_rot, out_cid, out_pidx, out n_containers, out used_gpu);
                sw.Stop();

                if (placed < 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "nest_spectral returned error code " + placed + ".");
                    return;
                }

                // Diagnostic: the actual voxel tray and which backend ran — so slow solves are easy to explain.
                double pitch = Math.Max(cx, Math.Max(cy, cz)) / Math.Max(1, prm.voxel_resolution);
                int gx = Math.Max(1, (int)Math.Round(cx / pitch)), gy = Math.Max(1, (int)Math.Round(cy / pitch)), gz = Math.Max(1, (int)Math.Round(cz / pitch));
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    string.Format("placed {0}/{1} · {2} · tray {3}x{4}x{5} · {6} orient · {7:0} ms",
                        placed, inst, used_gpu != 0 ? "GPU" : "CPU", gx, gy, gz, orientations, sw.Elapsed.TotalMilliseconds));

                var outMeshes = new List<Mesh>(inst);
                var outX = new List<Transform>(inst);
                var outC = new List<int>(inst);
                var outI = new List<int>(inst);
                for (int i = 0; i < inst; i++)
                {
                    int pi = out_pidx[i];
                    Mesh mm = src[pi].DuplicateMesh();
                    Transform world;
                    if (out_cid[i] >= 0)
                    {
                        Transform place = AffineFromRt(out_rot, 9 * i, out_tx[i], out_ty[i], out_tz[i]);
                        world = containerXform * place;
                        mm.Transform(world);
                    }
                    else
                    {
                        world = Transform.Identity;   // did not fit: leave the part where it was
                    }
                    outMeshes.Add(mm);
                    outX.Add(world);
                    outC.Add(out_cid[i]);
                    outI.Add(srcOrig[pi]);
                }

                DA.SetDataList(0, outMeshes);
                DA.SetDataList(1, outX);
                DA.SetDataList(2, outC);
                DA.SetDataList(3, outI);
            }
            catch (Exception e)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, e.Message);
                Rhino.RhinoApp.WriteLine("OpenNest3D: " + e);
            }
        }

        // Mesh -> interleaved x,y,z doubles + flat 0-based triangle indices (quads triangulated).
        private static void MeshToFlat(Mesh mesh, out double[] xyz, out int[] tris)
        {
            Mesh m = mesh.DuplicateMesh();
            m.Faces.ConvertQuadsToTriangles();
            int nv = m.Vertices.Count;
            xyz = new double[nv * 3];
            for (int i = 0; i < nv; i++)
            {
                Point3d p = m.Vertices.Point3dAt(i);
                xyz[3 * i] = p.X; xyz[3 * i + 1] = p.Y; xyz[3 * i + 2] = p.Z;
            }
            int nf = m.Faces.Count;
            tris = new int[nf * 3];
            for (int f = 0; f < nf; f++)
            {
                MeshFace fc = m.Faces[f];
                tris[3 * f] = fc.A; tris[3 * f + 1] = fc.B; tris[3 * f + 2] = fc.C;
            }
        }

        // Build a 4x4 affine Transform from a row-major 3x3 rotation R (world = R*p + t) and translation t.
        private static Transform AffineFromRt(double[] R, int off, double tx, double ty, double tz)
        {
            Transform x = new Transform(1.0);   // identity (diagonal = 1)
            x.M00 = R[off + 0]; x.M01 = R[off + 1]; x.M02 = R[off + 2];
            x.M10 = R[off + 3]; x.M11 = R[off + 4]; x.M12 = R[off + 5];
            x.M20 = R[off + 6]; x.M21 = R[off + 7]; x.M22 = R[off + 8];
            x.M03 = tx; x.M13 = ty; x.M23 = tz;
            return x;
        }

        // 24x24 icon embedded from Resources\OpenNest3D.png (generated by Resources\make_icon.py).
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    foreach (var n in asm.GetManifestResourceNames())
                        if (n.EndsWith("OpenNest3D.png", StringComparison.OrdinalIgnoreCase))
                            using (var s = asm.GetManifestResourceStream(n))
                                if (s != null) return new System.Drawing.Bitmap(s);
                }
                catch { }
                return null;
            }
        }

        public override Guid ComponentGuid => new Guid("21b6bc0c-4302-41b8-ba51-ac99c4a9632a");
    }
}

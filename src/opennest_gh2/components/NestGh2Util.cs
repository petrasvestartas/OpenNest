using System.Collections.Generic;
using Grasshopper2.Data;
using Grasshopper2.UI.Icon;
using Rhino.Geometry;

namespace opennest_gh2.components
{
    // Loads an embedded PNG icon (icons\<file>.png -> opennest_gh2.icons.<file>.png) as an Eto bitmap and
    // wraps it in a GH2 icon. Robust across the WIP SDK (FromBitmap is the universal path).
    internal static class NestIcons
    {
        public static IIcon Load(string pngFile)
        {
            try
            {
                var asm = typeof(opennest_gh2.OpenNestGh2Plugin).Assembly;
                using (var s = asm.GetManifestResourceStream("opennest_gh2.icons." + pngFile))
                {
                    if (s == null) return null;
                    var bmp = new Eto.Drawing.Bitmap(s);
                    return AbstractIcon.FromBitmap(new[] { bmp });
                }
            }
            catch { return null; }
        }
    }

    internal static class NestGh2Util
    {
        // Bounding-box diagonal of a set of curves (used to derive a tessellation length for curved input).
        public static double Diagonal(IEnumerable<Curve> curves)
        {
            BoundingBox bb = BoundingBox.Empty;
            foreach (var c in curves)
                if (c != null) bb.Union(c.GetBoundingBox(false));
            return bb.IsValid ? bb.Diagonal.Length : 0.0;
        }

        // First value in a Tree (or default). Used to read a scalar from an Access.Tree input so that NO input
        // can drive GH2 per-branch iteration — the component then runs ONCE and emits ONE object.
        public static T First<T>(Tree<T> t, T def)
        {
            if (t != null) foreach (var v in t.AllItems) return v;
            return def;
        }

        // All values in a Tree as a list (e.g. per-part Copies, GH1-style), empty -> the given fallback.
        public static List<T> AllOr<T>(Tree<T> t, T fallback)
        {
            var list = new List<T>();
            if (t != null) foreach (var v in t.AllItems) list.Add(v);
            if (list.Count == 0) list.Add(fallback);
            return list;
        }
    }

    // Shared attribute->part matcher for the GH2 Geometry / Geometry (Surfaces) components (mirrors the GH1
    // opennest_2.AttributeMatch). For each part it collects the extra "Attributes" geometry that belongs to
    // it. Matching is decided PER ATTRIBUTE PORT, in priority order:
    //   0. FLAT LIST - a single attribute branch whose (non-null) item count equals the part count maps one
    //                  item to each part (the natural "list of N attributes for N parts" wiring).
    //   1. PATH      - (only when the parts have real tree paths) attach every attribute branch whose path
    //                  EQUALS or is a SUB-BRANCH of the part path. Used only when EVERY non-empty branch
    //                  maps to some part, so a pruned/empty branch still works.
    //   2. POSITION  - if the port has exactly one branch per part, pair branch i -> part i.
    //   3. MISMATCH  - otherwise the port lines up with the parts in none of these ways: it contributes
    //                  nothing and `mismatch` is raised so the caller can warn.
    internal static class NestGh2Attr
    {
        // full == prefix, or prefix is an ancestor of full.
        private static bool PathStartsWith(Grasshopper2.Data.Path full, Grasshopper2.Data.Path prefix)
        {
            if (full.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (full[i] != prefix[i]) return false;
            return true;
        }

        private static int NonNullCount(GeometryBase[] arr)
        {
            if (arr == null) return 0;
            int c = 0; foreach (var g in arr) if (g != null) c++; return c;
        }

        private static void AddNonNull(List<GeometryBase> dst, GeometryBase[] arr)
        {
            if (arr == null) return;
            foreach (var g in arr) if (g != null) dst.Add(g);
        }

        // Add the non-null items of `arr` to the geometry accumulator AND record their source port.
        private static void AddNonNullP(List<GeometryBase> dstGeo, List<int> dstPort, GeometryBase[] arr, int port)
        {
            if (arr == null) return;
            foreach (var g in arr) if (g != null) { dstGeo.Add(g); dstPort.Add(port); }
        }

        // Returns one GeometryBase[] per part (index-aligned with the parts). `partPaths` may be null for
        // list-based parts (the Surfaces component) -> path matching is skipped. `attrPorts` is parallel to
        // `attrTrees`: the source attribute-input-port index of each tree (base = 0, "Attributes 2" = 1, ...).
        // `portsPerPart` is returned parallel to the result: the source port of every attribute item, so the
        // nester can emit {part; port} sub-branches. `mismatch` is true when a port matches none of the rules.
        public static List<GeometryBase[]> Match(
            int partCount,
            IList<Grasshopper2.Data.Path> partPaths,
            IList<Tree<GeometryBase>> attrTrees,
            IList<int> attrPorts,
            out List<int[]> portsPerPart,
            out bool mismatch)
        {
            mismatch = false;
            int nP = partCount;
            var acc = new List<List<GeometryBase>>(nP);
            var accP = new List<List<int>>(nP);
            for (int i = 0; i < nP; i++) { acc.Add(new List<GeometryBase>()); accP.Add(new List<int>()); }

            if (attrTrees != null)
                for (int t = 0; t < attrTrees.Count; t++)
                {
                    var T = attrTrees[t];
                    if (T == null || T.LeafCount == 0) continue;
                    int port = (attrPorts != null && t < attrPorts.Count) ? attrPorts[t] : 0;
                    T.ToArrays(out GeometryBase[][] ab);
                    int nB = ab != null ? ab.Length : 0;

                    // --- (0) FLAT LIST: single branch, one item per part ---
                    if (nP > 1 && nB == 1)
                    {
                        var items = new List<GeometryBase>();
                        AddNonNull(items, ab[0]);
                        if (items.Count == nP)
                        {
                            for (int i = 0; i < nP; i++) { acc[i].Add(items[i]); accP[i].Add(port); }
                            continue;
                        }
                    }

                    // --- (1) PATH mode (parts with real tree paths only) ---
                    if (partPaths != null)
                    {
                        var branchToParts = new List<List<int>>(nB);
                        bool anyMatched = false, allNonEmptyMatched = true;
                        for (int b = 0; b < nB; b++)
                        {
                            var hits = new List<int>();
                            var bp = T.Paths[b];
                            for (int i = 0; i < nP; i++)
                                if (PathStartsWith(bp, partPaths[i])) hits.Add(i);
                            branchToParts.Add(hits);

                            if (hits.Count > 0) anyMatched = true;
                            else if (NonNullCount(ab[b]) > 0) allNonEmptyMatched = false;
                        }
                        if (anyMatched && allNonEmptyMatched)
                        {
                            for (int b = 0; b < nB; b++)
                                foreach (int i in branchToParts[b]) AddNonNullP(acc[i], accP[i], ab[b], port);
                            continue;
                        }
                    }

                    // --- (2) POSITION mode: one branch per part ---
                    if (nB == nP)
                    {
                        for (int b = 0; b < nB; b++) AddNonNullP(acc[b], accP[b], ab[b], port);
                        continue;
                    }

                    // --- (3) MISMATCH ---
                    mismatch = true;
                }

            var result = new List<GeometryBase[]>(nP);
            portsPerPart = new List<int[]>(nP);
            for (int i = 0; i < nP; i++) { result.Add(acc[i].ToArray()); portsPerPart.Add(accP[i].ToArray()); }
            return result;
        }
    }
}

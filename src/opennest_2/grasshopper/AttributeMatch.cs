using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace opennest_2
{
    // Shared attribute->part matcher for the GH1 "Geometry" and "Geometry (Surfaces)" components.
    //
    // For each part it collects the extra "Attributes" geometry that belongs to it. Matching is decided
    // PER ATTRIBUTE PORT, in this priority order:
    //
    //   0. FLAT LIST - a single attribute branch whose (non-null) item count equals the part count maps
    //                  one item to each part (the natural "list of N attributes for N parts" wiring).
    //   1. PATH      - (only when the parts have real tree paths, i.e. the curve component) attach every
    //                  attribute branch whose path EQUALS or is a SUB-BRANCH of the part path ({0} matches
    //                  {0}, {0;0}, {0;1;2}). Lets a user park one part's attributes across sub-branches.
    //                  Used only when EVERY non-empty branch maps to some part, so a pruned/empty branch
    //                  still works (the present branches map by value, the missing one leaves a gap).
    //   2. POSITION  - if the port has exactly one branch per part, pair branch i -> part i (an empty
    //                  branch = that part gets no attributes). Handles trees authored with a different
    //                  path scheme but the same branch count.
    //   3. MISMATCH  - otherwise the port's branch structure lines up with the parts in none of these
    //                  ways: it contributes nothing and `mismatch` is raised so the caller can warn.
    //
    // This removes the old failure mode where a single empty attribute branch (which Grasshopper prunes
    // upstream) dropped the branch count below the part count, the equal-count gate failed for EVERY part,
    // and so NO attributes were nested at all -- silently.
    internal static class AttributeMatch
    {
        // full == prefix, or prefix is an ancestor of full ({0} matches {0}, {0;0}, {0;1;2}).
        private static bool PathStartsWith(GH_Path full, GH_Path prefix)
        {
            if (full == null || prefix == null || full.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (full.Indices[i] != prefix.Indices[i]) return false;
            return true;
        }

        // Convert a GH branch of geometric goo to GeometryBase, skipping null/unconvertible items.
        private static List<GeometryBase> ToGeometry(IList<IGH_GeometricGoo> branch)
        {
            var outl = new List<GeometryBase>();
            if (branch == null) return outl;
            for (int k = 0; k < branch.Count; k++)
            {
                if (branch[k] == null) continue;
                var gb = GH_Convert.ToGeometryBase(branch[k]);
                if (gb != null) outl.Add(gb);
            }
            return outl;
        }

        private static void AddItems(List<GeometryBase> accGeo, List<int> accPort, List<GeometryBase> items, int port)
        {
            for (int k = 0; k < items.Count; k++) { accGeo.Add(items[k]); accPort.Add(port); }
        }

        // Returns one GeometryBase[] per part (index-aligned with the parts). `partPaths` may be null for
        // list-based parts (the Surfaces component) -> path matching is skipped, only flat-list / branch-
        // count rules apply. `attrPorts` is parallel to `attrTrees`: the source attribute-input-port index of
        // each tree (base port = 0, "Attributes 2" = 1, ...). `portsPerPart` is returned parallel to the
        // result: the source port of every attribute item, so the nester can emit {part; port} sub-branches.
        // `mismatch` is set true when at least one attribute port matches none of the rules.
        public static List<GeometryBase[]> Match(
            int partCount,
            IList<GH_Path> partPaths,
            IList<GH_Structure<IGH_GeometricGoo>> attrTrees,
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
                    if (T == null || T.DataCount == 0) continue;
                    int port = (attrPorts != null && t < attrPorts.Count) ? attrPorts[t] : 0;
                    int nB = T.PathCount;

                    // --- (0) FLAT LIST: single branch, one item per part ----------------------------
                    if (nP > 1 && nB == 1)
                    {
                        var items = ToGeometry(T.Branches[0]);
                        if (items.Count == nP)
                        {
                            for (int i = 0; i < nP; i++) { acc[i].Add(items[i]); accP[i].Add(port); }
                            continue;
                        }
                    }

                    // --- (1) PATH mode (parts with real tree paths only) ----------------------------
                    if (partPaths != null)
                    {
                        var branchToParts = new List<List<int>>(nB);
                        bool anyMatched = false, allNonEmptyMatched = true;
                        for (int b = 0; b < nB; b++)
                        {
                            var hits = new List<int>();
                            GH_Path bp = T.Paths[b];
                            for (int i = 0; i < nP; i++)
                                if (PathStartsWith(bp, partPaths[i])) hits.Add(i);
                            branchToParts.Add(hits);

                            bool branchHasData = T.Branches[b] != null && T.Branches[b].Count > 0;
                            if (hits.Count > 0) anyMatched = true;
                            else if (branchHasData) allNonEmptyMatched = false;   // a non-empty branch maps nowhere
                        }
                        if (anyMatched && allNonEmptyMatched)
                        {
                            for (int b = 0; b < nB; b++)
                            {
                                var items = ToGeometry(T.Branches[b]);
                                if (items.Count == 0) continue;
                                foreach (int i in branchToParts[b]) AddItems(acc[i], accP[i], items, port);
                            }
                            continue;
                        }
                    }

                    // --- (2) POSITION mode: one branch per part -------------------------------------
                    if (nB == nP)
                    {
                        for (int b = 0; b < nB; b++) AddItems(acc[b], accP[b], ToGeometry(T.Branches[b]), port);
                        continue;
                    }

                    // --- (3) MISMATCH ---------------------------------------------------------------
                    mismatch = true;   // contributes nothing; caller warns
                }

            var result = new List<GeometryBase[]>(nP);
            portsPerPart = new List<int[]>(nP);
            for (int i = 0; i < nP; i++) { result.Add(acc[i].ToArray()); portsPerPart.Add(accP[i].ToArray()); }
            return result;
        }
    }
}

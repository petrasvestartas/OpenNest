using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper2.Components;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_rhino_lib;

namespace opennest_gh2.components
{
    // Merge: combine several OpenNest Geometry (nest_geo) streams into one.
    [IoId("8eb83827-eb1d-457e-a45e-a29a241d314d")]
    public class MergeComponent : Component
    {
        public MergeComponent() : base(new Nomen("Merge", "Merge several OpenNest Geometry streams.", "OpenNest", "Input")) { }
        public MergeComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("merge.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Geometry A", "A", "OpenNest Geometry.", Access.Twig);
            inputs.AddGeneric("Geometry B", "B", "OpenNest Geometry.", Access.Twig);
        }
        protected override void AddOutputs(OutputAdder outputs) => outputs.AddGeneric("Geometry", "G", "Merged OpenNest Geometry.");

        protected override void Process(IDataAccess access)
        {
            var list = new List<nest_geo>();
            if (access.GetItemArray(0, out nest_geo[] a) && a != null) list.AddRange(a.Where(x => x != null));
            if (access.GetItemArray(1, out nest_geo[] b) && b != null) list.AddRange(b.Where(x => x != null));
            if (list.Count == 0) { access.AddWarning("No geometry", "Connect Geometry streams."); return; }
            access.SetItem(0, nest_geo.Merge(list));
        }
    }

    // Sheets (Surfaces): build nest_sheets from planar Breps' boundary loops.
    [IoId("11e19ce6-e1a3-47b1-9a91-ecc959d1dfca")]
    public class SheetsSurfacesComponent : Component
    {
        public SheetsSurfacesComponent() : base(new Nomen("Sheets (Surfaces)", "Define nesting sheets from planar surfaces.", "OpenNest", "Input")) { }
        public SheetsSurfacesComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("sheet_surface.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Surfaces", "S", "Planar surfaces/breps (with optional holes).", Access.Twig);
            inputs.AddInteger("Copies", "C", "Total sheets to array.", Access.Item, Requirement.MayBeMissing).Set(100);
            inputs.AddNumber("Gap", "G", "Gap between arrayed sheets.", Access.Item, Requirement.MayBeMissing).Set(0.1);
            inputs.AddNumber("Offset", "O", "Inward margin (0 = off).", Access.Item, Requirement.MayBeMissing).Set(0.0);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Sheets", "S", "OpenNest sheets.");
            outputs.AddCurve("Polylines", "P", "Generated sheet outlines.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Brep[] breps) || breps == null || breps.Length == 0) return;
            access.GetItem(1, out int copies); access.GetItem(2, out double gap); access.GetItem(3, out double offset);
            var helper = new nest_geo();
            var plinesList = new List<List<Polyline>>();
            foreach (var brep in breps)
            {
                if (brep == null) continue;
                var loops = ComponentGeoUtil.BrepLoops(brep);
                var pls = loops.Where(c => c != null && c.IsClosed).Select(c => helper.curve_to_polyline(c, 0)).Where(p => p != null && p.Count >= 4).ToList();
                if (pls.Count > 0) plinesList.Add(pls);
            }
            if (plinesList.Count == 0) return;
            double g = gap;
            var sheets = new nest_sheets(plinesList, new List<double> { g, g }, new List<int>(), copies < 1 ? 1 : copies);
            if (offset != 0) sheets.offset_sheet_boundary(offset);
            access.SetItem(0, sheets);
            var outPolys = new List<Curve>();
            foreach (var s in sheets.sheets) { if (s == null) continue; foreach (var pl in s) if (pl != null) outPolys.Add(pl.ToNurbsCurve()); }
            access.SetTwig(1, outPolys.ToArray());
        }
    }

    // Geometry (Surfaces): build nest_geo from planar Breps' boundary loops.
    [IoId("d5a5685a-5bf3-45a2-1c32-1a54a0d1a10e")]
    public class GeometrySurfacesComponent : Component
    {
        public GeometrySurfacesComponent() : base(new Nomen("Geometry (Surfaces)", "Prepare parts for nesting from planar surfaces.", "OpenNest", "Input")) { }
        public GeometrySurfacesComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("element_surface.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Surfaces", "S", "Planar surfaces/breps (holes auto-detected).", Access.Twig);
            inputs.AddNumber("Simplify", "Sm", "Segment division length (0 = keep all).", Access.Item, Requirement.MayBeMissing).Set(0.0);
            inputs.AddBoolean("Hull", "H", "Convex hull each part.", Access.Item, Requirement.MayBeMissing).Set(false);
            inputs.AddInteger("Copies", "C", "Copies per part.", Access.Item, Requirement.MayBeMissing).Set(1);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Geometry", "G", "OpenNest parts.");
            outputs.AddCurve("Borders", "B", "Outline border polylines.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Brep[] breps) || breps == null || breps.Length == 0) return;
            access.GetItem(1, out double simplify); access.GetItem(2, out bool hull); access.GetItem(3, out int copies);
            var curves = new List<Curve>();
            foreach (var brep in breps) if (brep != null) curves.AddRange(ComponentGeoUtil.BrepLoops(brep).Where(c => c != null && c.IsClosed));
            if (curves.Count == 0) return;
            double diag = NestGh2Util.Diagonal(curves); double seg = simplify > 0 ? simplify : (diag > 0 ? diag * 0.01 : 1.0);
            var grouped = curves.Select(c => new[] { c }).ToList();
            var copiesList = Enumerable.Repeat(copies < 1 ? 1 : copies, grouped.Count).ToList();
            var geo = nest_geo_util.geo_to_nest_geo(grouped, copiesList, new List<double> { seg, hull ? 1.0 : 0.0 }, null, hard_coded_input: false);
            access.SetItem(0, geo);
            var borders = new List<Curve>();
            foreach (var grp in geo.boundary_sorted) foreach (var tup in grp) if (tup.Item2 != null && tup.Item2.Count >= 2) borders.Add(tup.Item2.ToNurbsCurve());
            access.SetTwig(1, borders.ToArray());
        }
    }

    // Shared geometry helper (Brep -> outline curves), mirroring nest_lib.rhino_conversions.BrepLoops behaviour.
    internal static class ComponentGeoUtil
    {
        public static List<Curve> BrepLoops(Brep b)
        {
            var result = new List<Curve>();
            try
            {
                var pls = nest_lib.rhino_conversions.BrepLoops(b);
                if (pls != null) foreach (var pl in pls) if (pl != null && pl.IsValid && pl.Count >= 4) result.Add(pl.ToNurbsCurve());
            }
            catch { }
            return result;
        }
    }
}

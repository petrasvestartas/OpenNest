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
    // GH2 port of the GH1 "Geometry" component. Prepares parts for nesting from closed curves; holes vs
    // borders are auto-detected by nest_geo.identify_groups (containment + winding). Reuses geo_to_nest_geo.
    [IoId("b2c4e8a6-3f17-4d92-8a05-1c7b9e0d2a02")]
    public class GeometryComponent : Component
    {
        public GeometryComponent()
            : base(new Nomen("Geometry", "Prepare parts for nesting (holes auto-detected)", "OpenNest", "Nest")) { }

        public GeometryComponent(IReader reader) : base(reader) { }

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("element.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddCurve("Parts", "P", "Closed part curves (outer boundaries + holes; holes auto-detected).", Access.Twig);
            inputs.AddNumber("Simplify", "S", "Segment division length (0 = keep all vertices).", Access.Item, Requirement.MayBeMissing).Set(0.0);
            inputs.AddBoolean("Hull", "H", "Replace each part with its convex hull.", Access.Item, Requirement.MayBeMissing).Set(false);
            inputs.AddInteger("Copies", "C", "Copies per part.", Access.Item, Requirement.MayBeMissing).Set(1);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Geometry", "G", "OpenNest parts (feed into a solver).");
            outputs.AddCurve("Borders", "B", "Outline border polylines per part.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Curve[] parts) || parts == null || parts.Length == 0) return;
            access.GetItem(1, out double simplify);
            access.GetItem(2, out bool hull);
            access.GetItem(3, out int copies);

            var closed = parts.Where(c => c != null && c.IsClosed).ToList();
            if (closed.Count == 0) { access.AddWarning("No closed parts", "Parts must be closed curves."); return; }

            double diag = NestGh2Util.Diagonal(closed);
            double seg = simplify > 0 ? simplify : (diag > 0 ? diag * 0.01 : 1.0);

            var curvesGrouped = closed.Select(c => new[] { c }).ToList();
            var copiesList = Enumerable.Repeat(copies < 1 ? 1 : copies, curvesGrouped.Count).ToList();
            var simp = new List<double> { seg, hull ? 1.0 : 0.0 };

            var geo = nest_geo_util.geo_to_nest_geo(curvesGrouped, copiesList, simp, null, hard_coded_input: false);
            if (geo.boundary_sorted == null || geo.boundary_sorted.Count == 0)
            {
                access.AddWarning("No boundaries detected", "Could not extract closed part boundaries.");
                return;
            }

            access.SetItem(0, geo);

            var borders = new List<Curve>();
            foreach (var grp in geo.boundary_sorted)
                foreach (var tup in grp)
                    if (tup.Item2 != null && tup.Item2.Count >= 2) borders.Add(tup.Item2.ToNurbsCurve());
            access.SetTwig(1, borders.ToArray());
        }
    }
}

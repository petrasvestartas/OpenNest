using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper2.Components;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;

namespace opennest_gh2.components
{
    // Bounding Box Edges: the two base edges + dimensions of an (optionally plane-oriented) bounding box.
    [IoId("e47a4beb-a453-4915-90a7-a18adc13f91c")]
    public class BBoxEdgesComponent : Component
    {
        public BBoxEdgesComponent() : base(new Nomen("Bounding Box Edges", "First two base edges + dims of an oriented bounding box.", "OpenNest", "Util")) { }
        public BBoxEdgesComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("bbox_edges.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Geometry", "G", "Geometry to bound.");
            inputs.AddPlane("Plane", "P", "Orientation plane for the box.", Access.Item, Requirement.MayBeMissing).Set(Plane.WorldXY);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddLine("Edge 0", "L0", "First base edge.");
            outputs.AddLine("Edge 1", "L1", "Second base edge.");
            outputs.AddNumber("Length 0", "D0", "Length of first base edge.");
            outputs.AddNumber("Length 1", "D1", "Length of second base edge.");
            outputs.AddBox("Box", "B", "Oriented bounding box.");
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItem(0, out GeometryBase geo) || geo == null) { access.AddError("No geometry", "Connect geometry."); return; }
            access.GetItem(1, out Plane plane);
            Box box = plane.IsValid ? new Box(plane, geo) : new Box(geo.GetBoundingBox(true));
            var c = box.GetCorners();
            var l0 = new Line(c[0], c[1]);
            var l1 = new Line(c[0], c[3]);
            access.SetItem(0, l0);
            access.SetItem(1, l1);
            access.SetItem(2, l0.Length);
            access.SetItem(3, l1.Length);
            access.SetItem(4, box);
        }
    }

    // Project to Plane: project a curve to its best-fit plane.
    [IoId("3278ccf2-1258-1478-ae14-1b125e166bbd")]
    public class ProjectComponent : Component
    {
        public ProjectComponent() : base(new Nomen("Project to Plane", "Project a polyline onto its best-fit plane.", "OpenNest", "Util")) { }
        public ProjectComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("project.svg");

        protected override void AddInputs(InputAdder inputs) => inputs.AddCurve("Polyline", "P", "Curve to project.");
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddCurve("Polyline", "P", "Projected polyline.");
            outputs.AddPlane("Plane", "Pl", "Best-fit plane.");
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItem(0, out Curve crv) || crv == null) return;
            if (!crv.TryGetPolyline(out Polyline poly))
            {
                var pc = crv.ToPolyline(0, 1, 0.1, 1.0, 0, 0, 0, 0, true);
                if (pc == null || !pc.TryGetPolyline(out poly)) { access.AddError("Not a polyline", "Input must be a polyline-like curve."); return; }
            }
            if (Plane.FitPlaneToPoints(poly, out Plane plane) == PlaneFitResult.Failure) { access.AddError("Plane fit failed", ""); return; }
            var proj = new Polyline(poly.Select(p => plane.ClosestPoint(p)));
            access.SetItem(0, proj.ToNurbsCurve());
            access.SetItem(1, plane);
        }
    }
}

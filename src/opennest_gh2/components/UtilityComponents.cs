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

using System;
using Grasshopper2.Doc;        // IUpgradeGh1Component, IDocumentObject
using Grasshopper2.Interop;    // IGH_Component (GH2's interop view of a GH1 component)
using opennest_gh2.components;

namespace opennest_gh2.upgraders
{
    // GH1 -> GH2 upgraders. GH2 scans loaded plugin assemblies for IUpgradeGh1Component; when it opens a GH1
    // .gh/.ghx, a GH1 component whose GUID == Grasshopper1Id is replaced by Upgrade(...). We read the GH1
    // settings off UnderlyingObject and reconnect wires + persistent data via TransferInputs/Outputs.

    public sealed class OpenNestCollisionUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("b3d9f2a7-5c41-4e8a-9d6b-2a0f1c3e4d58");
        public IDocumentObject Upgrade(IGH_Component component)
        {
            var c = new OpenNestCollisionComponent();
            try
            {
                if (component.UnderlyingObject is opennest_2.component_nest src)
                    foreach (var o in src.Options)
                        foreach (var d in c.Options)
                            if (d.Key == o.Key) { d.SelectedIndex = o.SelectedIndex; d.Value = o.Value; d.TextValue = o.TextValue; }
            }
            catch { }
            // GH1 inputs Sheets/Geometry/Iterations -> GH2 0/1/2; 7 outputs 1:1.
            Safe(() => component.TransferInputs(c, new[] { (0, 0), (1, 1), (2, 2) }));
            Safe(() => component.TransferOutputs(c, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 6) }));
            Safe(() => component.TransferInstanceId(c));
            return c;
        }
        static void Safe(Action a) { try { a(); } catch { } }
    }

    public sealed class OpenNest2Upgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("55f51cca-4e86-4498-8fce-38abbe131c8c");
        public IDocumentObject Upgrade(IGH_Component component)
        {
            var c = new OpenNest2Component();
            try
            {
                if (component.UnderlyingObject is opennest_2.component2_nest src)
                    foreach (var o in src.Options)
                        foreach (var d in c.Options)
                            if (d.Key == o.Key) { d.SelectedIndex = o.SelectedIndex; d.Value = o.Value; d.TextValue = o.TextValue; }
            }
            catch { }
            Safe(() => component.TransferInputs(c, new[] { (0, 0), (1, 1), (2, 2) }));
            Safe(() => component.TransferOutputs(c, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 6) }));
            Safe(() => component.TransferInstanceId(c));
            return c;
        }
        static void Safe(Action a) { try { a(); } catch { } }
    }

    public sealed class SheetsUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("11e19ce6-e1a3-47b1-9a91-ecc987d1dfca");
        public IDocumentObject Upgrade(IGH_Component component)
        {
            var c = new SheetsComponent();
            // GH1 Sheets inputs now map 1:1 to GH2: 0 Polylines,1 Gap,2 Rows,3 Copies,4 Offset.
            Safe(() => component.TransferInputs(c, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4) }));
            Safe(() => component.TransferOutputs(c, new[] { (0, 0), (1, 1) }));
            Safe(() => component.TransferInstanceId(c));
            return c;
        }
        static void Safe(Action a) { try { a(); } catch { } }
    }

    public sealed class GeometryUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("d5a8325a-5bf3-45a2-1c32-1a54a0d1a10e");
        public IDocumentObject Upgrade(IGH_Component component)
        {
            var c = new GeometryComponent();
            // GH1 Geometry inputs now map 1:1 to GH2: 0 Parts,1 Simplify,2 Hull,3 Copies,4 Offset,5 Attributes.
            Safe(() => component.TransferInputs(c, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4), (5, 5) }));
            Safe(() => component.TransferOutputs(c, new[] { (0, 0), (1, 1) }));
            Safe(() => component.TransferInstanceId(c));
            return c;
        }
        static void Safe(Action a) { try { a(); } catch { } }
    }
}

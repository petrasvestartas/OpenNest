using System;
using Grasshopper2.Doc;
using Grasshopper2.Interop;
using opennest_gh2.components;

namespace opennest_gh2.upgraders
{
    internal static class Up { public static void Safe(Action a) { try { a(); } catch { } } }

    public sealed class OpenNest1Upgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("30400898-3a5a-434f-a703-864b9309d79e");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new OpenNest1Component();
            // GH1: Sheets0 Geo1 Spacing2 Placement3 Tolerance4 Rotations5 Iterations6 Seed7 Tries8 Reset9 Run10
            // GH2: Sheets0 Geo1 Spacing2 Placement3 Tolerance4 Rotations5 Iterations6 Seed7 Run8
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 6), (7, 7), (10, 8) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class MergeUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("8eb83827-eb1d-457e-a45e-a29a241d314d");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new MergeComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class SheetsSurfacesUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("11e19ce6-e1a3-47b1-9a91-ecc959d1dfca");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new SheetsSurfacesComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (3, 1), (1, 2), (4, 3) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class GeometrySurfacesUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("d5a5685a-5bf3-45a2-1c32-1a54a0d1a10e");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new GeometrySurfacesComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2), (3, 3) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class ProjectUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("3278ccf2-1258-1478-ae14-1b125e166bbd");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new ProjectComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }
}

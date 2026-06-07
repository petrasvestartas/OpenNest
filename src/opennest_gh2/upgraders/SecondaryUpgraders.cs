using System;
using Grasshopper2.Doc;
using Grasshopper2.Interop;
using opennest_gh2.components;

namespace opennest_gh2.upgraders
{
    // GH1 -> GH2 upgraders for the secondary / utility components (Up.Safe lives in MoreUpgraders.cs).

    public sealed class PcaUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("170bf094-b8b4-45fa-abb2-8111117c7e6d");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new PcaComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1), (2, 2) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class InscribeCircleUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("170bf094-b8b4-45fa-abb2-8799117c7e6d");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new InscribeCircleComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class RegionSlitsUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("0feeeaca-8f1f-4d7c-a24a-8e7dd21334a2");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new RegionSlitsComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0) }));   // Topology (1) dropped
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class TextUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("e47a4beb-a459-4915-90a7-a18adc56f33c");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new TextComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2), (3, 3) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class TransformGuidUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("3718d986-f4f1-40ee-4c0f-84ad2b4e1114");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new TransformGuidComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class RhinoObjectsUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("3278ccf2-1258-4896-ae14-1b125e122bbd");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new RhinoObjectsComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class UnrollUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("3718d679-f4f1-10ee-4c0f-84ad2b4e7811");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new UnrollComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2) }));   // Text(3)/TextPoint(4) dropped
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1), (2, 2) }));  // TextDots(3,4) dropped
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class BinPackingUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("9bcf7312-b1fb-4a6e-8b4e-5c213154771c");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new BinPackingComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class GeometryRhinoUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("d5a8325a-5bf3-45a2-8c32-1a54a5d1a10e");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new GeometryRhinoComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4), (5, 5) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class SimplifyUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("7b1e2c44-9a3d-4e57-b6c1-0e9f2a8d4c31");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new SimplifyComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1), (2, 2) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }

    public sealed class PackUpgrader : IUpgradeGh1Component
    {
        public Guid Grasshopper1Id => new Guid("3278ccf2-7220-1478-ae14-1b111e786bbd");
        public IDocumentObject Upgrade(IGH_Component c)
        {
            var g = new PackComponent();
            Up.Safe(() => c.TransferInputs(g, new[] { (0, 0), (1, 1), (2, 2), (3, 3), (4, 4) }));
            Up.Safe(() => c.TransferOutputs(g, new[] { (0, 0), (1, 1) }));
            Up.Safe(() => c.TransferInstanceId(g));
            return g;
        }
    }
}

using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace opennest_3d
{
    // GH1 plugin descriptor (mirrors opennest_2Info). Registers the opennest_3d.gha so Rhino 8
    // Grasshopper loads the 3D nesting component.
    public class opennest_3dInfo : GH_AssemblyInfo
    {
        public override string Name => "OpenNest3D";
        public override Bitmap Icon => null;            // default glyph is fine
        public override string Description => "3D mesh-mesh nesting (spectral / FFT packing).";
        public override Guid Id => new Guid("626e5729-bd48-4cc1-8968-c57e43928b92");
        public override string AuthorName => "Petras Vestartas";
        public override string AuthorContact => "petrasvestartas@gmail.com";
    }
}

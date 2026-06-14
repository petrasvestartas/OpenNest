using System.Runtime.InteropServices;
using Rhino.PlugIns;

namespace opennest_gh2
{
    // Minimal Rhino plug-in shell. The real work is the Grasshopper 2 components (OpenNestGh2Plugin +
    // the component classes). But this assembly is shipped as a .rhp inside a Yak package, so Rhino's
    // plug-in manager loads it at startup and REQUIRES a Rhino.PlugIns.PlugIn subclass — without one it
    // throws "No PlugIn subclass found ... initialization failed" (a popup), even though GH2 already
    // loaded the components fine. This empty shell satisfies that loader so there is no popup; native GH2
    // component libraries avoid it instead by living in GH2's Components folder (which Rhino doesn't scan).
    [Guid("c1d2e3f4-5a6b-4c7d-8e9f-0a1b2c3d4e5f")]
    public sealed class OpenNestGh2RhinoPlugin : PlugIn
    {
        public OpenNestGh2RhinoPlugin() { Instance = this; }
        public static OpenNestGh2RhinoPlugin Instance { get; private set; }
    }
}

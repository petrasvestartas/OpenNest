using System;
using System.IO;
using System.Reflection;
using Rhino.PlugIns;

// Rhino plug-in descriptor. This .rhp is what makes OpenNest2 / OpenNestCollision real Rhino commands
// (a Grasshopper .gha cannot register commands). The GUID is this plug-in's id (distinct from the
// opennest_2 GH library id). PlugInDescription attributes are surfaced in Rhino's Plug-in manager.
[assembly: System.Runtime.InteropServices.Guid("b8d4e2a1-7c3f-4e6a-9b2d-1f5a8c0e3d77")]
[assembly: PlugInDescription(DescriptionType.Organization, "OpenNest")]
[assembly: PlugInDescription(DescriptionType.Email, "petrasvestartas@gmail.com")]

namespace opennest_commands
{
    public class OpenNestCommandPlugIn : PlugIn
    {
        public OpenNestCommandPlugIn() { Instance = this; }

        public static OpenNestCommandPlugIn Instance { get; private set; }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // The commands reuse types compiled into opennest_2.gha. If a command runs before Grasshopper
            // has loaded that .gha, the default assembly resolver can't find it (the file extension is
            // .gha, not .dll), so resolve it from this plug-in's install folder ourselves. When GH has
            // already loaded it, default resolution succeeds and this handler never fires.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveOpenNest2;
            return LoadReturnCode.Success;
        }

        private static Assembly ResolveOpenNest2(object sender, ResolveEventArgs args)
        {
            if (new AssemblyName(args.Name).Name != "opennest_2") return null;
            string dir = Path.GetDirectoryName(typeof(OpenNestCommandPlugIn).Assembly.Location);
            foreach (var ext in new[] { ".gha", ".dll" })
            {
                string p = Path.Combine(dir ?? "", "opennest_2" + ext);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        }
    }
}

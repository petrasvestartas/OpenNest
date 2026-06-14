using System;
using System.IO;
using System.Reflection;
using Grasshopper2.Framework;
using Grasshopper2.UI;

namespace opennest_gh2
{
    // Grasshopper 2 plugin descriptor for OpenNest. Discovered by GH2 when the .rhp sits in the active
    // Grasshopper2 package's net7.0\Components folder.
    public class OpenNestGh2Plugin : Plugin
    {
        // Reuse the nesting logic compiled into opennest_2 (a GH1 assembly). It is bundled next to this
        // .rhp; resolve it (and let its native solver DLLs load) from the plugin folder, since the GH2 host
        // won't find "opennest_2" (the file is opennest_2.gha, which the default resolver ignores).
        static OpenNestGh2Plugin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                if (name != "opennest_2") return null;
                string dir = Path.GetDirectoryName(typeof(OpenNestGh2Plugin).Assembly.Location) ?? "";
                foreach (var ext in new[] { ".gha", ".dll" })
                {
                    string p = Path.Combine(dir, "opennest_2" + ext);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                return null;
            };

            // DIAGNOSTIC: GH2 swallows the exception behind "initialization failed", so capture any
            // exception whose text/stack mentions opennest to %TEMP%\opennest_gh2_load.log for inspection.
            try
            {
                AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
                {
                    try
                    {
                        string txt = e.Exception.ToString();
                        if (txt.IndexOf("opennest", StringComparison.OrdinalIgnoreCase) >= 0)
                            File.AppendAllText(Path.Combine(Path.GetTempPath(), "opennest_gh2_load.log"),
                                DateTime.Now.ToString("HH:mm:ss") + "  " + txt + "\n\n------\n\n");
                    }
                    catch { }
                };
            }
            catch { }
        }

        public OpenNestGh2Plugin()
            : base(
                new Guid("a1f0c3e2-7b54-4d96-9e21-6c8b0f3a4d50"),
                new Nomen("OpenNest", "2D polygonal nesting for Grasshopper 2", "OpenNest", "Nest"),
                new Version(1, 0, 0, 0))
        {
        }

        public override string Author => "Petras Vestartas";
        public override string Contact => "petrasvestartas@gmail.com";
        public override string Copyright => $"Copyright © {DateTime.UtcNow.Year} Petras Vestartas";
    }
}

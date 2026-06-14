using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

namespace opennest_gh2
{
    // This assembly is a Grasshopper 2 component library, but it ships inside a Yak package, so Rhino's
    // plug-in manager scans its .rhp at startup and tries to load it as a Rhino plug-in. Two things are
    // needed so that load succeeds silently (no "initialization failed" popup), while Grasshopper 2 still
    // loads the components normally:
    //
    //   1) A Rhino.PlugIns.PlugIn subclass (below) — Rhino requires one in any .rhp it loads.
    //   2) A module initializer that can resolve Grasshopper2 / GrasshopperIO. Rhino enumerates this
    //      assembly's types (Assembly.GetTypes()) to find the PlugIn; that loads the base type of the GH2
    //      Plugin/components (Grasshopper2.Framework.*). At Rhino's plug-in-load time Grasshopper2.dll is
    //      not in the AppDomain yet (it loads with the Grasshopper2 plug-in), so without help GetTypes throws
    //      and Rhino reports init failure. The resolver finds the assemblies in Rhino's own GH2 folder.
    internal static class Gh2AssemblyResolver
    {
        [ModuleInitializer]
        internal static void Init()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                if (name != "Grasshopper2" && name != "GrasshopperIO") return null;
                try
                {
                    // <Rhino install>\System\Rhino.exe -> <Rhino install>\Plug-ins\Grasshopper2\<tfm>\<name>.dll
                    string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    string root = Path.GetDirectoryName(Path.GetDirectoryName(exe));
                    if (root == null) return null;
                    foreach (string tfm in new[] { "net8.0", "net7.0", "net48" })
                    {
                        string p = Path.Combine(root, "Plug-ins", "Grasshopper2", tfm, name + ".dll");
                        if (File.Exists(p)) return Assembly.LoadFrom(p);
                    }
                }
                catch { }
                return null;
            };
        }
    }

    [Guid("c1d2e3f4-5a6b-4c7d-8e9f-0a1b2c3d4e5f")]
    public sealed class OpenNestGh2RhinoPlugin : PlugIn
    {
        public OpenNestGh2RhinoPlugin() { Instance = this; }
        public static OpenNestGh2RhinoPlugin Instance { get; private set; }

        // Do NOT load at Rhino startup. At startup Grasshopper 2 hasn't loaded Grasshopper2.dll yet, so
        // Rhino enumerating this assembly's (GH2-derived) types throws -> "application initialization failed".
        // Loading WhenNeeded defers it until Grasshopper 2 pulls it in (via its RhinoPluginInclude list), by
        // which point Grasshopper2.dll is loaded and the GH2 types resolve cleanly.
        public override PlugInLoadTime LoadTime => PlugInLoadTime.WhenNeeded;
    }
}

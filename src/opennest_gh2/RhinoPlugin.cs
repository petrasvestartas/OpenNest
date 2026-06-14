using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rhino.PlugIns;

namespace opennest_gh2
{
    // OpenNest is a Grasshopper 2 component library shipped in a Yak package, so Rhino's plug-in manager
    // loads its .rhp in a load context that — unlike GH2's own Components folder — does not have Rhino's UI
    // assemblies (Eto, Grasshopper2, GrasshopperIO, RhinoCommon) on its probing path. Enumerating this
    // assembly's types then fails to resolve e.g. Eto 2.11.0.0 -> Rhino reports "application initialization
    // failed". The module initializer below installs an assembly resolver (BEFORE the type enumeration that
    // needs them) that finds those host assemblies in the running Rhino's own folders. It also writes a boot
    // log so load failures are diagnosable.
    internal static class Gh2AssemblyResolver
    {
        static readonly string Log = Path.Combine(Path.GetTempPath(), "opennest_gh2_boot.log");
        static void W(string m) { try { File.AppendAllText(Log, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\n"); } catch { } }

        [ModuleInitializer]
        internal static void Init()
        {
            W("module init");
            string root = null;
            try { root = Path.GetDirectoryName(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName)); } catch { }
            // Fallback: derive the Rhino root from a loaded RhinoCommon, if present.
            if (root == null) {
                var rc = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RhinoCommon");
                if (rc != null) { try { root = Path.GetDirectoryName(Path.GetDirectoryName(rc.Location)); } catch { } }
            }
            W("rhino root = " + (root ?? "<null>"));

            // This plug-in's own folder (the package / Components folder) holds opennest_2.gha + the native
            // DLLs; Rhino's folders hold the host assemblies (Eto, Grasshopper2, GrasshopperIO, RhinoCommon).
            string self = null;
            try { self = Path.GetDirectoryName(typeof(Gh2AssemblyResolver).Assembly.Location); } catch { }
            string[] dirs = (new[] {
                self,
                root == null ? null : Path.Combine(root, "Plug-ins", "Grasshopper2", "net8.0"),
                root == null ? null : Path.Combine(root, "System", "netcore"),
                root == null ? null : Path.Combine(root, "System"),
            }).Where(x => !string.IsNullOrEmpty(x)).ToArray();

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name;
                if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null; // satellite probes: expected to miss
                foreach (var d in dirs)
                    foreach (var ext in new[] { ".dll", ".gha", ".rhp" })
                    {
                        var p = Path.Combine(d, name + ext);
                        if (File.Exists(p)) { W("resolved " + name + " <- " + p); try { return Assembly.LoadFrom(p); } catch (Exception e) { W("  load failed: " + e.Message); return null; } }
                    }
                W("UNRESOLVED " + name);
                return null;
            };
            W("resolver installed; dirs: " + string.Join(" | ", dirs));

            // Capture the real load-time exception Rhino swallows behind "application initialization failed".
            // Windowed (first 25s) + filtered to load-relevant types so we don't log unrelated Rhino exceptions.
            var t0 = DateTime.UtcNow;
            AppDomain.CurrentDomain.FirstChanceException += (sender, e) =>
            {
                try
                {
                    if ((DateTime.UtcNow - t0).TotalSeconds > 25) return;
                    string msg = e.Exception.Message ?? "";
                    // Skip only the truly-benign satellite/theme probes (FileNotFound for *.resources / theme asms).
                    if (e.Exception is FileNotFoundException &&
                        (msg.IndexOf(".resources", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         msg.IndexOf("Eto.Wpf.Aero2", StringComparison.OrdinalIgnoreCase) >= 0)) return;
                    string tn = e.Exception.GetType().Name;
                    string st = (e.Exception.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim();
                    W("EXC " + tn + ": " + msg + (st != null ? "  @ " + st : ""));
                }
                catch { }
            };
        }
    }

    [Guid("c1d2e3f4-5a6b-4c7d-8e9f-0a1b2c3d4e5f")]
    public sealed class OpenNestGh2RhinoPlugin : PlugIn
    {
        public OpenNestGh2RhinoPlugin()
        {
            Instance = this;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "opennest_gh2_boot.log"),
                  DateTime.Now.ToString("HH:mm:ss.fff") + "  PlugIn ctor\n"); } catch { }
        }
        public static OpenNestGh2RhinoPlugin Instance { get; private set; }
        public override PlugInLoadTime LoadTime => PlugInLoadTime.WhenNeeded;
    }
}

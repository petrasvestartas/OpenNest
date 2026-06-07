using System;
using System.IO;
using System.Linq;
using System.Reflection;

class Probe
{
    static Assembly _gh;
    static void Main()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string nuget = Path.Combine(home, ".nuget", "packages");
        string ghRef = Path.Combine(nuget, "grasshopper2", "2.0.9225-wip.14825", "ref", "net7.0");
        var paths = Directory.GetFiles(ghRef, "*.dll").ToList();
        if (Directory.Exists(Path.Combine(nuget, "rhinocommon")))
            paths.AddRange(Directory.GetFiles(Path.Combine(nuget, "rhinocommon"), "RhinoCommon.dll", SearchOption.AllDirectories));
        if (File.Exists(@"C:\Program Files\Rhino 8\System\Eto.dll")) paths.Add(@"C:\Program Files\Rhino 8\System\Eto.dll");
        paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location), "*.dll"));
        var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths.Distinct()), "System.Private.CoreLib");
        _gh = mlc.LoadFromAssemblyPath(Path.Combine(ghRef, "Grasshopper2.dll"));

        var sw = new StreamWriter(@"C:\pc\3_code\code_cpp\OpenNest\_gh2probe\gh2final.txt", false) { AutoFlush = true };
        Console.SetOut(sw);

        var resp = _gh.GetType("Grasshopper2.UI.Flex.Response");
        Console.WriteLine("Response enum: " + (resp != null ? string.Join(", ", Enum.GetNames(resp)) : "?"));

        // Component methods that trigger recompute / expire
        var comp = _gh.GetType("Grasshopper2.Components.Component");
        Console.WriteLine("=== Component expire/recompute-ish methods ===");
        foreach (var m in comp.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                              .Where(m => !m.IsSpecialName && (m.Name.Contains("Expire") || m.Name.Contains("Recompute") || m.Name.Contains("Modif") || m.Name.Contains("Invalidate") || m.Name.Contains("Schedule") || m.Name.Contains("Solution")))
                              .Select(m => m.Name).Distinct().OrderBy(x => x))
            Console.WriteLine("  " + m);

        // Attributes base: how to access the Document / owner to schedule a new solution
        var ca = _gh.GetType("Grasshopper2.Doc.Attributes.ComponentAttributes");
        Console.WriteLine("=== ComponentAttributes/base props for Owner/Document ===");
        foreach (var p in ca.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                            .Select(p => { try { return p.PropertyType.Name + " " + p.Name; } catch { return "? " + p.Name; } }).Distinct())
            Console.WriteLine("  " + p);
    }
}

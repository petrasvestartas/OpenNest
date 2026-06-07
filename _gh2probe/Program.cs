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

        var sw = new StreamWriter(@"C:\pc\3_code\code_cpp\OpenNest\_gh2probe\gh2builder.txt", false) { AutoFlush = true };
        Console.SetOut(sw);

        Dump("Grasshopper2.UI.Icon.Vector.Builder", new[] { "WithFill", "WithEdge", "Polyline", "Line", "Box", "Circle", "Ellipse", "Bezier", "Arc", "Text", "Curve", "Element" });
        Dump("Grasshopper2.UI.Icon.Vector.VectorIcon", null);
        Dump("Grasshopper2.UI.Icon.AbstractIcon", new[] { "FromBitmap" });
    }

    static void Dump(string typeName, string[] only)
    {
        var t = _gh.GetType(typeName);
        if (t == null) { Console.WriteLine("### " + typeName + " <NOT FOUND>"); return; }
        Console.WriteLine("### " + typeName);
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            try { Console.WriteLine("  ctor(" + Pars(c.GetParameters()) + ")"); } catch { Console.WriteLine("  ctor(?)"); }
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                          .Where(m => !m.IsSpecialName && (only == null || only.Contains(m.Name))).OrderBy(m => m.Name))
            try { Console.WriteLine("  " + Short(m.ReturnType) + " " + m.Name + "(" + Pars(m.GetParameters()) + ")"); }
            catch { Console.WriteLine("  ? " + m.Name + "(?)"); }
    }
    static string Pars(ParameterInfo[] ps) { try { return string.Join(", ", ps.Select(p => Short(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? "=" + (p.RawDefaultValue ?? "null") : ""))); } catch { return "?"; } }
    static string Short(Type t) => t == null ? "void" : t.Name;
}

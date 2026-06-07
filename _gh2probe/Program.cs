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

        var sw = new StreamWriter(@"C:\pc\3_code\code_cpp\OpenNest\_gh2probe\gh2interop.txt", false) { AutoFlush = true };
        Console.SetOut(sw);

        Console.WriteLine("=== all public Grasshopper2.Interop types ===");
        Type[] all; try { all = _gh.GetTypes(); } catch (ReflectionTypeLoadException ex) { all = ex.Types.Where(t => t != null).ToArray(); }
        foreach (var t in all.Where(t => t.IsPublic && t.Namespace == "Grasshopper2.Interop").OrderBy(t => t.FullName))
            Console.WriteLine("  " + t.FullName);

        foreach (var n in new[] { "Grasshopper2.Interop.IGH_Component", "Grasshopper2.Interop.IGH_DocumentObject", "Grasshopper2.Interop.IGH_Param", "Grasshopper2.Interop.IGH_Structure", "Grasshopper2.Interop.GH_IReader" })
            Dump(n);
    }

    static void Dump(string name)
    {
        var t = _gh.GetType(name);
        if (t == null) { Console.WriteLine("### " + name + " <NOT FOUND>"); return; }
        Console.WriteLine("### " + t.FullName + (t.IsInterface ? " (interface)" : ""));
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy).Where(m => !m.IsSpecialName))
            try { Console.WriteLine("  " + Short(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => Short(p.ParameterType) + " " + p.Name)) + ")"); } catch { Console.WriteLine("  ? " + m.Name); }
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            try { Console.WriteLine("  prop " + Short(p.PropertyType) + " " + p.Name); } catch { }
    }
    static string Short(Type t) => t == null ? "void" : t.Name;
}

using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.IO;

var dir = @"C:\Users\Petras\AppData\Roaming\McNeel\Rhinoceros\packages\8.0\Grasshopper2\2.0.9231-wip.35834\net7.0";
var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
var paths = new List<string>();
paths.AddRange(Directory.GetFiles(coreDir, "*.dll"));
paths.AddRange(Directory.GetFiles(dir, "*.dll"));
var rhinodir = @"C:\Program Files\Rhino 8\System";
if (Directory.Exists(rhinodir)) paths.AddRange(Directory.GetFiles(rhinodir, "RhinoCommon.dll"));
var resolver = new PathAssemblyResolver(paths.Distinct());
var mlc = new MetadataLoadContext(resolver);
var asm = mlc.LoadFromAssemblyPath(Path.Combine(dir, "Grasshopper2.dll"));
foreach (var tn in new[]{"InputAdder","OutputAdder"})
{
    var t = asm.GetTypes().FirstOrDefault(x => x.Name == tn);
    if (t == null) { Console.WriteLine(tn+" NOT FOUND"); continue; }
    Console.WriteLine("==== "+t.FullName+" ====");
    foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>m.Name.StartsWith("Add")).OrderBy(m=>m.Name))
    {
        var ps = string.Join(", ", m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name));
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({ps})");
    }
}

// Rhino plug-in descriptor for the command-line workflow. The opennest_2 assembly is loaded by
// Grasshopper as a .gha; adding a Rhino.PlugIns.PlugIn subclass lets Rhino ALSO register it as a
// command plug-in, so OpenNest2 / OpenNestCollision become typed commands. The plug-in id is the
// assembly GUID already declared in nest_rhino_lib/Properties/AssemblyInfo.cs (7797d081-...). If a
// future Rhino refuses to surface commands from a .gha, the same command classes can be compiled into
// a thin separate .rhp referencing this assembly.
[assembly: Rhino.PlugIns.PlugInDescription(Rhino.PlugIns.DescriptionType.Organization, "OpenNest")]
[assembly: Rhino.PlugIns.PlugInDescription(Rhino.PlugIns.DescriptionType.Email, "petrasvestartas@gmail.com")]

namespace opennest_2.commands
{
    public class OpenNestCommandPlugIn : Rhino.PlugIns.PlugIn
    {
        public OpenNestCommandPlugIn() { Instance = this; }

        public static OpenNestCommandPlugIn Instance { get; private set; }
    }
}

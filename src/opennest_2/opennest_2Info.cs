using System;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;

namespace opennest_2
{
  public class opennest_2Info : GH_AssemblyInfo
  {
    public override string Name => "opennest_2 Info";

    //Return a 24x24 pixel bitmap to represent this GHA library.
    public override Bitmap Icon => null;

    //Return a short string describing the purpose of this GHA library.
    public override string Description => "2D nesting.";

    public override Guid Id => new Guid("69663e7d-f412-4b90-ba83-520c906eaeec");

    //Return a string identifying you or your company.
    public override string AuthorName => "Petras Vestartas";

    //Return a string representing your preferred contact details.
    public override string AuthorContact => "petrasvestartas@gmail.com";

    //Return a string representing the version.  This returns the same version as the assembly.
    public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
  }
}
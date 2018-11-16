using System;
using System.Drawing;
using Grasshopper.Kernel;

namespace OpenNest {
    public class OpenNestInfo : GH_AssemblyInfo {
        public override string Name {
            get {
                return "OpenNest";
            }
        }
        public override Bitmap Icon {
            get {
                //Return a 24x24 pixel bitmap to represent this GHA library.
                return Properties.Resources.NestIcon;
            }
        }
        public override string Description {
            get {
                //Return a short string describing the purpose of this GHA library.
                return "Nesting";
            }
        }
        public override Guid Id {
            get {
                return new Guid("791ece99-2fb5-4f58-816c-6250644a9de0");
            }
        }

        public override string AuthorName {
            get {
                //Return a string identifying you or your company.
                return "Petras Vestartas";
            }
        }
        public override string AuthorContact {
            get {
                //Return a string representing your preferred contact details.
                return "petrasvestartas@gmail.com";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace opennest_2
{
    public class ComponentText : GH_Component {
        /// <summary>
        /// Initializes a new instance of the ComponentText class.
        /// </summary>
        public ComponentText()
          : base("Text", "Text",
              "Builds text outlines as curves for laser-cutting or milling.",
               "Params", "OpenNest2") {
        }

        public override GH_Exposure Exposure => GH_Exposure.octonary;

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddPlaneParameter("Location", "L", "Location and orientation of the text", GH_ParamAccess.item);
            pManager.AddTextParameter("Text", "T", "Text to display", GH_ParamAccess.item);
            pManager.AddNumberParameter("Size", "S", "Size of the text", GH_ParamAccess.item, 1);
            pManager.AddTextParameter("Font", "F", "Font, if nothing is supplied the most optimal is used \n for bold italic -> FontName True True \n for bold  -> FontName True False", GH_ParamAccess.item);
            pManager[3].Optional = true;
            //pManager.AddBooleanParameter("Bold", "B", "True for bold font", GH_ParamAccess.item, false);
            //pManager.AddIntegerParameter("Horizontal alignment", "H", "0=Center, 1=Left, 2=Right", GH_ParamAccess.item, 0);
            //pManager.AddIntegerParameter("Vertical alignment", "V", "0=Center, 1=Top, 2=Bottom, 3=Baseline", GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddCurveParameter("Curves", "C", "Text as curves", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA) {
            var location = default(Plane);
            var text = default(string);
            double size = 1;
            var bold = false;
            var hAlign = 0;
            var vAlign = 0;
            string font = "";
            DA.GetData(3, ref font);
            if (!DA.GetData(0, ref location)) return;
            if (!DA.GetData(1, ref text)) return;
            if (!DA.GetData(2, ref size)) return;

            string[] options = font.Split(' ');
            bool Bold = false;
            bool Italic = false;

            if (options.Length == 3) {
                font = options[0];
                Bold = options[1] == "True" || options[1] == "true" || options[1] == "1" ? true : false;
                Italic = options[2] == "True" || options[2] == "true" || options[2] == "1" ? true : false;

            } else if (options.Length == 1) {
                font = options[0];
            }

            if (font != "") {


                if (size == 0)
                    size = 1;

                if (!string.IsNullOrEmpty(font) && size > 0 && !string.IsNullOrEmpty(text) && location.IsValid) {

                    var te = Rhino.RhinoDoc.ActiveDoc.Objects.AddText(text, location, size, font, Bold, Italic);
                    Rhino.DocObjects.TextObject txt = Rhino.RhinoDoc.ActiveDoc.Objects.Find(te) as Rhino.DocObjects.TextObject;

                 

                    if (txt != null) {
                        var tt = txt.Geometry as Rhino.Geometry.TextEntity;
                        DA.SetDataList(0, tt.Explode());
                    }

                    Rhino.RhinoDoc.ActiveDoc.Objects.Delete(te, true);

                        

                }

            } else {

    
                //if (!DA.GetData(3, ref bold)) return;
                //if (!DA.GetData(3, ref hAlign)) return;
                // if (!DA.GetData(4, ref vAlign)) return;

                var typeWriter = bold ? nest_rhino_lib.Typewriter.Bold : nest_rhino_lib.Typewriter.Regular;

                var position = location.Origin;
                var unitX = location.XAxis * size;
                var unitZ = location.YAxis * size;

                var curves = typeWriter.Write(text, position, unitX, unitZ, hAlign, vAlign);

                DA.SetDataList(0, curves);
            }

            //FontWriter.Save(@"C:\Users\Thomas\Desktop\Test.xml", new[] { Typewriter.Regular, Typewriter.Bold });

        }




    protected override System.Drawing.Bitmap Icon {
        get {
            return Properties.Resources.text;
        }
    }

    internal static Bitmap GetIcon(GH_ActiveObject comp) {
        string nickName = comp.NickName;
        Bitmap bitmap = new Bitmap(24, 24);
        using (Graphics graphic = Graphics.FromImage(bitmap)) {
            graphic.TextRenderingHint = TextRenderingHint.AntiAlias;
            graphic.DrawString(nickName, new System.Drawing.Font(FontFamily.GenericSansSerif, 6f), Brushes.Black, new RectangleF(-1f, -1f, 26f, 26f));
        }
        return bitmap;
    }


        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid => new Guid("e47a4beb-a459-4915-90a7-a18adc56f33c"); 
    }
}
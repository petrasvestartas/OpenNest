using opennest_2;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace nest_rhino_lib
{
    public static class nest_geo_util
    {
        public static nest_geo geo_to_nest_geo(List<Curve[]> curves, List<int> copies = null, List<double> simplify_parameters = null, List<GeometryBase[]> attributes_geometries=null, bool hard_coded_input=true, List<int> rotations = null)
        {
            // Initialize the nest_geometry.
            nest_geo nestGeo = new nest_geo();

            // Defensive defaults: callers should pass >=2 simplify params, but don't crash if not.
            double sp0 = (simplify_parameters != null && simplify_parameters.Count > 0) ? simplify_parameters[0] : -100;
            bool   sp1 = (simplify_parameters != null && simplify_parameters.Count > 1) && simplify_parameters[1] > 0;

            // Count how many copies there is.
            List<int> number_of_copies = new List<int>();
            if (copies != null)
                number_of_copies = copies.Count == curves.Count ? copies : Enumerable.Repeat(1, curves.Count).ToList();
            else
                number_of_copies = Enumerable.Repeat(1, curves.Count).ToList();

            // Per-part rotation overrides (same alignment rule as copies; 0 = inherit solver setting).
            List<int> rotation_overrides =
                (rotations != null && rotations.Count == curves.Count)
                    ? rotations
                    : Enumerable.Repeat(0, curves.Count).ToList();

            // Place each curve into a separate nest object.
            int counter = 0;
            for (int i = 0; i < curves.Count; i++)
            {

                var ids = new List<int>();
                for (int j = 0; j < curves[i].Length; j++)
                {
                    nestGeo.boudary_indices_non_sorted.Add(counter);
                    nestGeo.boundary_curves_non_sorted.Add(curves[i][j]);

                    ObjectAttributes objectAttribute = new ObjectAttributes();
                    GeometryBase item = curves[i][j];

                    nestGeo.geometry.Add(item);
                    nestGeo.attributes.Add(objectAttribute);
                    // degrade to empty if attributes were omitted or the list is short/null (no crash)
                    nestGeo.geometry_attributes.Add(
                        (attributes_geometries != null && i < attributes_geometries.Count && attributes_geometries[i] != null)
                            ? attributes_geometries[i] : new GeometryBase[0]);
                    nestGeo.indices.Add(counter);
                    nestGeo.copies.Add(number_of_copies[i]);
                    nestGeo.rotations.Add(System.Math.Max(0, rotation_overrides[i]));

                    ids.Add(counter);
                    counter++;

                }

                // Ordered input is locally cleaned up.
                if (hard_coded_input)
                {
                    nestGeo.hard_coded_input(ids, sp0, sp1);

                    var text = new TextEntity();
                    text.PlainText = number_of_copies[i].ToString();
                    text.TextHeight = 100;
                    text.Plane = new Plane(nestGeo.boundary_sorted.Last()[0].Item2.CenterPoint(), Vector3d.ZAxis);
                    //RhinoApp.WriteLine("text: " + copies[i].ToString());
                    nestGeo.disply_texts.Add(text);
                }
            }

            // Unordered input is ordered at the end.
            if (!hard_coded_input)
                nestGeo.identify_groups(sp0, sp1);

            
            return nestGeo;
        }

        public static nest_geo guid_to_nest_geo(List<Guid[]> guids, string boundary_layer = "outlines", List<int> copies = null, List<double> simplify_parameters = null)
        {
            nest_geo nestGeo = new nest_geo();
            List<int> nums = new List<int>();
            if (copies != null)
            {
                nums = (copies.Count == guids.Count ? copies : Enumerable.Repeat<int>(1, guids.Count).ToList<int>());
            }
            else
            {
                nums = Enumerable.Repeat<int>(1, guids.Count).ToList<int>();
            }
            int num = 0;
            for (int i = 0; i < guids.Count; i++)
            {
                for (int j = 0; j < (int)guids[i].Length; j++)
                {
                    RhinoObject rhinoObject = RhinoDoc.ActiveDoc.Objects.Find(guids[i][j]);
                    ObjectAttributes attributes = rhinoObject.Attributes;
                    GeometryBase geometry = rhinoObject.Geometry;
                    nestGeo.geometry.Add(geometry);
                    nestGeo.attributes.Add(attributes);
                    nestGeo.indices.Add(num);
                    nestGeo.copies.Add(nums[i]);
                    string str = geometry.ObjectType.ToString();
                    if (RhinoDoc.ActiveDoc.Layers.FindIndex(attributes.LayerIndex).Name == boundary_layer)
                    {
                        if (str == "Curve")
                        {
                            Curve curve = geometry as Curve;
                            nestGeo.boudary_indices_non_sorted.Add(num);
                            nestGeo.boundary_curves_non_sorted.Add(curve);
                        }
                        else
                        {
                            RhinoApp.WriteLine(string.Concat("Uknown type: ", str));
                        }
                        num++;
                    }
                    else
                    {
                        num++;
                    }
                }
            }
            nestGeo.identify_groups(simplify_parameters[0], simplify_parameters[1] > 0);
            return nestGeo;
        }
    }
}
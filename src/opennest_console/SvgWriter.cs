using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenNestConsole
{
    // Writes a result SVG so a nest is visually verifiable: one rectangle per used sheet (laid side by
    // side) with the placed parts coloured per sheet, plus any unplaced parts in a strip below in red.
    internal static class SvgWriter
    {
        private static readonly string[] Palette =
        {
            "#4a4e69", "#22577a", "#38a3a5", "#57cc99", "#c77dff", "#ff9f1c",
            "#e76f51", "#2a9d8f", "#8338ec", "#06d6a0", "#ef476f", "#118ab2"
        };

        public static void Write(string path, IEnumerable<NestFlatten.Placement> placements,
                                 double sheetW, double sheetH, int nSheets, double gap = 40.0)
        {
            if (nSheets < 1) nSheets = 1;
            double totalW = nSheets * sheetW + (nSheets - 1) * gap;
            double unplacedTop = sheetH + gap;
            double totalH = unplacedTop + sheetH * 0.6; // room for an unplaced strip

            var sb = new StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" width=\"{0:0}\" height=\"{1:0}\" viewBox=\"-20 -20 {2:0} {3:0}\">\n",
                totalW, totalH, totalW + 40, totalH + 40));
            sb.Append("<rect x=\"-20\" y=\"-20\" width=\"100%\" height=\"100%\" fill=\"#1a1a2e\"/>\n");

            // sheet rectangles
            for (int s = 0; s < nSheets; s++)
            {
                double ox = s * (sheetW + gap);
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "<rect x=\"{0:0.##}\" y=\"0\" width=\"{1:0.##}\" height=\"{2:0.##}\" fill=\"#16213e\" stroke=\"#9a8c98\" stroke-width=\"1.5\"/>\n",
                    ox, sheetW, sheetH));
            }

            foreach (var pl in placements)
            {
                bool unplaced = pl.SheetId < 0;
                double ox, oy;
                string fill, stroke;
                if (unplaced)
                {
                    ox = 0; oy = unplacedTop;
                    fill = "#ef476f"; stroke = "#ffd6e0";
                }
                else
                {
                    ox = pl.SheetId * (sheetW + gap); oy = 0;
                    fill = Palette[pl.SheetId % Palette.Length]; stroke = "#e0e0e0";
                }
                sb.Append("<polygon points=\"");
                for (int k = 0; k < pl.Xy.Length / 2; k++)
                {
                    if (k > 0) sb.Append(' ');
                    sb.Append(string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###}",
                        pl.Xy[2 * k] + ox, pl.Xy[2 * k + 1] + oy));
                }
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "\" fill=\"{0}\" fill-opacity=\"0.6\" stroke=\"{1}\" stroke-width=\"0.8\"/>\n", fill, stroke));
            }

            sb.Append("</svg>\n");
            File.WriteAllText(path, sb.ToString());
        }
    }
}

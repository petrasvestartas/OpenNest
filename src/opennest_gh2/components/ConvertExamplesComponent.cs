using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grasshopper2.Components;
using Grasshopper2.Doc;        // Document, DocumentIO, FileContents, BackupMethod
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Path = System.IO.Path;

namespace opennest_gh2.components
{
    // One-click batch converter: opens every GH1 OpenNest example (.ghx/.gh) via GH2's document IO (which imports
    // GH1 and applies our upgraders -> our GH2 components) and saves each as a GH2 .ghz. Set Run = true to convert.
    [IoId("e9a1c4f3-6b27-4d50-8e19-0c3a7d2b5e10")]
    public class ConvertExamplesComponent : Component
    {
        public ConvertExamplesComponent()
            : base(new Nomen("Convert GH1 Examples", "Batch-convert OpenNest GH1 example files (.ghx/.gh) to GH2 .ghz.", "OpenNest", "Util")) { }

        public ConvertExamplesComponent(IReader reader) : base(reader) { }

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("merge.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddBoolean("Run", "Run", "Set true to convert the GH1 example files to .ghz.", Access.Item, Requirement.MayBeMissing).Set(false);
            inputs.AddText("Folder", "Folder", "Folder of GH1 example files (.ghx/.gh), scanned recursively.", Access.Item, Requirement.MayBeMissing).Set(@"C:\pc\3_code\code_cpp\OpenNest\docs\components\files");
            inputs.AddText("Output", "Out", "Output folder for the .ghz files.", Access.Item, Requirement.MayBeMissing).Set(@"C:\pc\3_code\code_cpp\OpenNest\examples_gh2");
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddText("Converted", "Files", "The .ghz files written.", Access.Twig);
            outputs.AddText("Log", "Log", "Per-file conversion log.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            access.GetItem(0, out bool run);
            access.GetItem(1, out string folder);
            access.GetItem(2, out string outDir);
            if (!run) { access.AddRemark("Idle", "Set Run = true to convert."); return; }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { access.AddError("Bad folder", "Examples folder not found: " + folder); return; }
            if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(folder, "examples_gh2");
            Directory.CreateDirectory(outDir);

            var sources = new List<string>();
            sources.AddRange(Directory.GetFiles(folder, "*.ghx", SearchOption.AllDirectories));
            sources.AddRange(Directory.GetFiles(folder, "*.gh", SearchOption.AllDirectories));
            sources = sources.Distinct().OrderBy(f => f).ToList();

            var converted = new List<string>();
            var log = new List<string>();
            foreach (var file in sources)
            {
                string ghz = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".ghz");
                try
                {
                    var d = Document.NewInactiveDocument();
                    var io = new DocumentIO(d, false, false, false);
                    bool opened = io.Open(file, (IEnumerable<Document>)null, (Action<Document>)null);
                    if (!opened) { log.Add("OPEN FAILED: " + file); continue; }
                    bool saved = io.SaveCopy(ghz, FileContents.All, BackupMethod.None, null);
                    if (saved) { converted.Add(ghz); log.Add("OK: " + Path.GetFileName(file) + " -> " + Path.GetFileName(ghz)); }
                    else log.Add("SAVE FAILED: " + file);
                }
                catch (Exception ex2) { log.Add("ERROR " + Path.GetFileName(file) + ": " + ex2.Message); }
            }
            log.Insert(0, $"Converted {converted.Count}/{sources.Count} -> {outDir}");
            access.SetTwig(0, converted.ToArray());
            access.SetTwig(1, log.ToArray());
        }
    }
}

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
            inputs.AddBoolean("Wipe", "Wipe", "Delete every existing .ghz in the output folder first, so no stale file lingers.", Access.Item, Requirement.MayBeMissing).Set(false);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddText("Converted", "Files", "The .ghz files written.", Access.Twig);
            outputs.AddText("Log", "Log", "Per-file conversion log: [objects, OpenNest-GH2, GH1-interop].", Access.Twig);
            outputs.AddBoolean("Clean", "Clean", "True overall only if every converted file has ZERO GH1 interop wrappers.", Access.Item);
        }

        protected override void Process(IDataAccess access)
        {
            access.GetItem(0, out bool run);
            access.GetItem(1, out string folder);
            access.GetItem(2, out string outDir);
            access.GetItem(3, out bool wipe);
            if (!run) { access.AddRemark("Idle", "Set Run = true to convert."); return; }
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { access.AddError("Bad folder", "Examples folder not found: " + folder); return; }
            if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(folder, "examples_gh2");
            Directory.CreateDirectory(outDir);

            var log = new List<string>();
            int wiped = 0;
            if (wipe)
            {
                foreach (var old in Directory.GetFiles(outDir, "*.ghz"))
                    try { File.Delete(old); wiped++; } catch (Exception ex) { log.Add("WIPE FAILED: " + Path.GetFileName(old) + " — " + ex.Message); }
            }

            var sources = new List<string>();
            sources.AddRange(Directory.GetFiles(folder, "*.ghx", SearchOption.AllDirectories));
            sources.AddRange(Directory.GetFiles(folder, "*.gh", SearchOption.AllDirectories));
            sources = sources.Distinct().OrderBy(f => f).ToList();

            var converted = new List<string>();
            int totalInterop = 0, filesWithInterop = 0;
            foreach (var file in sources)
            {
                string ghz = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file) + ".ghz");
                try
                {
                    var d = Document.NewInactiveDocument();
                    var io = new DocumentIO(d, false, false, false);
                    bool opened = io.Open(file, (IEnumerable<Document>)null, (Action<Document>)null);
                    if (!opened) { log.Add("OPEN FAILED: " + Path.GetFileName(file)); continue; }

                    // Audit: did GH2 upgrade every GH1 object to native GH2, or are some left as GH1 interop
                    // wrappers (which look/behave like GH1)? Count them so a clean conversion is verifiable.
                    int objs = 0, interop = 0, nest = 0;
                    try
                    {
                        foreach (var obj in io.Document.Objects.AllObjects)
                        {
                            objs++;
                            string fn = obj?.GetType().FullName ?? "";
                            if (fn.IndexOf("Interop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fn.IndexOf("Grasshopper1", StringComparison.OrdinalIgnoreCase) >= 0) interop++;
                            else if (fn.StartsWith("opennest_gh2", StringComparison.OrdinalIgnoreCase)) nest++;
                        }
                    }
                    catch (Exception exA) { log.Add("AUDIT WARN " + Path.GetFileName(file) + ": " + exA.Message); }

                    bool saved = io.SaveCopy(ghz, FileContents.All, BackupMethod.None, null);
                    if (!saved) { log.Add("SAVE FAILED: " + Path.GetFileName(file)); continue; }

                    converted.Add(ghz);
                    if (interop > 0) { filesWithInterop++; totalInterop += interop; }
                    string tag = interop > 0 ? "WARN" : "OK  ";
                    log.Add($"{tag} {Path.GetFileName(ghz),-34} [{objs} objs, {nest} OpenNest-GH2, {interop} GH1-interop]");
                }
                catch (Exception ex2) { log.Add("ERROR " + Path.GetFileName(file) + ": " + ex2.Message); }
            }

            bool clean = filesWithInterop == 0;
            log.Insert(0, $"Converted {converted.Count}/{sources.Count} -> {outDir}"
                          + (wipe ? $"  (wiped {wiped} old)" : "")
                          + (clean ? "  | ALL CLEAN (no GH1 interop)" : $"  | {filesWithInterop} file(s) still have GH1 interop ({totalInterop} objects)"));
            if (!clean) access.AddWarning("GH1 interop remains", $"{filesWithInterop} file(s) still contain GH1 interop wrappers — see the Log; those native GH1 objects had no GH2 upgrader.");

            access.SetTwig(0, converted.ToArray());
            access.SetTwig(1, log.ToArray());
            access.SetItem(2, clean);
        }
    }
}

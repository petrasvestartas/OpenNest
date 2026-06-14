using System;
using System.Collections.Generic;
using System.Threading;
using Grasshopper2.Components;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_rhino_lib;
using opennest_2;   // NpRun + NestOption (compiled into opennest_2.gha, bundled next to this .rhp)
using opennest_gh2.attributes;

namespace opennest_gh2.components
{
    // GH2 port of the GH1 "OpenNestCollision" component (penetration-depth nesting, nest_physics.dll / np_nest).
    // Inputs match GH1 (Sheets, Geometry, Iterations); the eight options are drawn ON THE COMPONENT BODY by
    // NestAttributes (collapsible dropdown/type-in panel + Run button) using the exact GH1 NestOption model.
    [IoId("c3d5f9b7-4a28-4e03-9b16-2d8c0a1e3b03")]
    public class OpenNestCollisionComponent : NestComponentBase
    {
        // Serialize all native nest_physics solves across every OpenNestCollision instance (the C++ engine
        // keeps process-global state: np_cancel / np_progress). GH2 may evaluate two collision components on
        // separate worker threads, so this lock is the final reentrancy guard around np_nest.
        private static readonly object s_engineLock = new object();

        // Exact GH1 option set (component_nest.cs BuildOptions), reused via the shared NestOption model.
        private readonly List<NestOption> _options = BuildCollisionOptions();

        public OpenNestCollisionComponent()
            : base(new Nomen("OpenNestCollision", "Penetration-depth nesting (nest_physics).", "OpenNest", "Nest")) { }

        public OpenNestCollisionComponent(IReader reader) : base(reader) { }

        public override IReadOnlyList<NestOption> Options => _options;

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("nest_collision.svg");

        // ESC / input-change / Stop -> ask the native solver to stop at the next round (keeps best-so-far).
        protected override void RequestCancel() { try { NestPhysics.NestPhysicsWrapper.np_cancel(); } catch { } }

        // Read the standard "Run" boolean input (port 4) that drives the solve (replaces the on-canvas button).
        protected override bool ReadRunInput(IDataAccess access) { access.GetItem(4, out bool run); return run; }

        // Exact GH1 option set — defined once in NestOptionCatalog (shared with GH1 and Nest Options).
        private static List<NestOption> BuildCollisionOptions() => NestOptionCatalog.Collision();

        private int OptInt(string key, int def)
        { foreach (var o in _options) if (o.Key == key) return (int)Math.Round(o.Value); return def; }
        private int OptToken(string key, int def)
        {
            foreach (var o in _options) if (o.Key == key)
            { int i = Math.Min(Math.Max(o.SelectedIndex, 0), o.ChoiceTokens.Count - 1); if (int.TryParse(o.ChoiceTokens[i], out int t)) return t; }
            return def;
        }
        private string OptText(string key, string def)
        { foreach (var o in _options) if (o.Key == key) return o.TextValue ?? def; return def; }

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Sheets", "Sheets", "From OpenNest tab, use component Sheets.");
            inputs.AddGeneric("Geometry", "Geometry", "From OpenNest tab, use component Geometry.");
            inputs.AddInteger("Iterations", "Iterations", "Relaxation rounds; higher packs tighter but is slower. ~4000 = the tight all-on-one-sheet pack; lower for a quick rough preview.", Access.Item, Requirement.MayBeMissing).Set(4000);
            // Optional wired options ("key value" strings, e.g. from the Nest Options component); they
            // override the matching on-canvas option rows. MayBeMissing keeps pre-v2.73 files loading.
            inputs.AddText("Options", "Options", NestOptionCatalog.OptionsInputDescription, Access.Twig, Requirement.MayBeMissing);
            inputs.AddBoolean("Run", "Run", "Wire a Boolean Toggle. TRUE = nest now; FALSE = hold the last result (toggle off then on to re-run after an input change). ESC also stops a running solve, keeping the best so far. (Options are still edited on the component body.)", Access.Item, Requirement.MayBeMissing).Set(false);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddCurve("Sheets", "Sheets", "Sheet outline polylines used for nesting.", Access.Twig);
            outputs.AddCurve("Borders", "Borders", "Placed part outline curves.", Access.Twig);
            outputs.AddGeneric("All Geometry", "All Geo", "All placed part geometry.", Access.Twig);
            outputs.AddTransform("Transforms", "Transforms", "Placement transforms for each part.", Access.Twig);
            outputs.AddInteger("Sheet Id", "Sheet Id", "Index of the sheet each part landed on.", Access.Twig);
            outputs.AddCurve("Sheet Txt", "Sheet Txt", "Sheet-number label curves.", Access.Twig);
            outputs.AddGeneric("Attributes", "Attributes", "Per-part attribute geometry.", Access.Twig);
        }

        // snapshotted solve state (set in Prepare on the UI thread, consumed by SolveCore on the worker)
        private nest_sheets _sheets;
        private nest_geo _geo;
        private NpRun _run;

        // PASS 1 (UI thread): read inputs, flatten, wire the live-preview poll. false = invalid (stay Idle).
        protected override bool Prepare(IDataAccess access)
        {
            if (!access.GetItem(0, out nest_sheets sheets) || sheets == null)
            { access.AddError("No sheets", "Connect the OpenNest Sheets output."); return false; }
            if (!access.GetItem(1, out nest_geo geo) || geo == null)
            { access.AddError("No geometry", "Connect the OpenNest Geometry output."); return false; }
            access.GetItem(2, out int iterations);

            int rotations = OptInt("num_of_rotations", 3600);
            int seed = OptInt("seed", 100);
            int starts = OptInt("starts", 1);
            int partHolesMode = OptToken("element_holes", 1);   // 0=Off 1=Fill 2=Fill First
            int poles = OptInt("poles", 48);
            bool compactOn = OptToken("compact", 1) != 0;        // Off=>false, Bottom-Left/Multi=>true
            int fitMode = OptToken("fit", 1);                    // One sheet=>1, All parts=>0

            var run = new NpRun();
            // NpRun.Flatten reads parameters positionally: [0]=rotations [1]=seed [2]=starts.
            run.Flatten(sheets, geo, new List<double> { rotations, seed, starts < 1 ? 1 : starts },
                        iterations < 1 ? 1 : iterations, partHolesMode, poles, compactOn, fitMode);
            if (!run.Ready) { access.AddError("Nothing to nest", "No parts/sheet after flattening."); return false; }

            _sheets = sheets; _geo = geo; _run = run;
            SetPoll(() =>
            {
                long done = 0; try { done = NestPhysics.NestPhysicsWrapper.np_progress(); } catch { }
                long total = run.TotalBudget;
                return new NestTick
                {
                    Done = done,
                    Total = total,
                    Status = done + " / ~" + total + " rounds  (ESC = stop)",
                    Borders = run.BuildPreviewBorders(),
                    Sheets = run.SheetOutlines()
                };
            });
            return true;
        }

        // BACKGROUND THREAD: the blocking native solve (process-global state -> serialize across instances).
        protected override void SolveCore() { lock (s_engineLock) { _run.Solve(); } }

        // PASS 2 (UI thread): assemble + publish the placed geometry, cache it for no-solve re-emits.
        protected override void Assemble(IDataAccess access)
        {
            var run = _run; var geo = _geo;
            if (run == null || geo == null) return;
            run.Assemble();

            var sheetCurves = new List<Curve>();
            foreach (var s in run.output_sheets)
                foreach (var pl in s)
                    if (pl != null && pl.Count >= 2) sheetCurves.Add(pl.ToNurbsCurve());

            var borders = new List<Curve>();
            var placed = new List<GeometryBase>();
            var xforms = new List<Transform>();
            var ids = new List<int>();
            var attributes = new List<GeometryBase>();

            int nGroups = geo.boundary_sorted.Count;
            for (int i = 0; i < nGroups; i++)
            {
                var tlist = i < run.output_transforms.Count ? run.output_transforms[i] : null;
                var slist = i < run.output_polygon_sheet_ids.Count ? run.output_polygon_sheet_ids[i] : null;
                if (tlist == null) continue;
                for (int j = 0; j < tlist.Count; j++)
                {
                    Transform x = tlist[j];
                    xforms.Add(x);
                    ids.Add(slist != null && j < slist.Count ? slist[j] : -1);
                    foreach (var tup in geo.boundary_sorted[i])
                    {
                        // Borders = the ORIGINAL outline (Item4), transformed — never the simplified collision
                        // boundary (Item2). So the placed result is always the true input shape; the Geometry
                        // component's Simplify only ever speeds up internal collision (default 0 = no simplify).
                        Curve oc = tup.Item4 != null ? tup.Item4.DuplicateCurve() : tup.Item2.ToNurbsCurve();
                        if (oc != null) { oc.Transform(x); borders.Add(oc); }
                    }
                    foreach (var gi in geo.geometry_sorted[i])
                    {
                        var g = geo.geometry[gi].Duplicate(); g.Transform(x); placed.Add(g);
                        if (geo.geometry_attributes != null && gi < geo.geometry_attributes.Count && geo.geometry_attributes[gi] != null)
                            foreach (var att in geo.geometry_attributes[gi])
                            { var a = att.Duplicate(); a.Transform(x); attributes.Add(a); }
                    }
                }
            }

            var sheetArr = sheetCurves.ToArray();
            var borderArr = borders.ToArray();
            var placedArr = placed.ToArray();
            var xformArr = xforms.ToArray();
            var idArr = ids.ToArray();
            var attrArr = attributes.ToArray();
            Action<IDataAccess> emit = a =>
            {
                a.SetTwig(0, sheetArr);
                a.SetTwig(1, borderArr);
                a.SetTwig(2, placedArr);
                a.SetTwig(3, xformArr);
                a.SetTwig(4, idArr);
                a.SetTwig(5, new Curve[0]);          // Sheet Txt (font-rendered labels) — parity placeholder
                a.SetTwig(6, attrArr);
            };
            emit(access);
            CacheResult(emit);

            var finalWires = new List<Curve>(sheetArr.Length + borderArr.Length);
            finalWires.AddRange(sheetArr); finalWires.AddRange(borderArr);
            SetFinalWires(finalWires);
        }
    }
}

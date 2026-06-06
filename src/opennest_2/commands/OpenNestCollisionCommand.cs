using System.Collections.Generic;
using Rhino.Commands;
using nest_rhino_lib;

namespace opennest_2.commands
{
    // Command-line OpenNestCollision: penetration-depth overlap relaxation (nest_physics.dll / np_nest),
    // driven through the SAME NpRun the Grasshopper component_nest uses.
    [System.Runtime.InteropServices.Guid("3f1c9a52-2d84-4f0b-8c6e-7a91b0d4e612")]
    public class OpenNestCollisionCommand : NestCommandBase
    {
        public override string EnglishName => "OpenNestCollision";

        protected override void Solve(ref nest_sheets sheets, ref nest_geo geo, int rotations, int budget, int seed)
        {
            // NpRun.Flatten reads `parameters` positionally: [0]=rotations [1]=seed [2]=starts.
            var parameters = new List<double> { rotations, seed, 1 };
            var run = new NpRun();
            run.Flatten(sheets, geo, parameters, budget, partHolesMode: 1, poles: 0, compact: true, fitMode: 0);
            run.Solve();
            run.Assemble();   // writes geo.xforms (one transform list per part group)
        }
    }
}

using System.Collections.Generic;
using nest_rhino_lib;

namespace opennest_2.commands
{
    // Command-line OpenNest2: NFP + genetic algorithm (nfp_nest.dll), driven through the SAME
    // nest_lib.rhino_example the Grasshopper component_nest2 uses (Engine defaults to "cpp").
    [System.Runtime.InteropServices.Guid("c2e7b4d8-5a16-4e93-bf02-9d3c1a76e548")]
    public class OpenNest2Command : NestCommandBase
    {
        public override string EnglishName => "OpenNest2";

        // For the GA the "budget" is generations and the seed convention differs slightly.
        protected override int DefaultBudget => 10;
        protected override string BudgetPrompt => "Generations";
        protected override int DefaultSeed => 30;

        protected override void Solve(ref nest_sheets sheets, ref nest_geo geo, int rotations, int budget, int seed)
        {
            // rhino_example reads parameters[0..8] positionally:
            //   [0]=rotations [1]=wiggle [2]=placementType(1=gravity) [3]=spacing [4]=seed
            //   [5]=curveTolerance [6]=mutation [7]=population [8]=time. At least 9 entries are required.
            var parameters = new List<double> { rotations, 0, 1, 0, seed, 0.72, 10, 10, 0 };
            var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, budget);
            nest.static_solver(ref geo);   // Engine == "cpp" -> nfp_nest.dll; writes geo.xforms
        }
    }
}

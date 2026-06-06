using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace nest_lib.Examples
{

    public class example_default_input
    {
        public NestingContext Context = new NestingContext();

        public example_default_input(int max_iterations = 3)
        {
            add_sheets_and_parts();
            run(max_iterations);
        }

        private void add_sheets_and_parts()
        {

            //add sheets
            for (int i = 0; i < 5; i++)
            {
                Context.AddSheet(3000, 1500, 0);
            }

            //add parts | is source used for identical parts?
            int src1 = Context.GetNextSource();
            for (int i = 0; i < 200; i++)
            {
                Context.AddRectanglePart(src1, 250, 220);
            }

        }

        private void run(int max_iterations)
        {
            Background.UseParallel = true;
            SvgNest.Config.placementType = PlacementTypeEnum.gravity;
            Console.WriteLine("Settings updated.. \n Start nesting.. \n " + "Parts: " + Context.Polygons.Count() + "Sheets: " + Context.Sheets.Count());

            Context.StartNest();
            do
            {
                var sw = Stopwatch.StartNew();
                Context.NestIterate(max_iterations);
                sw.Stop();
                Console.WriteLine("Iteration: " + Context.Iterations + "; fitness: " + Context.Current.fitness + "; nesting time: " + sw.ElapsedMilliseconds + "ms");
            } while (!(Context.Iterations >= max_iterations));


        }
    }
}

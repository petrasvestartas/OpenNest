using System;
using System.Collections.Generic;
using System.Linq;

namespace nest_lib
{
    // Canonical SVGnest / DeepNest genetic algorithm (faithful port; see DeepNestPort/DeepNestSharp).
    // A "generation" = the whole population evaluated once, then evolved by selection + order-crossover
    // + low-rate mutation, with SINGLE elitism so the best solution can never regress. This is what makes
    // it converge and get better over generations. The previous version multiplied the mutation rate by
    // ~100 (every gene scrambled => random search) and layered fitness-blind operators (clustering, SA,
    // local refinement, population rebuild) that injected noise instead of optimizing — all removed.
    public class GeneticAlgorithm
    {
        private SvgNestConfig Config;
        public List<PopulationItem> population;
        public static bool StrictAngles = false;
        private float[] defaultAngles;
        private Random r;

        public GeneticAlgorithm(NFP[] adam, SvgNestConfig config)
        {
            Config = config;
            this.r = (config.seed > -1) ? new Random(config.seed) : new Random();

            // Default per-part angles (used only when StrictAngles): 0,90,180,270 cycling.
            defaultAngles = new float[adam.Length];
            for (int i = 0; i < adam.Length; i++)
                defaultAngles[i] = (i * 90) % 360.0f;

            // Seed individual = parts in the given (area-sorted) order, each at an initial angle.
            var angles = new float[adam.Length];
            for (int i = 0; i < adam.Length; i++)
                angles[i] = StrictAngles ? defaultAngles[i] : randomAngle(adam[i]);

            population = new List<PopulationItem>
            {
                new PopulationItem() { placements = adam.ToList(), Rotation = angles }
            };

            // Canonical: fill the rest of the population by mutating the seed.
            while (population.Count < config.populationSize)
                population.Add(mutate(population[0]));
        }

        // A random valid orientation from the discrete rotation set (0, 360/rotations, ...).
        private float randomAngle(NFP part)
        {
            return (float)Math.Floor(r.NextDouble() * Config.rotations) * (360.0f / Config.rotations);
        }

        // Canonical mutation: per gene, with probability 0.01*mutationRate (default rate 10 => 10%),
        // swap the part with the next one; and, independently with the same probability, re-roll its angle.
        // Offspring keep fitness == null so launchWorkers re-evaluates them; the single elite (non-null
        // fitness) is skipped, so the best never regresses.
        public PopulationItem mutate(PopulationItem p)
        {
            var clone = new PopulationItem
            {
                placements = new List<NFP>(p.placements),
                Rotation = (float[])p.Rotation.Clone()
            };

            double rate = 0.01 * Config.mutationRate;
            for (int i = 0; i < clone.placements.Count; i++)
            {
                if (r.NextDouble() < rate)
                {
                    int j = i + 1;
                    if (j < clone.placements.Count)
                    {
                        var temp = clone.placements[i];
                        clone.placements[i] = clone.placements[j];
                        clone.placements[j] = temp;
                    }
                }

                if (r.NextDouble() < rate)
                    clone.Rotation[i] = randomAngle(clone.placements[i]);
            }

            return clone;
        }

        // Fitness-proportional (quadratically weighted toward fitter) selection; can exclude one individual.
        public PopulationItem randomWeightedIndividual(PopulationItem exclude = null)
        {
            var pop = this.population.ToArray();

            if (exclude != null && Array.IndexOf(pop, exclude) >= 0)
            {
                pop = pop.Where(p => p != exclude).ToArray();
            }

            var rand = r.NextDouble();
            float lower = 0;
            var weight = 1 / (float)pop.Length;
            float upper = weight;

            for (var i = 0; i < pop.Length; i++)
            {
                if (rand >= lower && rand < upper)
                {
                    return pop[i];
                }
                lower = upper;
                upper += 2 * weight * ((pop.Length - i) / (float)pop.Length);
            }

            return pop[0];
        }

        // Single-point ORDER crossover: take male[0..cut], then append the remaining genes from the other
        // parent in their original order (and a symmetric second child).
        public PopulationItem[] mate(PopulationItem male, PopulationItem female)
        {
            var cutpoint = (int)Math.Round(Math.Min(Math.Max(r.NextDouble(), 0.1), 0.9) * (male.placements.Count - 1));

            var gene1 = new List<NFP>(male.placements.Take(cutpoint).ToArray());
            var rot1 = new List<float>(male.Rotation.Take(cutpoint).ToArray());

            var gene2 = new List<NFP>(female.placements.Take(cutpoint).ToArray());
            var rot2 = new List<float>(female.Rotation.Take(cutpoint).ToArray());

            for (int i = 0; i < female.placements.Count; i++)
            {
                if (!gene1.Any(z => z.id == female.placements[i].id))
                {
                    gene1.Add(female.placements[i]);
                    rot1.Add(female.Rotation[i]);
                }
            }

            for (int i = 0; i < male.placements.Count; i++)
            {
                if (!gene2.Any(z => z.id == male.placements[i].id))
                {
                    gene2.Add(male.placements[i]);
                    rot2.Add(male.Rotation[i]);
                }
            }

            return new[] {
                new PopulationItem() { placements = gene1, Rotation = rot1.ToArray() },
                new PopulationItem() { placements = gene2, Rotation = rot2.ToArray() }
            };
        }

        // Evolve one generation: sort by fitness (lower = better), keep the single best UNCHANGED (elitism
        // => best never regresses), then fill the new population with mutated offspring of weighted parents.
        public void generation()
        {
            population = population.OrderBy(z => z.fitness ?? double.MaxValue).ToList();

            var newpopulation = new List<PopulationItem> { population[0] }; // single elite (keeps its fitness)

            while (newpopulation.Count < Config.populationSize)
            {
                var male = randomWeightedIndividual();
                var female = randomWeightedIndividual(male);
                var children = mate(male, female);

                newpopulation.Add(mutate(children[0]));
                if (newpopulation.Count < Config.populationSize)
                    newpopulation.Add(mutate(children[1]));
            }

            this.population = newpopulation;
        }
    }
}

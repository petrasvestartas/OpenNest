using Sharp3DBinPacking.Algorithms;
using Sharp3DBinPacking.Internal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sharp3DBinPacking
{
    public delegate IBinPackAlgorithm BinPackAlgorithmFactory(BinPackParameter parameter);

    public class BinPacker : IBinPacker
    {
        private readonly BinPackAlgorithmFactory[] _factories;
        private readonly BinPackerVerifyOption _verifyOption;

        public BinPacker(BinPackerVerifyOption verifyOption, params BinPackAlgorithmFactory[] factories)
        {
            _verifyOption = verifyOption;
            _factories = factories;
        }

        public BinPackResult Pack(BinPackParameter parameter)
        {
            // [ [ cuboid in bin a, cuboid in bin a, ... ], [ cuboid in bin b, ... ] ]
            var bestResult = new List<IList<Cuboid>>();
            IList<Cuboid> pendingCuboids = parameter.Cuboids.ToList();
            while (pendingCuboids.Count > 0)
            {
                // pack a single bin
                // find the best volume rate from the combination of algorithms and permutations
                IList<Cuboid> singleBestResult = null;
                IList<Cuboid> singleBestRemain = null;
                decimal singleBestVolumeRate = 0;
                string singleBestAlgorihm = null;
                foreach (var factory in _factories)
                {
                    foreach (var cuboids in GetCuboidsPermutations(pendingCuboids, parameter.ShuffleCount))
                    {
                        var targetCuboids = cuboids.Select(c => c.CloneWithoutPlaceInformation()).ToList();
                        var algorithm = factory(parameter);
                        var algorithmName = algorithm.ToString();
                        algorithm.Insert(targetCuboids);
                        var packedCuboids = targetCuboids.Where(c => c.IsPlaced).ToList();
                        if (packedCuboids.Count == 0)
                        {
                            break;
                        }
                        // verify this result
                        if (_verifyOption == BinPackerVerifyOption.All)
                        {
                            Verify(parameter, algorithmName, packedCuboids);
                        }
                        // compare with the best result
                        var volumeRate = GetVolumeRate(parameter, packedCuboids);
                        if (singleBestResult == null || volumeRate > singleBestVolumeRate)
                        {
                            // update the best result
                            singleBestResult = packedCuboids;
                            singleBestRemain = targetCuboids.Where(c => !c.IsPlaced).ToList();
                            singleBestVolumeRate = volumeRate;
                            singleBestAlgorihm = algorithmName;
                        }
                    }
                }
                if (singleBestResult == null)
                {
                    throw new InvalidOperationException(
                        "no algorithm can pack these cuboids\n" +
                        $"binWidth: {parameter.BinWidth}, binHeight: {parameter.BinHeight}, " +
                        $"binDepth: {parameter.BinDepth}, binWeight: {parameter.BinWeight}\n" +
                        $"cuboids: {string.Join("\n", parameter.Cuboids.Select(x => x.ToString()))}");
                }
                // verify the best result
                if (_verifyOption == BinPackerVerifyOption.BestOnly)
                {
                    Verify(parameter, singleBestAlgorihm, singleBestResult);
                }
                // update the best result of multiple bins
                bestResult.Add(singleBestResult);
                pendingCuboids = singleBestRemain;
            }
            return new BinPackResult(bestResult);
        }

        public static void Verify(BinPackParameter parameter, string algorithmName, IList<Cuboid> cuboids)
        {
            //       o--------o
            //      /|       /|
            //     / |      / |
            //  h o--------o  |
            //  e |  o-----|--o h
            //y i | /      | / t
            //  g |/       |/ p z
            //  h o--------o e
            //  t | width   d
            //    |  x
            // (0, 0, 0)
            for (int a = 0; a < cuboids.Count; ++a)
            {
                // check if cuboid out of bin
                var cuboid = cuboids[a];
                if (cuboid.X < 0 || cuboid.Y < 0 || cuboid.Z < 0)
                {
                    throw new ArithmeticException(
                        $"verify cuboid failed: negative position, algorithm: {algorithmName}, cuboid: {cuboid}");
                }
                if (cuboid.X + cuboid.Width > parameter.BinWidth ||
                    cuboid.Y + cuboid.Height > parameter.BinHeight ||
                    cuboid.Z + cuboid.Depth > parameter.BinDepth)
                {
                    throw new ArithmeticException(
                        $"verify cuboid failed: out of bin, algorithm: {algorithmName}, cuboid: {cuboid}");
                }
                // check if this cuboid intersects others
                for (int b = a + 1; b < cuboids.Count; ++b)
                {
                    var otherCuboid = cuboids[b];
                    if (cuboid.X < otherCuboid.X + otherCuboid.Width &&
                        otherCuboid.X < cuboid.X + cuboid.Width &&
                        cuboid.Y < otherCuboid.Y + otherCuboid.Height &&
                        otherCuboid.Y < cuboid.Y + cuboid.Height &&
                        cuboid.Z < otherCuboid.Z + otherCuboid.Depth &&
                        otherCuboid.Z < cuboid.Z + cuboid.Depth)
                    {
                        throw new ArithmeticException(
                            $"verify cuboid failed: cuboid intersects others, algorithm: {algorithmName}, cuboid a: {cuboid}, cuboid b: {otherCuboid}");
                    }
                }
            }
            // check is cuboids overweight
            if (cuboids.Sum(c => c.Weight) > parameter.BinWeight)
            {
                throw new ArithmeticException(
                    $"verify cuboid failed: cuboids overweight, algorithm: {algorithmName}");
            }
        }

        public static decimal GetVolumeRate(BinPackParameter parameter, IList<Cuboid> result)
        {
            return result.Sum(x => x.Width * x.Height * x.Depth) /
                (parameter.BinWidth * parameter.BinHeight * parameter.BinDepth);
        }

        public static decimal GetVolumeRate(BinPackParameter parameter, IList<IList<Cuboid>> result)
        {
            var volumeRates = result.Select(x => GetVolumeRate(parameter, x)).ToList();
            if (volumeRates.Count > 1) {
                // ignore last bin
                volumeRates.RemoveAt(volumeRates.Count - 1);
            }
            return volumeRates.Average();
        }

        public static IEnumerable<IEnumerable<Cuboid>> GetCuboidsPermutations(
            IEnumerable<Cuboid> cuboids, int shuffleCount)
        {
            yield return cuboids;
            yield return cuboids.OrderByDescending(x => Math.Max(Math.Max(x.Width, x.Height), x.Depth));
            yield return cuboids.OrderByDescending(x => x.Width * x.Height * x.Depth);
            if (shuffleCount > 0)
            {
                var random = new Random();
                for (var x = 0; x < shuffleCount; ++x)
                {
                    yield return cuboids.OrderBy(_ => random.Next(int.MaxValue));
                }
            }
        }

        public static BinPackAlgorithmFactory[] GetDefaultAlgorithmFactories()
        {
            return new BinPackAlgorithmFactory[]
            {
                parameter => new BinPackShelfAlgorithm(
                    parameter,
                    FreeRectChoiceHeuristic.RectBestAreaFit,
                    GuillotineSplitHeuristic.SplitLongerLeftoverAxis,
                    ShelfChoiceHeuristic.ShelfFirstFit),
                parameter => new BinPackShelfAlgorithm(
                    parameter,
                    FreeRectChoiceHeuristic.RectBestAreaFit,
                    GuillotineSplitHeuristic.SplitLongerLeftoverAxis,
                    ShelfChoiceHeuristic.ShelfNextFit),
                parameter => new BinPackGuillotineAlgorithm(
                    parameter,
                    FreeCuboidChoiceHeuristic.CuboidMinHeight,
                    GuillotineSplitHeuristic.SplitLongerLeftoverAxis),
                parameter => new BinPackGuillotineAlgorithm(
                    parameter,
                    FreeCuboidChoiceHeuristic.CuboidMinHeight,
                    GuillotineSplitHeuristic.SplitShorterLeftoverAxis)
            };
        }

        public static IBinPacker GetDefault(BinPackerVerifyOption verifyOption)
        {
            return new BinPacker(verifyOption, GetDefaultAlgorithmFactories());
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace Sharp3DBinPacking
{
    public class BinPackParameter
    {
        public decimal BinWidth { get; private set; }
        public decimal BinHeight { get; private set; }
        public decimal BinDepth { get; private set; }
        public decimal BinWeight { get; private set; }
        public bool AllowRotateVertically { get; private set; }
        public IEnumerable<Cuboid> Cuboids { get; private set; }
        public int ShuffleCount { get; set; }

        public BinPackParameter(
            decimal binWidth, decimal binHeight, decimal binDepth, IEnumerable<Cuboid> cuboids) :
            this(binWidth, binHeight, binDepth, 0, true, cuboids) { }

        public BinPackParameter(
            decimal binWidth, decimal binHeight, decimal binDepth, decimal binWeight,
            bool allowRotateVertically, IEnumerable<Cuboid> cuboids)
        {
            BinWidth = binWidth;
            BinHeight = binHeight;
            BinDepth = binDepth;
            BinWeight = binWeight;
            AllowRotateVertically = allowRotateVertically;
            Cuboids = cuboids;
            ShuffleCount = 5;
        }
    }
}

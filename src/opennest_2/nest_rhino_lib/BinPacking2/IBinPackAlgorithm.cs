using System;
using System.Collections.Generic;
using System.Text;

namespace Sharp3DBinPacking
{
    public interface IBinPackAlgorithm
    {
        void Insert(IEnumerable<Cuboid> cuboids);
    }
}

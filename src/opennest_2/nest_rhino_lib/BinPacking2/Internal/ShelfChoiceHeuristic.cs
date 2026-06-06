using System;
using System.Collections.Generic;
using System.Text;

namespace Sharp3DBinPacking.Internal
{
    public enum ShelfChoiceHeuristic
    {
        ShelfNextFit, // We always put the new cuboid to the last open shelf.
        ShelfFirstFit, // We test each cuboid against each shelf in turn and
                       // pack it to the first where it fits.
    }
}

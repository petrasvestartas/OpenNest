namespace nest_lib
{
    public class SvgNestConfig
    {
        public PlacementTypeEnum placementType = PlacementTypeEnum.gravity; // canonical: gravity (bottom-left) packs tighter
        public double curveTolerance = 0.72;
        public double scale = 25;
        public double clipperScale = 10000000;
        public bool exploreConcave = false;
        public int mutationRate = 10;        // canonical: applied as 0.01*rate => ~10% per gene
        public int populationSize = 10;      // canonical GA pool size
        public int rotations = 4;
        public double spacing = 10;
        public double sheetSpacing = 0;
        public bool useHoles = false;
        public double timeRatio = 0.5;
        public bool mergeLines = false;
        public bool simplify;



        #region port features (don't exist in the original DeepNest project)
        public bool clipByHull = false;
        public bool clipByRects = true; //clip by AABB + MinRect
        public int seed = -1;
        public float rotation_limit = 360.0f;
        #endregion
    }
}

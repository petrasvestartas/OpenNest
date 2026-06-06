namespace Sharp3DBinPacking.Internal
{
    public class Shelf
    {
        public decimal StartY { get; set; }
        public decimal Height { get; set; }
        public Guillotine2D Guillotine { get; private set; }

        public Shelf(decimal startY, decimal height, decimal binWidth, decimal binDepth)
        {
            StartY = startY;
            Height = height;
            Guillotine = new Guillotine2D(binWidth, binDepth);
        }

        public override string ToString()
        {
            return $"Shelf(StartY: {StartY}, Height: {Height})";
        }
    }
}

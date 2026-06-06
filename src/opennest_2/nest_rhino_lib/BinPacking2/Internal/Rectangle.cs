using System;
using System.Collections.Generic;
using System.Text;

namespace Sharp3DBinPacking.Internal
{
    public class Rectangle
    {
        public decimal Width { get; set; }
        public decimal Height { get; set; }
        public decimal X { get; set; }
        public decimal Y { get; set; }
        internal bool IsPlaced { get; set; }

        public Rectangle() { }

        public Rectangle(decimal width, decimal height, decimal x, decimal y)
        {
            Width = width;
            Height = height;
            X = x;
            Y = y;
        }

        public override string ToString()
        {
            return $"Rectangle(X: {X}, Y: {Y}, Width: {Width}, Height:{Height})";
        }
    }
}

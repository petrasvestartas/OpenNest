using System;
using System.Collections.Generic;
using System.Text;

namespace Sharp3DBinPacking.Internal
{
    public class Guillotine2D
    {
        private readonly decimal _binWidth;
        private readonly decimal _binHeight;
        private readonly IList<Rectangle> _usedRectangles;
        private readonly IList<Rectangle> _freeRectangles;

        public Guillotine2D(decimal width, decimal height)
        {
            _binWidth = width;
            _binHeight = height;
            _usedRectangles = new List<Rectangle>();
            _freeRectangles = new List<Rectangle>();
            AddFreeRectangle(new Rectangle(_binWidth, _binHeight, 0, 0));
        }

        public void Insert(
            Rectangle rect,
            FreeRectChoiceHeuristic rectChoice,
            out int freeRectIndex)
        {
            // Find where to put the new rectangle
            FindPositionForNewRect(rect, rectChoice, out freeRectIndex);
        }

        public void InsertOnPosition(
            Rectangle rect,
            GuillotineSplitHeuristic splitMethod,
            int freeRectIndex)
        {
            // Remove the space that was just consumed by the new rectangle.
            if (freeRectIndex < 0)
                throw new ArithmeticException("freeRectIndex < 0");
            SplitFreeRectByHeuristic(_freeRectangles[freeRectIndex], rect, splitMethod);
            _freeRectangles.RemoveAt(freeRectIndex);

            // Remember the new used rectangle
            _usedRectangles.Add(rect);
        }

        public bool IsEmpty()
        {
            return _usedRectangles.Count == 0;
        }

        private void FindPositionForNewRect(
            Rectangle rect,
            FreeRectChoiceHeuristic rectChoice,
            out int freeRectIndex)
        {
            decimal width = rect.Width;
            decimal height = rect.Height;
            decimal bestScore = decimal.MaxValue;
            freeRectIndex = -1;

            // Try each free rectangle to find the best one for placement
            for (int index = 0; index < _freeRectangles.Count; ++index)
            {
                // If this is a perfect fit upright, choose it immediately.
                var freeRectangle = _freeRectangles[index];
                if (width == freeRectangle.Width &&
                    height == freeRectangle.Height)
                {
                    rect.IsPlaced = true;
                    rect.X = freeRectangle.X;
                    rect.Y = freeRectangle.Y;
                    rect.Width = width;
                    rect.Height = height;
                    freeRectIndex = index;
                    break;
                }
                // If this is a perfect fit sideways, choose it.
                else if (height == freeRectangle.Width &&
                        width == freeRectangle.Height)
                {
                    rect.IsPlaced = true;
                    rect.X = freeRectangle.X;
                    rect.Y = freeRectangle.Y;
                    rect.Width = height;
                    rect.Height = width;
                    freeRectIndex = index;
                    break;
                }
                // Does the rectangle fit upright?
                if (width <= freeRectangle.Width &&
                    height <= freeRectangle.Height)
                {
                    decimal score = ScoreByHeuristic(rect, freeRectangle, rectChoice);

                    if (score < bestScore)
                    {
                        rect.IsPlaced = true;
                        rect.X = freeRectangle.X;
                        rect.Y = freeRectangle.Y;
                        rect.Width = width;
                        rect.Height = height;
                        bestScore = score;
                        freeRectIndex = index;
                    }
                }
                // Does the rectangle fit sideways?
                if (height <= freeRectangle.Width &&
                    width <= freeRectangle.Height)
                {
                    decimal score = ScoreByHeuristic(rect, freeRectangle, rectChoice);

                    if (score < bestScore)
                    {
                        rect.IsPlaced = true;
                        rect.X = freeRectangle.X;
                        rect.Y = freeRectangle.Y;
                        rect.Width = height;
                        rect.Height = width;
                        bestScore = score;
                        freeRectIndex = index;
                    }
                }
            }
        }

        private static decimal ScoreByHeuristic(
            Rectangle rect,
            Rectangle freeRect,
            FreeRectChoiceHeuristic rectChoice)
        {

            switch (rectChoice)
            {
                case FreeRectChoiceHeuristic.RectBestAreaFit:
                    return ScoreBestAreaFit(rect, freeRect);
                case FreeRectChoiceHeuristic.RectBestShortSideFit:
                    return ScoreBestShortSideFit(rect, freeRect);
                default:
                    throw new NotSupportedException($"rect choice is unsupported: {rectChoice}");
            }
        }

        private static decimal ScoreBestAreaFit(
            Rectangle rect,
            Rectangle freeRect)
        {
            return freeRect.Width * freeRect.Height - rect.Width * rect.Height;
        }

        private static decimal ScoreBestShortSideFit(
            Rectangle rect,
            Rectangle freeRect)
        {
            decimal leftoverHoriz = Math.Abs(freeRect.Width - rect.Width);
            decimal leftoverVert = Math.Abs(freeRect.Width - rect.Width);
            decimal leftover = Math.Min(leftoverHoriz, leftoverVert);
            return leftover;
        }

        private void SplitFreeRectByHeuristic(
            Rectangle freeRect,
            Rectangle placedRect,
            GuillotineSplitHeuristic method)
        {
            // Compute the lengths of the leftover area.
            decimal w = freeRect.Width - placedRect.Width;
            decimal h = freeRect.Height - placedRect.Height;

            // Placing placedRect into freeRect results in an L-shaped free area, which
            // must be split into two disjoint rectangles. This can be achieved with by
            // splitting the L-shape using a single line.
            // We have two choices: horizontal or vertical.

            // Use the given heuristic to decide which choice to make.

            bool splitHorizontal;
            switch (method)
            {
                case GuillotineSplitHeuristic.SplitShorterLeftoverAxis:
                    // Split along the shorter leftover axis.
                    splitHorizontal = (w <= h);
                    break;
                case GuillotineSplitHeuristic.SplitLongerLeftoverAxis:
                    // Split along the longer leftover axis.
                    splitHorizontal = (w > h);
                    break;
                case GuillotineSplitHeuristic.SplitShorterAxis:
                    // Split along the shorter total axis.
                    splitHorizontal = (freeRect.Width <= freeRect.Height);
                    break;
                case GuillotineSplitHeuristic.SplitLongerAxis:
                    // Split along the longer total axis.
                    splitHorizontal = (freeRect.Width > freeRect.Height);
                    break;
                default:
                    throw new NotSupportedException($"split method is unsupported: {method}");
            }

            // Perform the actual split.
            SplitFreeRectAlongAxis(freeRect, placedRect, splitHorizontal);
        }

        private void SplitFreeRectAlongAxis(
            Rectangle freeRect,
            Rectangle placedRect,
            bool splitHorizontal)
        {
            // Form the two new rectangles.
            var bottom = new Rectangle();
            bottom.X = freeRect.X;
            bottom.Y = freeRect.Y + placedRect.Height;
            bottom.Height = freeRect.Height - placedRect.Height;

            var right = new Rectangle();
            right.X = freeRect.X + placedRect.Width;
            right.Y = freeRect.Y;
            right.Width = freeRect.Width - placedRect.Width;

            if (splitHorizontal)
            {
                bottom.Width = freeRect.Width;
                right.Height = placedRect.Height;
            }
            else // Split vertically
            {
                bottom.Width = placedRect.Width;
                right.Height = freeRect.Height;
            }

            // Add the new rectangles into the free rectangle pool if they weren't
            // degenerate.
            if (bottom.Width > 0 && bottom.Height > 0)
                AddFreeRectangle(bottom);
            if (right.Width > 0 && right.Height > 0)
                AddFreeRectangle(right);
        }

        private void AddFreeRectangle(Rectangle freeRectangle)
        {
            if (freeRectangle.X < 0 || freeRectangle.Y < 0)
            {
                throw new ArithmeticException(
                    $"add free rectangle failed: negative position, rectangle: {freeRectangle}");
            }
            if (freeRectangle.X + freeRectangle.Width > _binWidth ||
                freeRectangle.Y + freeRectangle.Height > _binHeight)
            {
                throw new ArithmeticException(
                    $"add free rectangle failed: out of area, rectangle: {freeRectangle}");
            }
            _freeRectangles.Add(freeRectangle);
        }
    }
}

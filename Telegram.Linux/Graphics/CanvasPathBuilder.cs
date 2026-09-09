//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using SkiaSharp;

namespace Microsoft.Graphics.Canvas.Geometry
{
    /// <summary>
    /// Win2D's path builder, over a SkiaSharp path. <see cref="CanvasGeometry.CreatePath"/> takes
    /// the path over and closes the builder, as it does in Win2D.
    /// </summary>
    public sealed partial class CanvasPathBuilder : IDisposable
    {
        // Direct2D fills with the alternate (even-odd) rule unless told otherwise, and the shapes
        // drawn through here were designed against that.
        private SKPath _path = new()
        {
            FillType = SKPathFillType.EvenOdd
        };

        public CanvasPathBuilder(ICanvasResourceCreator resourceCreator)
        {
        }

        private SKPath Path => _path ?? throw new ObjectDisposedException(nameof(CanvasPathBuilder));

        public void BeginFigure(Vector2 startPoint)
        {
            BeginFigure(startPoint.X, startPoint.Y, CanvasFigureFill.Default);
        }

        public void BeginFigure(Vector2 startPoint, CanvasFigureFill figureFill)
        {
            BeginFigure(startPoint.X, startPoint.Y, figureFill);
        }

        public void BeginFigure(float startX, float startY)
        {
            BeginFigure(startX, startY, CanvasFigureFill.Default);
        }

        public void BeginFigure(float startX, float startY, CanvasFigureFill figureFill)
        {
            // A stroke-only figure (DoesNotAffectFills) has no Skia counterpart; it fills.
            Path.MoveTo(startX, startY);
        }

        public void EndFigure(CanvasFigureLoop figureLoop)
        {
            if (figureLoop == CanvasFigureLoop.Closed)
            {
                Path.Close();
            }
        }

        public void AddLine(Vector2 endPoint)
        {
            Path.LineTo(endPoint.X, endPoint.Y);
        }

        public void AddLine(float x, float y)
        {
            Path.LineTo(x, y);
        }

        public void AddCubicBezier(Vector2 controlPoint1, Vector2 controlPoint2, Vector2 endPoint)
        {
            Path.CubicTo(controlPoint1.X, controlPoint1.Y, controlPoint2.X, controlPoint2.Y, endPoint.X, endPoint.Y);
        }

        public void AddQuadraticBezier(Vector2 controlPoint, Vector2 endPoint)
        {
            Path.QuadTo(controlPoint.X, controlPoint.Y, endPoint.X, endPoint.Y);
        }

        public void AddArc(Vector2 endPoint, float radiusX, float radiusY, float rotationAngle, CanvasSweepDirection sweepDirection, CanvasArcSize arcSize)
        {
            Path.ArcTo(new SKPoint(radiusX, radiusY), ToDegrees(rotationAngle), arcSize.ToSkia(), sweepDirection.ToSkia(), endPoint.ToSkia());
        }

        public void AddArc(Vector2 centerPoint, float radiusX, float radiusY, float startAngle, float sweepAngle)
        {
            // Both Win2D and Skia join the current point to the start of the arc with a line.
            var oval = SKRect.Create(centerPoint.X - radiusX, centerPoint.Y - radiusY, radiusX * 2, radiusY * 2);
            Path.ArcTo(oval, ToDegrees(startAngle), ToDegrees(sweepAngle), false);
        }

        public void AddGeometry(CanvasGeometry geometry)
        {
            if (geometry != null)
            {
                Path.AddPath(geometry.Path);
            }
        }

        public void SetFilledRegionDetermination(CanvasFilledRegionDetermination filledRegionDetermination)
        {
            Path.FillType = filledRegionDetermination.ToSkia();
        }

        public void SetSegmentOptions(CanvasFigureSegmentOptions figureSegmentOptions)
        {
            // Stroking hints; nothing built here is ever stroked.
        }

        internal SKPath Detach()
        {
            var path = Path;
            _path = null;
            return path;
        }

        public void Dispose()
        {
            _path?.Dispose();
            _path = null;
        }

        private static float ToDegrees(float radians)
        {
            return radians * 180f / MathF.PI;
        }
    }
}

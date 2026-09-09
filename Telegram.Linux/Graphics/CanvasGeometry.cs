//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;
using Windows.Foundation;
using Windows.Graphics;

namespace Microsoft.Graphics.Canvas.Geometry
{
    public enum CanvasFigureLoop
    {
        Open = 0,
        Closed = 1
    }

    public enum CanvasFigureFill
    {
        Default = 0,
        DoesNotAffectFills = 1
    }

    public enum CanvasFigureSegmentOptions
    {
        None = 0,
        ForceUnstroked = 1,
        ForceRoundLineJoin = 2
    }

    public enum CanvasFilledRegionDetermination
    {
        Alternate = 0,
        Winding = 1
    }

    public enum CanvasSweepDirection
    {
        CounterClockwise = 0,
        Clockwise = 1
    }

    public enum CanvasArcSize
    {
        Small = 0,
        Large = 1
    }

    public enum CanvasGeometryCombine
    {
        Union = 0,
        Intersect = 1,
        Xor = 2,
        Exclude = 3
    }

    /// <summary>
    /// Win2D's immutable geometry, over a SkiaSharp path.
    /// </summary>
    /// <remarks>
    /// Implements <see cref="IGeometrySource2D"/> so that <c>new CompositionPath(geometry)</c>
    /// compiles unchanged; the <see cref="Path"/> is what the compositor actually receives, see
    /// CompositionPath.cs. Disposing only drops the reference: a CompositionPath built from this
    /// geometry shares the same SKPath and keeps it alive, the way a Direct2D geometry outlives
    /// the Win2D wrapper that created it.
    /// </remarks>
    // Fully qualified on purpose: Uno.UI also ships a public Microsoft.Graphics.IGeometrySource2D,
    // and from inside Microsoft.Graphics.Canvas.Geometry that enclosing-namespace type shadows the
    // `using Windows.Graphics` one. The compositor (and CompositionPath) want Windows.Graphics'.
    public sealed partial class CanvasGeometry : global::Windows.Graphics.IGeometrySource2D, IDisposable
    {
        private SKPath _path;

        internal CanvasGeometry(SKPath path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        /// <summary>
        /// The geometry. Read-only by convention, as a geometry is immutable in Win2D: copy it
        /// before changing it, the compositor may be sharing it.
        /// </summary>
        public SKPath Path => _path ?? throw new ObjectDisposedException(nameof(CanvasGeometry));

        public CanvasDevice Device => CanvasDevice.GetSharedDevice();

        public static CanvasGeometry CreatePath(CanvasPathBuilder pathBuilder)
        {
            return new CanvasGeometry(pathBuilder.Detach());
        }

        public static CanvasGeometry CreateRectangle(ICanvasResourceCreator resourceCreator, Rect rect)
        {
            return CreateRectangle(resourceCreator, (float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
        }

        public static CanvasGeometry CreateRectangle(ICanvasResourceCreator resourceCreator, float x, float y, float w, float h)
        {
            var path = new SKPath();
            path.AddRect(SKRect.Create(x, y, w, h));
            return new CanvasGeometry(path);
        }

        public static CanvasGeometry CreateRoundedRectangle(ICanvasResourceCreator resourceCreator, Rect rect, float radiusX, float radiusY)
        {
            return CreateRoundedRectangle(resourceCreator, (float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height, radiusX, radiusY);
        }

        public static CanvasGeometry CreateRoundedRectangle(ICanvasResourceCreator resourceCreator, float x, float y, float w, float h, float radiusX, float radiusY)
        {
            var path = new SKPath();
            path.AddRoundRect(SKRect.Create(x, y, w, h), radiusX, radiusY);
            return new CanvasGeometry(path);
        }

        public static CanvasGeometry CreateEllipse(ICanvasResourceCreator resourceCreator, Vector2 centerPoint, float radiusX, float radiusY)
        {
            return CreateEllipse(resourceCreator, centerPoint.X, centerPoint.Y, radiusX, radiusY);
        }

        public static CanvasGeometry CreateEllipse(ICanvasResourceCreator resourceCreator, float x, float y, float radiusX, float radiusY)
        {
            var path = new SKPath();
            path.AddOval(SKRect.Create(x - radiusX, y - radiusY, radiusX * 2, radiusY * 2));
            return new CanvasGeometry(path);
        }

        public static CanvasGeometry CreateCircle(ICanvasResourceCreator resourceCreator, Vector2 centerPoint, float radius)
        {
            return CreateEllipse(resourceCreator, centerPoint.X, centerPoint.Y, radius, radius);
        }

        public static CanvasGeometry CreateCircle(ICanvasResourceCreator resourceCreator, float x, float y, float radius)
        {
            return CreateEllipse(resourceCreator, x, y, radius, radius);
        }

        public static CanvasGeometry CreatePolygon(ICanvasResourceCreator resourceCreator, Vector2[] points)
        {
            var path = new SKPath();

            if (points?.Length > 0)
            {
                path.MoveTo(points[0].X, points[0].Y);

                for (int i = 1; i < points.Length; i++)
                {
                    path.LineTo(points[i].X, points[i].Y);
                }

                path.Close();
            }

            return new CanvasGeometry(path);
        }

        public static CanvasGeometry CreateGroup(ICanvasResourceCreator resourceCreator, CanvasGeometry[] geometries)
        {
            return CreateGroup(resourceCreator, geometries, CanvasFilledRegionDetermination.Alternate);
        }

        public static CanvasGeometry CreateGroup(ICanvasResourceCreator resourceCreator, CanvasGeometry[] geometries, CanvasFilledRegionDetermination filledRegionDetermination)
        {
            var fillType = filledRegionDetermination.ToSkia();
            var sources = new List<SKPath>();

            if (geometries != null)
            {
                foreach (var geometry in geometries)
                {
                    if (geometry?._path != null)
                    {
                        sources.Add(geometry._path);
                    }
                }
            }

            // AddPath copies contours but not the source's fill rule, and SKPath.Op hands back
            // EvenOdd paths: dropping one of those into a Winding group fills its holes in. That
            // is how the locator squares of a QR code came out solid - a ring built by
            // subtracting one rounded rectangle from another lost its hole, and no scanner could
            // find the code. Where the rules already agree the contours can just be appended;
            // where they do not, folding with a union resolves each path under its own rule.
            var mixed = sources.Exists(x => x.FillType != fillType);

            if (mixed && sources.Count > 1)
            {
                var combined = sources[0];

                for (int i = 1; i < sources.Count; i++)
                {
                    combined = combined.Op(sources[i], SKPathOp.Union) ?? combined;
                }

                return new CanvasGeometry(combined);
            }

            var path = new SKPath
            {
                FillType = mixed && sources.Count == 1 ? sources[0].FillType : fillType
            };

            foreach (var source in sources)
            {
                path.AddPath(source);
            }

            return new CanvasGeometry(path);
        }

        public CanvasGeometry CombineWith(CanvasGeometry otherGeometry, Matrix3x2 otherGeometryTransform, CanvasGeometryCombine combine)
        {
            var other = otherGeometry.Path;
            SKPath transformed = null;

            if (!otherGeometryTransform.IsIdentity)
            {
                transformed = new SKPath();
                other.Transform(otherGeometryTransform.ToSkia(), transformed);
                other = transformed;
            }

            var result = Path.Op(other, combine.ToSkia()) ?? new SKPath();
            transformed?.Dispose();

            return new CanvasGeometry(result);
        }

        public CanvasGeometry Transform(Matrix3x2 transform)
        {
            var path = new SKPath();
            Path.Transform(transform.ToSkia(), path);
            return new CanvasGeometry(path);
        }

        public Rect ComputeBounds()
        {
            return Path.TightBounds.ToRect();
        }

        public Rect ComputeBounds(Matrix3x2 transform)
        {
            using var path = new SKPath();
            Path.Transform(transform.ToSkia(), path);
            return path.TightBounds.ToRect();
        }

        public bool FillContainsPoint(Vector2 point)
        {
            return Path.Contains(point.X, point.Y);
        }

        public bool FillContainsPoint(float x, float y)
        {
            return Path.Contains(x, y);
        }

        public void Dispose()
        {
            _path = null;
        }
    }

    internal static class CanvasSkiaExtensions
    {
        public static SKMatrix ToSkia(this Matrix3x2 matrix)
        {
            return new SKMatrix(
                matrix.M11, matrix.M21, matrix.M31,
                matrix.M12, matrix.M22, matrix.M32,
                0, 0, 1);
        }

        public static SKPoint ToSkia(this Vector2 point)
        {
            return new SKPoint(point.X, point.Y);
        }

        public static Rect ToRect(this SKRect rect)
        {
            return new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        public static SKPathFillType ToSkia(this CanvasFilledRegionDetermination filledRegionDetermination)
        {
            return filledRegionDetermination == CanvasFilledRegionDetermination.Winding
                ? SKPathFillType.Winding
                : SKPathFillType.EvenOdd;
        }

        public static SKPathOp ToSkia(this CanvasGeometryCombine combine)
        {
            return combine switch
            {
                CanvasGeometryCombine.Union => SKPathOp.Union,
                CanvasGeometryCombine.Intersect => SKPathOp.Intersect,
                CanvasGeometryCombine.Xor => SKPathOp.Xor,
                _ => SKPathOp.Difference
            };
        }

        public static SKPathArcSize ToSkia(this CanvasArcSize arcSize)
        {
            return arcSize == CanvasArcSize.Large
                ? SKPathArcSize.Large
                : SKPathArcSize.Small;
        }

        public static SKPathDirection ToSkia(this CanvasSweepDirection sweepDirection)
        {
            return sweepDirection == CanvasSweepDirection.Clockwise
                ? SKPathDirection.Clockwise
                : SKPathDirection.CounterClockwise;
        }
    }
}

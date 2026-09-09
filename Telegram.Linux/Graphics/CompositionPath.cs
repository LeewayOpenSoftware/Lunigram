//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Graphics.Canvas.Geometry;
using SkiaSharp;
using Windows.Graphics;

namespace Microsoft.UI.Composition
{
    /// <summary>
    /// Stands in for Uno's CompositionPath. Same name and namespace, so every
    /// <c>new CompositionPath(geometry)</c> in the shared code binds here: a type declared in the
    /// compilation takes precedence over the imported one.
    /// </summary>
    /// <remarks>
    /// Uno's CompositionPath is public, but on Skia its geometry is only ever drawn when the
    /// source is Uno's internal SkiaGeometrySource2D (or one of its internal Direct2D interop
    /// shapes): any other <see cref="IGeometrySource2D"/> throws at render time. This class keeps
    /// the SKPath and, at the moment it reaches the compositor, wraps a copy of it in that
    /// internal type through reflection (<see cref="UnoCompositionInterop"/>). That is also why
    /// the compositor is reached through <see cref="SkiaCompositionPathExtensions"/>, whose
    /// overloads take this type, rather than through Uno's own CreatePathGeometry(CompositionPath).
    /// </remarks>
    // Windows.Graphics.IGeometrySource2D is written out in full: Uno.UI also exports a public
    // Microsoft.Graphics.IGeometrySource2D, which shadows the `using` for any type declared under
    // a Microsoft.Graphics.* namespace (see CanvasGeometry.cs); keeping both sides explicit is
    // what makes the `CanvasGeometry geometry` pattern below hold.
    public sealed partial class CompositionPath : global::Windows.Graphics.IGeometrySource2D
    {
        private readonly SKPath _path;

        public CompositionPath(global::Windows.Graphics.IGeometrySource2D source)
        {
            Source = source;
            _path = source switch
            {
                CanvasGeometry geometry => geometry.Path,
                CompositionPath path => path._path,
                _ => null
            };
        }

        public global::Windows.Graphics.IGeometrySource2D Source { get; }

        /// <summary>
        /// The path, or null for an empty one. Shared with the geometry it was built from, so copy
        /// it before changing it.
        /// </summary>
        public SKPath Path => _path;
    }

    public static class SkiaCompositionPathExtensions
    {
        public static CompositionPathGeometry CreatePathGeometry(this Compositor compositor, CompositionPath path)
        {
            return UnoCompositionInterop.CreatePathGeometry(compositor, path);
        }

        /// <summary>
        /// What <c>geometry.Path = path</c> would be; the property itself is typed on Uno's class.
        /// </summary>
        public static void SetPath(this CompositionPathGeometry geometry, CompositionPath path)
        {
            UnoCompositionInterop.SetPath(geometry, path);
        }

        public static void InsertKeyFrame(this PathKeyFrameAnimation animation, float normalizedProgressKey, CompositionPath path)
        {
            UnoCompositionInterop.InsertKeyFrame(animation, normalizedProgressKey, path, null);
        }

        public static void InsertKeyFrame(this PathKeyFrameAnimation animation, float normalizedProgressKey, CompositionPath path, CompositionEasingFunction easingFunction)
        {
            UnoCompositionInterop.InsertKeyFrame(animation, normalizedProgressKey, path, easingFunction);
        }
    }

    /// <summary>
    /// The bridge into Uno.UI.Composition's internals: SkiaGeometrySource2D(SKPath) is the only
    /// path source the Skia compositor draws, and Uno's CompositionPath is the only thing its
    /// CreatePathGeometry, CompositionPathGeometry.Path and PathKeyFrameAnimation accept. Both
    /// are reached by name, on public constructors and members, so no access checks are bypassed;
    /// if a future Uno renames them this fails loudly at the first path rather than drawing nothing.
    /// </summary>
    internal static class UnoCompositionInterop
    {
        private static readonly ConstructorInfo _compositionPathCtor;
        private static readonly ConstructorInfo _skiaSourceCtor;
        private static readonly MethodInfo _createPathGeometry;
        private static readonly MethodInfo _setPath;
        private static readonly MethodInfo _insertKeyFrame;
        private static readonly MethodInfo _insertKeyFrameEasing;

        static UnoCompositionInterop()
        {
            var assembly = typeof(Compositor).Assembly;

            var compositionPath = assembly.GetType("Microsoft.UI.Composition.CompositionPath");
            var skiaSource = assembly.GetType("Microsoft.UI.Composition.SkiaGeometrySource2D");

            _compositionPathCtor = compositionPath?.GetConstructor(new[] { typeof(global::Windows.Graphics.IGeometrySource2D) });
            _skiaSourceCtor = skiaSource?.GetConstructor(new[] { typeof(SKPath) });

            if (compositionPath != null)
            {
                _createPathGeometry = typeof(Compositor).GetMethod(nameof(Compositor.CreatePathGeometry), new[] { compositionPath });
                _setPath = typeof(CompositionPathGeometry).GetProperty(nameof(CompositionPathGeometry.Path))?.SetMethod;
                _insertKeyFrame = typeof(PathKeyFrameAnimation).GetMethod(nameof(PathKeyFrameAnimation.InsertKeyFrame), new[] { typeof(float), compositionPath });
                _insertKeyFrameEasing = typeof(PathKeyFrameAnimation).GetMethod(nameof(PathKeyFrameAnimation.InsertKeyFrame), new[] { typeof(float), compositionPath, typeof(CompositionEasingFunction) });
            }
        }

        public static CompositionPathGeometry CreatePathGeometry(Compositor compositor, CompositionPath path)
        {
            return (CompositionPathGeometry)Invoke(_createPathGeometry, compositor, ToNative(path));
        }

        public static void SetPath(CompositionPathGeometry geometry, CompositionPath path)
        {
            Invoke(_setPath, geometry, ToNative(path));
        }

        public static void InsertKeyFrame(PathKeyFrameAnimation animation, float normalizedProgressKey, CompositionPath path, CompositionEasingFunction easingFunction)
        {
            if (easingFunction != null)
            {
                Invoke(_insertKeyFrameEasing, animation, normalizedProgressKey, ToNative(path), easingFunction);
            }
            else
            {
                Invoke(_insertKeyFrame, animation, normalizedProgressKey, ToNative(path));
            }
        }

        // Uno's CompositionPath over a SkiaGeometrySource2D. The geometry disposes the source it
        // is handed together with itself, so every call gets its own copy of the path and the
        // CompositionPath (and the CanvasGeometry behind it) can be reused or dropped freely.
        // A null path becomes an empty one: a geometry without a path is something Uno's
        // geometric clip refuses to render, while an empty path simply draws nothing.
        private static object ToNative(CompositionPath path)
        {
            if (_compositionPathCtor == null || _skiaSourceCtor == null)
            {
                throw Unsupported();
            }

            var copy = path?.Path != null ? new SKPath(path.Path) : new SKPath();
            var source = _skiaSourceCtor.Invoke(new object[] { copy });

            return _compositionPathCtor.Invoke(new[] { source });
        }

        private static object Invoke(MethodInfo method, object target, params object[] arguments)
        {
            if (method == null)
            {
                throw Unsupported();
            }

            try
            {
                return method.Invoke(target, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static Exception Unsupported()
        {
            return new NotSupportedException("Uno.UI.Composition no longer exposes CompositionPath(IGeometrySource2D), SkiaGeometrySource2D(SKPath) or the members that take them; Telegram.Linux/Graphics/CompositionPath.cs must be updated for this Uno version.");
        }
    }
}

//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;

namespace Microsoft.Graphics.Canvas
{
    // Win2D's resource creator is the Direct2D device every geometry is allocated on. The
    // geometries here are plain SkiaSharp paths with no device affinity, so this only exists for
    // the signatures that take one and is never consulted.
    public interface ICanvasResourceCreator
    {
        CanvasDevice Device { get; }
    }

    public sealed partial class CanvasDevice : ICanvasResourceCreator, IDisposable
    {
        private static readonly Lazy<CanvasDevice> _shared = new(() => new CanvasDevice());

        public CanvasDevice()
        {
        }

        public CanvasDevice(bool forceSoftwareRenderer)
        {
            ForceSoftwareRenderer = forceSoftwareRenderer;
        }

        public CanvasDevice Device => this;

        public bool ForceSoftwareRenderer { get; }

        public static CanvasDevice GetSharedDevice()
        {
            return _shared.Value;
        }

        public static CanvasDevice GetSharedDevice(bool forceSoftwareRenderer)
        {
            return _shared.Value;
        }

        public bool IsDeviceLost(int hresult)
        {
            return false;
        }

        public void Dispose()
        {
        }
    }

    public enum CanvasComposite
    {
        SourceOver = 0,
        DestinationOver = 1,
        SourceIn = 2,
        DestinationIn = 3,
        SourceOut = 4,
        DestinationOut = 5,
        SourceAtop = 6,
        DestinationAtop = 7,
        Xor = 8,
        Add = 9,
        Copy = 10,
        BoundedCopy = 11,
        MaskInvert = 12
    }

    public enum CanvasEdgeBehavior
    {
        Clamp = 0,
        Wrap = 1,
        Mirror = 2
    }

    public enum CanvasAlphaMode
    {
        Premultiplied = 0,
        Straight = 1,
        Ignore = 2
    }

    public enum CanvasBufferPrecision
    {
        Precision8UIntNormalized = 0,
        Precision8UIntNormalizedSrgb = 1,
        Precision16UIntNormalized = 2,
        Precision16Float = 3,
        Precision32Float = 4
    }
}

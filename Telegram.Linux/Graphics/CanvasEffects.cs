//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Graphics.Effects;
using Windows.Graphics.Effects.Interop;
using Windows.UI;

namespace Microsoft.Graphics.Canvas.Effects
{
    public enum EffectBorderMode
    {
        Soft = 0,
        Hard = 1
    }

    public enum EffectOptimization
    {
        Speed = 0,
        Balanced = 1,
        Quality = 2
    }

    public enum BlendEffectMode
    {
        Multiply = 0,
        Screen = 1,
        Darken = 2,
        Lighten = 3,
        Dissolve = 4,
        ColorBurn = 5,
        LinearBurn = 6,
        DarkerColor = 7,
        LighterColor = 8,
        ColorDodge = 9,
        LinearDodge = 10,
        Overlay = 11,
        SoftLight = 12,
        HardLight = 13,
        VividLight = 14,
        LinearLight = 15,
        PinLight = 16,
        HardMix = 17,
        Difference = 18,
        Exclusion = 19,
        Hue = 20,
        Saturation = 21,
        Color = 22,
        Luminosity = 23,
        Subtract = 24,
        Division = 25
    }

    public struct Matrix5x4
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;
        public float M51, M52, M53, M54;

        public static Matrix5x4 Identity => new()
        {
            M11 = 1,
            M22 = 1,
            M33 = 1,
            M44 = 1
        };

        internal float[] ToArray()
        {
            return new float[20]
            {
                M11, M12, M13, M14,
                M21, M22, M23, M24,
                M31, M32, M33, M34,
                M41, M42, M43, M44,
                M51, M52, M53, M54
            };
        }
    }

    public interface ICanvasEffect : IGraphicsEffect, IGraphicsEffectSource, IGraphicsEffectD2D1Interop, IDisposable
    {
        CanvasBufferPrecision? BufferPrecision { get; set; }

        bool CacheOutput { get; set; }
    }

    /// <summary>
    /// A Win2D effect described the way Uno's Skia compositor reads one: through
    /// <see cref="IGraphicsEffectD2D1Interop"/>, matched by the Direct2D effect CLSID, with the
    /// properties and sources indexed in the order Direct2D defines them. Enum properties are
    /// handed over as uint, as Direct2D would box them.
    /// </summary>
    public abstract partial class CanvasEffect : ICanvasEffect
    {
        private readonly Guid _effectId;
        private readonly (string Name, GraphicsEffectPropertyMapping Mapping)[] _properties;

        protected CanvasEffect(string name, string effectId, params (string Name, GraphicsEffectPropertyMapping Mapping)[] properties)
        {
            Name = name;
            _effectId = new Guid(effectId);
            _properties = properties;
        }

        public string Name { get; set; }

        public CanvasBufferPrecision? BufferPrecision { get; set; }

        public bool CacheOutput { get; set; }

        public Guid GetEffectId()
        {
            return _effectId;
        }

        public void GetNamedPropertyMapping(string name, out uint index, out GraphicsEffectPropertyMapping mapping)
        {
            for (int i = 0; i < _properties.Length; i++)
            {
                if (_properties[i].Name == name)
                {
                    index = (uint)i;
                    mapping = _properties[i].Mapping;
                    return;
                }
            }

            index = 0xFF;
            mapping = (GraphicsEffectPropertyMapping)0xFF;
        }

        public uint GetPropertyCount()
        {
            return (uint)_properties.Length;
        }

        public abstract object GetProperty(uint index);

        public abstract uint GetSourceCount();

        public abstract IGraphicsEffectSource GetSource(uint index);

        public void Dispose()
        {
        }
    }

    public sealed partial class GaussianBlurEffect : CanvasEffect
    {
        public GaussianBlurEffect()
            : base("GaussianBlurEffect", "1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5",
                  (nameof(BlurAmount), GraphicsEffectPropertyMapping.Direct),
                  (nameof(Optimization), GraphicsEffectPropertyMapping.Direct),
                  (nameof(BorderMode), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public IGraphicsEffectSource Source { get; set; }

        public float BlurAmount { get; set; } = 3.0f;

        public EffectOptimization Optimization { get; set; } = EffectOptimization.Balanced;

        public EffectBorderMode BorderMode { get; set; } = EffectBorderMode.Soft;

        public override object GetProperty(uint index) => index switch
        {
            0 => BlurAmount,
            1 => (uint)Optimization,
            2 => (uint)BorderMode,
            _ => null
        };

        public override uint GetSourceCount() => 1;

        public override IGraphicsEffectSource GetSource(uint index) => index == 0 ? Source : null;
    }

    public sealed partial class SaturationEffect : CanvasEffect
    {
        public SaturationEffect()
            : base("SaturationEffect", "5CB2D9CF-327D-459F-A0CE-40C0B2086BF7",
                  (nameof(Saturation), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public IGraphicsEffectSource Source { get; set; }

        public float Saturation { get; set; } = 0.5f;

        public override object GetProperty(uint index) => index switch
        {
            0 => Saturation,
            _ => null
        };

        public override uint GetSourceCount() => 1;

        public override IGraphicsEffectSource GetSource(uint index) => index == 0 ? Source : null;
    }

    public sealed partial class ColorSourceEffect : CanvasEffect
    {
        public ColorSourceEffect()
            : base("ColorSourceEffect", "61C23C20-AE69-4D8E-94CF-50078DF638F2",
                  (nameof(Color), GraphicsEffectPropertyMapping.ColorToVector4))
        {
        }

        public Color Color { get; set; } = Color.FromArgb(255, 255, 255, 255);

        public Vector4 ColorHdr
        {
            get => new(Color.R / 255f, Color.G / 255f, Color.B / 255f, Color.A / 255f);
            set => Color = Color.FromArgb((byte)(value.W * 255), (byte)(value.X * 255), (byte)(value.Y * 255), (byte)(value.Z * 255));
        }

        public override object GetProperty(uint index) => index switch
        {
            0 => Color,
            _ => null
        };

        public override uint GetSourceCount() => 0;

        public override IGraphicsEffectSource GetSource(uint index) => null;
    }

    public sealed partial class CompositeEffect : CanvasEffect
    {
        public CompositeEffect()
            : base("CompositeEffect", "48FC9F51-F6AC-48F1-8B58-3B28AC46F76D",
                  (nameof(Mode), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public CanvasComposite Mode { get; set; } = CanvasComposite.SourceOver;

        public IList<IGraphicsEffectSource> Sources { get; } = new List<IGraphicsEffectSource>();

        public override object GetProperty(uint index) => index switch
        {
            0 => (uint)Mode,
            _ => null
        };

        public override uint GetSourceCount() => (uint)Sources.Count;

        public override IGraphicsEffectSource GetSource(uint index) => index < Sources.Count ? Sources[(int)index] : null;
    }

    public sealed partial class TintEffect : CanvasEffect
    {
        public TintEffect()
            : base("TintEffect", "36312B17-F7DD-4014-915D-FFCA768CF211",
                  (nameof(Color), GraphicsEffectPropertyMapping.ColorToVector4),
                  (nameof(ClampOutput), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public static bool IsSupported => true;

        public IGraphicsEffectSource Source { get; set; }

        public Color Color { get; set; } = Color.FromArgb(255, 255, 255, 255);

        public bool ClampOutput { get; set; }

        public override object GetProperty(uint index) => index switch
        {
            0 => Color,
            1 => ClampOutput,
            _ => null
        };

        public override uint GetSourceCount() => 1;

        public override IGraphicsEffectSource GetSource(uint index) => index == 0 ? Source : null;
    }

    public sealed partial class ColorMatrixEffect : CanvasEffect
    {
        public ColorMatrixEffect()
            : base("ColorMatrixEffect", "921F03D6-641C-47DF-852D-B4BB6153AE11",
                  (nameof(ColorMatrix), GraphicsEffectPropertyMapping.Direct),
                  (nameof(AlphaMode), GraphicsEffectPropertyMapping.ColorMatrixAlphaMode),
                  (nameof(ClampOutput), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public IGraphicsEffectSource Source { get; set; }

        public Matrix5x4 ColorMatrix { get; set; } = Matrix5x4.Identity;

        public CanvasAlphaMode AlphaMode { get; set; } = CanvasAlphaMode.Premultiplied;

        public bool ClampOutput { get; set; }

        public override object GetProperty(uint index) => index switch
        {
            0 => ColorMatrix.ToArray(),
            1 => (uint)AlphaMode,
            2 => ClampOutput,
            _ => null
        };

        public override uint GetSourceCount() => 1;

        public override IGraphicsEffectSource GetSource(uint index) => index == 0 ? Source : null;
    }

    public sealed partial class OpacityEffect : CanvasEffect
    {
        public OpacityEffect()
            : base("OpacityEffect", "811D79A4-DE28-4454-8094-C64685F8BD4C",
                  (nameof(Opacity), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public static bool IsSupported => true;

        public IGraphicsEffectSource Source { get; set; }

        public float Opacity { get; set; } = 1.0f;

        public override object GetProperty(uint index) => index switch
        {
            0 => Opacity,
            _ => null
        };

        public override uint GetSourceCount() => 1;

        public override IGraphicsEffectSource GetSource(uint index) => index == 0 ? Source : null;
    }

    public sealed partial class BlendEffect : CanvasEffect
    {
        public BlendEffect()
            : base("BlendEffect", "81C5B77B-13F8-4CDD-AD20-C890547AC65D",
                  (nameof(Mode), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public BlendEffectMode Mode { get; set; } = BlendEffectMode.Multiply;

        public IGraphicsEffectSource Background { get; set; }

        public IGraphicsEffectSource Foreground { get; set; }

        public override object GetProperty(uint index) => index switch
        {
            0 => (uint)Mode,
            _ => null
        };

        public override uint GetSourceCount() => 2;

        public override IGraphicsEffectSource GetSource(uint index) => index switch
        {
            0 => Background,
            1 => Foreground,
            _ => null
        };
    }

    public sealed partial class BorderEffect : CanvasEffect
    {
        public BorderEffect()
            : base("BorderEffect", "2A2D49C0-4ACF-43C7-8C6A-7C4A27874D27",
                  (nameof(ExtendX), GraphicsEffectPropertyMapping.Direct),
                  (nameof(ExtendY), GraphicsEffectPropertyMapping.Direct))
        {
        }

        public IGraphicsEffectSource Source { get; set; }

        public CanvasEdgeBehavior ExtendX { get; set; } = CanvasEdgeBehavior.Clamp;

        public CanvasEdgeBehavior ExtendY { get; set; } = CanvasEdgeBehavior.Clamp;

        public override object GetProperty(uint index) => index switch
        {
            0 => (uint)ExtendX,
            1 => (uint)ExtendY,
            _ => null
        };

        public override uint GetSourceCount() => 1;

        public override IGraphicsEffectSource GetSource(uint index) => index == 0 ? Source : null;
    }

    public sealed partial class AlphaMaskEffect : CanvasEffect
    {
        public AlphaMaskEffect()
            : base("AlphaMaskEffect", "C80ECFF0-3FD5-4F05-8328-C5D1724B4F0A")
        {
        }

        public static bool IsSupported => true;

        public IGraphicsEffectSource Source { get; set; }

        public IGraphicsEffectSource AlphaMask { get; set; }

        public override object GetProperty(uint index) => null;

        public override uint GetSourceCount() => 2;

        public override IGraphicsEffectSource GetSource(uint index) => index switch
        {
            0 => Source,
            1 => AlphaMask,
            _ => null
        };
    }
}

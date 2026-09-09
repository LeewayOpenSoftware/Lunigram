//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Telegram.Common;
using Telegram.Native;
using Telegram.Services;
using Telegram.Td.Api;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Controls.Chats
{
    /// <summary>
    /// The chat wallpaper, painted straight into the composition tree with Skia.
    /// </summary>
    /// <remarks>
    /// <para>This is what replaces <c>ChatBackgroundPresenter</c> + <c>ChatBackgroundBrush</c> on
    /// Linux. Those two are a graph of composition effects over a
    /// <c>CompositionDrawingSurface</c> that Direct2D fills from the SVG, and Uno has no
    /// <c>CompositionGraphicsDevice</c> to create that surface with -- so there is nothing to hang
    /// the graph on, and no amount of effect support would help. The pixels are the same either
    /// way (see the note on ChatBackgroundRenderer for why the effect algebra collapses to "black
    /// over the gradient"), so the wallpaper is drawn rather than composed.</para>
    /// <para><see cref="SKCanvasElement"/> (Uno.WinUI.Graphics2DSK, already referenced by the
    /// SkiaRenderer feature) is the seam: a FrameworkElement whose visual hands out a real
    /// <see cref="SKCanvas"/>. Two consequences worth knowing: the canvas arrives with the DPI
    /// transform already applied, so <c>canvas.TotalMatrix.ScaleX</c> is the honest rasterization
    /// scale -- the one <c>XamlRoot.RasterizationScale</c> lies about (PORTING.md section 6) -- and
    /// <see cref="SKCanvasElement.RenderOverride"/> runs on the render path, so it must not do the
    /// ~100 ms of work that rasterizing 745 doodle outlines costs. Hence
    /// <see cref="ChatBackgroundRenderer.Surface"/>, which keeps the composed wallpaper as an image
    /// and blits it (measured 3-10 ms for a full 2736x1552 pane in
    /// unigram-linux/spikes/ChatBackgroundSpike).</para>
    /// </remarks>
    public partial class ChatBackgroundCanvas : Grid
    {
        private readonly ChatBackgroundRenderer.Surface _surface = new();
        private readonly ChatBackgroundRenderer.Options _options = new();
        private readonly Painter _painter;

        private ChatBackgroundRenderer.Pattern _pattern;
        private string _patternPath;
        private long? _emptyFillReportedFor = long.MinValue;

        private SKBitmap _wallpaper;
        private SKImage _wallpaperImage;
        private string _wallpaperPath;

        private IClientService _clientService;
        private Background _background;
        private long _fileToken;
        private int _loading;

        public ChatBackgroundCanvas()
        {
            // A Grid around the canvas rather than being the canvas: SKCanvasElement's constructor
            // THROWS PlatformNotSupportedException when the host has not registered a
            // SKCanvasVisualBaseFactory, and this control is built from MasterDetailView's
            // template, where an exception costs the whole page. A missing wallpaper is a degrade;
            // a missing MainPage is not.
            try
            {
                if (SKCanvasElement.IsSupportedOnCurrentPlatform())
                {
                    _painter = new Painter(RenderCore);
                    Children.Add(_painter);
                }
                else
                {
                    Logger.Error("SKCanvasElement is not available on this host: the chat wallpaper will not be drawn.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }
        }

        public void UpdateSource(IClientService clientService, Background background)
        {
            UpdateManager.Unsubscribe(this, ref _fileToken);

            _clientService = clientService;
            _background = background;

            if (background == null)
            {
                return;
            }

            if (background.Type is BackgroundTypeFill typeFill)
            {
                SetPattern(null, null);
                SetWallpaper(null);
                SetFill(typeFill.Fill);

                _options.Intensity = 1;
                _options.IsNegative = false;

                Redraw();
            }
            else if (background.Type is BackgroundTypePattern typePattern)
            {
                SetWallpaper(null);
                SetFill(typePattern.Fill);

                _options.Intensity = typePattern.Intensity / 100f;
                _options.IsNegative = typePattern.IsInverted;

                Redraw();

                var file = background.Document?.DocumentValue;
                if (file == null)
                {
                    return;
                }

                if (background.Document.MimeType != "application/x-tgwallpattern")
                {
                    // A raster pattern (a .png the server serves for old wallpapers): out of scope
                    // for now, the fill alone is drawn.
                    SetPattern(null, null);
                    return;
                }

                if (file.Local.IsDownloadingCompleted)
                {
                    LoadPatternAsync(file.Id, file.Local.Path);
                }
                else
                {
                    SetPattern(null, null);
                    Download(background, file);
                }
            }
            else if (background.Type is BackgroundTypeWallpaper)
            {
                SetPattern(null, null);
                _options.Intensity = 1;
                _options.IsNegative = false;

                var file = background.Document?.DocumentValue;
                if (file == null)
                {
                    return;
                }

                if (file.Local.IsDownloadingCompleted)
                {
                    LoadWallpaperAsync(file.Id, file.Local.Path);
                }
                else
                {
                    Download(background, file);
                }
            }
        }

        private void Download(Background background, File file)
        {
            if (_clientService == null)
            {
                return;
            }

            if (file.Local.CanBeDownloaded && !file.Local.IsDownloadingActive)
            {
                _clientService.DownloadFile(file.Id, 16);
            }

            UpdateManager.Subscribe(background, _clientService, file, ref _fileToken, UpdateFile, true);
        }

        // 12.10.2 dropped the subscriber argument from UpdateHandler<T>.
        private void UpdateFile(File file)
        {
            this.BeginOnUIThread(() => UpdateSource(_clientService, _background));
        }

        private void SetFill(BackgroundFill fill)
        {
            _options.FreeformColors = null;
            _options.GradientTopColor = null;
            _options.GradientBottomColor = null;
            _options.SolidColor = null;

            if (fill is BackgroundFillFreeformGradient freeform)
            {
                _options.FreeformColors = freeform.Colors;
            }
            else if (fill is BackgroundFillGradient gradient)
            {
                _options.GradientTopColor = gradient.TopColor;
                _options.GradientBottomColor = gradient.BottomColor;
                _options.GradientRotationAngle = gradient.RotationAngle;
            }
            else if (fill is BackgroundFillSolid solid)
            {
                _options.SolidColor = solid.Color;
            }

            // The one fact nobody has: what the Background actually carried. Id ties the line to a
            // tile, the fill type name says whether the type switch above matched anything at all,
            // and the colour count separates "matched but empty" from "never matched".
            // The instance id is the point of this line as much as the fill is. Valid data reaching
            // SetFill while something still paints empty options is only a contradiction if it is
            // the SAME canvas; across two canvases it is not a contradiction at all, it is the
            // shape of the bug. Ids that never appear in a SetFill line are canvases nothing ever
            // bound.
            Logger.Debug(string.Format(
                "ChatBackgroundCanvas[{6}].SetFill: background {0}, type {1}, fill {2}, freeform {3}, solid {4}, gradient {5}",
                _background?.Id,
                _background?.Type?.GetType().Name ?? "null",
                fill?.GetType().Name ?? "null",
                _options.FreeformColors?.Count ?? -1,
                _options.SolidColor?.ToString() ?? "null",
                _options.GradientTopColor?.ToString() ?? "null",
                RuntimeHelpers.GetHashCode(this)));
        }

        private void SetPattern(ChatBackgroundRenderer.Pattern pattern, string path)
        {
            if (ReferenceEquals(pattern, _pattern))
            {
                return;
            }

            // Deliberately NOT disposing the old one. RenderOverride runs off the compositor, and
            // a pattern that is being rasterized when its SKPath is disposed is a use-after-free in
            // native code, not an exception. There is at most one wallpaper on screen and swapping
            // it is a user action, so letting the finalizer collect the old path is the cheap side
            // of that trade.
            _pattern = pattern;
            _patternPath = path;
            _options.Pattern = pattern;
        }

        private void SetWallpaper(SKBitmap wallpaper)
        {
            if (ReferenceEquals(wallpaper, _wallpaper))
            {
                return;
            }

            // Same reason as SetPattern: no Dispose while a paint may still be reading it.
            _wallpaper = wallpaper;

            // And the SKImage that wraps it is built HERE, not in DrawWallpaper. SKImage.FromBitmap
            // over a mutable bitmap copies the pixels, and DrawWallpaper runs on the paint path
            // (SKCanvasVisual calls RenderOverride from Paint with CanPaint() always true), so
            // building it there was a full copy of the wallpaper per paint pass. The gradient +
            // pattern path next door already caches its result in ChatBackgroundRenderer.Surface;
            // this one did not.
            _wallpaperImage = wallpaper != null ? SKImage.FromBitmap(wallpaper) : null;
        }

        /// <summary>
        /// Parses the <c>.tgv</c> off the UI thread: gunzipping half a megabyte of SVG and building
        /// the path measured ~140 ms in the spike, which is a dropped frame on the dispatcher.
        /// </summary>
        private async void LoadPatternAsync(int fileId, string path)
        {
            if (_pattern != null && _patternPath == path)
            {
                Redraw();
                return;
            }

            if (Interlocked.Exchange(ref _loading, 1) == 1)
            {
                return;
            }

            try
            {
                string failure = null;
                var pattern = await Task.Run(() => ChatBackgroundRenderer.LoadPattern(path, out failure));

                if (pattern == null)
                {
                    Logger.Error($"Chat background pattern could not be read: {path} ({failure})");
                    return;
                }

                if (_background?.Document?.DocumentValue?.Id != fileId)
                {
                    pattern.Dispose();
                    return;
                }

                SetPattern(pattern, path);
                Redraw();
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }
            finally
            {
                Interlocked.Exchange(ref _loading, 0);
            }
        }

        private async void LoadWallpaperAsync(int fileId, string path)
        {
            if (_wallpaper != null && _wallpaperPath == path)
            {
                Redraw();
                return;
            }

            try
            {
                var bitmap = await Task.Run(() =>
                {
                    try
                    {
                        return SKBitmap.Decode(path);
                    }
                    catch
                    {
                        return null;
                    }
                });

                if (bitmap == null || _background?.Document?.DocumentValue?.Id != fileId)
                {
                    bitmap?.Dispose();
                    return;
                }

                SetWallpaper(bitmap);
                _wallpaperPath = path;
                Redraw();
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }
        }

        /// <summary>
        /// Walks the freeform gradient one phase along, the way the sending animation does.
        /// </summary>
        public void Next()
        {
            if (_options.FreeformColors is { Count: > 0 })
            {
                _options.Phase++;
                Redraw();
            }
        }

        private void Redraw()
        {
            _surface.Invalidate();
            _painter?.Invalidate();
        }

        private void RenderCore(SKCanvas canvas, Size area)
        {
            if (area.Width <= 0 || area.Height <= 0)
            {
                return;
            }

            // The canvas comes with the DPI transform applied, so this is the real device scale --
            // unlike XamlRoot.RasterizationScale, which answers 1 on this host.
            var scale = canvas.TotalMatrix.ScaleX;
            if (scale <= 0)
            {
                scale = 1;
            }

            if (_wallpaper != null)
            {
                DrawWallpaper(canvas, (float)area.Width, (float)area.Height);
                return;
            }

            // Per background, not per process: the once-per-process warning in ChatBackgroundRenderer
            // proved the fallback happens, but it could not say WHICH backgrounds fell through - so
            // it could not answer whether the one the user APPLIED did too. This can, and it names
            // the tile. Latched on the id so a canvas that repaints every frame says it once.
            if (_options.FreeformColors is not { Count: > 0 }
                && _options.SolidColor == null
                && _options.GradientTopColor == null)
            {
                var id = _background?.Id;
                if (_emptyFillReportedFor != id)
                {
                    _emptyFillReportedFor = id;
                    Logger.Warning(string.Format(
                        "ChatBackgroundCanvas[{2}]: painting background {0} ({1}) with NO fill set - the default palette is about to stand in for it",
                        id?.ToString() ?? "null",
                        _background?.Type?.GetType().Name ?? "null",
                        RuntimeHelpers.GetHashCode(this)));
                }
            }

            _surface.Draw(canvas, (float)area.Width, (float)area.Height, scale, _options);
        }

        private void DrawWallpaper(SKCanvas canvas, float width, float height)
        {
            var sourceRatio = _wallpaper.Width / (float)_wallpaper.Height;
            var targetRatio = width / height;

            SKRect destination;
            if (sourceRatio > targetRatio)
            {
                var scaled = height * sourceRatio;
                destination = new SKRect((width - scaled) / 2, 0, (width + scaled) / 2, height);
            }
            else
            {
                var scaled = width / sourceRatio;
                destination = new SKRect(0, (height - scaled) / 2, width, (height + scaled) / 2);
            }

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, width, height));

            var image = _wallpaperImage;
            if (image != null)
            {
                canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            canvas.Restore();
        }

        private sealed partial class Painter : SKCanvasElement
        {
            private readonly Action<SKCanvas, Size> _render;

            public Painter(Action<SKCanvas, Size> render)
            {
                _render = render;
            }

            protected override void RenderOverride(SKCanvas canvas, Size area)
            {
                _render(canvas, area);
            }
        }
    }
}

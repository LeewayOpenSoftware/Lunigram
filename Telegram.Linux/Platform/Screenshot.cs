//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using SkiaSharp;

namespace Telegram.Common
{
    /// <summary>
    /// Debugging aid for the port: with UNIGRAM_SCREENSHOT=&lt;seconds&gt;:&lt;path.png&gt; the window
    /// contents are rendered to a file that many seconds after the window is created. It goes
    /// through RenderTargetBitmap rather than the X server because the app runs on XWayland, where
    /// grabbing the X root window returns black - the compositor never hands window contents to X11
    /// clients. Add ":exit" to quit once the file is written.
    ///
    /// More than one instant can be requested by separating them with commas, and the seconds may
    /// be fractional:
    ///
    ///     UNIGRAM_SCREENSHOT=2:/tmp/a.png,2.4:/tmp/b.png,3:/tmp/c.png:exit
    ///
    /// which is how the port proves that an animation is actually moving: two captures a few
    /// hundred milliseconds apart must differ over the animated area. Captures are chained (the
    /// next one is scheduled once the previous file is on disk) so a slow RenderTargetBitmap
    /// delays the following instant instead of overlapping with it; the log line carries the real
    /// elapsed time of each shot.
    /// </summary>
    public static class Screenshot
    {
        private readonly record struct Shot(double Seconds, string Path, bool Exit);

        public static void Schedule(Window window)
        {
            var value = Environment.GetEnvironmentVariable("UNIGRAM_SCREENSHOT");
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var shots = new List<Shot>();

            foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':');
                if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                {
                    Logger.Error("UNIGRAM_SCREENSHOT must be <seconds>:<path.png>[:exit], comma separated for more than one instant");
                    return;
                }

                shots.Add(new Shot(seconds, parts[1], parts.Length > 2 && parts[2] == "exit"));
            }

            shots.Sort((x, y) => x.Seconds.CompareTo(y.Seconds));

            var started = Stopwatch.StartNew();
            var timer = new DispatcherTimer();
            var index = 0;

            void ScheduleNext()
            {
                if (index >= shots.Count)
                {
                    return;
                }

                var remaining = (shots[index].Seconds - started.Elapsed.TotalSeconds) * 1000;
                timer.Interval = TimeSpan.FromMilliseconds(Math.Max(remaining, 1));
                timer.Start();
            }

            timer.Tick += async (s, args) =>
            {
                timer.Stop();

                var shot = shots[index++];

                try
                {
                    await CaptureAsync(window, shot.Path);
                    Logger.Info($"screenshot: {shot.Path} at {started.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}s");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }

                if (shot.Exit)
                {
                    Application.Current.Exit();
                    return;
                }

                ScheduleNext();
            };

            ScheduleNext();
        }

        /// <summary>
        /// UNIGRAM_SHOT_REQUESTS=&lt;folder&gt;: capture whenever somebody outside asks for it, instead
        /// of at a second decided before the app started. Every 250 ms the folder is checked for
        /// files named <c>&lt;name&gt;.shot</c>; each one is turned into <c>&lt;name&gt;.png</c> next
        /// to it and then deleted, so the requester knows the capture is on disk when its request
        /// is gone.
        ///
        /// This exists for the touch work: a gesture is injected from outside the process with a
        /// uinput device, and what has to be captured is the state *right after that gesture*, whose
        /// instant nobody knows in advance - the app takes anything from 10 to 25 s to get to the
        /// point where the gesture makes sense, and a fixed schedule misses it.
        /// </summary>
        public static void ScheduleRequests(Window window)
        {
            var folder = Environment.GetEnvironmentVariable("UNIGRAM_SHOT_REQUESTS");
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                Logger.Error($"shot requests: cannot use \"{folder}\"", ex);
                return;
            }

            Logger.Info($"shot requests: watching {folder} for *.shot");

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };

            var busy = false;

            timer.Tick += async (s, args) =>
            {
                if (busy)
                {
                    return;
                }

                string[] requests;
                string[] trees;

                try
                {
                    requests = Directory.GetFiles(folder, "*.shot");
                    trees = Directory.GetFiles(folder, "*.tree");
                }
                catch
                {
                    return;
                }

                foreach (var tree in trees)
                {
                    // Same idea for the visual tree: what a gesture left behind is only readable
                    // right after it, and the tree is written to the log, not to a file.
                    Logger.Info($"tree request: {Path.GetFileNameWithoutExtension(tree)}");
                    VisualTreeDump.Dump(window.Content);

                    try
                    {
                        File.Delete(tree);
                    }
                    catch
                    {
                    }
                }

                if (requests.Length == 0)
                {
                    return;
                }

                busy = true;

                try
                {
                    Array.Sort(requests, StringComparer.Ordinal);

                    foreach (var request in requests)
                    {
                        var path = Path.ChangeExtension(request, ".png");

                        try
                        {
                            await CaptureAsync(window, path);
                            Logger.Info($"shot requests: wrote {path}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"shot requests: {path} failed", ex);
                        }

                        // Deleted last: the request file disappearing is what tells the outside
                        // that the png is complete.
                        try
                        {
                            File.Delete(request);
                        }
                        catch
                        {
                        }
                    }
                }
                finally
                {
                    busy = false;
                }
            };

            timer.Start();
        }

        public static async System.Threading.Tasks.Task CaptureAsync(Window window, string path)
        {
            using var surface = await RenderAsync(window.Content, null);

            if (surface == null)
            {
                Logger.Error("screenshot: the window has no content to render");
                return;
            }

            using var canvas = new SKCanvas(surface);

            // The gallery, the flyouts and every ContentPopup live in the popup root, which is a
            // SIBLING of Window.Content in Uno's visual tree, not a child: rendering the content
            // alone gives a picture of the app WITHOUT the thing that is on top of it - which is
            // exactly the thing being diagnosed. Each open popup is rendered on its own and drawn
            // over the base at the offset it has on screen.
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(window.Content.XamlRoot))
            {
                if (popup.Child is not FrameworkElement child || child.ActualWidth <= 0 || child.ActualHeight <= 0)
                {
                    continue;
                }

                using var layer = await RenderAsync(child, null);

                if (layer == null)
                {
                    continue;
                }

                var scale = surface.Width / Math.Max(window.Content.ActualSize.X, 1);
                var origin = new Point();

                try
                {
                    origin = child.TransformToVisual(window.Content).TransformPoint(new Point());
                }
                catch
                {
                    // Not in the same tree (it never is for a popup in some Uno versions): the
                    // popup's own offset is the best available answer.
                    origin = new Point(popup.HorizontalOffset, popup.VerticalOffset);
                }

                canvas.DrawBitmap(layer, (float)(origin.X * scale), (float)(origin.Y * scale));
            }

            using var image = SKImage.FromBitmap(surface);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);

            data.SaveTo(stream);
        }

        private static async System.Threading.Tasks.Task<SKBitmap> RenderAsync(UIElement element, object _)
        {
            if (element == null)
            {
                return null;
            }

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);

            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return null;
            }

            var buffer = await bitmap.GetPixelsAsync();
            var pixels = buffer.ToArray();

            var info = new SKImageInfo(bitmap.PixelWidth, bitmap.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            var result = new SKBitmap(info);

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, result.GetPixels(), pixels.Length);
            return result;
        }
    }
}

//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using LinqToVisualTree;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Telegram.Controls;
using TextSetOptions = Windows.UI.Text.TextSetOptions;
using ITextDocument = Windows.UI.Text.ITextDocument;
using ApplicationDataContainer = Telegram.Services.LocalSettingsContainer;
using WinRT;
using Telegram.Controls.Media;
using Telegram.Entities;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Services;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels.Gallery;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Calls;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.Core;
using Microsoft.UI.Input;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Data;

namespace Telegram.Common
{
    /// <summary>
    /// The Linux head's half of <see cref="Extensions"/>. Upstream 12.10.2 broke Common/Extensions.cs
    /// (2104 lines) into a 256-line trunk plus Extensions.Collections/Geometry/Rpc/Text/Xaml; this
    /// port had added members of its own to the old single file, and they live here instead of being
    /// scattered across the five, so the next bump can take all six from upstream untouched.
    /// </summary>
    public static partial class Extensions
    {
        public static IEnumerable<IList<T>> ToChunks<T>(this List<T> enumerable, int chunkSize)
        {
            int itemsReturned = 0;
            int count = enumerable.Count;
            while (itemsReturned < count)
            {
                int currentChunkSize = Math.Min(chunkSize, count - itemsReturned);
                yield return enumerable.GetRange(itemsReturned, currentChunkSize);
                itemsReturned += currentChunkSize;
            }
        }

        /// <summary>
        /// The root of the expanded content or item template of <paramref name="container"/>.
        /// </summary>
        /// <remarks>
        /// On Windows this is <c>ContentControl.ContentTemplateRoot</c> and nothing else.
        /// In Uno that property is never assigned for anything that has a control template -
        /// which is every ListViewItem, every ScrollViewer, every templated control - and the
        /// expanded template is left on the ContentPresenter inside the template instead. Because
        /// every caller asks with a type test, the miss is silent: no exception, just a container
        /// that is never bound. See Telegram.Linux/Xaml/ContentTemplateRootEx.cs.
        /// </remarks>
        public static UIElement ContentRoot(this ContentControl container)
        {
#if LINUX
            return container.GetContentTemplateRootEx();
#else
            return container?.ContentTemplateRoot;
#endif
        }

        /// <summary>
        /// The <see cref="MenuFlyoutPresenter"/> showing <paramref name="flyout"/>, or null when the
        /// flyout is not open.
        /// </summary>
        /// <remarks>
        /// Walking up from the first item is enough on Windows. In Uno it is not: a MenuFlyout hangs
        /// from the popup root, which is a SIBLING of Window.Content, so the walk never reaches the
        /// presenter and simply returns null -- the same tree difference that made PointerTest look
        /// through the open popups, see PORTING.md. The miss is silent, and a caller that takes it
        /// for "not open" gives up before showing anything: that is what left the reactions bar
        /// dead, with the menu itself perfectly visible underneath it.
        /// </remarks>
        public static MenuFlyoutPresenter Presenter(this MenuFlyout flyout)
        {
            if (flyout == null || flyout.Items.Count == 0)
            {
                return null;
            }

            var first = flyout.Items[0];
            var presenter = first.GetParent<MenuFlyoutPresenter>();

            if (presenter != null)
            {
                return presenter;
            }

#if LINUX
            // The item is matched against the presenter's own Items rather than trusted by type,
            // because more than one menu can be open at a time -- a submenu over its parent.
            var xamlRoot = flyout.XamlRoot ?? first.XamlRoot;
            if (xamlRoot == null)
            {
                return null;
            }

            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child is MenuFlyoutPresenter child && child.Items.Contains(first))
                {
                    return child;
                }

                if (popup.Child is DependencyObject root)
                {
                    foreach (var nested in root.Descendants<MenuFlyoutPresenter>())
                    {
                        if (nested.Items.Contains(first))
                        {
                            return nested;
                        }
                    }
                }
            }
#endif

            return null;
        }

        public static double GetNamedNumber(this System.Text.Json.Nodes.JsonObject obj, string name, double defaultValue)
        {
            return obj[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out double result)
                ? result
                : defaultValue;
        }

        public static string GetNamedString(this System.Text.Json.Nodes.JsonObject obj, string name, string defaultValue)
        {
            return obj[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string result)
                ? result
                : defaultValue;
        }

        public static bool GetNamedBoolean(this System.Text.Json.Nodes.JsonObject obj, string name, bool defaultValue)
        {
            return obj[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out bool result)
                ? result
                : defaultValue;
        }

        public static int ToTimestamp(this DateTime dateTime)
        {
            var dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            DateTime.SpecifyKind(dtDateTime, DateTimeKind.Utc);

            return (int)(dateTime.ToUniversalTime() - dtDateTime).TotalSeconds;
        }

        public static long ToTimestampMilliseconds(this DateTime dateTime)
        {
            var dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0);
            DateTime.SpecifyKind(dtDateTime, DateTimeKind.Utc);

            return (long)(dateTime.ToUniversalTime() - dtDateTime).TotalMilliseconds;
        }

        public static bool IntersectsWith(this Rect a, Rect b)
        {
            return (b.X <= a.X + a.Width) &&
                (a.X <= b.X + b.Width) &&
                (b.Y <= a.Y + a.Height) &&
                (a.Y <= b.Y + b.Height);
        }

        public static void Shiftino<T>(this T[] array, int offset)
        {
            if (offset < 0)
            {
                while (offset < 0)
                {
                    var element = array[array.Length - 1];
                    Array.Copy(array, 0, array, 1, array.Length - 1);
                    array[0] = element;
                    offset += 1;
                }
            }
            else if (offset > 0)
            {
                while (offset > 0)
                {
                    var element = array[0];
                    Array.Copy(array, 1, array, 0, array.Length - 1);
                    array[array.Length - 1] = element;
                    offset -= 1;
                }
            }
        }

        /// <summary>
        /// Creates a relative path from one file or folder to another.
        /// </summary>
        /// <param name="fromPath">Contains the directory that defines the start of the relative path.</param>
        /// <param name="toPath">Contains the path that defines the endpoint of the relative path.</param>
        /// <returns>The relative path from the start directory to the end path or <c>toPath</c> if the paths are not related.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="UriFormatException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public static string MakeRelativePath(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath))
            {
                throw new ArgumentNullException("fromPath");
            }

            if (string.IsNullOrEmpty(toPath))
            {
                throw new ArgumentNullException("toPath");
            }

            var fromUri = new Uri(fromPath);
            var toUri = new Uri(toPath);

            if (fromUri.Scheme != toUri.Scheme) { return toPath; } // path can't be made relative.

            var relativeUri = fromUri.MakeRelativeUri(toUri);
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (toUri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            return relativePath;
        }

        public static SvgImageSource ToSvg(string path, int width = 0, int height = 0)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            return new SvgImageSource(UriEx.ToLocal(path))
            {

            };
        }

        public static int GetNamedInt32(this System.Text.Json.Nodes.JsonObject obj, string name, int defaultValue)
        {
            return obj[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out int result)
                ? result
                : defaultValue;
        }

        public static long GetNamedInt64(this System.Text.Json.Nodes.JsonObject obj, string name, long defaultValue)
        {
            if (obj[name] is System.Text.Json.Nodes.JsonValue value)
            {
                if (value.TryGetValue(out long result))
                {
                    return result;
                }
                else if (value.TryGetValue(out string text) && long.TryParse(text, out result))
                {
                    return result;
                }
            }

            return defaultValue;
        }
    }
}

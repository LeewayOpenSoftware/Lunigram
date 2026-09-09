//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Services;
using Telegram.Td.Api;
using Microsoft.UI.Xaml;

namespace Telegram.Controls.Chats
{
    /// <summary>
    /// The small round wallpaper preview that service messages show, on top of the Skia painter.
    /// </summary>
    /// <remarks>
    /// <para>Telegram/Controls/Chats/ChatBackgroundPresenter.cs stays out of this head for the
    /// reason PORTING.md section 6 gives: it composes the pattern on a
    /// <c>CompositionDrawingSurface</c> and there is no <c>CompositionGraphicsDevice</c> to create
    /// one with. <see cref="ChatBackgroundCanvas"/> already paints the same wallpaper with Skia,
    /// so what is missing is only the shape of the call: the XAML that names this control is
    /// shared, and <c>win:</c> cannot swap an element that is not in the default namespace
    /// (PORTING.md rule 1), so the name is kept and the surface underneath is replaced.</para>
    /// </remarks>
    public partial class ChatBackgroundPresenter : ChatBackgroundCanvas
    {
        /// <summary>
        /// Accepted and ignored. The real presenter is a <c>Control</c> and the shared XAML turns
        /// it off so it does not eat the click meant for the service message; a
        /// <see cref="ChatBackgroundCanvas"/> is a Grid, which has neither the property nor
        /// anything to disable.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        public void UpdateSource(IClientService clientService, Background background, bool thumbnail, ChatTheme theme = null)
        {
            if (background == null)
            {
                UpdateSource(clientService, null);
                return;
            }

            if (background.Type is BackgroundTypeChatTheme typeChatTheme)
            {
                // The canvas knows the three drawable types only, so the emoji theme is resolved
                // to the background it stands for before it gets there.
                if (clientService != null && clientService.TryGetEmojiChatTheme(typeChatTheme.ThemeName, out EmojiChatTheme emoji))
                {
                    var settings = ActualTheme == ElementTheme.Light
                        ? emoji.LightSettings
                        : emoji.DarkSettings;

                    if (settings?.Background != null && settings.Background.Type is not BackgroundTypeChatTheme)
                    {
                        UpdateSource(clientService, settings.Background, thumbnail, null);
                    }
                }

                return;
            }

            UpdateSource(clientService, thumbnail ? Preview(background) : background);
        }

        /// <summary>
        /// Swaps in the thumbnail file for a photo wallpaper, which is what the preview asks for.
        /// </summary>
        /// <remarks>
        /// A pattern is deliberately left alone: its thumbnail is a raster of the <c>.tgv</c>, and
        /// the canvas draws patterns from the vector -- handing it the raster loses the doodles and
        /// leaves the bare gradient (see the "raster pattern" branch of
        /// <see cref="ChatBackgroundCanvas"/>). A pattern document is a few tens of KB, so paying
        /// for the real one buys the whole preview.
        /// </remarks>
        private static Background Preview(Background background)
        {
            if (background.Type is not BackgroundTypeWallpaper)
            {
                return background;
            }

            var document = background.Document;
            var thumbnail = document?.Thumbnail?.File;

            if (thumbnail == null)
            {
                return background;
            }

            return new Background(background.Id, background.IsDefault, background.IsDark, background.Name,
                new Document(document.FileName, document.MimeType, null, null, thumbnail),
                background.Type);
        }
    }
}

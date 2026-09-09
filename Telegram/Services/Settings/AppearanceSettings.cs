//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Navigation;
using Telegram.Td.Api;
using Windows.UI;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Colors = Microsoft.UI.Colors;
#if LINUX
using ApplicationDataContainer = Telegram.Services.LocalSettingsContainer;
#endif

namespace Telegram.Services.Settings
{
    public enum TelegramTheme
    {
        Light = 1 << 1,
        Dark = 1 << 2
    }

    public enum TelegramThemeType
    {
        Classic = 0,
        Day = 1,
        Night = 2,
        Tinted = 3,
        Custom = 4
    }

    public enum NightMode
    {
        Disabled,
        Scheduled,
        Automatic,
        System
    }

    public enum AccentShade
    {
        Default,
        Light1,
        Light2,
        Light3,
        Dark1,
        Dark2,
        Dark3
    }

    public readonly struct Acrylic
    {
        public static Acrylic<Color> Color(Color tint, Color fallback, double opacity, double? luminosity = null)
        {
            return new Acrylic<Color>(tint, fallback, opacity, luminosity);
        }

        public static Acrylic<AccentShade> Shade(AccentShade tint, AccentShade fallback, double opacity, double? luminosity = null)
        {
            return new Acrylic<AccentShade>(tint, fallback, opacity, luminosity);
        }
    }

    public readonly struct Acrylic<T> where T : struct
    {
        public T TintColor { get; }

        public T FallbackColor { get; }

        public double TintOpacity { get; }

        public double? TintLuminosityOpacity { get; }

        public Acrylic(T tint, T fallback, double opacity, double? tonality = null)
        {
            TintColor = tint;
            FallbackColor = fallback;
            TintOpacity = opacity;
            TintLuminosityOpacity = tonality;
        }
    }

    public partial class InstalledEmojiSet
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public int Version { get; set; }
    }

    public partial class AppearanceSettings : SettingsServiceBase
    {
        public AppearanceSettings()
            : base("Theme")
        {
            MigrateTheme();
        }

#if LINUX
        /// <summary>
        /// The desktop colour scheme changed (or xdg-desktop-portal finally answered). Re-read it
        /// and, if the theme this session started with was only a guess seeded from the system,
        /// drop the seed. Returns true when the guess was wrong, meaning the caller must rebuild
        /// the dictionaries and not just switch: the message brushes are shared instances.
        ///
        /// Lives here rather than in NightModeService because it needs _container and
        /// _requestedTheme; upstream 12.10 moved the ColorValuesChanged handler itself to the
        /// service, which calls this first.
        /// </summary>
        public bool InvalidateDesktopColorScheme()
        {
            DesktopColorScheme.Invalidate();

            if (!_container.ContainsKey("Theme"))
            {
                var seeded = _requestedTheme;
                _requestedTheme = null;

                if (seeded is TelegramTheme guess && guess != RequestedTheme)
                {
                    return true;
                }
            }

            return false;
        }
#endif

        // UpdateTimer/CheckNightModeConditions moved to Services/NightModeService.cs upstream
        // in 12.10; the copies that used to live here are gone with them.


        // UpdateNightMode moved to NightModeService.Update in 12.10; every caller now goes
        // through the service, so the copy that used to live here is gone.

        private string _emojiSet;
        public string EmojiSet
        {
            get => _emojiSet ??= GetValueOrDefault(_container, "EmojiSetId", "apple");
            set => AddOrUpdateValue(ref _emojiSet, _container, "EmojiSetId", value);
        }

        private void MigrateTheme()
        {
            if (TryGetValue(_container, "ThemePath", out string path))
            {
                if (path.EndsWith("Assets\\Themes\\DarkBlue.unigram-theme"))
                {
                    RequestedTheme = TelegramTheme.Dark;
                    this[TelegramTheme.Dark].Type = TelegramThemeType.Tinted;
                    Accents[TelegramThemeType.Tinted] = ThemeInfoBase.Accents[TelegramThemeType.Tinted][AccentShade.Default];
                }
                else if (path.Length > 0 && System.IO.File.Exists(path))
                {
                    this[RequestedTheme].Type = TelegramThemeType.Custom;
                    this[RequestedTheme].Custom = path;
                }

                _container.Remove("ThemePath");
            }
            else if (TryGetValue(_container, "ThemeType", out int type))
            {
                this[RequestedTheme].Type = (TelegramThemeType)type;

                if ((TelegramThemeType)type == TelegramThemeType.Custom && TryGetValue(_container, "ThemeCustom", out string custom))
                {
                    if (custom.Length > 0 && System.IO.File.Exists(custom))
                    {
                        this[RequestedTheme].Type = TelegramThemeType.Custom;
                        this[RequestedTheme].Custom = custom;
                    }
                }

                _container.Remove("ThemeCustom");
                _container.Remove("ThemeType");
            }
        }

        private ThemeSettingsBase _themeLight;
        private ThemeSettingsBase _themeDark;

        public ThemeSettingsBase this[TelegramTheme type]
        {
            get
            {
                if (type == TelegramTheme.Light)
                {
                    return _themeLight ??= new ThemeSettingsBase(_container, TelegramTheme.Light);
                }

                return _themeDark ??= new ThemeSettingsBase(_container, TelegramTheme.Dark);
            }
        }

        private ThemeTypeSettingsBase _accents;
        public ThemeTypeSettingsBase Accents => _accents ??= new ThemeTypeSettingsBase(_container);

        private TelegramTheme? _requestedTheme;
        public TelegramTheme RequestedTheme
        {
            get => _requestedTheme ??= (TelegramTheme)GetValueOrDefault(_container, "Theme", (int)GetSystemTheme());
            set => AddOrUpdateValue(_container, "Theme", (int)(_requestedTheme = value));
        }

        private NightMode? _nightMode;
        public NightMode NightMode
        {
            get => _nightMode ??= (NightMode)GetValueOrDefault(_container, "NightMode", (int)NightMode.Disabled);
            set => AddOrUpdateValue(_container, "NightMode", (int)(_nightMode = value));
        }

        private bool? _forceNightMode;
        public bool ForceNightMode
        {
            get => _forceNightMode ??= GetValueOrDefault(_container, "ForceNightMode", false);
            set => AddOrUpdateValue(ref _forceNightMode, _container, "ForceNightMode", value);
        }

        private bool? _isLocationBased;
        public bool IsLocationBased
        {
            get => _isLocationBased ??= GetValueOrDefault(_container, "IsLocationBased", false);
            set => AddOrUpdateValue(_container, "IsLocationBased", _isLocationBased = value);
        }

        private TimeSpan? _from;
        public TimeSpan From
        {
            get
            {
                if (_from == null)
                {
                    var value = GetValueOrDefault("From", 22 * 60 + 0);
                    var currentHour = value / 60;

                    _from = new TimeSpan(currentHour, value - currentHour * 60, 0);
                }

                return _from ?? new TimeSpan(22, 0, 0);
            }
            set
            {
                _from = value;
                AddOrUpdateValue("From", value.Hours * 60 + value.Minutes);
            }
        }

        private TimeSpan? _to;
        public TimeSpan To
        {
            get
            {
                if (_to == null)
                {
                    var value = GetValueOrDefault("To", 9 * 60 + 0);
                    var currentHour = value / 60;

                    _to = new TimeSpan(currentHour, value - currentHour * 60, 0);
                }

                return _to ?? new TimeSpan(9, 0, 0);
            }
            set
            {
                _to = value;
                AddOrUpdateValue("To", value.Hours * 60 + value.Minutes);
            }
        }

        private Location _location;
        public Location Location
        {
            get => _location ??= new Location { Latitude = GetValueOrDefault("Latitude", 0d), Longitude = GetValueOrDefault("Longitude", 0d) };
            set
            {
                _location = value;
                AddOrUpdateValue("Latitude", value.Latitude);
                AddOrUpdateValue("Longitude", value.Longitude);
            }
        }

        private string _town;
        public string Town
        {
            get => _town ??= GetValueOrDefault("Town", string.Empty);
            set => AddOrUpdateValue("Town", _town = value);
        }

        // RequestedTheme defaults to whatever the system is set to, so this has to stay on the
        // settings rather than move to NightModeService: the service reads the settings, and a
        // dependency the other way would recurse through both singletons' constructors.
        public TelegramTheme GetSystemTheme()
        {
#if LINUX
            // UISettings answers Light until xdg-desktop-portal replies to the X11 host, seconds
            // into the session, and this is what seeds the theme the very first window is built
            // with. DesktopColorScheme asks the desktop directly - the read starts in Program.Main
            // and is normally finished by now - so the window is born in the right theme instead
            // of flipping once the portal catches up. Unknown means no desktop answered: then the
            // behaviour is the one below, unchanged.
            var scheme = DesktopColorScheme.Current;
            if (scheme != DesktopTheme.Unknown)
            {
                return scheme == DesktopTheme.Dark
                    ? TelegramTheme.Dark
                    : TelegramTheme.Light;
            }
#endif

            var app = BootStrapper.Current as App;
            var current = app.UISettings.GetColorValue(UIColorType.Background);

            return current == Colors.Black ? TelegramTheme.Dark : TelegramTheme.Light;
        }

        private static bool? _useDefaultScaling;
        public bool UseDefaultScaling
        {
            get => _useDefaultScaling ??= GetValueOrDefault("UseDefaultScaling", true);
            set => AddOrUpdateValue(ref _useDefaultScaling, "UseDefaultScaling", value);
        }

        private static int? _scaling;
        public int Scaling
        {
            get => _scaling ??= GetValueOrDefault("Scaling", 0);
            set => AddOrUpdateValue(ref _scaling, "Scaling", value);
        }

        private static int? _messageFontSize;
        public int MessageFontSize
        {
            get => _messageFontSize ??= (int)GetValueOrDefault("MessageFontSize", 14d);
            set => AddOrUpdateValue("MessageFontSize", (double)(_messageFontSize = value));
        }

        public int CaptionFontSize => MessageFontSize - 2;

        private static int? _bubbleRadius;
        public int BubbleRadius
        {
            get => _bubbleRadius ??= GetValueOrDefault("BubbleRadius", 15);
            set => AddOrUpdateValue(ref _bubbleRadius, "BubbleRadius", value);
        }

        public int CornerRadius => BubbleRadius > 0 ? BubbleRadius < 15 ? BubbleRadius : 24 : 0;

        private bool? _isQuickReplySelected;
        public bool IsQuickReplySelected
        {
            get => _isQuickReplySelected ??= GetValueOrDefault("IsQuickReplySelected", true);
            set => AddOrUpdateValue(ref _isQuickReplySelected, "IsQuickReplySelected", value);
        }

        private string _fontFamily;
        public string FontFamily
        {
            get => _fontFamily ??= GetValueOrDefault("FontFamily", string.Empty);
            set => AddOrUpdateValue(ref _fontFamily, "FontFamily", value);
        }

        private bool _chatThemeLoaded;

        private EmojiChatTheme _chatTheme;
        public EmojiChatTheme ChatTheme
        {
            get => _chatTheme ??= LoadChatTheme();
            set => SaveChatTheme(value);
        }

        private void SaveChatTheme(EmojiChatTheme theme)
        {
            if (theme?.Name == "\U0001F3E0")
            {
                theme = null;
            }

            if (theme != null)
            {
                var light = _container.GetContainer("ChatThemeLight");
                var dark = _container.GetContainer("ChatThemeDark");

                AddOrUpdateValue("ChatThemeName", theme.Name);
                SaveChatThemeSettings(light, theme.LightSettings);
                SaveChatThemeSettings(dark, theme.DarkSettings);
            }
            else
            {
                _container.Remove("ChatThemeName");
                _container.DeleteContainer("ChatThemeLight");
                _container.DeleteContainer("ChatThemeDark");
            }

            _chatTheme = theme;
        }

        private void SaveChatThemeSettings(ISettingsStore container, ThemeSettings settings)
        {
            AddOrUpdateValue(container, "OutgoingMessageAccentColor", settings.OutgoingMessageAccentColor);
            AddOrUpdateValue(container, "OutgoingMessageFill", TdBackground.ToString(settings.OutgoingMessageFill));
            AddOrUpdateValue(container, "AnimateOutgoingMessageFill", settings.AnimateOutgoingMessageFill);
            AddOrUpdateValue(container, "AccentColor", settings.AccentColor);
        }

        private EmojiChatTheme LoadChatTheme()
        {
            if (_chatThemeLoaded)
            {
                return _chatTheme;
            }

            _chatThemeLoaded = true;

            var name = GetValueOrDefault<string>("ChatThemeName", null);
            if (name != null)
            {
                var light = _container.GetContainer("ChatThemeLight");
                var dark = _container.GetContainer("ChatThemeDark");

                return new EmojiChatTheme
                {
                    Name = name,
                    LightSettings = LoadChatThemeSettings(light),
                    DarkSettings = LoadChatThemeSettings(dark)
                };
            }

            return null;
        }

        private ThemeSettings LoadChatThemeSettings(ISettingsStore container)
        {
            return new ThemeSettings
            {
                OutgoingMessageAccentColor = GetValueOrDefault(container, "OutgoingMessageAccentColor", 0),
                OutgoingMessageFill = TdBackground.FromString(GetValueOrDefault(container, "OutgoingMessageFill", string.Empty)),
                AnimateOutgoingMessageFill = GetValueOrDefault(container, "AnimateOutgoingMessageFill", false),
                AccentColor = GetValueOrDefault(container, "AccentColor", 0)
            };
        }

        #region Touch mode

        /// <summary>
        /// One-handed, finger-sized layout: a single column (the chat list fills the window and a
        /// chat opens over it with a back button, the way Telegram for Android does it) and a
        /// larger interface scale. Off by default; it is a deliberate choice, not something to
        /// infer from the hardware, because a convertible is a tablet and a laptop on alternate
        /// hours.
        ///
        /// The single column is not a new layout: <see cref="Controls.MasterDetailState.Minimal"/>
        /// is the phone layout Unigram was born with, and it is already what a narrow window gets.
        /// Touch mode only takes the decision away from the window width -
        /// <c>MasterDetailPanel.ForceMinimal</c>.
        /// </summary>
        private static bool? _touchMode;
        public bool TouchMode
        {
            get => _touchMode ??= GetValueOrDefault("TouchMode", false);
            set
            {
                if (TouchMode == value)
                {
                    return;
                }

                AddOrUpdateValue(ref _touchMode, "TouchMode", value);
                TouchModeChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raised when <see cref="TouchMode"/> is toggled, so the layout can follow without a
        /// restart. <c>MasterDetailView</c> subscribes while it is loaded and drops the handler in
        /// <c>Dispose</c>; the interface scale, which cannot change in place (see
        /// <c>NativeUtils.OverrideScaleForCurrentView</c> on Linux), is the part that waits.
        /// </summary>
        public static event EventHandler TouchModeChanged;

        /// <summary>
        /// The interface scale touch mode raises to when the current one is smaller. 150% is the
        /// point where a 48 px chat-list row measures ~72 px, which is the target size every touch
        /// guideline converges on (Material 48 dp, Fluent 40 px, HIG 44 pt) once the ~10 mm of a
        /// fingertip is converted at the density of a Surface Pro 4 panel.
        /// </summary>
        public const int TouchModeScaling = 150;

        /// <summary>
        /// The scale that was in force before touch mode raised it, so turning touch mode off puts
        /// it back. 0 means "the system default" (<see cref="UseDefaultScaling"/>), which is also
        /// what <c>SettingsAppearanceViewModel</c> uses as the index of the Default entry; -1 means
        /// nothing was stored because touch mode never had to raise anything.
        /// </summary>
        private static int? _scalingBeforeTouchMode;
        public int ScalingBeforeTouchMode
        {
            get => _scalingBeforeTouchMode ??= GetValueOrDefault("ScalingBeforeTouchMode", -1);
            set => AddOrUpdateValue(ref _scalingBeforeTouchMode, "ScalingBeforeTouchMode", value);
        }

        #endregion
    }

    public partial class ThemeTypeSettingsBase : SettingsServiceBase
    {
        public ThemeTypeSettingsBase(ISettingsStore container)
            : base(container)
        {
        }

        public Color this[TelegramThemeType type]
        {
            get => ColorEx.FromHex(GetValueOrDefault(ConvertToKey(type, "Accent"), ColorEx.ToHex(ThemeInfoBase.Accents[type][AccentShade.Default])), true);
            set => AddOrUpdateValue(ConvertToKey(type, "Accent"), ColorEx.ToHex(value));
        }

        private string ConvertToKey(TelegramThemeType type, string key)
        {
            return $"{type}{key}";
        }
    }

    public partial class ThemeSettingsBase : SettingsServiceBase
    {
        private readonly TelegramTheme _prefix;

        public ThemeSettingsBase(ISettingsStore container, TelegramTheme prefix)
            : base(container)
        {
            _prefix = prefix;
        }

        private TelegramThemeType? _type;
        public TelegramThemeType Type
        {
            get
            {
                if (_type == null)
                {
                    _type = (TelegramThemeType)GetValueOrDefault(_container, $"ThemeType{_prefix}", 0);

                    if (_prefix == TelegramTheme.Dark && (_type == TelegramThemeType.Classic || _type == TelegramThemeType.Day))
                    {
                        _type = TelegramThemeType.Night;
                    }
                    else if (_prefix == TelegramTheme.Light && (_type == TelegramThemeType.Night || _type == TelegramThemeType.Tinted))
                    {
                        _type = TelegramThemeType.Classic;
                    }
                }

                return _type ?? TelegramThemeType.Classic;
            }
            set
            {
                if (_prefix == TelegramTheme.Dark && (value == TelegramThemeType.Classic || value == TelegramThemeType.Day))
                {
                    value = TelegramThemeType.Night;
                }
                else if (_prefix == TelegramTheme.Light && (value == TelegramThemeType.Night || value == TelegramThemeType.Tinted))
                {
                    value = TelegramThemeType.Classic;
                }

                _type = value;
                AddOrUpdateValue(_container, $"ThemeType{_prefix}", (int)value);
            }
        }

        private string _custom;
        public string Custom
        {
            get => _custom ??= GetValueOrDefault(_container, $"ThemeCustom{_prefix}", string.Empty);
            set => AddOrUpdateValue(_container, $"ThemeCustom{_prefix}", _custom = value);
        }

    }
}

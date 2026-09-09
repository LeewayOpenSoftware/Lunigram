//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Services;
using Windows.System;
using Windows.System.Power;
using Microsoft.UI.Composition;
using Windows.UI.ViewManagement;
#if LINUX
// m_dispatcher is Microsoft.UI.Dispatching.DispatcherQueue (CsWinRT.cs alias); Uno also ships
// Windows.System.DispatcherQueuePriority, which the `using Windows.System` above would pick.
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
#endif

namespace Telegram.Common
{
    public enum PowerSavingMode
    {
        Off,
        Auto
    }

    public enum PowerSavingStatus
    {
        Off,
        On
    }

    public partial class PowerSavingPolicy
    {
        private static bool m_isDisabledByPolicy;
        private static bool m_isPowerSavingMode;

        private static readonly bool m_energySaverStatusChangedRevokerValid;
        private static readonly CompositionCapabilities m_compositionCapabilities;
        private static readonly UISettings m_uiSettings;

        private static readonly DispatcherQueue m_dispatcher;


        static PowerSavingPolicy()
        {
            m_dispatcher = DispatcherQueue.GetForCurrentThread();

            try
            {
                PowerManager.EnergySaverStatusChanged += PowerManager_EnergySaverStatusChanged;
                m_energySaverStatusChangedRevokerValid = true;
            }
            catch
            {

            }

            m_compositionCapabilities = CompositionCapabilities.GetForCurrentView();
            m_compositionCapabilities.Changed += CompositionCapabilities_Changed;

            m_uiSettings = new UISettings();
            m_uiSettings.AdvancedEffectsEnabledChanged += UISettings_AdvancedEffectsEnabledChanged;

#if LINUX
            // The replacement for PowerManager.EnergySaverStatusChanged. PowerProfile.Start is
            // what makes the first read happen; the event is raised off the UI thread, which is
            // why it goes through the dispatcher hop below exactly like the Windows one.
            PowerProfile.Changed += PowerProfile_Changed;
            PowerProfile.Start();
#endif

            m_areMaterialsEnabled = AreMaterialsEnabled;

            UpdatePolicy();
        }

#if LINUX
        private static void PowerProfile_Changed(object sender, EventArgs e)
        {
            UpdatePolicyByDispatcher();
        }
#endif

        private static void PowerManager_EnergySaverStatusChanged(object sender, object e)
        {
            UpdatePolicyByDispatcher();
        }

        private static void CompositionCapabilities_Changed(CompositionCapabilities sender, object args)
        {
            UpdatePolicyByDispatcher();
        }

        private static void UISettings_AdvancedEffectsEnabledChanged(UISettings sender, object args)
        {
            UpdatePolicyByDispatcher();
        }

        private static void UpdatePolicyByDispatcher()
        {
#if LINUX
            if (m_dispatcher == null)
            {
                // The static constructor runs on whichever thread touches this class first, and
                // DispatcherQueue.GetForCurrentThread answers null off the UI thread. Upstream
                // never sees it because its only off-thread caller is a Windows power event that
                // cannot arrive before the app has a UI thread; here PowerProfile can answer from
                // the bus reader thread at any moment, and dereferencing null there would take the
                // whole read down and leave the policy stuck at its initial value.
                UpdatePolicy();
                return;
            }
#endif

            if (m_dispatcher.HasThreadAccess)
            {
                UpdatePolicy();
            }
            else
            {
                m_dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, UpdatePolicy);
            }
        }

        // Internal MUX logic: https://github.com/microsoft/microsoft-ui-xaml/blob/main/dev/Lights/MaterialHelper.cpp
        private static void UpdatePolicy()
        {
#if LINUX
            // PowerManager is not implemented in Uno Skia. The answer comes from
            // power-profiles-daemon over the system bus instead: ActiveProfile == "power-saver" is
            // the profile the user picks from the battery menu AND the one GNOME switches to on
            // its own at low battery, which is the same pair of ways Windows turns its own battery
            // saver on. Being on battery is deliberately NOT counted: Windows keeps animating on
            // battery until the saver engages, and counting it would make this port stop
            // autoplaying earlier than the client it is a port of.
            // See Telegram.Linux/Platform/DBus/PowerProfile.cs; on a machine with no daemon it
            // answers false, which is what this line used to be.
            var isEnergySaverMode = PowerProfile.IsEnergySaver;
#else
            var isEnergySaverMode = !m_energySaverStatusChangedRevokerValid || PowerManager.EnergySaverStatus == EnergySaverStatus.On;
#endif
            var areEffectsFast = m_compositionCapabilities != null && m_compositionCapabilities.AreEffectsFast();
            var advancedEffectsEnabled = m_uiSettings == null || m_uiSettings.AdvancedEffectsEnabled;

            // This applies only to visual effects
            var isDisabledByPolicy = Mode switch
            {
                PowerSavingMode.Auto => isEnergySaverMode || !areEffectsFast || !advancedEffectsEnabled,
                _ => false
            };

            // This applies to all the rest
            var isPowerSavingMode = Mode switch
            {
                PowerSavingMode.Auto => isEnergySaverMode,
                _ => false
            };

            if (m_isDisabledByPolicy != isDisabledByPolicy)
            {
                m_isDisabledByPolicy = isDisabledByPolicy;
                m_isPowerSavingMode = isPowerSavingMode;
                Changed?.Invoke(null, EventArgs.Empty);

                RaiseAreMaterialsEnabledChanged();
            }
            else if (m_isPowerSavingMode != isPowerSavingMode)
            {
                m_isPowerSavingMode = isPowerSavingMode;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

#if LINUX
        // Same question as the Windows line below -- "is there anything for Automatic to follow?"
        // -- asked of the only thing that can answer it here. PowerManager.BatteryStatus is not
        // implemented in Uno and m_energySaverStatusChangedRevokerValid is always false, so this
        // used to be a constant false.
        public static bool IsSupported => PowerProfile.IsSupported;
#else
        public static bool IsSupported => m_energySaverStatusChangedRevokerValid && PowerManager.BatteryStatus != BatteryStatus.NotPresent;
#endif

        public static PowerSavingStatus Status => m_isPowerSavingMode ? PowerSavingStatus.On : PowerSavingStatus.Off;

        public static bool IsDisabledByPolicy => m_isDisabledByPolicy;

        public static PowerSavingMode Mode
        {
            get => AppSettings.IsPowerSavingEnabled ? PowerSavingMode.Auto : PowerSavingMode.Off;
            set
            {
                AppSettings.IsPowerSavingEnabled = value == PowerSavingMode.Auto;
                UpdatePolicyByDispatcher();
            }
        }

        public static event EventHandler Changed;

        private static bool m_areMaterialsEnabled;
        public static bool AreMaterialsEnabled
        {
            get => AppSettings.AreMaterialsEnabled && !m_isDisabledByPolicy;
            set
            {
                AppSettings.AreMaterialsEnabled = value;
                RaiseAreMaterialsEnabledChanged();
            }
        }

        private static void RaiseAreMaterialsEnabledChanged()
        {
            if (m_areMaterialsEnabled != AreMaterialsEnabled)
            {
                m_areMaterialsEnabled = AreMaterialsEnabled;
                NightModeService.Current.Update(false, false);
            }
        }

        public static bool AutoPlayVideos
        {
            get => AppSettings.AutoPlayVideos && !m_isPowerSavingMode;
            set
            {
                AppSettings.AutoPlayVideos = value;
                RaisePropertyChanged();
            }
        }

        public static bool AutoPlayAnimations
        {
            get => AppSettings.AutoPlayAnimations && !m_isPowerSavingMode;
            set
            {
                AppSettings.AutoPlayAnimations = value;
                RaisePropertyChanged();
            }
        }

        public static bool AutoPlayStickers
        {
            get => AppSettings.AutoPlayStickers && !m_isPowerSavingMode;
            set
            {
                AppSettings.AutoPlayStickers = value;
                RaisePropertyChanged();
            }
        }

        public static bool AutoPlayStickersInChats
        {
            get => AppSettings.AutoPlayStickersInChats && !m_isPowerSavingMode;
            set
            {
                AppSettings.AutoPlayStickersInChats = value;
                RaisePropertyChanged();
            }
        }

        public static bool AutoPlayEmoji
        {
            get => AppSettings.AutoPlayEmoji && !m_isPowerSavingMode;
            set
            {
                AppSettings.AutoPlayEmoji = value;
                RaisePropertyChanged();
            }
        }

        public static bool AutoPlayEmojiInChats
        {
            get => AppSettings.AutoPlayEmojiInChats && !m_isPowerSavingMode;
            set
            {
                AppSettings.AutoPlayEmojiInChats = value;
                RaisePropertyChanged();
            }
        }

        public static bool AreSmoothTransitionsEnabled
        {
            get => AppSettings.AreSmoothTransitionsEnabled && m_uiSettings.AnimationsEnabled && !m_isPowerSavingMode;
            set
            {
                AppSettings.AreSmoothTransitionsEnabled = value;
                RaisePropertyChanged();
            }
        }

        public static bool AreCallsAnimated
        {
            get => AppSettings.AreCallsAnimated && !m_isPowerSavingMode;
            set
            {
                AppSettings.AreCallsAnimated = value;
                RaisePropertyChanged();
            }
        }

        private static void RaisePropertyChanged()
        {

        }
    }
}

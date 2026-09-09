//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Telegram.Services
{
    /// <summary>
    /// What DeviceInfoService reports to TDLib on Linux: the DMI product name as the device model
    /// and /etc/os-release as the system version, which is what the "Active sessions" list shows
    /// for this login. Read once; none of it changes while the process runs.
    /// </summary>
    internal static class LinuxSystemInfo
    {
        private const string DmiPath = "/sys/devices/virtual/dmi/id/";
        private const string OsReleasePath = "/etc/os-release";

        private static string _deviceModel;
        private static string _systemVersion;
        private static string _kernelVersion;
        private static string _applicationVersion;
        private static string _applicationVersion2;

        public static string DeviceModel => _deviceModel ??= ReadDeviceModel();

        public static string SystemVersion => _systemVersion ??= ReadSystemVersion();

        public static string KernelVersion => _kernelVersion ??= ReadKernelVersion();

        public static string ApplicationVersion => _applicationVersion ??= FormatApplicationVersion(false);

        public static string ApplicationVersion2 => _applicationVersion2 ??= FormatApplicationVersion(true);

        public static string SystemLanguageCode
        {
            get
            {
                var name = CultureInfo.CurrentUICulture.Name;
                return string.IsNullOrEmpty(name) ? "en" : name.ToLowerInvariant();
            }
        }

        private static string ReadDeviceModel()
        {
            // Firmware fills these with placeholders on boards that carry no product name, the
            // same way Windows does with "System Product Name", so those count as empty.
            var product = ReadDmi("product_name");
            var vendor = ReadDmi("sys_vendor");

            if (product != null)
            {
                return vendor != null && !product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)
                    ? vendor + " " + product
                    : product;
            }

            try
            {
                var host = Environment.MachineName;
                if (!string.IsNullOrEmpty(host))
                {
                    return host;
                }
            }
            catch
            {
                // gethostname can fail in a sandbox; the fallback below is fine.
            }

            return "Linux Desktop";
        }

        private static string ReadDmi(string name)
        {
            try
            {
                var value = File.ReadAllText(DmiPath + name).Trim();
                if (value.Length == 0
                    || value.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("Default string", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("OEM", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return value;
            }
            catch
            {
                // Not a DMI platform, or /sys is not mounted (container).
                return null;
            }
        }

        private static string ReadSystemVersion()
        {
            var pretty = ReadOsRelease("PRETTY_NAME");
            if (pretty != null)
            {
                return pretty;
            }

            var name = ReadOsRelease("NAME");
            var version = ReadOsRelease("VERSION_ID");

            if (name != null)
            {
                return version != null ? name + " " + version : name;
            }

            return "Linux";
        }

        private static string ReadOsRelease(string key)
        {
            try
            {
                foreach (var line in File.ReadLines(OsReleasePath))
                {
                    if (line.StartsWith(key + "=", StringComparison.Ordinal))
                    {
                        var value = line.Substring(key.Length + 1).Trim();
                        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
                        {
                            value = value.Substring(1, value.Length - 2);
                        }

                        return value.Length > 0 ? value : null;
                    }
                }
            }
            catch
            {
                // No /etc/os-release; the caller falls back to a plain "Linux".
            }

            return null;
        }

        private static string ReadKernelVersion()
        {
            var version = Environment.OSVersion.Version;
            return string.Format("{0}.{1}.{2}", version.Major, version.Minor, Math.Max(0, version.Build));
        }

        private static string FormatApplicationVersion(bool full)
        {
            var version = (Assembly.GetEntryAssembly() ?? typeof(LinuxSystemInfo).Assembly).GetName().Version ?? new Version(1, 0);

            var major = version.Major;
            var minor = Math.Max(0, version.Minor);
            var build = Math.Max(0, version.Build);
            var revision = Math.Max(0, version.Revision);

            if (full)
            {
                return string.Format("{0}.{1}.{2}.{3}", major, minor, build, revision);
            }

            var suffix = revision > 0
                ? string.Format(" ({0})", revision)
                : string.Empty;

            if (build > 0)
            {
                return string.Format("{0}.{1}.{2}{3}", major, minor, build, suffix);
            }

            return string.Format("{0}.{1}{2}", major, minor, suffix);
        }
    }
}

//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Telegram.Td.Api;
using Windows.Storage;

namespace Telegram.Services
{
    /// <summary>
    /// The Linux ProxyService. The Windows one keeps its list in LocalDatabase (winsqlite3) and
    /// follows the WinHTTP proxy through HttpProxyWatcher; here the list is proxies.json in the
    /// app data folder and the system proxy is whatever https_proxy the desktop session exports.
    /// Same semantics otherwise: EnabledProxyId in settings is the source of truth, -1 meaning the
    /// system proxy, 0 none, and every client gets the change pushed.
    /// </summary>
    public partial class ProxyService : IProxyService
    {
        private const string FileName = "proxies.json";

        private readonly ILifetimeService _lifetime;

        private readonly string _path;
        private readonly object _lock = new();
        private readonly List<AddedProxy> _proxies = new();
        private int _nextId = 1;

        public ProxyService(ILifetimeService lifetime)
        {
            _lifetime = lifetime;

            _path = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
            Load();

            // What HttpProxyWatcher does on Windows. dconf announces every write on the bus, so
            // there is nothing to poll: a user who changes the proxy in the Settings app while
            // Unigram is running gets it applied, instead of on the next start.
            _ = DesktopProxy.WatchAsync(OnSystemProxyChanged);
        }

        /// <summary>
        /// The desktop's proxy changed. Only interesting while the system proxy is the one in use
        /// (<c>EnabledProxyId == -1</c>); a user who picked a proxy of their own keeps it.
        /// <para>Runs on the D-Bus reader thread.</para>
        /// </summary>
        private void OnSystemProxyChanged()
        {
            if (AppSettings.EnabledProxyId != -1)
            {
                return;
            }

            Logger.Info("The desktop proxy changed, applying it");
            EnableSystemProxy();
        }

        public async void Migrate(int sessionId)
        {
            if (AppSettings.MigratedProxy)
            {
                if (AppSettings.EnabledProxyId == -1)
                {
                    EnableSystemProxy();
                }

                return;
            }

            var merged = new List<AddedProxy>();
            var enabled = default(AddedProxy);

            foreach (var client in _lifetime.ResolveAll<IClientService>())
            {
                var systemProxyId = await client.SendAsync(new GetOption(OptionsService.R.SystemProxy)) as OptionValueInteger;

                var response = await client.SendAsync(new GetProxies());
                if (response is AddedProxies proxies)
                {
                    foreach (var proxy in proxies.Proxies)
                    {
                        if (proxy.Id != systemProxyId?.Value && !merged.Any(x => AreTheSame(x.Proxy, proxy.Proxy)))
                        {
                            merged.Add(proxy);

                            if (client.SessionId == sessionId && proxy.IsEnabled)
                            {
                                enabled = proxy;
                            }
                        }
                    }
                }
            }

            if (merged.Count > 0)
            {
                lock (_lock)
                {
                    foreach (var item in merged)
                    {
                        item.Id = _nextId++;
                        item.IsEnabled = false;
                        _proxies.Add(item);
                    }

                    Save();
                }
            }

            // 12.10 folded the read-and-clear into SettingsService.ConsumeUseSystemProxy.
            if (SettingsService.ConsumeUseSystemProxy(sessionId))
            {
                EnableSystemProxy();
            }
            else if (enabled != null)
            {
                EnableProxy(enabled);
            }

            // Written only once the migration actually finished. This used to be set at the top
            // of the method: a throw anywhere in the fetch/merge loop above (every iteration is a
            // TDLib round trip that can fail) left the one-shot migration half-done with the flag
            // already saying "done" -- it would never run again. Async void, so nothing above this
            // method catches that throw; the flag simply has to wait until there is nothing left
            // to fail.
            AppSettings.MigratedProxy = true;
        }

        public async void Enable(IClientService clientService)
        {
            if (AppSettings.EnabledProxyId == -1)
            {
                if (TryGetSystemProxy(out var proxy))
                {
                    Enable(clientService, proxy);
                }
            }
            else if (AppSettings.EnabledProxyId != 0)
            {
                var enabled = GetProxyById(AppSettings.EnabledProxyId);
                if (enabled != null)
                {
                    Enable(clientService, enabled.Proxy);
                }
            }

            static async void Enable(IClientService clientService, Proxy proxy)
            {
                var proxyId = await clientService.SendAsync(new GetOption(OptionsService.R.Proxy)) as OptionValueInteger;
                if (proxyId != null)
                {
                    await clientService.SendAsync(new EditProxy((int)proxyId.Value, proxy, true, string.Empty));
                }
                else
                {
                    var added = await clientService.SendAsync(new AddProxy(proxy, true, string.Empty)) as AddedProxy;
                    if (added != null)
                    {
                        clientService.Options.Proxy = added.Id;
                    }
                }
            }
        }

        public AddedProxy AddProxy(Proxy proxy, bool enabled)
        {
            AddedProxy addedProxy;

            lock (_lock)
            {
                if (proxy == null || _proxies.Any(x => AreTheSame(x.Proxy, proxy)))
                {
                    return null;
                }

                addedProxy = new AddedProxy(_nextId++, 0, false, string.Empty, proxy);
                _proxies.Add(addedProxy);
                Save();
            }

            if (enabled)
            {
                EnableProxy(addedProxy);
            }

            return addedProxy;
        }

        public AddedProxy EditProxy(int proxyId, Proxy proxy)
        {
            var addedProxy = GetProxyById(proxyId);
            if (addedProxy == null)
            {
                return null;
            }

            lock (_lock)
            {
                addedProxy.Proxy = proxy;
                Save();
            }

            if (addedProxy.Id == AppSettings.EnabledProxyId)
            {
                EnableProxy(addedProxy);
            }

            return addedProxy;
        }

        public void RemoveProxy(int id)
        {
            // If deleting the currently enabled proxy, clear the setting
            if (AppSettings.EnabledProxyId == id)
            {
                DisableProxy();
            }

            lock (_lock)
            {
                if (_proxies.RemoveAll(x => x.Id == id) > 0)
                {
                    Save();
                }
            }
        }

        public AddedProxy GetProxyById(int id)
        {
            lock (_lock)
            {
                var proxy = _proxies.FirstOrDefault(x => x.Id == id);
                if (proxy != null)
                {
                    // Set IsEnabled based on settings
                    proxy.IsEnabled = AppSettings.EnabledProxyId == proxy.Id;
                }

                return proxy;
            }
        }

        public AddedProxy GetEnabledProxy()
        {
            if (AppSettings.EnabledProxyId != 0)
            {
                return GetProxyById(AppSettings.EnabledProxyId);
            }

            return null;
        }

        public int GetProxyCount()
        {
            lock (_lock)
            {
                return _proxies.Count;
            }
        }

        public async void EnableProxy(AddedProxy proxy)
        {
            int currentTimestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            proxy.LastUsedDate = currentTimestamp;

            if (proxy.Id > 0)
            {
                lock (_lock)
                {
                    Save();
                }
            }

            AppSettings.EnabledProxyId = proxy.Id;
            proxy.IsEnabled = true;

            foreach (var client in _lifetime.ResolveAll<IClientService>())
            {
                var proxyId = await client.SendAsync(new GetOption(OptionsService.R.Proxy)) as OptionValueInteger;
                if (proxyId != null)
                {
                    await client.SendAsync(new EditProxy((int)proxyId.Value, proxy.Proxy, true, string.Empty));
                }
                else
                {
                    var added = await client.SendAsync(new AddProxy(proxy.Proxy, true, string.Empty)) as AddedProxy;
                    if (added != null)
                    {
                        client.Options.Proxy = added.Id;
                    }
                }
            }
        }

        public void EnableProxy(int proxyId)
        {
            var proxy = GetProxyById(proxyId);
            if (proxy != null)
            {
                EnableProxy(proxy);
            }
        }

        public void EnableSystemProxy()
        {
            if (TryGetSystemProxy(out var proxy))
            {
                EnableProxy(new AddedProxy(-1, 0, true, string.Empty, proxy));
            }
            else
            {
                AppSettings.EnabledProxyId = -1;

                foreach (var client in _lifetime.ResolveAll<IClientService>())
                {
                    client.Send(new DisableProxy());
                }
            }
        }

        public void DisableProxy()
        {
            // If enabled proxy is -1 (system) we don't need to update settings
            if (AppSettings.EnabledProxyId > 0)
            {
                var proxy = GetProxyById(AppSettings.EnabledProxyId);
                if (proxy != null)
                {
                    lock (_lock)
                    {
                        proxy.LastUsedDate = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        Save();
                    }
                }
            }

            // Clear the enabled proxy ID from settings
            AppSettings.EnabledProxyId = 0;

            foreach (var client in _lifetime.ResolveAll<IClientService>())
            {
                client.Send(new DisableProxy());
            }
        }

        public Task<AddedProxies> GetProxiesAsync()
        {
            lock (_lock)
            {
                var items = _proxies
                    .OrderByDescending(x => x.LastUsedDate)
                    .Select(x => new AddedProxy(x.Id, x.LastUsedDate, AppSettings.EnabledProxyId == x.Id, string.Empty, x.Proxy))
                    .ToList();

                return Task.FromResult(new AddedProxies(items.ToVector()));
            }
        }

        /// <summary>
        /// The proxy the desktop is configured with. Two sources, in this order:
        ///
        /// <list type="number">
        /// <item>The desktop's own setting, <c>org.gnome.system.proxy</c>. This is where a user who
        /// opened Settings and typed a proxy put it, and it is the one that was missing: a session
        /// started from the display manager exports no proxy variables at all, so "use system
        /// proxy" did nothing on the machine where the proxy was actually configured. See
        /// Platform/DBus/DesktopProxy.cs.</item>
        /// <item>The environment, for a session that does export it and for the desktops that have
        /// no GSettings — a shell, a login with <c>https_proxy</c> in the profile, KDE.</item>
        /// </list>
        ///
        /// TDLib speaks HTTP CONNECT and SOCKS5, which is what both sources name.
        /// </summary>
        private static bool TryGetSystemProxy(out Proxy proxy)
        {
            if (DesktopProxy.TryGet(out var desktop))
            {
                ProxyType type = desktop.IsSocks
                    ? new ProxyTypeSocks5(desktop.Username, desktop.Password)
                    : new ProxyTypeHttp(desktop.Username, desktop.Password, false);

                proxy = new Proxy(desktop.Server, desktop.Port, type);
                Logger.Info($"System proxy from the desktop: {desktop}");
                return true;
            }

            foreach (var name in new[] { "https_proxy", "HTTPS_PROXY", "all_proxy", "ALL_PROXY", "http_proxy", "HTTP_PROXY" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!value.Contains("://"))
                {
                    value = "http://" + value;
                }

                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
                {
                    continue;
                }

                var username = string.Empty;
                var password = string.Empty;

                if (uri.UserInfo.Length > 0)
                {
                    var split = uri.UserInfo.Split(':', 2);
                    username = Uri.UnescapeDataString(split[0]);
                    password = split.Length > 1 ? Uri.UnescapeDataString(split[1]) : string.Empty;
                }

                ProxyType type = uri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
                    ? new ProxyTypeSocks5(username, password)
                    : new ProxyTypeHttp(username, password, false);

                proxy = new Proxy(uri.Host, uri.IsDefaultPort ? 80 : uri.Port, type);
                return true;
            }

            proxy = null;
            return false;
        }

        private bool AreTheSame(Proxy x, Proxy y)
        {
            if (x == null || y == null)
            {
                return x == y;
            }

            if (x.Server == y.Server && x.Port == y.Port)
            {
                if (x.Type is ProxyTypeMtproto xMtproto && y.Type is ProxyTypeMtproto yMtproto)
                {
                    return xMtproto.Secret == yMtproto.Secret;
                }
                else if (x.Type is ProxyTypeSocks5 xSocks5 && y.Type is ProxyTypeSocks5 ySocks5)
                {
                    return xSocks5.Username == ySocks5.Username
                        && xSocks5.Password == ySocks5.Password;
                }
                else if (x.Type is ProxyTypeHttp xHttp && y.Type is ProxyTypeHttp yHttp)
                {
                    return xHttp.Username == yHttp.Username
                        && xHttp.Password == yHttp.Password
                        && xHttp.HttpOnly == yHttp.HttpOnly;
                }
            }

            return false;
        }

        #region Storage

        // Called with _lock held
        private void Save()
        {
            try
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartArray();

                    foreach (var item in _proxies)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("id", item.Id);
                        writer.WriteNumber("lastUsedDate", item.LastUsedDate);
                        writer.WriteString("server", item.Proxy.Server);
                        writer.WriteNumber("port", item.Proxy.Port);

                        switch (item.Proxy.Type)
                        {
                            case ProxyTypeMtproto mtproto:
                                writer.WriteString("type", "mtproto");
                                writer.WriteString("secret", mtproto.Secret);
                                break;
                            case ProxyTypeSocks5 socks5:
                                writer.WriteString("type", "socks5");
                                writer.WriteString("username", socks5.Username ?? string.Empty);
                                writer.WriteString("password", socks5.Password ?? string.Empty);
                                break;
                            case ProxyTypeHttp http:
                                writer.WriteString("type", "http");
                                writer.WriteString("username", http.Username ?? string.Empty);
                                writer.WriteString("password", http.Password ?? string.Empty);
                                writer.WriteBoolean("httpOnly", http.HttpOnly);
                                break;
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                var temp = _path + ".tmp";
                System.IO.File.WriteAllBytes(temp, stream.ToArray());
                System.IO.File.Move(temp, _path, true);
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to write " + _path, ex);
            }
        }

        private void Load()
        {
            try
            {
                if (!System.IO.File.Exists(_path))
                {
                    return;
                }

                using var document = JsonDocument.Parse(System.IO.File.ReadAllBytes(_path));

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var server = item.GetProperty("server").GetString();
                    var port = item.GetProperty("port").GetInt32();

                    ProxyType type = item.GetProperty("type").GetString() switch
                    {
                        "mtproto" => new ProxyTypeMtproto(GetString(item, "secret")),
                        "socks5" => new ProxyTypeSocks5(GetString(item, "username"), GetString(item, "password")),
                        _ => new ProxyTypeHttp(GetString(item, "username"), GetString(item, "password"), item.TryGetProperty("httpOnly", out var httpOnly) && httpOnly.GetBoolean())
                    };

                    var id = item.GetProperty("id").GetInt32();
                    var lastUsedDate = item.TryGetProperty("lastUsedDate", out var date) ? date.GetInt32() : 0;

                    _proxies.Add(new AddedProxy(id, lastUsedDate, false, string.Empty, new Proxy(server, port, type)));
                    _nextId = Math.Max(_nextId, id + 1);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to read " + _path, ex);
                _proxies.Clear();
            }

            static string GetString(JsonElement element, string name)
            {
                return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : string.Empty;
            }
        }

        #endregion
    }
}

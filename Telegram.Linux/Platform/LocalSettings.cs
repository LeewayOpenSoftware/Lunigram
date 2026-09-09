//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Windows.Storage;

namespace Telegram.Services
{
    /// <summary>
    /// Stands in for <see cref="ApplicationData"/> in the files that build the settings tree.
    /// Uno Skia implements LocalFolder, but ApplicationDataContainer.CreateContainer, Containers
    /// and DeleteContainer are not implemented, and SettingsService keeps one container per
    /// session and per feature. Those files alias ApplicationData and ApplicationDataContainer
    /// to the two classes here, so their code reads as it does on Windows.
    /// </summary>
    public sealed class LocalApplicationData
    {
        public static LocalApplicationData Current { get; } = new();

        public StorageFolder LocalFolder => ApplicationData.Current.LocalFolder;

        public StorageFolder TemporaryFolder => ApplicationData.Current.TemporaryFolder;

        public LocalSettingsContainer LocalSettings => LocalSettingsContainer.Root;
    }

    /// <summary>
    /// A key-value container with named children, persisted as one JSON file for the whole tree
    /// in the app data folder. Writes are coalesced and flushed half a second later and again at
    /// process exit; values keep their exact CLR type across a restart, which the unboxing in
    /// SettingsServiceBase.GetValueOrDefault depends on.
    /// </summary>
    public sealed partial class LocalSettingsContainer
    {
        private const string FileName = "settings.json";
        private const int SaveDelay = 500;

        private static readonly object _rootLock = new();
        private static LocalSettingsContainer _root;

        private readonly Store _store;
        private readonly Dictionary<string, object> _values = new();
        private readonly Dictionary<string, LocalSettingsContainer> _containers = new();

        private LocalSettingsContainer(Store store, string name)
        {
            _store = store;
            Name = name;
            Values = new ValueMap(this);
        }

        public static LocalSettingsContainer Root
        {
            get
            {
                lock (_rootLock)
                {
                    return _root ??= Load();
                }
            }
        }

        public string Name { get; }

        public IDictionary<string, object> Values { get; }

        /// <summary>
        /// Writes the tree now, if it has changes the half second timer has not reached yet.
        /// </summary>
        /// <remarks>
        /// For the ways out that do not run <see cref="AppDomain.ProcessExit"/> - a signal, or an
        /// <c>exit()</c> made from native code - which is what <see cref="Common.ExitWatch"/>
        /// calls it for. It reads the field and never <see cref="Root"/>: building the tree here
        /// would touch <c>ApplicationData</c>, and doing that before the application exists
        /// poisons it for the rest of the process (see the note in LogFile.Open).
        /// </remarks>
        public static void FlushNow()
        {
            LocalSettingsContainer root;

            lock (_rootLock)
            {
                root = _root;
            }

            root?._store.Flush();
        }

        public IReadOnlyDictionary<string, LocalSettingsContainer> Containers
        {
            get
            {
                lock (_store.Sync)
                {
                    return new Dictionary<string, LocalSettingsContainer>(_containers);
                }
            }
        }

        public LocalSettingsContainer CreateContainer(string name, ApplicationDataCreateDisposition disposition)
        {
            lock (_store.Sync)
            {
                if (_containers.TryGetValue(name, out var container))
                {
                    return container;
                }

                if (disposition == ApplicationDataCreateDisposition.Existing)
                {
                    throw new KeyNotFoundException(name);
                }

                container = new LocalSettingsContainer(_store, name);
                _containers[name] = container;
                _store.MarkDirty();

                return container;
            }
        }

        public void DeleteContainer(string name)
        {
            lock (_store.Sync)
            {
                if (_containers.Remove(name))
                {
                    _store.MarkDirty();
                }
            }
        }

        /// <summary>
        /// Writes pending changes now. The store does this on its own after a delay and at
        /// process exit; this is for callers about to do something they do not expect to return
        /// from, such as restarting the app.
        /// </summary>
        public static void Flush()
        {
            lock (_rootLock)
            {
                _root?._store.Flush();
            }
        }

        private static LocalSettingsContainer Load()
        {
            var folder = ApplicationData.Current.LocalFolder.Path;
            var store = new Store(Path.Combine(folder, FileName));
            var root = new LocalSettingsContainer(store, string.Empty);
            store.Attach(root);

            try
            {
                if (File.Exists(store.Path))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(store.Path));
                    root.Read(document.RootElement);
                }
            }
            catch (Exception ex)
            {
                // A truncated write or a hand edit: start over rather than fail to launch, and
                // keep the file so the damage can be looked at. Not through Logger: that reads
                // SettingsService.Current, which is what is being constructed right now.
                LogFile.Write("Unable to read " + store.Path + "\n" + ex);

                try
                {
                    File.Copy(store.Path, store.Path + ".corrupt", true);
                }
                catch
                {
                    // The copy is a courtesy
                }

                root._values.Clear();
                root._containers.Clear();
            }

            return root;
        }

        #region Serialization

        // Each value is [type, value]: JSON alone cannot tell an int from a long, and the
        // settings code casts to the type it stored.
        private void Write(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("values");
            writer.WriteStartObject();

            foreach (var item in _values)
            {
                if (TryGetTypeCode(item.Value, out var code))
                {
                    writer.WritePropertyName(item.Key);
                    writer.WriteStartArray();
                    writer.WriteStringValue(code);
                    WriteValue(writer, code, item.Value);
                    writer.WriteEndArray();
                }
            }

            writer.WriteEndObject();

            writer.WritePropertyName("containers");
            writer.WriteStartObject();

            foreach (var item in _containers)
            {
                writer.WritePropertyName(item.Key);
                item.Value.Write(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private void Read(JsonElement element)
        {
            if (element.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in values.EnumerateObject())
                {
                    if (item.Value.ValueKind == JsonValueKind.Array && item.Value.GetArrayLength() == 2)
                    {
                        var code = item.Value[0].GetString();
                        if (TryReadValue(code, item.Value[1], out var value))
                        {
                            _values[item.Name] = value;
                        }
                    }
                }
            }

            if (element.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in containers.EnumerateObject())
                {
                    var container = new LocalSettingsContainer(_store, item.Name);
                    container.Read(item.Value);

                    _containers[item.Name] = container;
                }
            }
        }

        private static bool TryGetTypeCode(object value, out string code)
        {
            code = value switch
            {
                null => "n",
                bool => "b",
                byte => "u1",
                sbyte => "i1",
                short => "i2",
                ushort => "u2",
                int => "i4",
                uint => "u4",
                long => "i8",
                ulong => "u8",
                float => "r4",
                double => "r8",
                char => "c",
                string => "s",
                Guid => "g",
                DateTime => "dt",
                DateTimeOffset => "dto",
                TimeSpan => "ts",
                byte[] => "u1[]",
                int[] => "i4[]",
                long[] => "i8[]",
                string[] => "s[]",
                _ => null
            };

            if (code == null)
            {
                // WinRT refuses these outright; logging instead keeps a stray value from taking
                // the whole settings file with it.
                LogFile.Write("Unsupported settings value type " + value.GetType());
                return false;
            }

            return true;
        }

        private static void WriteValue(Utf8JsonWriter writer, string code, object value)
        {
            switch (code)
            {
                case "n":
                    writer.WriteNullValue();
                    break;
                case "b":
                    writer.WriteBooleanValue((bool)value);
                    break;
                case "u1":
                    writer.WriteNumberValue((byte)value);
                    break;
                case "i1":
                    writer.WriteNumberValue((sbyte)value);
                    break;
                case "i2":
                    writer.WriteNumberValue((short)value);
                    break;
                case "u2":
                    writer.WriteNumberValue((ushort)value);
                    break;
                case "i4":
                    writer.WriteNumberValue((int)value);
                    break;
                case "u4":
                    writer.WriteNumberValue((uint)value);
                    break;
                case "i8":
                    writer.WriteNumberValue((long)value);
                    break;
                case "u8":
                    writer.WriteNumberValue((ulong)value);
                    break;
                case "r4":
                    writer.WriteStringValue(((float)value).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case "r8":
                    writer.WriteStringValue(((double)value).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case "c":
                    writer.WriteStringValue(((char)value).ToString());
                    break;
                case "s":
                    writer.WriteStringValue((string)value);
                    break;
                case "g":
                    writer.WriteStringValue(((Guid)value).ToString("D"));
                    break;
                case "dt":
                    writer.WriteStringValue(((DateTime)value).ToString("O", CultureInfo.InvariantCulture));
                    break;
                case "dto":
                    writer.WriteStringValue(((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture));
                    break;
                case "ts":
                    writer.WriteNumberValue(((TimeSpan)value).Ticks);
                    break;
                case "u1[]":
                    writer.WriteBase64StringValue((byte[])value);
                    break;
                case "i4[]":
                    writer.WriteStartArray();
                    foreach (var item in (int[])value)
                    {
                        writer.WriteNumberValue(item);
                    }
                    writer.WriteEndArray();
                    break;
                case "i8[]":
                    writer.WriteStartArray();
                    foreach (var item in (long[])value)
                    {
                        writer.WriteNumberValue(item);
                    }
                    writer.WriteEndArray();
                    break;
                case "s[]":
                    writer.WriteStartArray();
                    foreach (var item in (string[])value)
                    {
                        writer.WriteStringValue(item);
                    }
                    writer.WriteEndArray();
                    break;
            }
        }

        private static bool TryReadValue(string code, JsonElement element, out object value)
        {
            try
            {
                value = code switch
                {
                    "n" => null,
                    "b" => element.GetBoolean(),
                    "u1" => element.GetByte(),
                    "i1" => element.GetSByte(),
                    "i2" => element.GetInt16(),
                    "u2" => element.GetUInt16(),
                    "i4" => element.GetInt32(),
                    "u4" => element.GetUInt32(),
                    "i8" => element.GetInt64(),
                    "u8" => element.GetUInt64(),
                    "r4" => float.Parse(element.GetString(), CultureInfo.InvariantCulture),
                    "r8" => double.Parse(element.GetString(), CultureInfo.InvariantCulture),
                    "c" => element.GetString()[0],
                    "s" => element.GetString(),
                    "g" => Guid.Parse(element.GetString()),
                    "dt" => DateTime.Parse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    "dto" => DateTimeOffset.Parse(element.GetString(), CultureInfo.InvariantCulture),
                    "ts" => TimeSpan.FromTicks(element.GetInt64()),
                    "u1[]" => element.GetBytesFromBase64(),
                    "i4[]" => ReadArray(element, x => x.GetInt32()),
                    "i8[]" => ReadArray(element, x => x.GetInt64()),
                    "s[]" => ReadArray(element, x => x.GetString()),
                    _ => throw new NotSupportedException(code)
                };

                return true;
            }
            catch (Exception ex)
            {
                // One bad entry is not a reason to drop the rest.
                LogFile.Write("Unable to read settings value " + code + ": " + ex.Message);

                value = null;
                return false;
            }
        }

        private static T[] ReadArray<T>(JsonElement element, Func<JsonElement, T> read)
        {
            var result = new T[element.GetArrayLength()];

            var i = 0;
            foreach (var item in element.EnumerateArray())
            {
                result[i++] = read(item);
            }

            return result;
        }

        #endregion

        /// <summary>
        /// The file and the lock, shared by every container of one tree.
        /// </summary>
        private sealed class Store
        {
            public readonly object Sync = new();

            private readonly object _flushLock = new();

            private LocalSettingsContainer _root;
            private Timer _timer;
            private bool _dirty;

            public Store(string path)
            {
                Path = path;
            }

            public string Path { get; }

            public void Attach(LocalSettingsContainer root)
            {
                _root = root;

                // Covers Environment.Exit, the end of Main and SIGTERM. A crash loses at most
                // the last half second of changes, the same window the timer leaves.
                AppDomain.CurrentDomain.ProcessExit += (s, args) => Flush();
            }

            // Called with Sync held
            public void MarkDirty()
            {
                _dirty = true;

                _timer ??= new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(SaveDelay, Timeout.Infinite);
            }

            private void OnTimer(object state)
            {
                Flush();
            }

            public void Flush()
            {
                // Serialization happens under Sync, the write under _flushLock, so two flushes
                // cannot put an older snapshot on disk after a newer one.
                lock (_flushLock)
                {
                    byte[] json;

                    lock (Sync)
                    {
                        if (!_dirty)
                        {
                            return;
                        }

                        _dirty = false;

                        using var stream = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                        {
                            _root.Write(writer);
                        }

                        json = stream.ToArray();
                    }

                    try
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));

                        var temp = Path + ".tmp";
                        File.WriteAllBytes(temp, json);
                        File.Move(temp, Path, true);
                    }
                    catch (Exception ex)
                    {
                        LogFile.Write("Unable to write " + Path + "\n" + ex);

                        lock (Sync)
                        {
                            // So the next change, or the exit, tries again.
                            _dirty = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The Values view: a dictionary whose every write marks the tree for saving. Reads take
        /// the tree lock too, because the TDLib thread reads settings while the UI writes them.
        /// </summary>
        private sealed class ValueMap : IDictionary<string, object>
        {
            private readonly LocalSettingsContainer _owner;

            public ValueMap(LocalSettingsContainer owner)
            {
                _owner = owner;
            }

            private object Sync => _owner._store.Sync;

            private Dictionary<string, object> Map => _owner._values;

            public object this[string key]
            {
                get
                {
                    lock (Sync)
                    {
                        return Map[key];
                    }
                }
                set
                {
                    lock (Sync)
                    {
                        Map[key] = value;
                        _owner._store.MarkDirty();
                    }
                }
            }

            public ICollection<string> Keys
            {
                get
                {
                    lock (Sync)
                    {
                        return new List<string>(Map.Keys);
                    }
                }
            }

            public ICollection<object> Values
            {
                get
                {
                    lock (Sync)
                    {
                        return new List<object>(Map.Values);
                    }
                }
            }

            public int Count
            {
                get
                {
                    lock (Sync)
                    {
                        return Map.Count;
                    }
                }
            }

            public bool IsReadOnly => false;

            public void Add(string key, object value)
            {
                lock (Sync)
                {
                    Map.Add(key, value);
                    _owner._store.MarkDirty();
                }
            }

            public void Add(KeyValuePair<string, object> item)
            {
                Add(item.Key, item.Value);
            }

            public void Clear()
            {
                lock (Sync)
                {
                    if (Map.Count > 0)
                    {
                        Map.Clear();
                        _owner._store.MarkDirty();
                    }
                }
            }

            public bool Contains(KeyValuePair<string, object> item)
            {
                lock (Sync)
                {
                    return Map.TryGetValue(item.Key, out var value) && Equals(value, item.Value);
                }
            }

            public bool ContainsKey(string key)
            {
                lock (Sync)
                {
                    return Map.ContainsKey(key);
                }
            }

            public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
            {
                lock (Sync)
                {
                    ((ICollection<KeyValuePair<string, object>>)Map).CopyTo(array, arrayIndex);
                }
            }

            public bool Remove(string key)
            {
                lock (Sync)
                {
                    if (Map.Remove(key))
                    {
                        _owner._store.MarkDirty();
                        return true;
                    }

                    return false;
                }
            }

            public bool Remove(KeyValuePair<string, object> item)
            {
                lock (Sync)
                {
                    return Contains(item) && Remove(item.Key);
                }
            }

            public bool TryGetValue(string key, out object value)
            {
                lock (Sync)
                {
                    return Map.TryGetValue(key, out value);
                }
            }

            public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
            {
                List<KeyValuePair<string, object>> snapshot;
                lock (Sync)
                {
                    snapshot = new List<KeyValuePair<string, object>>(Map);
                }

                return snapshot.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}

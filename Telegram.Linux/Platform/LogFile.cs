//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.IO;
using System.Text;
using Windows.Storage;
using Microsoft.UI.Xaml;

namespace Telegram
{
    /// <summary>
    /// Where Logger entries go on Linux: stderr, and unigram.log in the app data folder.
    /// Debug.WriteLine reaches nothing outside a debugger, and there are no crash minidumps to
    /// ship the tail with, so the file is the record of a session.
    /// </summary>
    /// <remarks>
    /// The file is created on the first entry rather than at startup: the app data folder is
    /// not known until Uno has initialized the package, and Logger can run before that.
    /// </remarks>
    internal static class LogFile
    {
        private const string FileName = "unigram.log";

        private static readonly object _lock = new();
        private static StreamWriter _writer;
        private static bool _failed;

        public static string Path { get; private set; }

        public static void Write(string entry)
        {
            Console.Error.WriteLine(entry);

            lock (_lock)
            {
                if (_failed)
                {
                    return;
                }

                try
                {
                    _writer ??= Open();
                    _writer?.WriteLine(entry);
                }
                catch
                {
                    // A full disk or a read-only home: stderr still has the entry, and retrying
                    // on every line would only add noise to it.
                    _failed = true;
                }
            }
        }

        private static StreamWriter Open()
        {
            // Too early: the package is not initialized yet, and Main logs before Uno builds the
            // application (the single instance check, the colour scheme probe). This is a null
            // check and not a try/catch around LocalFolder because that property is a Lazy in its
            // default mode, which CACHES the exception it throws: one early touch would leave the
            // app data folder unreachable for the rest of the process and kill the app later, in
            // its own constructor. Leave the file for the next entry, which still reaches stderr.
            if (Application.Current == null)
            {
                return null;
            }

            string folder;
            try
            {
                folder = ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                return null;
            }

            Directory.CreateDirectory(folder);
            Path = System.IO.Path.Combine(folder, FileName);

            var stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }
    }
}

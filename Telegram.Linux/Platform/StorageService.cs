//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Td.Api;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Microsoft.UI.Xaml;

namespace Telegram.Services
{
    public partial class DownloadFolder
    {
        public string DisplayPath { get; }

        public string Path { get; }

        public bool IsCustom { get; }

        public DownloadFolder(bool custom, StorageFolder folder)
        {
            DisplayPath = folder.Path;
            Path = folder.Path;

            IsCustom = custom;
        }

        public DownloadFolder(bool custom, string path)
        {
            DisplayPath = path;
            Path = path;

            IsCustom = custom;
        }

        public override string ToString()
        {
            return DisplayPath;
        }
    }

    public interface IStorageService
    {
        Task SaveFileAsAsync(XamlRoot xamlRoot, File file);

        Task OpenFileAsync(File file);

        Task OpenFileWithAsync(File file);

        Task CopyFilePathAsync(XamlRoot xamlRoot, File file);

        Task SaveFilesAsync(IEnumerable<File> files);

        Task OpenFolderAsync(File file);

        bool CheckAccessToFolder(File file);

        Task<DownloadFolder> GetDownloadFolderAsync();

        Task<DownloadFolder> SetDownloadFolderAsync(StorageFolder folder);
    }

    /// <summary>
    /// The Linux StorageService. The Windows one mirrors every manual download into the user's
    /// Downloads folder through FutureAccessList, which Uno does not implement, so here files
    /// stay in the TDLib cache (ApiInfo.HasCacheOnly) and the operations work on that copy:
    /// opening goes through the desktop's URI handler, saving through the file picker.
    /// </summary>
    public partial class StorageService : IStorageService
    {
        private readonly IClientService _clientService;

        public StorageService(IClientService clientService)
        {
            _clientService = clientService;
        }

        // xamlRoot is upstream's: the picker opens over a specific window there. This head has
        // one window and Uno drives its own pickers, so it is accepted and unused.
        public async Task SaveFileAsAsync(XamlRoot xamlRoot, File file)
        {
            // When saving a file as, we always want to retrieve the cached copy
            var cached = await _clientService.GetFileAsync(file);
            if (cached == null)
            {
                return;
            }

            var response = await _clientService.SendAsync(new GetSuggestedFileName(file.Id, string.Empty));
            if (response is not Text text)
            {
                return;
            }

            try
            {
                var extension = System.IO.Path.GetExtension(text.TextValue);

                // FileSavePicker doesn't support no exension.
                if (string.IsNullOrEmpty(extension))
                {
                    extension = ".dat";
                }

                var displayExtension = extension.TrimStart('.').ToUpper();
                var picker = new FileSavePicker();
                picker.FileTypeChoices.Add($"{displayExtension} File", new[] { extension });
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                picker.SuggestedFileName = text.TextValue;

                var picked = await picker.PickSaveFileAsync();
                if (picked != null)
                {
                    // Save as copy is never linked back
                    await cached.CopyAndReplaceAsync(picked);
                }
            }
            catch (Exception ex) { Logger.Error(nameof(SaveFileAsAsync), ex); }
        }

        public Task OpenFileAsync(File file)
        {
            return OpenFileAsync(file, false);
        }

        public Task OpenFileWithAsync(File file)
        {
            return OpenFileAsync(file, true);
        }

        private async Task OpenFileAsync(File file, bool displayApplicationPicker)
        {
            var permanent = await _clientService.GetPermanentFileAsync(file);
            if (permanent == null)
            {
                return;
            }

            try
            {
                // xdg-open picks the application; there is no "open with" chooser to ask for.
                var opened = await Launcher.LaunchUriAsync(new Uri(permanent.Path));
                if (opened)
                {
                    return;
                }

                await OpenFolderAsync(permanent);
            }
            catch (Exception ex) { Logger.Error(nameof(OpenFileAsync), ex); }
        }

        public async Task CopyFilePathAsync(XamlRoot xamlRoot, File file)
        {
            var cached = await _clientService.GetPermanentFileAsync(file);
            if (cached == null)
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(cached.Path);
            ClipboardEx.TrySetContent(dataPackage);

            ToastPopup.Show(xamlRoot, Strings.PathCopied, ToastPopupIcon.Copied);
        }

        public async Task SaveFilesAsync(IEnumerable<File> files)
        {
            try
            {
                var picker = new FolderPicker();

                var folder = await picker.PickSingleFolderAsync();
                if (folder == null)
                {
                    return;
                }

                foreach (var file in files)
                {
                    // When saving a file as, we always want to retrieve the cached copy
                    var cached = await _clientService.GetFileAsync(file);
                    if (cached == null)
                    {
                        return;
                    }

                    var response = await _clientService.SendAsync(new GetSuggestedFileName(file.Id, string.Empty));
                    if (response is not Text text)
                    {
                        return;
                    }

                    await cached.CopyAsync(folder, text.TextValue, NameCollisionOption.GenerateUniqueName);
                }

                await Launcher.LaunchUriAsync(new Uri(folder.Path));
            }
            catch (Exception ex) { Logger.Error(nameof(SaveFilesAsync), ex); }
        }

        public async Task OpenFolderAsync(File file)
        {
            var permanent = await _clientService.GetPermanentFileAsync(file);
            if (permanent == null)
            {
                return;
            }

            await OpenFolderAsync(permanent);
        }

        private static async Task OpenFolderAsync(StorageFile permanent)
        {
            try
            {
                var folder = System.IO.Path.GetDirectoryName(permanent.Path);
                if (folder != null)
                {
                    await Launcher.LaunchUriAsync(new Uri(folder));
                }
            }
            catch (Exception ex) { Logger.Error(nameof(OpenFolderAsync), ex); }
        }

        public bool CheckAccessToFolder(File file)
        {
            return file != null && file.Local.IsDownloadingCompleted;
        }

        public Task<DownloadFolder> GetDownloadFolderAsync()
        {
            return Future.GetFolderAsync();
        }

        public Task<DownloadFolder> SetDownloadFolderAsync(StorageFolder folder)
        {
            return Future.GetFolderAsync();
        }

        /// <summary>
        /// The FutureAccessList bookkeeping ClientService.Files.cs and Extensions.cs call into.
        /// With ApiInfo.HasCacheOnly every caller returns before reaching these, so they only
        /// have to exist and answer "nothing is linked".
        /// </summary>
        public static class Future
        {
            public const string DownloadFolder = "FilesDirectory";

            public static bool Contains(string token, bool temp = false)
            {
                return false;
            }

            public static void Remove(string token, bool temp = false)
            {
            }

            public static void AddOrReplace(string token, IStorageItem item, bool temp = false)
            {
            }

            public static bool CheckAccess(IStorageItem item)
            {
                return false;
            }

            public static Task<bool> ContainsAsync(string token, bool temp = false)
            {
                return Task.FromResult(false);
            }

            public static Task<StorageFile> GetFileAsync(string token, bool temp = false)
            {
                return Task.FromResult<StorageFile>(null);
            }

            public static Task<StorageFile> CreateFileAsync(string tempFileName)
            {
                return Task.FromResult<StorageFile>(null);
            }

            public static Task<DownloadFolder> GetFolderAsync()
            {
                return Task.FromResult<DownloadFolder>(null);
            }

            public static string Add(IStorageItem item)
            {
                return item?.Path;
            }
        }
    }
}

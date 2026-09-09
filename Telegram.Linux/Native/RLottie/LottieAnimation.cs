//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Telegram;
using Telegram.Native;
using Windows.Storage.Streams;

namespace RLottie
{
    public enum FitzModifier
    {
        None,
        Type12,
        Type3,
        Type4,
        Type5,
        Type6
    }

    /// <summary>
    /// Linux replacement for RLottie.winmd (Libraries/rlottie/x64), binding the lottie module of
    /// <c>libunigram-native.so</c> over the Unigram rlottie fork. Same surface the shared code
    /// expects: AnimatedImage.LoadLottie, LottieAnimatedImageTask, DiceView and
    /// StoryChannelInteractionBar.
    ///
    /// Contract: unigram-linux/native/unigram-native/include/unigram_native.h. The factories return
    /// null on any failure -- including "the native library is not there yet" -- which the callers
    /// already tolerate; nothing here throws at the caller.
    /// </summary>
    public sealed partial class LottieAnimation : IDisposable
    {
        /// <summary>
        /// Windows picks between the rlottie and tlottie rasterisers with this
        /// (SettingsService.cs sets it from Diagnostics.UseTLottieRenderer). There is one renderer
        /// on Linux, so the flag is only kept so the shared setter compiles.
        /// </summary>
        public static bool UseTLottie { get; set; }

        private readonly LottieAnimationHandle _handle;
        private readonly int _pixelWidth;
        private readonly int _pixelHeight;
        private readonly int _totalFrame;
        private readonly double _frameRate;

        // Packed BGRA, which is what the ABI writes and what WriteableBitmap.PixelBuffer holds.
        private readonly int _frameSize;

        // A render failure repeats every tick; it is only worth one line in the log.
        private bool _reported;

        private LottieAnimation(LottieAnimationHandle handle, LottieNative.LottieInfo info)
        {
            _handle = handle;
            _pixelWidth = info.PixelWidth;
            _pixelHeight = info.PixelHeight;
            _totalFrame = info.FrameCount;
            _frameRate = info.FrameRate;
            _frameSize = info.PixelWidth * info.PixelHeight * 4;
        }

        public static LottieAnimation LoadFromFile(string filePath, int pixelWidth, int pixelHeight, bool precache, IReadOnlyDictionary<int, int> colorReplacements)
        {
            return LoadFromFile(filePath, pixelWidth, pixelHeight, precache, colorReplacements, FitzModifier.None);
        }

        public static unsafe LottieAnimation LoadFromFile(string filePath, int pixelWidth, int pixelHeight, bool precache, IReadOnlyDictionary<int, int> colorReplacements, FitzModifier modifier)
        {
            if (!UnigramNative.IsAvailable || string.IsNullOrEmpty(filePath) || !IsValidSize(pixelWidth, pixelHeight))
            {
                return null;
            }

            try
            {
                var replacements = Flatten(colorReplacements);

                fixed (UnigramColorReplacement* pinned = replacements)
                {
                    var options = CreateOptions(pixelWidth, pixelHeight, precache, modifier, pinned, replacements?.Length ?? 0, IntPtr.Zero);
                    return FromHandle(LottieNative.OpenFile(filePath, ref options), filePath);
                }
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"rlottie: LoadFromFile({filePath})", ex);
                return null;
            }
        }

        public static LottieAnimation LoadFromData(string jsonData, int pixelWidth, int pixelHeight, string cacheKey, bool precache, IReadOnlyDictionary<int, int> colorReplacements)
        {
            return LoadFromData(jsonData, pixelWidth, pixelHeight, cacheKey, precache, colorReplacements, FitzModifier.None);
        }

        public static unsafe LottieAnimation LoadFromData(string jsonData, int pixelWidth, int pixelHeight, string cacheKey, bool precache, IReadOnlyDictionary<int, int> colorReplacements, FitzModifier modifier)
        {
            if (!UnigramNative.IsAvailable || string.IsNullOrEmpty(jsonData) || !IsValidSize(pixelWidth, pixelHeight))
            {
                return null;
            }

            var count = Encoding.UTF8.GetByteCount(jsonData);
            var bytes = ArrayPool<byte>.Shared.Rent(count);
            var key = IntPtr.Zero;

            try
            {
                Encoding.UTF8.GetBytes(jsonData.AsSpan(), bytes.AsSpan(0, count));
                key = Marshal.StringToCoTaskMemUTF8(ModelCacheKey(cacheKey, jsonData));

                var replacements = Flatten(colorReplacements);

                fixed (byte* data = bytes)
                fixed (UnigramColorReplacement* pinned = replacements)
                {
                    // precache is ignored by the ABI for a memory source (a blob has no stable
                    // identity to key a cache file on); it is passed through anyway so the library
                    // decides, not this binding.
                    var options = CreateOptions(pixelWidth, pixelHeight, precache, modifier, pinned, replacements?.Length ?? 0, key);
                    return FromHandle(LottieNative.OpenData(data, count, ref options), cacheKey);
                }
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"rlottie: LoadFromData({cacheKey})", ex);
                return null;
            }
            finally
            {
                if (key != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(key);
                }

                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        public unsafe void RenderSync(IBuffer bitmap, int frame)
        {
            if (bitmap == null || _frameSize <= 0)
            {
                return;
            }

            // PixelBuffer wraps a WriteableBitmap; the extension methods below only accept Uno's
            // own Buffer, so unwrap first (same as BufferSurface).
            var target = PixelBuffer.Unwrap(bitmap);
            if (target.Capacity < _frameSize)
            {
                return;
            }

            // One copy: the ABI renders straight into a caller pointer, but Uno keeps the bytes
            // behind IBuffer internal (Windows.Storage.Streams.Buffer.Span / GetSegment /
            // ApplyActionOnRawBufferPtr are all internal to Uno.dll), so there is no supported way
            // to pin the destination. Drop the staging buffer if Uno ever exposes it.
            var pixels = ArrayPool<byte>.Shared.Rent(_frameSize);

            try
            {
                int status;
                fixed (byte* buffer = pixels)
                {
                    // stride 0 = packed (pixel_width * 4), which is the WriteableBitmap layout.
                    status = LottieNative.Render(_handle, frame, buffer, 0, _frameSize);
                }

                if (status != (int)LottieNative.Status.Ok)
                {
                    // Busy is the normal "the frame cache is being written" answer; the caller
                    // already skips those ticks through IsCaching. Anything else is reported once:
                    // this runs 30 to 60 times a second per animation, and Logger.Log writes to the
                    // log file and to TDLib.
                    if (status != (int)LottieNative.Status.Busy && !_reported)
                    {
                        _reported = true;
                        UnigramNative.Report(Logger.LogLevel.Warning, $"rlottie: render({frame}) failed with {status}: {UnigramNative.LastError()}");
                    }

                    return;
                }

                pixels.CopyTo(0, target, 0, _frameSize);

                if (target.Length < _frameSize)
                {
                    target.Length = (uint)_frameSize;
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed from another thread while this frame was in flight.
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"rlottie: render({frame})", ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }

        public void Cache()
        {
            try
            {
                LottieNative.Cache(_handle);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "rlottie: cache", ex);
            }
        }

        public double FrameRate => _frameRate;

        public int TotalFrame => _totalFrame;

        // Both are polled once per frame by LottieAnimatedImageTask.NextFrame, so they go straight
        // to the library: no delegate, no allocation.
        public bool IsReadyToCache
        {
            get
            {
                try
                {
                    return LottieNative.IsReadyToCache(_handle) != 0;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public bool IsCaching
        {
            get
            {
                try
                {
                    return LottieNative.IsCaching(_handle) != 0;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public int PixelWidth => _pixelWidth;

        public int PixelHeight => _pixelHeight;

        public void Dispose()
        {
            // SafeHandle: idempotent, and it will not release the animation while a render started
            // on another thread is still inside the library.
            _handle.Dispose();
        }

        private static bool IsValidSize(int pixelWidth, int pixelHeight)
        {
            // The ABI rejects anything outside this range; catching it here keeps the failure a
            // null instead of a logged native error.
            return pixelWidth > 0 && pixelHeight > 0 && pixelWidth <= 4096 && pixelHeight <= 4096;
        }

        private static LottieAnimation FromHandle(LottieAnimationHandle handle, string source)
        {
            if (handle == null || handle.IsInvalid)
            {
                // Not exceptional: a partially downloaded sticker, a .webm mislabelled as .tgs...
                UnigramNative.Report(Logger.LogLevel.Debug, $"rlottie: cannot open {source}: {UnigramNative.LastError()}");
                handle?.Dispose();
                return null;
            }

            var info = new LottieNative.LottieInfo
            {
                StructSize = Marshal.SizeOf<LottieNative.LottieInfo>()
            };

            if (LottieNative.GetInfo(handle, ref info) != (int)LottieNative.Status.Ok
                || info.FrameCount <= 0
                || info.PixelWidth <= 0
                || info.PixelHeight <= 0)
            {
                UnigramNative.Report(Logger.LogLevel.Debug, $"rlottie: no usable metadata for {source}: {UnigramNative.LastError()}");
                handle.Dispose();
                return null;
            }

            return new LottieAnimation(handle, info);
        }

        private static unsafe LottieNative.LottieOptions CreateOptions(int pixelWidth, int pixelHeight, bool precache, FitzModifier modifier, UnigramColorReplacement* replacements, int replacementCount, IntPtr cacheKey)
        {
            return new LottieNative.LottieOptions
            {
                StructSize = Marshal.SizeOf<LottieNative.LottieOptions>(),
                Width = pixelWidth,
                Height = pixelHeight,
                Precache = precache ? 1 : 0,
                Fitz = (int)modifier,
                ReplacementCount = replacementCount,
                Replacements = (IntPtr)replacements,
                CacheKey = cacheKey
            };
        }

        private static UnigramColorReplacement[] Flatten(IReadOnlyDictionary<int, int> colorReplacements)
        {
            if (colorReplacements == null || colorReplacements.Count == 0)
            {
                return null;
            }

            var result = new UnigramColorReplacement[colorReplacements.Count];
            var index = 0;

            foreach (var pair in colorReplacements)
            {
                result[index++] = new UnigramColorReplacement
                {
                    From = unchecked((uint)pair.Key),
                    To = unchecked((uint)pair.Value)
                };
            }

            return result;
        }

        /// <summary>
        /// The ABI keys rlottie's parsed-model cache on this string and requires that two different
        /// JSONs never share one, but DiceView passes just the dice value ("1".."64"), which
        /// repeats across sticker sets. The cache is in-memory, so a process-local hash of the
        /// payload is enough to keep the variants apart.
        /// </summary>
        private static string ModelCacheKey(string cacheKey, string jsonData)
        {
            if (string.IsNullOrEmpty(cacheKey))
            {
                return null;
            }

            return $"{cacheKey}:{jsonData.Length:x}:{jsonData.GetHashCode():x8}";
        }
    }

    /// <summary>
    /// Owns an <c>unigram_lottie_animation*</c>. A SafeHandle rather than a raw pointer because
    /// AnimatedImage disposes animations from the UI thread while a render may still be running on
    /// a worker: the handle count keeps the pointer alive until that call returns.
    /// </summary>
    internal sealed class LottieAnimationHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LottieAnimationHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            LottieNative.Close(handle);
            return true;
        }
    }

    /// <summary>
    /// Lottie module of <c>libunigram-native.so</c>, one to one with the <c>unigram_lottie_*</c>
    /// section of unigram_native.h. Nothing here is called unless
    /// <see cref="UnigramNative.IsAvailable"/>.
    /// </summary>
    internal static partial class LottieNative
    {
        internal enum Status
        {
            Ok = 0,
            Error = -1,
            InvalidArgument = -2,
            OutOfMemory = -3,
            IO = -4,
            Format = -5,
            OutOfRange = -6,
            BufferTooSmall = -7,
            Busy = -8,
            Closed = -9
        }

        /// <summary>
        /// <c>unigram_lottie_options</c>: 40 bytes on LP64, no padding, and the caller fills
        /// <see cref="StructSize"/> so the library knows which fields it may read.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct LottieOptions
        {
            public int StructSize;
            public int Width;
            public int Height;
            public int Precache;
            public int Fitz;
            public int ReplacementCount;
            public IntPtr Replacements;
            public IntPtr CacheKey;
        }

        /// <summary>
        /// <c>unigram_lottie_info</c>: 40 bytes on LP64. Read once at load time and cached.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct LottieInfo
        {
            public int StructSize;
            public int PixelWidth;
            public int PixelHeight;
            public int FrameCount;
            public double FrameRate;
            public double Duration;
            public int FromCache;
            public int Reserved;
        }

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_open_file", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial LottieAnimationHandle OpenFile(string path, ref LottieOptions options);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_open_data")]
        internal static unsafe partial LottieAnimationHandle OpenData(byte* data, int size, ref LottieOptions options);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_close")]
        internal static partial void Close(IntPtr animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_get_info")]
        internal static partial int GetInfo(LottieAnimationHandle animation, ref LottieInfo info);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_render")]
        internal static unsafe partial int Render(LottieAnimationHandle animation, int frameIndex, byte* pixels, int stride, int capacity);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_is_ready_to_cache")]
        internal static partial int IsReadyToCache(LottieAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_is_caching")]
        internal static partial int IsCaching(LottieAnimationHandle animation);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_lottie_cache")]
        internal static partial int Cache(LottieAnimationHandle animation);
    }
}

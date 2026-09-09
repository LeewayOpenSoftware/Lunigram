//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Telegram.Native
{
    /// <summary>
    /// Linux replacement for Telegram.Native.LanguageIdentification, binding the langid module of
    /// <c>libunigram-native.so</c> (libtextclassifier's lang_id over the same
    /// Assets/langid_model.smfb.jpg the Windows build ships).
    ///
    /// <para>Answers "what language is this message in?" for the translate bar
    /// (TranslateService.CanTranslate), the translate popup, the story caption menu and the
    /// gallery's recognised text.</para>
    ///
    /// <para>Two return values matter to the callers and they are NOT the same thing:
    /// <c>"und"</c> means the model looked and could not decide -- which is what Windows returns
    /// too -- while an empty string means there was nothing to ask: no library, no model, no text.
    /// CanTranslate treats both as "do not offer a translation", so a missing model degrades to
    /// the phase 1 behaviour instead of throwing.</para>
    /// </summary>
    public static partial class LanguageIdentification
    {
        /// <summary>
        /// Development override for the model file. The real lookup is
        /// <see cref="NativeAssets.Resolve"/>: Assets/ next to the binary.
        /// </summary>
        private const string ModelVariable = "UNIGRAM_LANGID_MODEL";

        /// <summary>
        /// UNIGRAM_LANGID_UNKNOWN. The callers compare against this ("und" is TranslateService's
        /// LANG_UND), so it is not an implementation detail.
        /// </summary>
        public const string Unknown = "und";

        /// <summary>
        /// UNIGRAM_LANGID_CODE_MAX: the longest BCP-47 code in the model plus its terminator.
        /// </summary>
        private const int CodeMax = 24;

        // The model is ~370 KB of flatbuffer and its load is mmap + a checksum, so it is done once,
        // lazily, on whichever thread asks first. Lazy<T> because IdentifyLanguage is called from
        // the UI thread AND from message-processing threads.
        private static readonly Lazy<bool> _initialized = new(Initialize, isThreadSafe: true);

        private static bool Initialize()
        {
            if (!UnigramNative.IsAvailable)
            {
                return false;
            }

            var model = NativeAssets.Resolve(NativeAssets.LanguageModel, ModelVariable);
            if (model == null)
            {
                UnigramNative.Report(Logger.LogLevel.Warning,
                    $"langid: {NativeAssets.LanguageModel} not found next to the binary: language detection disabled");
                return false;
            }

            try
            {
                // Passing the path is the whole difference from Windows, where it was the literal
                // "Assets\\langid_model.smfb.jpg" resolved against the package directory. See
                // NativeAssets.
                if (unigram_langid_init(model) == 0)
                {
                    UnigramNative.Report(Logger.LogLevel.Error,
                        $"langid: cannot load {model}: {UnigramNative.LastError()}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"langid: init({model})", ex);
                return false;
            }
        }

        /// <summary>
        /// Most likely language of <paramref name="text"/> as a BCP-47 code, <see cref="Unknown"/>
        /// when the model will not commit, or an empty string when identification is unavailable.
        /// Never throws.
        /// </summary>
        public static unsafe string IdentifyLanguage(string text)
        {
            if (string.IsNullOrEmpty(text) || !_initialized.Value)
            {
                return string.Empty;
            }

            // The ABI takes UTF-8 bytes and a byte length. Encoding here (rather than letting the
            // marshaller allocate a NUL-terminated copy) keeps a long message off the LOH and lets
            // an embedded NUL through unharmed -- a message really can contain one.
            var count = Encoding.UTF8.GetByteCount(text);
            var bytes = ArrayPool<byte>.Shared.Rent(count);

            try
            {
                Encoding.UTF8.GetBytes(text.AsSpan(), bytes.AsSpan(0, count));

                var code = stackalloc byte[CodeMax];

                fixed (byte* buffer = bytes)
                {
                    var written = unigram_langid_identify(buffer, (nuint)count, code, CodeMax);
                    if (written <= 0)
                    {
                        // -1 is a bad argument (cannot happen here); 0 would be an empty code.
                        return string.Empty;
                    }

                    return Marshal.PtrToStringUTF8((IntPtr)code, written);
                }
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "langid: identify", ex);
                return string.Empty;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_langid_init", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int unigram_langid_init(string modelPath);

        // size_t on the boundary, so nuint: this module's header predates the "no size_t" rule of
        // unigram_native.h and keeps the shape it was tested with.
        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_langid_identify")]
        private static unsafe partial int unigram_langid_identify(byte* text, nuint length, byte* outLanguage, nuint outSize);
    }
}

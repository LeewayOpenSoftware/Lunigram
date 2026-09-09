//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Telegram.Native.Highlight
{
    /// <summary>
    /// Linux replacement for Telegram.Native.Highlight (SyntaxToken / TextToken), binding the
    /// highlight module of <c>libunigram-native.so</c> -- libprisma over the same
    /// Libraries/libprisma/libprisma/grammars.dat the Windows build packages as Assets\grammars.dat.
    ///
    /// <para>Consumed by FormattedTextBlock.ProcessCodeBlock, which walks the tree and turns every
    /// SyntaxToken into a coloured Span and every TextToken into a Run, and by
    /// TextEditorRichPopup, which lists <see cref="SyntaxToken.Languages"/>.</para>
    ///
    /// <para>SHAPE OF THE DATA. The Windows component returned a tree of runtimeclasses; the ABI
    /// returns that same tree flattened into an array of (offset, length, depth, type, alias) in
    /// preorder, with offsets in BYTES over the UTF-8 text -- crossing a tree of nodes with
    /// entangled lifetimes over a C boundary would have been the worse trade. The tree is rebuilt
    /// here, in <see cref="Build"/>: the children of a token are the tokens that follow it while
    /// their depth is greater. Text is sliced out of the same UTF-8 buffer that was handed to the
    /// library, so the offsets need no translation.</para>
    /// </summary>
    public partial class Token
    {
    }

    public sealed partial class SyntaxToken : Token
    {
        /// <summary>
        /// Development override for the grammars file; the real lookup is
        /// <see cref="NativeAssets.Resolve"/>.
        /// </summary>
        private const string GrammarsVariable = "UNIGRAM_GRAMMARS";

        private SyntaxToken(string type, string alias, IList<Token> children)
        {
            Type = type;
            Alias = alias;
            Children = children;
        }

        public string Type { get; }

        public string Alias { get; }

        public IList<Token> Children { get; }

        // grammars.dat is ~600 KB and parsing it builds ~380 grammars worth of compiled regexes:
        // once, on first use, whichever thread gets there first.
        private static readonly Lazy<bool> _initialized = new(Initialize, isThreadSafe: true);

        // Lazy, not a static initializer: reading the list has to load the grammars, and merely
        // touching this type (every code block does) must not pay for that.
        private static readonly Lazy<IList<string>> _languages = new(LoadLanguages, isThreadSafe: true);

        /// <summary>
        /// Every language the loaded grammars know, for the code block language picker. Empty when
        /// the module is unavailable, which leaves the picker empty rather than breaking it.
        /// </summary>
        public static IList<string> Languages => _languages.Value;

        private static bool Initialize()
        {
            if (!UnigramNative.IsAvailable)
            {
                return false;
            }

            var grammars = NativeAssets.Resolve(NativeAssets.Grammars, GrammarsVariable);
            if (grammars == null)
            {
                UnigramNative.Report(Logger.LogLevel.Warning,
                    $"highlight: {NativeAssets.Grammars} not found next to the binary: code blocks stay plain");
                return false;
            }

            try
            {
                // The path is an argument now; on Windows it was the literal "Assets\\grammars.dat"
                // resolved against the package directory. See NativeAssets.
                if (unigram_highlight_init(grammars) == 0)
                {
                    UnigramNative.Report(Logger.LogLevel.Error,
                        $"highlight: cannot load {grammars}: {UnigramNative.LastError()}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"highlight: init({grammars})", ex);
                return false;
            }
        }

        private static IList<string> LoadLanguages()
        {
            var languages = new List<string>();

            if (!_initialized.Value)
            {
                return languages;
            }

            try
            {
                var count = unigram_highlight_language_count();
                for (int i = 0; i < count; i++)
                {
                    var code = Marshal.PtrToStringUTF8(unigram_highlight_language_code(i));
                    if (!string.IsNullOrEmpty(code))
                    {
                        languages.Add(code);
                    }
                }
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, "highlight: languages", ex);
            }

            return languages;
        }

        /// <summary>
        /// Tokenizes <paramref name="code"/> with the grammar of <paramref name="language"/>.
        /// Never throws and never returns null: an unknown language, a missing grammars file or a
        /// missing library all come back as a root holding the whole block as one TextToken, which
        /// FormattedTextBlock renders as plain monospace text.
        /// </summary>
        public static unsafe SyntaxToken Tokenize(string language, string code)
        {
            code ??= string.Empty;

            if (string.IsNullOrEmpty(language) || code.Length == 0 || !_initialized.Value)
            {
                return Plain(code);
            }

            IntPtr result = IntPtr.Zero;

            try
            {
                // One UTF-8 copy, used both to call the library and to slice the text back out:
                // the offsets it returns index THIS array. Not pooled -- a rented array can be
                // longer than the content and every slice here is bounds-checked against Length.
                var bytes = Encoding.UTF8.GetBytes(code);

                fixed (byte* buffer = bytes)
                {
                    result = unigram_highlight_tokenize(language, buffer, (nuint)bytes.Length);
                }

                if (result == IntPtr.Zero)
                {
                    UnigramNative.Report(Logger.LogLevel.Debug,
                        $"highlight: tokenize({language}) failed: {UnigramNative.LastError()}");
                    return Plain(code);
                }

                var count = unigram_highlight_result_count(result);
                if (count <= 0)
                {
                    return Plain(code);
                }

                var tokens = (HighlightToken*)unigram_highlight_result_tokens(result);
                if (tokens == null)
                {
                    return Plain(code);
                }

                var index = 0;
                var strings = new Dictionary<IntPtr, string>();
                var children = Build(tokens, count, ref index, 0, bytes, strings);

                return new SyntaxToken(string.Empty, string.Empty, children);
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"highlight: tokenize({language})", ex);
                return Plain(code);
            }
            finally
            {
                if (result != IntPtr.Zero)
                {
                    unigram_highlight_result_free(result);
                }
            }
        }

        /// <summary>
        /// Off the UI thread, because tokenizing a long block is milliseconds of regex work. The
        /// Windows component did the same with IAsyncOperation + resume_background; the ABI is
        /// deliberately synchronous and thread-safe, so the thread is chosen here.
        /// </summary>
        public static Task<SyntaxToken> TokenizeAsync(string language, string code)
        {
            if (string.IsNullOrEmpty(language) || string.IsNullOrEmpty(code) || !UnigramNative.IsAvailable)
            {
                return Task.FromResult(Plain(code ?? string.Empty));
            }

            return Task.Run(() => Tokenize(language, code));
        }

        /// <summary>
        /// "cpp" -> "C++". Unknown codes come back unchanged, exactly as on Windows.
        /// </summary>
        public static string GetLanguageName(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode) || !_initialized.Value)
            {
                return languageCode;
            }

            try
            {
                // Borrowed pointer into the loaded grammar table: copied here, never freed.
                return Marshal.PtrToStringUTF8(unigram_highlight_language_name(languageCode)) ?? languageCode;
            }
            catch (Exception ex)
            {
                UnigramNative.Report(Logger.LogLevel.Error, $"highlight: name({languageCode})", ex);
                return languageCode;
            }
        }

        public override string ToString()
        {
            return Type;
        }

        /// <summary>
        /// The whole block as one text run: the answer whenever highlighting is not available.
        /// </summary>
        private static SyntaxToken Plain(string code)
        {
            return new SyntaxToken(string.Empty, string.Empty, new List<Token> { new TextToken(code) });
        }

        /// <summary>
        /// Rebuilds one level of the tree from the flat preorder array, consuming tokens while they
        /// sit at <paramref name="depth"/> and recursing into anything deeper.
        /// </summary>
        private static unsafe IList<Token> Build(HighlightToken* tokens, int count, ref int index,
            int depth, byte[] bytes, Dictionary<IntPtr, string> strings)
        {
            var list = new List<Token>();

            while (index < count && tokens[index].Depth == depth)
            {
                var token = tokens[index++];

                if (token.IsSyntax == 0)
                {
                    list.Add(new TextToken(Slice(bytes, token)));
                    continue;
                }

                var children = Build(tokens, count, ref index, depth + 1, bytes, strings);

                // A syntax token with no children of its own still covers text, and
                // ProcessCodeBlock only ever emits a Run for a TextToken: without this the block
                // would render a coloured but EMPTY span and the text would be gone.
                if (children.Count == 0)
                {
                    children.Add(new TextToken(Slice(bytes, token)));
                }

                list.Add(new SyntaxToken(Intern(strings, token.Type), Intern(strings, token.Alias), children));
            }

            return list;
        }

        /// <summary>
        /// The library interns its own type/alias strings, so the same pointer comes back for every
        /// "keyword" in the block: one managed string per distinct type instead of one per token.
        /// </summary>
        private static string Intern(Dictionary<IntPtr, string> strings, IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            if (!strings.TryGetValue(pointer, out var value))
            {
                value = Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
                strings[pointer] = value;
            }

            return value;
        }

        /// <summary>
        /// The token's text. Offsets are byte offsets into the UTF-8 buffer that was passed in, and
        /// they are validated: a grammar that reported a length past the end of the source would
        /// otherwise be an out-of-range read.
        /// </summary>
        private static string Slice(byte[] bytes, HighlightToken token)
        {
            var offset = (long)token.Offset;
            var length = (long)token.Length;

            if (offset < 0 || length <= 0 || offset + length > bytes.Length)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(bytes, (int)offset, (int)length);
        }

        /// <summary>
        /// <c>unigram_highlight_token</c>: 32 bytes on LP64 (12 of fields, 4 of padding, two
        /// pointers). Sequential layout gives the pointers the same 8-byte alignment the C
        /// compiler does.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HighlightToken
        {
            public uint Offset;
            public uint Length;
            public ushort Depth;
            public ushort IsSyntax;
            public IntPtr Type;
            public IntPtr Alias;
        }

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_init", StringMarshalling = StringMarshalling.Utf8)]
        private static partial int unigram_highlight_init(string grammarsPath);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_language_count")]
        private static partial int unigram_highlight_language_count();

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_language_code")]
        private static partial IntPtr unigram_highlight_language_code(int index);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_language_name", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr unigram_highlight_language_name(string languageCode);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_tokenize", StringMarshalling = StringMarshalling.Utf8)]
        private static unsafe partial IntPtr unigram_highlight_tokenize(string language, byte* code, nuint length);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_result_count")]
        private static partial int unigram_highlight_result_count(IntPtr result);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_result_tokens")]
        private static partial IntPtr unigram_highlight_result_tokens(IntPtr result);

        [LibraryImport(UnigramNative.Library, EntryPoint = "unigram_highlight_result_free")]
        private static partial void unigram_highlight_result_free(IntPtr result);
    }

    public sealed partial class TextToken : Token
    {
        public TextToken(string value)
        {
            Value = value;
        }

        public string Value { get; }

        public override string ToString()
        {
            return Value;
        }
    }
}

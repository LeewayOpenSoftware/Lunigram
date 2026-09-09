//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace Telegram.Native.AI
{
    public struct RecognizedTextBoundingBox
    {
        public Vector2 TopLeft;
        public Vector2 TopRight;
        public Vector2 BottomRight;
        public Vector2 BottomLeft;
    }

    public interface IOcrObject
    {
        RecognizedTextBoundingBox BoundingBox { get; }
    }

    public sealed partial class RecognizedWord : IOcrObject
    {
        public RecognizedWord(string text, RecognizedTextBoundingBox boundingBox)
        {
            Text = text;
            BoundingBox = boundingBox;
        }

        public string Text { get; }

        public RecognizedTextBoundingBox BoundingBox { get; }

        public override string ToString()
        {
            return Text;
        }
    }

    public sealed partial class RecognizedLine : IOcrObject
    {
        public RecognizedLine(string text, RecognizedTextBoundingBox boundingBox, IList<RecognizedWord> words, bool isBarcode)
        {
            Text = text;
            BoundingBox = boundingBox;
            Words = words ?? new List<RecognizedWord>();
            IsBarcode = isBarcode;
        }

        public string Text { get; }

        public IList<RecognizedWord> Words { get; }

        public bool IsBarcode { get; }

        public RecognizedTextBoundingBox BoundingBox { get; }

        public override string ToString()
        {
            return Text;
        }
    }

    public sealed partial class RecognizedText
    {
        public RecognizedText(IList<RecognizedLine> lines, float textAngle)
        {
            Lines = lines ?? new List<RecognizedLine>();
            TextAngle = textAngle;
        }

        public IList<RecognizedLine> Lines { get; }

        public float TextAngle { get; }
    }

    public interface ITextRecognizer
    {
        Task<RecognizedText> RecognizeAsync(SoftwareBitmap bitmap);
    }

    public sealed partial class TextRecognizer : ITextRecognizer
    {
        // No OCR model on Linux: TextRecognitionService reports the feature as unavailable.
        public static ITextRecognizer GetDefault()
        {
            return null;
        }

        public static ITextRecognizer GetOne(string modelKey)
        {
            return null;
        }

        public Task<RecognizedText> RecognizeAsync(SoftwareBitmap bitmap)
        {
            return Task.FromResult<RecognizedText>(null);
        }
    }
}

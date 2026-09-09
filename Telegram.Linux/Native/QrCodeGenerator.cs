//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Text;

namespace Telegram.Native
{
    // Managed port of Project Nayuki's QR Code generator (MIT licence, https://www.nayuki.io/page/qr-code-generator-library),
    // the same library Telegram.Native/Qr/QrCode.hpp is. Only what QrBuffer.FromString needs: numeric,
    // alphanumeric and byte segments, automatic version and mask selection, ECC boosting.
    internal sealed class QrCodeGenerator
    {
        public enum Ecc
        {
            Low = 0,
            Medium = 1,
            Quartile = 2,
            High = 3
        }

        private static readonly int[] EccFormatBits = { 1, 0, 3, 2 };

        private const int PenaltyN1 = 3;
        private const int PenaltyN2 = 3;
        private const int PenaltyN3 = 40;
        private const int PenaltyN4 = 10;

        private static readonly sbyte[][] EccCodewordsPerBlock =
        {
            // Version: (note that index 0 is for padding, and is set to an illegal value)
            //0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40
            new sbyte[] { -1,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },  // Low
            new sbyte[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },  // Medium
            new sbyte[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },  // Quartile
            new sbyte[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },  // High
        };

        private static readonly sbyte[][] NumErrorCorrectionBlocks =
        {
            // Version: (note that index 0 is for padding, and is set to an illegal value)
            //0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40
            new sbyte[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4,  4,  4,  4,  4,  6,  6,  6,  6,  7,  8,  8,  9,  9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },  // Low
            new sbyte[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5,  5,  8,  9,  9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },  // Medium
            new sbyte[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8,  8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },  // Quartile
            new sbyte[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },  // High
        };

        public int Version { get; }

        public int Size { get; }

        public Ecc ErrorCorrectionLevel { get; }

        public int Mask { get; }

        private readonly bool[,] _modules;
        private bool[,] _isFunction;

        public bool GetModule(int x, int y)
        {
            return 0 <= x && x < Size && 0 <= y && y < Size && _modules[y, x];
        }

        #region Encoding

        public static QrCodeGenerator EncodeText(string text, Ecc ecl, int minVersion, int maxVersion)
        {
            return EncodeSegments(MakeSegments(text), ecl, minVersion, maxVersion, -1, true);
        }

        private static QrCodeGenerator EncodeSegments(List<Segment> segments, Ecc ecl, int minVersion, int maxVersion, int mask, bool boostEcl)
        {
            if (minVersion < 1 || minVersion > maxVersion || maxVersion > 40 || mask < -1 || mask > 7)
            {
                throw new ArgumentException("Invalid value");
            }

            // Find the minimal version number to use
            int version;
            int dataUsedBits;

            for (version = minVersion; ; version++)
            {
                var dataCapacityBits = GetNumDataCodewords(version, ecl) * 8;
                dataUsedBits = GetTotalBits(segments, version);

                if (dataUsedBits != -1 && dataUsedBits <= dataCapacityBits)
                {
                    break;
                }

                if (version >= maxVersion)
                {
                    throw new InvalidOperationException("Data too long");
                }
            }

            // Increase the error correction level while the data still fits in the current version number
            foreach (var newEcl in new[] { Ecc.Low, Ecc.Medium, Ecc.Quartile, Ecc.High })
            {
                if (boostEcl && dataUsedBits <= GetNumDataCodewords(version, newEcl) * 8)
                {
                    ecl = newEcl;
                }
            }

            // Concatenate all segments to create the data bit string
            var bits = new BitBuffer();

            foreach (var segment in segments)
            {
                bits.AppendBits(segment.ModeBits, 4);
                bits.AppendBits(segment.NumChars, segment.CharCountBits(version));
                bits.AppendData(segment.Data);
            }

            // Add terminator and pad up to a byte if applicable
            var capacityBits = GetNumDataCodewords(version, ecl) * 8;
            bits.AppendBits(0, Math.Min(4, capacityBits - bits.Length));
            bits.AppendBits(0, (8 - bits.Length % 8) % 8);

            // Pad with alternating bytes until data capacity is reached
            for (int padByte = 0xEC; bits.Length < capacityBits; padByte ^= 0xEC ^ 0x11)
            {
                bits.AppendBits(padByte, 8);
            }

            // Pack bits into bytes in big endian
            var dataCodewords = new byte[bits.Length / 8];
            for (int i = 0; i < bits.Length; i++)
            {
                dataCodewords[i >> 3] |= (byte)((bits[i] ? 1 : 0) << (7 - (i & 7)));
            }

            return new QrCodeGenerator(version, ecl, dataCodewords, mask);
        }

        private QrCodeGenerator(int version, Ecc ecl, byte[] dataCodewords, int mask)
        {
            Version = version;
            Size = version * 4 + 17;
            ErrorCorrectionLevel = ecl;

            _modules = new bool[Size, Size];
            _isFunction = new bool[Size, Size];

            DrawFunctionPatterns();
            DrawCodewords(AddEccAndInterleave(dataCodewords));

            if (mask == -1)
            {
                var minPenalty = int.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    ApplyMask(i);
                    DrawFormatBits(i);

                    var penalty = GetPenaltyScore();
                    if (penalty < minPenalty)
                    {
                        mask = i;
                        minPenalty = penalty;
                    }

                    // Undoes the mask due to XOR
                    ApplyMask(i);
                }
            }

            Mask = mask;
            ApplyMask(mask);
            DrawFormatBits(mask);

            _isFunction = null;
        }

        #endregion

        #region Function patterns

        private void DrawFunctionPatterns()
        {
            // Draw horizontal and vertical timing patterns
            for (int i = 0; i < Size; i++)
            {
                SetFunctionModule(6, i, i % 2 == 0);
                SetFunctionModule(i, 6, i % 2 == 0);
            }

            // Draw 3 finder patterns (all corners except bottom right; overwrites some timing modules)
            DrawFinderPattern(3, 3);
            DrawFinderPattern(Size - 4, 3);
            DrawFinderPattern(3, Size - 4);

            // Draw numerous alignment patterns
            var alignPatPos = GetAlignmentPatternPositions();
            var numAlign = alignPatPos.Length;

            for (int i = 0; i < numAlign; i++)
            {
                for (int j = 0; j < numAlign; j++)
                {
                    // Don't draw on the three finder corners
                    if (!(i == 0 && j == 0 || i == 0 && j == numAlign - 1 || i == numAlign - 1 && j == 0))
                    {
                        DrawAlignmentPattern(alignPatPos[i], alignPatPos[j]);
                    }
                }
            }

            // Draw configuration data
            DrawFormatBits(0);
            DrawVersion();
        }

        private void DrawFormatBits(int mask)
        {
            // Calculate error correction code and pack bits
            var data = EccFormatBits[(int)ErrorCorrectionLevel] << 3 | mask;
            var rem = data;

            for (int i = 0; i < 10; i++)
            {
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            }

            var bits = (data << 10 | rem) ^ 0x5412;

            // Draw first copy
            for (int i = 0; i <= 5; i++)
            {
                SetFunctionModule(8, i, GetBit(bits, i));
            }

            SetFunctionModule(8, 7, GetBit(bits, 6));
            SetFunctionModule(8, 8, GetBit(bits, 7));
            SetFunctionModule(7, 8, GetBit(bits, 8));

            for (int i = 9; i < 15; i++)
            {
                SetFunctionModule(14 - i, 8, GetBit(bits, i));
            }

            // Draw second copy
            for (int i = 0; i < 8; i++)
            {
                SetFunctionModule(Size - 1 - i, 8, GetBit(bits, i));
            }

            for (int i = 8; i < 15; i++)
            {
                SetFunctionModule(8, Size - 15 + i, GetBit(bits, i));
            }

            // Always dark
            SetFunctionModule(8, Size - 8, true);
        }

        private void DrawVersion()
        {
            if (Version < 7)
            {
                return;
            }

            // Calculate error correction code and pack bits
            var rem = Version;
            for (int i = 0; i < 12; i++)
            {
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            }

            var bits = Version << 12 | rem;

            // Draw two copies
            for (int i = 0; i < 18; i++)
            {
                var bit = GetBit(bits, i);
                var a = Size - 11 + i % 3;
                var b = i / 3;

                SetFunctionModule(a, b, bit);
                SetFunctionModule(b, a, bit);
            }
        }

        private void DrawFinderPattern(int x, int y)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    var dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    var xx = x + dx;
                    var yy = y + dy;

                    if (0 <= xx && xx < Size && 0 <= yy && yy < Size)
                    {
                        SetFunctionModule(xx, yy, dist != 2 && dist != 4);
                    }
                }
            }
        }

        private void DrawAlignmentPattern(int x, int y)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    SetFunctionModule(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
        }

        private void SetFunctionModule(int x, int y, bool isDark)
        {
            _modules[y, x] = isDark;
            _isFunction[y, x] = true;
        }

        private int[] GetAlignmentPatternPositions()
        {
            if (Version == 1)
            {
                return Array.Empty<int>();
            }

            var numAlign = Version / 7 + 2;
            var step = Version == 32 ? 26 : (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;

            var result = new int[numAlign];
            result[0] = 6;

            for (int i = result.Length - 1, pos = Size - 7; i >= 1; i--, pos -= step)
            {
                result[i] = pos;
            }

            return result;
        }

        #endregion

        #region Codewords and masking

        private byte[] AddEccAndInterleave(byte[] data)
        {
            // Calculate parameter numbers
            var numBlocks = NumErrorCorrectionBlocks[(int)ErrorCorrectionLevel][Version];
            var blockEccLen = EccCodewordsPerBlock[(int)ErrorCorrectionLevel][Version];
            var rawCodewords = GetNumRawDataModules(Version) / 8;
            var numShortBlocks = numBlocks - rawCodewords % numBlocks;
            var shortBlockLen = rawCodewords / numBlocks;

            // Split data into blocks and append ECC to each block
            var blocks = new byte[numBlocks][];
            var rsDiv = ReedSolomonComputeDivisor(blockEccLen);

            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                var datLength = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLength];
                Array.Copy(data, k, dat, 0, datLength);
                k += datLength;

                var block = new byte[shortBlockLen + 1];
                Array.Copy(dat, block, datLength);

                var ecc = ReedSolomonComputeRemainder(dat, rsDiv);
                Array.Copy(ecc, 0, block, block.Length - blockEccLen, ecc.Length);

                blocks[i] = block;
            }

            // Interleave (not concatenate) the bytes from every block into a single sequence
            var result = new byte[rawCodewords];
            for (int i = 0, k = 0; i < blocks[0].Length; i++)
            {
                for (int j = 0; j < blocks.Length; j++)
                {
                    // Skip the padding byte in short blocks
                    if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    {
                        result[k] = blocks[j][i];
                        k++;
                    }
                }
            }

            return result;
        }

        private void DrawCodewords(byte[] data)
        {
            var i = 0;

            // Do the funny zigzag scan
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                {
                    right = 5;
                }

                for (int vert = 0; vert < Size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        var x = right - j;
                        var upward = ((right + 1) & 2) == 0;
                        var y = upward ? Size - 1 - vert : vert;

                        if (!_isFunction[y, x] && i < data.Length * 8)
                        {
                            _modules[y, x] = GetBit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
                }
            }
        }

        private void ApplyMask(int mask)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        case 7: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                        default: throw new ArgumentException("Mask value out of range");
                    }

                    _modules[y, x] ^= invert & !_isFunction[y, x];
                }
            }
        }

        private int GetPenaltyScore()
        {
            var result = 0;

            // Adjacent modules in row having same color, and finder-like patterns
            for (int y = 0; y < Size; y++)
            {
                var runColor = false;
                var runX = 0;
                var runHistory = new int[7];

                for (int x = 0; x < Size; x++)
                {
                    if (_modules[y, x] == runColor)
                    {
                        runX++;
                        if (runX == 5)
                        {
                            result += PenaltyN1;
                        }
                        else if (runX > 5)
                        {
                            result++;
                        }
                    }
                    else
                    {
                        FinderPenaltyAddHistory(runX, runHistory);
                        if (!runColor)
                        {
                            result += FinderPenaltyCountPatterns(runHistory) * PenaltyN3;
                        }

                        runColor = _modules[y, x];
                        runX = 1;
                    }
                }

                result += FinderPenaltyTerminateAndCount(runColor, runX, runHistory) * PenaltyN3;
            }

            // Adjacent modules in column having same color, and finder-like patterns
            for (int x = 0; x < Size; x++)
            {
                var runColor = false;
                var runY = 0;
                var runHistory = new int[7];

                for (int y = 0; y < Size; y++)
                {
                    if (_modules[y, x] == runColor)
                    {
                        runY++;
                        if (runY == 5)
                        {
                            result += PenaltyN1;
                        }
                        else if (runY > 5)
                        {
                            result++;
                        }
                    }
                    else
                    {
                        FinderPenaltyAddHistory(runY, runHistory);
                        if (!runColor)
                        {
                            result += FinderPenaltyCountPatterns(runHistory) * PenaltyN3;
                        }

                        runColor = _modules[y, x];
                        runY = 1;
                    }
                }

                result += FinderPenaltyTerminateAndCount(runColor, runY, runHistory) * PenaltyN3;
            }

            // 2*2 blocks of modules having same color
            for (int y = 0; y < Size - 1; y++)
            {
                for (int x = 0; x < Size - 1; x++)
                {
                    var color = _modules[y, x];
                    if (color == _modules[y, x + 1] && color == _modules[y + 1, x] && color == _modules[y + 1, x + 1])
                    {
                        result += PenaltyN2;
                    }
                }
            }

            // Balance of dark and light modules
            var dark = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (_modules[y, x])
                    {
                        dark++;
                    }
                }
            }

            // Compute the smallest integer k >= 0 such that (45-5k)% <= dark/total <= (55+5k)%
            var total = Size * Size;
            var k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            result += k * PenaltyN4;

            return result;
        }

        private int FinderPenaltyCountPatterns(int[] runHistory)
        {
            var n = runHistory[1];
            var core = n > 0 && runHistory[2] == n && runHistory[3] == n * 3 && runHistory[4] == n && runHistory[5] == n;

            return (core && runHistory[0] >= n * 4 && runHistory[6] >= n ? 1 : 0)
                 + (core && runHistory[6] >= n * 4 && runHistory[0] >= n ? 1 : 0);
        }

        private int FinderPenaltyTerminateAndCount(bool currentRunColor, int currentRunLength, int[] runHistory)
        {
            if (currentRunColor)
            {
                // Terminate dark run
                FinderPenaltyAddHistory(currentRunLength, runHistory);
                currentRunLength = 0;
            }

            // Add light border to final run
            currentRunLength += Size;
            FinderPenaltyAddHistory(currentRunLength, runHistory);

            return FinderPenaltyCountPatterns(runHistory);
        }

        private void FinderPenaltyAddHistory(int currentRunLength, int[] runHistory)
        {
            if (runHistory[0] == 0)
            {
                // Add light border to initial run
                currentRunLength += Size;
            }

            Array.Copy(runHistory, 0, runHistory, 1, runHistory.Length - 1);
            runHistory[0] = currentRunLength;
        }

        #endregion

        #region Tables and arithmetic

        private static int GetNumRawDataModules(int version)
        {
            var size = version * 4 + 17;
            var result = size * size;
            result -= 8 * 8 * 3;
            result -= 15 * 2 + 1;
            result -= (size - 16) * 2;

            if (version >= 2)
            {
                var numAlign = version / 7 + 2;
                result -= (numAlign - 1) * (numAlign - 1) * 25;
                result -= (numAlign - 2) * 2 * 20;

                if (version >= 7)
                {
                    result -= 6 * 3 * 2;
                }
            }

            return result;
        }

        private static int GetNumDataCodewords(int version, Ecc ecl)
        {
            return GetNumRawDataModules(version) / 8
                - EccCodewordsPerBlock[(int)ecl][version]
                * NumErrorCorrectionBlocks[(int)ecl][version];
        }

        private static byte[] ReedSolomonComputeDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;

            // Compute the product polynomial (x - r^0) * (x - r^1) * (x - r^2) * ... * (x - r^{degree-1}),
            // and drop the highest monomial term which is always 1x^degree.
            var root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] = (byte)ReedSolomonMultiply(result[j], root);
                    if (j + 1 < result.Length)
                    {
                        result[j] ^= result[j + 1];
                    }
                }

                root = ReedSolomonMultiply(root, 0x02);
            }

            return result;
        }

        private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];

            foreach (var b in data)
            {
                var factor = (b ^ result[0]) & 0xFF;
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;

                for (int i = 0; i < result.Length; i++)
                {
                    result[i] ^= (byte)ReedSolomonMultiply(divisor[i], factor);
                }
            }

            return result;
        }

        private static int ReedSolomonMultiply(int x, int y)
        {
            // Russian peasant multiplication
            var z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }

            return z;
        }

        private static bool GetBit(int x, int i)
        {
            return ((x >> i) & 1) != 0;
        }

        #endregion

        #region Segments

        private const string AlphanumericCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

        private sealed class Segment
        {
            private readonly int[] _numBitsCharCount;

            public Segment(int modeBits, int[] numBitsCharCount, int numChars, BitBuffer data)
            {
                ModeBits = modeBits;
                _numBitsCharCount = numBitsCharCount;
                NumChars = numChars;
                Data = data;
            }

            public int ModeBits { get; }

            public int NumChars { get; }

            public BitBuffer Data { get; }

            public int CharCountBits(int version)
            {
                return _numBitsCharCount[(version + 7) / 17];
            }
        }

        private static readonly int[] NumericCharCountBits = { 10, 12, 14 };
        private static readonly int[] AlphanumericCharCountBits = { 9, 11, 13 };
        private static readonly int[] ByteCharCountBits = { 8, 16, 16 };

        private static List<Segment> MakeSegments(string text)
        {
            var result = new List<Segment>();

            if (text.Length == 0)
            {
                // Leave result empty
            }
            else if (IsNumeric(text))
            {
                result.Add(MakeNumeric(text));
            }
            else if (IsAlphanumeric(text))
            {
                result.Add(MakeAlphanumeric(text));
            }
            else
            {
                result.Add(MakeBytes(Encoding.UTF8.GetBytes(text)));
            }

            return result;
        }

        private static Segment MakeBytes(byte[] data)
        {
            var bits = new BitBuffer();
            foreach (var b in data)
            {
                bits.AppendBits(b, 8);
            }

            return new Segment(0x4, ByteCharCountBits, data.Length, bits);
        }

        private static Segment MakeNumeric(string digits)
        {
            var bits = new BitBuffer();
            for (int i = 0; i < digits.Length;)
            {
                // Consume up to 3 digits per iteration
                var n = Math.Min(digits.Length - i, 3);
                bits.AppendBits(int.Parse(digits.Substring(i, n)), n * 3 + 1);
                i += n;
            }

            return new Segment(0x1, NumericCharCountBits, digits.Length, bits);
        }

        private static Segment MakeAlphanumeric(string text)
        {
            var bits = new BitBuffer();

            int i;
            for (i = 0; i <= text.Length - 2; i += 2)
            {
                // Process groups of 2
                var temp = AlphanumericCharset.IndexOf(text[i]) * 45;
                temp += AlphanumericCharset.IndexOf(text[i + 1]);
                bits.AppendBits(temp, 11);
            }

            if (i < text.Length)
            {
                // 1 character remaining
                bits.AppendBits(AlphanumericCharset.IndexOf(text[i]), 6);
            }

            return new Segment(0x2, AlphanumericCharCountBits, text.Length, bits);
        }

        private static bool IsNumeric(string text)
        {
            foreach (var c in text)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAlphanumeric(string text)
        {
            foreach (var c in text)
            {
                if (AlphanumericCharset.IndexOf(c) == -1)
                {
                    return false;
                }
            }

            return true;
        }

        private static int GetTotalBits(List<Segment> segments, int version)
        {
            long result = 0;

            foreach (var segment in segments)
            {
                var ccbits = segment.CharCountBits(version);
                if (segment.NumChars >= (1 << ccbits))
                {
                    return -1;
                }

                result += 4L + ccbits + segment.Data.Length;
                if (result > int.MaxValue)
                {
                    return -1;
                }
            }

            return (int)result;
        }

        private sealed class BitBuffer
        {
            private readonly List<bool> _bits = new();

            public int Length => _bits.Count;

            public bool this[int index] => _bits[index];

            public void AppendBits(int value, int length)
            {
                if (length < 0 || length > 31 || (value >> length) != 0)
                {
                    throw new ArgumentException("Value out of range");
                }

                for (int i = length - 1; i >= 0; i--)
                {
                    _bits.Add(((value >> i) & 1) != 0);
                }
            }

            public void AppendData(BitBuffer data)
            {
                _bits.AddRange(data._bits);
            }
        }

        #endregion
    }
}

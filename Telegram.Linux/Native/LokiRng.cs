//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

namespace Telegram.Native
{
    // Straight port of Telegram.Native/LokiRng.cpp, so seeded sequences match the other clients.
    public sealed partial class LokiRng
    {
        private const float Scale = 2.3283064365387e-10f;

        private float _seed;

        public LokiRng(uint seed0, uint seed1, uint seed2)
        {
            _seed = Hash(seed0, seed1, seed2);
        }

        public float Next()
        {
            var hashedSeed = unchecked((uint)(_seed * 1099087573U));

            var z1 = TausStep(hashedSeed, 13, 19, 12, 429496729U);
            var z2 = TausStep(hashedSeed, 2, 25, 4, 4294967288U);
            var z3 = TausStep(hashedSeed, 3, 11, 17, 429496280U);
            var z4 = unchecked(1664525U * hashedSeed + 1013904223U);

            var oldSeed = _seed;
            _seed = (z1 ^ z2 ^ z3 ^ z4) * Scale;

            return oldSeed;
        }

        public static float Random(uint withSeed0, uint seed1, uint seed2)
        {
            return Hash(withSeed0, seed1, seed2);
        }

        private static float Hash(uint seed0, uint seed1, uint seed2)
        {
            var seed = unchecked(seed0 * 1099087573U);
            var seedb = unchecked(seed1 * 1099087573U);
            var seedc = unchecked(seed2 * 1099087573U);

            // Round 1: Randomise seed
            var z1 = TausStep(seed, 13, 19, 12, 429496729U);
            var z2 = TausStep(seed, 2, 25, 4, 4294967288U);
            var z3 = TausStep(seed, 3, 11, 17, 429496280U);
            var z4 = unchecked(1664525U * seed + 1013904223U);

            // Round 2: Randomise seed again using second seed
            var r1 = z1 ^ z2 ^ z3 ^ z4 ^ seedb;

            z1 = TausStep(r1, 13, 19, 12, 429496729U);
            z2 = TausStep(r1, 2, 25, 4, 4294967288U);
            z3 = TausStep(r1, 3, 11, 17, 429496280U);
            z4 = unchecked(1664525U * r1 + 1013904223U);

            // Round 3: Randomise seed again using third seed
            r1 = z1 ^ z2 ^ z3 ^ z4 ^ seedc;

            z1 = TausStep(r1, 13, 19, 12, 429496729U);
            z2 = TausStep(r1, 2, 25, 4, 4294967288U);
            z3 = TausStep(r1, 3, 11, 17, 429496280U);
            z4 = unchecked(1664525U * r1 + 1013904223U);

            return (z1 ^ z2 ^ z3 ^ z4) * Scale;
        }

        private static uint TausStep(uint z, int s1, int s2, int s3, uint m)
        {
            var b = ((z << s1) ^ z) >> s2;
            return ((z & m) << s3) ^ b;
        }
    }
}

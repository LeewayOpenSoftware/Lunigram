//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

// Linux counterpart of Telegram/Common/Utils.cs, which hashes through
// Windows.Security.Cryptography.Core (HashAlgorithmProvider) that Uno does not implement.
// Same members and the same digests (PasscodeService stores salt + SHA1 in the settings),
// computed with System.Security.Cryptography instead.

using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Telegram.Common
{
    public static class Utils
    {
        /// <summary>
        /// Releases a XamlDirect handle. Upstream disposes the WinRT projection behind it
        /// (Telegram/Common/Utils.cs, guarded by NET9_0_OR_GREATER); there are no projections on
        /// this head -- TextElementDirect hands out the managed elements themselves -- so there is
        /// nothing to release and this is deliberately a no-op.
        /// </summary>
        internal static void ReleaseHandle(object handle)
        {
        }

        public static byte[] ComputeSHA1(byte[] data)
        {
            return SHA1.HashData(data);
        }

        [MethodImpl(MethodImplOptions.NoOptimization)]
        public static byte[] ComputeHash(byte[] salt, byte[] passcode)
        {
            var array = Combine(salt, passcode, salt);
            for (int i = 0; i < 1000; i++)
            {
                var data = Combine(BitConverter.GetBytes(i), array);
                ComputeSHA1(data);
            }
            return ComputeSHA1(array);
        }

        public static byte[] Combine(params byte[][] arrays)
        {
            var length = 0;
            for (int i = 0; i < arrays.Length; i++)
            {
                length += arrays[i].Length;
            }

            var result = new byte[length];
            var offset = 0;
            foreach (var array in arrays)
            {
                Buffer.BlockCopy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }
            return result;
        }

        public static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
    }
}

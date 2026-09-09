//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Telegram.Native
{
    /// <summary>
    /// Identity for an object that a native library calls back into, as an opaque
    /// <c>void*</c> cookie.
    ///
    /// <para>This exists because <see cref="System.Runtime.InteropServices.GCHandle"/> CANNOT be
    /// used to decide whether a cookie is still alive, and every callback on this boundary needs
    /// exactly that decision. <c>GCHandle.IsAllocated</c> is <c>_handle != 0</c> on the copy that
    /// <c>FromIntPtr</c> just built, so it answers "yes" for a handle freed ten minutes ago; and
    /// the runtime recycles handle slots, so a cookie that has been freed can later resolve to a
    /// DIFFERENT, perfectly live object -- of the same type, which is the one case a
    /// <c>Target as T</c> cast cannot catch either. A native thread that outlives its owner (the
    /// video engine's compression worker is the measured case) would then drive somebody else's
    /// source: a wrong download offset, frames of another video written into a cache file, and no
    /// crash to point at it.</para>
    ///
    /// <para>A cookie handed out here is a counter value that is NEVER reissued, so a stale one
    /// deterministically resolves to null instead of to a recycled slot. That makes the managed
    /// side safe on its own, without depending on the native side calling <c>destroy</c> at
    /// exactly the right moment -- which is the invariant this port has already broken once.</para>
    ///
    /// <para>The entry is the strong reference that keeps the object alive while the library holds
    /// the cookie, the job the <see cref="System.Runtime.InteropServices.GCHandle"/> used to do:
    /// <see cref="Remove"/> is the exact counterpart of freeing it, with the same "exactly once,
    /// at the point the library promises never to call again" discipline.</para>
    ///
    /// <para>One table per <typeparamref name="T"/>: a static generic field is per closed type, so
    /// video sources and mpv streams never share a keyspace even though the keys are just
    /// counters.</para>
    /// </summary>
    internal static class NativeCookieTable<T> where T : class
    {
        private static readonly ConcurrentDictionary<IntPtr, T> _entries = new();

        // Starts at 0 and is pre-incremented, so the first cookie is 1 and IntPtr.Zero is never a
        // valid key: the callbacks get "no user data" and "stale user data" as the same answer.
        private static long _next;

        public static IntPtr Add(T value)
        {
            // 64-bit only (this port is x86-64): the counter cannot reach the point where the cast
            // to a pointer-sized integer would lose a bit before the process is long gone.
            var cookie = (IntPtr)Interlocked.Increment(ref _next);
            _entries[cookie] = value;

            return cookie;
        }

        public static T Resolve(IntPtr cookie)
        {
            return cookie != IntPtr.Zero && _entries.TryGetValue(cookie, out var value)
                ? value
                : null;
        }

        /// <summary>
        /// Retires <paramref name="cookie"/>. Idempotent, and from that moment every callback that
        /// still carries it resolves to null.
        /// </summary>
        public static void Remove(IntPtr cookie)
        {
            if (cookie != IntPtr.Zero)
            {
                _entries.TryRemove(cookie, out _);
            }
        }
    }
}

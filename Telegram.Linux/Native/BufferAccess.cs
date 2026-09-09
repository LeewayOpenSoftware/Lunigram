//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;

namespace Telegram.Native
{
    /// <summary>
    /// Reaches the bytes behind Uno's <c>Windows.Storage.Streams.Buffer</c> without copying them.
    ///
    /// <para>Every public way in and out of an <see cref="IBuffer"/> on Uno copies: the
    /// <c>CopyTo</c>/<c>ToArray</c> extension methods all go through <c>Buffer.Cast(...)</c> and
    /// then a <c>Span</c> copy. That is fine for a thumbnail and ruinous for a video frame, where
    /// the same 16 MB was being walked three times before it reached the screen (decoder scratch
    /// -> staging IBuffer -> <c>WriteableBitmap.PixelBuffer</c> -> the SKImage Uno builds inside
    /// <c>Invalidate</c>). The bytes themselves are a plain <c>byte[]</c>; only the accessors are
    /// <c>internal</c>.</para>
    ///
    /// <para>So: one reflected call per <b>buffer</b>, not per frame, and every failure falls back
    /// to the copying path the callers already had. Measured against Uno 6.6.184, where
    /// <c>Buffer</c> holds a <c>byte[] _buffer</c> and a <c>Memory&lt;byte&gt; _data</c> over it and
    /// exposes <c>internal ArraySegment&lt;byte&gt; GetSegment()</c>. <c>GetSegment</c> is preferred
    /// because it is also correct for the <c>Buffer(Memory&lt;byte&gt;)</c> constructor, which
    /// leaves <c>_buffer</c> null.</para>
    ///
    /// <para>Nothing here names the Uno type: the members are resolved from the type of the buffer
    /// that actually shows up, which is what lets the console spikes exercise both the fast path
    /// and the fallback with their own <c>IBuffer</c>.</para>
    /// </summary>
    public static class BufferAccess
    {
        private static readonly object _lock = new();

        private static Type _type;
        private static MethodInfo _getSegment;
        private static FieldInfo _data;
        private static bool _available;

        /// <summary>
        /// True when the bytes of an Uno buffer can be reached without copying. Only a diagnostic:
        /// the callers ask <see cref="TryGetArray"/>, which answers for the buffer in hand.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                var type = Type.GetType("Windows.Storage.Streams.Buffer, Uno", false);
                return type != null && Resolve(type);
            }
        }

        private static bool Resolve(Type type)
        {
            lock (_lock)
            {
                if (type == _type)
                {
                    return _available;
                }

                _type = type;
                _getSegment = null;
                _data = null;
                _available = false;

                try
                {
                    _getSegment = type.GetMethod("GetSegment", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);

                    if (_getSegment != null && _getSegment.ReturnType != typeof(ArraySegment<byte>))
                    {
                        _getSegment = null;
                    }

                    _data = type.GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic);

                    if (_data != null && _data.FieldType != typeof(Memory<byte>))
                    {
                        _data = null;
                    }

                    _available = _getSegment != null || _data != null;
                }
                catch (Exception)
                {
                    _available = false;
                }

                return _available;
            }
        }

        /// <summary>
        /// The array that backs <paramref name="buffer"/>, without copying anything. False means
        /// "use the copying path"; it is never an error.
        /// </summary>
        public static bool TryGetArray(IBuffer buffer, out byte[] array, out int offset, out int count)
        {
            array = null;
            offset = 0;
            count = 0;

            var inner = PixelBuffer.Unwrap(buffer);
            if (inner == null || !Resolve(inner.GetType()))
            {
                return false;
            }

            try
            {
                if (_getSegment != null)
                {
                    var segment = (ArraySegment<byte>)_getSegment.Invoke(inner, null);
                    if (segment.Array == null)
                    {
                        return false;
                    }

                    array = segment.Array;
                    offset = segment.Offset;
                    count = segment.Count;
                    return true;
                }

                if (_data != null)
                {
                    var memory = (Memory<byte>)_data.GetValue(inner);
                    if (MemoryMarshal.TryGetArray<byte>(memory, out var fromMemory) && fromMemory.Array != null)
                    {
                        array = fromMemory.Array;
                        offset = fromMemory.Offset;
                        count = fromMemory.Count;
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // A shape change in Uno is a reason to copy, not to crash.
                lock (_lock)
                {
                    _available = false;
                }
            }

            return false;
        }
    }
}

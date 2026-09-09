//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace Telegram.Native.Media
{
    public interface IAsyncMediaPlayerSource : IVideoAnimationSource
    {
        void Open();
        void Close();
    }

    [Flags]
    public enum AsyncMediaPlayerMode : uint
    {
        None = 0,
        Audio = 1,
        Video = 2
    }

    public enum AsyncMediaPlayerState
    {
        NothingSpecial = 0,
        Opening = 1,
        Buffering = 2,
        Playing = 3,
        Paused = 4,
        Stopped = 5,
        Ended = 6,
        Error = 7
    }

    public enum AsyncMediaPlayerStreamType
    {
        Unknown = -1,
        Audio = 0,
        Video = 1,
        Text = 2
    }

    public enum AsyncMediaPlayerLogLevel
    {
        Debug = 0,
        Notice = 2,
        Warning = 3,
        Error = 4
    }

    public sealed partial class AsyncMediaPlayerOptions
    {
        public AsyncMediaPlayerMode Mode { get; set; }

        public bool Debug { get; set; }

        public bool CreateSwapChain { get; set; }

        public bool Mute { get; set; }

        public double Volume { get; set; } = 1;

        public double Rate { get; set; } = 1;

        public IList<string> Arguments { get; } = new List<string>();
    }

    public sealed partial class AsyncMediaPlayerSwapChain : IDisposable
    {
        public AsyncMediaPlayerSwapChain()
        {
        }

        public AsyncMediaPlayerSwapChain(bool create)
        {
        }

        public bool IsLoaded => false;

        public void Clear()
        {
        }

        public IList<string> SwapChainOptions { get; } = new List<string>();

        public bool Create()
        {
            return false;
        }

        public bool Create(bool subscribe)
        {
            return false;
        }

        public void Attach(SwapChainPanel panel)
        {
        }

        public void Attach(SwapChainPanel panel, bool subscribe)
        {
        }

        public void Detach()
        {
        }

        public void Detach(SwapChainPanel panel)
        {
        }

        public void UpdateSize()
        {
        }

        public void UpdateScale()
        {
        }

        public void Dispose()
        {
        }
    }

    public sealed partial class AsyncMediaPlayerStateChangedEventArgs
    {
        public AsyncMediaPlayerStateChangedEventArgs(AsyncMediaPlayerState state)
        {
            State = state;
        }

        public AsyncMediaPlayerState State { get; set; }
    }

    public sealed partial class AsyncMediaPlayerBufferingEventArgs
    {
        public AsyncMediaPlayerBufferingEventArgs(float cache)
        {
            Cache = cache;
        }

        public float Cache { get; set; }
    }

    public sealed partial class AsyncMediaPlayerPositionChangedEventArgs
    {
        public AsyncMediaPlayerPositionChangedEventArgs(double position)
        {
            Position = position;
        }

        public double Position { get; set; }
    }

    public sealed partial class AsyncMediaPlayerDurationChangedEventArgs
    {
        public AsyncMediaPlayerDurationChangedEventArgs(double duration)
        {
            Duration = duration;
        }

        public double Duration { get; set; }
    }

    public sealed partial class AsyncMediaPlayerStreamSelectedEventArgs
    {
        public AsyncMediaPlayerStreamSelectedEventArgs(int id, AsyncMediaPlayerStreamType type, int width, int height)
        {
            Id = id;
            Type = type;
            Width = width;
            Height = height;
        }

        public int Id { get; }

        public AsyncMediaPlayerStreamType Type { get; }

        public int Width { get; }

        public int Height { get; }
    }

    public sealed partial class AsyncMediaPlayerLogEventArgs
    {
        public AsyncMediaPlayerLogEventArgs(AsyncMediaPlayerLogLevel level, string message, string modulo, string sourceFile, uint sourceLine)
        {
            Level = level;
            Message = message;
            Module = modulo;
            SourceFile = sourceFile;
            SourceLine = sourceLine;
        }

        public AsyncMediaPlayerLogLevel Level { get; }

        public string Message { get; }

        public string Module { get; }

        public string SourceFile { get; }

        public uint SourceLine { get; }
    }
}

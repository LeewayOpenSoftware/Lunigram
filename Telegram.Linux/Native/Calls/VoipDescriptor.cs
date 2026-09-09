//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;

namespace Telegram.Native.Calls
{
    public interface VoipCallServerType
    {
    }

    public sealed partial class VoipCallServerTypeTelegramReflector : VoipCallServerType
    {
        public VoipCallServerTypeTelegramReflector(string peerTag, bool isTcp)
        {
            PeerTag = peerTag;
            IsTcp = isTcp;
        }

        public string PeerTag { get; }

        public bool IsTcp { get; }
    }

    public sealed partial class VoipCallServerTypeWebrtc : VoipCallServerType
    {
        public VoipCallServerTypeWebrtc(string username, string password, bool supportsTurn, bool supportsStun)
        {
            Username = username;
            Password = password;
            SupportsTurn = supportsTurn;
            SupportsStun = supportsStun;
        }

        public string Username { get; }

        public string Password { get; }

        public bool SupportsTurn { get; }

        public bool SupportsStun { get; }
    }

    public sealed partial class VoipCallServer
    {
        public VoipCallServer(long id, string ipAddress, string ipv6Address, int port, VoipCallServerType type)
        {
            Id = id;
            IpAddress = ipAddress;
            Ipv6Address = ipv6Address;
            Port = port;
            Type = type;
        }

        public long Id { get; }

        public string IpAddress { get; }

        public string Ipv6Address { get; }

        public int Port { get; }

        public VoipCallServerType Type { get; }
    }

    public sealed partial class VoipCallProtocol
    {
        public VoipCallProtocol(bool udpP2p, bool udpReflector, int minLayer, int maxLayer, IList<string> libraryVersions)
        {
            UdpP2p = udpP2p;
            UdpReflector = udpReflector;
            MinLayer = minLayer;
            MaxLayer = maxLayer;
            LibraryVersions = libraryVersions;
        }

        public bool UdpP2p { get; }

        public bool UdpReflector { get; }

        public int MinLayer { get; }

        public int MaxLayer { get; }

        public IList<string> LibraryVersions { get; }
    }

    public sealed partial class VoipDescriptor
    {
        public string Version { get; set; }

        public string CustomParameters { get; set; }

        public double InitializationTimeout { get; set; }

        public double ReceiveTimeout { get; set; }

        public IList<byte> PersistentState { get; set; }

        public IList<VoipCallServer> Servers { get; set; }

        public IList<byte> EncryptionKey { get; set; }

        public bool IsOutgoing { get; set; }

        public bool EnableP2p { get; set; }

        public string AudioInputId { get; set; }

        public string AudioOutputId { get; set; }

        public VoipCaptureBase VideoCapture { get; set; }
    }

    public sealed partial class VoipGroupDescriptor
    {
        public string AudioInputId { get; set; }

        public string AudioOutputId { get; set; }

        public VoipVideoContentType VideoContentType { get; set; } = VoipVideoContentType.Generic;

        public VoipCaptureBase VideoCapture { get; set; }

        public bool IsConference { get; set; }

        public bool IsNoiseSuppressionEnabled { get; set; }

        public long AudioProcessId { get; set; }
    }
}

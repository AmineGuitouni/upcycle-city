using System;
using Unity.Collections;
using Unity.Netcode;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Connected player metadata replicated in the pre-game lobby and used to initialize
    /// spawned <see cref="NetworkPlayerController"/> instances when gameplay begins.
    /// </summary>
    public struct CoopLobbyPlayer : INetworkSerializable, IEquatable<CoopLobbyPlayer>
    {
        public ulong clientId;
        public byte slotNumber;
        public FixedString32Bytes displayName;
        public byte isHost;
        public NetworkCharacterAppearance appearance;

        public bool IsHost => isHost != 0;

        public string FormattedTag => $"P{(slotNumber > 0 ? slotNumber : 1)}";

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref clientId);
            serializer.SerializeValue(ref slotNumber);
            serializer.SerializeValue(ref displayName);
            serializer.SerializeValue(ref isHost);
            serializer.SerializeValue(ref appearance);
        }

        public bool Equals(CoopLobbyPlayer o) =>
            clientId == o.clientId
            && slotNumber == o.slotNumber
            && displayName.Equals(o.displayName)
            && isHost == o.isHost
            && appearance.Equals(o.appearance);

        public override bool Equals(object obj) => obj is CoopLobbyPlayer o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(clientId, slotNumber, displayName.GetHashCode(), isHost);
    }
}

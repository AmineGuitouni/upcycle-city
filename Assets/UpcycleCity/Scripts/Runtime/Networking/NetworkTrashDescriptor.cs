using System;
using Unity.Collections;
using Unity.Netcode;
using UpcycleCity.Waste;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Replicated archetype descriptor for a networked <see cref="TrashItem"/> so both host and clients
    /// reconstruct the exact matching visual prefab, collider bounds, waste stream, and gameplay values
    /// from the shared <see cref="Ecosystem.WasteCatalog"/>.
    /// </summary>
    public struct NetworkTrashDescriptor : INetworkSerializable, IEquatable<NetworkTrashDescriptor>
    {
        public short catalogIndex;
        public byte stream;
        public byte sourceKind;
        public FixedString64Bytes displayName;
        public float pollutionWeight;
        public int baseCoinValue;
        public float mass;
        public byte isBulky;
        public float carrySpeed;
        public float holdOffset;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref catalogIndex);
            serializer.SerializeValue(ref stream);
            serializer.SerializeValue(ref sourceKind);
            serializer.SerializeValue(ref displayName);
            serializer.SerializeValue(ref pollutionWeight);
            serializer.SerializeValue(ref baseCoinValue);
            serializer.SerializeValue(ref mass);
            serializer.SerializeValue(ref isBulky);
            serializer.SerializeValue(ref carrySpeed);
            serializer.SerializeValue(ref holdOffset);
        }

        public bool Equals(NetworkTrashDescriptor o) =>
            catalogIndex == o.catalogIndex
            && stream == o.stream
            && sourceKind == o.sourceKind
            && displayName.Equals(o.displayName)
            && Math.Abs(pollutionWeight - o.pollutionWeight) < 0.0001f
            && baseCoinValue == o.baseCoinValue
            && Math.Abs(mass - o.mass) < 0.0001f
            && isBulky == o.isBulky
            && Math.Abs(carrySpeed - o.carrySpeed) < 0.0001f
            && Math.Abs(holdOffset - o.holdOffset) < 0.0001f;

        public override bool Equals(object obj) => obj is NetworkTrashDescriptor o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine(catalogIndex, stream, sourceKind, displayName.GetHashCode(), baseCoinValue, isBulky);

        public static NetworkTrashDescriptor CreateFallback(
            string name,
            WasteStream wasteStream,
            WasteSourceKind kind,
            float weight = 1f,
            int coinValue = 6,
            float itemMass = 0.5f,
            bool bulky = false,
            float carrySpeedMult = 1f,
            float holdDistanceOffset = 0f,
            short entryIndex = -1)
        {
            return new NetworkTrashDescriptor
            {
                catalogIndex = entryIndex,
                stream = (byte)wasteStream,
                sourceKind = (byte)kind,
                displayName = new FixedString64Bytes(string.IsNullOrWhiteSpace(name) ? "Litter" : name),
                pollutionWeight = weight > 0.05f ? weight : 1f,
                baseCoinValue = coinValue > 0 ? coinValue : 6,
                mass = itemMass > 0.1f ? itemMass : 0.5f,
                isBulky = bulky ? (byte)1 : (byte)0,
                carrySpeed = carrySpeedMult > 0.1f ? carrySpeedMult : 1f,
                holdOffset = holdDistanceOffset,
            };
        }
    }
}

using System;
using Unity.Netcode;
using UpcycleCity.Character;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Compact 8-byte <see cref="INetworkSerializable"/> mirror of <see cref="CharacterAppearance"/>
    /// used to replicate each player's Character Creator look across the network.
    /// </summary>
    public struct NetworkCharacterAppearance : INetworkSerializable, IEquatable<NetworkCharacterAppearance>
    {
        public byte sex;
        public byte skinTone;
        public byte outfitColor;
        public byte eyeStyle;
        public byte eyeColor;
        public byte hairStyle;
        public byte hairColor;
        public byte facialHair;

        public static NetworkCharacterAppearance From(CharacterAppearance a) => new NetworkCharacterAppearance
        {
            sex = ClampByte((int)a.sex),
            skinTone = ClampByte(a.skinTone),
            outfitColor = ClampByte(a.outfitColor),
            eyeStyle = ClampByte(a.eyeStyle),
            eyeColor = ClampByte(a.eyeColor),
            hairStyle = ClampByte(a.hairStyle),
            hairColor = ClampByte(a.hairColor),
            facialHair = ClampByte(a.facialHair),
        };

        public CharacterAppearance ToAppearance() => new CharacterAppearance
        {
            sex = sex == 0 ? CharacterSex.Male : CharacterSex.Female,
            skinTone = skinTone,
            outfitColor = outfitColor,
            eyeStyle = eyeStyle,
            eyeColor = eyeColor,
            hairStyle = hairStyle,
            hairColor = hairColor,
            facialHair = facialHair,
        };

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref sex);
            serializer.SerializeValue(ref skinTone);
            serializer.SerializeValue(ref outfitColor);
            serializer.SerializeValue(ref eyeStyle);
            serializer.SerializeValue(ref eyeColor);
            serializer.SerializeValue(ref hairStyle);
            serializer.SerializeValue(ref hairColor);
            serializer.SerializeValue(ref facialHair);
        }

        public bool Equals(NetworkCharacterAppearance o) =>
            sex == o.sex && skinTone == o.skinTone && outfitColor == o.outfitColor && eyeStyle == o.eyeStyle
            && eyeColor == o.eyeColor && hairStyle == o.hairStyle && hairColor == o.hairColor && facialHair == o.facialHair;

        public override bool Equals(object obj) => obj is NetworkCharacterAppearance o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine(sex, skinTone, outfitColor, eyeStyle, eyeColor, hairStyle, hairColor, facialHair);

        static byte ClampByte(int value)
        {
            if (value <= 0) return 0;
            if (value >= byte.MaxValue) return byte.MaxValue;
            return (byte)value;
        }
    }
}

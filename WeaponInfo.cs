namespace WeaponPaints
{
	public class WeaponInfo
	{
		public int Paint { get; set; }
		public int Seed { get; set; }
		public float Wear { get; set; }
		public string Nametag { get; set; } = "";
		public bool StatTrak { get; set; }
		public int StatTrakCount { get; set; }
		public KeyChainInfo? KeyChain { get; set; }
		public List<StickerInfo> Stickers { get; set; } = new();
		internal int StorageTeam { get; set; }

		public WeaponInfo Clone() => new()
		{
			Paint = Paint, Seed = Seed, Wear = Wear, Nametag = Nametag,
			StatTrak = StatTrak, StatTrakCount = StatTrakCount,
			StorageTeam = StorageTeam,
			KeyChain = KeyChain is null ? null : new KeyChainInfo
			{
				Id = KeyChain.Id, OffsetX = KeyChain.OffsetX, OffsetY = KeyChain.OffsetY,
				OffsetZ = KeyChain.OffsetZ, Seed = KeyChain.Seed
			},
			Stickers = Stickers.Select(sticker => new StickerInfo
			{
				Id = sticker.Id, Schema = sticker.Schema, OffsetX = sticker.OffsetX,
				OffsetY = sticker.OffsetY, Wear = sticker.Wear, Scale = sticker.Scale,
				Rotation = sticker.Rotation
			}).ToList()
		};

		internal ulong GetVisualSignature(ulong steamId64, int definitionIndex)
		{
			ulong hash = 14695981039346656037UL;
			AddHashValue(ref hash, steamId64);
			AddHashValue(ref hash, unchecked((uint)definitionIndex));
			AddHashValue(ref hash, unchecked((uint)Paint));
			AddHashValue(ref hash, unchecked((uint)Seed));
			AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(Wear));
			AddHashValue(ref hash, StatTrak ? 1U : 0U);
			AddHashValue(ref hash, unchecked((uint)StatTrakCount));
			AddHashValue(ref hash, Nametag);

			if (KeyChain is { } keyChain)
			{
				AddHashValue(ref hash, 1U);
				AddHashValue(ref hash, keyChain.Id);
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(keyChain.OffsetX));
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(keyChain.OffsetY));
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(keyChain.OffsetZ));
				AddHashValue(ref hash, keyChain.Seed);
			}
			else
			{
				AddHashValue(ref hash, 0U);
			}

			AddHashValue(ref hash, unchecked((uint)Stickers.Count));
			foreach (StickerInfo sticker in Stickers)
			{
				AddHashValue(ref hash, sticker.Id);
				AddHashValue(ref hash, sticker.Schema);
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(sticker.OffsetX));
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(sticker.OffsetY));
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(sticker.Wear));
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(sticker.Scale));
				AddHashValue(ref hash, BitConverter.SingleToUInt32Bits(sticker.Rotation));
			}

			return hash;
		}

		internal static ulong GetDefaultVisualSignature(ulong steamId64, int definitionIndex, bool isKnife)
		{
			ulong hash = 14695981039346656037UL;
			AddHashValue(ref hash, steamId64);
			AddHashValue(ref hash, unchecked((uint)definitionIndex));
			AddHashValue(ref hash, isKnife ? 0x4B4E4946U : 0x44454641U);
			return hash;
		}

		private static void AddHashValue(ref ulong hash, ulong value)
		{
			for (int byteIndex = 0; byteIndex < sizeof(ulong); byteIndex++)
			{
				hash ^= (byte)value;
				hash *= 1099511628211UL;
				value >>= 8;
			}
		}

		private static void AddHashValue(ref ulong hash, uint value) => AddHashValue(ref hash, (ulong)value);

		private static void AddHashValue(ref ulong hash, string value)
		{
			AddHashValue(ref hash, unchecked((uint)value.Length));
			foreach (char character in value) AddHashValue(ref hash, character);
		}
	}

	public class StickerInfo
	{
		public uint Id { get; set; }
		public uint Schema { get; set; }
		public float OffsetX { get; set; }
		public float OffsetY { get; set; }
		public float Wear { get; set; }
		public float Scale { get; set; }
		public float Rotation { get; set; }
	}

	public class KeyChainInfo
	{
		public uint Id { get; set; }
		public float OffsetX { get; set; }
		public float OffsetY { get; set; }
		public float OffsetZ { get; set; }
		public uint Seed { get; set; }
	}
}

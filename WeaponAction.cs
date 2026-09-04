using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

public partial class WeaponPaints
{
	private void GivePlayerWeaponSkin(
		CCSPlayerController player,
		CBasePlayerWeapon weapon,
		bool allowUnowned = false,
		bool clearIfUnselected = false)
	{
		if (!Utility.IsPlayerFullyConnected(player)
		    || !weapon.IsValid || !PlayerPaints.TryGet(player, out var state)) return;
		bool isKnife = IsKnife(weapon);
		if (!isKnife && !WeaponList.ContainsKey(weapon.DesignerName)) return;
		if ((isKnife && !Config.Additional.KnifeEnabled)
		    || (!isKnife && !Config.Additional.SkinEnabled)) return;
		if (!allowUnowned && !IsWeaponOwnedBy(player, weapon)) return;

		int definitionIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
		if (isKnife)
		{
			string knifeName = state.Knives.GetValueOrDefault(player.Team, "weapon_knife");
			int selectedDefinition = string.Equals(knifeName, "weapon_knife", StringComparison.Ordinal)
				? player.Team == CsTeam.Terrorist ? 59 : 42
				: WeaponClassDefindex.GetValueOrDefault(knifeName);
			if (selectedDefinition == 0)
				selectedDefinition = player.Team == CsTeam.Terrorist ? 59 : 42;
			if (selectedDefinition != 0)
			{
				definitionIndex = selectedDefinition;
				if (weapon.AttributeManager.Item.ItemDefinitionIndex != definitionIndex)
				{
					SubclassChange(weapon, (ushort)definitionIndex);
					if (!weapon.IsValid) return;
				}
				weapon.AttributeManager.Item.ItemDefinitionIndex = (ushort)definitionIndex;
			}
		}

		bool hasSelection = state.TryGetWeapon(player.Team, definitionIndex, out var weaponInfo)
		                    && weaponInfo != null && weaponInfo.Paint > 0;
		if (!hasSelection && Config.Additional.SkinEnabled && _config.Additional.GiveRandomSkin)
		{
			int randomPaint = GetRandomPaint(definitionIndex);
			if (randomPaint > 0)
			{
				weaponInfo = new WeaponInfo { Paint = randomPaint, Seed = 0, Wear = 0.01f };
				hasSelection = true;
			}
		}

		if (!hasSelection)
		{
			if (clearIfUnselected) ClearWeaponCosmetics(player, weapon, isKnife);
			else if (isKnife)
			{
				var item = weapon.AttributeManager.Item;
				item.EntityQuality = 3;
				item.AccountID = (uint)player.SteamID;
				UpdatePlayerEconItemId(item);
				_appliedWeaponSelections[weapon.Index] =
					WeaponInfo.GetDefaultVisualSignature(player.SteamID, definitionIndex, isKnife: true);
			}
			return;
		}

		ulong fingerprint = weaponInfo!.GetVisualSignature(player.SteamID, definitionIndex);
		if (_appliedWeaponSelections.TryGetValue(weapon.Index, out ulong existing)
		    && existing == fingerprint) return;

		try
		{
			WeaponInfo selectedInfo = weaponInfo!;
			var item = weapon.AttributeManager.Item;
			item.AttributeList.Attributes.RemoveAll();
			item.NetworkedDynamicAttributes.Attributes.RemoveAll();
			item.EntityQuality = isKnife ? 3 : selectedInfo.StatTrak ? 9 : 0;
			item.AccountID = (uint)player.SteamID;
			item.CustomName = selectedInfo.Nametag;
			UpdatePlayerEconItemId(item);

			weapon.FallbackPaintKit = selectedInfo.Paint;
			weapon.FallbackSeed = selectedInfo is { Paint: 38, Seed: 0 } ? _fadeSeed++ : selectedInfo.Seed;
			weapon.FallbackWear = NormalizeWear(selectedInfo.Wear);
			ApplyTextureAttributes(weapon);
			if (selectedInfo.StatTrak) SetStatTrakAttributes(weapon, selectedInfo.StatTrakCount);
			SetKeychain(weapon, selectedInfo.KeyChain);
			SetStickers(weapon, selectedInfo.Stickers);
			UpdateWeaponMeshGroupMask(weapon, IsLegacyModel(definitionIndex, selectedInfo.Paint));
			_appliedWeaponSelections[weapon.Index] = fingerprint;
		}
		catch (Exception exception)
		{
			if (_applyFailureLogs.ShouldLog(out int suppressed))
				Logger.LogWarning(exception,
					"[WeaponPaints] Failed to apply definition {DefinitionIndex} for SteamID64 {SteamId}. Suppressed {SuppressedCount} similar failures.",
					definitionIndex, player.SteamID, suppressed);
		}
	}

	private static bool IsWeaponOwnedBy(CCSPlayerController player, CBasePlayerWeapon weapon)
	{
		if (!Utility.IsPlayerFullyConnected(player) || !weapon.IsValid) return false;
		var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
		if (weapons == null) return false;
		foreach (var handle in weapons)
			if (handle.Value is { IsValid: true } owned && owned.Index == weapon.Index) return true;
		return false;
	}

	private void ClearWeaponCosmetics(CCSPlayerController player, CBasePlayerWeapon weapon, bool isKnife)
	{
		if (!weapon.IsValid || !IsWeaponOwnedBy(player, weapon)) return;
		var item = weapon.AttributeManager.Item;
		item.AttributeList.Attributes.RemoveAll();
		item.NetworkedDynamicAttributes.Attributes.RemoveAll();
		item.CustomName = "";
		item.EntityQuality = isKnife ? 3 : 0;
		item.AccountID = (uint)player.SteamID;
		weapon.FallbackPaintKit = 0;
		weapon.FallbackSeed = 0;
		weapon.FallbackWear = 0.000001f;
		UpdatePlayerEconItemId(item);
		UpdateWeaponMeshGroupMask(weapon, true);
		_appliedWeaponSelections[weapon.Index] =
			WeaponInfo.GetDefaultVisualSignature(player.SteamID,
				weapon.AttributeManager.Item.ItemDefinitionIndex, isKnife);
	}

	private static void ApplyTextureAttributes(CBasePlayerWeapon weapon)
	{
		ApplyTextureAttributes(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, weapon);
		ApplyTextureAttributes(weapon.AttributeManager.Item.AttributeList.Handle, weapon);
	}

	private static void ApplyTextureAttributes(nint attributes, CBasePlayerWeapon weapon)
	{
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "set item texture prefab", weapon.FallbackPaintKit);
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "set item texture seed", weapon.FallbackSeed);
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "set item texture wear", weapon.FallbackWear);
	}

	private static void SetStickers(CBasePlayerWeapon weapon, IReadOnlyList<StickerInfo> stickers)
	{
		for (int slot = 0; slot < stickers.Count && slot < 5; slot++)
		{
			StickerInfo sticker = stickers[slot];
			nint attributes = weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle;
			string[] names = StickerAttributeNames[slot];
			CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, names[0], ViewAsFloat(sticker.Id));
			CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, names[1], ViewAsFloat(sticker.Schema));
			CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, names[2], sticker.OffsetX);
			CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, names[3], sticker.OffsetY);
			CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, names[4], sticker.Wear);
			CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, names[5], sticker.Scale);
			CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, names[6], sticker.Rotation);
		}
	}

	private static void SetKeychain(CBasePlayerWeapon weapon, KeyChainInfo? keyChain)
	{
		if (keyChain is not { Id: > 0 }) return;
		nint attributes = weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle;
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "keychain slot 0 id", ViewAsFloat(keyChain.Id));
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "keychain slot 0 offset x", keyChain.OffsetX);
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "keychain slot 0 offset y", keyChain.OffsetY);
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "keychain slot 0 offset z", keyChain.OffsetZ);
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "keychain slot 0 seed", ViewAsFloat(keyChain.Seed));
	}

	private static void SetStatTrakAttributes(CBasePlayerWeapon weapon, int count)
	{
		if (!weapon.IsValid) return;
		SetStatTrakAttributes(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, count);
		SetStatTrakAttributes(weapon.AttributeManager.Item.AttributeList.Handle, count);
	}

	private static void SetStatTrakAttributes(nint attributes, int count)
	{
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "kill eater", ViewAsFloat((uint)Math.Max(0, count)));
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "kill eater score type", 0);
	}

	private void RefreshWeapons(CCSPlayerController? player)
	{
		if (!Utility.IsPlayerFullyConnected(player) || !Utility.IsPlayerValid(player)
		    || !player!.PawnIsAlive) return;
		var handles = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
		if (handles == null) return;
		foreach (var handle in handles)
			if (handle.Value is { IsValid: true } weapon && !IsKnife(weapon))
				GivePlayerWeaponSkin(player, weapon, clearIfUnselected: true);
		foreach (var handle in handles)
			if (handle.Value is { IsValid: true } weapon && IsKnife(weapon))
				GivePlayerWeaponSkin(player, weapon, clearIfUnselected: true);
	}

	private void GivePlayerGloves(CCSPlayerController player)
	{
		if (!Utility.IsPlayerFullyConnected(player) || !Utility.IsPlayerValid(player) || !player.PawnIsAlive) return;
		int slot = player.Slot;
		ulong steamId = player.SteamID;
		uint pawnIndex = player.PlayerPawn.Value?.Index ?? 0;
		if (pawnIndex == 0) return;
		AddTimer(0.08f, () => ApplyGloves(slot, steamId, pawnIndex), TimerFlags.STOP_ON_MAPCHANGE);
	}

	private void ApplyGloves(int slot, ulong steamId, uint expectedPawnIndex)
	{
		try
		{
			var player = Utilities.GetPlayerFromSlot(slot);
			if (!Utility.IsPlayerFullyConnected(player) || !Utility.IsPlayerValid(player)
			    || player!.SteamID != steamId || !player.PawnIsAlive) return;
			var pawn = player.PlayerPawn.Value;
			if (pawn == null || !pawn.IsValid || pawn.Index != expectedPawnIndex
			    || !PlayerPaints.TryGet(player, out var state)) return;
			var item = pawn.EconGloves;
			item.AttributeList.Attributes.RemoveAll();
			item.NetworkedDynamicAttributes.Attributes.RemoveAll();

			if (!state.Gloves.TryGetValue(player.Team, out ushort gloveId) || gloveId == 0
			    || !state.TryGetWeapon(player.Team, gloveId, out var glove) || glove is not { Paint: > 0 })
			{
				item.ItemDefinitionIndex = 0;
				item.Initialized = true;
				player.ExecuteClientCommand("lastinv");
				SetBodygroup(pawn, "first_or_third_person", 0);
				AddTimer(0.2f, () => RestoreGloveBodygroup(slot, steamId, expectedPawnIndex), TimerFlags.STOP_ON_MAPCHANGE);
				return;
			}

			item.ItemDefinitionIndex = gloveId;
			UpdatePlayerEconItemId(item);
			ApplyGloveTextureAttributes(item.NetworkedDynamicAttributes.Handle, glove);
			ApplyGloveTextureAttributes(item.AttributeList.Handle, glove);
			item.Initialized = true;
			player.ExecuteClientCommand("lastinv");
			SetBodygroup(pawn, "first_or_third_person", 0);
			AddTimer(0.2f, () => RestoreGloveBodygroup(slot, steamId, expectedPawnIndex), TimerFlags.STOP_ON_MAPCHANGE);
		}
		catch (Exception exception)
		{
			Logger.LogDebug(exception, "[WeaponPaints] Glove target became invalid before application.");
		}
	}

	private static void ApplyGloveTextureAttributes(nint attributes, WeaponInfo glove)
	{
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "set item texture prefab", glove.Paint);
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "set item texture seed", glove.Seed);
		CAttributeListSetOrAddAttributeValueByName.Invoke(attributes, "set item texture wear", NormalizeWear(glove.Wear));
	}

	private static void RestoreGloveBodygroup(int slot, ulong steamId, uint expectedPawnIndex)
	{
		var player = Utilities.GetPlayerFromSlot(slot);
		var pawn = player?.PlayerPawn.Value;
		if (!Utility.IsPlayerValid(player) || player!.SteamID != steamId || !player.PawnIsAlive
		    || pawn == null || !pawn.IsValid || pawn.Index != expectedPawnIndex
		    || !PlayerPaints.TryGet(player, out _)) return;
		SetBodygroup(pawn, "first_or_third_person", 1);
	}

	private static void GivePlayerAgent(CCSPlayerController player)
	{
		if (!Utility.IsPlayerFullyConnected(player) || !Utility.IsPlayerValid(player)
		    || !PlayerPaints.TryGet(player, out _)) return;
		int slot = player.Slot;
		ulong steamId = player.SteamID;
		uint pawnIndex = player.PlayerPawn.Value?.Index ?? 0;
		if (pawnIndex == 0) return;
		Server.NextFrame(() =>
		{
			var current = Utilities.GetPlayerFromSlot(slot);
			var pawn = current?.PlayerPawn.Value;
			if (!Utility.IsPlayerFullyConnected(current) || !Utility.IsPlayerValid(current)
			    || current!.SteamID != steamId || pawn == null || !pawn.IsValid || pawn.Index != pawnIndex
			    || !PlayerPaints.TryGet(current, out var state)) return;
			string? model = Utility.NormalizeAgentModel(current.TeamNum == 3 ? state.CtAgent : state.TAgent);
			if (model == null || !AgentModelsByTeam.TryGetValue(current.TeamNum, out HashSet<string>? models)
			    || !models.Contains(model)) return;
			pawn.SetModel($"agents/models/{model}.vmdl");
		});
	}

	private static void GivePlayerMusicKit(CCSPlayerController player)
	{
		if (!Utility.IsPlayerValid(player) || !PlayerPaints.TryGet(player, out var state)
		    || !state.MusicKits.TryGetValue(player.Team, out ushort musicId)) return;
		if (player.InventoryServices == null) return;
		player.MusicKitID = musicId;
		player.InventoryServices.MusicID = musicId;
		Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
		Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
	}

	private static void GivePlayerPin(CCSPlayerController player)
	{
		if (!Utility.IsPlayerValid(player) || !PlayerPaints.TryGet(player, out var state)
		    || !state.Pins.TryGetValue(player.Team, out ushort pinId) || player.InventoryServices == null) return;
		player.InventoryServices.Rank[5] = pinId > 0 ? (MedalRank_t)pinId : MedalRank_t.MEDAL_RANK_NONE;
		Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
	}

	private void UpdatePlayerEconItemId(CEconItemView item)
	{
		ulong itemId = _nextItemId++;
		item.ItemID = itemId;
		item.ItemIDLow = (uint)itemId;
		item.ItemIDHigh = uint.MaxValue;
	}

	private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
	{
		var pawn = itemServices.Pawn.Value;
		if (pawn == null || !pawn.IsValid || !pawn.Controller.IsValid || pawn.Controller.Value == null) return null;
		var player = new CCSPlayerController(pawn.Controller.Value.Handle);
		return Utility.IsPlayerValid(player) ? player : null;
	}

	private static bool HasChangedPaint(CCSPlayerController player, int definitionIndex, out WeaponInfo? weapon)
	{
		weapon = null;
		return PlayerPaints.TryGet(player, out var state)
		       && state.TryGetWeapon(player.Team, definitionIndex, out weapon)
		       && weapon is { Paint: > 0 };
	}

	private static bool IsKnife(CBasePlayerWeapon weapon) =>
		weapon.DesignerName.Contains("knife", StringComparison.Ordinal)
		|| weapon.DesignerName.Contains("bayonet", StringComparison.Ordinal);

	private static bool IsLegacyModel(int definitionIndex, int paint) =>
		LegacyModelBySkin.GetValueOrDefault((definitionIndex, paint), true);

	private static int GetRandomPaint(int definitionIndex)
	{
		return PaintsByDefinition.TryGetValue(definitionIndex, out int[]? paints) && paints.Length > 0
			? paints[Random.Shared.Next(paints.Length)]
			: 0;
	}

	public static void SubclassChange(CBasePlayerWeapon weapon, ushort definitionIndex)
	{
		if (weapon.IsValid) weapon.AcceptInput("ChangeSubclass", value: definitionIndex.ToString());
	}

	public static void SetBodygroup(CCSPlayerPawn pawn, string group, int value)
	{
		if (pawn.IsValid) pawn.AcceptInput("SetBodygroup", value: $"{group},{value}");
	}

	private static void UpdateWeaponMeshGroupMask(CBaseEntity weapon, bool legacy)
	{
		if (weapon.IsValid && weapon.CBodyComponent?.SceneNode != null)
			weapon.AcceptInput("SetBodygroup", value: $"body,{(legacy ? 1 : 0)}");
	}

	private static float NormalizeWear(float value) =>
		float.IsFinite(value) ? Math.Clamp(value, 0.000001f, 1f) : 0.000001f;

	private static float ViewAsFloat(uint value) => BitConverter.Int32BitsToSingle((int)value);
}

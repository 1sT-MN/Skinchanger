using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

public partial class WeaponPaints
{
	private bool _mvpPlayed;

	private HookResult OnClientFullConnect(EventPlayerConnectFull @event, GameEventInfo _)
	{
		QueuePlayerLoad(@event.Userid);
		return HookResult.Continue;
	}

	private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo eventInfo)
	{
		var player = @event.Userid;
		if (player is null || player.IsBot || player.SteamID == 0) return HookResult.Continue;

		int slot = player.Slot;
		ulong steamId = player.SteamID;
		if (PlayerPaints.TryGet(player, out var state) && WeaponSync != null)
		{
			var statTrak = state.Weapons.SelectMany(team => team.Value.Select(weapon =>
				(Team: weapon.Value.StorageTeam, DefinitionIndex: weapon.Key,
					Enabled: weapon.Value.StatTrak, Count: weapon.Value.StatTrakCount)))
				.GroupBy(row => (row.Team, row.DefinitionIndex))
				.Select(group => group.OrderByDescending(row => row.Count).First())
				.ToList();
			_ = WeaponSync.SaveStatTrakAsync(steamId, statTrak);
		}

		PlayerPaints.Remove(slot, steamId);
		_refreshesInProgress.TryRemove(steamId, out _);
		CommandsCooldown.Remove(steamId);
		_playerWeaponImage.Remove(steamId);
		return HookResult.Continue;
	}

	private void QueuePlayerLoad(
		CCSPlayerController? player,
		bool refreshInventory = true,
		bool notify = false,
		bool trackRefresh = false)
	{
		if (!Utility.IsPlayerValid(player) || player!.SteamID == 0 || WeaponSync == null) return;
		if (trackRefresh && !_refreshesInProgress.TryAdd(player.SteamID, -1)) return;
		var session = PlayerPaints.BeginLoad(player.Slot, player.SteamID, retainExistingState: trackRefresh);
		if (trackRefresh) _refreshesInProgress[player.SteamID] = session.Generation;
		_ = CompletePlayerLoadAsync(session, refreshInventory, notify);
	}

	private async Task CompletePlayerLoadAsync(PlayerLoadSession session, bool refreshInventory, bool notify)
	{
		var synchronization = WeaponSync;
		if (synchronization == null) return;
		bool loaded = await synchronization.LoadPlayerDataAsync(session).ConfigureAwait(false);
		if (!loaded)
		{
			RemoveRefreshMarker(session);
			return;
		}
		Server.NextFrame(() =>
		{
			RemoveRefreshMarker(session);
			if (!PlayerPaints.IsCurrent(session)) return;
			var player = Utilities.GetPlayerFromSlot(session.Slot);
			if (!Utility.IsPlayerValid(player) || player!.SteamID != session.SteamId64) return;
			ApplyPlayerCosmetics(player, refreshInventory);
			if (notify && !string.IsNullOrEmpty(Localizer["wp_command_refresh_done"]))
				player.Print(Localizer["wp_command_refresh_done"]);
		});
	}

	private void RemoveRefreshMarker(PlayerLoadSession session) =>
		_refreshesInProgress.TryRemove(
			new KeyValuePair<ulong, long>(session.SteamId64, session.Generation));

	private void LoadConnectedPlayers()
	{
		foreach (var player in Utilities.GetPlayers()) QueuePlayerLoad(player);
	}

	private void ResetMapState()
	{
		PlayerPaints.Clear();
		_appliedWeaponSelections.Clear();
		_refreshesInProgress.Clear();
		_playerWeaponImage.Clear();
		_fadeSeed = 0;
		_nextImageRenderTick = 0;
		_nextImageGeneration = 0;
		_nextItemId = MinimumCustomItemId;
		_gBCommandsAllowed = true;
		_mvpPlayed = false;
	}

	private void OnMapStart(string _)
	{
		ResetMapState();
		Server.NextFrame(LoadConnectedPlayers);
	}

	private void OnMapEnd() => ResetMapState();

	private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo eventInfo)
	{
		var player = @event.Userid;
		if (!Utility.IsPlayerValid(player) || player!.SteamID == 0) return HookResult.Continue;
		int slot = player.Slot;
		ulong steamId = player.SteamID;
		uint pawnIndex = player.PlayerPawn.Value?.Index ?? 0;
		if (pawnIndex == 0) return HookResult.Continue;
		Server.NextFrame(() =>
		{
			var current = Utilities.GetPlayerFromSlot(slot);
			var pawn = current?.PlayerPawn.Value;
			if (Utility.IsPlayerValid(current) && current!.SteamID == steamId
			    && pawn is { IsValid: true } && pawn.Index == pawnIndex
			    && PlayerPaints.TryGet(current, out _))
				ApplyPlayerCosmetics(current, refreshInventory: true);
		});
		return HookResult.Continue;
	}

	private void ApplyPlayerCosmetics(CCSPlayerController player, bool refreshInventory)
	{
		if (!Utility.IsPlayerValid(player) || !PlayerPaints.TryGet(player, out _)) return;
		if (Config.Additional.MusicEnabled) GivePlayerMusicKit(player);
		if (Config.Additional.AgentEnabled) GivePlayerAgent(player);
		if (Config.Additional.GloveEnabled) GivePlayerGloves(player);
		if (Config.Additional.PinsEnabled) GivePlayerPin(player);
		if (refreshInventory && (Config.Additional.SkinEnabled || Config.Additional.KnifeEnabled)) RefreshWeapons(player);
	}

	private HookResult OnRoundEnd(EventRoundEnd _, GameEventInfo __)
	{
		_gBCommandsAllowed = false;
		return HookResult.Continue;
	}

	private HookResult OnRoundStart(EventRoundStart _, GameEventInfo __)
	{
		_gBCommandsAllowed = true;
		_mvpPlayed = false;
		return HookResult.Continue;
	}

	private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
	{
		if (_mvpPlayed) return HookResult.Continue;
		var player = @event.Userid;
		if (!Utility.IsPlayerValid(player) || !PlayerPaints.TryGet(player, out var state)
		    || !state.MusicKits.TryGetValue(player!.Team, out ushort musicId) || musicId == 0)
			return HookResult.Continue;

		@event.Musickitid = musicId;
		@event.Nomusic = 0;
		info.DontBroadcast = true;
		_mvpPlayed = true;
		new EventRoundMvp(true) { Userid = player, Musickitid = musicId, Nomusic = 0 }.FireEvent(false);
		return HookResult.Continue;
	}

	private HookResult OnGiveNamedItemPost(DynamicHook hook)
	{
		try
		{
			var weapon = hook.GetReturn<CBasePlayerWeapon>();
			if (weapon == null || !weapon.IsValid
			    || (!IsKnife(weapon) && !WeaponList.ContainsKey(weapon.DesignerName)))
				return HookResult.Continue;
			var player = GetPlayerFromItemServices(hook.GetParam<CCSPlayer_ItemServices>(0));
			if (player != null) GivePlayerWeaponSkin(player, weapon);
		}
		catch (Exception exception)
		{
			Logger.LogDebug(exception, "[WeaponPaints] GiveNamedItem post hook could not apply a cosmetic.");
		}
		return HookResult.Continue;
	}

	private void OnEntityCreated(CEntityInstance entity)
	{
		bool isKnife = entity.DesignerName.Contains("knife", StringComparison.Ordinal)
		               || entity.DesignerName.Contains("bayonet", StringComparison.Ordinal);
		if (!isKnife && !WeaponList.ContainsKey(entity.DesignerName)) return;
		_appliedWeaponSelections.TryRemove(entity.Index, out _);
		nint handle = entity.Handle;
		uint index = entity.Index;
		string designerName = entity.DesignerName;
		Server.NextWorldUpdate(() => TryApplySpawnedWeapon(handle, index, designerName));
	}

	private void TryApplySpawnedWeapon(nint handle, uint expectedIndex, string expectedDesignerName)
	{
		try
		{
			var weapon = new CBasePlayerWeapon(handle);
			if (!weapon.IsValid || weapon.Index != expectedIndex
			    || !weapon.DesignerName.Equals(expectedDesignerName, StringComparison.Ordinal)) return;
			var player = FindCurrentWeaponOwner(weapon);
			bool allowOriginalOwner = false;
			if (player == null)
			{
				ulong originalOwner = ((ulong)weapon.OriginalOwnerXuidHigh << 32) | weapon.OriginalOwnerXuidLow;
				if (originalOwner != 0)
				{
					player = Utilities.GetPlayers().FirstOrDefault(candidate =>
						Utility.IsPlayerValid(candidate) && candidate.SteamID == originalOwner);
					allowOriginalOwner = player != null;
				}
			}
			if (player != null) GivePlayerWeaponSkin(player, weapon, allowOriginalOwner);
		}
		catch (Exception exception)
		{
			Logger.LogDebug(exception, "[WeaponPaints] Spawned weapon became invalid before cosmetic application.");
		}
	}

	private CCSPlayerController? FindCurrentWeaponOwner(CBasePlayerWeapon weapon)
	{
		CCSPlayerController? directOwner = weapon.OwnerEntity.Get()?.As<CCSPlayerController>();
		if (Utility.IsPlayerValid(directOwner) && IsWeaponOwnedBy(directOwner!, weapon)) return directOwner;

		// OwnerEntity is the constant-time path. The scan handles short engine
		// transition windows where the owner handle has not caught up yet.
		foreach (CCSPlayerController player in Utilities.GetPlayers())
			if (Utility.IsPlayerValid(player) && IsWeaponOwnedBy(player, weapon)) return player;
		return null;
	}

	private void OnTick()
	{
		if (!Config.Additional.ShowSkinImage || _playerWeaponImage.Count == 0) return;
		int currentTick = Server.TickCount;
		if (currentTick < _nextImageRenderTick) return;
		_nextImageRenderTick = currentTick + ImageRenderIntervalTicks;
		foreach (var player in Utilities.GetPlayers())
			if (Utility.IsPlayerValid(player)
			    && _playerWeaponImage.TryGetValue(player.SteamID, out WeaponImageDisplay image))
				player.PrintToCenterHtml(image.Html);
	}

	private HookResult OnItemPickup(EventItemPickup @event, GameEventInfo eventInfo)
	{
		var player = @event.Userid;
		if (!Utility.IsPlayerValid(player) || @event.Defindex == 49
		    || @event.Item.Contains("c4", StringComparison.OrdinalIgnoreCase)) return HookResult.Continue;
		int slot = player!.Slot;
		ulong steamId = player.SteamID;
		if (@event.Defindex > int.MaxValue) return HookResult.Continue;
		int definitionIndex = (int)@event.Defindex;
		if (definitionIndex is not (42 or 59) && !WeaponDefindex.ContainsKey(definitionIndex))
			return HookResult.Continue;
		uint pawnIndex = player.PlayerPawn.Value?.Index ?? 0;
		if (pawnIndex == 0) return HookResult.Continue;
		Server.NextFrame(() => ApplyPickedUpWeapon(slot, steamId, pawnIndex, definitionIndex));
		return HookResult.Continue;
	}

	private void ApplyPickedUpWeapon(int slot, ulong steamId, uint expectedPawnIndex, int definitionIndex)
	{
		var player = Utilities.GetPlayerFromSlot(slot);
		if (!Utility.IsPlayerValid(player) || player!.SteamID != steamId) return;
		var pawn = player.PlayerPawn.Value;
		if (pawn == null || !pawn.IsValid || pawn.Index != expectedPawnIndex) return;
		var weapons = pawn.WeaponServices?.MyWeapons;
		if (weapons == null) return;
		foreach (var handle in weapons)
		{
			var weapon = handle.Value;
			if (weapon == null || !weapon.IsValid) continue;
			if (weapon.AttributeManager.Item.ItemDefinitionIndex != definitionIndex) continue;
			GivePlayerWeaponSkin(player, weapon, clearIfUnselected: true);
		}
	}

	private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo _)
	{
		var attacker = @event.Attacker;
		var victim = @event.Userid;
		if (!Utility.IsPlayerValid(attacker) || victim == null || !victim.IsValid || victim == attacker)
			return HookResult.Continue;
		var weapon = attacker!.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
		if (weapon == null || !weapon.IsValid || !HasChangedPaint(attacker, weapon.AttributeManager.Item.ItemDefinitionIndex, out var info)
		    || info is not { StatTrak: true }) return HookResult.Continue;
		info.StatTrakCount++;
		SetStatTrakAttributes(weapon, info.StatTrakCount);
		return HookResult.Continue;
	}

	private void RegisterListeners()
	{
		RegisterListener<Listeners.OnMapStart>(OnMapStart);
		RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
		RegisterListener<Listeners.OnEntitySpawned>(OnEntityCreated);
		RegisterEventHandler<EventPlayerConnectFull>(OnClientFullConnect);
		RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
		RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
		RegisterEventHandler<EventRoundStart>(OnRoundStart);
		RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
		RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
		RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
		RegisterEventHandler<EventItemPickup>(OnItemPickup);
		if (Config.Additional.ShowSkinImage) RegisterListener<Listeners.OnTick>(OnTick);
		VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
	}
}

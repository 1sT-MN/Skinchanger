using System.Collections.Concurrent;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponPaints;

internal sealed class PlayerPaintState(ulong steamId64)
{
	internal ulong SteamId64 { get; } = steamId64;
	internal Dictionary<CsTeam, Dictionary<int, WeaponInfo>> Weapons { get; } = [];
	internal Dictionary<CsTeam, string> Knives { get; } = [];
	internal Dictionary<CsTeam, ushort> Gloves { get; } = [];
	internal Dictionary<CsTeam, ushort> MusicKits { get; } = [];
	internal Dictionary<CsTeam, ushort> Pins { get; } = [];
	internal string? CtAgent { get; set; }
	internal string? TAgent { get; set; }

	internal Dictionary<int, WeaponInfo> GetOrCreateWeapons(CsTeam team)
	{
		if (!Weapons.TryGetValue(team, out var weapons))
		{
			weapons = [];
			Weapons[team] = weapons;
		}

		return weapons;
	}

	internal bool TryGetWeapon(CsTeam team, int definitionIndex, out WeaponInfo? weapon)
	{
		weapon = null;
		return Weapons.TryGetValue(team, out var weapons)
		       && weapons.TryGetValue(definitionIndex, out weapon);
	}
}

internal readonly record struct PlayerLoadSession(int Slot, ulong SteamId64, long Generation);

/// <summary>
/// Persistent cosmetic state is keyed by SteamID64. Slots only identify a live load
/// session; its generation prevents a completed old query from publishing after a
/// disconnect, reconnect, hot reload, or slot reuse.
/// </summary>
internal sealed class PlayerPaintCache
{
	private readonly record struct CacheEntry(long Generation, PlayerPaintState State);

	private readonly ConcurrentDictionary<ulong, CacheEntry> _states = new();
	private readonly ConcurrentDictionary<int, PlayerLoadSession> _sessions = new();
	private readonly ConcurrentDictionary<ulong, PlayerLoadSession> _steamSessions = new();
	private long _nextGeneration;

	internal PlayerLoadSession BeginLoad(int slot, ulong steamId64, bool retainExistingState = false)
	{
		var session = new PlayerLoadSession(slot, steamId64, Interlocked.Increment(ref _nextGeneration));
		_sessions.TryGetValue(slot, out var previousAtSlot);
		_steamSessions.TryGetValue(steamId64, out var previousForSteamId);

		// Publish the SteamID generation first. This immediately invalidates an older
		// load for the same player even when a reconnect moved them to another slot.
		_steamSessions[steamId64] = session;
		_sessions[slot] = session;

		if (previousForSteamId.SteamId64 != 0 && previousForSteamId != session)
			_sessions.TryRemove(new KeyValuePair<int, PlayerLoadSession>(previousForSteamId.Slot, previousForSteamId));
		if (previousAtSlot.SteamId64 != 0 && previousAtSlot.SteamId64 != steamId64)
		{
			_steamSessions.TryRemove(new KeyValuePair<ulong, PlayerLoadSession>(previousAtSlot.SteamId64, previousAtSlot));
			RemoveStateAtOrBefore(previousAtSlot.SteamId64, previousAtSlot.Generation);
		}
		if (!retainExistingState)
			RemoveStateAtOrBefore(steamId64, session.Generation - 1);
		return session;
	}

	internal bool IsCurrent(PlayerLoadSession session) =>
		_sessions.TryGetValue(session.Slot, out var slotSession) && slotSession == session
		&& _steamSessions.TryGetValue(session.SteamId64, out var steamSession) && steamSession == session;

	internal bool Publish(PlayerLoadSession session, PlayerPaintState state)
	{
		if (state.SteamId64 != session.SteamId64 || !IsCurrent(session)) return false;
		var entry = new CacheEntry(session.Generation, state);
		_states[session.SteamId64] = entry;
		if (IsCurrent(session)) return true;
		_states.TryRemove(new KeyValuePair<ulong, CacheEntry>(session.SteamId64, entry));
		return false;
	}

	internal bool TryGet(CCSPlayerController? player, out PlayerPaintState state)
	{
		state = null!;
		if (player is null || !player.IsValid || player.SteamID == 0) return false;
		if (!_sessions.TryGetValue(player.Slot, out var session) || session.SteamId64 != player.SteamID)
			return false;
		if (!_steamSessions.TryGetValue(player.SteamID, out var steamSession) || steamSession != session)
			return false;
		if (!_states.TryGetValue(session.SteamId64, out var entry)) return false;
		if (entry.Generation > session.Generation) return false;
		state = entry.State;
		return true;
	}

	internal bool TryGet(ulong steamId64, out PlayerPaintState state)
	{
		state = null!;
		if (!_states.TryGetValue(steamId64, out var entry)) return false;
		state = entry.State;
		return true;
	}

	internal void Remove(int slot, ulong steamId64)
	{
		if (_sessions.TryGetValue(slot, out var current) && current.SteamId64 == steamId64)
		{
			_sessions.TryRemove(new KeyValuePair<int, PlayerLoadSession>(slot, current));
			_steamSessions.TryRemove(new KeyValuePair<ulong, PlayerLoadSession>(steamId64, current));
			RemoveStateAtOrBefore(steamId64, current.Generation);
		}
	}

	private void RemoveStateAtOrBefore(ulong steamId64, long generation)
	{
		while (_states.TryGetValue(steamId64, out var entry) && entry.Generation <= generation)
			if (_states.TryRemove(new KeyValuePair<ulong, CacheEntry>(steamId64, entry))) return;
	}

	internal void Clear()
	{
		_sessions.Clear();
		_steamSessions.Clear();
		_states.Clear();
	}
}

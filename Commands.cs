using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MenuManager;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

public partial class WeaponPaints
{
	private void RegisterCommands()
	{
		if (Config.Additional.SkinEnabled && Config.Additional.CommandStatTrakEnabled)
			RegisterAliases(Config.Additional.CommandStattrak, "StatTrak toggle", OnCommandStatTrak);
		if (Config.Additional.CommandWebsiteEnabled)
			RegisterAliases(Config.Additional.CommandSkin, "WeaponPaints information", OnCommandWebsite);
		if (Config.Additional.CommandWpEnabled)
			RegisterAliases(Config.Additional.CommandRefresh, "Reload WeaponPaints selections", OnCommandRefresh);
		if (Config.Additional.CommandKillEnabled)
			RegisterAliases(Config.Additional.CommandKill, "Commit suicide", (player, _) =>
			{
				if (Utility.IsPlayerValid(player) && player!.PlayerPawn.Value is { IsValid: true } pawn)
					pawn.CommitSuicide(true, false);
			});
		AddCommand("wp_refresh", "Reload WeaponPaints selections for a SteamID64 or all players", OnAdminRefresh);
	}

	private void RegisterAliases(IEnumerable<string> aliases, string description, CommandInfo.CommandCallback callback)
	{
		foreach (string alias in aliases.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
			AddCommand($"css_{alias.Trim()}", description, callback);
	}

	private bool TryUseCooldown(CCSPlayerController player)
	{
		DateTime now = DateTime.UtcNow;
		if (CommandsCooldown.TryGetValue(player.SteamID, out DateTime until) && now < until)
		{
			if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"])) player.Print(Localizer["wp_command_cooldown"]);
			return false;
		}
		CommandsCooldown[player.SteamID] = now.AddSeconds(Math.Max(0, Config.CmdRefreshCooldownSeconds));
		return true;
	}

	private void OnCommandRefresh(CCSPlayerController? player, CommandInfo _)
	{
		if (!Config.Additional.CommandWpEnabled || !_gBCommandsAllowed || !Utility.IsPlayerValid(player)
		    || !TryUseCooldown(player!)) return;
		QueuePlayerLoad(player, refreshInventory: true, notify: true, trackRefresh: true);
	}

	private void OnCommandWebsite(CCSPlayerController? player, CommandInfo _)
	{
		if (!Utility.IsPlayerValid(player)) return;
		if (!string.IsNullOrEmpty(Localizer["wp_info_website"])) player!.Print(Localizer["wp_info_website", Config.Website]);
		if (!string.IsNullOrEmpty(Localizer["wp_info_refresh"])) player!.Print(Localizer["wp_info_refresh"]);
		if (Config.Additional.KnifeEnabled && !string.IsNullOrEmpty(Localizer["wp_info_knife"])) player!.Print(Localizer["wp_info_knife"]);
		if (Config.Additional.GloveEnabled && !string.IsNullOrEmpty(Localizer["wp_info_glove"])) player!.Print(Localizer["wp_info_glove"]);
		if (Config.Additional.AgentEnabled && !string.IsNullOrEmpty(Localizer["wp_info_agent"])) player!.Print(Localizer["wp_info_agent"]);
		if (Config.Additional.MusicEnabled && !string.IsNullOrEmpty(Localizer["wp_info_music"])) player!.Print(Localizer["wp_info_music"]);
		if (Config.Additional.PinsEnabled && !string.IsNullOrEmpty(Localizer["wp_info_pin"])) player!.Print(Localizer["wp_info_pin"]);
	}

	private void OnAdminRefresh(CCSPlayerController? caller, CommandInfo command)
	{
		if (caller != null) return;
		string target = command.GetArg(1);
		if (string.IsNullOrWhiteSpace(target))
		{
			Logger.LogInformation("[WeaponPaints] Usage: wp_refresh <steamid64|all>");
			return;
		}

		foreach (var player in Utilities.GetPlayers())
		{
			if (!Utility.IsPlayerValid(player) || (!target.Equals("all", StringComparison.OrdinalIgnoreCase)
			    && !player.SteamID.ToString().Equals(target, StringComparison.Ordinal))) continue;
			QueuePlayerLoad(player, refreshInventory: true, notify: false, trackRefresh: true);
		}
	}

	private void OnCommandStatTrak(CCSPlayerController? player, CommandInfo commandInfo)
	{
		var weapon = player?.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
		if (!Utility.IsPlayerValid(player) || weapon == null || !weapon.IsValid
		    || !PlayerPaints.TryGet(player, out var state)
		    || !state.TryGetWeapon(player!.Team, weapon.AttributeManager.Item.ItemDefinitionIndex, out var info)
		    || info == null) return;
		info.StatTrak = !info.StatTrak;
		GivePlayerWeaponSkin(player, weapon);
		_ = WeaponSync?.SaveWeaponAsync(player.SteamID, (CsTeam)info.StorageTeam,
			weapon.AttributeManager.Item.ItemDefinitionIndex, info);
		if (!string.IsNullOrEmpty(Localizer["wp_stattrak_action"])) player.Print(Localizer["wp_stattrak_action"]);
	}

	private void SetupKnifeMenu()
	{
		if (!Config.Additional.CommandKnifeEnabled) return;
		var menu = Utility.CreateMenu(Localizer["wp_knife_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;
		foreach (var knife in WeaponList.Where(entry => entry.Key.StartsWith("weapon_knife") || entry.Key == "weapon_bayonet"))
		{
			string key = knife.Key;
			string name = knife.Value;
			menu.AddMenuOption(name, (player, _) => SelectKnife(player, key, name));
		}
		RegisterMenuAliases(Config.Additional.CommandKnife, "Knife menu", menu);
	}

	private void SelectKnife(CCSPlayerController player, string key, string name)
	{
		if (!TryGetSelectionState(player, out var state)) return;
		CsTeam[] teams = TeamsFor(player);
		foreach (CsTeam team in teams) state.Knives[team] = key;
		_ = WeaponSync?.SaveKnifeAsync(player.SteamID, key, teams);
		if (!string.IsNullOrEmpty(Localizer["wp_knife_menu_select"])) player.Print(Localizer["wp_knife_menu_select", name]);
		RefreshWeapons(player);
	}

	private void SetupSkinsMenu()
	{
		if (!Config.Additional.CommandSkinsEnabled) return;
		var menu = Utility.CreateMenu(Localizer["wp_skin_menu_weapon_title"]);
		if (menu == null) return;
		foreach (var weapon in WeaponList.Where(entry => entry.Key != "weapon_knife"))
		{
			string className = weapon.Key;
			string displayName = weapon.Value;
			menu.AddMenuOption(displayName, (player, _) => OpenSkinMenu(player, className, displayName));
		}
		RegisterMenuAliases(Config.Additional.CommandSkinSelection, "Weapon skin menu", menu);
	}

	private void OpenSkinMenu(CCSPlayerController player, string className, string displayName)
	{
		if (!TryGetSelectionState(player, out _)) return;
		if (_skinMenus.TryGetValue(className, out IMenu? cachedMenu))
		{
			cachedMenu.Open(player);
			return;
		}
		var menu = Utility.CreateMenu(Localizer["wp_skin_menu_skin_title", displayName]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;
		if (!SkinsByWeapon.TryGetValue(className, out JObject[]? weaponSkins)) return;
		foreach (JObject row in weaponSkins)
		{
			if (row["weapon_defindex"]?.Value<int>() is not { } definitionIndex || definitionIndex <= 0
			    || row["paint"]?.Value<int>() is not { } paint || paint < 0
			    || row["paint_name"]?.ToString() is not { Length: > 0 } name) continue;
			string label = $"{name} ({paint})";
			string image = row["image"]?.ToString() ?? "";
			menu.AddMenuOption(label, (selectedPlayer, _) => SelectSkin(selectedPlayer, definitionIndex, paint, label, image));
		}
		_skinMenus[className] = menu;
		menu.Open(player);
	}

	private void SelectSkin(CCSPlayerController player, int definitionIndex, int paint, string label, string image)
	{
		if (!TryGetSelectionState(player, out var state)) return;
		CsTeam[] teams = TeamsFor(player);
		var selections = new List<(CsTeam Team, WeaponInfo Weapon)>(teams.Length);
		foreach (CsTeam team in teams)
		{
			var weapons = state.GetOrCreateWeapons(team);
			WeaponInfo info = weapons.TryGetValue(definitionIndex, out var existing)
				? existing.Clone()
				: new WeaponInfo();
			info.StorageTeam = (int)team;
			weapons[definitionIndex] = info;
			info.Paint = paint;
			info.Seed = 0;
			info.Wear = 0.01f;
			selections.Add((team, info));
		}
		_ = WeaponSync?.SaveWeaponSelectionsAsync(player.SteamID, definitionIndex, selections);
		ShowImage(player, image);
		player.Print(Localizer["wp_skin_menu_select", label]);
		RefreshWeapons(player);
	}

	private void SetupGlovesMenu()
	{
		if (!Config.Additional.CommandGlovesEnabled) return;
		var menu = Utility.CreateMenu(Localizer["wp_glove_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;
		foreach (JObject row in GlovesList)
		{
			if (row["weapon_defindex"]?.Value<int>() is not { } definitionIndex || definitionIndex is < 0 or > ushort.MaxValue
			    || row["paint"]?.Value<int>() is not { } paint || paint < 0
			    || row["paint_name"]?.ToString() is not { Length: > 0 } name) continue;
			string image = row["image"]?.ToString() ?? "";
			menu.AddMenuOption(name, (player, _) => SelectGloves(player, (ushort)definitionIndex, paint, name, image));
		}
		RegisterMenuAliases(Config.Additional.CommandGlove, "Glove menu", menu);
	}

	private void SelectGloves(CCSPlayerController player, ushort definitionIndex, int paint, string name, string image)
	{
		if (!TryGetSelectionState(player, out var state)) return;
		CsTeam[] teams = TeamsFor(player);
		var selections = new List<(CsTeam Team, WeaponInfo? Weapon)>(teams.Length);
		foreach (CsTeam team in teams)
		{
			state.Gloves[team] = definitionIndex;
			if (definitionIndex == 0)
			{
				selections.Add((team, null));
				continue;
			}
			var weapons = state.GetOrCreateWeapons(team);
			WeaponInfo info = weapons.TryGetValue(definitionIndex, out var existing)
				? existing.Clone()
				: new WeaponInfo();
			info.StorageTeam = (int)team;
			weapons[definitionIndex] = info;
			info.Paint = paint;
			info.Seed = 0;
			info.Wear = 0.000001f;
			selections.Add((team, info));
		}
		_ = WeaponSync?.SaveGloveSelectionAsync(player.SteamID, definitionIndex, selections);
		ShowImage(player, image);
		if (!string.IsNullOrEmpty(Localizer["wp_glove_menu_select"])) player.Print(Localizer["wp_glove_menu_select", name]);
		GivePlayerGloves(player);
	}

	private void SetupAgentsMenu()
	{
		if (!Config.Additional.CommandAgentsEnabled) return;
		foreach (string alias in Config.Additional.CommandAgent.Distinct(StringComparer.OrdinalIgnoreCase))
			AddCommand($"css_{alias}", "Agent menu", (player, _) => OpenAgentMenu(player));
	}

	private void OpenAgentMenu(CCSPlayerController? player)
	{
		if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed || !TryUseCooldown(player!)
		    || !TryGetSelectionState(player!, out _)) return;
		if (_agentMenus.TryGetValue(player!.TeamNum, out IMenu? cachedMenu))
		{
			cachedMenu.Open(player);
			return;
		}
		var menu = Utility.CreateMenu(Localizer["wp_agent_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;
		foreach (JObject row in AgentsList.Where(row => row["team"]?.Value<int>() == player!.TeamNum))
		{
			string? name = row["agent_name"]?.ToString();
			if (string.IsNullOrWhiteSpace(name)) continue;
			string? model = Utility.NormalizeAgentModel(row["model"]?.ToString());
			string image = row["image"]?.ToString() ?? "";
			menu.AddMenuOption(name, (selectedPlayer, _) => SelectAgent(selectedPlayer, model, name, image));
		}
		_agentMenus[player.TeamNum] = menu;
		menu.Open(player!);
	}

	private void SelectAgent(CCSPlayerController player, string? model, string name, string image)
	{
		if (!TryGetSelectionState(player, out var state)) return;
		if (player.TeamNum == 3) state.CtAgent = model;
		else if (player.TeamNum == 2) state.TAgent = model;
		else return;
		_ = WeaponSync?.SaveAgentAsync(player.SteamID, state.CtAgent, state.TAgent);
		ShowImage(player, image);
		if (!string.IsNullOrEmpty(Localizer["wp_agent_menu_select"])) player.Print(Localizer["wp_agent_menu_select", name]);
		GivePlayerAgent(player);
	}

	private void SetupMusicMenu()
	{
		if (!Config.Additional.CommandMusicEnabled) return;
		var menu = Utility.CreateMenu(Localizer["wp_music_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;
		menu.AddMenuOption(Localizer["None"], (player, _) => SelectMusic(player, 0, Localizer["None"], ""));
		foreach (JObject row in MusicList)
		{
			if (row["id"]?.Value<int>() is not { } id || id is < 0 or > ushort.MaxValue
			    || row["name"]?.ToString() is not { Length: > 0 } name) continue;
			string image = row["image"]?.ToString() ?? "";
			menu.AddMenuOption(name, (player, _) => SelectMusic(player, (ushort)id, name, image));
		}
		RegisterMenuAliases(Config.Additional.CommandMusic, "Music kit menu", menu);
	}

	private void SelectMusic(CCSPlayerController player, ushort id, string name, string image)
	{
		if (!TryGetSelectionState(player, out var state)) return;
		CsTeam[] teams = TeamsFor(player);
		foreach (CsTeam team in teams) state.MusicKits[team] = id;
		_ = WeaponSync?.SaveMusicAsync(player.SteamID, id, teams);
		ShowImage(player, image);
		if (!string.IsNullOrEmpty(Localizer["wp_music_menu_select"])) player.Print(Localizer["wp_music_menu_select", name]);
		GivePlayerMusicKit(player);
	}

	private void SetupPinsMenu()
	{
		if (!Config.Additional.CommandPinsEnabled) return;
		var menu = Utility.CreateMenu(Localizer["wp_pins_menu_title"]);
		if (menu == null) return;
		menu.PostSelectAction = PostSelectAction.Close;
		menu.AddMenuOption(Localizer["None"], (player, _) => SelectPin(player, 0, Localizer["None"], ""));
		foreach (JObject row in PinsList)
		{
			if (row["id"]?.Value<int>() is not { } id || id is < 0 or > ushort.MaxValue
			    || row["name"]?.ToString() is not { Length: > 0 } name) continue;
			string image = row["image"]?.ToString() ?? "";
			menu.AddMenuOption(name, (player, _) => SelectPin(player, (ushort)id, name, image));
		}
		RegisterMenuAliases(Config.Additional.CommandPin, "Pin menu", menu);
	}

	private void SelectPin(CCSPlayerController player, ushort id, string name, string image)
	{
		if (!TryGetSelectionState(player, out var state)) return;
		CsTeam[] teams = TeamsFor(player);
		foreach (CsTeam team in teams) state.Pins[team] = id;
		_ = WeaponSync?.SavePinAsync(player.SteamID, id, teams);
		ShowImage(player, image);
		if (!string.IsNullOrEmpty(Localizer["wp_pins_menu_select"])) player.Print(Localizer["wp_pins_menu_select", name]);
		GivePlayerPin(player);
	}

	private void RegisterMenuAliases(IEnumerable<string> aliases, string description, IMenu menu)
	{
		foreach (string alias in aliases.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
			AddCommand($"css_{alias.Trim()}", description, (player, commandInfo) =>
			{
				if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed || !TryUseCooldown(player!)
				    || !TryGetSelectionState(player!, out var state)) return;
				menu.Open(player!);
			});
	}

	private bool TryGetSelectionState(CCSPlayerController player, out PlayerPaintState state)
	{
		state = null!;
		return Utility.IsPlayerValid(player) && !_refreshesInProgress.ContainsKey(player.SteamID)
		       && PlayerPaints.TryGet(player, out state);
	}

	private static CsTeam[] TeamsFor(CCSPlayerController player) => player.TeamNum is 2 or 3
		? [player.Team]
		: [CsTeam.Terrorist, CsTeam.CounterTerrorist];

	private void ShowImage(CCSPlayerController player, string image)
	{
		if (!Config.Additional.ShowSkinImage || string.IsNullOrWhiteSpace(image)) return;
		ulong steamId = player.SteamID;
		var display = new WeaponImageDisplay($"<img src='{image}'</img>", ++_nextImageGeneration);
		_playerWeaponImage[steamId] = display;
		AddTimer(2f, () =>
		{
			if (_playerWeaponImage.TryGetValue(steamId, out WeaponImageDisplay current) && current == display)
				_playerWeaponImage.Remove(steamId);
		}, TimerFlags.STOP_ON_MAPCHANGE);
	}
}

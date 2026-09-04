using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Menu;
using MenuManager;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WeaponPaints
{
	internal static class Utility
	{
		internal static bool IsPlayerValid(CCSPlayerController? player)
		{
			if (player is null || WeaponPaints.WeaponSync is null) return false;

			return player is { IsValid: true, IsBot: false, IsHLTV: false, UserId: not null };
		}

		internal static bool IsPlayerFullyConnected(CCSPlayerController? player) =>
			player is { Connected: PlayerConnectedState.Connected };

		internal static string? NormalizeAgentModel(string? model)
		{
			if (string.IsNullOrWhiteSpace(model)) return null;
			string normalized = model.Trim().Trim('\'', '"').Replace('\\', '/').Trim('/');
			if (normalized.StartsWith("agents/models/", StringComparison.OrdinalIgnoreCase))
				normalized = normalized["agents/models/".Length..];
			if (normalized.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
				normalized = normalized[..^".vmdl".Length];
			if (normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
			    || normalized.Contains("..", StringComparison.Ordinal)
			    || normalized.Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '-' or '/')))
				return null;
			return normalized;
		}

		internal static void LoadCatalogFiles(string moduleDirectory, string language, ILogger logger)
		{
			string safeLanguage = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
			WeaponPaints.SkinsList = LoadList(Path.Combine(moduleDirectory, "data", $"skins_{safeLanguage}.json"), logger);
			WeaponPaints.GlovesList = LoadList(Path.Combine(moduleDirectory, "data", $"gloves_{safeLanguage}.json"), logger);
			WeaponPaints.AgentsList = LoadList(Path.Combine(moduleDirectory, "data", $"agents_{safeLanguage}.json"), logger);
			WeaponPaints.MusicList = LoadList(Path.Combine(moduleDirectory, "data", $"music_{safeLanguage}.json"), logger);
			WeaponPaints.PinsList = LoadList(Path.Combine(moduleDirectory, "data", $"collectibles_{safeLanguage}.json"), logger);
			BuildCatalogIndexes();
		}

		private static void BuildCatalogIndexes()
		{
			var skinsByWeapon = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
			var legacyModels = new Dictionary<(int DefinitionIndex, int Paint), bool>();
			var paintsByDefinition = new Dictionary<int, HashSet<int>>();

			foreach (JObject row in WeaponPaints.SkinsList)
			{
				string? weaponName = row["weapon_name"]?.ToString();
				int? definitionIndex = row["weapon_defindex"]?.Value<int?>();
				int? paint = row["paint"]?.Value<int?>();
				if (!string.IsNullOrWhiteSpace(weaponName))
				{
					if (!skinsByWeapon.TryGetValue(weaponName, out List<JObject>? rows))
						skinsByWeapon[weaponName] = rows = [];
					rows.Add(row);
				}

				if (definitionIndex is not { } definition || paint is not { } paintId) continue;
				legacyModels[(definition, paintId)] = row.Value<bool?>("legacy_model") ?? true;
				if (paintId <= 0) continue;
				if (!paintsByDefinition.TryGetValue(definition, out HashSet<int>? paints))
					paintsByDefinition[definition] = paints = [];
				paints.Add(paintId);
			}

			var agentsByTeam = new Dictionary<int, HashSet<string>>();
			foreach (JObject row in WeaponPaints.AgentsList)
			{
				int? team = row["team"]?.Value<int?>();
				string? model = NormalizeAgentModel(row["model"]?.ToString());
				if (team is not { } teamNumber || model == null) continue;
				if (!agentsByTeam.TryGetValue(teamNumber, out HashSet<string>? models))
					agentsByTeam[teamNumber] = models = new(StringComparer.OrdinalIgnoreCase);
				models.Add(model);
			}

			WeaponPaints.SkinsByWeapon = skinsByWeapon.ToDictionary(
				entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal);
			WeaponPaints.LegacyModelBySkin = legacyModels;
			WeaponPaints.PaintsByDefinition = paintsByDefinition.ToDictionary(
				entry => entry.Key, entry => entry.Value.ToArray());
			WeaponPaints.AgentModelsByTeam = agentsByTeam;
		}

		private static List<JObject> LoadList(string path, ILogger logger)
		{
			try
			{
				return JsonConvert.DeserializeObject<List<JObject>>(File.ReadAllText(path)) ?? [];
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
			{
				logger.LogError(exception, "[WeaponPaints] Failed to load catalog file {CatalogFile}.", path);
				return [];
			}
		}

		internal static IMenu? CreateMenu(string title)
		{
			string menuType = WeaponPaints.Instance.Config.MenuType?.Trim() ?? "selectable";
        
			var menu = menuType switch
			{
				_ when menuType.Equals("selectable", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.NewMenu(title),

				_ when menuType.Equals("dynamic", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.ButtonMenu),

				_ when menuType.Equals("center", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.CenterMenu),

				_ when menuType.Equals("chat", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.ChatMenu),

				_ when menuType.Equals("console", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.ConsoleMenu),

				_ => WeaponPaints.MenuApi?.NewMenu(title)
			};

			return menu;
		}

	}
}

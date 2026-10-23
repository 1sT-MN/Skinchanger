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
			if (safeLanguage.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
			{
				logger.LogWarning("[WeaponPaints] Invalid catalog language {Language}; using English.", language);
				safeLanguage = "en";
			}
			WeaponPaints.SkinsList = LoadList(moduleDirectory, "skins", safeLanguage, logger);
			WeaponPaints.GlovesList = LoadList(moduleDirectory, "gloves", safeLanguage, logger);
			WeaponPaints.AgentsList = LoadList(moduleDirectory, "agents", safeLanguage, logger);
			WeaponPaints.MusicList = LoadList(moduleDirectory, "music", safeLanguage, logger);
			WeaponPaints.PinsList = LoadList(moduleDirectory, "collectibles", safeLanguage, logger);
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

		private static List<JObject> LoadList(string moduleDirectory, string catalog, string language, ILogger logger)
		{
			string path = Path.Combine(moduleDirectory, "data", $"{catalog}_{language}.json");
			if (TryLoadList(path, logger) is { } localized) return localized;
			if (!language.Equals("en", StringComparison.OrdinalIgnoreCase))
			{
				logger.LogWarning("[WeaponPaints] Catalog {Catalog} for language {Language} is unavailable; using English.", catalog, language);
				string englishPath = Path.Combine(moduleDirectory, "data", $"{catalog}_en.json");
				if (TryLoadList(englishPath, logger) is { } english) return english;
			}

			using Stream stream = typeof(Utility).Assembly.GetManifestResourceStream($"WeaponPaints.Catalogs.{catalog}_en.json")
				?? throw new InvalidOperationException($"Bundled catalog {catalog}_en.json is missing from WeaponPaints.dll.");
			using var reader = new StreamReader(stream);
			var rows = JsonConvert.DeserializeObject<List<JObject>>(reader.ReadToEnd())
				?? throw new InvalidOperationException($"Bundled catalog {catalog}_en.json is empty.");
			logger.LogInformation("[WeaponPaints] Loaded bundled English catalog {Catalog} ({Count} entries).", catalog, rows.Count);
			return rows;
		}

		private static List<JObject>? TryLoadList(string path, ILogger logger)
		{
			if (!File.Exists(path)) return null;
			try
			{
				return JsonConvert.DeserializeObject<List<JObject>>(File.ReadAllText(path))
					?? throw new JsonException("Catalog must contain a JSON array.");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
			{
				logger.LogWarning(exception, "[WeaponPaints] Failed to load catalog file {CatalogFile}; trying the English fallback.", path);
				return null;
			}
		}

		internal static IMenu? CreateMenu(string title)
		{
			string menuType = WeaponPaints.Instance.Config.MenuType?.Trim() ?? "selectable";
			if (WeaponPaints.MenuApi == null)
				return menuType.Equals("console", StringComparison.OrdinalIgnoreCase)
					? new ConsoleMenu(title)
					: new ChatMenu(title);
        
			var menu = menuType switch
			{
				_ when menuType.Equals("selectable", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.GetMenu(title),

				_ when menuType.Equals("dynamic", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.GetMenuForcetype(title, MenuType.ButtonMenu),

				_ when menuType.Equals("center", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.GetMenuForcetype(title, MenuType.CenterMenu),

				_ when menuType.Equals("chat", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.GetMenuForcetype(title, MenuType.ChatMenu),

				_ when menuType.Equals("console", StringComparison.OrdinalIgnoreCase) =>
					WeaponPaints.MenuApi?.GetMenuForcetype(title, MenuType.ConsoleMenu),

				_ => WeaponPaints.MenuApi?.GetMenu(title)
			};

			return menu;
		}

	}
}

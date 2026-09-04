using System.Globalization;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

internal sealed class WeaponSynchronization(
	Database database,
	WeaponPaintsConfig config,
	PlayerPaintCache cache,
	Task databaseReady,
	ILogger logger)
{
	private const string SaveWeaponSql = """
		INSERT INTO `wp_player_skins`
		(`steamid`, `weapon_team`, `weapon_defindex`, `weapon_paint_id`, `weapon_wear`, `weapon_seed`,
		 `weapon_nametag`, `weapon_stattrak`, `weapon_stattrak_count`, `weapon_sticker_0`, `weapon_sticker_1`,
		 `weapon_sticker_2`, `weapon_sticker_3`, `weapon_sticker_4`, `weapon_keychain`)
		VALUES
		(@steamid, @team, @definitionIndex, @paint, @wear, @seed, @nametag, @statTrak, @statTrakCount,
		 @sticker0, @sticker1, @sticker2, @sticker3, @sticker4, @keyChain)
		ON DUPLICATE KEY UPDATE
		 `weapon_paint_id` = VALUES(`weapon_paint_id`), `weapon_wear` = VALUES(`weapon_wear`),
		 `weapon_seed` = VALUES(`weapon_seed`), `weapon_nametag` = VALUES(`weapon_nametag`),
		 `weapon_stattrak` = VALUES(`weapon_stattrak`), `weapon_stattrak_count` = VALUES(`weapon_stattrak_count`),
		 `weapon_sticker_0` = VALUES(`weapon_sticker_0`), `weapon_sticker_1` = VALUES(`weapon_sticker_1`),
		 `weapon_sticker_2` = VALUES(`weapon_sticker_2`), `weapon_sticker_3` = VALUES(`weapon_sticker_3`),
		 `weapon_sticker_4` = VALUES(`weapon_sticker_4`), `weapon_keychain` = VALUES(`weapon_keychain`)
		""";

	private readonly SemaphoreSlim _schemaGate = new(1, 1);
	private readonly SemaphoreSlim _loadGate = new(Math.Clamp((int)config.DatabaseMaximumPoolSize / 2, 1, 8));
	private readonly FailureLogLimiter _loadFailureLogs = new(TimeSpan.FromSeconds(10));
	private readonly FailureLogLimiter _saveFailureLogs = new(TimeSpan.FromSeconds(10));
	private readonly FailureLogLimiter _schemaFailureLogs = new(TimeSpan.FromSeconds(10));
	private volatile bool _schemaReady;
	private sealed class TeamValueRow
	{
		public int WeaponTeam { get; init; }
		public string Knife { get; init; } = "";
		public int WeaponDefindex { get; init; }
		public int MusicId { get; init; }
		public int Id { get; init; }
	}

	private sealed class AgentRow
	{
		public string? AgentCt { get; init; }
		public string? AgentT { get; init; }
	}

	private sealed class SkinRow
	{
		public int WeaponTeam { get; init; }
		public int WeaponDefindex { get; init; }
		public int WeaponPaintId { get; init; }
		public float WeaponWear { get; init; }
		public int WeaponSeed { get; init; }
		public string? WeaponNametag { get; init; }
		public bool WeaponStattrak { get; init; }
		public int WeaponStattrakCount { get; init; }
		public string? WeaponSticker0 { get; init; }
		public string? WeaponSticker1 { get; init; }
		public string? WeaponSticker2 { get; init; }
		public string? WeaponSticker3 { get; init; }
		public string? WeaponSticker4 { get; init; }
		public string? WeaponKeychain { get; init; }

		public IEnumerable<string?> Stickers()
		{
			yield return WeaponSticker0;
			yield return WeaponSticker1;
			yield return WeaponSticker2;
			yield return WeaponSticker3;
			yield return WeaponSticker4;
		}
	}

	internal async Task<bool> LoadPlayerDataAsync(PlayerLoadSession session)
	{
		bool enteredLoadGate = false;
		try
		{
			await _loadGate.WaitAsync(database.StoppingToken).ConfigureAwait(false);
			enteredLoadGate = true;
			await EnsureDatabaseReadyAsync().ConfigureAwait(false);
			if (!cache.IsCurrent(session)) return false;

			await using var connection = await database.GetConnectionAsync().ConfigureAwait(false);
			string steamId = SteamId(session.SteamId64);
			var state = new PlayerPaintState(session.SteamId64);

			const string sql = """
				SELECT `knife` AS Knife, `weapon_team` AS WeaponTeam
				FROM `wp_player_knife` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC;
				SELECT `weapon_defindex` AS WeaponDefindex, `weapon_team` AS WeaponTeam
				FROM `wp_player_gloves` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC;
				SELECT `agent_ct` AS AgentCt, `agent_t` AS AgentT
				FROM `wp_player_agents` WHERE `steamid` = @steamid;
				SELECT `music_id` AS MusicId, `weapon_team` AS WeaponTeam
				FROM `wp_player_music` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC;
				SELECT `id` AS Id, `weapon_team` AS WeaponTeam
				FROM `wp_player_pins` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC;
				SELECT `weapon_team` AS WeaponTeam, `weapon_defindex` AS WeaponDefindex,
				       `weapon_paint_id` AS WeaponPaintId, `weapon_wear` AS WeaponWear,
				       `weapon_seed` AS WeaponSeed, `weapon_nametag` AS WeaponNametag,
				       `weapon_stattrak` AS WeaponStattrak, `weapon_stattrak_count` AS WeaponStattrakCount,
				       `weapon_sticker_0` AS WeaponSticker0, `weapon_sticker_1` AS WeaponSticker1,
				       `weapon_sticker_2` AS WeaponSticker2, `weapon_sticker_3` AS WeaponSticker3,
				       `weapon_sticker_4` AS WeaponSticker4, `weapon_keychain` AS WeaponKeychain
				FROM `wp_player_skins` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC
				""";
			var command = new CommandDefinition(sql, new { steamid = steamId },
				cancellationToken: database.StoppingToken);
			using var results = await connection.QueryMultipleAsync(command).ConfigureAwait(false);
			var knives = await results.ReadAsync<TeamValueRow>().ConfigureAwait(false);
			var gloves = await results.ReadAsync<TeamValueRow>().ConfigureAwait(false);
			AgentRow? agent = (await results.ReadAsync<AgentRow>().ConfigureAwait(false)).FirstOrDefault();
			var musicKits = await results.ReadAsync<TeamValueRow>().ConfigureAwait(false);
			var pins = await results.ReadAsync<TeamValueRow>().ConfigureAwait(false);
			var skins = await results.ReadAsync<SkinRow>().ConfigureAwait(false);
			if (!cache.IsCurrent(session)) return false;

			if (config.Additional.KnifeEnabled)
				foreach (TeamValueRow row in knives)
					if (IsValidStorageTeam(row.WeaponTeam) && !string.IsNullOrWhiteSpace(row.Knife))
						SetForTeam(state.Knives, row.WeaponTeam, row.Knife);

			if (config.Additional.GloveEnabled)
				foreach (TeamValueRow row in gloves)
					if (IsValidStorageTeam(row.WeaponTeam) && row.WeaponDefindex is >= 0 and <= ushort.MaxValue)
						SetForTeam(state.Gloves, row.WeaponTeam, (ushort)row.WeaponDefindex);

			if (config.Additional.AgentEnabled)
			{
				state.CtAgent = Utility.NormalizeAgentModel(agent?.AgentCt);
				state.TAgent = Utility.NormalizeAgentModel(agent?.AgentT);
			}

			if (config.Additional.MusicEnabled)
				foreach (TeamValueRow row in musicKits)
					if (IsValidStorageTeam(row.WeaponTeam) && row.MusicId is >= 0 and <= ushort.MaxValue)
						SetForTeam(state.MusicKits, row.WeaponTeam, (ushort)row.MusicId);

			if (config.Additional.PinsEnabled)
				foreach (TeamValueRow row in pins)
					if (IsValidStorageTeam(row.WeaponTeam) && row.Id is >= 0 and <= ushort.MaxValue)
						SetForTeam(state.Pins, row.WeaponTeam, (ushort)row.Id);

			if (config.Additional.SkinEnabled || config.Additional.GloveEnabled)
				PopulateWeapons(skins, steamId, state);

			if (!cache.Publish(session, state)) return false;
			logger.LogDebug("[WeaponPaints] Loaded cosmetics for SteamID64 {SteamId}.", steamId);
			return true;
		}
		catch (OperationCanceledException) when (database.IsStopping) { return false; }
		catch (Exception exception)
		{
			if (_loadFailureLogs.ShouldLog(out int suppressed))
				logger.LogWarning(exception,
					"[WeaponPaints] Failed to load cosmetics for SteamID64 {SteamId}. Suppressed {SuppressedCount} similar failures.",
					session.SteamId64, suppressed);
			return false;
		}
		finally
		{
			if (enteredLoadGate) _loadGate.Release();
		}
	}

	private void PopulateWeapons(IEnumerable<SkinRow> rows, string steamId, PlayerPaintState state)
	{
		int invalidRows = 0;
		foreach (SkinRow row in rows)
		{
			if (row.WeaponDefindex <= 0 || !IsValidStorageTeam(row.WeaponTeam))
			{
				invalidRows++;
				continue;
			}

			var weapon = new WeaponInfo
			{
				Paint = Math.Max(0, row.WeaponPaintId), Seed = Math.Max(0, row.WeaponSeed),
				Wear = NormalizeWear(row.WeaponWear), Nametag = row.WeaponNametag ?? "",
				StatTrak = row.WeaponStattrak, StatTrakCount = Math.Max(0, row.WeaponStattrakCount),
				KeyChain = ParseKeyChain(row.WeaponKeychain), StorageTeam = row.WeaponTeam
			};
			foreach (string? serialized in row.Stickers())
				if (ParseSticker(serialized) is { } sticker) weapon.Stickers.Add(sticker);
			SetWeaponForTeam(state, row.WeaponTeam, row.WeaponDefindex, weapon);
		}
		if (invalidRows > 0)
			logger.LogWarning("[WeaponPaints] Ignored {InvalidRowCount} invalid skin rows for SteamID64 {SteamId}.",
				invalidRows, steamId);
	}

	internal Task SaveKnifeAsync(ulong steamId64, string knife, IReadOnlyList<CsTeam> teams) =>
		ExecuteSafeAsync(steamId64, async connection =>
		{
			const string sql = "INSERT INTO `wp_player_knife` (`steamid`, `weapon_team`, `knife`) VALUES (@steamid, @team, @knife) ON DUPLICATE KEY UPDATE `knife` = VALUES(`knife`)";
			foreach (CsTeam team in teams)
				await connection.ExecuteAsync(sql, new { steamid = SteamId(steamId64), team = (int)team, knife }).ConfigureAwait(false);
		}, "knife");

	internal Task SaveGloveSelectionAsync(
		ulong steamId64,
		ushort glove,
		IReadOnlyList<(CsTeam Team, WeaponInfo? Weapon)> selections)
	{
		var snapshot = selections
			.Select(selection => (selection.Team, Weapon: selection.Weapon?.Clone()))
			.ToArray();
		return ExecuteSafeAsync(steamId64, async connection =>
		{
			await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
			foreach (var selection in snapshot)
				if (selection.Weapon != null)
					await connection.ExecuteAsync(SaveWeaponSql,
						WeaponParameters(steamId64, selection.Team, glove, selection.Weapon), transaction)
						.ConfigureAwait(false);

			const string gloveSql = "INSERT INTO `wp_player_gloves` (`steamid`, `weapon_team`, `weapon_defindex`) VALUES (@steamid, @team, @glove) ON DUPLICATE KEY UPDATE `weapon_defindex` = VALUES(`weapon_defindex`)";
			foreach (var selection in snapshot)
				await connection.ExecuteAsync(gloveSql,
					new { steamid = SteamId(steamId64), team = (int)selection.Team, glove }, transaction)
					.ConfigureAwait(false);
			await transaction.CommitAsync().ConfigureAwait(false);
		}, "glove selection");
	}

	internal Task SaveAgentAsync(ulong steamId64, string? ctAgent, string? tAgent) =>
		ExecuteSafeAsync(steamId64, connection => connection.ExecuteAsync("""
			INSERT INTO `wp_player_agents` (`steamid`, `agent_ct`, `agent_t`)
			VALUES (@steamid, @ctAgent, @tAgent)
			ON DUPLICATE KEY UPDATE `agent_ct` = VALUES(`agent_ct`), `agent_t` = VALUES(`agent_t`)
			""", new { steamid = SteamId(steamId64), ctAgent, tAgent }), "agent");

	internal Task SaveMusicAsync(ulong steamId64, ushort music, IReadOnlyList<CsTeam> teams) =>
		SaveTeamValueAsync(steamId64, teams, "wp_player_music", "music_id", music, "music kit");

	internal Task SavePinAsync(ulong steamId64, ushort pin, IReadOnlyList<CsTeam> teams) =>
		SaveTeamValueAsync(steamId64, teams, "wp_player_pins", "id", pin, "pin");

	internal Task SaveWeaponAsync(ulong steamId64, CsTeam team, int definitionIndex, WeaponInfo source)
	{
		WeaponInfo weapon = source.Clone();
		return ExecuteSafeAsync(steamId64, connection => connection.ExecuteAsync(
			SaveWeaponSql, WeaponParameters(steamId64, team, definitionIndex, weapon)), "weapon skin");
	}

	internal Task SaveWeaponSelectionsAsync(
		ulong steamId64,
		int definitionIndex,
		IReadOnlyList<(CsTeam Team, WeaponInfo Weapon)> selections)
	{
		var snapshots = selections
			.Select(selection => (selection.Team, Weapon: selection.Weapon.Clone()))
			.ToArray();
		return ExecuteSafeAsync(steamId64, async connection =>
		{
			if (snapshots.Length == 1)
			{
				await connection.ExecuteAsync(SaveWeaponSql,
					WeaponParameters(steamId64, snapshots[0].Team, definitionIndex, snapshots[0].Weapon))
					.ConfigureAwait(false);
				return;
			}

			await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
			foreach (var selection in snapshots)
				await connection.ExecuteAsync(SaveWeaponSql,
					WeaponParameters(steamId64, selection.Team, definitionIndex, selection.Weapon), transaction)
					.ConfigureAwait(false);
			await transaction.CommitAsync().ConfigureAwait(false);
		}, "weapon skin selection");
	}

	private static object WeaponParameters(ulong steamId64, CsTeam team, int definitionIndex, WeaponInfo weapon)
	{
		string[] stickers = Enumerable.Range(0, 5)
			.Select(index => index < weapon.Stickers.Count ? SerializeSticker(weapon.Stickers[index]) : "0;0;0;0;0;0;0")
			.ToArray();
		return new
			{
				steamid = SteamId(steamId64), team = (int)team, definitionIndex,
				paint = weapon.Paint, wear = NormalizeWear(weapon.Wear), seed = weapon.Seed,
				nametag = weapon.Nametag, statTrak = weapon.StatTrak, statTrakCount = weapon.StatTrakCount,
				sticker0 = stickers[0], sticker1 = stickers[1], sticker2 = stickers[2],
				sticker3 = stickers[3], sticker4 = stickers[4], keyChain = SerializeKeyChain(weapon.KeyChain)
			};
	}

	internal Task SaveStatTrakAsync(ulong steamId64, IReadOnlyList<(int Team, int DefinitionIndex, bool Enabled, int Count)> rows) =>
		ExecuteSafeAsync(steamId64, async connection =>
		{
			const string sql = "UPDATE `wp_player_skins` SET `weapon_stattrak` = @enabled, `weapon_stattrak_count` = @count WHERE `steamid` = @steamid AND `weapon_team` = @team AND `weapon_defindex` = @definitionIndex";
			await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
			foreach (var row in rows)
				await connection.ExecuteAsync(sql, new { steamid = SteamId(steamId64), team = row.Team, row.DefinitionIndex, enabled = row.Enabled, count = row.Count }, transaction).ConfigureAwait(false);
			await transaction.CommitAsync().ConfigureAwait(false);
		}, "StatTrak");

	private async Task SaveTeamValueAsync(ulong steamId64, IReadOnlyList<CsTeam> teams, string table, string column, ushort value, string operation)
	{
		if (!((table == "wp_player_music" && column == "music_id") || (table == "wp_player_pins" && column == "id")))
			throw new ArgumentOutOfRangeException(nameof(table));
		string sql = $"INSERT INTO `{table}` (`steamid`, `weapon_team`, `{column}`) VALUES (@steamid, @team, @value) ON DUPLICATE KEY UPDATE `{column}` = VALUES(`{column}`)";
		await ExecuteSafeAsync(steamId64, async connection =>
		{
			foreach (CsTeam team in teams)
				await connection.ExecuteAsync(sql, new { steamid = SteamId(steamId64), team = (int)team, value }).ConfigureAwait(false);
		}, operation).ConfigureAwait(false);
	}

	private async Task ExecuteSafeAsync(ulong steamId64, Func<MySqlConnection, Task> operation, string operationName)
	{
		try
		{
			await EnsureDatabaseReadyAsync().ConfigureAwait(false);
			await using var connection = await database.GetConnectionAsync().ConfigureAwait(false);
			await operation(connection).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (database.IsStopping) { }
		catch (Exception exception)
		{
			if (_saveFailureLogs.ShouldLog(out int suppressed))
				logger.LogWarning(exception,
					"[WeaponPaints] Failed to save {Operation} for SteamID64 {SteamId}. Suppressed {SuppressedCount} similar failures.",
					operationName, steamId64, suppressed);
		}
	}

	private async Task EnsureDatabaseReadyAsync()
	{
		if (_schemaReady) return;
		await _schemaGate.WaitAsync(database.StoppingToken).ConfigureAwait(false);
		try
		{
			if (_schemaReady) return;
				try { await databaseReady.ConfigureAwait(false); }
				catch (Exception exception)
				{
					if (_schemaFailureLogs.ShouldLog(out int suppressed))
						logger.LogWarning(exception,
							"[WeaponPaints] Initial schema setup did not complete; retrying before database access. Suppressed {SuppressedCount} similar warnings.",
							suppressed);
					await DatabaseSchema.EnsureAsync(database, logger).ConfigureAwait(false);
				}
				_schemaReady = true;
		}
		finally { _schemaGate.Release(); }
	}

	private static void SetForTeam<T>(Dictionary<CsTeam, T> values, int teamNumber, T value)
	{
		CsTeam team = ParseTeam(teamNumber);
		if (team == CsTeam.None)
		{
			values[CsTeam.Terrorist] = value;
			values[CsTeam.CounterTerrorist] = value;
		}
		else values[team] = value;
	}

	private static void SetWeaponForTeam(PlayerPaintState state, int teamNumber, int definitionIndex, WeaponInfo weapon)
	{
		CsTeam team = ParseTeam(teamNumber);
		if (team == CsTeam.None)
		{
			state.GetOrCreateWeapons(CsTeam.Terrorist)[definitionIndex] = weapon;
			state.GetOrCreateWeapons(CsTeam.CounterTerrorist)[definitionIndex] = weapon;
		}
		else state.GetOrCreateWeapons(team)[definitionIndex] = weapon;
	}

	private static CsTeam ParseTeam(int value) => value switch
	{
		2 => CsTeam.Terrorist, 3 => CsTeam.CounterTerrorist, _ => CsTeam.None
	};
	private static bool IsValidStorageTeam(int value) => value is 0 or 2 or 3;

	private static float NormalizeWear(float value) =>
		float.IsFinite(value) ? Math.Clamp(value, 0.000001f, 1f) : 0.000001f;


	private static KeyChainInfo? ParseKeyChain(string? value)
	{
		string[] parts = (value ?? "").Split(';');
		if (parts.Length != 5 || !uint.TryParse(parts[0], out uint id) || id == 0
		    || !TryFloat(parts[1], out float x) || !TryFloat(parts[2], out float y)
		    || !TryFloat(parts[3], out float z) || !uint.TryParse(parts[4], out uint seed)) return null;
		return new KeyChainInfo { Id = id, OffsetX = x, OffsetY = y, OffsetZ = z, Seed = seed };
	}

	private static StickerInfo? ParseSticker(string? value)
	{
		string[] parts = (value ?? "").Split(';');
		if (parts.Length != 7 || !uint.TryParse(parts[0], out uint id) || id == 0
		    || !uint.TryParse(parts[1], out uint schema) || !TryFloat(parts[2], out float x)
		    || !TryFloat(parts[3], out float y) || !TryFloat(parts[4], out float wear)
		    || !TryFloat(parts[5], out float scale) || !TryFloat(parts[6], out float rotation)) return null;
		return new StickerInfo { Id = id, Schema = schema, OffsetX = x, OffsetY = y, Wear = wear, Scale = scale, Rotation = rotation };
	}

	private static bool TryFloat(string value, out float result) =>
		float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && float.IsFinite(result);
	private static string SerializeSticker(StickerInfo value) => string.Join(';', value.Id, value.Schema,
		F(value.OffsetX), F(value.OffsetY), F(value.Wear), F(value.Scale), F(value.Rotation));
	private static string SerializeKeyChain(KeyChainInfo? value) => value is null
		? "0;0;0;0;0" : string.Join(';', value.Id, F(value.OffsetX), F(value.OffsetY), F(value.OffsetZ), value.Seed);
	private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
	private static string SteamId(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}

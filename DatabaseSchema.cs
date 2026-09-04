using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

internal static class DatabaseSchema
{
	private static readonly string[] CreateStatements =
	[
		"""
		CREATE TABLE IF NOT EXISTS `wp_player_skins` (
		    `steamid` varchar(18) NOT NULL, `weapon_team` int(1) NOT NULL,
		    `weapon_defindex` int(6) NOT NULL, `weapon_paint_id` int(6) NOT NULL,
		    `weapon_wear` float NOT NULL DEFAULT 0.000001, `weapon_seed` int(16) NOT NULL DEFAULT 0,
		    `weapon_nametag` varchar(128) DEFAULT NULL,
		    `weapon_stattrak` tinyint(1) NOT NULL DEFAULT 0, `weapon_stattrak_count` int(10) NOT NULL DEFAULT 0,
		    `weapon_sticker_0` varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0',
		    `weapon_sticker_1` varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0',
		    `weapon_sticker_2` varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0',
		    `weapon_sticker_3` varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0',
		    `weapon_sticker_4` varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0',
		    `weapon_keychain` varchar(128) NOT NULL DEFAULT '0;0;0;0;0',
		    UNIQUE KEY `uq_wp_player_skins` (`steamid`, `weapon_team`, `weapon_defindex`)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci
		""",
		"""
		CREATE TABLE IF NOT EXISTS `wp_player_knife` (
		    `steamid` varchar(18) NOT NULL, `weapon_team` int(1) NOT NULL, `knife` varchar(64) NOT NULL,
		    UNIQUE KEY `uq_wp_player_knife` (`steamid`, `weapon_team`)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci
		""",
		"""
		CREATE TABLE IF NOT EXISTS `wp_player_gloves` (
		    `steamid` varchar(18) NOT NULL, `weapon_team` int(1) NOT NULL, `weapon_defindex` int(11) NOT NULL,
		    UNIQUE KEY `uq_wp_player_gloves` (`steamid`, `weapon_team`)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci
		""",
		"""
		CREATE TABLE IF NOT EXISTS `wp_player_agents` (
		    `steamid` varchar(18) NOT NULL, `agent_ct` varchar(64) DEFAULT NULL, `agent_t` varchar(64) DEFAULT NULL,
		    UNIQUE KEY `uq_wp_player_agents` (`steamid`)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci
		""",
		"""
		CREATE TABLE IF NOT EXISTS `wp_player_music` (
		    `steamid` varchar(64) NOT NULL, `weapon_team` int(1) NOT NULL, `music_id` int(11) NOT NULL,
		    UNIQUE KEY `uq_wp_player_music` (`steamid`, `weapon_team`)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci
		""",
		"""
		CREATE TABLE IF NOT EXISTS `wp_player_pins` (
		    `steamid` varchar(64) NOT NULL, `weapon_team` int(1) NOT NULL, `id` int(11) NOT NULL,
		    UNIQUE KEY `uq_wp_player_pins` (`steamid`, `weapon_team`)
		) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci
		"""
	];

	private static readonly (string Name, string Definition)[] SkinColumns =
	[
		("weapon_nametag", "varchar(128) DEFAULT NULL"),
		("weapon_stattrak", "tinyint(1) NOT NULL DEFAULT 0"),
		("weapon_stattrak_count", "int(10) NOT NULL DEFAULT 0"),
		("weapon_sticker_0", "varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0'"),
		("weapon_sticker_1", "varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0'"),
		("weapon_sticker_2", "varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0'"),
		("weapon_sticker_3", "varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0'"),
		("weapon_sticker_4", "varchar(128) NOT NULL DEFAULT '0;0;0;0;0;0;0'"),
		("weapon_keychain", "varchar(128) NOT NULL DEFAULT '0;0;0;0;0'")
	];

	internal static async Task EnsureAsync(Database database, ILogger logger)
	{
		await using var connection = await database.GetConnectionAsync().ConfigureAwait(false);
		foreach (string statement in CreateStatements)
			await connection.ExecuteAsync(new CommandDefinition(statement,
				cancellationToken: database.StoppingToken)).ConfigureAwait(false);

		const string columnsSql = """
		SELECT `COLUMN_NAME` FROM `information_schema`.`COLUMNS`
		WHERE `TABLE_SCHEMA` = DATABASE() AND `TABLE_NAME` = @table
		""";
		var columnsCommand = new CommandDefinition(columnsSql, new { table = "wp_player_skins" },
			cancellationToken: database.StoppingToken);
		var columns = (await connection.QueryAsync<string>(columnsCommand).ConfigureAwait(false))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		foreach (var column in SkinColumns)
		{
			if (columns.Contains(column.Name)) continue;
			// Column names and definitions come only from the fixed whitelist above.
			try
			{
				var command = new CommandDefinition(
					$"ALTER TABLE `wp_player_skins` ADD COLUMN `{column.Name}` {column.Definition}",
					cancellationToken: database.StoppingToken);
				await connection.ExecuteAsync(command).ConfigureAwait(false);
				logger.LogInformation("[WeaponPaints] Added backward-compatible column wp_player_skins.{Column}.", column.Name);
			}
			catch (MySqlException exception) when (exception.Number == 1060)
			{
				logger.LogDebug("[WeaponPaints] Column wp_player_skins.{Column} was added concurrently.", column.Name);
			}
		}

		logger.LogInformation("[WeaponPaints] Database schema is ready.");
	}
}

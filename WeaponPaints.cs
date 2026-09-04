using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

[MinimumApiVersion(373)]
public partial class WeaponPaints : BasePlugin, IPluginConfig<WeaponPaintsConfig>
{
	internal static WeaponPaints Instance { get; private set; } = null!;
	private bool _configured;

	public WeaponPaintsConfig Config { get; set; } = new();
	private static WeaponPaintsConfig _config { get; set; } = new();
	public override string ModuleAuthor => "Snaximusss+";
	public override string ModuleDescription => "I NEED GF";
	public override string ModuleName => "1sT-Skinchanger";
	public override string ModuleVersion => "0.0.1";

	public void OnConfigParsed(WeaponPaintsConfig config)
	{
		Config = config;
		_config = config;

		if (config.DatabaseHost.Length < 1 || config.DatabaseName.Length < 1 || config.DatabaseUser.Length < 1)
		{
			Logger.LogError("[WeaponPaints] Configure DatabaseHost, DatabaseName and DatabaseUser before loading.");
			return;
		}
		if (config.DatabasePort is < 1 or > 65_535 || config.DatabaseConnectionTimeoutSeconds == 0
		    || config.DatabaseCommandTimeoutSeconds == 0 || config.DatabaseMaximumPoolSize == 0
		    || config.DatabaseOpenAttempts is < 1 or > 10)
		{
			Logger.LogError("[WeaponPaints] Database port, timeouts, pool size, or retry count is invalid.");
			return;
		}

		string gameData = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(ModuleDirectory))!, "gamedata", "weaponpaints.json");
		if (!File.Exists(gameData))
		{
			Logger.LogError("[WeaponPaints] Copy gamedata/weaponpaints.json to the CounterStrikeSharp gamedata directory.");
			return;
		}

		var builder = new MySqlConnectionStringBuilder
		{
			Server = config.DatabaseHost,
			UserID = config.DatabaseUser,
			Password = config.DatabasePassword,
			Database = config.DatabaseName,
			Port = (uint)config.DatabasePort,
			Pooling = true,
			ConnectionTimeout = config.DatabaseConnectionTimeoutSeconds,
			DefaultCommandTimeout = config.DatabaseCommandTimeoutSeconds,
			MaximumPoolSize = config.DatabaseMaximumPoolSize,
			SslMode = MySqlSslMode.Preferred
		};

		Database = new Database(builder.ConnectionString, Logger, _lifetime.Token, config.DatabaseOpenAttempts);
		_databaseReady = DatabaseSchema.EnsureAsync(Database, Logger);
		WeaponSync = new WeaponSynchronization(Database, Config, PlayerPaints, _databaseReady, Logger);
		_ = ObserveDatabaseStartupAsync();
		_localizer = Localizer;
		_configured = true;
	}

	public override void Load(bool hotReload)
	{
		Instance = this;
		if (!_configured)
		{
			Logger.LogError("[WeaponPaints] Plugin disabled because configuration validation failed.");
			return;
		}

		Utility.LoadCatalogFiles(ModuleDirectory, _config.SkinsLanguage, Logger);
		CAttributeListSetOrAddAttributeValueByName = new(
			GameData.GetSignature("CAttributeList_SetOrAddAttributeValueByName"));
		RegisterListeners();
		Logger.LogInformation("[WeaponPaints] {Version} started (hot reload: {HotReload}).", ModuleVersion, hotReload);

		if (hotReload)
		{
			ResetMapState();
			Server.NextFrame(LoadConnectedPlayers);
		}
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		if (!_configured) return;
		RegisterCommands();
		try
		{
			MenuApi = MenuCapability.Get();
			if (Config.Additional.KnifeEnabled) SetupKnifeMenu();
			if (Config.Additional.SkinEnabled) SetupSkinsMenu();
			if (Config.Additional.GloveEnabled) SetupGlovesMenu();
			if (Config.Additional.AgentEnabled) SetupAgentsMenu();
			if (Config.Additional.MusicEnabled) SetupMusicMenu();
			if (Config.Additional.PinsEnabled) SetupPinsMenu();
		}
		catch (Exception exception)
		{
			MenuApi = null;
			Logger.LogError(exception, "[WeaponPaints] MenuManager is unavailable; non-menu commands remain registered.");
		}
	}

	public override void Unload(bool hotReload)
	{
		_lifetime.Cancel();
		if (!_configured) return;
		try { VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPost, HookMode.Post); }
		catch (Exception exception) { Logger.LogDebug(exception, "[WeaponPaints] GiveNamedItem hook was already removed."); }

		RemoveListener<Listeners.OnMapStart>(OnMapStart);
		RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
		RemoveListener<Listeners.OnEntitySpawned>(OnEntityCreated);
		if (Config.Additional.ShowSkinImage) RemoveListener<Listeners.OnTick>(OnTick);
		DeregisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
		DeregisterEventHandler<EventPlayerConnectFull>(OnClientFullConnect);
		DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
		DeregisterEventHandler<EventRoundStart>(OnRoundStart);
		DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
		DeregisterEventHandler<EventRoundMvp>(OnRoundMvp);
		DeregisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
		DeregisterEventHandler<EventItemPickup>(OnItemPickup);

		PlayerPaints.Clear();
		CommandsCooldown.Clear();
		_playerWeaponImage.Clear();
		_skinMenus.Clear();
		_agentMenus.Clear();
		_appliedWeaponSelections.Clear();
		_refreshesInProgress.Clear();
		WeaponSync = null;
		Database = null;
		MenuApi = null;
		Logger.LogInformation("[WeaponPaints] Plugin stopped (hot reload: {HotReload}).", hotReload);
	}

	private async Task ObserveDatabaseStartupAsync()
	{
		try { await _databaseReady.ConfigureAwait(false); }
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception exception)
		{
			Logger.LogError(exception, "[WeaponPaints] Database/schema initialization failed; player loads will retry through the pool.");
		}
	}
}

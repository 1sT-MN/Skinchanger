using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AnyBaseLib;
using AnyBaseLib.Bases;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using MenuManager;
using Microsoft.Extensions.Logging.Abstractions;
using PlayerSettings;

string library = Path.GetFullPath(args.Single());
NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly,
    (name, _, _) => name == "e_sqlite3" ? NativeLibrary.Load(library) : nint.Zero);
string temporaryDirectory = Path.Combine(Path.GetTempPath(), "weaponpaints-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
IAnyBase db = CAnyBase.Base("sqlite");
try
{
    db.Set(CommitMode.AutoCommit, Path.Combine(temporaryDirectory, "settings"));
    db.Init();
    db.Query("CREATE TABLE settings_users(id INTEGER PRIMARY KEY AUTO_INCREMENT, steam VARCHAR(255) NOT NULL)", [], true);
    db.Query("INSERT INTO settings_users(steam) VALUES ('76561198154367261')", [], true);
    var rows = db.Query("SELECT steam FROM settings_users");
    if (rows.Count != 1 || rows[0][0] != "76561198154367261")
        throw new InvalidOperationException("Settings SQLite write/read failed.");
    db.Close();
    db.Set(CommitMode.AutoCommit, Path.Combine(temporaryDirectory, "settings"));
    db.Init();
    if (db.Query("SELECT steam FROM settings_users").Count != 1)
        throw new InvalidOperationException("Settings must persist across reopening the database.");
    Console.WriteLine("PASS: AnyBaseLib SQLite driver initializes and persists settings using the compatible native library.");
}
finally
{
    db.Close();
    Directory.Delete(temporaryDirectory, recursive: true);
}

// Configuration/loading uses native CS2 APIs; this callback only needs a logger.
var menuManager = (MenuManagerCore)RuntimeHelpers.GetUninitializedObject(typeof(MenuManagerCore));
typeof(BasePlugin).GetProperty("Logger")!.SetValue(menuManager, NullLogger.Instance);
typeof(MenuManagerCore).GetField("_settingsCapability", BindingFlags.Instance | BindingFlags.NonPublic)!
    .SetValue(menuManager, new PluginCapability<ISettingsApi?>("settings:nfcore"));
menuManager.OnAllPluginsLoaded(false);
Type misc = typeof(MenuManagerCore).Assembly.GetType("MenuManager.Misc", true)!;
object? menuType = misc.GetMethod("GetCurrentPlayerMenu", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [null]);
if (menuType is not MenuType.ButtonMenu)
    throw new InvalidOperationException("Missing PlayerSettings must use the configured default menu.");
Console.WriteLine("PASS: MenuManager starts and resolves a default menu without settings:nfcore.");

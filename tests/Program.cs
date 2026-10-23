using System.Reflection;
using System.Runtime.CompilerServices;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Menu;
using MenuManager;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Plugin = WeaponPaints.WeaponPaints;

const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
DeferredWeaponChecks.Run();
Type utility = typeof(Plugin).Assembly.GetType("WeaponPaints.Utility", throwOnError: true)!;
string temporaryDirectory = Path.Combine(Path.GetTempPath(), "weaponpaints-startup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
    // Exercise the actual startup loader with no deployed data directory.
    Load("en");
    foreach (string field in new[] { "SkinsList", "GlovesList", "AgentsList", "MusicList", "PinsList" })
        Check(Rows(field).Count > 0, $"Missing data directory must load {field}");
    Check(((System.Collections.IDictionary)Field("SkinsByWeapon")).Count > 0, "Skin index must be populated");
    Check(((System.Collections.IDictionary)Field("PaintsByDefinition")).Count > 0, "Paint index must be populated");
    Check(((System.Collections.IDictionary)Field("AgentModelsByTeam")).Count > 0, "Agent index must be populated");
    int bundledSkins = Rows("SkinsList").Count;
    Load("missing-language");
    Check(Rows("SkinsList").Count == bundledSkins, "Missing language must fall back to bundled English");
    Load("../invalid");
    Check(Rows("SkinsList").Count == bundledSkins, "Invalid language must fall back to English");

    string dataDirectory = Path.Combine(temporaryDirectory, "data");
    Directory.CreateDirectory(dataDirectory);
    const string customSkin = "[{\"weapon_name\":\"weapon_ak47\",\"weapon_defindex\":7,\"paint\":180,\"paint_name\":\"Custom skin\",\"legacy_model\":false}]";
    File.WriteAllText(Path.Combine(dataDirectory, "skins_en.json"), customSkin);
    Load("en");
    Check(Rows("SkinsList").Count == 1 && Rows("SkinsList")[0].Value<string>("paint_name") == "Custom skin", "External English must override embedded data");
    Load("de");
    Check(Rows("SkinsList").Count == 1, "Missing translation must use external English first");
    File.WriteAllText(Path.Combine(dataDirectory, "skins_de.json"), customSkin.Replace("Custom skin", "Localized skin"));
    Load("de");
    Check(Rows("SkinsList")[0].Value<string>("paint_name") == "Localized skin", "External configured language must take priority");
    File.WriteAllText(Path.Combine(dataDirectory, "skins_en.json"), "invalid JSON");
    Load("en");
    Check(Rows("SkinsList").Count == bundledSkins, "Malformed external data must fall back to embedded data");
    File.WriteAllText(Path.Combine(dataDirectory, "skins_en.json"), "null");
    Load("en");
    Check(Rows("SkinsList").Count == bundledSkins, "Null external data must fall back to embedded data");
    File.WriteAllText(Path.Combine(dataDirectory, "skins_en.json"), "[]");
    Load("en");
    Check(Rows("SkinsList").Count == 0, "Explicit empty external catalog must remain an override");

    // Avoid BasePlugin's native server constructor; menu creation only needs config.
    var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
    plugin.Config = new WeaponPaints.WeaponPaintsConfig();
    typeof(Plugin).GetProperty("Instance", StaticMembers)!.SetValue(null, plugin);
    typeof(Plugin).GetField("MenuApi", StaticMembers)!.SetValue(null, null);
    foreach (string menuType in new[] { "selectable", "dynamic", "center", "chat", "unknown", " CHAT " })
    {
        plugin.Config.MenuType = menuType;
        IMenu menu = Menu();
        Check(menu is ChatMenu, $"Missing MenuManager must provide chat fallback for {menuType}");
        menu.AddMenuOption("Selection", (_, _) => { });
        Check(menu.MenuOptions.Count == 1, "Fallback menu must accept selections");
    }
    plugin.Config.MenuType = "console";
    Check(Menu() is ConsoleMenu, "Console configuration must retain console fallback");

    IMenuApi api = DispatchProxy.Create<IMenuApi, MenuApiProxy>();
    typeof(Plugin).GetField("MenuApi", StaticMembers)!.SetValue(null, api);
    foreach (string menuType in new[] { "selectable", "dynamic", "center", "chat", "console", "unknown" })
    {
        plugin.Config.MenuType = menuType;
        Check(Menu() is ChatMenu, "Installed MenuManager must still provide menus");
        var proxy = (MenuApiProxy)api;
        string expectedMethod = menuType is "selectable" or "unknown" ? "GetMenu" : "GetMenuForcetype";
        Check(proxy.LastMethod == expectedMethod, $"Installed MenuManager must use {expectedMethod} for {menuType}");
    }
    Console.WriteLine("PASS: missing/malformed catalogs, language and external overrides, catalog indexes, and menus with/without MenuManager.");
}
finally
{
    Directory.Delete(temporaryDirectory, recursive: true);
}

void Load(string language) => utility.GetMethod("LoadCatalogFiles", StaticMembers)!
    .Invoke(null, [temporaryDirectory, language, NullLogger.Instance]);
object Field(string name) => typeof(Plugin).GetField(name, StaticMembers)!.GetValue(null)!;
List<JObject> Rows(string name) => (List<JObject>)Field(name);
IMenu Menu() => (IMenu)utility.GetMethod("CreateMenu", StaticMembers)!.Invoke(null, ["Startup check"])!;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public class MenuApiProxy : DispatchProxy
{
    public string? LastMethod { get; private set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        LastMethod = targetMethod!.Name;
        return new ChatMenu((string)args![0]!);
    }
}

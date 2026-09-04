using CounterStrikeSharp.API.Core;

namespace WeaponPaints;

public static class PlayerExtensions
{
	public static void Print(this CCSPlayerController controller, string message)
	{
		if (!controller.IsValid || WeaponPaints._localizer == null) return;
		controller.PrintToChat($"{WeaponPaints._localizer["wp_prefix"]}{message}");
	}
}

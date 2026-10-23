using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Plugin = WeaponPaints.WeaponPaints;

internal static class DeferredWeaponChecks
{
    internal static void Run()
    {
        const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo apply = typeof(Plugin).GetMethod("TryApplySpawnedWeapon", InstanceMembers)!;
        if (apply.GetParameters()[0].ParameterType != typeof(uint))
            throw new InvalidOperationException("Deferred weapon callbacks must capture a numeric entity handle, not a native pointer.");

        // Exercise the real plugin callback and the real CounterStrikeSharp
        // resolver against a small allocated entity-list fixture. No CS2 native
        // entry points or real weapon schema writes are invoked in this test.
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        using var lifetime = new CancellationTokenSource();
        typeof(Plugin).GetField("_lifetime", InstanceMembers)!.SetValue(plugin, lifetime);
        typeof(BasePlugin).GetProperty("Logger")!.SetValue(plugin, NullLogger.Instance);
        var ready = typeof(Plugin).GetField("_runtimeReady", InstanceMembers)!;
        var generation = typeof(Plugin).GetField("_entityCallbackGeneration", InstanceMembers)!;
        ready.SetValue(plugin, true);
        generation.SetValue(plugin, 7);

        Type system = typeof(EntitySystem);
        FieldInfo list = system.GetField("ConcreteEntityListPointer", StaticMembers)!;
        object? previousList = list.GetValue(null);
        int Constant(string name) => (int)system.GetField(name, StaticMembers)!.GetRawConstantValue()!;
        int count = Constant("MaxChunks");
        int chunkSize = Constant("MaxEntitiesPerChunk");
        int identitySize = Constant("SizeOfEntityIdentity");
        int handleOffset = Constant("HandleOffset");
        nint chunks = Marshal.AllocHGlobal(count * IntPtr.Size);
        nint identities = Marshal.AllocHGlobal(chunkSize * identitySize);
        try
        {
            Marshal.Copy(new byte[count * IntPtr.Size], 0, chunks, count * IntPtr.Size);
            Marshal.Copy(new byte[chunkSize * identitySize], 0, identities, chunkSize * identitySize);
            var fixtureList = new Lazy<nint>(() => chunks);
            list.SetValue(null, fixtureList);
            uint raw = (42u << Utilities.MaxEntityBits) | 123u;
            Apply(raw, 7); // Entity-list chunk is absent.
            if (!fixtureList.IsValueCreated)
                throw new InvalidOperationException("Current-generation callback did not exercise the entity resolver.");
            CheckUnresolved(raw);

            Marshal.WriteIntPtr(chunks, identities);
            nint identity = identities + 123 * identitySize;
            // Deliberately unusable entity pointer: serial validation must reject
            // the callback BEFORE the NativeEntity constructor could read it.
            Marshal.WriteIntPtr(identity, (nint)1);
            Marshal.WriteInt32(identity, handleOffset, -1);
            Apply(raw, 7); // Deleted entity.
            CheckUnresolved(raw);
            uint reused = (43u << Utilities.MaxEntityBits) | 123u;
            Marshal.WriteInt32(identity, handleOffset, unchecked((int)reused));
            Apply(raw, 7); // Same entity index, different serial number.
            CheckUnresolved(raw);
            Apply(uint.MaxValue, 7); // Invalid handle sentinel.
            CheckUnresolved(uint.MaxValue);

            bool resolverTouched = false;
            list.SetValue(null, new Lazy<nint>(() =>
            {
                resolverTouched = true;
                throw new InvalidOperationException("A stale callback reached the entity resolver.");
            }));
            Apply(raw, 6); // Old map generation.
            if (resolverTouched) throw new InvalidOperationException("Map-reset callback was not rejected.");
            ready.SetValue(plugin, false);
            Apply(raw, 7); // Runtime has stopped.
            if (resolverTouched) throw new InvalidOperationException("Stopped-runtime callback was not rejected.");
            ready.SetValue(plugin, true);
            lifetime.Cancel();
            Apply(raw, 7); // Unloaded/hot-reloaded old plugin instance.
            if (resolverTouched) throw new InvalidOperationException("Unload callback was not rejected.");
        }
        finally
        {
            list.SetValue(null, previousList);
            Marshal.FreeHGlobal(identities);
            Marshal.FreeHGlobal(chunks);
        }
        Console.WriteLine("PASS: deferred weapon callbacks safely skip absent/deleted/reused handles and map-reset/unload callbacks using the actual CounterStrikeSharp resolver.");

        void Apply(uint raw, int epoch) => apply.Invoke(plugin, [raw, epoch, "weapon_ak47"]);
        void CheckUnresolved(uint raw)
        {
            // Do not allow the plugin's ordinary managed-exception catch to
            // mask a resolver failure in the test fixture.
            if (new CHandle<CBasePlayerWeapon>(raw).Value != null)
                throw new InvalidOperationException("Deleted/reused entity handle resolved to a weapon.");
        }
    }
}

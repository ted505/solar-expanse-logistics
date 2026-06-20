using Game.Info;
using Language;
using ScriptableObjectScripts;

namespace LogisticsModSdk.Logic;

/// <summary>
/// Central place for user-visible strings. Each entry goes through
/// <see cref="LEManager.Get(string, string)"/> with a stable key plus an English
/// fallback so translation packs can override without code changes.
/// </summary>
internal static class LogisticsStrings
{
    private const string Prefix = "logisticsmod.";

    private static string Loc(string key, string fallback)
    {
        return LEManager.Get(Prefix + key, fallback);
    }

    private static string Name(ObjectInfo oi) => oi?.ObjectName ?? "?";
    private static string Name(ResourceDefinition rd) => rd != null ? LEManager.Get(rd.ID, rd.ID) : "?";

    // --- status words shown in the UI ---
    public static string StatusIdle() => Loc("status.idle", "idle");
    public static string StatusPending() => Loc("status.pending", "pending");
    public static string StatusInTransit() => Loc("status.in_transit", "in transit");
    public static string StatusBlocked() => Loc("status.blocked", "blocked");
    public static string StatusSatisfied() => Loc("status.satisfied", "satisfied");
    public static string StatusFailed() => Loc("status.failed", "failed");

    // --- relay-progress notes ---
    public static string NoProviderInNetwork() => Loc("note.no_provider", "No provider in network");
    public static string StagingTo(ObjectInfo orbit) => string.Format(Loc("note.staging_to", "Staging to {0}"), Name(orbit));
    public static string StagedAt(ObjectInfo orbit) => string.Format(Loc("note.staged_at", "Staged at {0}"), Name(orbit));
    public static string ShippingFrom(ObjectInfo orbit) => string.Format(Loc("note.shipping_from", "Shipping from {0}"), Name(orbit));
    public static string WaitingForSpacecraftAt(ObjectInfo orbit) => string.Format(Loc("note.waiting_spacecraft", "Waiting for spacecraft at {0}"), Name(orbit));
    public static string WaitingForLaunchVehicleAt(ObjectInfo current) => string.Format(Loc("note.waiting_lv", "Waiting for launch vehicle at {0}"), Name(current));
    public static string AwaitingReturnFrom(ObjectInfo current) => string.Format(Loc("note.awaiting_return", "Awaiting return from {0}"), Name(current));
    public static string ReturnRetryCooldown(double days) => string.Format(Loc("note.return_retry_cooldown", "Return launch blocked; retrying in {0:0.#} days"), days);
    public static string WaitingForReturnFuel(ResourceDefinition fuelType, ObjectInfo requester) => string.Format(Loc("note.waiting_return_fuel", "Waiting for return fuel {0} needed at {1} for the return leg"), Name(fuelType), Name(requester));
    public static string ReturnBlockedSuffix(string baseNote, string vehicleName) => string.Format(Loc("note.return_blocked_on_ship", "{0} on {1}"), baseNote ?? "", vehicleName ?? "?");

    // --- planner/blocker reasons (returned by TryCreateDeliveries) ---
    public static string NoSurplusAt(ResourceDefinition rd, ObjectInfo provider) => string.Format(Loc("blocker.no_surplus", "No surplus {0} available at {1}"), Name(rd), Name(provider));
    public static string NoSurplusAtWithDetails(ResourceDefinition rd, ObjectInfo provider, double stock, double minKeep, double committed) => string.Format(Loc("blocker.no_surplus_detail", "No surplus {0} at {1} (stock: {2}, reserve: {3}, committed: {4})"), Name(rd), Name(provider), stock.ToString("0.#"), minKeep.ToString("0.#"), committed.ToString("0.#"));
    public static string NoLogisticsDataAt(ObjectInfo location) => string.Format(Loc("blocker.no_logistics_data", "No logistics data at {0}"), Name(location));
    public static string NoSpacecraftLogisticsAt(ObjectInfo location) => string.Format(Loc("blocker.no_sc_logistics", "No spacecraft logistics configured at {0}"), Name(location));
    public static string NoSpacecraftQuotaAt(ObjectInfo location) => string.Format(Loc("blocker.no_sc_quota", "No spacecraft quota at {0}"), Name(location));
    public static string NoSpacecraftPresentAt(ObjectInfo location) => string.Format(Loc("blocker.no_sc_present", "No spacecraft present at {0}"), Name(location));
    public static string NoMatchingSpacecraftAt(ObjectInfo location) => string.Format(Loc("blocker.no_sc_matching", "No matching spacecraft present at {0}"), Name(location));
    public static string NoSpacecraftInRange(ObjectInfo location, ObjectInfo target) => string.Format(Loc("blocker.no_sc_range", "No spacecraft at {0} can reach {1}"), Name(location), Name(target));
    public static string AllSpacecraftQuotaInUseAt(ObjectInfo location) => string.Format(Loc("blocker.sc_quota_full", "All spacecraft quota in use at {0}"), Name(location));
    public static string NoIdleSpacecraftAt(ObjectInfo location) => string.Format(Loc("blocker.no_sc_idle", "No idle spacecraft available at {0}"), Name(location));
    public static string NoSpacecraftAvailableAt(ObjectInfo location) => string.Format(Loc("blocker.no_sc_available", "No spacecraft available at {0}"), Name(location));
    public static string NoProviderSelected() => Loc("blocker.no_provider_selected", "No provider selected");
    public static string NoSurfaceLaunchPathFrom(ObjectInfo provider) => string.Format(Loc("blocker.no_surface_launch", "No surface launch path from {0}"), Name(provider));
    public static string NoLaunchVehiclesAt(ObjectInfo provider) => string.Format(Loc("blocker.no_lvs_present", "No launch vehicles at {0}"), Name(provider));
    public static string NoLvQuotaAt(ObjectInfo provider) => string.Format(Loc("blocker.no_lv_quota", "No launch vehicle quota enabled at {0}"), Name(provider));
    public static string NoReadyLvAt(ObjectInfo provider) => string.Format(Loc("blocker.no_lv_ready", "No ready launch vehicle at {0}"), Name(provider));
    public static string AllLvsCoolingDownAt(ObjectInfo provider) => string.Format(Loc("blocker.lv_cooldown", "All launch vehicles at {0} are recovering for reuse"), Name(provider));
    public static string NoMatchingLvQuotaAt(ObjectInfo provider) => string.Format(Loc("blocker.no_lv_matching", "No matching launch vehicle quota at {0}"), Name(provider));
    public static string MatchingLvsCoolingDownAt(ObjectInfo provider) => string.Format(Loc("blocker.lv_matching_cooldown", "Matching launch vehicles at {0} are recovering for reuse"), Name(provider));
    public static string AllLvQuotaInUseAt(ObjectInfo provider) => string.Format(Loc("blocker.lv_quota_full", "All launch vehicle quota in use at {0}"), Name(provider));
    public static string NoLvAvailableAt(ObjectInfo provider) => string.Format(Loc("blocker.no_lv_available", "No launch vehicle available at {0}"), Name(provider));
    public static string NoOrbitalContainerAt(ObjectInfo provider) => string.Format(Loc("blocker.no_container", "No orbital payload container available for {0}"), Name(provider));
    public static string NoCargoCapacityFrom(ObjectInfo provider) => string.Format(Loc("blocker.no_cargo_capacity", "No cargo capacity available from {0}"), Name(provider));
    public static string NoOrbitalPayloadCapacityFrom(ObjectInfo provider) => string.Format(Loc("blocker.no_orbital_payload_capacity", "No orbital payload capacity available from {0}"), Name(provider));
    public static string NoSourceOrbitAt(ObjectInfo provider) => string.Format(Loc("blocker.no_source_orbit", "No source orbit available at {0}"), Name(provider));

    // --- transit suffix shown in the UI ---
    public static string TransitOnVehicleOnly(string vehicleName) => string.Format(Loc("transit.on_vehicle", " (on {0})"), vehicleName ?? "?");
    public static string TransitArrivesOnly(string arrival) => string.Format(Loc("transit.arrives_only", " (arrives {0})"), arrival ?? "?");
    public static string TransitOnVehicleArrives(string vehicleName, string arrival) => string.Format(Loc("transit.on_vehicle_arrives", " (on {0}, arrives {1})"), vehicleName ?? "?", arrival ?? "?");
}

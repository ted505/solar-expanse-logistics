using System.Collections.Generic;
using System.Linq;
using CustomUpdate;
using Game;
using Game.Info;
using Game.UI.Windows.Elements.PlanMissionElements;
using Manager;
using UnityEngine;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Fleet query and reservation helpers. Real spacecraft reservations are keyed by positive
/// stock ship IDs; synthetic carriers are tracked separately by dispatch ID and Unity instance.
/// </summary>
public sealed class SdkFleet
{
    private readonly Dictionary<int, SdkFleetReservation> _reservations = new Dictionary<int, SdkFleetReservation>();
    private readonly Dictionary<string, SdkSyntheticCarrierReservation> _syntheticCarriersByDispatchId = new Dictionary<string, SdkSyntheticCarrierReservation>();
    private SdkLogging _log;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Current player company from stock <see cref="GameManager"/>.
    /// </summary>
    public Company PlayerCompany => MonoBehaviourSingleton<GameManager>.Instance?.Player;

    /// <summary>
    /// Finds all loaded stock spacecraft belonging to the player company.
    /// </summary>
    public List<Spacecraft> GetPlayerSpacecraft()
    {
        var player = PlayerCompany;
        return Resources.FindObjectsOfTypeAll<Spacecraft>()
            .Where(sc => sc != null && sc.GetCompany() == player)
            .ToList();
    }

    /// <summary>
    /// Finds all loaded player spacecraft currently on the supplied object.
    /// </summary>
    public List<Spacecraft> GetPlayerSpacecraftAt(ObjectInfo location)
    {
        return GetPlayerSpacecraft()
            .Where(sc => sc.CurrentlyOnThisObject == location)
            .ToList();
    }

    /// <summary>
    /// Returns true when a spacecraft is on an object, has no active mission info, and is not owned by an active stock cycle.
    /// </summary>
    public bool IsIdle(Spacecraft spacecraft)
    {
        if (spacecraft == null)
            return false;

        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        return spacecraft.CurrentlyOnThisObject != null
            && spacecraft.GetMissionInfo() == null
            && (cm == null || cm.GetCycleMission(spacecraft) == null);
    }

    /// <summary>
    /// Reserves a real spacecraft ID for a mod owner and route.
    /// </summary>
    /// <remarks>
    /// Negative IDs are rejected because they are commonly synthetic carriers. Use
    /// <see cref="TrackSyntheticCarrier"/> for those.
    /// </remarks>
    public bool ReserveSpacecraft(int shipId, string ownerId, string reason, string routeId = null, int sourceId = -1, int targetId = -1, double? expiresInSeconds = null)
    {
        if (shipId < 0 || string.IsNullOrWhiteSpace(ownerId))
        {
            _log?.Verbose("sdk.fleet", $"reserve-failed ship={shipId} owner={ownerId ?? "null"} reason=invalid-arguments");
            return false;
        }

        if (_reservations.TryGetValue(shipId, out var existing)
            && !string.Equals(existing.OwnerId, ownerId, System.StringComparison.OrdinalIgnoreCase))
        {
            _log?.Verbose("sdk.fleet", $"reserve-failed ship={shipId} owner={ownerId} existingOwner={existing.OwnerId} route={existing.RouteId}");
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce("fleet-reservation-conflict", $"{shipId}:{ownerId}");
            return false;
        }

        _reservations[shipId] = new SdkFleetReservation
        {
            ShipId = shipId,
            OwnerId = ownerId,
            Reason = reason,
            RouteId = routeId,
            SourceId = sourceId,
            TargetId = targetId,
            CreatedAtUtc = System.DateTime.UtcNow,
            ExpiresAtUtc = expiresInSeconds.HasValue ? System.DateTime.UtcNow.AddSeconds(expiresInSeconds.Value) : (System.DateTime?)null
        };
        _log?.Verbose("sdk.fleet", $"reserve ship={shipId} owner={ownerId} routeId={routeId ?? "none"} reason={reason ?? "none"} result=ok");
        return true;
    }

    /// <summary>
    /// Releases a real spacecraft reservation, optionally requiring a matching owner.
    /// </summary>
    public bool ReleaseSpacecraft(int shipId, string ownerId = null)
    {
        if (!_reservations.TryGetValue(shipId, out var existing))
        {
            _log?.VerboseThrottled("sdk.fleet", $"release-not-reserved-{shipId}-{ownerId ?? "any"}", $"release ship={shipId} owner={ownerId ?? "any"} result=not-reserved", 10.0);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ownerId)
            && !string.Equals(existing.OwnerId, ownerId, System.StringComparison.OrdinalIgnoreCase))
        {
            _log?.Verbose("sdk.fleet", $"release-failed ship={shipId} owner={ownerId} existingOwner={existing.OwnerId}");
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce("fleet-release-conflict", $"{shipId}:{ownerId}");
            return false;
        }

        _reservations.Remove(shipId);
        _log?.Verbose("sdk.fleet", $"release ship={shipId} owner={ownerId ?? existing.OwnerId} result=ok");
        return true;
    }

    /// <summary>
    /// Returns a real spacecraft reservation, removing it first if it has expired.
    /// </summary>
    public SdkFleetReservation GetReservation(int shipId)
    {
        if (!_reservations.TryGetValue(shipId, out var reservation))
            return null;
        if (reservation.ExpiresAtUtc.HasValue && reservation.ExpiresAtUtc.Value < System.DateTime.UtcNow)
        {
            _reservations.Remove(shipId);
            return null;
        }
        return reservation;
    }

    /// <summary>
    /// Returns true when a real spacecraft ID currently has a non-expired reservation.
    /// </summary>
    public bool IsReserved(int shipId) => GetReservation(shipId) != null;

    /// <summary>
    /// Clears real reservations and synthetic carrier records for one owner, or all owners when omitted.
    /// </summary>
    public void ClearReservations(string ownerId = null)
    {
        var before = _reservations.Count;
        var syntheticBefore = _syntheticCarriersByDispatchId.Count;
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            _reservations.Clear();
            _syntheticCarriersByDispatchId.Clear();
        }
        else
        {
            foreach (var shipId in _reservations.Where(p => string.Equals(p.Value.OwnerId, ownerId, System.StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
                _reservations.Remove(shipId);
            foreach (var dispatchId in _syntheticCarriersByDispatchId.Where(p => string.Equals(p.Value.OwnerId, ownerId, System.StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
                _syntheticCarriersByDispatchId.Remove(dispatchId);
        }
        _log?.Verbose("sdk.fleet", $"clear-reservations owner={ownerId ?? "all"} before={before} after={_reservations.Count} syntheticBefore={syntheticBefore} syntheticAfter={_syntheticCarriersByDispatchId.Count}");
    }

    /// <summary>
    /// Returns a snapshot copy of real reservations for diagnostics JSON or debug UI.
    /// </summary>
    public List<SdkFleetReservation> GetReservationsSnapshot()
    {
        return _reservations.Values.ToList();
    }

    /// <summary>
    /// Returns true when a dispatch ID currently has a synthetic carrier record.
    /// </summary>
    public bool HasSyntheticCarrier(string dispatchId)
    {
        return !string.IsNullOrWhiteSpace(dispatchId)
            && _syntheticCarriersByDispatchId.ContainsKey(dispatchId);
    }

    /// <summary>
    /// Tracks a synthetic carrier, such as a logistics LV payload container, without reserving a real spacecraft ID.
    /// </summary>
    public bool TrackSyntheticCarrier(string dispatchId, string ownerId, Spacecraft carrier, string reason, int sourceId = -1, int targetId = -1)
    {
        if (string.IsNullOrWhiteSpace(dispatchId) || string.IsNullOrWhiteSpace(ownerId) || carrier == null)
        {
            _log?.Verbose("sdk.fleet", $"synthetic-track-failed dispatchId={dispatchId ?? "null"} owner={ownerId ?? "null"} reason=invalid-arguments carrier={carrier?.ID ?? -9999}");
            return false;
        }

        if (_syntheticCarriersByDispatchId.TryGetValue(dispatchId, out var existing)
            && !string.Equals(existing.OwnerId, ownerId, System.StringComparison.OrdinalIgnoreCase))
        {
            _log?.Verbose("sdk.fleet", $"synthetic-track-failed dispatchId={dispatchId} owner={ownerId} existingOwner={existing.OwnerId} reason=owner-conflict");
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce("synthetic-carrier-conflict", dispatchId);
            return false;
        }

        _syntheticCarriersByDispatchId[dispatchId] = new SdkSyntheticCarrierReservation
        {
            DispatchId = dispatchId,
            OwnerId = ownerId,
            Reason = reason,
            CarrierId = carrier.ID,
            CarrierInstanceId = carrier.GetInstanceID(),
            CarrierName = carrier.GetSpacecraftName(),
            CarrierType = carrier.spacecraftType?.NameRocketType ?? carrier.spacecraftType?.ID,
            SourceId = sourceId,
            TargetId = targetId,
            CreatedAtUtc = System.DateTime.UtcNow
        };
        _log?.Verbose("sdk.fleet", $"synthetic-track dispatchId={dispatchId} owner={ownerId} carrier={carrier.GetSpacecraftName() ?? "null"}#{carrier.ID} instance={carrier.GetInstanceID()} reason={reason ?? "none"} result=ok");
        return true;
    }

    /// <summary>
    /// Releases a synthetic carrier record by dispatch ID, optionally requiring a matching owner.
    /// </summary>
    public bool ReleaseSyntheticCarrier(string dispatchId, string ownerId = null)
    {
        if (string.IsNullOrWhiteSpace(dispatchId) || !_syntheticCarriersByDispatchId.TryGetValue(dispatchId, out var existing))
        {
            _log?.VerboseThrottled("sdk.fleet", $"synthetic-release-not-tracked-{dispatchId ?? "null"}-{ownerId ?? "any"}", $"synthetic-release dispatchId={dispatchId ?? "null"} owner={ownerId ?? "any"} result=not-tracked", 10.0);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ownerId)
            && !string.Equals(existing.OwnerId, ownerId, System.StringComparison.OrdinalIgnoreCase))
        {
            _log?.Verbose("sdk.fleet", $"synthetic-release-failed dispatchId={dispatchId} owner={ownerId} existingOwner={existing.OwnerId}");
            Core.SolarSdk.Diagnostics.WriteSnapshotOnce("synthetic-carrier-release-conflict", dispatchId);
            return false;
        }

        _syntheticCarriersByDispatchId.Remove(dispatchId);
        _log?.Verbose("sdk.fleet", $"synthetic-release dispatchId={dispatchId} owner={ownerId ?? existing.OwnerId} result=ok");
        return true;
    }

    /// <summary>
    /// Returns a snapshot copy of synthetic carrier records for diagnostics JSON or debug UI.
    /// </summary>
    public List<SdkSyntheticCarrierReservation> GetSyntheticCarrierSnapshot()
    {
        return _syntheticCarriersByDispatchId.Values.ToList();
    }
}

/// <summary>
/// Runtime reservation record for a real, positive-ID spacecraft.
/// </summary>
public sealed class SdkFleetReservation
{
    /// <summary>Real stock spacecraft ID.</summary>
    public int ShipId { get; set; }
    /// <summary>Mod owner that created the reservation.</summary>
    public string OwnerId { get; set; }
    /// <summary>Human-readable reservation reason.</summary>
    public string Reason { get; set; }
    /// <summary>Route or dispatch ID associated with the reservation.</summary>
    public string RouteId { get; set; }
    /// <summary>Stock source object ID.</summary>
    public int SourceId { get; set; }
    /// <summary>Stock target object ID.</summary>
    public int TargetId { get; set; }
    /// <summary>UTC reservation creation time.</summary>
    public System.DateTime CreatedAtUtc { get; set; }
    /// <summary>Optional UTC expiration time.</summary>
    public System.DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>
/// Runtime record for fake carrier spacecraft that should not enter the real reservation ledger.
/// </summary>
public sealed class SdkSyntheticCarrierReservation
{
    /// <summary>Dispatch ID that owns the synthetic carrier.</summary>
    public string DispatchId { get; set; }
    /// <summary>Mod owner that created the synthetic carrier record.</summary>
    public string OwnerId { get; set; }
    /// <summary>Human-readable tracking reason.</summary>
    public string Reason { get; set; }
    /// <summary>Carrier stock ID, often negative for synthetic objects.</summary>
    public int CarrierId { get; set; }
    /// <summary>Unity runtime instance ID used to disambiguate fake carriers.</summary>
    public int CarrierInstanceId { get; set; }
    /// <summary>Carrier display name, when available.</summary>
    public string CarrierName { get; set; }
    /// <summary>Carrier type name or ID, when available.</summary>
    public string CarrierType { get; set; }
    /// <summary>Stock source object ID.</summary>
    public int SourceId { get; set; }
    /// <summary>Stock target object ID.</summary>
    public int TargetId { get; set; }
    /// <summary>UTC record creation time.</summary>
    public System.DateTime CreatedAtUtc { get; set; }
}

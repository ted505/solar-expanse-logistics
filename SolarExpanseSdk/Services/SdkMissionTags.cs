using System;
using System.Collections.Generic;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.PlanMissionElements;
using Game.VisualizationScripts;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Mission tag and display-name correlation helpers. This service preserves mod-owned mission
/// names across stock parameter creation, mission-info creation, and mission row display.
/// </summary>
public sealed class SdkMissionTags
{
    private readonly List<string> _tagPrefixes = new List<string>();
    private readonly List<ResolverRegistration> _resolvers = new List<ResolverRegistration>();
    private SdkLogging _log;

    /// <summary>
    /// Raised after the SDK applies a resolved name to a stock <see cref="MissionInfo"/>.
    /// </summary>
    public event Action<MissionInfo, string> MissionInfoNameApplied;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Registers a mission-name prefix that identifies mod-owned missions.
    /// </summary>
    public void RegisterMissionPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || _tagPrefixes.Contains(prefix))
            return;

        _tagPrefixes.Add(prefix);
        _log?.Verbose("sdk.missionPlanning", $"mission-tag-prefix registered prefix={prefix}");
    }

    /// <summary>
    /// Registers or replaces a name resolver for one mod owner.
    /// </summary>
    /// <remarks>
    /// Resolvers should be cheap and side-effect-free. The first non-empty resolved name wins.
    /// </remarks>
    public void RegisterNameResolver(string ownerId, Func<SdkMissionNameContext, string> resolver)
    {
        if (resolver == null)
            return;

        var owner = string.IsNullOrWhiteSpace(ownerId) ? "mod" : ownerId;
        _resolvers.RemoveAll(r => string.Equals(r.OwnerId, owner, StringComparison.OrdinalIgnoreCase));
        _resolvers.Add(new ResolverRegistration { OwnerId = owner, Resolver = resolver });
        _log?.Verbose("sdk.missionPlanning", $"mission-name-resolver registered owner={owner}");
    }

    /// <summary>
    /// Returns true when a mission name starts with a registered prefix.
    /// </summary>
    public bool IsTaggedName(string missionName)
    {
        if (string.IsNullOrEmpty(missionName))
            return false;

        foreach (var prefix in _tagPrefixes)
        {
            if (missionName.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when a mission info already has a tagged name or a resolver can produce one.
    /// </summary>
    public bool IsTaggedMission(MissionInfo missionInfo)
    {
        return IsTaggedName(missionInfo?.missionName) || !string.IsNullOrEmpty(ResolveName(missionInfo));
    }

    /// <summary>
    /// Resolves a display name for a stock mission parameter.
    /// </summary>
    public string ResolveName(PMMissionParameter parameter)
    {
        return ResolveName(new SdkMissionNameContext
        {
            MissionParameter = parameter,
            ExistingName = parameter?.MissionName,
            Start = parameter?.Start,
            Target = parameter?.Target,
            Company = parameter?.FlyCompany,
            SpacecraftInfo = parameter?.SC,
            CargoAll = parameter?.CargoAll
        });
    }

    /// <summary>
    /// Resolves a display name for a stock mission info object.
    /// </summary>
    public string ResolveName(MissionInfo missionInfo)
    {
        return ResolveName(new SdkMissionNameContext
        {
            MissionInfo = missionInfo,
            ExistingName = missionInfo?.missionName,
            Start = missionInfo?.start,
            Target = missionInfo?.target,
            Company = missionInfo?.company,
            SpacecraftInfo = missionInfo?.spacecraftInfo2,
            SpacecraftInfos = missionInfo?.ListSpacecraftInfo2,
            CargoAll = missionInfo?.cargoAll,
            MissionCreator = missionInfo?.missionCreator
        });
    }

    /// <summary>
    /// Resolves a display name from an arbitrary SDK mission-name context.
    /// </summary>
    public string ResolveName(SdkMissionNameContext context)
    {
        if (context == null)
            return null;

        if (IsTaggedName(context.ExistingName))
            return context.ExistingName;

        foreach (var registration in _resolvers)
        {
            try
            {
                var name = registration.Resolver(context);
                if (!string.IsNullOrEmpty(name))
                {
                    _log?.Verbose("sdk.missionPlanning", $"mission-name resolved owner={registration.OwnerId} name=\"{name}\" route=\"{context.Start?.ObjectName ?? "null"}->{context.Target?.ObjectName ?? "null"}\"");
                    return name;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("sdk.missionPlanning", $"mission-name resolver failed owner={registration.OwnerId} error={ex.GetType().Name}: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("mission-name-resolver-error", registration.OwnerId);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves and applies a mission name to a stock parameter with <see cref="PMMissionParameter.ChangeMissionName"/>.
    /// </summary>
    public bool ApplyMissionParameterName(PMMissionParameter parameter, string context)
    {
        var name = ResolveName(parameter);
        if (string.IsNullOrEmpty(name))
            return false;

        parameter.ChangeMissionName(name, _manualChangeName: true);
        _log?.Verbose("sdk.missionPlanning", $"mission-parameter-name context={context ?? "none"} name=\"{name}\" route=\"{parameter.Start?.ObjectName ?? "null"}->{parameter.Target?.ObjectName ?? "null"}\"");
        return true;
    }

    /// <summary>
    /// Resolves and applies a mission name to stock mission info, then updates dispatch correlation if possible.
    /// </summary>
    public bool ApplyMissionInfoName(MissionInfo missionInfo, string name, string context)
    {
        if (missionInfo == null)
            return false;

        if (string.IsNullOrEmpty(name))
            name = ResolveName(missionInfo);
        if (string.IsNullOrEmpty(name))
            return false;

        missionInfo.missionName = name;
        missionInfo.fromCyclicalMission = true;
        var dispatchId = Core.SolarSdk.CyclicalMissions.FindDispatchId(missionInfo);
        if (!string.IsNullOrEmpty(dispatchId))
            Core.SolarSdk.CyclicalMissions.RegisterMissionInfo(dispatchId, missionInfo);
        _log?.Verbose("sdk.missionPlanning", $"mission-info-name context={context ?? "none"} mission={missionInfo.id} name=\"{name}\" dispatchId={dispatchId ?? "none"}");
        MissionInfoNameApplied?.Invoke(missionInfo, name);
        return true;
    }

    private sealed class ResolverRegistration
    {
        public string OwnerId;
        public Func<SdkMissionNameContext, string> Resolver;
    }
}

/// <summary>
/// Context object passed to mission-name resolvers.
/// </summary>
public sealed class SdkMissionNameContext
{
    /// <summary>Stock mission parameter, when resolving before mission info creation.</summary>
    public PMMissionParameter MissionParameter { get; set; }
    /// <summary>Stock mission info, when resolving after mission info creation.</summary>
    public MissionInfo MissionInfo { get; set; }
    /// <summary>Primary spacecraft or synthetic carrier.</summary>
    public ISpacecraftInfo SpacecraftInfo { get; set; }
    /// <summary>Optional multi-spacecraft stock selection.</summary>
    public IEnumerable<ISpacecraftInfo> SpacecraftInfos { get; set; }
    /// <summary>Stock cargo object associated with the mission.</summary>
    public CargoAll CargoAll { get; set; }
    /// <summary>Company that owns the mission.</summary>
    public Company Company { get; set; }
    /// <summary>Stock trajectory object, when display code provides one.</summary>
    public TrajectoryObject TrajectoryObject { get; set; }
    /// <summary>Stock mission creator value, when known.</summary>
    public MissionInfo.EMissionCreator? MissionCreator { get; set; }
    /// <summary>Existing stock mission name before resolver override.</summary>
    public string ExistingName { get; set; }
    /// <summary>Mission source object.</summary>
    public ObjectInfo Start { get; set; }
    /// <summary>Mission target object.</summary>
    public ObjectInfo Target { get; set; }
}

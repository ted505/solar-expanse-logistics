using System;
using System.Collections.Generic;
using CustomUpdate;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.PlanMissionElements;
using Manager;
using ScriptableObjectScripts;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Helpers for reading and mutating stock mission cargo, fuel, supply, crew, resource,
/// and payload data. This service wraps stock <see cref="CargoAll"/> and loadout-adjacent
/// APIs without performing mission launch or resource removal.
/// </summary>
public sealed class SdkMissionLoadout
{
    private SdkLogging _log;

    /// <summary>
    /// Raised after stock creates cargo for a cyclical mission planning attempt.
    /// </summary>
    public event Action<SdkCycleCargoCreatedContext> CargoCreatedForCycle;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Dispatches stock-created cyclical cargo diagnostics to subscribers.
    /// </summary>
    public void RaiseCargoCreatedForCycle(SdkCycleCargoCreatedContext context)
    {
        _log?.Verbose("sdk.missionLoadout", $"cycle-cargo-created name=\"{context?.Cycle?.customNameFromPlanMission ?? "null"}\" start={context?.StartObject?.ObjectName ?? "null"} allOnPlanet={context?.AllResourceOnPlanet.ToString() ?? "null"} cargo={FormatCargo(context?.Cargo)} subscribers={CargoCreatedForCycle?.GetInvocationList().Length ?? 0}");
        if (CargoCreatedForCycle == null)
            return;

        foreach (Action<SdkCycleCargoCreatedContext> handler in CargoCreatedForCycle.GetInvocationList())
        {
            try
            {
                handler(context);
            }
            catch (Exception ex)
            {
                _log?.Warning("sdk.missionLoadout", $"cycle cargo handler failed: {ex.GetType().Name}: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("cycle-cargo-handler-error", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Creates a new empty stock cargo container using <see cref="CargoAll.CreateCargoEmpty"/>.
    /// </summary>
    public CargoAll CreateEmptyCargo()
    {
        return CargoAll.CreateCargoEmpty();
    }

    /// <summary>
    /// Deep-copies SDK-visible cargo lists and special cargo fields into a new stock cargo container.
    /// </summary>
    public CargoAll CloneCargo(CargoAll cargo)
    {
        if (cargo == null)
            return null;

        var clone = CargoAll.CreateCargoEmpty();
        clone.hypothetical = cargo.hypothetical;
        clone.cyclicalMissionWhenUI = cargo.cyclicalMissionWhenUI;
        clone.entireAsteroid = cargo.entireAsteroid;
        clone.gravityAssistHelp1 = cargo.gravityAssistHelp1;
        clone.cargoMax = cargo.cargoMax;
        clone.cargoMaxCrew = cargo.cargoMaxCrew;
        clone.loadLimit2 = cargo.loadLimit2;
        clone.toReset = cargo.toReset;
        clone.toCancel = cargo.toCancel;
        clone.cargoFuel = CloneCargoItem(cargo.cargoFuel, clone, fuelSpecial: true, lifeSupportSpecial: true);
        CloneCargoList(cargo.listCargo, clone.listCargo, clone);
        CloneCargoList(cargo.listCargoToOrbit, clone.listCargoToOrbit, clone);
        CloneCargoList(cargo.listCargoGravityAssists, clone.listCargoGravityAssists, clone);
        return clone;
    }

    /// <summary>
    /// Clones a single cargo item for the supplied owner cargo container.
    /// </summary>
    public Cargo CloneCargoItem(Cargo cargo, CargoAll owner)
    {
        return CloneCargoItem(cargo, owner, cargo?.IsCargoFuelSpecial ?? false, cargo?.IsCargoLifeSupportSpecial ?? false);
    }

    /// <summary>
    /// Ensures a stock cargo container has initialized lists and special fuel cargo, then removes
    /// null or non-positive cargo entries from regular cargo lists.
    /// </summary>
    /// <remarks>This mutates the supplied cargo object. If cargo is null, a new empty cargo object is returned.</remarks>
    public CargoAll NormalizeCargo(CargoAll cargo)
    {
        if (cargo == null)
            cargo = CargoAll.CreateCargoEmpty();
        cargo.listCargo ??= new List<Cargo>();
        cargo.listCargoToOrbit ??= new List<Cargo>();
        cargo.listCargoGravityAssists ??= new List<Cargo>();
        if (cargo.cargoFuel == null)
        {
            cargo.cargoFuel = new Cargo(cargo, _isCargoFuelSpecial: true);
            cargo.cargoFuel.resourceTypeType = EResourceTypeType.resorces;
            cargo.cargoFuel.resourceType = GetFuelResource();
        }
        cargo.listCargo.RemoveAll(c => c == null || c.cargoMass <= 0.0);
        cargo.listCargoToOrbit.RemoveAll(c => c == null || c.cargoMass <= 0.0);
        cargo.listCargoGravityAssists.RemoveAll(c => c == null || c.cargoMass <= 0.0);
        return cargo;
    }

    /// <summary>
    /// Returns stock current cargo mass, or zero when cargo is null.
    /// </summary>
    public double GetCargoMass(CargoAll cargo)
    {
        return cargo?.CargoCurrent ?? 0.0;
    }

    /// <summary>
    /// Returns loaded fuel mass from the special cargo fuel item.
    /// </summary>
    public double GetCargoFuelMass(CargoAll cargo)
    {
        return cargo?.cargoFuel?.cargoMass ?? 0.0;
    }

    /// <summary>
    /// Returns potential fuel mass from the special cargo fuel item.
    /// </summary>
    public double GetCargoPotentialFuelMass(CargoAll cargo)
    {
        return cargo?.cargoFuel?.cargoMassPotencjal ?? 0.0;
    }

    /// <summary>
    /// Returns stock life support value represented by the cargo.
    /// </summary>
    public double GetCargoLifeSupport(CargoAll cargo)
    {
        return cargo?.GetLifeSupport() ?? 0.0;
    }

    /// <summary>
    /// Returns the number of crew entries represented by the cargo.
    /// </summary>
    public int GetCrewCount(CargoAll cargo)
    {
        return cargo?.HowMuchCrew() ?? 0;
    }

    /// <summary>
    /// Returns stock supply mass represented by the cargo.
    /// </summary>
    public double GetSupplyMass(CargoAll cargo)
    {
        return cargo?.GetSupplyFromCargo() ?? 0.0;
    }

    /// <summary>
    /// Converts supply cargo already present in the loadout into stock life support value.
    /// </summary>
    public double GetLifeSupportFromSupply(CargoAll cargo)
    {
        return cargo?.GetLifeSupportFromCargoSupply() ?? 0.0;
    }

    /// <summary>
    /// Converts supply mass to life support using <see cref="GameManager.Economic"/> tuning.
    /// </summary>
    public double ConvertSupplyToLifeSupport(double supplyMass)
    {
        return supplyMass * (MonoBehaviourSingleton<GameManager>.Instance?.Economic?.SupplyToLifeSupportMultiplayer ?? 0f);
    }

    /// <summary>
    /// Converts life support value to supply mass using <see cref="GameManager.Economic"/> tuning.
    /// </summary>
    public double ConvertLifeSupportToSupply(double lifeSupport)
    {
        var multiplier = MonoBehaviourSingleton<GameManager>.Instance?.Economic?.SupplyToLifeSupportMultiplayer ?? 0f;
        return multiplier <= 0f ? 0.0 : lifeSupport / multiplier;
    }

    /// <summary>
    /// Sums all cargo mass for a specific resource, including the special fuel cargo when it uses that resource.
    /// </summary>
    public double GetResourceMass(CargoAll cargo, ResourceDefinition resource)
    {
        if (cargo == null || resource == null)
            return 0.0;

        var total = 0.0;
        AddResourceMass(cargo.listCargo, resource, ref total);
        AddResourceMass(cargo.listCargoToOrbit, resource, ref total);
        AddResourceMass(cargo.listCargoGravityAssists, resource, ref total);
        if (cargo.cargoFuel?.resourceType == resource)
            total += cargo.cargoFuel.cargoMass;
        return total;
    }

    /// <summary>
    /// Returns regular resource cargo items from the normal cargo list and, optionally, to-orbit cargo list.
    /// Fuel special cargo and gravity-assist cargo are excluded.
    /// </summary>
    public List<Cargo> GetRegularResourceCargoItems(CargoAll cargo, bool includeToOrbit = true)
    {
        var result = new List<Cargo>();
        if (cargo?.listCargo != null)
            AddRegularResourceItems(cargo.listCargo, result);
        if (includeToOrbit && cargo?.listCargoToOrbit != null)
            AddRegularResourceItems(cargo.listCargoToOrbit, result);
        return result;
    }

    /// <summary>
    /// Finds a regular resource cargo item in the normal cargo list.
    /// </summary>
    public Cargo FindRegularResourceCargo(CargoAll cargo, ResourceDefinition resource)
    {
        return FindResourceCargo(cargo?.listCargo, resource);
    }

    /// <summary>
    /// Sums regular resource cargo mass from the normal cargo list and, optionally, to-orbit cargo list.
    /// Fuel special cargo and gravity-assist cargo are excluded.
    /// </summary>
    public double GetRegularResourceMass(CargoAll cargo, ResourceDefinition resource, bool includeToOrbit = true)
    {
        if (cargo == null || resource == null)
            return 0.0;

        var total = 0.0;
        AddResourceMass(cargo.listCargo, resource, ref total);
        if (includeToOrbit)
            AddResourceMass(cargo.listCargoToOrbit, resource, ref total);
        return total;
    }

    /// <summary>
    /// Returns true when regular cargo contains a positive amount of the resource.
    /// </summary>
    public bool ContainsRegularResource(CargoAll cargo, ResourceDefinition resource, bool includeToOrbit = true)
    {
        return GetRegularResourceMass(cargo, resource, includeToOrbit) > 0.0;
    }

    /// <summary>
    /// Adds a new resource cargo item to the regular stock cargo list and raises the cargo free-space change event.
    /// </summary>
    /// <remarks>This mutates the supplied cargo object.</remarks>
    public Cargo AddResourceCargo(CargoAll cargo, ResourceDefinition resource, double mass, ObjectInfo source = null)
    {
        cargo = NormalizeCargo(cargo);
        if (resource == null || mass <= 0.0)
            return null;

        var item = new Cargo(cargo)
        {
            resourceTypeType = EResourceTypeType.resorces,
            resourceType = resource,
            cargoMass = mass,
            objectInfo = source
        };
        cargo.listCargo.Add(item);
        cargo.InvokeFreeSpaceChange();
        return item;
    }

    /// <summary>
    /// Finds or creates a normal resource cargo item and increases its mass.
    /// </summary>
    /// <remarks>This mutates the supplied cargo object.</remarks>
    public Cargo AddOrIncreaseResourceCargo(CargoAll cargo, ResourceDefinition resource, double amount, ObjectInfo source = null)
    {
        cargo = NormalizeCargo(cargo);
        if (resource == null || amount <= 0.0)
            return null;

        var item = FindRegularResourceCargo(cargo, resource);
        if (item == null)
            return AddResourceCargo(cargo, resource, amount, source);

        item.cargoMass += amount;
        item.objectInfo = source ?? item.objectInfo;
        cargo.InvokeFreeSpaceChange();
        return item;
    }

    /// <summary>
    /// Sets or creates a regular resource cargo item and raises the cargo free-space change event.
    /// </summary>
    /// <remarks>This mutates the supplied cargo object.</remarks>
    public Cargo SetResourceCargo(CargoAll cargo, ResourceDefinition resource, double mass, ObjectInfo source = null)
    {
        cargo = NormalizeCargo(cargo);
        if (resource == null)
            return null;

        var item = FindResourceCargo(cargo.listCargo, resource);
        if (item == null)
        {
            item = new Cargo(cargo)
            {
                resourceTypeType = EResourceTypeType.resorces,
                resourceType = resource,
                objectInfo = source
            };
            cargo.listCargo.Add(item);
        }

        item.cargoMass = Math.Max(0.0, mass);
        item.objectInfo = source ?? item.objectInfo;
        cargo.InvokeFreeSpaceChange();
        return item;
    }

    /// <summary>
    /// Normalizes cargo and returns how many regular cargo entries were removed.
    /// </summary>
    /// <remarks>This mutates the supplied cargo object.</remarks>
    public int RemoveInvalidCargo(CargoAll cargo)
    {
        if (cargo == null)
            return 0;

        var before = CountCargo(cargo);
        NormalizeCargo(cargo);
        return before - CountCargo(cargo);
    }

    /// <summary>
    /// Reduces stock cargo mass to a maximum using stock <see cref="CargoAll.ChangeResourcesMassToLimit"/>.
    /// </summary>
    /// <remarks>This mutates the supplied cargo object.</remarks>
    public bool CapCargoToMass(CargoAll cargo, double maxMass)
    {
        if (cargo == null || maxMass < 0.0)
            return false;

        if (cargo.CargoCurrent <= maxMass)
            return true;

        cargo.ChangeResourcesMassToLimit((int)Math.Floor(maxMass));
        return cargo.CargoCurrent <= maxMass;
    }

    /// <summary>
    /// Removes mass from regular non-fuel resource cargo until the requested amount is removed or cargo runs out.
    /// </summary>
    /// <remarks>This mutates the supplied cargo object and removes empty cargo rows from the normal cargo list.</remarks>
    public double ReduceNonFuelResourceCargo(CargoAll cargo, ResourceDefinition fuelResource, double amountToRemove)
    {
        if (cargo?.listCargo == null || amountToRemove <= 0.0)
            return 0.0;

        var removed = 0.0;
        for (var i = cargo.listCargo.Count - 1; i >= 0 && removed < amountToRemove; i--)
        {
            var item = cargo.listCargo[i];
            if (!IsRegularResourceCargo(item) || item.resourceType == fuelResource)
                continue;

            var take = Math.Min(item.cargoMass, amountToRemove - removed);
            item.cargoMass -= take;
            removed += take;
            if (item.cargoMass <= 0.0)
                cargo.listCargo.RemoveAt(i);
        }

        if (removed > 0.0)
            cargo.InvokeFreeSpaceChange();
        return removed;
    }

    /// <summary>
    /// Reads total fuel need from stock <see cref="PMMissionParameter.AllFuelNeed"/>.
    /// </summary>
    public double GetRequiredFuel(PMMissionParameter parameter)
    {
        return parameter?.AllFuelNeed ?? 0.0;
    }

    /// <summary>
    /// Reads optimal fuel need from stock <see cref="PMMissionParameter.OptimalFuelNeed"/>.
    /// </summary>
    public double GetOptimalFuel(PMMissionParameter parameter)
    {
        return parameter?.OptimalFuelNeed ?? 0.0;
    }

    /// <summary>
    /// Reads loaded fuel from the parameter cargo's special fuel item.
    /// </summary>
    public double GetLoadedFuel(PMMissionParameter parameter)
    {
        return parameter?.CargoAll?.cargoFuel?.cargoMass ?? 0.0;
    }

    /// <summary>
    /// Reads potential fuel from the parameter cargo's special fuel item.
    /// </summary>
    public double GetPotentialFuel(PMMissionParameter parameter)
    {
        return parameter?.CargoAll?.cargoFuel?.cargoMassPotencjal ?? 0.0;
    }

    /// <summary>
    /// Sets loaded fuel on the parameter cargo's special fuel item, creating that item if needed.
    /// </summary>
    /// <remarks>This mutates <c>parameter.CargoAll</c>. It does not buy or remove stock resources.</remarks>
    public void SetLoadedFuel(PMMissionParameter parameter, double amount)
    {
        var fuel = EnsureFuelCargo(parameter?.CargoAll);
        if (fuel == null)
            return;

        fuel.cargoMass = Math.Max(0.0, amount);
        _log?.Verbose("sdk.missionPlanning", $"loadout-fuel-set loaded={fuel.cargoMass:0.##} route=\"{parameter?.Start?.ObjectName ?? "null"}->{parameter?.Target?.ObjectName ?? "null"}\"");
    }

    /// <summary>
    /// Sets potential fuel on the parameter cargo's special fuel item, creating that item if needed.
    /// </summary>
    /// <remarks>This mutates <c>parameter.CargoAll</c>.</remarks>
    public void SetPotentialFuel(PMMissionParameter parameter, double amount)
    {
        var fuel = EnsureFuelCargo(parameter?.CargoAll);
        if (fuel == null)
            return;

        fuel.cargoMassPotencjal = Math.Max(0.0, amount);
    }

    /// <summary>
    /// Configures the special fuel cargo as reserve propellant for a mission parameter.
    /// </summary>
    /// <remarks>This mutates <c>parameter.CargoAll.cargoFuel</c> and can disable stock reduce-to-minimum behavior.</remarks>
    public bool ConfigureReservePropellant(PMMissionParameter parameter, ResourceDefinition fuelResource, double targetPropellant, bool disableReduceFuelToMinimum = true)
    {
        var fuel = EnsureFuelCargo(parameter?.CargoAll);
        if (fuel == null || parameter == null || fuelResource == null || targetPropellant <= 0.0)
            return false;

        fuel.objectInfo = parameter.Start;
        fuel.resourceTypeType = EResourceTypeType.resorces;
        fuel.resourceType = fuelResource;
        fuel.cargoMassPotencjal = Math.Max(0.0, targetPropellant);
        if (fuel.cargoMass > fuel.cargoMassPotencjal)
            fuel.cargoMass = fuel.cargoMassPotencjal;
        if (disableReduceFuelToMinimum)
            parameter.ReduceFuelToMinimum = false;
        return true;
    }

    /// <summary>
    /// Ensures loaded fuel is at least the requested amount.
    /// </summary>
    /// <remarks>This may mutate <c>parameter.CargoAll</c>.</remarks>
    public void EnsureMinimumFuel(PMMissionParameter parameter, double amount)
    {
        if (GetLoadedFuel(parameter) < amount)
            SetLoadedFuel(parameter, amount);
    }

    /// <summary>
    /// Caps loaded fuel to potential fuel when potential fuel is set.
    /// </summary>
    /// <remarks>This mutates <c>parameter.CargoAll</c> when loaded fuel exceeds potential fuel.</remarks>
    public void CapFuelToPotential(PMMissionParameter parameter)
    {
        var fuel = EnsureFuelCargo(parameter?.CargoAll);
        if (fuel == null)
            return;

        if (fuel.cargoMassPotencjal >= 0.0 && fuel.cargoMass > fuel.cargoMassPotencjal)
            fuel.cargoMass = fuel.cargoMassPotencjal;
    }

    /// <summary>
    /// Returns required fuel minus currently loaded fuel, never below zero.
    /// </summary>
    public double GetFuelShortfall(PMMissionParameter parameter)
    {
        if (parameter == null)
            return 0.0;

        return Math.Max(0.0, parameter.AllFuelNeed - GetLoadedFuel(parameter));
    }

    /// <summary>
    /// Adds fuel as ordinary resource cargo sourced from the mission start object.
    /// </summary>
    /// <remarks>This mutates <c>parameter.CargoAll</c>. It does not load the special fuel cargo item.</remarks>
    public Cargo StageFuelAsCargo(PMMissionParameter parameter, double amount)
    {
        if (parameter?.CargoAll == null)
            return null;

        return AddResourceCargo(parameter.CargoAll, GetFuelResource(), amount, parameter.Start);
    }

    /// <summary>
    /// Checks whether the source object currently has enough resources for the cargo using stock <see cref="ObjectInfoData.CheckResources(CargoAll)"/>.
    /// </summary>
    public bool CheckCargoAvailable(ObjectInfo source, Company company, CargoAll cargo)
    {
        if (source == null || company == null || cargo == null)
            return false;

        return source.GetObjectInfoData(company)?.CheckResources(cargo) ?? false;
    }

    /// <summary>
    /// Returns available mass of a resource at a source object using stock <see cref="ObjectInfoData.CheckResources(ResourceDefinition)"/>.
    /// </summary>
    public double GetAvailableResource(ObjectInfo source, Company company, ResourceDefinition resource)
    {
        if (source == null || company == null || resource == null)
            return 0.0;

        return source.GetObjectInfoData(company)?.CheckResources(resource) ?? 0.0;
    }

    /// <summary>
    /// Compares cargo resource demand against source availability and returns per-resource shortfalls.
    /// </summary>
    public List<SdkResourceShortfall> GetResourceShortfalls(ObjectInfo source, Company company, CargoAll cargo)
    {
        var result = new List<SdkResourceShortfall>();
        if (source == null || company == null || cargo == null)
            return result;

        var demand = BuildResourceDemand(cargo);
        foreach (var pair in demand)
        {
            var available = GetAvailableResource(source, company, pair.Key);
            if (available < pair.Value)
            {
                result.Add(new SdkResourceShortfall
                {
                    Resource = pair.Key,
                    Required = pair.Value,
                    Available = available,
                    Shortfall = pair.Value - available
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Returns stock spacecraft dry mass.
    /// </summary>
    public double GetSpacecraftDryMass(ISpacecraftInfo spacecraft)
    {
        return spacecraft?.GetMass() ?? 0.0;
    }

    /// <summary>
    /// Returns stock cargo capacity for a spacecraft type and company.
    /// </summary>
    public double GetSpacecraftCargoCapacity(ISpacecraftInfo spacecraft, Company company)
    {
        return spacecraft?.GetTypeSpaceCraft()?.GetCargoCapacity(company) ?? 0.0;
    }

    /// <summary>
    /// Returns launch vehicle payload capacity for a start object and company using stock launch-vehicle type rules.
    /// </summary>
    public double GetLaunchVehiclePayload(ILaunchVehicleInfo launchVehicle, ObjectInfo start, Company company)
    {
        return launchVehicle?.GetLaunchVehicleType()?.MaxPayloadOnThisObject(start, company) ?? 0.0;
    }

    /// <summary>
    /// Checks whether a launch vehicle can carry the spacecraft plus cargo at the start object.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="GetLaunchVehiclePayload"/> when it returns a positive capacity, otherwise falls back
    /// to stock <see cref="LaunchVehicleType.CheckMaximumPayload"/>.
    /// </remarks>
    public bool CheckLaunchVehiclePayload(ILaunchVehicleInfo launchVehicle, CargoAll cargo, ISpacecraftInfo spacecraft, ObjectInfo start, Company company)
    {
        if (launchVehicle == null || cargo == null || spacecraft == null)
            return false;

        var type = launchVehicle.GetLaunchVehicleType();
        if (type == null)
            return false;

        var maxPayload = GetLaunchVehiclePayload(launchVehicle, start, company);
        if (maxPayload > 0.0)
            return maxPayload >= cargo.CargoCurrent + spacecraft.GetMass();
        return type.CheckMaximumPayload(cargo, spacecraft);
    }

    /// <summary>
    /// Returns the stock fuel resource definition by scriptable-object ID.
    /// </summary>
    public ResourceDefinition GetFuelResource()
    {
        return SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance?.AllResourceDefinitions?.GetByID("id_resource_fuel");
    }

    /// <summary>
    /// Formats regular resource cargo and special loaded fuel cargo for compact diagnostics.
    /// </summary>
    public string FormatCargo(CargoAll cargo)
    {
        if (cargo == null)
            return "null";

        var parts = new List<string>();
        AddFormattedCargo(cargo.listCargo, parts);
        AddFormattedCargo(cargo.listCargoToOrbit, parts, "orbit/");
        if (cargo.cargoFuel?.resourceType != null && cargo.cargoFuel.cargoMass > 0.0)
            parts.Add($"fuel/{cargo.cargoFuel.resourceType.ID}:{cargo.cargoFuel.cargoMass:0.#}");

        return parts.Count == 0 ? "empty" : string.Join(",", parts);
    }

    private Cargo CloneCargoItem(Cargo cargo, CargoAll owner, bool fuelSpecial, bool lifeSupportSpecial)
    {
        if (cargo == null)
            return null;

        return new Cargo(owner, fuelSpecial, lifeSupportSpecial)
        {
            lifeSupportValue = cargo.lifeSupportValue,
            cargoMassPotencjal = cargo.cargoMassPotencjal,
            cargoMass = cargo.cargoMass,
            crew = cargo.crew,
            crewValue = cargo.crewValue,
            sendOnOrbitWhenAtoBtoC = cargo.sendOnOrbitWhenAtoBtoC,
            fromAtoBtoC = cargo.fromAtoBtoC,
            massUseInFly = cargo.massUseInFly,
            resourceType = cargo.resourceType,
            SourceModule = cargo.SourceModule,
            moduleData = cargo.moduleData,
            objectInfo = cargo.objectInfo,
            resourceTypeType = cargo.resourceTypeType,
            OrbitCargo = cargo.OrbitCargo
        };
    }

    private void CloneCargoList(IEnumerable<Cargo> source, ICollection<Cargo> target, CargoAll owner)
    {
        if (source == null || target == null)
            return;

        foreach (var item in source)
        {
            var clone = CloneCargoItem(item, owner);
            if (clone != null)
                target.Add(clone);
        }
    }

    private Cargo EnsureFuelCargo(CargoAll cargo)
    {
        if (cargo == null)
            return null;

        if (cargo.cargoFuel == null)
        {
            cargo.cargoFuel = new Cargo(cargo, _isCargoFuelSpecial: true)
            {
                resourceTypeType = EResourceTypeType.resorces,
                resourceType = GetFuelResource()
            };
        }

        return cargo.cargoFuel;
    }

    private static void AddResourceMass(IEnumerable<Cargo> list, ResourceDefinition resource, ref double total)
    {
        if (list == null)
            return;

        foreach (var item in list)
        {
            if (item?.resourceType == resource)
                total += item.cargoMass;
        }
    }

    private static void AddRegularResourceItems(IEnumerable<Cargo> source, ICollection<Cargo> target)
    {
        if (source == null || target == null)
            return;

        foreach (var item in source)
        {
            if (IsRegularResourceCargo(item))
                target.Add(item);
        }
    }

    private static bool IsRegularResourceCargo(Cargo cargo)
    {
        return cargo != null
            && cargo.resourceTypeType == EResourceTypeType.resorces
            && cargo.resourceType != null
            && cargo.cargoMass > 0.0;
    }

    private static Cargo FindResourceCargo(IEnumerable<Cargo> list, ResourceDefinition resource)
    {
        if (list == null)
            return null;

        foreach (var item in list)
        {
            if (item?.resourceType == resource)
                return item;
        }

        return null;
    }

    private static void AddFormattedCargo(IEnumerable<Cargo> list, ICollection<string> parts, string prefix = null)
    {
        if (list == null || parts == null)
            return;

        foreach (var cargo in list)
        {
            if (!IsRegularResourceCargo(cargo))
                continue;
            parts.Add($"{prefix}{cargo.resourceType.ID}:{cargo.cargoMass:0.#}");
        }
    }

    private static int CountCargo(CargoAll cargo)
    {
        return (cargo.listCargo?.Count ?? 0)
            + (cargo.listCargoToOrbit?.Count ?? 0)
            + (cargo.listCargoGravityAssists?.Count ?? 0);
    }

    private Dictionary<ResourceDefinition, double> BuildResourceDemand(CargoAll cargo)
    {
        var result = new Dictionary<ResourceDefinition, double>();
        AddDemand(cargo.listCargo, result);
        AddDemand(cargo.listCargoToOrbit, result);
        AddDemand(cargo.listCargoGravityAssists, result);
        if (cargo.cargoFuel?.resourceType != null && cargo.cargoFuel.cargoMass > 0.0)
            AddDemand(result, cargo.cargoFuel.resourceType, cargo.cargoFuel.cargoMass);
        return result;
    }

    private static void AddDemand(IEnumerable<Cargo> list, IDictionary<ResourceDefinition, double> demand)
    {
        if (list == null)
            return;

        foreach (var item in list)
        {
            if (item?.resourceType != null && item.resourceTypeType == EResourceTypeType.resorces && item.cargoMass > 0.0)
                AddDemand(demand, item.resourceType, item.cargoMass);
        }
    }

    private static void AddDemand(IDictionary<ResourceDefinition, double> demand, ResourceDefinition resource, double mass)
    {
        if (!demand.ContainsKey(resource))
            demand[resource] = 0.0;
        demand[resource] += mass;
    }
}

/// <summary>
/// Per-resource shortage record returned by <see cref="SdkMissionLoadout.GetResourceShortfalls"/>.
/// </summary>
public sealed class SdkResourceShortfall
{
    /// <summary>Resource definition that is short.</summary>
    public ResourceDefinition Resource { get; set; }
    /// <summary>Required resource mass.</summary>
    public double Required { get; set; }
    /// <summary>Available resource mass at the source.</summary>
    public double Available { get; set; }
    /// <summary>Required minus available resource mass.</summary>
    public double Shortfall { get; set; }
}

/// <summary>
/// Context raised after stock creates cargo for a cyclical mission planning attempt.
/// </summary>
public sealed class SdkCycleCargoCreatedContext
{
    /// <summary>Created stock cargo.</summary>
    public CargoAll Cargo { get; set; }
    /// <summary>Stock cargo start mode.</summary>
    public ECargoStart CargoStart { get; set; }
    /// <summary>Stock cycle that requested the cargo.</summary>
    public CycleMissionsData Cycle { get; set; }
    /// <summary>Start object used by stock cargo creation.</summary>
    public ObjectInfo StartObject { get; set; }
    /// <summary>Spacecraft used by stock cargo creation.</summary>
    public Spacecraft Spacecraft { get; set; }
    /// <summary>Launch vehicle selected by stock, when any.</summary>
    public LaunchVehicle LaunchVehicle { get; set; }
    /// <summary>Whether all requested resource cargo existed on the source object.</summary>
    public bool AllResourceOnPlanet { get; set; }
    /// <summary>Stock load-limit argument.</summary>
    public double? LoadLimit { get; set; }
    /// <summary>Number of spacecraft in the stock cycle.</summary>
    public int SpacecraftCount { get; set; }
    /// <summary>Whether stock was adding supply for the attempt.</summary>
    public bool AddSupply { get; set; }
    /// <summary>Stock mission length argument, when any.</summary>
    public TimeSpan? MissionLength { get; set; }
}

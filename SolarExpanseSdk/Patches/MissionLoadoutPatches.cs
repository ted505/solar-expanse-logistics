using System;
using CustomUpdate;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.PlanMissionElements;
using HarmonyLib;
using SolarExpanseSdk.Core;
using SolarExpanseSdk.Services;

namespace SolarExpanseSdk.Patches;

[HarmonyPatch]
internal static class MissionLoadoutPatches
{
    [HarmonyPatch(typeof(ObjectInfoData), nameof(ObjectInfoData.CreatedCargoToTakeNormal))]
    [HarmonyPostfix]
    private static void CreatedCargoToTakeNormalPostfix(CargoAll __result, ECargoStart cargoStart,
        CycleMissionsData cycleMissionsData, ObjectInfo startObject, Spacecraft sc, LaunchVehicle lv,
        bool allResourceOnPlanet, double? loadLimit2, int countSC, bool addSupply, TimeSpan? missionLenght)
    {
        SolarSdk.MissionLoadout.RaiseCargoCreatedForCycle(new SdkCycleCargoCreatedContext
        {
            Cargo = __result,
            CargoStart = cargoStart,
            Cycle = cycleMissionsData,
            StartObject = startObject,
            Spacecraft = sc,
            LaunchVehicle = lv,
            AllResourceOnPlanet = allResourceOnPlanet,
            LoadLimit = loadLimit2,
            SpacecraftCount = countSC,
            AddSupply = addSupply,
            MissionLength = missionLenght
        });
    }
}

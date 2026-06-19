using System.Linq;
using HarmonyLib;
using Game;
using Game.UI.Windows.Elements.PlanMissionElements;
using LogisticsModSdk.Logic;
using Manager;

namespace LogisticsModSdk.Patches;

[HarmonyPatch]
internal static class SaveLoadPatches
{
    [HarmonyPatch(typeof(LoadSaveManager), "ExtractAllFromSaveData")]
    [HarmonyPrefix]
    private static void ExtractAllPrefix()
    {
        ResetLoadState();
    }

    [HarmonyPatch(typeof(LoadSaveManager), "SaveToFile", new[] { typeof(string) })]
    [HarmonyPostfix]
    private static void SaveToFilePostfix(string saveName)
    {
        Data.LogisticsPersistence.Save(saveName);
    }

    [HarmonyPatch(typeof(LoadSaveManager), "ExtractAllFromSaveData")]
    [HarmonyPostfix]
    private static void ExtractAllPostfix()
    {
        var saveName = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance?.LastSaveName;
        if (!string.IsNullOrEmpty(saveName))
            Data.LogisticsPersistence.Load(saveName);

        ReconcileAfterLoad();
    }

    private static void ResetLoadState()
    {
        _pendingPostLoadTrigger = false;
        Data.LogisticsNetwork.ClearAll();
        LogisticsObserver.ResetRuntimeState();
        TimeControllerPatches.ResetRuntimeFlags();
        SpaceCraftCyclicalMissionControllerPatches.ResetNotifiedCycleRoutes();
    }

    private static void ReconcileAfterLoad()
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var cm = MonoBehaviourSingleton<CycleMissionManager>.Instance;
        if (player == null || cm == null) return;

        MatchCyclesToRequests(player, cm);
        LogisticsObserver.CleanupCompletedLogisticsMissionTrajectories(player);
        _pendingPostLoadTrigger = true;
    }

    public static bool PendingPostLoadTrigger
    {
        get => _pendingPostLoadTrigger;
        set => _pendingPostLoadTrigger = value;
    }
    private static bool _pendingPostLoadTrigger;

    private static void MatchCyclesToRequests(Company player, CycleMissionManager cm)
    {
        foreach (var requesterOI in Data.LogisticsNetwork.GetAllObjects())
        {
            var reqData = Data.LogisticsNetwork.Get(requesterOI);
            if (reqData == null) continue;

            foreach (var cmd in cm.GetAllCycleMission(player))
            {
                if (cmd.CheckComplete()) continue;
                if (cmd.B != requesterOI) continue;
                if (!cmd.customNameFromPlanMission.StartsWith("[LOGI]")) continue;
                if (cmd.cargoAllStart?.Tab == null) continue;

                foreach (var tabRes in cmd.cargoAllStart.Tab)
                {
                    foreach (var req in reqData.requests)
                    {
                        if (req.status != Data.LogisticsRequestStatus.Pending
                            && req.status != Data.LogisticsRequestStatus.InProgress)
                            continue;
                        if (req.ResourceDefinition == tabRes)
                        {
                            req.status = Data.LogisticsRequestStatus.InProgress;
                            if (req.relayFinalTargetObjectId <= 0)
                                req.relayFinalTargetObjectId = requesterOI.id;
                            LogisticsObserver.Log($"Reconciled cycle: {cmd.A?.ObjectName} -> {cmd.B?.ObjectName} ({tabRes.ID})");
                            break;
                        }
                    }
                }
            }
        }
    }
}

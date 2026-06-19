using Game.ObjectInfoDataScripts;
using HarmonyLib;
using Manager;
using SolarExpanseSdk.Core;

namespace SolarExpanseSdk.Patches;

[HarmonyPatch]
internal static class MarketPatches
{
    [HarmonyPatch(typeof(MarketOfferManager), nameof(MarketOfferManager.AddOffer))]
    [HarmonyPrefix]
    private static void AddOfferPrefix(Offer offer, ref bool suppresssNotification)
    {
        suppresssNotification = SolarSdk.Market.ApplyOfferNotificationPolicy(offer, suppresssNotification);
    }
}

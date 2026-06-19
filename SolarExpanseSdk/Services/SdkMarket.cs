using System;
using Game.ObjectInfoDataScripts;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Market offer hooks for mods that need to observe offers or suppress noisy offer notifications.
/// </summary>
public sealed class SdkMarket
{
    private SdkLogging _log;

    /// <summary>
    /// Raised before stock adds a market offer. Return true to suppress the stock notification.
    /// </summary>
    public event Func<SdkMarketOfferContext, bool?> ShouldSuppressOfferNotification;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Applies market offer notification policy and returns the new suppression value.
    /// </summary>
    public bool ApplyOfferNotificationPolicy(Offer offer, bool currentlySuppressed)
    {
        var context = new SdkMarketOfferContext
        {
            Offer = offer,
            SuppressNotification = currentlySuppressed
        };

        if (currentlySuppressed || ShouldSuppressOfferNotification == null)
            return currentlySuppressed;

        foreach (Func<SdkMarketOfferContext, bool?> handler in ShouldSuppressOfferNotification.GetInvocationList())
        {
            try
            {
                var result = handler(context);
                if (result == true || context.SuppressNotification)
                {
                    context.SuppressNotification = true;
                    _log?.Verbose("sdk.market", $"offer-notification-suppressed where={offer?.WhereOffer?.ObjectName ?? "null"} rd={offer?.Rd?.ID ?? "null"} buySell={offer?.BuySell.ToString() ?? "null"} reason={context.Reason ?? "subscriber"}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("sdk.market", $"offer notification handler failed: {ex.GetType().Name}: {ex.Message}");
                Core.SolarSdk.Diagnostics.WriteSnapshotOnce("market-offer-handler-error", ex.GetType().Name);
            }
        }

        return context.SuppressNotification;
    }
}

/// <summary>
/// Context for market offer notification policy.
/// </summary>
public sealed class SdkMarketOfferContext
{
    /// <summary>Stock market offer being added.</summary>
    public Offer Offer { get; set; }
    /// <summary>Set true to suppress stock notification for this offer.</summary>
    public bool SuppressNotification { get; set; }
    /// <summary>Optional diagnostic reason.</summary>
    public string Reason { get; set; }
}

using System;
using ScriptableObjectScripts;

namespace LogisticsModSdk.Data;

internal static class LogisticsResourceFilter
{
    public static bool IsSupported(ResourceDefinition rd)
    {
        if (rd == null)
            return false;

        if (rd.ResourceType == ResourceDefinition.EResourceType.Human)
            return false;

        var id = rd.ID ?? string.Empty;
        return id.IndexOf("consumer", StringComparison.OrdinalIgnoreCase) < 0
            && id.IndexOf("antimatter", StringComparison.OrdinalIgnoreCase) < 0;
    }
}

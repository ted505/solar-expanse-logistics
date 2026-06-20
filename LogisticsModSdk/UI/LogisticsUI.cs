using System;
using System.Collections.Generic;
using System.Linq;
using CustomUpdate;
using Extensions;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Windows;
using HarmonyLib;
using Language;
using LogisticsModSdk.Logic;
using Manager;
using ScriptableObjectScripts;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LogisticsModSdk.UI;

public class LogisticsUI : MonoBehaviour
{
    private const int SectionIndexSpacecraft = 3;
    private const int SectionIndexLaunchVehicle = 4;
    private const int SectionIndexPlannedMission = 5;

    private static readonly Color RowBgColor = new Color(0.12f, 0.12f, 0.13f, 0.96f);
    private static readonly Color RowBgMutedColor = new Color(0.1f, 0.1f, 0.11f, 0.92f);
    private static readonly Color AccentButtonColor = new Color(0.24f, 0.29f, 0.36f, 0.98f);
    private static readonly Color ConfirmButtonColor = new Color(0.23f, 0.33f, 0.25f, 0.98f);
    private static readonly Color BackButtonColor = new Color(0.24f, 0.22f, 0.24f, 0.98f);
    private static readonly Color RemoveButtonColor = new Color(0.55f, 0.15f, 0.15f, 0.98f);
    private static readonly Color CountButtonColor = new Color(0.25f, 0.28f, 0.33f, 0.98f);
    private static readonly Color CountButtonPositiveColor = new Color(0.24f, 0.31f, 0.27f, 0.98f);
    private static readonly Color ToggleOnRowColor = new Color(0.12f, 0.20f, 0.14f, 0.96f);
    private static readonly Color ToggleOffRowColor = new Color(0.14f, 0.14f, 0.16f, 0.96f);
    private static readonly Color SubtleTextColor = new Color(0.75f, 0.75f, 0.77f, 1f);

    private List<LogisticsSection> _sections = new List<LogisticsSection>();
    private ObjectInfoData _currentData;
    private ObjectInfo _currentObjectInfo;
    private ObjectInfoWindow _objectInfoWindow;
    private RectTransform _parentRt;
    private bool _built;
    private TMP_FontAsset _font;
    private Material _fontMaterial;
    private RuntimeUiStyle _runtimeStyle = new RuntimeUiStyle();

    private HashSet<string> _expandedQuotaKeys = new HashSet<string>();
    private HashSet<string> _expandedGetRequestKeys = new HashSet<string>();
    private HashSet<string> _expandedSendProviderKeys = new HashSet<string>();

    private LogisticsSection _getSection;
    private LogisticsSection _sendSection;
    private LogisticsSection _scSection;
    private LogisticsSection _lvSection;

    private sealed class RuntimeUiStyle
    {
        public TMP_FontAsset Font;
        public float RowFontSize = 13f;
        public float HeaderFontSize = 15f;
        public float HeaderHeight = 50f;
        public float RowHeight = 28f;
        public Color HeaderTextColor = new Color(0.604f, 0.604f, 0.604f, 1f);
        public Color HeaderDividerColor = new Color(0.425f, 0.425f, 0.425f, 1f);
        public Color HeaderBackgroundColor = new Color(0f, 0f, 0f, 0f);
        public Color RowBackgroundColor = RowBgColor;
        public Color RowTextColor = SubtleTextColor;
        public Color ActionButtonColor = LogisticsUI.AccentButtonColor;
        public Color ConfirmButtonColor = LogisticsUI.ConfirmButtonColor;
        public Color BackButtonColor = LogisticsUI.BackButtonColor;
        public Color RemoveButtonColor = LogisticsUI.RemoveButtonColor;
        public Color SmallButtonColor = LogisticsUI.CountButtonColor;
        public Color SmallButtonPositiveColor = LogisticsUI.CountButtonPositiveColor;
        public Color ToggleOnColor = LogisticsUI.ToggleOnRowColor;
        public Color ToggleOffColor = LogisticsUI.ToggleOffRowColor;
        public ColorBlock HeaderButtonColors;
        public bool HasHeaderButtonColors;
    }

    private void Start()
    {
        try
        {
            _objectInfoWindow = GetComponent<ObjectInfoWindow>();
            if (_objectInfoWindow == null) { LogisticsObserver.LogError("No ObjectInfoWindow"); return; }

            _font = FindFont();
            if (_font == null) { LogisticsObserver.LogError("No TMP font found!"); return; }

            var oics = _objectInfoWindow.GetComponent<ObjectInfoCollapseSections>();
            if (oics == null || oics.uiLists == null || oics.uiLists.Count == 0)
            { LogisticsObserver.LogError("No ObjectInfoCollapseSections"); return; }

            var sectionParent = oics.uiLists[0].transform;
            _parentRt = sectionParent.parent as RectTransform;
            float sectionWidth = (sectionParent as RectTransform).sizeDelta.x;
            if (sectionWidth <= 0) sectionWidth = _parentRt.rect.width;

            var styleButton = oics.expandButtons != null && oics.expandButtons.Count > SectionIndexPlannedMission ? oics.expandButtons[SectionIndexPlannedMission] : null;
            var styleIcon = oics.buttonsIcons != null && oics.buttonsIcons.Count > SectionIndexPlannedMission ? oics.buttonsIcons[SectionIndexPlannedMission] : null;
            CaptureRuntimeStyle(oics, styleButton);

            _getSection = new LogisticsSection(_parentRt, FormatSectionTitle("GET", "Import Resources"), _font, sectionWidth,
                styleButton, styleIcon, oics.spriteExpand, oics.spriteCollapse,
                _runtimeStyle.HeaderHeight, _runtimeStyle.HeaderFontSize, _runtimeStyle.HeaderBackgroundColor,
                _runtimeStyle.HeaderTextColor, _runtimeStyle.HeaderDividerColor, new Color(0f, 0f, 0f, 0f),
                _runtimeStyle.HasHeaderButtonColors ? _runtimeStyle.HeaderButtonColors : null, fontMaterial: _fontMaterial);
            _sections.Add(_getSection);

            _sendSection = new LogisticsSection(_parentRt, FormatSectionTitle("SEND", "Export Resources"), _font, sectionWidth,
                styleButton, styleIcon, oics.spriteExpand, oics.spriteCollapse,
                _runtimeStyle.HeaderHeight, _runtimeStyle.HeaderFontSize, _runtimeStyle.HeaderBackgroundColor,
                _runtimeStyle.HeaderTextColor, _runtimeStyle.HeaderDividerColor, new Color(0f, 0f, 0f, 0f),
                _runtimeStyle.HasHeaderButtonColors ? _runtimeStyle.HeaderButtonColors : null, fontMaterial: _fontMaterial);
            _sections.Add(_sendSection);

            _scSection = new LogisticsSection(_parentRt, FormatSectionTitle("SPACECRAFT", "Logistics Vessels"), _font, sectionWidth,
                styleButton, styleIcon, oics.spriteExpand, oics.spriteCollapse,
                _runtimeStyle.HeaderHeight, _runtimeStyle.HeaderFontSize, _runtimeStyle.HeaderBackgroundColor,
                _runtimeStyle.HeaderTextColor, _runtimeStyle.HeaderDividerColor, new Color(0f, 0f, 0f, 0f),
                _runtimeStyle.HasHeaderButtonColors ? _runtimeStyle.HeaderButtonColors : null, fontMaterial: _fontMaterial);
            _sections.Add(_scSection);

            _lvSection = new LogisticsSection(_parentRt, FormatSectionTitle("LAUNCH VEHICLE", "Surface Shuttles"), _font, sectionWidth,
                styleButton, styleIcon, oics.spriteExpand, oics.spriteCollapse,
                _runtimeStyle.HeaderHeight, _runtimeStyle.HeaderFontSize, _runtimeStyle.HeaderBackgroundColor,
                _runtimeStyle.HeaderTextColor, _runtimeStyle.HeaderDividerColor, new Color(0f, 0f, 0f, 0f),
                _runtimeStyle.HasHeaderButtonColors ? _runtimeStyle.HeaderButtonColors : null, fontMaterial: _fontMaterial);
            _sections.Add(_lvSection);

            _built = true;
            TrySyncFromWindow(force: true);
            RefreshAllSections();
        }
        catch (System.Exception ex) { LogisticsObserver.LogError("Start Exception: " + ex); }
    }

    private void OnEnable()
    {
        TrySyncFromWindow(force: true);
    }

    private void LateUpdate()
    {
        if (!_built || !isActiveAndEnabled) return;
        TrySyncFromWindow(force: false);
    }

    private TMP_FontAsset FindFont()
    {
        // Donor approach: sample font + material from ObjectInfoWindow's own TMP elements
        if (_objectInfoWindow != null)
        {
            var donor = _objectInfoWindow.GetComponentInChildren<TextMeshProUGUI>(true);
            if (donor?.font != null)
            {
                _fontMaterial = donor.fontSharedMaterial;
                return donor.font;
            }
        }
        // Last resort: first active TMP
        foreach (var tmp in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            if (tmp.font != null && tmp.isActiveAndEnabled)
            {
                _fontMaterial = tmp.fontSharedMaterial;
                return tmp.font;
            }
        return null;
    }

    private void CaptureRuntimeStyle(ObjectInfoCollapseSections oics, Button headerButton)
    {
        _runtimeStyle.Font = _font;
        if (headerButton != null)
        {
            _runtimeStyle.HeaderButtonColors = headerButton.colors;
            _runtimeStyle.HasHeaderButtonColors = true;
        }

        TryCaptureHeaderTypography(oics, SectionIndexPlannedMission, "PLANNED");
        TryCaptureLaunchListRowStyle(_objectInfoWindow?.launchVehicleList);
        LogCapturedSectionStyle(oics, SectionIndexSpacecraft, "SPACECRAFT", _objectInfoWindow?.rocketList);
        LogCapturedSectionStyle(oics, SectionIndexLaunchVehicle, "LAUNCH VEHICLES", _objectInfoWindow?.launchVehicleList);
        LogCapturedSectionStyle(oics, SectionIndexPlannedMission, "PLANNED MISSIONS", _objectInfoWindow?.missionsList);
    }

    private void TryCaptureHeaderTypography(ObjectInfoCollapseSections oics, int sectionIndex, string headerHint)
    {
        var button = oics?.expandButtons != null && sectionIndex >= 0 && sectionIndex < oics.expandButtons.Count
            ? oics.expandButtons[sectionIndex]
            : null;
        if (button == null) return;

        foreach (var tmp in button.transform.parent.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (tmp == null) continue;
            if (tmp.text == null || tmp.text.IndexOf(headerHint, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            _runtimeStyle.Font ??= tmp.font;
            _runtimeStyle.HeaderFontSize = tmp.fontSize;
            _runtimeStyle.HeaderTextColor = tmp.color;
            var rt = button.transform.parent as RectTransform;
            if (rt != null && rt.rect.height >= 20f)
                _runtimeStyle.HeaderHeight = rt.rect.height;
            break;
        }
    }

    private void TryCaptureLaunchListRowStyle(MonoBehaviour donorList)
    {
        if (donorList == null) return;

        foreach (var btn in donorList.GetComponentsInChildren<Button>(true))
        {
            if (btn == null || btn.gameObject == donorList.gameObject) continue;
            var rt = btn.transform as RectTransform;
            if (rt == null || rt.rect.height < 40f) continue;

            var bg = btn.GetComponent<Image>();
            if (bg != null)
                _runtimeStyle.RowBackgroundColor = bg.color;

            var tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                _runtimeStyle.Font ??= tmp.font;
                _runtimeStyle.RowFontSize = tmp.fontSize;
                _runtimeStyle.RowTextColor = tmp.color;
            }
            _runtimeStyle.RowHeight = rt.rect.height;

            break;
        }
    }

    private void LogCapturedSectionStyle(ObjectInfoCollapseSections oics, int sectionIndex, string sectionName, MonoBehaviour donorList)
    {
        try
        {
            var button = oics?.expandButtons != null && sectionIndex >= 0 && sectionIndex < oics.expandButtons.Count
                ? oics.expandButtons[sectionIndex]
                : null;
            var headerTmp = button?.transform.parent.GetComponentsInChildren<TextMeshProUGUI>(true)
                .FirstOrDefault(tmp => tmp != null && tmp.text != null && tmp.text.IndexOf(sectionName.Split(' ')[0], System.StringComparison.OrdinalIgnoreCase) >= 0);

            var headerRect = button?.transform as RectTransform;
            var headerImage = button?.GetComponent<Image>();

            Button rowButton = null;
            TextMeshProUGUI rowTmp = null;
            Image rowImage = null;
            RectTransform rowRect = null;

            if (donorList != null)
            {
                foreach (var btn in donorList.GetComponentsInChildren<Button>(true))
                {
                    if (btn == null || btn.gameObject == donorList.gameObject) continue;
                    var rt = btn.transform as RectTransform;
                    if (rt == null || rt.rect.height < 30f) continue;
                    rowButton = btn;
                    rowRect = rt;
                    rowImage = btn.GetComponent<Image>();
                    rowTmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
                    break;
                }
            }

        }
        catch (System.Exception ex)
        {
            if (LogisticsObserver.VerboseLoggingEnabled)
                LogisticsObserver.LogWarning($"UISTYLE capture failed for {sectionName}: {ex.Message}");
        }
    }

    private string FormatSectionTitle(string primary, string secondary)
    {
        var subtitleColor = Color.Lerp(_runtimeStyle.HeaderTextColor, new Color(0.45f, 0.45f, 0.48f, _runtimeStyle.HeaderTextColor.a), 0.35f);
        var subtitleHex = ColorUtility.ToHtmlStringRGBA(subtitleColor);
        return $"{primary} <size=82%><color=#{subtitleHex}>— {secondary}</color></size>";
    }

    public void RefreshData(ObjectInfoData oid)
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (oid != null && player != null && oid.company != player)
        {
            _currentData = null;
            _currentObjectInfo = null;
            if (_built)
                ClearForNonPlayerCompany();
            return;
        }

        var newOi = oid?.ObjectInfo;
        var newName = newOi?.ObjectName ?? "NULL";
        var newId = newOi?.id ?? -1;
        var prevName = _currentObjectInfo?.ObjectName ?? "null";
        var prevId = _currentObjectInfo?.id ?? -1;
        LogisticsObserver.LogVerbose($"RefreshData: \"{newName}\" (id={newId}), _built={_built}, prev=\"{prevName}\" (id={prevId})");

        if (LogisticsObserver.VerboseLoggingEnabled && newOi != null && _currentObjectInfo != null && newId == prevId && newName != prevName)
            LogisticsObserver.LogWarning($"DIAG RefreshData: SAME id ({newId}) but DIFFERENT name! prev=\"{prevName}\" new=\"{newName}\"");

        if (newOi != null)
        {
            var dictData = Data.LogisticsNetwork.Get(newOi);
            if (dictData != null)
            {
                if (LogisticsObserver.VerboseLoggingEnabled)
                {
                var storedOiName = (dictData.ObjectInfo as ObjectInfo)?.ObjectName ?? "NULL";
                if (storedOiName != newName)
                    LogisticsObserver.LogWarning($"DIAG RefreshData: dict entry id={newId} has storedOI=\"{storedOiName}\" but incoming OI name=\"{newName}\" — MISMATCH!");
                LogisticsObserver.LogVerbose($"DIAG RefreshData: dict data for id={newId}: {dictData.requests.Count}req {dictData.providers.Count}prov");
                }
            }
            else
            {
                LogisticsObserver.LogVerbose($"DIAG RefreshData: NO dict entry for id={newId} name=\"{newName}\"");
            }
        }

        _currentData = oid;
        _currentObjectInfo = newOi;
        if (!_built) return;
        RefreshAllSections();
    }

    private void TrySyncFromWindow(bool force)
    {
        if (_objectInfoWindow == null)
            _objectInfoWindow = GetComponent<ObjectInfoWindow>();
        if (_objectInfoWindow == null) return;

        var liveData = _objectInfoWindow.ObjectInfoDataCurrent;
        var liveOi = liveData?.ObjectInfo;
        var liveId = liveOi?.id ?? -1;
        var currentId = _currentObjectInfo?.id ?? -1;
        var liveCompany = liveData?.company;
        var currentCompany = _currentData?.company;

        if (!force && liveId == currentId && liveCompany == currentCompany)
            return;

        LogisticsObserver.LogVerbose($"UI sync-from-window: force={force} live=\"{liveOi?.ObjectName ?? "NULL"}\"(id={liveId}) cached=\"{_currentObjectInfo?.ObjectName ?? "NULL"}\"(id={currentId})");
        RefreshData(liveData);
    }

    private void ClearForNonPlayerCompany()
    {
        foreach (var section in _sections)
            section.ClearContent();

        _getSection?.AddTextRow("Logistics are only available for the player company.", _font, 13f, new Color(0.55f, 0.55f, 0.6f, 1f));
        LayoutRebuilder.ForceRebuildLayoutImmediate(_parentRt);
    }

    private void RefreshAllSections()
    {
        if (_currentObjectInfo == null) return;
        BuildGetSection();
        BuildSendSection();
        BuildSCSection();
        BuildLVSection();
        LayoutRebuilder.ForceRebuildLayoutImmediate(_parentRt);
    }

    private void RebuildSectionLayout(LogisticsSection section)
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(section.ContentArea);
        LayoutRebuilder.ForceRebuildLayoutImmediate(_parentRt);
    }

    private void BuildGetSection()
    {
        _getSection.ClearContent();
        var data = Data.LogisticsNetwork.Get(_currentObjectInfo);
        var requestCount = data?.requests.Count ?? 0;
        LogisticsObserver.LogVerbose($"BuildGet for {_currentObjectInfo?.ObjectName}: {requestCount} requests");

        if (requestCount > 0)
        {
            for (int i = 0; i < data.requests.Count; i++)
            {
                var req = data.requests[i];
                var idx = i;
                var rd = req.ResourceDefinition;
                if (!Data.LogisticsResourceFilter.IsSupported(rd))
                    continue;
                var displayName = ResourceLabel(rd, req.resourceDef?.id);
                var displayStatus = LogisticsObserver.GetRequestDisplayStatus(req, _currentObjectInfo, rd);
                var statusStr = displayStatus.Label;
                var noteStr = !string.IsNullOrEmpty(displayStatus.Note) ? $" ({displayStatus.Note})" : "";
                var getExpandKey = $"{_currentObjectInfo?.id ?? -1}:get:{idx}:{rd?.ID ?? req.resourceDef?.id ?? "resource"}";
                var inboundStatuses = LogisticsObserver.GetAllShipsForGetRequest(_currentObjectInfo, rd);
                var hasInboundStatuses = inboundStatuses.Count > 0;
                var isGetExpanded = _expandedGetRequestKeys.Contains(getExpandKey);

                var row = MakeHLRow(_getSection.ContentArea, 28f, 4);
                var amountText = req.useMinimumAmount
                    ? $"target {FormatCompactAmount(req.requestedAmount)}, min {FormatCompactAmount(System.Math.Min(req.minimumAmount, req.requestedAmount))}"
                    : FormatCompactAmount(req.requestedAmount);
                var modeText = req.oneShot
                    ? (req.dispatchedAmount > 0
                        ? $" one-shot ({FormatCompactAmount(req.dispatchedAmount)}/{FormatCompactAmount(req.requestedAmount)} sent)"
                        : " one-shot")
                    : "";
                var autoBuyText = req.autoBuy ? $" auto-buy <= ${req.autoBuyMaxPrice:0.##}" : "";
                string netText;
                if (req.isDirect && req.directLinkedObjectId >= 0)
                {
                    var linkedOiDisp = Data.LogisticsNetwork.ResolveObjectById(req.directLinkedObjectId);
                    var linkedProv = Data.LogisticsNetwork.FindLinkedDirectProvider(req.directLinkedObjectId, rd, _currentObjectInfo?.id ?? -1);
                    var reserveText = linkedProv != null ? $" reserve: {FormatCompactAmount(linkedProv.minimumKeep)}" : "";
                    netText = $" <color=#88DDBB>[← {BodyLabel(linkedOiDisp)}{reserveText}]</color>";
                }
                else
                {
                    netText = req.networkId == Data.LogisticsNetwork.LocalSystemNetworkId ? " <color=#88BBDD>[Local]</color>"
                        : req.networkId > 0 ? $" <color=#88BBDD>[N{req.networkId}]</color>" : "";
                }
                var labelTmp = MakeTMP(row.transform, $"<b><color=#EEEEF0>{displayName}</color></b>: {amountText}{modeText}{autoBuyText}{netText}  [{statusStr}]{noteStr}", 13, StatusColor(displayStatus.State));
                labelTmp.enableWordWrapping = true;
                labelTmp.overflowMode = TextOverflowModes.Overflow;
                labelTmp.alignment = TextAlignmentOptions.MidlineLeft;
                var labelLe = labelTmp.gameObject.AddComponent<LayoutElement>();
                labelLe.flexibleWidth = 1f;
                labelLe.preferredWidth = 0f;
                {
                    var priTmp = MakeTMP(row.transform, $"<color={PriorityColor(req.priority)}>{PriorityLabel(req.priority)}</color>", 12, new Color(0.65f, 0.65f, 0.65f, 1f));
                    priTmp.alignment = TextAlignmentOptions.MidlineRight;
                    var priLe = priTmp.gameObject.AddComponent<LayoutElement>();
                    priLe.preferredWidth = 52f;
                    priLe.flexibleWidth = 0f;
                }
                if (hasInboundStatuses)
                {
                    AddSmallButton(row.transform, isGetExpanded ? "▼" : ">", _runtimeStyle.SmallButtonColor, () =>
                    {
                        if (_expandedGetRequestKeys.Contains(getExpandKey))
                            _expandedGetRequestKeys.Remove(getExpandKey);
                        else
                            _expandedGetRequestKeys.Add(getExpandKey);
                        BuildGetSection();
                        RebuildSectionLayout(_getSection);
                    }, width: 28f, tooltip: "Show inbound logistics spacecraft for this GET rule");
                }
                AddSmallButton(row.transform, "EDIT", _runtimeStyle.SmallButtonColor, () =>
                {
                    ShowAmountInput(_getSection, rd, true, isAvailable: true, editRequest: req);
                }, width: 54f, tooltip: "Edit this import rule");
                MakeXButton(row.transform, () =>
                {
                    var capturedOi = _currentObjectInfo;
                    if (LogisticsObserver.VerboseLoggingEnabled)
                        LogisticsObserver.LogVerbose($"X clicked on GET req idx={idx} capturedOi=\"{capturedOi?.ObjectName}\"(id={capturedOi?.id})");
                    Data.LogisticsNetwork.RemoveRequest(capturedOi, idx);
                    BuildGetSection();
                    RebuildSectionLayout(_getSection);
                }, tooltip: "Remove this import rule");

                if (hasInboundStatuses && isGetExpanded)
                    BuildGetInboundStatusPanel(inboundStatuses);
            }
        }
        else
        {
            _getSection.AddTextRow("No import rules configured.", _font, 13f, new Color(0.5f, 0.5f, 0.5f, 1f));
        }

        AddBigButton(_getSection.ContentArea, "+ Add Import Rule", _runtimeStyle.ConfirmButtonColor, () =>
        {
            ShowResourcePicker(_getSection, true);
        }, tooltip: "Add a new resource import rule");
    }

    private string BuildTransitInfoSuffix(Data.LogisticsRequest req, ResourceDefinition rd)
    {
        if (req == null || rd == null || req.status != Data.LogisticsRequestStatus.InProgress)
            return "";

        var vehicle = FindInboundLogisticsVehicle(rd);
        if (vehicle == null)
            return "";

        var vehicleName = VehicleDisplayName(vehicle);
        var mission = vehicle?.GetMissionInfo();
        if (mission == null)
            mission = FindInboundMissionInfo(rd, vehicle);
        if (mission == null)
            return string.IsNullOrEmpty(vehicleName) ? "" : Logic.LogisticsStrings.TransitOnVehicleOnly(vehicleName);

        var arrivalText = mission.DateArrive.ToString("yyyy MMM d", LEManager.GetCultureInfoForDateTrajectory());
        if (string.IsNullOrEmpty(vehicleName))
            return Logic.LogisticsStrings.TransitArrivesOnly(arrivalText);
        return Logic.LogisticsStrings.TransitOnVehicleArrives(vehicleName, arrivalText);
    }

    private Spacecraft FindInboundLogisticsVehicle(ResourceDefinition rd)
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();
        if (player == null || _currentObjectInfo == null || rd == null)
            return null;

        foreach (var ship in ships)
        {
            if (ship == null || ship.GetCompany() != player)
                continue;

            var cycle = ship.CycleMissionsData;
            if (cycle == null || cycle.CheckComplete())
                continue;
            if (cycle.B != _currentObjectInfo)
                continue;
            if (cycle.customNameFromPlanMission == null
                || !cycle.customNameFromPlanMission.StartsWith("[LOGI]", System.StringComparison.Ordinal))
                continue;
            if (cycle.cargoAllStart?.Tab == null || !cycle.cargoAllStart.Tab.Any(tabRd => tabRd == rd))
                continue;
            return ship;
        }

        return null;
    }

    private MissionInfo FindInboundMissionInfo(ResourceDefinition rd, Spacecraft preferredVehicle = null)
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var missionManager = MonoBehaviourSingleton<MissionInfoManager>.Instance;
        if (player == null || missionManager?.ListMissionInfo == null || _currentObjectInfo == null || rd == null)
            return null;

        return missionManager.ListMissionInfo
            .Where(mi => mi != null
                && !mi.complete
                && !mi.cancel
                && mi.company == player
                && mi.target == _currentObjectInfo
                && (preferredVehicle == null || Equals(mi.spacecraftInfo2, preferredVehicle))
                && MissionCarriesResource(mi, rd))
            .OrderBy(mi => mi.DateArrive)
            .FirstOrDefault();
    }

    private List<LogisticsObserver.QuotaShipStatus> GetInboundLogisticsStatuses(ResourceDefinition rd)
    {
        var result = new List<LogisticsObserver.QuotaShipStatus>();
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        var missionManager = MonoBehaviourSingleton<MissionInfoManager>.Instance;
        if (player == null || missionManager?.ListMissionInfo == null || _currentObjectInfo == null || rd == null)
            return result;

        var seenShipIds = new HashSet<int>();
        var now = MonoBehaviourSingleton<TimeController>.Instance?.CurrentTime ?? DateTime.MinValue;

        foreach (var mi in missionManager.ListMissionInfo)
        {
            if (mi == null || mi.complete || mi.cancel)
                continue;
            if (mi.company != player || mi.target != _currentObjectInfo)
                continue;
            if (!LogisticsObserver.IsLogisticsMissionInfo(mi) && (string.IsNullOrEmpty(mi.missionName) || !mi.missionName.StartsWith("[LOGI]", StringComparison.Ordinal)))
                continue;
            if (!MissionCarriesResource(mi, rd))
                continue;

            var ships = GetMissionSpacecraft(mi).ToList();
            if (ships.Count == 0)
            {
                result.Add(BuildMissionStatus(mi, null, now));
                continue;
            }

            foreach (var ship in ships)
            {
                if (ship == null)
                    continue;
                if (ship.ID >= 0 && !seenShipIds.Add(ship.ID))
                    continue;
                result.Add(BuildMissionStatus(mi, ship, now));
            }
        }

        result.Sort((a, b) =>
        {
            var etaA = a.ETA ?? DateTime.MaxValue;
            var etaB = b.ETA ?? DateTime.MaxValue;
            var eta = etaA.CompareTo(etaB);
            return eta != 0 ? eta : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });
        return result;
    }

    private static IEnumerable<Spacecraft> GetMissionSpacecraft(MissionInfo mi)
    {
        if (mi?.spacecraftInfo2 is Spacecraft primary)
            yield return primary;
        if (mi?.ListSpacecraftInfo2 == null)
            yield break;
        foreach (var info in mi.ListSpacecraftInfo2)
        {
            if (info is Spacecraft sc)
                yield return sc;
        }
    }

    private static LogisticsObserver.QuotaShipStatus BuildMissionStatus(MissionInfo mi, Spacecraft ship, DateTime now)
    {
        var planned = mi != null && mi.DateLaunch != default && now != DateTime.MinValue && mi.DateLaunch > now;
        return new LogisticsObserver.QuotaShipStatus
        {
            Name = VehicleDisplayName(ship) ?? MissionVehicleName(mi) ?? "Spacecraft",
            Location = $"{mi?.start?.ObjectName ?? "?"} → {mi?.target?.ObjectName ?? "?"}",
            StatusText = planned ? "Planned" : "In transit",
            ETA = mi != null && mi.DateArrive != default ? (DateTime?)mi.DateArrive : null,
            State = planned ? LogisticsObserver.ShipState.Pending : LogisticsObserver.ShipState.InTransit
        };
    }

    private void BuildGetInboundStatusPanel(List<LogisticsObserver.QuotaShipStatus> statuses)
    {
        var detailBg = new Color(0.09f, 0.09f, 0.10f, 0.96f);
        if (statuses == null || statuses.Count == 0)
        {
            var emptyRow = MakeHLRow(_getSection.ContentArea, 22f, 4);
            emptyRow.GetComponent<Image>().color = detailBg;
            MakeTMP(emptyRow.transform, "  No inbound spacecraft found", 11, new Color(0.45f, 0.45f, 0.48f, 1f));
            return;
        }

        foreach (var ship in statuses)
            AddShipStatusRow(_getSection.ContentArea, ship, detailBg, 104f, 48);
    }

    private static bool MissionCarriesResource(MissionInfo mission, ResourceDefinition rd)
    {
        if (mission?.cargoAll == null || rd == null)
            return false;
        return CargoListCarriesResource(mission.cargoAll.listCargo, rd)
            || CargoListCarriesResource(mission.cargoAll.listCargoToOrbit, rd);
    }

    private static bool CargoListCarriesResource(IEnumerable<Cargo> cargoList, ResourceDefinition rd)
    {
        if (cargoList == null || rd == null)
            return false;
        return cargoList.Any(c => c != null
            && c.resourceTypeType == EResourceTypeType.resorces
            && c.resourceType == rd
            && c.cargoMass > 0);
    }

    private static string VehicleDisplayName(Spacecraft spacecraft)
    {
        if (spacecraft == null)
            return null;
        return spacecraft.GetSpacecraftName();
    }

    private static string MissionVehicleName(MissionInfo mission)
    {
        if (mission?.spacecraftInfo2 is Spacecraft spacecraft)
            return spacecraft.GetSpacecraftName();
        if (mission?.spacecraftInfo2?.GetTypeSpaceCraft() != null)
            return mission.spacecraftInfo2.GetTypeSpaceCraft().NameRocketType;
        return null;
    }

    private void BuildSendSection()
    {
        _sendSection.ClearContent();
        var data = Data.LogisticsNetwork.Get(_currentObjectInfo);
        var providerCount = data?.providers.Count ?? 0;

        if (providerCount > 0)
        {
            for (int i = 0; i < data.providers.Count; i++)
            {
                var prov = data.providers[i];
                var idx = i;
                var rd = prov.ResourceDefinition;
                if (!Data.LogisticsResourceFilter.IsSupported(rd))
                    continue;
                var displayName = ResourceLabel(rd, prov.resourceDef?.id);
                var sendExpandKey = $"{_currentObjectInfo?.id ?? -1}:send:{idx}:{rd?.ID ?? prov.resourceDef?.id ?? "resource"}";
                var sendShipStatuses = LogisticsObserver.GetAllShipsForSendProvider(_currentObjectInfo, rd, prov);
                var hasSendShips = sendShipStatuses.Count > 0;
                var isSendExpanded = _expandedSendProviderKeys.Contains(sendExpandKey);

                var row = MakeHLRow(_sendSection.ContentArea, 28f, 4);
                var autoSellText = "";
                if (prov.autoSell)
                {
                    autoSellText = prov.autoSellMode == Data.AutoSellMode.PerMonth
                        ? $" auto-sell {FormatCompactAmount(prov.autoSellMaxPerMonth)}/mo >= ${prov.autoSellMinPrice:0.##}"
                        : $" auto-sell >= ${prov.autoSellMinPrice:0.##}";
                }
                var minShipmentText = prov.minimumShipmentAmount > 0 ? $" min ship {FormatCompactAmount(prov.minimumShipmentAmount)}" : "";
                var assignedText = hasSendShips
                    ? $" ships: {sendShipStatuses.Count} active"
                    : "";
                var exportText = "";
                if (prov.exportToOrbit)
                {
                    exportText = prov.exportOrbitMaxStock > 0
                        ? $" export-to-orbit max {FormatCompactAmount(prov.exportOrbitMaxStock)}"
                        : " export-to-orbit";
                }
                string netText;
                if (prov.isDirect && prov.directLinkedObjectId >= 0)
                {
                    var linkedOiDisp = Data.LogisticsNetwork.ResolveObjectById(prov.directLinkedObjectId);
                    var linkedReq = Data.LogisticsNetwork.FindLinkedDirectRequest(prov.directLinkedObjectId, rd, _currentObjectInfo?.id ?? -1);
                    var targetText = linkedReq != null ? $" target: {FormatCompactAmount(linkedReq.requestedAmount)}" : "";
                    netText = $" <color=#88DDBB>[→ {BodyLabel(linkedOiDisp)}{targetText}]</color>";
                }
                else
                {
                    netText = prov.networkId == Data.LogisticsNetwork.LocalSystemNetworkId ? " <color=#88BBDD>[Local]</color>"
                        : prov.networkId > 0 ? $" <color=#88BBDD>[N{prov.networkId}]</color>" : "";
                }
                var labelTmp = MakeTMP(row.transform, $"<b><color=#EEEEF0>{displayName}</color></b>: keep {FormatCompactAmount(prov.minimumKeep)} in reserve{minShipmentText}{autoSellText}{exportText}{assignedText}{netText}", 13, new Color(0.7f, 0.7f, 0.7f, 1f));
                labelTmp.enableWordWrapping = true;
                labelTmp.overflowMode = TextOverflowModes.Overflow;
                labelTmp.alignment = TextAlignmentOptions.MidlineLeft;
                var labelLe = labelTmp.gameObject.AddComponent<LayoutElement>();
                labelLe.flexibleWidth = 1f;
                labelLe.preferredWidth = 0f;
                {
                    var priTmp = MakeTMP(row.transform, $"<color={PriorityColor(prov.priority)}>{PriorityLabel(prov.priority)}</color>", 12, new Color(0.65f, 0.65f, 0.65f, 1f));
                    priTmp.alignment = TextAlignmentOptions.MidlineRight;
                    var priLe = priTmp.gameObject.AddComponent<LayoutElement>();
                    priLe.preferredWidth = 52f;
                    priLe.flexibleWidth = 0f;
                }
                if (hasSendShips)
                {
                    AddSmallButton(row.transform, isSendExpanded ? "▼" : ">", _runtimeStyle.SmallButtonColor, () =>
                    {
                        if (_expandedSendProviderKeys.Contains(sendExpandKey))
                            _expandedSendProviderKeys.Remove(sendExpandKey);
                        else
                            _expandedSendProviderKeys.Add(sendExpandKey);
                        BuildSendSection();
                        RebuildSectionLayout(_sendSection);
                    }, width: 28f, tooltip: "Show spacecraft status for this export rule");
                }
                AddSmallButton(row.transform, "EDIT", _runtimeStyle.SmallButtonColor, () =>
                {
                    ShowAmountInput(_sendSection, rd, false, isAvailable: true, editProvider: prov);
                }, width: 54f, tooltip: "Edit this export rule");
                MakeXButton(row.transform, () =>
                {
                    var capturedOi = _currentObjectInfo;
                    if (LogisticsObserver.VerboseLoggingEnabled)
                        LogisticsObserver.LogVerbose($"X clicked on SEND prov idx={idx} capturedOi=\"{capturedOi?.ObjectName}\"(id={capturedOi?.id})");
                    Data.LogisticsNetwork.RemoveProvider(capturedOi, idx);
                    BuildSendSection();
                    RebuildSectionLayout(_sendSection);
                }, tooltip: "Remove this export rule");

                if (hasSendShips && isSendExpanded)
                    BuildSendShipStatusPanel(sendShipStatuses);
            }
        }
        else
        {
            _sendSection.AddTextRow("No export rules configured.", _font, 13f, new Color(0.5f, 0.5f, 0.5f, 1f));
        }

        AddBigButton(_sendSection.ContentArea, "+ Add Export Rule", _runtimeStyle.ActionButtonColor, () =>
        {
            ShowResourcePicker(_sendSection, false);
        }, tooltip: "Add a new resource export rule");
    }

    private void BuildSendShipStatusPanel(List<LogisticsObserver.QuotaShipStatus> statuses)
    {
        var detailBg = new Color(0.09f, 0.09f, 0.10f, 0.96f);
        if (statuses == null || statuses.Count == 0)
        {
            var emptyRow = MakeHLRow(_sendSection.ContentArea, 22f, 4);
            emptyRow.GetComponent<Image>().color = detailBg;
            MakeTMP(emptyRow.transform, "  No active ships found", 11, new Color(0.45f, 0.45f, 0.48f, 1f));
            return;
        }

        foreach (var ship in statuses)
            AddShipStatusRow(_sendSection.ContentArea, ship, detailBg, 104f, 48);
    }

    private void BuildSCSection()
    {
        BuildShipSection(_scSection, true);
    }

    private void BuildLVSection()
    {
        BuildShipSection(_lvSection, false);
    }

    private void BuildShipSection(LogisticsSection section, bool isSpacecraft)
    {
        section.ClearContent();
        if (_currentObjectInfo == null) return;

        var typeName = isSpacecraft ? "spacecraft" : "launch vehicles";

        // LV section - no quotas, just show available types
        if (!isSpacecraft)
        {
            BuildLVSectionOnly(section);
            return;
        }

        var quotaRows = new List<(ObjectInfo quotaObject, Data.ShipQuotaEntry quota)>();
        foreach (var quotaObject in GetQuotaDisplayObjects(isSpacecraft))
        {
            var data = Data.LogisticsNetwork.Get(quotaObject);
            foreach (var quota in data?.spacecraftQuota ?? new List<Data.ShipQuotaEntry>())
                quotaRows.Add((quotaObject, quota));
        }

        if (quotaRows.Count > 0)
        {
            foreach (var quotaRow in quotaRows)
            {
                var quotaObject = quotaRow.quotaObject;
                var q = quotaRow.quota;
                var quotaTypeName = q.typeName;
                var displayName = ShipDisplayNameForQuotaObject(quotaTypeName, true, quotaObject);
                var quotaCount = q.count;
                var readyHere = Data.LogisticsNetwork.GetReadySpacecraftCountForQuota(quotaObject, q);
                var awayAssigned = LogisticsObserver.GetAwayLogisticsSpacecraftCountForQuota(quotaObject, q);
                var expandKey = $"{quotaObject?.id ?? -1}:{quotaTypeName}";
                var isExpanded = _expandedQuotaKeys.Contains(expandKey);

                var row = MakeHLRow(section.ContentArea, 28f, 4);
                row.GetComponent<Image>().color = _runtimeStyle.RowBackgroundColor;

                var countColor = readyHere > 0
                    ? new Color(0.5f, 0.9f, 0.5f, 1f)
                    : awayAssigned > 0
                        ? new Color(0.45f, 0.68f, 0.95f, 1f)
                    : new Color(0.9f, 0.55f, 0.1f, 1f);
                var countText = awayAssigned > 0
                    ? $"{readyHere} (+{awayAssigned})/{quotaCount}"
                    : $"{readyHere}/{quotaCount}";
                var countLabel = MakeTMP(row.transform, countText, 13, countColor);
                countLabel.alignment = TextAlignmentOptions.Center;
                countLabel.rectTransform.sizeDelta = new Vector2(96, 0);

                var nameTmp = MakeTMP(row.transform, $"<b><color=#EEEEF0>{displayName}</color></b>", _runtimeStyle.RowFontSize, _runtimeStyle.RowTextColor);
                var nameLe = nameTmp.gameObject.AddComponent<LayoutElement>();
                nameLe.flexibleWidth = 1f;
                nameLe.preferredWidth = 0f;

                var capturedExpandKey = expandKey;
                AddSmallButton(row.transform, isExpanded ? "▼" : ">", _runtimeStyle.SmallButtonColor, () =>
                {
                    if (_expandedQuotaKeys.Contains(capturedExpandKey))
                        _expandedQuotaKeys.Remove(capturedExpandKey);
                    else
                        _expandedQuotaKeys.Add(capturedExpandKey);
                    BuildShipSection(section, isSpacecraft);
                    RebuildSectionLayout(section);
                }, width: 28f, tooltip: "Show ship details and settings");

                AddSmallButton(row.transform, "-", _runtimeStyle.SmallButtonColor, () =>
                {
                    var capturedOi = quotaObject;
                    var newVal = quotaCount - 1;
                    if (newVal <= 0)
                        Data.LogisticsNetwork.RemoveQuota(capturedOi, quotaTypeName, isSpacecraft);
                    else
                        Data.LogisticsNetwork.SetQuota(capturedOi, quotaTypeName, newVal, isSpacecraft);
                    BuildShipSection(section, isSpacecraft);
                    RebuildSectionLayout(section);
                    RefreshStockSections();
                }, tooltip: "Decrease spacecraft quota");

                AddSmallButton(row.transform, "+", _runtimeStyle.SmallButtonPositiveColor, () =>
                {
                    var capturedOi = quotaObject;
                    Data.LogisticsNetwork.SetQuota(capturedOi, quotaTypeName, quotaCount + 1, isSpacecraft);
                    BuildShipSection(section, isSpacecraft);
                    RebuildSectionLayout(section);
                    RefreshStockSections();
                }, tooltip: "Increase spacecraft quota");

                if (isExpanded)
                    BuildQuotaDetailPanel(section, quotaObject, q, quotaTypeName, isSpacecraft);
            }
        }
        else
        {
            section.AddTextRow($"No logistics {typeName} quotas set.", _font, 13f, new Color(0.5f, 0.5f, 0.5f, 1f));
        }

        AddBigButton(section.ContentArea, $"+ Add {typeName} quota", _runtimeStyle.ActionButtonColor, () =>
        {
            ShowShipPicker(section, true);
        }, tooltip: "Assign a spacecraft type to logistics duty");
    }

    private static readonly Color ShipDotIdle = new Color(0.4f, 0.85f, 0.4f, 1f);
    private static readonly Color ShipDotInTransit = new Color(0.4f, 0.65f, 0.95f, 1f);
    private static readonly Color ShipDotPending = new Color(0.9f, 0.8f, 0.25f, 1f);
    private static readonly Color ShipDotBlocked = new Color(0.9f, 0.3f, 0.3f, 1f);

    private void BuildQuotaDetailPanel(LogisticsSection section, ObjectInfo quotaObject, Data.ShipQuotaEntry q, string quotaTypeName, bool isSpacecraft)
    {
        var detailBg = new Color(0.09f, 0.09f, 0.10f, 0.96f);

        var settingsRow = MakeHLRow(section.ContentArea, 26f, 4);
        settingsRow.GetComponent<Image>().color = detailBg;

        var transferLabel = q.useFastestTransfer ? "[x] Fast" : "[ ] Fast";
        var transferColor = q.useFastestTransfer
            ? new Color(0.35f, 0.58f, 0.82f, 1f)
            : new Color(0.33f, 0.43f, 0.34f, 1f);
        AddSmallButton(settingsRow.transform, transferLabel, transferColor, () =>
        {
            Data.LogisticsNetwork.SetQuotaTransferPreference(quotaObject, quotaTypeName, true, !q.useFastestTransfer);
            BuildShipSection(section, isSpacecraft);
            RebuildSectionLayout(section);
        }, tooltip: "Toggle fastest transfer (vs optimal/lowest energy)");

        var backhaulLabel = q.backhaul ? "[x] Back" : "[ ] Back";
        var backhaulColor = q.backhaul
            ? new Color(0.6f, 0.45f, 0.82f, 1f)
            : new Color(0.33f, 0.43f, 0.34f, 1f);
        AddSmallButton(settingsRow.transform, backhaulLabel, backhaulColor, () =>
        {
            Data.LogisticsNetwork.SetQuotaBackhaul(quotaObject, quotaTypeName, true, !q.backhaul);
            BuildShipSection(section, isSpacecraft);
            RebuildSectionLayout(section);
        }, tooltip: "Toggle backhaul (carry resources on return trip)");

        var fuelProbeLabel = q.useFuelProbe ? "[x] Fuel" : "[ ] Fuel";
        var fuelProbeColor = q.useFuelProbe
            ? new Color(0.35f, 0.58f, 0.82f, 1f)
            : new Color(0.33f, 0.43f, 0.34f, 1f);
        AddSmallButton(settingsRow.transform, fuelProbeLabel, fuelProbeColor, () =>
        {
            Data.LogisticsNetwork.SetQuotaUseFuelProbe(quotaObject, quotaTypeName, true, !q.useFuelProbe);
            BuildShipSection(section, isSpacecraft);
            RebuildSectionLayout(section);
        }, width: 58f, tooltip: "Toggle return-fuel handling. When off, logistics will not probe, reserve, or ship return fuel for this spacecraft quota.");

        AddSmallButton(settingsRow.transform, "Min", _runtimeStyle.SmallButtonColor, () =>
        {
            ShowQuotaMinimumInput(section, quotaObject, quotaTypeName);
        }, tooltip: "Set minimum shipment size before dispatching");

        var minShipText = q.minimumShipmentAmount > 0 ? $"min: {FormatCompactAmount(q.minimumShipmentAmount)}" : "min: off";
        var minLabel = MakeTMP(settingsRow.transform, minShipText, 11, new Color(0.55f, 0.55f, 0.58f, 1f));
        minLabel.alignment = TextAlignmentOptions.MidlineRight;
        var minLe = minLabel.gameObject.AddComponent<LayoutElement>();
        minLe.flexibleWidth = 1f;

        var shipStatuses = LogisticsObserver.GetShipStatusesForQuota(quotaObject, q);
        if (shipStatuses.Count == 0)
        {
            var emptyRow = MakeHLRow(section.ContentArea, 22f, 4);
            emptyRow.GetComponent<Image>().color = detailBg;
            MakeTMP(emptyRow.transform, "  No ships assigned", 11, new Color(0.45f, 0.45f, 0.48f, 1f));
        }
        else
        {
            foreach (var ship in shipStatuses)
            {
                var shipRow = MakeHLRow(section.ContentArea, 22f, 6);
                shipRow.GetComponent<Image>().color = detailBg;

                var dotColor = ship.State switch
                {
                    LogisticsObserver.ShipState.Idle => ShipDotIdle,
                    LogisticsObserver.ShipState.InTransit => ShipDotInTransit,
                    LogisticsObserver.ShipState.Pending => ShipDotPending,
                    LogisticsObserver.ShipState.Blocked => ShipDotBlocked,
                    _ => SubtleTextColor
                };
                var dotTmp = MakeTMP(shipRow.transform, "●", 10, dotColor);
                var dotLe = dotTmp.gameObject.AddComponent<LayoutElement>();
                dotLe.preferredWidth = 14f;
                dotLe.flexibleWidth = 0f;

                var shipNameTmp = MakeTMP(shipRow.transform, ship.Name, 11, _runtimeStyle.RowTextColor);
                var shipNameLe = shipNameTmp.gameObject.AddComponent<LayoutElement>();
                shipNameLe.preferredWidth = 100f;
                shipNameLe.flexibleWidth = 0f;

                var statusColor = ship.State switch
                {
                    LogisticsObserver.ShipState.Idle => new Color(0.5f, 0.75f, 0.5f, 1f),
                    LogisticsObserver.ShipState.InTransit => new Color(0.45f, 0.65f, 0.9f, 1f),
                    LogisticsObserver.ShipState.Pending => new Color(0.8f, 0.72f, 0.3f, 1f),
                    LogisticsObserver.ShipState.Blocked => new Color(0.85f, 0.4f, 0.35f, 1f),
                    _ => SubtleTextColor
                };
                var statusTmp = MakeTMP(shipRow.transform, ship.StatusText, 11, statusColor);
                statusTmp.enableWordWrapping = false;
                var statusLe = statusTmp.gameObject.AddComponent<LayoutElement>();
                statusLe.flexibleWidth = 1f;
                statusLe.preferredWidth = 0f;

                if (ship.ETA.HasValue)
                {
                    var etaText = ship.ETA.Value.ToString("yyyy MMM d", Language.LEManager.GetCultureInfoForDateTrajectory());
                    var etaTmp = MakeTMP(shipRow.transform, etaText, 11, new Color(0.55f, 0.55f, 0.6f, 1f));
                    etaTmp.alignment = TextAlignmentOptions.MidlineRight;
                    var etaLe = etaTmp.gameObject.AddComponent<LayoutElement>();
                    etaLe.preferredWidth = 80f;
                    etaLe.flexibleWidth = 0f;
                }
            }
        }
    }

    private void ShowQuotaMinimumInput(LogisticsSection section, ObjectInfo quotaObject, string quotaTypeName)
    {
        section.ClearContent();
        var quotaEntry = Data.LogisticsNetwork.GetQuotaEntry(quotaObject, quotaTypeName, true);
        var minimumShipment = quotaEntry?.minimumShipmentAmount ?? 0;

        AddBigButton(section.ContentArea, "← Back", _runtimeStyle.BackButtonColor, () =>
        {
            BuildSCSection();
            RebuildSectionLayout(section);
        }, tooltip: "Return to spacecraft quota settings");

        var title = MakeTMP(section.ContentArea,
            $"Minimum shipment: {ShipDisplayNameForQuotaObject(quotaTypeName, true, quotaObject)}",
            14,
            new Color(0.9f, 0.9f, 0.5f, 1f));
        title.rectTransform.sizeDelta = new Vector2(0, 22);

        var summary = MakeTMP(section.ContentArea, "", 13, _runtimeStyle.RowTextColor);
        summary.rectTransform.sizeDelta = new Vector2(0, 22);

        TMP_InputField input = null;
        void RefreshSummary()
        {
            summary.text = minimumShipment > 0
                ? $"Minimum useful load: {FormatCompactAmount(minimumShipment)}"
                : "Minimum useful load: off";
            if (input != null && !input.isFocused)
                input.text = minimumShipment.ToString("0.##");
        }

        MakeNumericInputRow(section.ContentArea, "Minimum load", minimumShipment, value =>
        {
            minimumShipment = Math.Max(0, value);
            RefreshSummary();
        }, out input);

        var plusRow = MakeHLRow(section.ContentArea, 28f, 4);
        void AddMinimum(double delta)
        {
            minimumShipment = Math.Max(0, minimumShipment + delta);
            RefreshSummary();
        }
        AddSmallButton(plusRow.transform, "+100", _runtimeStyle.SmallButtonPositiveColor, () => AddMinimum(100), tooltip: "Increase minimum useful load by 100");
        AddSmallButton(plusRow.transform, "+1K", _runtimeStyle.SmallButtonPositiveColor, () => AddMinimum(1000), tooltip: "Increase minimum useful load by 1K");
        AddSmallButton(plusRow.transform, "+10K", _runtimeStyle.SmallButtonPositiveColor, () => AddMinimum(10000), tooltip: "Increase minimum useful load by 10K");
        AddSmallButton(plusRow.transform, "+100K", _runtimeStyle.SmallButtonPositiveColor, () => AddMinimum(100000), tooltip: "Increase minimum useful load by 100K");

        var minusRow = MakeHLRow(section.ContentArea, 28f, 4);
        AddSmallButton(minusRow.transform, "−100", _runtimeStyle.SmallButtonColor, () => AddMinimum(-100), tooltip: "Decrease minimum useful load by 100");
        AddSmallButton(minusRow.transform, "−1K", _runtimeStyle.SmallButtonColor, () => AddMinimum(-1000), tooltip: "Decrease minimum useful load by 1K");
        AddSmallButton(minusRow.transform, "−10K", _runtimeStyle.SmallButtonColor, () => AddMinimum(-10000), tooltip: "Decrease minimum useful load by 10K");
        AddSmallButton(minusRow.transform, "Off", _runtimeStyle.SmallButtonColor, () =>
        {
            minimumShipment = 0;
            RefreshSummary();
        }, tooltip: "Disable the minimum useful load requirement");

        AddBigButton(section.ContentArea, "Confirm", _runtimeStyle.ConfirmButtonColor, () =>
        {
            Data.LogisticsNetwork.SetQuotaMinimumShipment(quotaObject, quotaTypeName, true, minimumShipment);
            BuildSCSection();
            RebuildSectionLayout(section);
        }, tooltip: "Save the minimum useful load for this spacecraft quota");

        RefreshSummary();
        RebuildSectionLayout(section);
    }

    private void AddShipStatusRow(Transform parent, LogisticsObserver.QuotaShipStatus ship, Color background, float nameWidth, int maxStatusChars, bool includeLocation = true)
    {
        var shipRow = MakeHLRow(parent, 22f, 6);
        shipRow.GetComponent<Image>().color = background;

        var dotColor = ship.State switch
        {
            LogisticsObserver.ShipState.Idle => ShipDotIdle,
            LogisticsObserver.ShipState.InTransit => ShipDotInTransit,
            LogisticsObserver.ShipState.Pending => ShipDotPending,
            LogisticsObserver.ShipState.Blocked => ShipDotBlocked,
            _ => SubtleTextColor
        };
        var dotTmp = MakeTMP(shipRow.transform, "●", 10, dotColor);
        var dotLe = dotTmp.gameObject.AddComponent<LayoutElement>();
        dotLe.preferredWidth = 14f;
        dotLe.flexibleWidth = 0f;

        var shipNameTmp = MakeTMP(shipRow.transform, TruncateText(ship.Name, 16), 11, _runtimeStyle.RowTextColor);
        var shipNameLe = shipNameTmp.gameObject.AddComponent<LayoutElement>();
        shipNameLe.preferredWidth = nameWidth;
        shipNameLe.flexibleWidth = 0f;

        if (includeLocation)
        {
            var locationTmp = MakeTMP(shipRow.transform, TruncateText(ship.Location, 28), 11, new Color(0.56f, 0.56f, 0.62f, 1f));
            locationTmp.enableWordWrapping = false;
            var locationLe = locationTmp.gameObject.AddComponent<LayoutElement>();
            locationLe.preferredWidth = 125f;
            locationLe.flexibleWidth = 0f;
        }

        var statusColor = ship.State switch
        {
            LogisticsObserver.ShipState.Idle => new Color(0.5f, 0.75f, 0.5f, 1f),
            LogisticsObserver.ShipState.InTransit => new Color(0.45f, 0.65f, 0.9f, 1f),
            LogisticsObserver.ShipState.Pending => new Color(0.8f, 0.72f, 0.3f, 1f),
            LogisticsObserver.ShipState.Blocked => new Color(0.85f, 0.4f, 0.35f, 1f),
            _ => SubtleTextColor
        };
        var statusTmp = MakeTMP(shipRow.transform, TruncateText(ship.StatusText, maxStatusChars), 11, statusColor);
        statusTmp.enableWordWrapping = false;
        var statusLe = statusTmp.gameObject.AddComponent<LayoutElement>();
        statusLe.flexibleWidth = 1f;
        statusLe.preferredWidth = 0f;

        if (ship.ETA.HasValue)
        {
            var etaText = ship.ETA.Value.ToString("yyyy MMM d", Language.LEManager.GetCultureInfoForDateTrajectory());
            var etaTmp = MakeTMP(shipRow.transform, etaText, 11, new Color(0.55f, 0.55f, 0.6f, 1f));
            etaTmp.alignment = TextAlignmentOptions.MidlineRight;
            var etaLe = etaTmp.gameObject.AddComponent<LayoutElement>();
            etaLe.preferredWidth = 80f;
            etaLe.flexibleWidth = 0f;
        }
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || maxChars <= 3 || text.Length <= maxChars)
            return text ?? "";
        return text.Substring(0, maxChars - 3) + "...";
    }

    private void BuildLVSectionOnly(LogisticsSection section)
    {
        section.ClearContent();
        if (_currentObjectInfo == null) return;

        var typeCounts = Data.LogisticsNetwork.GetShipTypeCountsOnObject(_currentObjectInfo, false);

        if (typeCounts.Count == 0)
        {
            section.AddTextRow("No launch vehicles on this object.", _font, 13f, new Color(0.5f, 0.5f, 0.5f, 1f));
            RebuildSectionLayout(section);
            return;
        }

        section.AddTextRow("Click to toggle:", _font, 12f, new Color(0.5f, 0.5f, 0.58f, 1f));

        foreach (var kv in typeCounts)
        {
            var lvTypeName = kv.Key;
            var count = kv.Value;

            // Check if this LV type is "enabled" (has quota > 0)
            var currentQuota = Data.LogisticsNetwork.GetQuota(_currentObjectInfo, lvTypeName, false);
            var isEnabled = currentQuota > 0;

            var row = MakeHLRow(section.ContentArea, 26f, 4);
            row.GetComponent<Image>().color = isEnabled ? _runtimeStyle.ToggleOnColor : _runtimeStyle.ToggleOffColor;

            var activeColor = isEnabled ? new Color(0.54f, 0.9f, 0.62f, 1f) : new Color(0.66f, 0.66f, 0.7f, 1f);
            MakeTMP(row.transform, $"<b>{ShipDisplayName(lvTypeName, false)}</b>  x{count}", 13, activeColor);

            var statusText = isEnabled ? "ON" : "OFF";
            var statusColor = isEnabled ? new Color(0.58f, 0.9f, 0.58f, 1f) : new Color(0.48f, 0.48f, 0.52f, 1f);
            MakeTMP(row.transform, statusText, 11, statusColor);

            // Click to toggle
            var rowBg = row.GetComponent<Image>();
            if (rowBg != null) rowBg.sprite = null;
            var btn = row.AddComponent<Button>();
            btn.targetGraphic = rowBg;
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            btn.colors = MakeColorBlock(rowBg != null ? rowBg.color : _runtimeStyle.ToggleOffColor);
            var capturedOi = _currentObjectInfo;
            var capturedType = lvTypeName;
            btn.onClick.AddListener(() =>
            {
                if (currentQuota > 0)
                    Data.LogisticsNetwork.RemoveQuota(capturedOi, capturedType, false);
                else
                    Data.LogisticsNetwork.SetQuota(capturedOi, capturedType, 1, false);
                BuildShipSection(section, false);
                RebuildSectionLayout(section);
                RefreshStockSections();
            });
            SetTooltip(row, isEnabled
                ? "Disable this launch vehicle type for logistics launches"
                : "Enable this launch vehicle type for logistics launches");
        }

        RebuildSectionLayout(section);
    }

    private void ShowShipPicker(LogisticsSection section, bool isSpacecraft)
    {
        section.ClearContent();

        AddBigButton(section.ContentArea, "← Back", _runtimeStyle.BackButtonColor, () =>
        {
            if (isSpacecraft) BuildSCSection(); else BuildLVSection();
            RebuildSectionLayout(section);
        }, tooltip: "Return to quota settings");

        if (_currentObjectInfo == null)
        {
            section.AddTextRow("No object selected.", _font);
            RebuildSectionLayout(section);
            return;
        }

        var typeName = isSpacecraft ? "spacecraft" : "launch vehicles";
        var pickerRows = new List<(ObjectInfo quotaObject, string typeKey, int count)>();
        foreach (var quotaObject in GetQuotaDisplayObjects(isSpacecraft))
        {
            foreach (var kv in Data.LogisticsNetwork.GetShipTypeCountsOnObject(quotaObject, isSpacecraft))
                pickerRows.Add((quotaObject, kv.Key, kv.Value));
        }

        if (pickerRows.Count == 0)
        {
            section.AddTextRow($"No {typeName} found on this object.", _font);
            RebuildSectionLayout(section);
            return;
        }

        foreach (var pickerRow in pickerRows)
        {
            var quotaObject = pickerRow.quotaObject;
            var shipTypeName = pickerRow.typeKey;
            var totalCount = pickerRow.count;
            var currentQuota = Data.LogisticsNetwork.GetQuota(quotaObject, shipTypeName, isSpacecraft);
            var quotaEntry = Data.LogisticsNetwork.GetQuotaEntry(quotaObject, shipTypeName, isSpacecraft);
            var displayQuota = currentQuota > 0 ? $"quota: {totalCount}/{currentQuota}" : "no quota";
            var transferText = !isSpacecraft || quotaEntry == null
                ? ""
                : $" (route: {(quotaEntry.useFastestTransfer ? "Fastest" : "Optimal")})";
            var minShipText = !isSpacecraft || quotaEntry == null || quotaEntry.minimumShipmentAmount <= 0
                ? ""
                : $" (min: {FormatCompactAmount(quotaEntry.minimumShipmentAmount)})";
            var fuelProbeText = !isSpacecraft || quotaEntry == null || quotaEntry.useFuelProbe
                ? ""
                : " (fuel: off)";

            var row = MakeHLRow(section.ContentArea, 26f, 4);
            row.GetComponent<Image>().color = _runtimeStyle.RowBackgroundColor;

            MakeTMP(row.transform, $"<b><color=#EEEEF0>{ShipDisplayNameForQuotaObject(shipTypeName, isSpacecraft, quotaObject)}</color></b>  {totalCount} available ({displayQuota}){transferText}{minShipText}{fuelProbeText}", _runtimeStyle.RowFontSize, _runtimeStyle.RowTextColor);

            if (isSpacecraft && currentQuota > 0)
            {
                var useFastest = quotaEntry?.useFastestTransfer ?? false;
                AddSmallButton(row.transform, useFastest ? "[x] Fast" : "[ ] Fast",
                    useFastest ? new Color(0.35f, 0.58f, 0.82f, 1f) : new Color(0.33f, 0.43f, 0.34f, 1f), () =>
                    {
                        var capturedOi = quotaObject;
                        Data.LogisticsNetwork.SetQuotaTransferPreference(capturedOi, shipTypeName, true, !useFastest);
                        ShowShipPicker(section, isSpacecraft);
                        RebuildSectionLayout(section);
                    }, tooltip: "Toggle fastest transfer");
            }

            AddSmallButton(row.transform, "+", _runtimeStyle.SmallButtonPositiveColor, () =>
            {
                var capturedOi = quotaObject;
                Data.LogisticsNetwork.SetQuota(capturedOi, shipTypeName, currentQuota + 1, isSpacecraft);
                if (isSpacecraft) BuildSCSection(); else BuildLVSection();
                RebuildSectionLayout(section);
                RefreshStockSections();
            }, tooltip: "Add one to quota");
        }

        RebuildSectionLayout(section);
    }

    private void EnsureLinkedDirectProvider(ObjectInfo requestOi, ResourceDefinition rd, int providerObjectId, int priority)
    {
        var existing = Data.LogisticsNetwork.FindLinkedDirectProvider(providerObjectId, rd, requestOi?.id ?? -1);
        if (existing != null)
            return;

        var providerOi = Data.LogisticsNetwork.ResolveObjectById(providerObjectId);
        if (providerOi == null || rd == null) return;

        var prov = Data.LogisticsNetwork.AddProvider(providerOi, rd, 0, priority: priority);
        if (prov != null)
        {
            prov.isDirect = true;
            prov.directLinkedObjectId = requestOi?.id ?? -1;
        }
    }

    private void EnsureLinkedDirectRequest(ObjectInfo providerOi, ResourceDefinition rd, int requestObjectId, int priority)
    {
        var existing = Data.LogisticsNetwork.FindLinkedDirectRequest(requestObjectId, rd, providerOi?.id ?? -1);
        if (existing != null)
            return;

        var requestOi = Data.LogisticsNetwork.ResolveObjectById(requestObjectId);
        if (requestOi == null || rd == null) return;

        var req = Data.LogisticsNetwork.AddRequest(requestOi, rd, 100, 100, false, priority: priority);
        if (req != null)
        {
            req.isDirect = true;
            req.directLinkedObjectId = providerOi?.id ?? -1;
        }
    }

    private void ShowBodyPicker(LogisticsSection section, ResourceDefinition rd, bool isGet,
        ObjectInfo currentBody, System.Action<ObjectInfo> onSelected)
    {
        section.ClearContent();
        AddBigButton(section.ContentArea, "← Back", _runtimeStyle.BackButtonColor, () =>
        {
            if (isGet) BuildGetSection(); else BuildSendSection();
            RebuildSectionLayout(section);
        });

        var filterInput = MakeTextFilterInput(section.ContentArea, "Type to filter...");

        var listParent = new GameObject("BodyList", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        listParent.transform.SetParent(section.ContentArea, false);
        listParent.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var listVlg = listParent.GetComponent<VerticalLayoutGroup>();
        listVlg.childForceExpandWidth = true;
        listVlg.childForceExpandHeight = false;
        listVlg.childControlWidth = true;
        listVlg.childControlHeight = true;
        listVlg.spacing = 1f;

        var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
        var spriteFormat = objManager?.spriteTextStart ?? "";
        var allBodies = objManager?.allObjectInfos?
            .Where(oi => oi != null && oi != currentBody
                && oi.objectTypes != global::Data.EObjectTypes.Spacecraft
                && oi.objectTypes != global::Data.EObjectTypes.Belt
                && oi.objectTypes != global::Data.EObjectTypes.None
                && oi.objectTypes != global::Data.EObjectTypes.AllMainObject
                && oi.objectTypes != global::Data.EObjectTypes.SolarSystem)
            .OrderBy(oi => oi.ObjectName)
            .ToList() ?? new List<ObjectInfo>();

        var bodyRows = new List<(GameObject row, ObjectInfo body, string searchName)>();
        foreach (var oi in allBodies)
        {
            var bodyOi = oi;
            var row = MakeHLRow(listParent.transform, 26f, 4);
            var rowBg = row.GetComponent<Image>();
            rowBg.sprite = null;
            rowBg.color = _runtimeStyle.RowBackgroundColor;

            var iconStr = "";
            try
            {
                var sprite = oi.ImagePlanetUI;
                if (sprite != null && !string.IsNullOrEmpty(spriteFormat))
                    iconStr = spriteFormat.MyFormat(sprite.name) + " ";
            }
            catch { }

            MakeTMP(row.transform, $"{iconStr}{oi.ObjectName}", 13, new Color(0.8f, 0.8f, 0.82f, 1f));
            var btn = row.AddComponent<Button>();
            btn.targetGraphic = rowBg;
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            btn.colors = MakeColorBlock(_runtimeStyle.RowBackgroundColor);
            btn.onClick.AddListener(() => onSelected?.Invoke(bodyOi));
            bodyRows.Add((row, oi, oi.ObjectName.ToUpperInvariant()));
        }

        void FilterList(string filter)
        {
            var upper = (filter ?? "").Trim().ToUpperInvariant();
            var shown = 0;
            foreach (var entry in bodyRows)
            {
                var match = string.IsNullOrEmpty(upper) || entry.searchName.Contains(upper);
                var visible = match && shown < 30;
                entry.row.SetActive(visible);
                if (match) shown++;
            }
            RebuildSectionLayout(section);
        }

        filterInput.onValueChanged.AddListener(FilterList);
        FilterList("");
        RebuildSectionLayout(section);
    }

    private void ShowResourcePicker(LogisticsSection section, bool isGet)
    {
        section.ClearContent();

        AddBigButton(section.ContentArea, "← Back", _runtimeStyle.BackButtonColor, () =>
        {
            if (isGet) BuildGetSection(); else BuildSendSection();
            RebuildSectionLayout(section);
        }, tooltip: isGet ? "Return to import rules" : "Return to export rules");

        var am = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
        if (am == null || am.AllResourceDefinitions == null)
        {
            section.AddTextRow("Resource list not available.", _font);
            RebuildSectionLayout(section);
            return;
        }

        var gm = MonoBehaviourSingleton<GameManager>.Instance;
        var player = gm?.Player;
        HashSet<ResourceDefinition> available;
        if (player != null && _currentObjectInfo != null)
        {
            if (isGet)
                available = Data.LogisticsNetwork.GetNetworkResourcesSet(player);
            else
                available = Data.LogisticsNetwork.GetAvailableResourcesOnObject(_currentObjectInfo, player);
        }
        else
        {
            available = new HashSet<ResourceDefinition>();
        }

        foreach (var rd in am.AllResourceDefinitions.ListNotEmpty)
        {
            if (!Data.LogisticsResourceFilter.IsSupported(rd))
                continue;

            var rdCaptured = rd;
            var sectionRef = section;
            var isGetCaptured = isGet;
            var isAvailable = available.Contains(rd);
            var hasLocalSellOffer = isGet && !isAvailable && _currentObjectInfo != null && HasMatchingSellOffer(_currentObjectInfo, rd);

            var row = MakeHLRow(section.ContentArea, 24f, 0);
            var rowBg = row.GetComponent<Image>();
            rowBg.sprite = null;
            rowBg.color = _runtimeStyle.RowBackgroundColor;
            var color = (isAvailable || hasLocalSellOffer) ? new Color(0.8f, 0.8f, 0.8f, 1f) : new Color(0.35f, 0.35f, 0.35f, 1f);
            var label = isAvailable ? ResourceLabel(rd)
                : hasLocalSellOffer ? $"{ResourceLabel(rd)} <color=#B0D0FF>[MARKET]</color>"
                : $"{ResourceLabel(rd)} (not available)";
            MakeTMP(row.transform, label, 13, color);

            var btn = row.AddComponent<Button>();
            btn.targetGraphic = rowBg;
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            btn.colors = MakeColorBlock(_runtimeStyle.RowBackgroundColor);
            btn.onClick.AddListener(() =>
            {
                ShowAmountInput(sectionRef, rdCaptured, isGetCaptured, isAvailable || hasLocalSellOffer);
            });
            SetTooltip(row, isGet
                ? "Create an import rule for this resource"
                : "Create an export rule for this resource");
        }

        RebuildSectionLayout(section);
    }

    private bool _inputConfirmed;
    private int _pendingDirectLinkedObjectId = -1;
    private bool _pendingDirectMode;
    private double _pendingTargetAmount;
    private double _pendingCurrentAmount;
    private int _pendingPriority;
    private int _pendingNetworkId;
    private bool _hasPendingState;
    private System.Action _linkedReturnAction;

    private void ShowAmountInput(LogisticsSection section, ResourceDefinition rd, bool isGet, bool isAvailable = true,
        Data.LogisticsRequest editRequest = null, Data.LogisticsProvider editProvider = null,
        ObjectInfo contextOi = null)
    {
        var capturedOi = contextOi ?? _currentObjectInfo;
        if (LogisticsObserver.VerboseLoggingEnabled)
            LogisticsObserver.LogVerbose($"ShowAmountInput: rd={rd.ID} isGet={isGet} capturedOi=\"{capturedOi?.ObjectName}\"(id={capturedOi?.id})");
        _inputConfirmed = false;
        double currentAmount = 0;
        double targetAmount = 0;
        double minimumAmount = 0;
        bool useMinimum = false;
        bool oneShot = false;
        bool autoBuy = false;
        double autoBuyMaxPrice = 0;
        bool autoSell = false;
        Data.AutoSellMode autoSellMode = Data.AutoSellMode.Continuous;
        double autoSellMaxPerMonth = 0;
        double autoSellMinPrice = 0;
        bool exportToOrbit = false;
        double minimumShipmentAmount = 0;
        double exportOrbitMaxStock = 0;
        bool useSharedSpacecraftPool = true;
        var assignedSpacecraftIds = new HashSet<int>();
        var assignedSpacecraftSettings = new List<Data.ProviderSpacecraftSetting>();
        int priority = 0;
        int networkId = 0;
        bool isDirect = _pendingDirectMode;
        int directLinkedObjectId = _pendingDirectLinkedObjectId;
        if (_hasPendingState)
        {
            targetAmount = _pendingTargetAmount;
            currentAmount = _pendingCurrentAmount;
            priority = _pendingPriority;
            networkId = _pendingNetworkId;
        }
        _pendingDirectMode = false;
        _pendingDirectLinkedObjectId = -1;
        _hasPendingState = false;
        bool editingMinimum = false;
        var isEditing = editRequest != null || editProvider != null;
        if (editRequest != null)
        {
            targetAmount = editRequest.requestedAmount;
            minimumAmount = editRequest.minimumAmount;
            useMinimum = editRequest.useMinimumAmount;
            oneShot = editRequest.oneShot;
            autoBuy = editRequest.autoBuy;
            autoBuyMaxPrice = editRequest.autoBuyMaxPrice;
            priority = ClampPriority(editRequest.priority);
            networkId = Data.LogisticsNetwork.ClampNetworkId(editRequest.networkId);
            if (!isDirect)
            {
                isDirect = editRequest.isDirect;
                directLinkedObjectId = editRequest.directLinkedObjectId;
            }
            currentAmount = targetAmount;
        }
        if (editProvider != null)
        {
            currentAmount = editProvider.minimumKeep;
            autoSell = editProvider.autoSell;
            autoSellMode = editProvider.autoSellMode;
            autoSellMaxPerMonth = editProvider.autoSellMaxPerMonth;
            autoSellMinPrice = editProvider.autoSellMinPrice;
            exportToOrbit = editProvider.exportToOrbit;
            minimumShipmentAmount = editProvider.minimumShipmentAmount;
            exportOrbitMaxStock = editProvider.exportOrbitMaxStock;
            useSharedSpacecraftPool = editProvider.useSharedSpacecraftPool;
            assignedSpacecraftIds = new HashSet<int>(editProvider.assignedSpacecraftIds ?? new List<int>());
            assignedSpacecraftSettings = (editProvider.assignedSpacecraftSettings ?? new List<Data.ProviderSpacecraftSetting>())
                .Select(s => new Data.ProviderSpacecraftSetting
                {
                    typeName = s.typeName,
                    useFastestTransfer = s.useFastestTransfer,
                    minimumShipmentAmount = s.minimumShipmentAmount,
                    backhaul = s.backhaul,
                    useFuelProbe = s.useFuelProbe
                })
                .ToList();
            priority = ClampPriority(editProvider.priority);
            networkId = Data.LogisticsNetwork.ClampNetworkId(editProvider.networkId);
            if (!isDirect)
            {
                isDirect = editProvider.isDirect;
                directLinkedObjectId = editProvider.directLinkedObjectId;
            }
        }
        section.ClearContent();

        AddBigButton(section.ContentArea, isEditing ? "← Back to rules" : "← Back to resources", _runtimeStyle.BackButtonColor, () =>
        {
            if (isEditing)
            {
                if (isGet) BuildGetSection(); else BuildSendSection();
                RebuildSectionLayout(section);
            }
            else
            {
                ShowResourcePicker(section, isGet);
            }
        }, tooltip: isEditing ? "Discard edits and return to the rule list" : "Return to resource selection");

        if (!isAvailable)
        {
            var warnTmp = MakeTMP(section.ContentArea, "WARNING: Resource not currently available", 12, new Color(0.9f, 0.6f, 0.1f, 1f));
            warnTmp.rectTransform.sizeDelta = new Vector2(0, 20);
        }

        var titlePrefix = isEditing ? "Edit" : (isGet ? "Import target" : "Export reserve");
        var titleLabel = MakeTMP(section.ContentArea, $"{titlePrefix}: {ResourceLabel(rd)}", 14, new Color(0.9f, 0.9f, 0.5f, 1f));
        titleLabel.rectTransform.sizeDelta = new Vector2(0, 22);

        var priorityRow = MakeHLRow(section.ContentArea, 28f, 6);
        TextMeshProUGUI priorityLabel = null;
        AddSmallButton(priorityRow.transform, "-", _runtimeStyle.SmallButtonColor, () =>
        {
            priority = ClampPriority(priority - 1);
            RefreshPriorityDisplay();
        }, tooltip: "Decrease priority");
        priorityLabel = MakeTMP(priorityRow.transform, "", 13, _runtimeStyle.RowTextColor);
        priorityLabel.alignment = TextAlignmentOptions.Center;
        var priorityLayout = priorityLabel.gameObject.AddComponent<LayoutElement>();
        priorityLayout.flexibleWidth = 1f;
        AddSmallButton(priorityRow.transform, "+", _runtimeStyle.SmallButtonPositiveColor, () =>
        {
            priority = ClampPriority(priority + 1);
            RefreshPriorityDisplay();
        }, tooltip: "Increase priority");

        void RefreshPriorityDisplay()
        {
            priority = ClampPriority(priority);
            priorityLabel.text = $"Priority: {PriorityLabel(priority)}";
        }
        RefreshPriorityDisplay();

        var routeModeRow = MakeHLRow(section.ContentArea, 28f, 6);
        GameObject networkRow = null;
        GameObject directRow = null;
        GameObject directLinkedButtonRow = null;
        TextMeshProUGUI networkLabel = null;
        TextMeshProUGUI directBodyLabel = null;

        GameObject networkModeBtn = null;
        GameObject directModeBtn = null;
        networkModeBtn = AddBigButtonInline(routeModeRow.transform, "Network", _runtimeStyle.ActionButtonColor, () =>
        {
            isDirect = false;
            directLinkedObjectId = -1;
            RefreshRouteMode();
        }, tooltip: "Use logistics network to find providers/requesters");
        directModeBtn = AddBigButtonInline(routeModeRow.transform, "Direct Route", _runtimeStyle.ActionButtonColor, () =>
        {
            isDirect = true;
            RefreshRouteMode();
        }, tooltip: "Set a direct 1-to-1 route to a specific body");

        networkRow = MakeHLRow(section.ContentArea, 28f, 6);
        AddSmallButton(networkRow.transform, "-", _runtimeStyle.SmallButtonColor, () =>
        {
            networkId = Data.LogisticsNetwork.StepNetworkId(networkId, -1);
            RefreshNetworkDisplay();
        }, tooltip: "Previous network");
        networkLabel = MakeTMP(networkRow.transform, "", 13, _runtimeStyle.RowTextColor);
        networkLabel.alignment = TextAlignmentOptions.Center;
        var networkLayout = networkLabel.gameObject.AddComponent<LayoutElement>();
        networkLayout.flexibleWidth = 1f;
        AddSmallButton(networkRow.transform, "+", _runtimeStyle.SmallButtonPositiveColor, () =>
        {
            networkId = Data.LogisticsNetwork.StepNetworkId(networkId, 1);
            RefreshNetworkDisplay();
        }, tooltip: "Increase network ID (isolates from other networks)");

        directRow = MakeHLRow(section.ContentArea, 28f, 6);
        var directLabel = isGet ? "Source:" : "Destination:";
        MakeTMP(directRow.transform, directLabel, 13, _runtimeStyle.RowTextColor);
        directBodyLabel = MakeTMP(directRow.transform, "", 13, new Color(0.8f, 0.85f, 0.9f, 1f));
        directBodyLabel.alignment = TextAlignmentOptions.MidlineLeft;
        var directBodyLe = directBodyLabel.gameObject.AddComponent<LayoutElement>();
        directBodyLe.flexibleWidth = 1f;
        AddSmallButton(directRow.transform, "Pick", _runtimeStyle.ActionButtonColor, () =>
        {
            _pendingTargetAmount = isGet ? targetAmount : currentAmount;
            _pendingCurrentAmount = currentAmount;
            _pendingPriority = priority;
            _pendingNetworkId = networkId;
            _hasPendingState = true;
            ShowBodyPicker(section, rd, isGet, capturedOi, (selectedOi) =>
            {
                _pendingDirectLinkedObjectId = selectedOi?.id ?? -1;
                _pendingDirectMode = true;
                ShowAmountInput(section, rd, isGet, isAvailable, editRequest, editProvider);
            });
        }, width: 54f, tooltip: isGet ? "Choose which body to import from" : "Choose which body to export to");

        directLinkedButtonRow = MakeHLRow(section.ContentArea, 28f, 6);
        if (isDirect && directLinkedObjectId >= 0)
        {
            var linkedOi = Data.LogisticsNetwork.ResolveObjectById(directLinkedObjectId);
            var linkedBodyLabelText = isGet ? $"Edit SEND on {BodyLabel(linkedOi)}" : $"Edit GET on {BodyLabel(linkedOi)}";
            AddBigButton(directLinkedButtonRow.transform, linkedBodyLabelText, _runtimeStyle.SmallButtonColor, () =>
            {
                if (linkedOi == null) return;
                _pendingDirectLinkedObjectId = directLinkedObjectId;
                _pendingDirectMode = true;
                _pendingTargetAmount = isGet ? targetAmount : currentAmount;
                _pendingCurrentAmount = currentAmount;
                _pendingPriority = priority;
                _pendingNetworkId = networkId;
                _hasPendingState = true;

                var linkedIsGet = !isGet;
                Data.LogisticsRequest linkedReq = null;
                Data.LogisticsProvider linkedProv = null;
                if (linkedIsGet)
                {
                    linkedReq = Data.LogisticsNetwork.FindLinkedDirectRequest(directLinkedObjectId, rd, capturedOi?.id ?? -1);
                    if (linkedReq == null)
                    {
                        EnsureLinkedDirectRequest(capturedOi, rd, directLinkedObjectId, priority);
                        linkedReq = Data.LogisticsNetwork.FindLinkedDirectRequest(directLinkedObjectId, rd, capturedOi?.id ?? -1);
                    }
                }
                else
                {
                    linkedProv = Data.LogisticsNetwork.FindLinkedDirectProvider(directLinkedObjectId, rd, capturedOi?.id ?? -1);
                    if (linkedProv == null)
                    {
                        EnsureLinkedDirectProvider(capturedOi, rd, directLinkedObjectId, priority);
                        linkedProv = Data.LogisticsNetwork.FindLinkedDirectProvider(directLinkedObjectId, rd, capturedOi?.id ?? -1);
                    }
                }

                _linkedReturnAction = () =>
                {
                    _pendingDirectLinkedObjectId = directLinkedObjectId;
                    _pendingDirectMode = true;
                    _pendingTargetAmount = isGet ? targetAmount : currentAmount;
                    _pendingCurrentAmount = currentAmount;
                    _pendingPriority = priority;
                    _pendingNetworkId = networkId;
                    _hasPendingState = true;
                    ShowAmountInput(section, rd, isGet, isAvailable, editRequest, editProvider, contextOi: capturedOi);
                };

                _pendingDirectMode = false;
                _pendingDirectLinkedObjectId = -1;
                _hasPendingState = false;

                ShowAmountInput(section, rd, linkedIsGet, isAvailable: true,
                    editRequest: linkedReq, editProvider: linkedProv, contextOi: linkedOi);
            }, tooltip: isGet ? "Edit the linked SEND rule on the source body" : "Edit the linked GET rule on the destination body");
        }
        else
        {
            directLinkedButtonRow.SetActive(false);
        }

        void RefreshRouteMode()
        {
            SetModeButtonColor(networkModeBtn, !isDirect);
            SetModeButtonColor(directModeBtn, isDirect);
            networkRow?.SetActive(!isDirect);
            directRow?.SetActive(isDirect);
            directLinkedButtonRow?.SetActive(isDirect && directLinkedObjectId >= 0);
            RefreshNetworkDisplay();
            RefreshDirectDisplay();
            RebuildSectionLayout(section);
        }

        void RefreshNetworkDisplay()
        {
            networkId = Data.LogisticsNetwork.ClampNetworkId(networkId);
            if (networkLabel != null)
                networkLabel.text = $"Network: {Data.LogisticsNetwork.NetworkLabel(networkId)}";
        }

        void RefreshDirectDisplay()
        {
            if (directBodyLabel == null) return;
            var linkedOi = directLinkedObjectId >= 0 ? Data.LogisticsNetwork.ResolveObjectById(directLinkedObjectId) : null;
            directBodyLabel.text = linkedOi != null ? BodyLabel(linkedOi) : "(none selected)";
        }

        RefreshRouteMode();

        var amountRow = MakeHLRow(section.ContentArea, 34f, 0);
        var amountDisplay = MakeTMP(amountRow.transform, "0", 22, Color.white);
        amountDisplay.alignment = TextAlignmentOptions.Center;
        TextMeshProUGUI targetSummary = null;
        TextMeshProUGUI minimumSummary = null;
        TMP_InputField amountInput = null;

        void UpdateAmountDisplay()
        {
            if (isGet)
            {
                if (!useMinimum)
                    editingMinimum = false;
                currentAmount = editingMinimum ? minimumAmount : targetAmount;
            }
            amountDisplay.text = FormatCompactAmount(currentAmount);

            if (isGet)
            {
                amountDisplay.text = (editingMinimum ? "Minimum: " : "Target: ") + amountDisplay.text;
                if (minimumAmount > targetAmount)
                    minimumAmount = targetAmount;
                if (targetSummary != null)
                    targetSummary.text = $"Target: {FormatCompactAmount(targetAmount)}";
                if (minimumSummary != null)
                    minimumSummary.text = useMinimum ? $"Minimum: {FormatCompactAmount(minimumAmount)}" : "Minimum: off";
            }
            else
            {
                amountDisplay.text = "Keep: " + amountDisplay.text;
            }

            if (amountInput != null && !amountInput.isFocused)
                amountInput.text = currentAmount.ToString("0.##");
        }

        void SetTypedAmount(double value)
        {
            value = System.Math.Max(0, value);
            if (isGet)
            {
                if (editingMinimum)
                    minimumAmount = useMinimum ? System.Math.Min(targetAmount, value) : 0;
                else
                {
                    targetAmount = value;
                    if (minimumAmount > targetAmount)
                        minimumAmount = targetAmount;
                }
            }
            else
            {
                currentAmount = value;
            }
            UpdateAmountDisplay();
        }

        MakeNumericInputRow(section.ContentArea, "Amount", currentAmount, SetTypedAmount, out amountInput);

        void AddAmount(double delta)
        {
            if (isGet)
            {
                if (editingMinimum)
                    minimumAmount = useMinimum ? System.Math.Max(0, System.Math.Min(targetAmount, minimumAmount + delta)) : 0;
                else
                {
                    targetAmount = System.Math.Max(0, targetAmount + delta);
                    if (minimumAmount > targetAmount)
                        minimumAmount = targetAmount;
                }
            }
            else
            {
                currentAmount = System.Math.Max(0, currentAmount + delta);
            }
            UpdateAmountDisplay();
        }

        var plusRow = MakeHLRow(section.ContentArea, 28f, 4);
        AddSmallButton(plusRow.transform, "+10", _runtimeStyle.SmallButtonPositiveColor, () => AddAmount(10), tooltip: "Increase amount by 10");
        AddSmallButton(plusRow.transform, "+100", _runtimeStyle.SmallButtonPositiveColor, () => AddAmount(100), tooltip: "Increase amount by 100");
        AddSmallButton(plusRow.transform, "+1K", _runtimeStyle.SmallButtonPositiveColor, () => AddAmount(1000), tooltip: "Increase amount by 1K");
        AddSmallButton(plusRow.transform, "+10K", _runtimeStyle.SmallButtonPositiveColor, () => AddAmount(10000), tooltip: "Increase amount by 10K");
        AddSmallButton(plusRow.transform, "+100K", _runtimeStyle.SmallButtonPositiveColor, () => AddAmount(100000), tooltip: "Increase amount by 100K");
        AddSmallButton(plusRow.transform, "+1M", _runtimeStyle.SmallButtonPositiveColor, () => AddAmount(1000000), tooltip: "Increase amount by 1M");

        var minusRow = MakeHLRow(section.ContentArea, 28f, 4);
        AddSmallButton(minusRow.transform, "−10", _runtimeStyle.SmallButtonColor, () => AddAmount(-10), tooltip: "Decrease amount by 10");
        AddSmallButton(minusRow.transform, "−100", _runtimeStyle.SmallButtonColor, () => AddAmount(-100), tooltip: "Decrease amount by 100");
        AddSmallButton(minusRow.transform, "−1K", _runtimeStyle.SmallButtonColor, () => AddAmount(-1000), tooltip: "Decrease amount by 1K");
        AddSmallButton(minusRow.transform, "−10K", _runtimeStyle.SmallButtonColor, () => AddAmount(-10000), tooltip: "Decrease amount by 10K");
        AddSmallButton(minusRow.transform, "−100K", _runtimeStyle.SmallButtonColor, () => AddAmount(-100000), tooltip: "Decrease amount by 100K");
        AddSmallButton(minusRow.transform, "−1M", _runtimeStyle.SmallButtonColor, () => AddAmount(-1000000), tooltip: "Decrease amount by 1M");

        if (isGet)
        {
            var editRow = MakeHLRow(section.ContentArea, 28f, 6);
            var editTargetGo = AddBigButtonInline(editRow.transform, "Edit Target", _runtimeStyle.ActionButtonColor, () =>
            {
                editingMinimum = false;
                UpdateAmountDisplay();
            }, tooltip: "Edit the target stock level to refill toward");
            var editMinimumGo = AddBigButtonInline(editRow.transform, "Edit Minimum", _runtimeStyle.ActionButtonColor, () =>
            {
                editingMinimum = useMinimum;
                UpdateAmountDisplay();
            }, tooltip: "Edit the minimum stock threshold that triggers refill");

            var minimumToggleRow = MakeHLRow(section.ContentArea, 28f, 6);
            TextMeshProUGUI minimumToggleLabel = null;
            void RefreshMinimumToggle()
            {
                if (minimumToggleLabel != null)
                    minimumToggleLabel.text = useMinimum ? "[X] Reorder Threshold" : "[ ] Reorder Threshold";
                editRow.SetActive(useMinimum);
                editTargetGo.SetActive(useMinimum);
                editMinimumGo.SetActive(useMinimum);
                RebuildSectionLayout(section);
            }
            var minimumToggleGo = MakeToggleButtonGo("MinimumToggle", minimumToggleRow.transform,
                "Enable min/target mode. Logistics waits for stock to fall below minimum, then ships toward target.");
            var minimumToggleButton = minimumToggleGo.GetComponent<Button>();
            minimumToggleLabel = MakeTMP(minimumToggleGo.transform, "", 13, new Color(0.8f, 0.8f, 0.82f, 1f));
            minimumToggleLabel.alignment = TextAlignmentOptions.Center;
            minimumToggleButton.onClick.AddListener(() =>
            {
                useMinimum = !useMinimum;
                if (!useMinimum)
                    editingMinimum = false;
                else if (minimumAmount <= 0 && targetAmount > 0)
                    minimumAmount = targetAmount;
                RefreshMinimumToggle();
                UpdateAmountDisplay();
            });
            RefreshMinimumToggle();

            var oneShotToggleRow = MakeHLRow(section.ContentArea, 28f, 6);
            TextMeshProUGUI oneShotToggleLabel = null;
            void RefreshOneShotToggle()
            {
                if (oneShotToggleLabel != null)
                    oneShotToggleLabel.text = oneShot ? "[X] One-shot request" : "[ ] One-shot request";
            }
            var oneShotToggleGo = MakeToggleButtonGo("OneShotToggle", oneShotToggleRow.transform,
                "Fulfill this import once, then remove the request after the target shipment is satisfied.");
            var oneShotToggleButton = oneShotToggleGo.GetComponent<Button>();
            oneShotToggleLabel = MakeTMP(oneShotToggleGo.transform, "", 13, new Color(0.8f, 0.8f, 0.82f, 1f));
            oneShotToggleLabel.alignment = TextAlignmentOptions.Center;
            oneShotToggleButton.onClick.AddListener(() =>
            {
                oneShot = !oneShot;
                RefreshOneShotToggle();
            });
            RefreshOneShotToggle();

            if (HasMatchingSellOffer(capturedOi, rd) || autoBuy)
            {
                var autoBuyToggleRow = MakeHLRow(section.ContentArea, 28f, 6);
                TextMeshProUGUI autoBuyToggleLabel = null;
                GameObject autoBuyInputRow = null;
                TMP_InputField autoBuyInput = null;
                void RefreshAutoBuyToggle()
                {
                    if (autoBuyToggleLabel != null)
                        autoBuyToggleLabel.text = autoBuy ? "[X] Auto-Buy from market" : "[ ] Auto-Buy from market";
                    autoBuyInputRow?.SetActive(autoBuy);
                    RebuildSectionLayout(section);
                }
                var autoBuyToggleGo = MakeToggleButtonGo("AutoBuyToggle", autoBuyToggleRow.transform,
                    "Buy from matching local market sell offers before planning logistics shipments.");
                var autoBuyToggleButton = autoBuyToggleGo.GetComponent<Button>();
                autoBuyToggleLabel = MakeTMP(autoBuyToggleGo.transform, "", 13, new Color(0.8f, 0.8f, 0.82f, 1f));
                autoBuyToggleLabel.alignment = TextAlignmentOptions.Center;
                autoBuyToggleButton.onClick.AddListener(() =>
                {
                    autoBuy = !autoBuy;
                    if (autoBuy && autoBuyMaxPrice <= 0)
                    {
                        autoBuyMaxPrice = GetLowestSellOfferPrice(capturedOi, rd);
                        if (autoBuyInput != null)
                            autoBuyInput.text = autoBuyMaxPrice.ToString("0.##");
                    }
                    RefreshAutoBuyToggle();
                });
                autoBuyInputRow = MakeNumericInputRow(section.ContentArea, "Max buy price", autoBuyMaxPrice, value => autoBuyMaxPrice = value, out autoBuyInput);
                RefreshAutoBuyToggle();
            }

            targetSummary = MakeTMP(section.ContentArea, "Target: 0", 12, new Color(0.7f, 0.7f, 0.75f, 1f));
            targetSummary.rectTransform.sizeDelta = new Vector2(0, 18);
            minimumSummary = MakeTMP(section.ContentArea, "Minimum: 0", 12, new Color(0.7f, 0.7f, 0.75f, 1f));
            minimumSummary.rectTransform.sizeDelta = new Vector2(0, 18);
        }
        else
        {
            var autoSellToggleRow = MakeHLRow(section.ContentArea, 28f, 6);
            TextMeshProUGUI autoSellToggleLabel = null;
            GameObject autoSellModeRow = null;
            GameObject autoSellMonthlyRow = null;
            GameObject autoSellPriceRow = null;
            GameObject exportOrbitMaxRow = null;
            GameObject continuousSellButton = null;
            GameObject monthlySellButton = null;
            TMP_InputField autoSellPriceInput = null;
            void RefreshAutoSellToggle()
            {
                if (autoSellToggleLabel != null)
                    autoSellToggleLabel.text = autoSell ? "[X] Auto-Sell surplus" : "[ ] Auto-Sell surplus";
                autoSellModeRow?.SetActive(autoSell);
                autoSellMonthlyRow?.SetActive(autoSell && autoSellMode == Data.AutoSellMode.PerMonth);
                autoSellPriceRow?.SetActive(autoSell);
                SetModeButtonColor(continuousSellButton, autoSellMode == Data.AutoSellMode.Continuous);
                SetModeButtonColor(monthlySellButton, autoSellMode == Data.AutoSellMode.PerMonth);
                RebuildSectionLayout(section);
            }
            var autoSellToggleGo = MakeToggleButtonGo("AutoSellToggle", autoSellToggleRow.transform,
                "Sell surplus above this reserve into matching local market buy offers.");
            var autoSellToggleButton = autoSellToggleGo.GetComponent<Button>();
            autoSellToggleLabel = MakeTMP(autoSellToggleGo.transform, "", 13, new Color(0.8f, 0.8f, 0.82f, 1f));
            autoSellToggleLabel.alignment = TextAlignmentOptions.Center;
            autoSellToggleButton.onClick.AddListener(() =>
            {
                autoSell = !autoSell;
                if (autoSell && autoSellMinPrice <= 0)
                {
                    autoSellMinPrice = GetHighestBuyOfferPrice(capturedOi, rd);
                    if (autoSellPriceInput != null)
                        autoSellPriceInput.text = autoSellMinPrice.ToString("0.##");
                }
                RefreshAutoSellToggle();
            });

            autoSellModeRow = MakeHLRow(section.ContentArea, 28f, 6);
            continuousSellButton = AddBigButtonInline(autoSellModeRow.transform, "Continuous Sell", _runtimeStyle.ActionButtonColor, () =>
            {
                autoSellMode = Data.AutoSellMode.Continuous;
                RefreshAutoSellToggle();
            }, tooltip: "Sell all eligible surplus whenever matching buy offers exist.");
            monthlySellButton = AddBigButtonInline(autoSellModeRow.transform, "Sell Per Month", _runtimeStyle.ActionButtonColor, () =>
            {
                autoSellMode = Data.AutoSellMode.PerMonth;
                RefreshAutoSellToggle();
            }, tooltip: "Limit auto-sell to the configured monthly amount.");
            autoSellMonthlyRow = MakeNumericInputRow(section.ContentArea, "Max sell per month", autoSellMaxPerMonth, value => autoSellMaxPerMonth = value);
            autoSellPriceRow = MakeNumericInputRow(section.ContentArea, "Minimum sell price", autoSellMinPrice, value => autoSellMinPrice = value, out autoSellPriceInput);
            RefreshAutoSellToggle();

            if (_currentObjectInfo != null && _currentObjectInfo.NeedVehicleToLaunch())
            {
                var exportOrbitToggleRow = MakeHLRow(section.ContentArea, 28f, 6);
                TextMeshProUGUI exportOrbitToggleLabel = null;
                void RefreshExportOrbitToggle()
                {
                    if (exportOrbitToggleLabel != null)
                        exportOrbitToggleLabel.text = exportToOrbit ? "[X] Export to Orbit" : "[ ] Export to Orbit";
                    exportOrbitMaxRow?.SetActive(exportToOrbit);
                    RebuildSectionLayout(section);
                }
                var exportOrbitToggleGo = MakeToggleButtonGo("ExportOrbitToggle", exportOrbitToggleRow.transform,
                    "Use enabled launch vehicles to stage this export resource to orbit.");
                var exportOrbitToggleButton = exportOrbitToggleGo.GetComponent<Button>();
                exportOrbitToggleLabel = MakeTMP(exportOrbitToggleGo.transform, "", 13, new Color(0.8f, 0.8f, 0.82f, 1f));
                exportOrbitToggleLabel.alignment = TextAlignmentOptions.Center;
                exportOrbitToggleButton.onClick.AddListener(() =>
                {
                    exportToOrbit = !exportToOrbit;
                    RefreshExportOrbitToggle();
                });
                exportOrbitMaxRow = MakeNumericInputRow(section.ContentArea, "Max stock in orbit", exportOrbitMaxStock, value => exportOrbitMaxStock = System.Math.Max(0, value));
                RefreshExportOrbitToggle();
            }
            MakeNumericInputRow(section.ContentArea, "Minimum shipment", minimumShipmentAmount, value => minimumShipmentAmount = System.Math.Max(0, value));

            var sharedPoolRow = MakeHLRow(section.ContentArea, 28f, 6);
            TextMeshProUGUI sharedPoolLabel = null;
            void RefreshSharedPoolToggle()
            {
                if (sharedPoolLabel != null)
                    sharedPoolLabel.text = useSharedSpacecraftPool ? "[X] Use shared logistics pool" : "[ ] Use shared logistics pool";
            }
            var sharedPoolToggleGo = MakeToggleButtonGo("SharedPoolToggle", sharedPoolRow.transform,
                "Allow this SEND order to use normal spacecraft quotas in addition to assigned ships.");
            var sharedPoolButton = sharedPoolToggleGo.GetComponent<Button>();
            sharedPoolLabel = MakeTMP(sharedPoolToggleGo.transform, "", 13, new Color(0.8f, 0.8f, 0.82f, 1f));
            sharedPoolLabel.alignment = TextAlignmentOptions.Center;
            sharedPoolButton.onClick.AddListener(() =>
            {
                useSharedSpacecraftPool = !useSharedSpacecraftPool;
                RefreshSharedPoolToggle();
            });
            RefreshSharedPoolToggle();

            var assignmentPanel = new GameObject("AssignedSpacecraftPicker", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
            assignmentPanel.transform.SetParent(section.ContentArea, false);
            var assignmentPanelLayout = assignmentPanel.GetComponent<VerticalLayoutGroup>();
            assignmentPanelLayout.spacing = 3;
            assignmentPanelLayout.childForceExpandWidth = true;
            assignmentPanelLayout.childControlWidth = true;
            assignmentPanelLayout.childControlHeight = true;
            var assignmentPanelLe = assignmentPanel.GetComponent<LayoutElement>();
            assignmentPanelLe.minHeight = 0;

            GameObject assignmentButton = null;
            TextMeshProUGUI assignmentButtonLabel = null;
            void RefreshAssignmentButton()
            {
                if (assignmentButtonLabel != null)
                    assignmentButtonLabel.text = assignedSpacecraftIds.Count > 0
                        ? $"Assign spacecraft ({assignedSpacecraftIds.Count})"
                        : "Assign spacecraft";
            }

            assignmentButton = AddBigButton(section.ContentArea, "Assign spacecraft", _runtimeStyle.ActionButtonColor, () =>
            {
                assignmentPanel.SetActive(!assignmentPanel.activeSelf);
                RebuildSectionLayout(section);
            }, tooltip: "Open the spacecraft assignment picker for this SEND order.");
            assignmentButtonLabel = assignmentButton.GetComponentInChildren<TextMeshProUGUI>();
            assignmentPanel.transform.SetSiblingIndex(assignmentButton.transform.GetSiblingIndex() + 1);

            Data.ProviderSpacecraftSetting GetLocalAssignedSetting(string typeKey)
            {
                var setting = assignedSpacecraftSettings.FirstOrDefault(s => SameUiKey(s.typeName, typeKey));
                if (setting == null)
                {
                    setting = new Data.ProviderSpacecraftSetting { typeName = typeKey, useFuelProbe = true };
                    assignedSpacecraftSettings.Add(setting);
                }
                return setting;
            }

            var assignmentRows = GetAssignableProviderShipTypeRows(capturedOi, editProvider, assignedSpacecraftIds).ToList();
            if (assignmentRows.Count == 0)
            {
                var noneRow = MakeHLRow(assignmentPanel.transform, 24f, 4);
                MakeTMP(noneRow.transform, "No unassigned spacecraft available beyond shared quota.", 12, new Color(0.5f, 0.5f, 0.55f, 1f));
            }
            else
            {
                foreach (var shipTypeRow in assignmentRows)
                {
                    var typeKey = shipTypeRow.typeKey;
                    var setting = GetLocalAssignedSetting(typeKey);
                    var row = MakeHLRow(assignmentPanel.transform, 26f, 4);
                    var rowImage = row.GetComponent<Image>();
                    GameObject minInputRow = null;

                    var label = MakeTMP(row.transform,
                        $"{ShipIcon(shipTypeRow.spriteId)} <b><color=#EEEEF0>{shipTypeRow.displayName}</color></b>{shipTypeRow.locationSuffix}  {shipTypeRow.assigned.Count}/{shipTypeRow.totalAssignable} assigned  cap {FormatCompactAmount(shipTypeRow.capacity)}",
                        12,
                        _runtimeStyle.RowTextColor);
                    var labelLayout = label.gameObject.AddComponent<LayoutElement>();
                    labelLayout.flexibleWidth = 1f;
                    labelLayout.preferredWidth = 0;
                    GameObject transferBtn = null;
                    TextMeshProUGUI transferBtnLabel = null;
                    GameObject backhaulBtn = null;
                    TextMeshProUGUI backhaulBtnLabel = null;
                    GameObject fuelBtn = null;
                    TextMeshProUGUI fuelBtnLabel = null;

                    void RefreshAssignmentRow()
                    {
                        if (rowImage != null)
                            rowImage.color = shipTypeRow.assigned.Any(id => assignedSpacecraftIds.Contains(id))
                                ? _runtimeStyle.ToggleOnColor
                                : _runtimeStyle.RowBackgroundColor;
                        label.text = $"{ShipIcon(shipTypeRow.spriteId)} <b><color=#EEEEF0>{shipTypeRow.displayName}</color></b>{shipTypeRow.locationSuffix}  {shipTypeRow.assigned.Count(id => assignedSpacecraftIds.Contains(id))}/{shipTypeRow.totalAssignable} assigned  cap {FormatCompactAmount(shipTypeRow.capacity)}";
                        if (transferBtnLabel != null)
                            transferBtnLabel.text = setting.useFastestTransfer ? "[x] Fast" : "[ ] Fast";
                        if (transferBtn != null)
                            SetButtonBaseColor(transferBtn, setting.useFastestTransfer ? new Color(0.35f, 0.58f, 0.82f, 1f) : new Color(0.33f, 0.43f, 0.34f, 1f));
                        if (backhaulBtnLabel != null)
                            backhaulBtnLabel.text = setting.backhaul ? "[x] Back" : "[ ] Back";
                        if (backhaulBtn != null)
                            SetButtonBaseColor(backhaulBtn, setting.backhaul ? new Color(0.6f, 0.45f, 0.82f, 1f) : new Color(0.33f, 0.43f, 0.34f, 1f));
                        if (fuelBtnLabel != null)
                            fuelBtnLabel.text = setting.useFuelProbe ? "[x] Fuel" : "[ ] Fuel";
                        if (fuelBtn != null)
                            SetButtonBaseColor(fuelBtn, setting.useFuelProbe ? new Color(0.35f, 0.58f, 0.82f, 1f) : new Color(0.33f, 0.43f, 0.34f, 1f));
                    }

                    AddSmallButton(row.transform, "-", _runtimeStyle.SmallButtonColor, () =>
                    {
                        var id = shipTypeRow.assigned
                            .Where(assignedSpacecraftIds.Contains)
                            .Select(candidateId => (int?)candidateId)
                            .LastOrDefault();
                        if (id.HasValue)
                        {
                            assignedSpacecraftIds.Remove(id.Value);
                        }
                        RefreshAssignmentRow();
                        RefreshAssignmentButton();
                    }, width: 34f, tooltip: "Remove one assigned spacecraft of this type");

                    AddSmallButton(row.transform, "+", _runtimeStyle.SmallButtonPositiveColor, () =>
                    {
                        var id = shipTypeRow.addCandidates
                            .Where(candidateId => !assignedSpacecraftIds.Contains(candidateId))
                            .Select(candidateId => (int?)candidateId)
                            .FirstOrDefault();
                        if (id.HasValue)
                        {
                            assignedSpacecraftIds.Add(id.Value);
                            if (!shipTypeRow.assigned.Contains(id.Value))
                                shipTypeRow.assigned.Add(id.Value);
                        }
                        RefreshAssignmentRow();
                        RefreshAssignmentButton();
                    }, width: 34f, tooltip: "Assign one available spacecraft of this type");

                    var transferLabel = setting.useFastestTransfer ? "[x] Fast" : "[ ] Fast";
                    var transferColor = setting.useFastestTransfer
                        ? new Color(0.35f, 0.58f, 0.82f, 1f)
                        : new Color(0.33f, 0.43f, 0.34f, 1f);
                    transferBtn = AddSmallButton(row.transform, transferLabel, transferColor, () =>
                    {
                        setting.useFastestTransfer = !setting.useFastestTransfer;
                        RefreshAssignmentRow();
                    }, tooltip: "Toggle fastest transfer for assigned ships of this type");
                    transferBtnLabel = transferBtn.GetComponentInChildren<TextMeshProUGUI>();

                    var backhaulLabel = setting.backhaul ? "[x] Back" : "[ ] Back";
                    var backhaulColor = setting.backhaul
                        ? new Color(0.6f, 0.45f, 0.82f, 1f)
                        : new Color(0.33f, 0.43f, 0.34f, 1f);
                    backhaulBtn = AddSmallButton(row.transform, backhaulLabel, backhaulColor, () =>
                    {
                        setting.backhaul = !setting.backhaul;
                        RefreshAssignmentRow();
                    }, tooltip: "Toggle backhaul for assigned ships of this type");
                    backhaulBtnLabel = backhaulBtn.GetComponentInChildren<TextMeshProUGUI>();

                    var fuelLabel = setting.useFuelProbe ? "[x] Fuel" : "[ ] Fuel";
                    var fuelColor = setting.useFuelProbe
                        ? new Color(0.35f, 0.58f, 0.82f, 1f)
                        : new Color(0.33f, 0.43f, 0.34f, 1f);
                    fuelBtn = AddSmallButton(row.transform, fuelLabel, fuelColor, () =>
                    {
                        setting.useFuelProbe = !setting.useFuelProbe;
                        RefreshAssignmentRow();
                    }, width: 58f, tooltip: "Toggle return-fuel handling for assigned ships of this type");
                    fuelBtnLabel = fuelBtn.GetComponentInChildren<TextMeshProUGUI>();

                    AddSmallButton(row.transform, "refill at", _runtimeStyle.SmallButtonColor, () =>
                    {
                        if (minInputRow != null)
                        {
                            minInputRow.SetActive(!minInputRow.activeSelf);
                            RebuildSectionLayout(section);
                        }
                    }, tooltip: "Set minimum shipment size for assigned ships of this type");

                    minInputRow = MakeNumericInputRow(assignmentPanel.transform, "Minimum shipment", setting.minimumShipmentAmount, value =>
                    {
                        setting.minimumShipmentAmount = Math.Max(0, value);
                    });
                    minInputRow.SetActive(false);

                    RefreshAssignmentRow();
                }
            }

            assignmentPanel.SetActive(false);
            RefreshAssignmentButton();
        }

        void DoConfirm()
        {
            if (_inputConfirmed) return;
            _inputConfirmed = true;
            var finalAmount = isGet ? targetAmount : currentAmount;
            if (isGet ? finalAmount > 0 : finalAmount >= 0)
            {
                if (editRequest != null)
                {
                    editRequest.requestedAmount = targetAmount;
                    editRequest.minimumAmount = System.Math.Max(0, System.Math.Min(minimumAmount, targetAmount));
                    editRequest.useMinimumAmount = useMinimum;
                    editRequest.reorderActive = false;
                    editRequest.oneShot = oneShot;
                    editRequest.autoBuy = autoBuy;
                    editRequest.autoBuyMaxPrice = autoBuyMaxPrice;
                    editRequest.priority = ClampPriority(priority);
                    editRequest.networkId = isDirect ? 0 : Data.LogisticsNetwork.ClampNetworkId(networkId);
                    editRequest.isDirect = isDirect;
                    editRequest.directLinkedObjectId = isDirect ? directLinkedObjectId : -1;
                    editRequest.status = Data.LogisticsRequestStatus.Pending;
                    editRequest.statusNote = null;
                    editRequest.relayStage = Data.RelayStage.None;
                    editRequest.relaySourceObjectId = -1;
                    editRequest.relayOrbitObjectId = -1;
                    editRequest.relayFinalTargetObjectId = capturedOi?.id ?? -1;
                    if (isDirect && directLinkedObjectId >= 0 && _linkedReturnAction == null)
                        EnsureLinkedDirectProvider(capturedOi, rd, directLinkedObjectId, ClampPriority(priority));
                }
                else if (editProvider != null)
                {
                    editProvider.minimumKeep = currentAmount;
                    editProvider.autoSell = autoSell;
                    editProvider.autoSellMode = autoSellMode;
                    editProvider.autoSellMaxPerMonth = autoSellMaxPerMonth;
                    editProvider.autoSellMinPrice = autoSellMinPrice;
                    editProvider.exportToOrbit = exportToOrbit;
                    editProvider.minimumShipmentAmount = System.Math.Max(0, minimumShipmentAmount);
                    editProvider.exportOrbitMaxStock = System.Math.Max(0, exportOrbitMaxStock);
                    editProvider.useSharedSpacecraftPool = useSharedSpacecraftPool;
                    editProvider.assignedSpacecraftIds = assignedSpacecraftIds.Where(id => id >= 0).Distinct().ToList();
                    editProvider.assignedSpacecraftSettings = assignedSpacecraftSettings
                        .Where(s => s != null && !string.IsNullOrWhiteSpace(s.typeName))
                        .ToList();
                    editProvider.priority = ClampPriority(priority);
                    editProvider.networkId = isDirect ? 0 : Data.LogisticsNetwork.ClampNetworkId(networkId);
                    editProvider.isDirect = isDirect;
                    editProvider.directLinkedObjectId = isDirect ? directLinkedObjectId : -1;
                    if (isDirect && directLinkedObjectId >= 0 && _linkedReturnAction == null)
                        EnsureLinkedDirectRequest(capturedOi, rd, directLinkedObjectId, ClampPriority(priority));
                }
                else if (isGet)
                {
                    var req = Data.LogisticsNetwork.AddRequest(capturedOi, rd, targetAmount, minimumAmount, useMinimum, oneShot, autoBuy, autoBuyMaxPrice, ClampPriority(priority), isDirect ? 0 : Data.LogisticsNetwork.ClampNetworkId(networkId));
                    if (req != null && isDirect && directLinkedObjectId >= 0)
                    {
                        req.isDirect = true;
                        req.directLinkedObjectId = directLinkedObjectId;
                        EnsureLinkedDirectProvider(capturedOi, rd, directLinkedObjectId, ClampPriority(priority));
                    }
                }
                else
                {
                    var prov = Data.LogisticsNetwork.AddProvider(capturedOi, rd, currentAmount, autoSell, autoSellMode, autoSellMaxPerMonth, autoSellMinPrice, ClampPriority(priority), exportToOrbit, minimumShipmentAmount, exportOrbitMaxStock, isDirect ? 0 : Data.LogisticsNetwork.ClampNetworkId(networkId), useSharedSpacecraftPool, assignedSpacecraftIds, assignedSpacecraftSettings);
                    if (prov != null && isDirect && directLinkedObjectId >= 0)
                    {
                        prov.isDirect = true;
                        prov.directLinkedObjectId = directLinkedObjectId;
                        EnsureLinkedDirectRequest(capturedOi, rd, directLinkedObjectId, ClampPriority(priority));
                    }
                }
            }
            var returnAction = _linkedReturnAction;
            _linkedReturnAction = null;
            if (returnAction != null)
                returnAction();
            else
            {
                if (isGet) BuildGetSection(); else BuildSendSection();
                RebuildSectionLayout(section);
            }
        }

        var confirmRow = new GameObject("ConfirmRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        confirmRow.transform.SetParent(section.ContentArea, false);
        confirmRow.GetComponent<LayoutElement>().preferredHeight = 32f;
        var crHLG = confirmRow.GetComponent<HorizontalLayoutGroup>();
        crHLG.spacing = 8;

        AddBigButtonInline(confirmRow.transform, "Confirm", _runtimeStyle.ConfirmButtonColor, () => DoConfirm(), tooltip: "Save this logistics rule");
        AddBigButtonInline(confirmRow.transform, "Cancel", _runtimeStyle.BackButtonColor, () =>
        {
            _inputConfirmed = true;
            var cancelReturn = _linkedReturnAction;
            _linkedReturnAction = null;
            if (cancelReturn != null)
            {
                cancelReturn();
            }
            else if (isEditing)
            {
                if (isGet) BuildGetSection(); else BuildSendSection();
                RebuildSectionLayout(section);
            }
            else
            {
                ShowResourcePicker(section, isGet);
            }
        }, tooltip: "Discard changes and return");

        UpdateAmountDisplay();
        RebuildSectionLayout(section);
    }

    private static int ClampPriority(int priority)
    {
        return System.Math.Max(-1, System.Math.Min(2, priority));
    }

    private static string PriorityLabel(int priority)
    {
        return ClampPriority(priority) switch
        {
            -1 => "Low",
            1 => "High",
            2 => "Critical",
            _ => "Normal"
        };
    }

    private static string PrioritySummary(int priority)
    {
        priority = ClampPriority(priority);
        return priority == 0 ? string.Empty : $" priority {PriorityLabel(priority)}";
    }

    private static string PriorityColor(int priority)
    {
        return ClampPriority(priority) switch
        {
            -1 => "#7799AA",
            1 => "#DDAA44",
            2 => "#DD5544",
            _ => "#A8A8A8"
        };
    }

    private static bool HasMatchingSellOffer(ObjectInfo oi, ResourceDefinition rd)
    {
        return GetMatchingSellOffers(oi, rd).Any();
    }

    private static double GetLowestSellOfferPrice(ObjectInfo oi, ResourceDefinition rd)
    {
        return GetMatchingSellOffers(oi, rd).Select(o => o.PricePerUnit).DefaultIfEmpty(0).Min();
    }

    private static double GetHighestBuyOfferPrice(ObjectInfo oi, ResourceDefinition rd)
    {
        return GetMatchingBuyOffers(oi, rd).Select(o => o.PricePerUnit).DefaultIfEmpty(0).Max();
    }

    private static IEnumerable<Offer> GetMatchingSellOffers(ObjectInfo oi, ResourceDefinition rd)
    {
        return GetMatchingOffers(oi, rd, buySell: false);
    }

    private static IEnumerable<Offer> GetMatchingBuyOffers(ObjectInfo oi, ResourceDefinition rd)
    {
        return GetMatchingOffers(oi, rd, buySell: true);
    }

    private static IEnumerable<Offer> GetMatchingOffers(ObjectInfo oi, ResourceDefinition rd, bool buySell)
    {
        var offers = MonoBehaviourSingleton<MarketOfferManager>.Instance?.Offerts;
        if (oi == null || rd == null || offers == null)
            return Enumerable.Empty<Offer>();
        return offers.Where(offer => offer != null
            && !offer.OfferDone
            && offer.WhereOffer == oi
            && offer.Rd == rd
            && offer.BuySell == buySell
            && offer.CountLeft > 0);
    }

    private GameObject MakeNumericInputRow(Transform parent, string label, double value, Action<double> onValueChanged)
    {
        return MakeNumericInputRow(parent, label, value, onValueChanged, out _);
    }

    private GameObject MakeNumericInputRow(Transform parent, string label, double value, Action<double> onValueChanged, out TMP_InputField input)
    {
        var row = MakeHLRow(parent, 30f, 8);
        var labelTmp = MakeTMP(row.transform, label, 12, _runtimeStyle.RowTextColor);
        labelTmp.alignment = TextAlignmentOptions.MidlineLeft;
        var labelLe = labelTmp.gameObject.AddComponent<LayoutElement>();
        labelLe.minWidth = 150f;
        labelLe.flexibleWidth = 1f;
        input = MakeNumericInput(row.transform, value, onValueChanged);
        return row;
    }

    private TMP_InputField MakeNumericInput(Transform parent, double value, Action<double> onValueChanged)
    {
        var inputGo = new GameObject("NumericInput", typeof(RectTransform), typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
        inputGo.transform.SetParent(parent, false);
        var background = inputGo.GetComponent<Image>();
        background.color = new Color(0.06f, 0.06f, 0.075f, 0.98f);
        var layout = inputGo.GetComponent<LayoutElement>();
        layout.preferredWidth = 120f;
        layout.preferredHeight = 24f;

        var text = MakeTMP(inputGo.transform, value > 0 ? value.ToString("0.##") : "0", 12, new Color(0.9f, 0.9f, 0.92f, 1f));
        text.richText = false;
        text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.rectTransform.offsetMin = new Vector2(6, 1);
        text.rectTransform.offsetMax = new Vector2(-6, -1);

        var placeholder = MakeTMP(inputGo.transform, "0", 12, new Color(0.45f, 0.45f, 0.48f, 1f));
        placeholder.richText = false;
        placeholder.raycastTarget = false;
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.rectTransform.offsetMin = new Vector2(6, 1);
        placeholder.rectTransform.offsetMax = new Vector2(-6, -1);

        var input = inputGo.GetComponent<TMP_InputField>();
        input.targetGraphic = background;
        input.textViewport = inputGo.GetComponent<RectTransform>();
        input.textComponent = text;
        input.placeholder = placeholder;
        input.contentType = TMP_InputField.ContentType.DecimalNumber;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.customCaretColor = true;
        input.caretColor = new Color(0.9f, 0.9f, 0.92f, 1f);
        input.caretWidth = 2;
        input.caretBlinkRate = 0.85f;
        input.selectionColor = new Color(0.25f, 0.55f, 0.9f, 0.35f);
        input.text = value > 0 ? value.ToString("0.##") : "0";
        input.onEndEdit.AddListener(raw =>
        {
            if (!double.TryParse(raw, out var parsed) || parsed < 0)
                parsed = 0;
            input.text = parsed.ToString("0.##");
            onValueChanged?.Invoke(parsed);
        });

        // Force TMP_InputField to re-run OnEnable now that textComponent/textViewport are assigned.
        // Without this, the caret doesn't blink because OnEnable fired during construction before properties were set.
        inputGo.SetActive(false);
        inputGo.SetActive(true);

        return input;
    }

    private GameObject MakeHLRow(Transform parent, float height, float spacing)
    {
        var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(Image), typeof(LayoutElement), typeof(ContentSizeFitter));
        row.transform.SetParent(parent, false);
        var le = row.GetComponent<LayoutElement>();
        le.minHeight = height;
        var rowBg = row.GetComponent<Image>();
        rowBg.sprite = null;
        rowBg.color = _runtimeStyle.RowBackgroundColor;
        var hlg = row.GetComponent<HorizontalLayoutGroup>();
        hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = false;
        hlg.childControlWidth = true; hlg.childControlHeight = true;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.spacing = spacing;
        hlg.padding = new RectOffset(8, 8, 4, 4);
        var fitter = row.GetComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        return row;
    }

    private static void SetTooltip(GameObject go, string tooltip)
    {
        if (go == null || string.IsNullOrEmpty(tooltip)) return;
        var tt = go.GetComponent<ShowToolTip>() ?? go.AddComponent<ShowToolTip>();
        Traverse.Create(tt).Field("showCustomFromCode").SetValue(true);
        Traverse.Create(tt).Field("afterTime").SetValue(0.15f);
        tt.CustomTextFromCode = tooltip;
    }

    private void MakeXButton(Transform parent, UnityEngine.Events.UnityAction onClick, string tooltip = null)
    {
        var btnGo = new GameObject("XBtn", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        btnGo.transform.SetParent(parent, false);
        var le = btnGo.GetComponent<LayoutElement>();
        le.preferredWidth = 24f;
        le.preferredHeight = 24f;
        var bg = btnGo.GetComponent<Image>();
        bg.sprite = null;
        bg.type = UnityEngine.UI.Image.Type.Simple;
        bg.color = _runtimeStyle.RemoveButtonColor;
        bg.raycastTarget = true;
        var btn = btnGo.GetComponent<Button>();
        btn.targetGraphic = bg;
        btn.navigation = new Navigation { mode = Navigation.Mode.None };
        btn.colors = MakeColorBlock(_runtimeStyle.RemoveButtonColor);
        var tmp = MakeTMP(btnGo.transform, "X", 12, new Color(0.92f, 0.88f, 0.88f, 1f));
        tmp.alignment = TextAlignmentOptions.Center;
        btn.onClick.AddListener(onClick);
        SetTooltip(btnGo, tooltip ?? "Remove this rule");
    }

    private GameObject AddBigButton(Transform parent, string text, Color color, UnityEngine.Events.UnityAction onClick, string tooltip = null)
    {
        return AddBigButtonInline(parent, text, color, onClick, tooltip);
    }

    private void SetModeButtonColor(GameObject buttonGo, bool selected)
    {
        if (buttonGo == null) return;
        var image = buttonGo.GetComponent<Image>();
        if (image == null) return;

        image.color = selected
            ? _runtimeStyle.ActionButtonColor
            : new Color(
                _runtimeStyle.ActionButtonColor.r * 0.55f,
                _runtimeStyle.ActionButtonColor.g * 0.55f,
                _runtimeStyle.ActionButtonColor.b * 0.55f,
                _runtimeStyle.ActionButtonColor.a);
    }

    private static void SetButtonBaseColor(GameObject buttonGo, Color color)
    {
        if (buttonGo == null) return;
        var image = buttonGo.GetComponent<Image>();
        if (image != null)
            image.color = color;
        var button = buttonGo.GetComponent<Button>();
        if (button != null)
            button.colors = MakeColorBlock(color);
    }

    private GameObject AddBigButtonInline(Transform parent, string text, Color color, UnityEngine.Events.UnityAction onClick, string tooltip = null)
    {
        var btnGo = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        btnGo.transform.SetParent(parent, false);
        var layout = btnGo.GetComponent<LayoutElement>();
        layout.preferredHeight = 28f;
        layout.minWidth = 120f;
        layout.flexibleWidth = 1f;
        var bg = btnGo.GetComponent<Image>();
        bg.sprite = null;
        bg.color = color;
        var btn = btnGo.GetComponent<Button>();
        btn.targetGraphic = bg;
        btn.navigation = new Navigation { mode = Navigation.Mode.None };
        btn.colors = MakeColorBlock(color);

        var labelTmp = MakeTMP(btnGo.transform, text, 14, new Color(0.86f, 0.86f, 0.88f, 1f));
        labelTmp.alignment = TextAlignmentOptions.Center;
        labelTmp.rectTransform.offsetMin = new Vector2(8, 2);
        labelTmp.rectTransform.offsetMax = new Vector2(-8, -2);

        btn.onClick.AddListener(onClick);
        SetTooltip(btnGo, tooltip);
        return btnGo;
    }

    private GameObject AddSmallButton(Transform parent, string text, Color color, UnityEngine.Events.UnityAction onClick, float width = 46f, string tooltip = null)
    {
        var btnGo = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        btnGo.transform.SetParent(parent, false);
        var le = btnGo.GetComponent<LayoutElement>();
        le.preferredWidth = width;
        le.preferredHeight = 24f;
        var bg = btnGo.GetComponent<Image>();
        bg.sprite = null;
        bg.color = color;
        var btn = btnGo.GetComponent<Button>();
        btn.targetGraphic = bg;
        btn.navigation = new Navigation { mode = Navigation.Mode.None };
        btn.colors = MakeColorBlock(color);

        var labelTmp = MakeTMP(btnGo.transform, text, 12, new Color(0.86f, 0.86f, 0.88f, 1f));
        labelTmp.alignment = TextAlignmentOptions.Center;
        labelTmp.rectTransform.offsetMin = new Vector2(4, 1);
        labelTmp.rectTransform.offsetMax = new Vector2(-4, -1);

        btn.onClick.AddListener(onClick);
        SetTooltip(btnGo, tooltip);
        return btnGo;
    }

    private TextMeshProUGUI MakeTMP(Transform parent, string text, float fontSize, Color color)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(4, 2); rt.offsetMax = new Vector2(-4, -2);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text; tmp.font = _font; tmp.fontSize = fontSize; tmp.color = color;
        if (_fontMaterial != null) tmp.fontSharedMaterial = _fontMaterial;
        tmp.richText = true;
        return tmp;
    }

    private static string ResourceLabel(ResourceDefinition rd, string fallbackId = null)
    {
        if (rd == null) return fallbackId?.ToUpper() ?? "?";
        var name = LEManager.Get(rd.ID, rd.ID);
        return $"{rd.IconString} {name.ToUpper()}";
    }

    private static ColorBlock MakeColorBlock(Color normal)
    {
        // Use multiplier-style tinting: Image.color holds the base color,
        // ColorBlock multiplies it. normalColor=white means "show base as-is".
        // fadeDuration=0 prevents the visible flash when buttons are recreated
        // under the mouse cursor during section rebuilds — the correct state
        // (normal or highlighted) applies instantly with no transition.
        return new ColorBlock
        {
            normalColor = Color.white,
            highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f),
            pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f),
            selectedColor = Color.white,
            disabledColor = new Color(0.5f, 0.5f, 0.5f, 1f),
            colorMultiplier = 1f,
            fadeDuration = 0f
        };
    }

    private GameObject MakeToggleButtonGo(string name, Transform parent, string tooltip = null)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        var layout = go.GetComponent<LayoutElement>();
        layout.preferredHeight = 28f;
        layout.minWidth = 160f;
        layout.flexibleWidth = 1f;
        var bg = go.GetComponent<Image>();
        bg.sprite = null;
        bg.color = _runtimeStyle.RowBackgroundColor;
        var btn = go.GetComponent<Button>();
        btn.targetGraphic = bg;
        btn.navigation = new Navigation { mode = Navigation.Mode.None };
        btn.colors = MakeColorBlock(_runtimeStyle.RowBackgroundColor);
        SetTooltip(go, tooltip);
        return go;
    }

    private static string FormatCompactAmount(double amount)
    {
        var format = LEManager.Get("UI.MassFormat");
        return amount.ToPostfixString(format);
    }

    private static string ShipDisplayName(string typeKey, bool isSpacecraft)
    {
        if (string.IsNullOrEmpty(typeKey)) return "?";

        if (isSpacecraft)
        {
            foreach (var sc in UnityEngine.Object.FindObjectsOfType<Spacecraft>())
            {
                var type = sc?.spacecraftType;
                if (type == null) continue;
                if (sc.GetCompany() != MonoBehaviourSingleton<GameManager>.Instance?.Player) continue;
                if (Data.LogisticsNetwork.TypeKey(type.ID, type.NameRocketType ?? "SC") == typeKey || type.NameRocketType == typeKey)
                    return ShipIcon(type.SpriteId) + " " + (type.NameRocketType ?? "SC").ToUpper();
            }
        }
        else
        {
            foreach (var lv in UnityEngine.Object.FindObjectsOfType<LaunchVehicle>())
            {
                var type = lv?.launchVehicleType;
                if (type == null) continue;
                if (lv.GetCompany() != MonoBehaviourSingleton<GameManager>.Instance?.Player) continue;
                if (Data.LogisticsNetwork.TypeKey(type.ID, type.Name ?? "LV") == typeKey || type.Name == typeKey)
                    return ShipIcon(type.SpriteId) + " " + (type.Name ?? "LV").ToUpper();
            }
        }

        return typeKey.ToUpper();
    }

    private IEnumerable<ObjectInfo> GetQuotaDisplayObjects(bool isSpacecraft)
    {
        if (_currentObjectInfo == null)
            yield break;

        yield return _currentObjectInfo;

        if (!isSpacecraft)
            yield break;

        if (_currentObjectInfo.objectTypes == global::Data.EObjectTypes.Orbit)
            yield break;

        var orbit = _currentObjectInfo.LowOrbitCustom?.GetObjectInfo();
        if (orbit != null && orbit != _currentObjectInfo)
            yield return orbit;
    }

    private sealed class ProviderShipTypeAssignmentRow
    {
        public string typeKey;
        public string displayName;
        public string spriteId;
        public string locationSuffix;
        public double capacity;
        public int totalAssignable;
        public List<int> assigned = new List<int>();
        public List<int> addCandidates = new List<int>();
    }

    private IEnumerable<ProviderShipTypeAssignmentRow> GetAssignableProviderShipTypeRows(ObjectInfo providerObject, Data.LogisticsProvider currentProvider, HashSet<int> currentAssignedIds)
    {
        var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
        if (providerObject == null || player == null)
            yield break;

        var allowedLocations = new List<ObjectInfo> { providerObject };
        if (providerObject.objectTypes != global::Data.EObjectTypes.Orbit)
        {
            var orbit = providerObject.LowOrbitCustom?.GetObjectInfo();
            if (orbit != null && orbit != providerObject)
                allowedLocations.Add(orbit);
        }

        var ships = MonoBehaviourSingleton<ShipManager>.Instance?.ListAllSpaceShip
            ?? UnityEngine.Object.FindObjectsOfType<Spacecraft>().ToList();

        var rows = new Dictionary<string, ProviderShipTypeAssignmentRow>();
        foreach (var sc in ships
            .Where(sc =>
            {
                if (sc == null || sc.ID < 0 || sc.spacecraftType == null || sc.GetCompany() != player)
                    return false;
                if (Data.LogisticsNetwork.IsSpacecraftAssignedToProvider(sc.ID, currentProvider))
                    return true;
                return sc.CurrentPhase == Spacecraft.EPhase.None
                    && allowedLocations.Contains(sc.CurrentlyOnThisObject)
                    && !Data.LogisticsNetwork.IsSpacecraftAssignedToOtherProvider(sc.ID, currentProvider);
            })
            .OrderBy(sc => allowedLocations.Contains(sc.CurrentlyOnThisObject) ? allowedLocations.IndexOf(sc.CurrentlyOnThisObject) : int.MaxValue)
            .ThenBy(sc => sc.spacecraftType?.NameRocketType ?? "SC", StringComparer.OrdinalIgnoreCase)
            .ThenBy(sc => sc.GetSpacecraftName(), StringComparer.OrdinalIgnoreCase))
        {
            var suffix = sc.CurrentlyOnThisObject == providerObject
                ? ""
                : allowedLocations.Contains(sc.CurrentlyOnThisObject)
                    ? " [ORBIT]"
                    : " [AWAY]";
            var type = sc.spacecraftType;
            var typeKey = Data.LogisticsNetwork.TypeKey(type.ID, type.NameRocketType ?? "SC");
            var quotaLocation = allowedLocations.Contains(sc.CurrentlyOnThisObject) ? sc.CurrentlyOnThisObject : providerObject;
            var rowKey = $"{quotaLocation?.id ?? -1}:{typeKey}";
            if (!rows.TryGetValue(rowKey, out var row))
            {
                row = new ProviderShipTypeAssignmentRow
                {
                    typeKey = typeKey,
                    displayName = (type.NameRocketType ?? "SC").ToUpper(),
                    spriteId = type.SpriteId,
                    locationSuffix = suffix,
                    capacity = type.GetCargoCapacity(player)
                };
                rows[rowKey] = row;
            }

            if (currentAssignedIds.Contains(sc.ID))
            {
                row.assigned.Add(sc.ID);
                row.totalAssignable++;
            }
            else if (allowedLocations.Contains(sc.CurrentlyOnThisObject))
            {
                row.addCandidates.Add(sc.ID);
            }
        }

        foreach (var rowGroup in rows.Values)
        {
            var location = rowGroup.locationSuffix == " [ORBIT]"
                ? providerObject.LowOrbitCustom?.GetObjectInfo()
                : providerObject;
            var sharedQuota = Data.LogisticsNetwork.GetQuota(location, rowGroup.typeKey, true);
            if (sharedQuota > 0 && rowGroup.addCandidates.Count > 0)
                rowGroup.addCandidates = rowGroup.addCandidates.Skip(Math.Min(sharedQuota, rowGroup.addCandidates.Count)).ToList();
            rowGroup.totalAssignable += rowGroup.addCandidates.Count;
        }

        foreach (var row in rows.Values
            .Where(row => row.totalAssignable > 0)
            .OrderBy(row => row.locationSuffix)
            .ThenBy(row => row.displayName, StringComparer.OrdinalIgnoreCase))
        {
            yield return row;
        }
    }

    private static bool SameUiKey(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private string ShipDisplayNameForQuotaObject(string typeKey, bool isSpacecraft, ObjectInfo quotaObject)
    {
        var name = ShipDisplayName(typeKey, isSpacecraft);
        if (!isSpacecraft || quotaObject == null || _currentObjectInfo == null)
            return name;

        var surfaceOrbit = _currentObjectInfo.LowOrbitCustom?.GetObjectInfo();
        if (_currentObjectInfo.objectTypes != global::Data.EObjectTypes.Orbit && quotaObject == surfaceOrbit)
            return $"{name} [ORBIT]";

        return name;
    }

    private static string ShipIcon(string spriteId)
    {
        if (string.IsNullOrEmpty(spriteId)) return "";
        var objManager = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
        return objManager != null ? objManager.spriteTextStart5.MyFormat(spriteId, "") : "";
    }

    private TMP_InputField MakeTextFilterInput(Transform parent, string placeholderText)
    {
        var inputGo = new GameObject("FilterInput", typeof(RectTransform), typeof(Image), typeof(TMP_InputField), typeof(LayoutElement));
        inputGo.transform.SetParent(parent, false);
        var background = inputGo.GetComponent<Image>();
        background.color = new Color(0.06f, 0.06f, 0.075f, 0.98f);
        var layout = inputGo.GetComponent<LayoutElement>();
        layout.preferredHeight = 28f;
        layout.flexibleWidth = 1f;

        var text = MakeTMP(inputGo.transform, "", 13, new Color(0.9f, 0.9f, 0.92f, 1f));
        text.richText = false;
        text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.rectTransform.offsetMin = new Vector2(8, 1);
        text.rectTransform.offsetMax = new Vector2(-8, -1);

        var placeholder = MakeTMP(inputGo.transform, placeholderText ?? "", 13, new Color(0.45f, 0.45f, 0.48f, 1f));
        placeholder.richText = false;
        placeholder.raycastTarget = false;
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.rectTransform.offsetMin = new Vector2(8, 1);
        placeholder.rectTransform.offsetMax = new Vector2(-8, -1);

        var input = inputGo.GetComponent<TMP_InputField>();
        input.targetGraphic = background;
        input.textViewport = inputGo.GetComponent<RectTransform>();
        input.textComponent = text;
        input.placeholder = placeholder;
        input.contentType = TMP_InputField.ContentType.Standard;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.customCaretColor = true;
        input.caretColor = new Color(0.9f, 0.9f, 0.92f, 1f);
        input.caretWidth = 2;
        input.caretBlinkRate = 0.85f;
        input.selectionColor = new Color(0.25f, 0.55f, 0.9f, 0.35f);
        input.text = "";

        inputGo.SetActive(false);
        inputGo.SetActive(true);

        return input;
    }

    private static string BodyIconName(ObjectInfo oi)
    {
        if (oi == null) return "";
        try
        {
            var sprite = oi.ImagePlanetUI;
            if (sprite == null) return "";
            var fmt = MonoBehaviourSingleton<ObjectInfoManager>.Instance?.spriteTextStart;
            if (string.IsNullOrEmpty(fmt)) return "";
            return fmt.MyFormat(sprite.name);
        }
        catch { return ""; }
    }

    private static string BodyLabel(ObjectInfo oi)
    {
        if (oi == null) return "?";
        var icon = BodyIconName(oi);
        return string.IsNullOrEmpty(icon) ? oi.ObjectName : $"{icon} {oi.ObjectName}";
    }

    public void RebuildLayout()
    {
        if (_built && isActiveAndEnabled)
            LayoutRebuilder.ForceRebuildLayoutImmediate(GetComponent<RectTransform>());
    }

    private void RefreshStockSections()
    {
        if (_objectInfoWindow == null || _currentData == null)
            return;
        Traverse.Create(_objectInfoWindow)
            .Method("SetData", new[] { typeof(Game.ObjectInfoDataScripts.ObjectInfoData), typeof(bool) })
            .GetValue(_currentData, false);
    }

    private void OnDestroy()
    {
        foreach (var sec in _sections)
            if (sec?.Root != null) Destroy(sec.Root);
        _sections.Clear();
    }

    private static string StatusToString(Data.LogisticsRequestStatus s) => s switch
    {
        Data.LogisticsRequestStatus.Pending => Logic.LogisticsStrings.StatusPending(),
        Data.LogisticsRequestStatus.InProgress => Logic.LogisticsStrings.StatusInTransit(),
        Data.LogisticsRequestStatus.Satisfied => Logic.LogisticsStrings.StatusSatisfied(),
        Data.LogisticsRequestStatus.Failed => Logic.LogisticsStrings.StatusFailed(),
        _ => "?"
    };

    private static Color StatusColor(LogisticsObserver.ShipState s) => s switch
    {
        LogisticsObserver.ShipState.Pending => new Color(0.8f, 0.72f, 0.3f, 1f),
        LogisticsObserver.ShipState.InTransit => new Color(0.45f, 0.65f, 0.9f, 1f),
        LogisticsObserver.ShipState.Idle => new Color(0.5f, 0.75f, 0.5f, 1f),
        LogisticsObserver.ShipState.Blocked => new Color(0.85f, 0.4f, 0.35f, 1f),
        _ => new Color(0.5f, 0.5f, 0.5f, 1f)
    };
    private static Color StatusColor(Data.LogisticsRequestStatus s) => s switch
    {
        Data.LogisticsRequestStatus.Pending => new Color(0.7f, 0.7f, 0.3f, 1f),
        Data.LogisticsRequestStatus.InProgress => new Color(0.3f, 0.5f, 0.9f, 1f),
        Data.LogisticsRequestStatus.Satisfied => new Color(0.3f, 0.8f, 0.3f, 1f),
        Data.LogisticsRequestStatus.Failed => new Color(0.9f, 0.3f, 0.3f, 1f),
        _ => new Color(0.5f, 0.5f, 0.5f, 1f)
    };
}

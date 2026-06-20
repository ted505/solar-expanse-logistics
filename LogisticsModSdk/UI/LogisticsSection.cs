using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LogisticsModSdk.UI;

public class LogisticsSection
{
    public GameObject Root { get; private set; }
    public RectTransform ContentArea { get; private set; }
    public bool Expanded { get; private set; }

    private TextMeshProUGUI _arrowText;
    private Image _arrowImage;
    private GameObject _contentGo;
    private RectTransform _parentRt;
    private readonly Sprite _spriteExpand;
    private readonly Sprite _spriteCollapse;

    private readonly float _headerHeight;
    private readonly float _headerFontSize;
    private readonly Color _headerColor;
    private readonly Color _headerTextColor;
    private readonly Color _headerDividerColor;
    private readonly Color _contentBgColor;
    private readonly ColorBlock? _headerButtonColors;
    private readonly Material _fontMaterial;

    public LogisticsSection(Transform parent, string title, TMP_FontAsset font, float fixedWidth,
        Button styleButton = null, Image styleIcon = null, Sprite spriteExpand = null, Sprite spriteCollapse = null,
        float headerHeight = 42f, float headerFontSize = 15f, Color? headerColor = null,
        Color? headerTextColor = null, Color? headerDividerColor = null, Color? contentBgColor = null,
        ColorBlock? headerButtonColors = null, Material fontMaterial = null)
    {
        _parentRt = parent as RectTransform;
        _fontMaterial = fontMaterial;
        _headerHeight = headerHeight;
        _headerFontSize = headerFontSize;
        _headerColor = headerColor ?? new Color(0f, 0f, 0f, 0f);
        _headerTextColor = headerTextColor ?? new Color(0.42f, 0.42f, 0.45f, 0.96f);
        _headerDividerColor = headerDividerColor ?? new Color(0.42f, 0.42f, 0.42f, 0.38f);
        _contentBgColor = contentBgColor ?? new Color(0f, 0f, 0f, 0f);
        _headerButtonColors = headerButtonColors;
        _spriteExpand = spriteExpand;
        _spriteCollapse = spriteCollapse;

        Root = new GameObject("Log_" + title.Replace(" ", "").Replace("\u2014", ""),
            typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        Root.transform.SetParent(parent, false);
        Root.transform.SetAsLastSibling();

        var rootRt = Root.GetComponent<RectTransform>();
        rootRt.anchorMin = new Vector2(0, 1);
        rootRt.anchorMax = new Vector2(0, 1);
        rootRt.pivot = new Vector2(0, 1);
        rootRt.sizeDelta = new Vector2(fixedWidth, _headerHeight);

        var rootVlg = Root.GetComponent<VerticalLayoutGroup>();
        rootVlg.childForceExpandWidth = true;
        rootVlg.childForceExpandHeight = false;
        rootVlg.childControlHeight = true;
        rootVlg.childControlWidth = true;
        rootVlg.padding = new RectOffset(0, 0, 0, 0);
        rootVlg.spacing = 0;

        var rootCsf = Root.GetComponent<ContentSizeFitter>();
        rootCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        rootCsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        var lineGo = new GameObject("Line", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        lineGo.transform.SetParent(Root.transform, false);
        var lineRt = lineGo.GetComponent<RectTransform>();
        lineRt.anchorMin = new Vector2(0, 1);
        lineRt.anchorMax = new Vector2(1, 1);
        lineRt.pivot = new Vector2(0.5f, 0.5f);
        lineRt.sizeDelta = new Vector2(0, 1);
        var lineLayout = lineGo.GetComponent<LayoutElement>();
        lineLayout.preferredHeight = 1f;
        lineLayout.ignoreLayout = true;
        lineGo.GetComponent<Image>().color = _headerDividerColor;

        var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Image), typeof(LayoutElement), typeof(HeaderHoverTint));
        headerGo.transform.SetParent(Root.transform, false);
        var headerRt = headerGo.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0, 0);
        headerRt.anchorMax = new Vector2(0, 0);
        headerRt.pivot = new Vector2(0.5f, 0.5f);
        headerRt.sizeDelta = new Vector2(fixedWidth, _headerHeight);
        var headerLayout = headerGo.GetComponent<LayoutElement>();
        headerLayout.minHeight = _headerHeight;
        headerLayout.preferredHeight = _headerHeight;

        var headerImg = headerGo.GetComponent<Image>();
        headerImg.color = _headerColor;
        headerImg.raycastTarget = true;

        var arrowColors = _headerButtonColors ?? (styleButton != null ? styleButton.colors : new ColorBlock
        {
            normalColor = new Color(0.424f, 0.424f, 0.424f, 1f),
            highlightedColor = new Color(0f, 1f, 0.914f, 1f),
            pressedColor = new Color(0.424f, 0.424f, 0.424f, 1f),
            selectedColor = new Color(0.424f, 0.424f, 0.424f, 1f),
            disabledColor = new Color(0.45f, 0.45f, 0.45f, 1f),
            colorMultiplier = 1f,
            fadeDuration = 0.08f
        });

        float labelLeftOffset = 24f;
        if (styleIcon != null)
        {
            var arrowGo = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            arrowGo.transform.SetParent(headerGo.transform, false);
            var arrowRt = arrowGo.GetComponent<RectTransform>();
            var donorRt = styleIcon.transform as RectTransform;
            if (donorRt != null)
            {
                arrowRt.anchorMin = donorRt.anchorMin;
                arrowRt.anchorMax = donorRt.anchorMax;
                arrowRt.pivot = donorRt.pivot;
                arrowRt.sizeDelta = donorRt.sizeDelta;
                arrowRt.anchoredPosition = donorRt.anchoredPosition;
                arrowRt.localScale = donorRt.localScale;
                labelLeftOffset = donorRt.sizeDelta.x + donorRt.anchoredPosition.x + 8f;
            }
            else
            {
                arrowRt.anchorMin = new Vector2(0, 0.5f);
                arrowRt.anchorMax = new Vector2(0, 0.5f);
                arrowRt.pivot = new Vector2(0, 0.5f);
                arrowRt.sizeDelta = new Vector2(18, 18);
                arrowRt.anchoredPosition = new Vector2(8, 0);
            }

            _arrowImage = arrowGo.GetComponent<Image>();
            _arrowImage.color = arrowColors.normalColor;
            _arrowImage.material = styleIcon.material;
            _arrowImage.type = styleIcon.type;
            _arrowImage.preserveAspect = styleIcon.preserveAspect;
            _arrowImage.raycastTarget = false;
        }
        else
        {
            var arrowGo = new GameObject("Arrow", typeof(RectTransform), typeof(TextMeshProUGUI));
            arrowGo.transform.SetParent(headerGo.transform, false);
            var arrowRt = arrowGo.GetComponent<RectTransform>();
            arrowRt.anchorMin = new Vector2(0, 0.5f);
            arrowRt.anchorMax = new Vector2(0, 0.5f);
            arrowRt.pivot = new Vector2(0, 0.5f);
            arrowRt.sizeDelta = new Vector2(18, 18);
            arrowRt.anchoredPosition = new Vector2(8, 0);
            _arrowText = arrowGo.GetComponent<TextMeshProUGUI>();
            _arrowText.font = font;
            if (_fontMaterial != null) _arrowText.fontSharedMaterial = _fontMaterial;
            _arrowText.fontSize = 14;
            _arrowText.color = arrowColors.normalColor;
            _arrowText.alignment = TextAlignmentOptions.Center;
            _arrowText.raycastTarget = false;
        }

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(headerGo.transform, false);
        var labelRt = labelGo.GetComponent<RectTransform>();
        labelRt.anchorMin = new Vector2(0, 0.5f);
        labelRt.anchorMax = new Vector2(0, 0.5f);
        labelRt.pivot = new Vector2(0, 0.5f);
        labelRt.anchoredPosition = new Vector2(labelLeftOffset, 0);
        labelRt.sizeDelta = new Vector2(Mathf.Max(254f, fixedWidth - labelLeftOffset - 42f), 22.5f);
        var labelTmp = labelGo.GetComponent<TextMeshProUGUI>();
        labelTmp.text = title;
        labelTmp.font = font;
        if (_fontMaterial != null) labelTmp.fontSharedMaterial = _fontMaterial;
        labelTmp.fontSize = _headerFontSize;
        labelTmp.color = _headerTextColor;
        labelTmp.fontStyle = FontStyles.Normal;
        labelTmp.alignment = TextAlignmentOptions.Left;
        labelTmp.richText = true;
        labelTmp.raycastTarget = false;

        _contentGo = new GameObject("Content", typeof(RectTransform), typeof(Image),
            typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        _contentGo.transform.SetParent(Root.transform, false);
        ContentArea = _contentGo.GetComponent<RectTransform>();

        _contentGo.GetComponent<Image>().color = _contentBgColor;

        var cVlg = _contentGo.GetComponent<VerticalLayoutGroup>();
        cVlg.childForceExpandWidth = true;
        cVlg.childForceExpandHeight = false;
        cVlg.childControlHeight = true;
        cVlg.childControlWidth = true;
        cVlg.padding = new RectOffset(0, 0, 0, -1);
        cVlg.spacing = 2.5f;

        var cCsf = _contentGo.GetComponent<ContentSizeFitter>();
        cCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        cCsf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        var placeholderGo = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
        placeholderGo.transform.SetParent(_contentGo.transform, false);
        var phTmp = placeholderGo.GetComponent<TextMeshProUGUI>();
        phTmp.text = "No items configured.";
        phTmp.font = font;
        if (_fontMaterial != null) phTmp.fontSharedMaterial = _fontMaterial;
        phTmp.fontSize = 13;
        phTmp.color = new Color(0.5f, 0.5f, 0.5f, 1f);

        SetExpanded(false);

        var hover = headerGo.GetComponent<HeaderHoverTint>();
        hover.ArrowImage = _arrowImage;
        hover.LabelText = labelTmp;
        hover.ArrowNormal = arrowColors.normalColor;
        hover.ArrowHighlighted = arrowColors.highlightedColor;
        hover.ArrowPressed = arrowColors.pressedColor;
        hover.LabelNormal = _headerTextColor;
        hover.LabelHighlighted = arrowColors.highlightedColor;
        hover.LabelPressed = arrowColors.pressedColor;
        hover.OnClick = Toggle;
    }

    public void Toggle()
    {
        Expanded = !Expanded;
        _contentGo.SetActive(Expanded);
        SetArrowSprite();
        LayoutRebuilder.ForceRebuildLayoutImmediate(_parentRt);
    }

    public void SetExpanded(bool expanded)
    {
        Expanded = expanded;
        _contentGo.SetActive(expanded);
        SetArrowSprite();
    }

    private void SetArrowSprite()
    {
        if (_arrowImage != null)
            _arrowImage.sprite = Expanded ? _spriteCollapse : _spriteExpand;
        else if (_arrowText != null)
            _arrowText.text = Expanded ? "^" : "v";
    }

    public TextMeshProUGUI AddTextRow(string text, TMP_FontAsset font, float fontSize = 13f, Color? color = null)
    {
        var go = new GameObject("Row", typeof(RectTransform), typeof(TextMeshProUGUI), typeof(LayoutElement));
        go.transform.SetParent(_contentGo.transform, false);
        go.GetComponent<LayoutElement>().preferredHeight = 22f;
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.font = font;
        if (_fontMaterial != null) tmp.fontSharedMaterial = _fontMaterial;
        tmp.fontSize = fontSize;
        tmp.color = color ?? new Color(0.7f, 0.7f, 0.7f, 1f);
        return tmp;
    }

    public void ClearContent()
    {
        var es = EventSystem.current;
        var children = new List<GameObject>();
        foreach (Transform child in _contentGo.transform)
            children.Add(child.gameObject);
        foreach (var child in children)
        {
            if (es != null && es.currentSelectedGameObject == child)
                es.SetSelectedGameObject(null);
            Object.DestroyImmediate(child);
        }
    }
}

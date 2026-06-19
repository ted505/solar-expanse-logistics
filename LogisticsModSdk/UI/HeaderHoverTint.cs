using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LogisticsModSdk.UI;

/// <summary>
/// Mirrors hover (and pressed) state onto both an arrow <see cref="Image"/> and a label
/// <see cref="TextMeshProUGUI"/>, since Unity's <see cref="Button"/> color-tint transition can
/// only drive a single <c>targetGraphic</c>. Attach to the full-width header GameObject — the
/// host's own <see cref="Graphic"/> (with <c>raycastTarget=true</c>) supplies the hit region.
/// </summary>
public sealed class HeaderHoverTint : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
{
    public Image ArrowImage;
    public TextMeshProUGUI LabelText;
    public Color ArrowNormal = new Color(0.424f, 0.424f, 0.424f, 1f);
    public Color ArrowHighlighted = new Color(0f, 1f, 0.914f, 1f);
    public Color ArrowPressed = new Color(0.424f, 0.424f, 0.424f, 1f);
    public Color LabelNormal = new Color(0.604f, 0.604f, 0.604f, 1f);
    public Color LabelHighlighted = new Color(0f, 1f, 0.914f, 1f);
    public Color LabelPressed = new Color(0.604f, 0.604f, 0.604f, 1f);
    public Action OnClick;

    private bool _hovered;
    private bool _pressed;

    private void OnEnable()
    {
        _hovered = false;
        _pressed = false;
        Apply();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        _hovered = true;
        Apply();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _hovered = false;
        _pressed = false;
        Apply();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        _pressed = true;
        Apply();
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _pressed = false;
        Apply();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        OnClick?.Invoke();
    }

    private void Apply()
    {
        var arrow = _pressed ? ArrowPressed : (_hovered ? ArrowHighlighted : ArrowNormal);
        var label = _pressed ? LabelPressed : (_hovered ? LabelHighlighted : LabelNormal);
        if (ArrowImage != null) ArrowImage.color = arrow;
        if (LabelText != null) LabelText.color = label;
    }
}

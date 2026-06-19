using System;
using Game.UI.Windows.Elements;
using Game.UI.Windows.Windows;
using Manager;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LogisticsModSdk.UI;

public class SimpleTooltip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IEventSystemHandler
{
    public string Text;
    public Func<string> DynamicText;
    public float Delay;

    private MonoBehaviourOnDisable _owner;
    private float _enterTime;
    private bool _waiting;
    private bool _showing;

    public void OnPointerEnter(PointerEventData eventData)
    {
        _enterTime = Time.time;
        if (Delay > 0)
        {
            _waiting = true;
        }
        else
        {
            Show();
        }
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _waiting = false;
        Hide();
    }

    private void Update()
    {
        if (_waiting && Time.time - _enterTime >= Delay)
        {
            _waiting = false;
            Show();
        }
    }

    private MonoBehaviourOnDisable FindOwner()
    {
        // Try direct parent chain first
        var owner = GetComponentInParent<MonoBehaviourOnDisable>();
        if (owner != null) return owner;

        // Our buttons are in injected section roots that may not be children of
        // the ObjectInfoWindow's MonoBehaviourOnDisable hierarchy. Find any stock
        // ShowToolTip component on the ObjectInfoWindow as a stable identity anchor.
        var oiw = UnityEngine.Object.FindObjectOfType<ObjectInfoWindow>();
        if (oiw != null)
            owner = oiw.GetComponentInChildren<MonoBehaviourOnDisable>(true);
        return owner;
    }

    private void Show()
    {
        var text = DynamicText?.Invoke() ?? Text ?? "";
        if (string.IsNullOrEmpty(text))
            return;

        if (_owner == null)
            _owner = FindOwner();
        if (_owner == null)
            return;

        MonoBehaviourSingleton<ToolTipManager>.Instance.DefaultSetting();
        MonoBehaviourSingleton<ToolTipManager>.Instance.ShowToolTip(_owner, text, DynamicText);
        _showing = true;
    }

    private void Hide()
    {
        if (!_showing || _owner == null)
            return;

        MonoBehaviourSingleton<ToolTipManager>.Instance.DefaultSetting();
        MonoBehaviourSingleton<ToolTipManager>.Instance.HideToolTip(_owner);
        _showing = false;
    }

    private void OnDisable()
    {
        _waiting = false;
        Hide();
    }
}

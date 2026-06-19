using System;
using System.Collections.Generic;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Windows;
using UnityEngine;

namespace SolarExpanseSdk.Services;

/// <summary>
/// Object-info UI extension service. Mods register Unity components and rocket-row decorators;
/// SDK patches attach or invoke them at stock object-info UI boundaries.
/// </summary>
public sealed class SdkObjectInfoUi
{
    private readonly List<Type> _windowComponents = new List<Type>();
    private readonly List<Func<UIRowRocket, string>> _rocketRowDecorators = new List<Func<UIRowRocket, string>>();
    private SdkLogging _log;

    /// <summary>
    /// Connects the service to the SDK logger during plugin startup.
    /// </summary>
    public void Initialize(SdkLogging log)
    {
        _log = log;
    }

    /// <summary>
    /// Registers a Unity component type to attach to every stock <see cref="ObjectInfoWindow"/>.
    /// </summary>
    public void RegisterWindowComponent<T>() where T : Component
    {
        var type = typeof(T);
        if (!_windowComponents.Contains(type))
        {
            _windowComponents.Add(type);
            _log?.Verbose("sdk.objectInfoUi", $"register component={type.FullName}");
        }
    }

    /// <summary>
    /// Registers a decorator that can return marker text for a stock rocket row.
    /// </summary>
    public void RegisterRocketRowDecorator(Func<UIRowRocket, string> decorator)
    {
        if (decorator != null && !_rocketRowDecorators.Contains(decorator))
        {
            _rocketRowDecorators.Add(decorator);
            _log?.Verbose("sdk.objectInfoUi", $"register rocketRowDecorator count={_rocketRowDecorators.Count}");
        }
    }

    /// <summary>
    /// Attaches all registered window components to the supplied stock object-info window.
    /// </summary>
    public void AttachRegisteredComponents(ObjectInfoWindow window)
    {
        if (window == null)
            return;

        foreach (var type in _windowComponents)
        {
            try
            {
                if (window.GetComponent(type) == null)
                {
                    window.gameObject.AddComponent(type);
                    _log?.VerboseThrottled("sdk.objectInfoUi", $"attach-{type.FullName}", $"attach component={type.Name} window={window.GetInstanceID()} result=added");
                }
                else
                {
                    _log?.VerboseThrottled("sdk.objectInfoUi", $"attach-{type.FullName}", $"attach component={type.Name} window={window.GetInstanceID()} result=already-present");
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"ObjectInfo component attach failed for {type.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs registered rocket-row decorators and joins non-empty marker strings.
    /// </summary>
    public string BuildRocketRowMarker(UIRowRocket row)
    {
        var markers = new List<string>();
        foreach (var decorator in _rocketRowDecorators)
        {
            try
            {
                var marker = decorator(row);
                if (!string.IsNullOrEmpty(marker))
                {
                    markers.Add(marker);
                    _log?.VerboseThrottled("sdk.objectInfoUi", "rocketRowDecorator", $"rocketRowDecorator result=marker length={marker.Length}");
                }
            }
            catch (Exception ex)
            {
                _log?.Warning($"Rocket row decorator failed: {ex.Message}");
            }
        }

        return markers.Count == 0 ? null : string.Join(" ", markers);
    }
}

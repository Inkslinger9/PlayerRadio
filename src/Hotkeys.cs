using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace PlayerRadio
{
    /// <summary>A configurable Input System control path (e.g. "&lt;Keyboard&gt;/f5") read directly from the device.</summary>
    public sealed class Hotkey
    {
        public readonly string Name;
        private readonly ConfigEntry<string> _entry;
        private string _resolvedPath;
        private ButtonControl _control;
        private float _nextResolve;
        private bool _warned;

        public Hotkey(string name, ConfigEntry<string> entry) { Name = name; _entry = entry; }

        public string Path => _entry.Value;

        public string Display
        {
            get
            {
                Resolve();
                if (_control != null && !string.IsNullOrEmpty(_control.displayName)) return _control.displayName;
                var p = _entry.Value ?? "";
                int i = p.LastIndexOf('/');
                return (i >= 0 ? p.Substring(i + 1) : p).ToUpperInvariant();
            }
        }

        private void Resolve()
        {
            bool pathChanged = _entry.Value != _resolvedPath;
            bool deviceGone = _control != null && (_control.device == null || !_control.device.added);
            if (!pathChanged && !deviceGone && (_control != null || Time.unscaledTime < _nextResolve)) return;
            _resolvedPath = _entry.Value;
            _nextResolve = Time.unscaledTime + 5f;
            _control = string.IsNullOrWhiteSpace(_resolvedPath) ? null : InputSystem.FindControl(_resolvedPath) as ButtonControl;
            if (_control == null && !_warned && !string.IsNullOrWhiteSpace(_resolvedPath))
            {
                _warned = true;
                Plugin.Log.LogWarning($"Hotkey {Name}: control path '{_resolvedPath}' not found (no such key/device yet). Example: <Keyboard>/f5");
            }
            else if (_control != null)
            {
                _warned = false;
                Plugin.Verbose($"Hotkey {Name} -> {_control.path}");
            }
        }

        public bool Pressed()
        {
            Resolve();
            return _control != null && _control.wasPressedThisFrame;
        }
    }
}

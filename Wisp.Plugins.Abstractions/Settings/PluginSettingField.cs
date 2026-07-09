using System;
using System.Collections.Generic;

namespace Wisp.Plugins.Settings
{
    /// <summary>
    /// Base class for all declarative plugin settings. Wisp will automatically generate
    /// a UI control for this field in the plugin's Settings dialog.
    /// </summary>
    public abstract class PluginSettingField
    {
        /// <summary>Unique key for this setting. Used in the dictionary passed to <see cref="IWispPlugin.OnSettingsSaved"/>.</summary>
        public string Key { get; }

        /// <summary>Human-readable label shown next to the control.</summary>
        public string Label { get; }

        /// <summary>Optional tooltip or sub-label text to explain what the setting does.</summary>
        public string? Description { get; set; }

        protected PluginSettingField(string key, string label)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key cannot be null or empty.", nameof(key));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Label cannot be null or empty.", nameof(label));
            Key = key;
            Label = label;
        }
    }

    /// <summary>A boolean setting, rendered as a CheckBox.</summary>
    public class BoolSettingField : PluginSettingField
    {
        public bool CurrentValue { get; }

        public BoolSettingField(string key, string label, bool currentValue) : base(key, label)
        {
            CurrentValue = currentValue;
        }
    }

    /// <summary>A plain text setting, rendered as a TextBox.</summary>
    public class StringSettingField : PluginSettingField
    {
        public string CurrentValue { get; }

        public StringSettingField(string key, string label, string currentValue) : base(key, label)
        {
            CurrentValue = currentValue ?? string.Empty;
        }
    }

    /// <summary>A numeric setting, rendered as a Slider or number-validated TextBox.</summary>
    public class NumberSettingField : PluginSettingField
    {
        public double CurrentValue { get; }
        public double Min { get; set; } = 0;
        public double Max { get; set; } = 100;
        public double Step { get; set; } = 1;

        public NumberSettingField(string key, string label, double currentValue) : base(key, label)
        {
            CurrentValue = currentValue;
        }
    }

    /// <summary>A setting that allows picking one option from a predefined list, rendered as a ComboBox.</summary>
    public class ChoiceSettingField : PluginSettingField
    {
        public string CurrentValue { get; }
        public IReadOnlyList<string> Choices { get; }

        public ChoiceSettingField(string key, string label, string currentValue, IReadOnlyList<string> choices) : base(key, label)
        {
            CurrentValue = currentValue ?? string.Empty;
            Choices = choices ?? Array.Empty<string>();
        }
    }
}

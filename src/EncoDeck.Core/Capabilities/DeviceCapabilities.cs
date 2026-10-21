// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Frozen;

namespace EncoDeck.Core.Capabilities;

/// <summary>Support level of one capability together with the evidence behind it.</summary>
public sealed record CapabilityEntry(CapabilitySupport Support, string Evidence);

public interface IDeviceCapabilities
{
    CapabilityEntry Get(DeviceCapability capability);

    IEnumerable<KeyValuePair<DeviceCapability, CapabilityEntry>> All { get; }
}

public static class DeviceCapabilitiesExtensions
{
    /// <summary>Whether a control for this capability may be offered to the user.</summary>
    public static bool IsAvailable(this IDeviceCapabilities capabilities, DeviceCapability capability, bool allowExperimental)
    {
        var support = capabilities.Get(capability).Support;
        return support == CapabilitySupport.Supported
            || (allowExperimental && support == CapabilitySupport.Experimental);
    }
}

public sealed class DeviceCapabilities : IDeviceCapabilities
{
    private static readonly CapabilityEntry UnknownEntry = new(CapabilitySupport.Unknown, "No evidence collected.");

    private readonly FrozenDictionary<DeviceCapability, CapabilityEntry> _entries;

    private DeviceCapabilities(IDictionary<DeviceCapability, CapabilityEntry> entries)
    {
        _entries = entries.ToFrozenDictionary();
    }

    public static DeviceCapabilities Empty { get; } = new(new Dictionary<DeviceCapability, CapabilityEntry>());

    public CapabilityEntry Get(DeviceCapability capability) =>
        _entries.TryGetValue(capability, out var entry) ? entry : UnknownEntry;

    public IEnumerable<KeyValuePair<DeviceCapability, CapabilityEntry>> All =>
        Enum.GetValues<DeviceCapability>().Select(c => new KeyValuePair<DeviceCapability, CapabilityEntry>(c, Get(c)));

    /// <summary>Returns a copy where <paramref name="capability"/> has a new support level (e.g. after a runtime probe).</summary>
    public DeviceCapabilities With(DeviceCapability capability, CapabilitySupport support, string evidence)
    {
        var copy = new Dictionary<DeviceCapability, CapabilityEntry>(_entries)
        {
            [capability] = new CapabilityEntry(support, evidence),
        };
        return new DeviceCapabilities(copy);
    }

    public static Builder CreateBuilder() => new();

    public sealed class Builder
    {
        private readonly Dictionary<DeviceCapability, CapabilityEntry> _entries = [];

        public Builder Set(DeviceCapability capability, CapabilitySupport support, string evidence)
        {
            _entries[capability] = new CapabilityEntry(support, evidence);
            return this;
        }

        public DeviceCapabilities Build() => new(_entries);
    }
}

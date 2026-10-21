// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Bluetooth.Control;

/// <summary>Windows' view of whether the device's Bluetooth link (ACL) is up.</summary>
public interface ILinkStateSource : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Raised on an arbitrary thread when <see cref="IsConnected"/> changes.</summary>
    event EventHandler<bool>? ConnectionChanged;
}

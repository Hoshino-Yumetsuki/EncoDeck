// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Core.Commands;
using EncoDeck.Core.Devices;
using EncoDeck.Core.Profiles;
using EncoDeck.Core.State;
using EncoDeck.Protocol.Oppo;
using EncoDeck.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace EncoDeck.Protocol.Brands;

/// <summary>What a brand controller needs besides its channel.</summary>
public sealed record ControllerContext(
    DeviceIdentity Identity,
    IDeviceStateStore Store,
    Func<bool> AllowExperimental,
    ILoggerFactory Loggers,
    TimeProvider Time,
    IProtocolTrace? Trace = null);

/// <summary>
/// A device profile that knows its brand protocol: which control services to open and how to
/// drive the channel once open. Every brand (OPPO family, vivo, Huawei, Xiaomi, Edifier) provides one.
/// </summary>
public interface IControllableProfile : IDeviceProfile
{
    /// <summary>Brand family id, e.g. <c>oppo</c>, <c>vivo</c>, <c>huawei</c>.</summary>
    string Brand { get; }

    /// <summary>
    /// True for name-only fallback profiles ("some HUAWEI earbuds"). Those are accepted only for
    /// audio devices, since names like "HUAWEI …" also match phones and watches.
    /// </summary>
    bool IsGeneric { get; }

    /// <summary>
    /// The profile to show before the channel opens, given what these earbuds reported last time
    /// (product id, capabilities, firmware). Returns this profile when nothing changes.
    /// </summary>
    IControllableProfile Remember(DeviceState known) => this;

    /// <summary>Creates the controller for an opened channel; the controller owns the channel from here on.</summary>
    IDeviceController CreateController(IDuplexByteChannel channel, ControllerContext context);
}

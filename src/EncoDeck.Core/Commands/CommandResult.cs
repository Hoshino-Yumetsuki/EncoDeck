// SPDX-License-Identifier: GPL-3.0-only
namespace EncoDeck.Core.Commands;

public enum CommandStatus
{
    Success,
    /// <summary>No acknowledgement or read-back arrived within the request timeout.</summary>
    Timeout,
    /// <summary>The control channel failed while sending or receiving.</summary>
    TransportError,
    /// <summary>The device answered with a failure status.</summary>
    Rejected,
    /// <summary>The device profile does not allow this command.</summary>
    Unsupported,
    /// <summary>No ready control channel.</summary>
    NotConnected,
    /// <summary>The caller cancelled the operation.</summary>
    Cancelled,
}

public sealed record CommandResult(CommandStatus Status, string? Detail = null)
{
    public bool IsSuccess => Status == CommandStatus.Success;

    public static CommandResult Ok { get; } = new(CommandStatus.Success);

    public static CommandResult NotConnected { get; } = new(CommandStatus.NotConnected);

    public static CommandResult Unsupported(string detail) => new(CommandStatus.Unsupported, detail);

    public override string ToString() => Detail is null ? Status.ToString() : $"{Status}: {Detail}";
}

/// <summary>A device-changing command that has been sent but not yet confirmed.</summary>
public sealed record PendingCommand(Guid Id, string Description, DateTimeOffset StartedAt);

/// <summary>Last error worth surfacing to the user.</summary>
/// <param name="ResourceKey">Localized text for the UI; <paramref name="Message"/> stays the logged English detail.</param>
public sealed record DeviceError(string Message, CommandStatus? Status, DateTimeOffset Timestamp, string? ResourceKey = null);

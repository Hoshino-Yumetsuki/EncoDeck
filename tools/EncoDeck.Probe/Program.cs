// SPDX-License-Identifier: GPL-3.0-only
using EncoDeck.Probe;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await ProbeApp.RunAsync(args, cts.Token);

// SPDX-License-Identifier: GPL-3.0-only
using Windows.Devices.Radios;

namespace EncoDeck.Bluetooth.Discovery;

/// <summary>
/// Whether the local Bluetooth radio is switched off (Quick Settings toggle, airplane mode), via
/// <see cref="Radio.StateChanged"/>. Unknown (no adapter, or the radio could not be read) counts as on.
/// </summary>
public sealed class BluetoothRadioMonitor : IDisposable
{
    private Radio? _radio;
    private volatile bool _off;

    public bool IsOff => _off;

    public async Task StartAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (radio is null) return;
            radio.StateChanged += OnStateChanged;
            _radio = radio;
            OnStateChanged(radio, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Radio state is unavailable on this system; behave as if Bluetooth is on.
        }
    }

    private void OnStateChanged(Radio sender, object? args) => _off = sender.State is RadioState.Off or RadioState.Disabled;

    public void Dispose()
    {
        var radio = Interlocked.Exchange(ref _radio, null);
        if (radio is not null) radio.StateChanged -= OnStateChanged;
    }
}

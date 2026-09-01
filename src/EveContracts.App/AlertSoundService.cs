using System.IO;
using System.Media;
using EveContracts.Core.Services;
using Microsoft.Extensions.Logging;

namespace EveContracts.App;

/// <summary>
/// Synthesized alert sounds (in-memory WAVs, no asset files):
///  - new inbound contract: soft two-note chime
///  - big-profit opportunity: rising three-note arpeggio, played with urgency
/// Respects the "Sound alerts" setting.
/// </summary>
public sealed class AlertSoundService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ILogger<AlertSoundService> _log;
    private readonly SoundPlayer _newContract;
    private readonly SoundPlayer _profit;

    public AlertSoundService(SettingsService settings, ILogger<AlertSoundService> log)
    {
        _settings = settings;
        _log = log;
        _newContract = Load(Synthesize([(880, 90, 0.35), (1174.7, 140, 0.35)]));
        _profit = Load(Synthesize([(784, 80, 0.4), (987.8, 80, 0.4), (1318.5, 90, 0.45), (1568, 200, 0.5)]));
    }

    public void PlayNewContract() => Play(_newContract);
    public void PlayProfitAlert() => Play(_profit);

    private void Play(SoundPlayer player)
    {
        if (!_settings.SoundAlerts) return;
        try { player.Play(); }
        catch (Exception ex) { _log.LogWarning("Alert sound failed: {Error}", ex.Message); }
    }

    private static SoundPlayer Load(byte[] wav)
    {
        var player = new SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    /// <summary>Renders sine notes with a 10 ms attack/release envelope into a 16-bit mono 44.1 kHz WAV.</summary>
    private static byte[] Synthesize((double Freq, int Ms, double Amp)[] notes)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (freq, ms, amp) in notes)
        {
            var count = rate * ms / 1000;
            var ramp = Math.Min(count / 2, rate / 100); // 10 ms
            for (var n = 0; n < count; n++)
            {
                var env = Math.Min(1.0, Math.Min(n / (double)ramp, (count - 1 - n) / (double)ramp));
                var v = Math.Sin(2 * Math.PI * freq * n / rate) * amp * env;
                samples.Add((short)(v * short.MaxValue));
            }
        }

        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        var dataLen = samples.Count * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataLen);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(rate);
        w.Write(rate * 2);          // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits
        w.Write("data"u8);
        w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms2.ToArray();
    }

    public void Dispose()
    {
        _newContract.Dispose();
        _profit.Dispose();
    }
}

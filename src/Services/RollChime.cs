using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Game.Config;

namespace LootView.Services;

/// <summary>
/// A small two-note chime for an item resolving to someone else. The game sounds a fanfare when
/// you win and nothing at all when you don't, so without this a lost roll goes by unnoticed.
///
/// The chime is synthesised rather than taken from the game's sound effects: those are all
/// notifications meant to be heard, and this one is meant to be barely there. It still follows
/// the game's master and system volume, and stays silent when either is muted.
/// </summary>
public sealed class RollChime : IDisposable
{
    private const int SampleRate = 44100;
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;

    /// <summary>Several results in a row chime once.</summary>
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(600);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    /// <summary>
    /// Rendered chimes by volume step. Windows reads the buffer while it plays, so a buffer is
    /// never freed or reused for something else until the plugin unloads.
    /// </summary>
    private readonly Dictionary<int, IntPtr> rendered = new();
    private DateTime lastPlayed = DateTime.MinValue;

    /// <param name="volume">LootView's own volume for the chime, 0 to 1.</param>
    public void Play(float volume)
    {
        var now = DateTime.Now;
        if (now - lastPlayed < MinGap) return;

        var loudness = Math.Clamp(volume, 0f, 1f) * GameVolume();
        var step = (int)MathF.Round(loudness * 40f);
        if (step <= 0) return;

        lastPlayed = now;
        try
        {
            if (!rendered.TryGetValue(step, out var buffer))
            {
                var wav = Render(step / 40f);
                buffer = Marshal.AllocHGlobal(wav.Length);
                Marshal.Copy(wav, 0, buffer, wav.Length);
                rendered[step] = buffer;
            }

            PlaySound(buffer, IntPtr.Zero, SndMemory | SndAsync | SndNoDefault);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Could not play the roll chime");
        }
    }

    /// <summary>The game's master and system volume combined, or 0 when either is muted.</summary>
    private static float GameVolume()
    {
        try
        {
            var config = Plugin.GameConfig;
            if (config.TryGet(SystemConfigOption.IsSndMaster, out bool masterMuted) && masterMuted) return 0f;
            if (config.TryGet(SystemConfigOption.IsSndSystem, out bool systemMuted) && systemMuted) return 0f;

            var master = config.TryGet(SystemConfigOption.SoundMaster, out uint m) ? m / 100f : 1f;
            var system = config.TryGet(SystemConfigOption.SoundSystem, out uint s) ? s / 100f : 1f;
            return Math.Clamp(master * system, 0f, 1f);
        }
        catch
        {
            return 1f;
        }
    }

    /// <summary>
    /// Two soft glassy notes falling a fourth, C6 then G5: a gentle "that's gone" rather than an
    /// alert. Each note is a sine with faint overtones and a quick exponential fade.
    /// </summary>
    private static byte[] Render(float loudness)
    {
        const float duration = 0.62f;
        var count = (int)(SampleRate * duration);
        var samples = new float[count];

        void Note(float frequency, float start, float gain)
        {
            var first = (int)(start * SampleRate);
            for (var i = first; i < count; i++)
            {
                var t = (i - first) / (float)SampleRate;
                var attack = Math.Min(t / 0.006f, 1f);
                var envelope = attack * MathF.Exp(-t / 0.12f);
                var phase = 2f * MathF.PI * frequency * t;
                samples[i] += gain * envelope * (MathF.Sin(phase) + 0.22f * MathF.Sin(phase * 2f) + 0.06f * MathF.Sin(phase * 3f));
            }
        }

        Note(1046.5f, 0f, 1f);
        Note(784f, 0.095f, 0.85f);

        // Peak at most a quarter of full scale at 100%: it sits under the game, not over it.
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        var gain = peak > 0 ? 0.25f * loudness / peak : 0f;

        using var stream = new MemoryStream(44 + count * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + count * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(count * 2);
        foreach (var s in samples)
            writer.Write((short)Math.Clamp(s * gain * short.MaxValue, short.MinValue, short.MaxValue));

        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        // Stop anything still playing before its buffer goes away.
        try { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); } catch { /* winmm unavailable */ }

        foreach (var buffer in rendered.Values)
            Marshal.FreeHGlobal(buffer);
        rendered.Clear();
    }
}

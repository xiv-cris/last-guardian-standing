using System;
using System.Collections.Generic;
using Godot;
using LastGuardianStanding.Sim;

namespace LastGuardianStanding.Game;

/// <summary>
/// Sound effects, synthesized in code at startup (no audio files):
/// <list type="bullet">
/// <item>Paddle hit: a short falling "pew" laser zap.</item>
/// <item>Wall / post bounce: a quick, rounder "bwip", quieter than the zap.</item>
/// <item>Ball spawn: a rising "materialize" shimmer. Balls appearing together (chaos mode starts with 3)
/// make one sound, not a stack of them.</item>
/// </list>
/// Each play is slightly re-pitched so repeats don't sound robotic.
/// </summary>
public partial class Sfx : Node
{
    const int MixRate = 44100;

    /// <summary>Spawns closer together than this count as one (same tick, or the same snapshot online).</summary>
    const ulong SpawnGroupMs = 150;
    /// <summary>Chaos mode can bounce many balls in one frame: don't stack identical bounces on top of each other.</summary>
    const ulong BounceGapMs = 25;

    AudioStreamPlayer _zap = null!, _bounce = null!, _spawn = null!;
    readonly Random _rng = new();
    ulong _lastSpawnMs, _lastBounceMs;

    public override void _Ready()
    {
        _zap = MakePlayer(MakeLaser(), -9f, 8);       // chaos mode: overlapping hits don't cut each other off
        _bounce = MakePlayer(MakeBounce(), -15f, 6);
        _spawn = MakePlayer(MakeSpawn(), -12f, 4);
    }

    AudioStreamPlayer MakePlayer(AudioStreamWav stream, float volumeDb, int polyphony)
    {
        var p = new AudioStreamPlayer { Stream = stream, VolumeDb = volumeDb, MaxPolyphony = polyphony };
        AddChild(p);
        return p;
    }

    public void OnEvents(IEnumerable<SimEvent> events)
    {
        ulong now = Time.GetTicksMsec();
        foreach (var e in events)
        {
            switch (e.Type)
            {
                case SimEventType.PaddleHit:
                    Play(_zap, 0.9f, 1.15f);
                    break;
                case SimEventType.WallHit when now - _lastBounceMs >= BounceGapMs:
                    _lastBounceMs = now;
                    Play(_bounce, 0.88f, 1.12f);
                    break;
                case SimEventType.BallSpawned when now - _lastSpawnMs >= SpawnGroupMs || _lastSpawnMs == 0:
                    _lastSpawnMs = now;
                    Play(_spawn, 0.95f, 1.05f);
                    break;
            }
        }
    }

    void Play(AudioStreamPlayer player, float minPitch, float maxPitch)
    {
        player.PitchScale = minPitch + (float)_rng.NextDouble() * (maxPitch - minPitch);
        player.Play();
    }

    // ------------------------------------------------------------------ synthesis

    /// <summary>
    /// 0.17 s: pitch sweeps exponentially from 1900 Hz down to 240 Hz; a square wave (buzzy) blended with a
    /// sine a fifth above (smooth, ringing) gives the sci-fi edge; fast attack, exponential decay.
    /// </summary>
    static AudioStreamWav MakeLaser()
    {
        const float duration = 0.17f, startHz = 1900f, endHz = 240f;
        double phase = 0, phase2 = 0;
        return Synth(duration, t =>
        {
            float hz = startHz * MathF.Pow(endHz / startHz, t / duration);
            phase += hz / MixRate;
            phase2 += hz * 1.5f / MixRate;
            float square = (phase % 1.0) < 0.5 ? 1f : -1f;
            float sine = MathF.Sin((float)(phase2 * Math.Tau));
            float envelope = MathF.Min(1f, t / 0.003f) * MathF.Exp(-t * 24f);
            return (0.35f * square + 0.65f * sine) * envelope * 0.8f;
        });
    }

    /// <summary>
    /// 0.09 s: a sine dropping 700 → 330 Hz (the "bwip" of a rubbery bounce) with a soft octave overtone
    /// and a tiny noise tick at the start for the impact.
    /// </summary>
    static AudioStreamWav MakeBounce()
    {
        const float duration = 0.09f;
        double phase = 0;
        var rng = new Random(3);
        return Synth(duration, t =>
        {
            float hz = 330f + 370f * MathF.Exp(-t * 55f);
            phase += hz / MixRate;
            float x = (float)(phase * Math.Tau);
            float tone = MathF.Sin(x) + 0.3f * MathF.Sin(2f * x);
            float tick = t < 0.004f ? (float)(rng.NextDouble() * 2 - 1) * (1f - t / 0.004f) * 0.5f : 0f;
            float envelope = MathF.Min(1f, t / 0.002f) * MathF.Exp(-t * 45f);
            return (tone * envelope + tick) * 0.75f;
        });
    }

    /// <summary>
    /// 0.45 s: two detuned sines sliding up 160 → 1300 Hz under a fast 28 Hz flutter, swelling in and
    /// fading out — something charging up and blinking into existence.
    /// </summary>
    static AudioStreamWav MakeSpawn()
    {
        const float duration = 0.45f, startHz = 160f, endHz = 1300f;
        double phase = 0, phase2 = 0;
        return Synth(duration, t =>
        {
            float u = t / duration;
            float hz = startHz * MathF.Pow(endHz / startHz, u * u); // slow start, quick finish
            phase += hz / MixRate;
            phase2 += hz * 1.007f / MixRate;
            float tone = MathF.Sin((float)(phase * Math.Tau)) + MathF.Sin((float)(phase2 * Math.Tau));
            float flutter = 0.65f + 0.35f * MathF.Sin(t * 28f * MathF.Tau);
            float envelope = MathF.Sin(MathF.PI * MathF.Pow(u, 0.7f)); // swell up, then fade
            return tone * 0.5f * flutter * envelope * 0.8f;
        });
    }

    /// <summary>Renders <paramref name="sample"/>(time in seconds) into a mono 16-bit sound.</summary>
    static AudioStreamWav Synth(float duration, Func<float, float> sample)
    {
        int samples = (int)(MixRate * duration);
        var data = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short s16 = (short)(Math.Clamp(sample(i / (float)MixRate), -1f, 1f) * short.MaxValue);
            data[i * 2] = (byte)(s16 & 0xFF);
            data[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = MixRate, Stereo = false, Data = data };
    }
}

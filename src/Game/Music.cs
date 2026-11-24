using System;
using System.Threading.Tasks;
using Godot;

namespace LastGuardianStanding.Game;

/// <summary>
/// Background music, composed and synthesized in code (no audio files): a 30-second loop at 128 BPM in
/// A minor. Section A (8 bars): four-on-the-floor drums, galloping bass, 16th-note arpeggio over
/// Am–F–C–G. Section B (8 bars): adds a pad and a lead melody over Am–F–G–E, whose E major chord pulls
/// back into the Am at the top of the loop. Echo tails wrap around the end, so the loop is seamless.
/// Rendered on a background thread at startup (~1.3 M samples), then looped forever.
/// </summary>
public partial class Music : Node
{
    const int Rate = 44100;
    const double Bpm = 128;
    const double StepSec = 60.0 / Bpm / 4; // one 16th note
    const int Steps = 16 * 16;              // 16 bars of 16ths
    const string SavePath = "user://audio.cfg";

    static readonly int Length = (int)Math.Round(Steps * StepSec * Rate);

    AudioStreamPlayer _player = null!;
    bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            var cfg = new ConfigFile();
            cfg.SetValue("audio", "music", value);
            cfg.Save(SavePath);
            Apply();
        }
    }

    public override void _Ready()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SavePath) == Error.Ok) _enabled = cfg.GetValue("audio", "music", true).AsBool();

        _player = new AudioStreamPlayer { VolumeDb = -13f };
        AddChild(_player);

        Task.Run(() =>
        {
            var data = Render();
            Callable.From(() => OnRendered(data)).CallDeferred();
        });
    }

    void OnRendered(byte[] data)
    {
        _player.Stream = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Rate,
            Stereo = false,
            Data = data,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward,
            LoopBegin = 0,
            LoopEnd = Length,
        };
        Apply();
    }

    void Apply()
    {
        if (_player?.Stream == null) return;
        if (_enabled && !_player.Playing) _player.Play();
        else if (!_enabled) _player.Stop();
    }

    // ------------------------------------------------------------------ composition

    // MIDI note numbers. Chords last 2 bars (32 steps) each: root (bass octave) + major/minor third.
    static readonly (int root, bool minor)[] Chords =
    {
        (45, true), (41, false), (48, false), (43, false), // A: Am  F  C  G
        (45, true), (41, false), (43, false), (40, false), // B: Am  F  G  E
    };

    // Section B lead: (start step within the 2-bar chord, length in steps, MIDI note); 0 = rest.
    static readonly (int start, int len, int note)[][] Lead =
    {
        new[] { (0, 4, 76), (4, 4, 81), (8, 2, 79), (10, 2, 76), (12, 4, 74), (16, 12, 76) },             // Am
        new[] { (0, 4, 77), (4, 4, 76), (8, 2, 74), (10, 2, 72), (12, 4, 69), (16, 12, 72) },             // F
        new[] { (0, 4, 74), (4, 4, 79), (8, 2, 77), (10, 2, 74), (12, 4, 71), (16, 12, 74) },             // G
        new[] { (0, 4, 76), (4, 4, 80), (8, 4, 83), (12, 4, 80), (16, 4, 76), (20, 4, 74), (24, 4, 72), (28, 4, 71) }, // E
    };

    enum Wave { Sine, Saw, Square, Pulse }

    static byte[] Render()
    {
        var drums = new float[Length];
        var synths = new float[Length]; // ducked by the kick (pumping "sidechain" feel)
        var echoed = new float[Length]; // arp + lead, fed through the echo
        var rng = new Random(7);

        for (int step = 0; step < Steps; step++)
        {
            double t = step * StepSec;
            int bar = step / 16, inBar = step % 16, inChord = step % 32;
            bool sectionB = bar >= 8;
            var (root, minor) = Chords[step / 32];
            int[] tones = { root, root + (minor ? 3 : 4), root + 7 };

            // Drums: kick every beat, snare on 2 and 4, open-ish hats on the off-beats (16ths in section B).
            if (inBar % 4 == 0) Kick(drums, t);
            if (inBar == 4 || inBar == 12) Snare(drums, t, rng);
            if (inBar % 4 == 2) Hat(drums, t, rng, 0.22f, 0.05);
            else if (sectionB && inBar % 2 == 1) Hat(drums, t, rng, 0.09f, 0.025);
            if (bar % 4 == 3 && inBar >= 12) Snare(drums, t, rng, 0.5f); // fill into every 4th bar

            // Bass: gallop (1, 3, 4 of each beat), octave jump on the last 16th of each beat.
            int beatPos = inBar % 4;
            if (beatPos != 1)
            {
                int note = beatPos == 3 ? root + 12 : root;
                Note(synths, t, StepSec * 0.8, Hz(note - 12), 0.30f, Wave.Sine, 0.003, 9, 0.5f, 0.03, 20000);
                Note(synths, t, StepSec * 0.8, Hz(note), 0.22f, Wave.Saw, 0.003, 14, 0.3f, 0.03, 700);
            }

            // Arpeggio: chord tones two octaves up, up-and-down pattern on every 16th.
            int[] pattern = { 0, 1, 2, 3, 2, 1, 2, 3 };
            int idx = pattern[inBar % pattern.Length];
            int arpNote = (idx == 3 ? root + 12 : tones[idx]) + 24;
            Note(echoed, t, StepSec * 0.5, Hz(arpNote), sectionB ? 0.075f : 0.095f, Wave.Pulse, 0.002, 26, 0f, 0.02, 3800);

            if (inChord == 0 && sectionB)
            {
                // Pad: detuned saws holding the chord for 2 bars, slow swell.
                foreach (int n in tones)
                    foreach (float detune in new[] { 0.997f, 1.003f })
                        Note(synths, t, 32 * StepSec, Hz(n + 12) * detune, 0.05f, Wave.Saw, 0.6, 0.5, 0.7f, 0.4, 1600);

                foreach (var (start, len, note) in Lead[step / 32 - 4])
                    Note(echoed, t + start * StepSec, len * StepSec * 0.92, Hz(note), 0.15f, Wave.Square, 0.008, 3, 0.7f, 0.06, 3000, vibrato: true);
            }
        }

        Echo(echoed, delaySteps: 3, feedback: 0.38f, wet: 0.45f);

        var mix = new float[Length];
        double beatSamples = 4 * StepSec * Rate;
        float peak = 0f;
        for (int i = 0; i < Length; i++)
        {
            double sinceKick = (i % beatSamples) / Rate;
            float duck = 1f - 0.45f * MathF.Exp(-(float)sinceKick * 14f);
            float v = drums[i] + (synths[i] + echoed[i]) * duck;
            v = MathF.Tanh(v * 1.2f); // gentle saturation glues it together
            mix[i] = v;
            peak = MathF.Max(peak, MathF.Abs(v));
        }

        var data = new byte[Length * 2];
        float gain = peak > 0 ? 0.92f / peak : 1f;
        for (int i = 0; i < Length; i++)
        {
            short s = (short)(Math.Clamp(mix[i] * gain, -1f, 1f) * short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return data;
    }

    static float Hz(int midi) => 440f * MathF.Pow(2f, (midi - 69) / 12f);

    static int Index(double sec) => (int)(sec * Rate);

    /// <summary>
    /// One synth note, added into <paramref name="buf"/> (wrapping around the end so tails continue at the
    /// loop start). Envelope: attack, then decay towards a sustain level; release after the note's length.
    /// A one-pole low-pass at <paramref name="cutoff"/> Hz softens the bright waveforms.
    /// </summary>
    static void Note(float[] buf, double start, double length, float hz, float amp, Wave wave,
        double attack, double decay, float sustain, double release, float cutoff, bool vibrato = false)
    {
        int s0 = Index(start);
        int total = Index(length + release);
        int releaseAt = Index(length);
        float lp = 1f - MathF.Exp(-2f * MathF.PI * cutoff / Rate);
        double phase = 0;
        float y = 0f, level = 0f;
        for (int i = 0; i < total; i++)
        {
            double t = (double)i / Rate;
            float env = t < attack ? (float)(t / attack) : sustain + (1 - sustain) * MathF.Exp(-(float)((t - attack) * decay));
            if (i < releaseAt) level = env;
            else env = level * (1f - (float)(i - releaseAt) / (total - releaseAt));

            float f = hz;
            if (vibrato && t > 0.15) f *= 1f + 0.006f * MathF.Sin((float)(t * 5.5 * Math.Tau));
            phase += f / Rate;
            float frac = (float)(phase - Math.Floor(phase));
            float x = wave switch
            {
                Wave.Sine => MathF.Sin(frac * MathF.Tau),
                Wave.Saw => 2f * frac - 1f,
                Wave.Square => frac < 0.5f ? 1f : -1f,
                _ => frac < 0.25f ? 1f : -1f,
            };
            y += lp * (x - y);
            buf[(s0 + i) % buf.Length] += y * env * amp;
        }
    }

    /// <summary>Punchy kick: a sine sweeping 160 → 45 Hz with a click on top.</summary>
    static void Kick(float[] buf, double start)
    {
        int s0 = Index(start), n = Index(0.28);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float hz = 45f + 115f * MathF.Exp(-t * 28f);
            phase += hz / Rate;
            float v = MathF.Sin((float)(phase * Math.Tau)) * MathF.Exp(-t * 9f) * 0.75f;
            if (t < 0.004f) v += 0.25f * (1f - t / 0.004f);
            buf[(s0 + i) % buf.Length] += v;
        }
    }

    static void Snare(float[] buf, double start, Random rng, float amp = 1f)
    {
        int s0 = Index(start), n = Index(0.18);
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float bright = noise - prev * 0.6f; // tilt towards the highs
            prev = noise;
            float tone = MathF.Sin(t * 190f * MathF.Tau) * MathF.Exp(-t * 30f);
            buf[(s0 + i) % buf.Length] += (bright * 0.32f * MathF.Exp(-t * 20f) + tone * 0.25f) * amp;
        }
    }

    static void Hat(float[] buf, double start, Random rng, float amp, double length)
    {
        int s0 = Index(start), n = Index(length);
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float high = noise - prev; // crude high-pass: only the fizz
            prev = noise;
            buf[(s0 + i) % buf.Length] += high * amp * MathF.Exp(-t / (float)length * 4f);
        }
    }

    /// <summary>Feedback echo that wraps around the loop, so repeats spill seamlessly into the next lap.</summary>
    static void Echo(float[] buf, int delaySteps, float feedback, float wet)
    {
        int d = Index(delaySteps * StepSec);
        int n = buf.Length;
        var echo = new float[n];
        for (int pass = 0; pass < 3; pass++) // repeated passes let wrapped repeats settle
            for (int i = 0; i < n; i++)
            {
                int j = (i - d + n) % n;
                echo[i] = feedback * (buf[j] + echo[j]);
            }
        for (int i = 0; i < n; i++) buf[i] += echo[i] * wet;
    }
}

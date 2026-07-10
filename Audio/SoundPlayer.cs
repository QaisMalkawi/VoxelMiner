using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoxelMiner.Audio;

/// Tiny synthesized effects (noise bursts and square blips) mixed via NAudio.
/// All failures are swallowed — audio is best-effort.
public sealed class SoundPlayer : IDisposable
{
    const int SampleRate = 44100;
    WaveOutEvent _output;
    MixingSampleProvider _mixer;
    static readonly Random Rng = new();

    /// Master volume multiplier (0..1), driven by the settings screen.
    public float Volume = 1f;

    public SoundPlayer()
    {
        try
        {
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)) { ReadFully = true };
            _output = new WaveOutEvent();
            _output.Init(_mixer);
            _output.Play();
        }
        catch
        {
            _output = null;
        }
    }

    /// Band-passed noise burst with linear decay (digging/breaking).
    public void Noise(double duration, double volume, double frequency)
    {
        if (_output == null || Volume <= 0) return;
        volume *= Volume;
        try
        {
            int n = (int)(SampleRate * duration);
            var samples = new float[n];
            // RBJ biquad bandpass
            double w0 = 2 * Math.PI * frequency / SampleRate, q = 1.2;
            double alpha = Math.Sin(w0) / (2 * q);
            double b0 = alpha, b1 = 0, b2 = -alpha;
            double a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < n; i++)
            {
                double x = (Rng.NextDouble() * 2 - 1) * (1.0 - (double)i / n);
                double y = (b0 / a0) * x + (b1 / a0) * x1 + (b2 / a0) * x2 - (a1 / a0) * y1 - (a2 / a0) * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                samples[i] = (float)(y * volume * 4);
            }
            _mixer.AddMixerInput(new ClipProvider(samples));
        }
        catch { /* best-effort */ }
    }

    /// Square-wave blip with exponential decay (placing/crafting/UI).
    public void Tone(double frequency, double duration, double volume)
    {
        if (_output == null || Volume <= 0) return;
        volume *= Volume;
        try
        {
            int n = (int)(SampleRate * duration);
            var samples = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SampleRate;
                double square = Math.Sign(Math.Sin(2 * Math.PI * frequency * t));
                double envelope = Math.Pow(0.001, (double)i / n);
                samples[i] = (float)(square * envelope * volume);
            }
            _mixer.AddMixerInput(new ClipProvider(samples));
        }
        catch { /* best-effort */ }
    }

    sealed class ClipProvider : ISampleProvider
    {
        readonly float[] _samples;
        int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        public ClipProvider(float[] samples) => _samples = samples;

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _samples.Length - _pos);
            Array.Copy(_samples, _pos, buffer, offset, available);
            _pos += available;
            return available; // 0 → mixer removes this input
        }
    }

    public void Dispose()
    {
        _output?.Dispose();
    }
}

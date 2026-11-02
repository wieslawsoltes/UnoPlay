using UnoPlay.Media;

const int rate = 48000;
var signal = new ClassicSignal(rate);
float[] tone = new float[rate * 2];
for (int i = 0; i < tone.Length; i += 2)
    tone[i] = tone[i + 1] = (float)(.1 * Math.Sin(i / 2 * 2 * Math.PI * 1000 / rate));
var filtered = (float[])tone.Clone();
var settings = new double[11];
settings[5] = -12; // fifth classic band, centered at 1 kHz.
signal.Equalize(settings);
signal.Balance(-1);
signal.Process(filtered, 2);
double energy = 0, reference = 0;
for (int i = rate; i < filtered.Length; i += 2)
{
    energy += filtered[i] * filtered[i];
    reference += tone[i] * tone[i];
    if (filtered[i + 1] != 0) throw new Exception("Hard-left balance leaked into right channel.");
}
var db = 10 * Math.Log10(energy / reference);
if (Math.Abs(db + 12) > .1) throw new Exception($"Unexpected 1 kHz gain: {db} dB");
if (signal.Spectrum().Max() <= 100 || signal.Waveform().All(sample => sample == 128))
    throw new Exception("The filtered samples did not reach both visualizers.");
Console.WriteLine($"Stereo EQ measured {db:F2} dB at 1 kHz. Balance and FFT/oscilloscope passed.");

if (args.Length != 0)
{
    using var transport = new DesktopAudio();
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.Ended += (_, _) => ended.TrySetResult();
    transport.Open(args[0]);
    if (transport.Duration <= TimeSpan.Zero) throw new Exception("The decoder returned no duration.");
    transport.Play();
    await Task.Delay(300);
    if (transport.Position <= TimeSpan.Zero || transport.Signal.Spectrum().Max() <= 0)
        throw new Exception("The native device did not render decoded audio.");
    transport.Pause();
    transport.Seek(transport.Duration - TimeSpan.FromMilliseconds(200));
    transport.Play();
    await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("Native decode, output, seek and EOF passed.");
}

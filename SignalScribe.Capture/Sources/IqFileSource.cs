using System.Buffers.Binary;

namespace SignalScribe.Capture.Sources;

/// <summary>
/// Replay source reading interleaved 16-bit signed little-endian I/Q (cs16) from a file.
/// Drives the exact pipeline the RSP1 source drives — the basis of all DSP regression tests.
/// </summary>
public sealed class IqFileSource(string path, double sampleRate, long centerFrequencyHz, bool throttle = false)
    : ISampleSource
{
    private FileStream? _stream;

    private long _sampleCounter;

    private byte[] _readBuffer = [];

    public double SampleRate { get; } = sampleRate;

    public long CenterFrequencyHz { get; } = centerFrequencyHz;

    public long SampleCounter => Interlocked.Read(ref _sampleCounter);

    public void Start()
    {
        _stream = File.OpenRead(path);
        _sampleCounter = 0;
    }

    public void Stop()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public int Read(Span<float> iq)
    {
        if (_stream is null)
        {
            return 0;
        }

        var bytesWanted = iq.Length * sizeof(short);
        if (_readBuffer.Length < bytesWanted)
        {
            _readBuffer = new byte[bytesWanted];
        }

        var bytesRead = _stream.Read(_readBuffer, 0, bytesWanted);
        if (bytesRead == 0)
        {
            return 0; // end of fixture
        }

        var floats = bytesRead / sizeof(short);
        for (var i = 0; i < floats; i++)
        {
            var raw = BinaryPrimitives.ReadInt16LittleEndian(_readBuffer.AsSpan(i * sizeof(short)));
            iq[i] = raw / 32768f;
        }

        Interlocked.Add(ref _sampleCounter, floats / 2);

        if (throttle)
        {
            // Approximate real-time pacing for live-replay debugging; regression tests run unthrottled.
            Thread.Sleep(TimeSpan.FromSeconds(floats / 2 / SampleRate));
        }

        return floats;
    }

    public void Dispose() => Stop();
}

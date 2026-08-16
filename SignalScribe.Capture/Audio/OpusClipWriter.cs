using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace SignalScribe.Capture.Audio;

/// <summary>
/// Writes 16 kHz mono PCM to an Opus-in-OGG clip at 32 kbps VBR voice mode (CLAUDE.md: audio is
/// ground truth in exactly this format). One writer per open squelch gate.
/// </summary>
public sealed class OpusClipWriter : IDisposable
{
    public const int SampleRate = 16_000;

    private readonly FileStream _file;

    private readonly OpusOggWriteStream _ogg;

    public OpusClipWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = File.Create(path);

        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 32_000;
        encoder.UseVBR = true;

        _ogg = new OpusOggWriteStream(encoder, _file, inputSampleRate: SampleRate);
    }

    private short[] _scratch = new short[4096];

    public void Write(ReadOnlySpan<short> pcm)
    {
        // Reused scratch instead of ToArray — no per-write allocation on the audio path.
        if (_scratch.Length < pcm.Length)
        {
            _scratch = new short[pcm.Length];
        }

        pcm.CopyTo(_scratch);
        _ogg.WriteSamples(_scratch, 0, pcm.Length);
    }

    public void Dispose()
    {
        _ogg.Finish(); // finalizes the OGG stream and closes the underlying file
    }
}

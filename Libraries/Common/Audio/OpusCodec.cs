using Concentus.Enums;
using Concentus.Structs;
using System.Runtime.InteropServices;

namespace Common.Audio;

public sealed class OpusCodec {
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int BytesPerSample = 2;
    private const int DefaultMaxPacketBytes = 1275;

    private readonly OpusEncoder encoder;
    private readonly OpusDecoder decoder;
    private readonly short[] pcmBuffer;
    private readonly short[] decodeBuffer;

    public int FrameSamples { get; }
    public int MaxPacketSize { get; }
    public int Bitrate { get; }
    public string FormatLabel { get; }

    public OpusCodec(int frameMs = 10, int bitrate = 32000, int? maxPacketBytes = null) {
        FrameSamples = SampleRate * frameMs / 1000;
        Bitrate = bitrate;
        MaxPacketSize = maxPacketBytes ?? DefaultMaxPacketBytes;
        FormatLabel = $"OPUS_{SampleRate / 1000}k_Mono_{bitrate / 1000}kbps";

        encoder = OpusEncoder.Create(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrate;

        decoder = OpusDecoder.Create(SampleRate, Channels);

        pcmBuffer = new short[FrameSamples];
        decodeBuffer = new short[FrameSamples];
    }

    public int Encode(ReadOnlySpan<byte> pcmBytes, byte[] output) {
        if (pcmBytes.Length != FrameSamples * BytesPerSample) {
            throw new ArgumentException($"Expected {FrameSamples * BytesPerSample} bytes for {FrameSamples} samples.", nameof(pcmBytes));
        }
        if (output.Length < MaxPacketSize) {
            throw new ArgumentException($"Output buffer must be at least {MaxPacketSize} bytes.", nameof(output));
        }

        MemoryMarshal.Cast<byte, short>(pcmBytes).CopyTo(pcmBuffer);
        return encoder.Encode(pcmBuffer, 0, FrameSamples, output, 0, output.Length);
    }

    public byte[] Decode(ReadOnlySpan<byte> encoded) {
        int samples = decoder.Decode(encoded.ToArray(), 0, encoded.Length, decodeBuffer, 0, FrameSamples, false);
        var pcmBytes = MemoryMarshal.AsBytes(decodeBuffer.AsSpan(0, samples));
        return pcmBytes.ToArray();
    }
}

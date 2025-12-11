using OpenTK.Audio.OpenAL;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Common.Audio;

public sealed class MicRecorder : IDisposable {
    private const int SampleRate = 48000;
    private const ALFormat CaptureFormat = ALFormat.Mono16;

    private readonly int samplesPerFrame;

    private readonly ConcurrentQueue<byte[]> frames = new();
    private ALCaptureDevice _captureDevice;

    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _disposed;

    public MicRecorder(int frameMs = 20) {

        samplesPerFrame = SampleRate * frameMs / 1000;
        // Internal ring buffer (~1 second)
        int captureBufferSamples = SampleRate * 1;

        // Open default capture device
        _captureDevice = ALC.CaptureOpenDevice(
            devicename: null,
            frequency: SampleRate,
            format: CaptureFormat,
            buffersize: captureBufferSamples
        );

        if (_captureDevice == ALCaptureDevice.Null)
            throw new InvalidOperationException("Failed to open OpenAL capture device.");

        Console.WriteLine("[OpenAL] Microphone capture device opened.");
    }

    public void Start() {
        if (_running || _disposed) return;

        ALC.CaptureStart(_captureDevice);
        _running = true;

        _worker = new Thread(CaptureLoop) {
            IsBackground = true,
            Name = "OpenAL Mic Capture"
        };
        _worker.Start();
    }

    private void CaptureLoop() {
        var sampleBuffer = new short[samplesPerFrame];

        try {
            while (_running) {
                // How many samples are ready?
                ALC.GetInteger(
                    _captureDevice,
                    AlcGetInteger.CaptureSamples,
                    1,
                    out int available
                );

                if (available >= samplesPerFrame) {
                    // Read a chunk into the short[] buffer
                    ALC.CaptureSamples(_captureDevice, sampleBuffer, samplesPerFrame);

                    // Convert short[] -> byte[] (PCM16 LE)
                    var spanShort = sampleBuffer.AsSpan();
                    var spanBytes = MemoryMarshal.AsBytes(spanShort);
                    var frameBytes = spanBytes.ToArray(); // copy

                    frames.Enqueue(frameBytes);
                }
                else {
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex) {
            Console.WriteLine("[OpenAL] Error in MicRecorder loop: " + ex);
        }
    }

    public bool TryDequeue(out byte[]? buffer) => frames.TryDequeue(out buffer);

    public void Stop() {
        if (!_running) return;
        _running = false;

        try { _worker?.Join(200); } catch { /* ignore */ }
        _worker = null;

        if (_captureDevice != ALCaptureDevice.Null) {
            ALC.CaptureStop(_captureDevice);
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        Stop();

        if (_captureDevice != ALCaptureDevice.Null) {
            ALC.CaptureCloseDevice(_captureDevice);
            _captureDevice = ALCaptureDevice.Null;
        }
    }
}

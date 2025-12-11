using OpenTK.Audio.OpenAL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Common.Audio;
public sealed class AudioPlayer : IDisposable {
    private const int SampleRate = 48000;
    private const ALFormat PlaybackFormat = ALFormat.Mono16;

    private const int FrameMs = 20;
    private const int SamplesPerFrame = SampleRate * FrameMs / 1000;
    private const int NumBuffers = 8;

    private readonly ConcurrentQueue<byte[]> queue = new();

    private ALDevice _device = ALDevice.Null;
    private ALContext _context = ALContext.Null;
    private int _source;
    private int[] _buffers = Array.Empty<int>();

    private Thread? _playbackThread;
    private volatile bool _running;
    private volatile bool _disposed;

    // latencyMs/jitterMs kept for signature compatibility; we mainly use them
    // to decide how many buffers we keep in flight (via NumBuffers).
    public AudioPlayer(int latencyMs = 100, int jitterMs = 500) {
        // Enumerate playback devices
        var devices = ALC.GetString(ALDevice.Null, AlcGetStringList.DeviceSpecifier);
        if (devices.Count == 0)
            throw new InvalidOperationException("OpenAL reports no playback devices.");

        string chosen = devices[0];
        Console.WriteLine($"[OpenAL] Using playback device: {chosen}");

        _device = ALC.OpenDevice(chosen);
        if (_device == ALDevice.Null)
            throw new InvalidOperationException("Failed to open OpenAL playback device.");

        _context = ALC.CreateContext(_device, (int[]?)null);
        if (_context == ALContext.Null)
            throw new InvalidOperationException("Failed to create OpenAL context.");

        if (!ALC.MakeContextCurrent(_context))
            throw new InvalidOperationException("Failed to make OpenAL context current.");

        _source = AL.GenSource();
        _buffers = new int[NumBuffers];
        AL.GenBuffers(_buffers);

        // Prime queue with silence so playback starts smoothly
        short[] silence = new short[SamplesPerFrame];
        foreach (int buf in _buffers) {
            AL.BufferData(buf, PlaybackFormat, silence, SampleRate);
            AL.SourceQueueBuffer(_source, buf);
        }

        AL.SourcePlay(_source);

        _running = true;
        _playbackThread = new Thread(PlaybackLoop) {
            IsBackground = true,
            Name = "OpenAL Playback"
        };
        _playbackThread.Start();

        Console.WriteLine("[OpenAL] Playback ready.");
    }

    private void PlaybackLoop() {
        // Context must be current on the thread that uses AL.* APIs
        ALC.MakeContextCurrent(_context);

        var silence = new short[SamplesPerFrame];

        try {
            while (_running) {
                // How many buffers have finished playing?
                AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out int processed);

                while (processed-- > 0) {
                    int bufferId = AL.SourceUnqueueBuffer(_source);

                    byte[]? frame = null;
                    if (!queue.TryDequeue(out frame) || frame == null) {
                        // No audio available: play silence
                        AL.BufferData(bufferId, PlaybackFormat, silence, SampleRate);
                    }
                    else {
                        // Ensure the frame length is a multiple of 2 bytes
                        if ((frame.Length & 1) != 0) {
                            // Trim one byte if odd length (shouldn't happen in your pipeline)
                            Array.Resize(ref frame, frame.Length - 1);
                        }

                        AL.BufferData(bufferId, PlaybackFormat, frame, SampleRate);
                    }

                    AL.SourceQueueBuffer(_source, bufferId);
                }

                // Ensure source is still playing
                var state = (ALSourceState)AL.GetSource(_source, ALGetSourcei.SourceState);
                if (state != ALSourceState.Playing) {
                    AL.SourcePlay(_source);
                }

                Thread.Sleep(1);
            }
        }
        catch (Exception ex) {
            Console.WriteLine("[OpenAL] Error in Pcm16Player loop: " + ex);
        }
    }

    public void AddFrame(byte[] frame, int offset, int count) {
        if (_disposed) return;

        // Make a copy; caller may reuse the buffer
        var copy = new byte[count];
        Buffer.BlockCopy(frame, offset, copy, 0, count);
        queue.Enqueue(copy);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        _running = false;
        try { _playbackThread?.Join(200); } catch { /* ignore */ }
        _playbackThread = null;

        if (_source != 0) {
            try {
                AL.SourceStop(_source);
                AL.GetSource(_source, ALGetSourcei.BuffersQueued, out int queued);
                while (queued-- > 0) {
                    AL.SourceUnqueueBuffer(_source);
                }
                AL.DeleteSource(_source);
            }
            catch { /* ignore */ }
            _source = 0;
        }

        if (_buffers.Length > 0) {
            try { AL.DeleteBuffers(_buffers.Length, _buffers); } catch { }
            _buffers = Array.Empty<int>();
        }

        if (_context != ALContext.Null) {
            try {
                ALC.MakeContextCurrent(ALContext.Null);
                ALC.DestroyContext(_context);
            }
            catch { /* ignore */ }
            _context = ALContext.Null;
        }

        if (_device != ALDevice.Null) {
            try { ALC.CloseDevice(_device); } catch { }
            _device = ALDevice.Null;
        }
    }
}

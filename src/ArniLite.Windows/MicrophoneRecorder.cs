using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ArniLite.Windows
{
    public sealed class MicrophoneDevice
    {
        public int Id { get; }
        public string Name { get; }
        public MicrophoneDevice(int id, string name) { Id = id; Name = name; }
    }

    public sealed class RecordedSpeech
    {
        public byte[] Pcm { get; }
        public bool HasSignal { get; }
        public RecordedSpeech(byte[] pcm, bool hasSignal) { Pcm = pcm; HasSignal = hasSignal; }
        public void WriteWave(string path)
        {
            using var file = new FileStream(path, FileMode.CreateNew);
            using var writer = new BinaryWriter(file, Encoding.ASCII);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + Pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(16000); writer.Write(32000); writer.Write((ushort)2); writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(Pcm.Length); writer.Write(Pcm);
        }
    }

    // WinMM is available on the legacy Windows baseline; no browser or resident recognizer.
    public sealed class MicrophoneRecorder : IDisposable
    {
        public const int MaximumSeconds = 120;
        private const uint DataMessage = 0x3C0;
        private readonly object _gate = new object();
        private readonly WaveCallback _callback;
        private readonly List<IntPtr> _headers = new List<IntPtr>();
        private readonly List<IntPtr> _buffers = new List<IntPtr>();
        private readonly MemoryStream _pcm = new MemoryStream();
        private IntPtr _handle;
        private bool _recording;
        private bool _stopping;
        private bool _limitSignaled;
        private int _loudSamples;
        public event EventHandler? LimitReached;
        public MicrophoneRecorder() { _callback = OnWave; }

        public static IReadOnlyList<MicrophoneDevice> Devices()
        {
            var result = new List<MicrophoneDevice>();
            for (uint i = 0; i < waveInGetNumDevs(); i++)
                if (waveInGetDevCaps(new UIntPtr(i), out var caps, (uint)Marshal.SizeOf(typeof(WaveCaps))) == 0)
                    result.Add(new MicrophoneDevice((int)i, caps.Name));
            return result;
        }

        public void Start(int deviceId)
        {
            if (_handle != IntPtr.Zero) throw new InvalidOperationException("Already recording.");
            var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSecond = 16000, AverageBytesPerSecond = 32000, BlockAlign = 2, BitsPerSample = 16 };
            Check(waveInOpen(out _handle, unchecked((uint)deviceId), ref format, _callback, UIntPtr.Zero, 0x30000));
            try
            {
                _pcm.SetLength(0); _loudSamples = 0; _limitSignaled = false;
                for (var i = 0; i < 4; i++)
                {
                    var buffer = Marshal.AllocHGlobal(3200); _buffers.Add(buffer);
                    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHeader))); _headers.Add(pointer);
                    Marshal.StructureToPtr(new WaveHeader { Data = buffer, Length = 3200 }, pointer, false);
                    Check(waveInPrepareHeader(_handle, pointer, (uint)Marshal.SizeOf(typeof(WaveHeader))));
                    Check(waveInAddBuffer(_handle, pointer, (uint)Marshal.SizeOf(typeof(WaveHeader))));
                }
                _recording = true;
                Check(waveInStart(_handle));
            }
            catch { Dispose(); throw; }
        }

        private void OnWave(IntPtr handle, uint message, UIntPtr instance, IntPtr headerPointer, UIntPtr reserved)
        {
            if (message != DataMessage) return;
            var reached = false;
            lock (_gate)
            {
                if (!_recording) return;
                var header = Marshal.PtrToStructure<WaveHeader>(headerPointer);
                var count = (int)Math.Min(header.Recorded, (uint)(MaximumSeconds * 32000 - _pcm.Length));
                var bytes = new byte[count];
                Marshal.Copy(header.Data, bytes, 0, count);
                _pcm.Write(bytes, 0, count);
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                    if (Math.Abs((int)(short)(bytes[i] | bytes[i + 1] << 8)) > 500) _loudSamples++;
                if (_pcm.Length >= MaximumSeconds * 32000)
                {
                    if (!_limitSignaled) { _limitSignaled = true; reached = true; }
                }
                else if (!_stopping && waveInAddBuffer(handle, headerPointer, (uint)Marshal.SizeOf(typeof(WaveHeader))) != 0)
                { if (!_limitSignaled) { _limitSignaled = true; reached = true; } }
            }
            // Do not call waveInReset/Stop from the native callback thread.
            if (reached) System.Threading.ThreadPool.QueueUserWorkItem(_ => LimitReached?.Invoke(this, EventArgs.Empty));
        }

        public RecordedSpeech Stop()
        {
            // Reset flushes the partial final buffer through the callback before recording is disabled.
            lock (_gate) _stopping = true;
            if (_handle != IntPtr.Zero) { waveInStop(_handle); waveInReset(_handle); }
            lock (_gate) _recording = false;
            var result = new RecordedSpeech(_pcm.ToArray(), _loudSamples > 160);
            Dispose();
            return result;
        }

        public void Dispose()
        {
            lock (_gate) _recording = false;
            if (_handle != IntPtr.Zero)
            {
                waveInReset(_handle);
                foreach (var header in _headers) waveInUnprepareHeader(_handle, header, (uint)Marshal.SizeOf(typeof(WaveHeader)));
                if (waveInClose(_handle) != 0) return; // Never free buffers still owned by a driver.
                _handle = IntPtr.Zero;
            }
            foreach (var header in _headers) Marshal.FreeHGlobal(header);
            foreach (var buffer in _buffers) Marshal.FreeHGlobal(buffer);
            _headers.Clear(); _buffers.Clear(); _pcm.Dispose();
        }
        private static void Check(uint result) { if (result != 0) throw new InvalidOperationException("Microphone error " + result); }
        private delegate void WaveCallback(IntPtr handle, uint message, UIntPtr instance, IntPtr parameter1, UIntPtr parameter2);
        [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat { public ushort FormatTag, Channels; public uint SamplesPerSecond, AverageBytesPerSecond; public ushort BlockAlign, BitsPerSample, ExtraSize; }
        [StructLayout(LayoutKind.Sequential)] private struct WaveHeader { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WaveCaps { public ushort Manufacturer, Product; public uint Driver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; }
        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW")] private static extern uint waveInGetDevCaps(UIntPtr id, out WaveCaps caps, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, WaveCallback callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
    }
}

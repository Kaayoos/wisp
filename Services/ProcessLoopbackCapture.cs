using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace Wisp.Services
{
    /// <summary>
    /// Captures the audio of a single application (process tree) using the Windows process-loopback
    /// API (<c>ActivateAudioInterfaceAsync</c> with <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>,
    /// Windows 10 build 19041+). NAudio 2.x has no wrapper for this, so the COM interop lives here.
    ///
    /// Two modes:
    ///   • INCLUDE - capture ONLY the target process tree (used for the "social app" path).
    ///   • EXCLUDE - capture everything EXCEPT the target process tree (used to make the
    ///     "system / game" path while keeping the social app out of it).
    ///
    /// The surface deliberately mirrors NAudio's WasapiCapture (<see cref="DataAvailable"/>,
    /// <see cref="WaveFormat"/>, StartRecording/StopRecording/Dispose) so <c>AudioCaptureManager</c>
    /// can treat it like the other capture sources. Output is 48 kHz / 32-bit IEEE float / stereo.
    /// All failures are logged and leave the source silently inactive (graceful degrade) rather than
    /// throwing into the recorder.
    /// </summary>
    public sealed class ProcessLoopbackCapture : IDisposable
    {
        private readonly uint _targetPid;
        private readonly bool _includeTree;

        private Thread? _captureThread;
        private IntPtr _hEvent = IntPtr.Zero;
        private volatile bool _running;
        private readonly int _blockAlign;

        public WaveFormat WaveFormat { get; }
        public event EventHandler<WaveInEventArgs>? DataAvailable;

        /// <param name="targetProcessId">A PID anywhere in the target app's process tree.</param>
        /// <param name="includeTree">true = capture only that tree; false = capture all but that tree.</param>
        public ProcessLoopbackCapture(uint targetProcessId, bool includeTree)
        {
            _targetPid = targetProcessId;
            _includeTree = includeTree;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            _blockAlign = WaveFormat.BlockAlign; // 8 bytes (2ch * 4 bytes)
        }

        public void StartRecording()
        {
            if (_running) return;
            _running = true;

            // Auto-reset event signalled by WASAPI each engine period. Owned here (closed after the
            // thread is joined) so the capture thread only ever waits on it, never frees it.
            _hEvent = CreateEventW(IntPtr.Zero, false, false, null);
            if (_hEvent == IntPtr.Zero)
            {
                _running = false;
                Logger.Error("ProcessLoopbackCapture: CreateEvent failed; social/excluded source unavailable.");
                return;
            }

            _captureThread = new Thread(CaptureThreadProc)
            {
                IsBackground = true,
                Name = _includeTree ? "WispSocialCapture" : "WispSystemExclCapture"
            };
            // ActivateAudioInterfaceAsync delivers its completion on an MTA thread; initialize this
            // thread as MTA so the async activation/COM calls behave.
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }

        public void StopRecording()
        {
            _running = false;
            if (_hEvent != IntPtr.Zero) SetEvent(_hEvent); // wake the wait so the loop exits promptly

            try { _captureThread?.Join(2000); } catch { }
            _captureThread = null;

            if (_hEvent != IntPtr.Zero)
            {
                try { CloseHandle(_hEvent); } catch { }
                _hEvent = IntPtr.Zero;
            }
        }

        public void Dispose() => StopRecording();

        private void CaptureThreadProc()
        {
            IntPtr pFormat = IntPtr.Zero;
            IntPtr pProp = IntPtr.Zero;
            IntPtr pParams = IntPtr.Zero;
            IAudioClient? audioClient = null;
            IAudioCaptureClient? captureClient = null;
            bool started = false;

            try
            {
                // 1. Build AUDIOCLIENT_ACTIVATION_PARAMS describing the process-loopback request.
                var activation = new AudioClientActivationParams
                {
                    ActivationType = AudioClientActivationType.ProcessLoopback,
                    ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                    {
                        TargetProcessId = _targetPid,
                        ProcessLoopbackMode = _includeTree
                            ? ProcessLoopbackMode.IncludeTargetProcessTree
                            : ProcessLoopbackMode.ExcludeTargetProcessTree
                    }
                };
                int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
                pParams = Marshal.AllocHGlobal(paramsSize);
                Marshal.StructureToPtr(activation, pParams, false);

                // Wrap it in a VT_BLOB PROPVARIANT (the activation API takes the params this way).
                pProp = BuildBlobPropVariant(pParams, paramsSize);

                // 2. Activate an IAudioClient bound to the virtual process-loopback device.
                var handler = new ActivateCompletionHandler();
                int hr = ActivateAudioInterfaceAsync(
                    VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, IID_IAudioClient, pProp, handler, out _);
                if (hr != 0) { Marshal.ThrowExceptionForHR(hr); }

                if (!handler.Wait(3000))
                    throw new TimeoutException("Process-loopback activation did not complete in time.");
                if (handler.ActivateHr != 0 || handler.Client == null)
                    throw new COMException("Process-loopback activation failed.", handler.ActivateHr);

                audioClient = handler.Client;

                // 3. Initialize the stream. AUTOCONVERTPCM lets WASAPI hand us exactly the format we
                //    ask for (48k float stereo) regardless of the engine's native mix format.
                pFormat = BuildFloatFormat();
                const uint streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK
                                       | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                                       | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                                       | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                hr = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, streamFlags,
                    2_000_000 /* 200ms buffer */, 0, pFormat, IntPtr.Zero);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                hr = audioClient.SetEventHandle(_hEvent);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                hr = audioClient.GetService(IID_IAudioCaptureClient, out object svc);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                captureClient = (IAudioCaptureClient)svc;

                hr = audioClient.Start();
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                started = true;
                Logger.Info($"ProcessLoopbackCapture started ({(_includeTree ? "INCLUDE" : "EXCLUDE")} pid {_targetPid}).");

                // 4. Drain loop. Wake on the engine event (or every 200ms during silence) and pull all
                //    available packets, raising DataAvailable just like NAudio's capture sources.
                while (_running)
                {
                    WaitForSingleObject(_hEvent, 200);
                    if (!_running) break;
                    DrainPackets(captureClient);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"ProcessLoopbackCapture ({(_includeTree ? "INCLUDE" : "EXCLUDE")} pid {_targetPid}) failed; source inactive.", ex);
            }
            finally
            {
                try { if (started) audioClient?.Stop(); } catch { }
                if (captureClient != null) { try { Marshal.ReleaseComObject(captureClient); } catch { } }
                if (audioClient != null) { try { Marshal.ReleaseComObject(audioClient); } catch { } }
                if (pFormat != IntPtr.Zero) Marshal.FreeHGlobal(pFormat);
                if (pProp != IntPtr.Zero) Marshal.FreeHGlobal(pProp);
                if (pParams != IntPtr.Zero) Marshal.FreeHGlobal(pParams);
            }
        }

        private void DrainPackets(IAudioCaptureClient captureClient)
        {
            while (_running)
            {
                if (captureClient.GetNextPacketSize(out uint packetFrames) != 0 || packetFrames == 0)
                    break;

                if (captureClient.GetBuffer(out IntPtr dataPtr, out uint framesAvailable,
                        out uint flags, out _, out _) != 0)
                    break;

                int byteCount = (int)(framesAvailable * _blockAlign);
                if (byteCount > 0)
                {
                    var buffer = new byte[byteCount];
                    // A SILENT packet's memory is undefined; leave the buffer zeroed in that case so
                    // the timeline stays continuous without copying garbage.
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && dataPtr != IntPtr.Zero)
                        Marshal.Copy(dataPtr, buffer, 0, byteCount);

                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, byteCount));
                }

                captureClient.ReleaseBuffer(framesAvailable);
            }
        }

        /// <summary>Builds a heap WAVEFORMATEX for 48 kHz / 32-bit float / stereo.</summary>
        private static IntPtr BuildFloatFormat()
        {
            var wf = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 32,
                nBlockAlign = (ushort)(2 * 32 / 8),
                nAvgBytesPerSec = 48000u * (2 * 32 / 8),
                cbSize = 0
            };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(wf, p, false);
            return p;
        }

        /// <summary>
        /// Hand-builds a PROPVARIANT of type VT_BLOB pointing at the activation params. Offsets are
        /// bitness-aware (the union starts at offset 8; the BLOB pointer follows cbSize after pointer
        /// padding). The app runs as x64, but this stays correct either way.
        /// </summary>
        private static IntPtr BuildBlobPropVariant(IntPtr blobData, int blobSize)
        {
            int size = IntPtr.Size == 8 ? 24 : 16;
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0); // zero VT + reserved
            Marshal.WriteInt16(p, 0, unchecked((short)VT_BLOB));
            Marshal.WriteInt32(p, 8, blobSize);                          // blob.cbSize
            Marshal.WriteIntPtr(p, IntPtr.Size == 8 ? 16 : 12, blobData); // blob.pBlobData
            return p;
        }

        // ===================== Interop declarations =====================

        private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
        private const ushort VT_BLOB = 0x0041;

        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        private enum ProcessLoopbackMode { IncludeTargetProcessTree = 0, ExcludeTargetProcessTree = 1 }
        private enum AudioClientActivationType { Default = 0, ProcessLoopback = 1 }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParams
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        // AUDIOCLIENT_ACTIVATION_PARAMS - the C union holds only the loopback params for our type,
        // so a flat sequential layout matches the native memory layout.
        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParams
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParams ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(
                [MarshalAs(UnmanagedType.Error)] out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        // Marker interface: the completion handler must be agile for ActivateAudioInterfaceAsync.
        [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject { }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr eventHandle);
            [PreserveSig] int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out uint bufferFlags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint numFramesRead);
            [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
        }

        /// <summary>Blocks the capture thread until the async activation reports a result.</summary>
        private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            private readonly ManualResetEventSlim _done = new(false);
            public IAudioClient? Client;
            public int ActivateHr;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
            {
                try
                {
                    op.GetActivateResult(out int hr, out object iface);
                    ActivateHr = hr;
                    if (hr == 0 && iface != null) Client = (IAudioClient)iface;
                }
                catch (Exception ex)
                {
                    ActivateHr = ex.HResult != 0 ? ex.HResult : -1;
                }
                finally
                {
                    _done.Set();
                }
            }

            public bool Wait(int ms) => _done.Wait(ms);
        }
    }
}

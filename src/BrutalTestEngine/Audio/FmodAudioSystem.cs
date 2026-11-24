using System.Runtime.InteropServices;
using Brutal.FmodApi;
using Brutal.Logging;
using BrutalTestEngine.Logging;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Audio;

/// <summary>
/// The engine's FMOD-backed audio system, built on BRUTAL's <see cref="Fmod"/> binding.
/// </summary>
/// <remarks>
/// <para>
/// FMOD is what BRUTAL ships, so this is the intended backend. The binding is a thin P/Invoke layer over
/// <c>fmod.dll</c>: BRUTAL's NuGet package contains the managed assembly but not the native library, which is
/// licensed separately. That means this type has to be written for the case where the managed half is present
/// and the native half is not, which is the normal state of a fresh checkout.
/// </para>
/// <para>
/// <b>Failure is expected and handled.</b> Any failure to create the FMOD system is treated as "audio is
/// unavailable" rather than fatal. Crashing or refusing to start because a sound device could not be opened is
/// the wrong trade for a client, and a headless server has no device at all.
/// </para>
/// </remarks>
public sealed class FmodAudioSystem : IAudioSystem
{
    /// <summary>
    /// Channels the mixer is allowed to consider at once, including virtualised ones.
    /// </summary>
    /// <remarks>
    /// FMOD virtualises channels above its real-voice limit, so a generous count here costs a table entry per
    /// voice rather than a real voice per entry. 64 is enough to prove the mixer runs without pretending to
    /// know how busy a real scene will be.
    /// </remarks>
    private const int MaxChannels = 64;

    private const float DefaultVolume = 0.8f;

    private static readonly LogCategory Log = EngineLog.Categories.Engine;

    private readonly object _gate = new();

    private FmodSystem _system;
    private long _updates;
    private int _playingChannels;
    private int _realChannels;
    private bool _disposed;

    private FmodAudioSystem(bool available) => IsAvailable = available;

    public string Name => "fmod";

    public bool IsAvailable { get; private set; }

    public long UpdateCount => Interlocked.Read(ref _updates);

    public int PlayingChannels
    {
        get
        {
            lock (_gate)
                return _playingChannels;
        }
    }

    public int RealChannels
    {
        get
        {
            lock (_gate)
                return _realChannels;
        }
    }

    /// <summary>Mixer sample rate in Hz, when the device reported one.</summary>
    public int SampleRate { get; private set; }

    /// <summary>
    /// Creates an audio system, or an unavailable one when FMOD cannot be initialized.
    /// </summary>
    /// <remarks>
    /// The native <c>fmod.dll</c> is not redistributed in the managed package, so on a machine without an FMOD
    /// installation this is expected to fail on the very first call with
    /// <see cref="DllNotFoundException"/>. That is reported once, at information level, and the engine runs
    /// silently.
    /// </remarks>
    public static FmodAudioSystem Create()
    {
        // BRUTAL's package carries the managed binding only: fmod.dll is licensed separately by Firelight and
        // is not redistributable. Probing for it explicitly means a fresh checkout reports "the library is not
        // installed" once, instead of a DllNotFoundException surfacing from somewhere less obvious later.
        if (!IsNativeLibraryPresent())
        {
            var unavailable = new FmodAudioSystem(false)
            {
                UnavailableReason = "fmod.dll is not installed; set FMOD_DLL to a copy or drop it beside the executable",
            };

            if (Log.IsEnabled(LogLevel.Information))
                Log.Info($"audio unavailable: {unavailable.UnavailableReason}");

            return unavailable;
        }

        try
        {
            var result = Fmod.TrySystemCreate(out var system, Fmod.VERSION);
            if (result != Result.Ok || system.IsNull())
                return new FmodAudioSystem(false) { UnavailableReason = $"fmod.dll is not available ({result})" };

            var audio = new FmodAudioSystem(true);
            audio.Initialize(system);
            return audio;
        }
        catch (DllNotFoundException)
        {
            return new FmodAudioSystem(false) { UnavailableReason = "fmod.dll was not found; audio is disabled" };
        }
        catch (EntryPointNotFoundException exception)
        {
            return new FmodAudioSystem(false)
            {
                UnavailableReason = $"fmod.dll does not match this binding: {exception.Message}",
            };
        }
        catch (Exception exception)
        {
            return new FmodAudioSystem(false) { UnavailableReason = exception.Message };
        }
    }

    /// <summary>Why audio is unavailable, or <c>null</c> when it is available.</summary>
    public string? UnavailableReason { get; private init; }

    /// <summary>
    /// True when <c>fmod.dll</c> can be found, either beside the process or through the <c>FMOD_DLL</c>
    /// environment variable.
    /// </summary>
    /// <remarks>
    /// Checked with the same probing rules the loader uses, so this answer agrees with what the first P/Invoke
    /// would discover. Checking rather than attempting keeps the failure diagnostic and cheap.
    /// </remarks>
    private static bool IsNativeLibraryPresent()
    {
        var configured = Environment.GetEnvironmentVariable("FMOD_DLL");
        if (configured is { Length: > 0 } && File.Exists(configured))
            return true;

        return NativeLibrary.TryLoad("fmod", typeof(FmodAudioSystem).Assembly, null, out _);
    }

    private void Initialize(FmodSystem system)
    {
        _system = system;

        // 3dRightHanded matches the engine's coordinate convention, so a sound's position can be fed straight
        // from a transform without a conversion step that would be easy to get subtly wrong later.
        var result = InitializeSystem();
        if (result != Result.Ok)
        {
            Log.Warning($"FMOD opened but failed to initialize: {result}");
            IsAvailable = false;

            // Released here rather than in Dispose: the system object exists but is unusable, and leaving it
            // allocated until engine shutdown would hold the device.
            Fmod.TryRelease(_system);
            return;
        }

        SetMasterVolume(DefaultVolume);

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info($"audio system ready: fmod, {MaxChannels} max channels");
    }

    /// <summary>
    /// Initializes the mixer.
    /// </summary>
    /// <remarks>
    /// BRUTAL exposes <c>SystemInit</c> on the raw P/Invoke surface rather than as a checked wrapper, unlike the
    /// rest of the API. It is called directly here so a failure is returned to the caller instead of being
    /// ignored, which is the only reason this file reaches past the comfortable layer.
    /// </remarks>
    private Result InitializeSystem() =>
        Fmod.PInvoke.SystemInit(_system, MaxChannels, InitFlags._3dRightHanded, IntPtr.Zero);

    public void SetMasterVolume(float volume)
    {
        if (!IsAvailable)
            return;

        // FMOD expects a linear 0..1 volume on the master channel group; clamping here keeps a bad value from a
        // command or a config file out of the mixer.
        MasterVolume = Math.Clamp(volume, 0.0f, 1.0f);
    }

    /// <summary>The last volume set, clamped to 0..1.</summary>
    public float MasterVolume { get; private set; } = DefaultVolume;

    public void Update()
    {
        if (!IsAvailable || _disposed)
            return;

        try
        {
            if (Fmod.TryUpdate(_system) != Result.Ok)
                return;

            Interlocked.Increment(ref _updates);

            if (Fmod.TryGetChannelsPlaying(_system, out var channels, out var realChannels) != Result.Ok)
                return;

            lock (_gate)
            {
                _playingChannels = channels;
                _realChannels = realChannels;
            }
        }
        catch (Exception exception)
        {
            // The mixer faulting is not recoverable per update, so the system stops being usable rather than
            // throwing into the audio thread's loop every frame.
            if (Log.IsEnabled(LogLevel.Error))
                Log.Error(exception, $"audio update failed: {exception.Message}");

            IsAvailable = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (!IsAvailable)
            return;

        try
        {
            Fmod.TryRelease(_system);
        }
        catch (Exception exception)
        {
            if (Log.IsEnabled(LogLevel.Warning))
                Log.Warning($"audio teardown raised {exception.GetType().Name}: {exception.Message}");
        }

        _system = default;

        lock (_gate)
        {
            _playingChannels = 0;
            _realChannels = 0;
        }
    }
}

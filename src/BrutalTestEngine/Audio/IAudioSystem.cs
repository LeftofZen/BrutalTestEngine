namespace BrutalTestEngine.Audio;

/// <summary>
/// The seam between the engine and whatever plays sound.
/// </summary>
/// <remarks>
/// <para>
/// Audio is its own thread in this engine, not a call made from the simulation, because the two run on
/// different clocks. A game tick must never block on a mixer, and an audio thread that is late must not make a
/// tick late; the mixer is therefore fed a snapshot and left to interpolate the rest itself.
/// </para>
/// <para>
/// The surface is intentionally tiny — initialize, update, ask what is playing, shut down. Events, banks,
/// 3D attenuation and DSP graphs are all real features of the backend, and all of them are decisions that
/// belong to a game that has actually been specified. What is proven here is that the engine can own an audio
/// device on its own thread and keep it ticking without any other thread knowing.
/// </para>
/// <para>
/// <b>Threading.</b> A backend belongs to the audio thread. The counters are safe to read from any thread.
/// </para>
/// </remarks>
public interface IAudioSystem : IDisposable
{
    /// <summary>A name for the backend, used in logs and in the debug panel.</summary>
    string Name { get; }

    /// <summary>True when a device was opened and the system can be updated.</summary>
    bool IsAvailable { get; }

    /// <summary>Mixer updates performed since the system was created.</summary>
    long UpdateCount { get; }

    /// <summary>Channels currently being mixed, including virtual ones.</summary>
    int PlayingChannels { get; }

    /// <summary>Channels actually producing output, excluding those the mixer virtualised.</summary>
    int RealChannels { get; }

    /// <summary>
    /// Runs the mixer once. Called on the audio thread at its own rate, not at the simulation's tick rate.
    /// </summary>
    void Update();

    /// <summary>Sets the master volume, in the range 0 to 1.</summary>
    void SetMasterVolume(float volume);
}

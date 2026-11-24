using BrutalTestEngine.Audio;

namespace BrutalTestEngine;

/// <summary>
/// Chooses which audio system the engine runs on.
/// </summary>
/// <remarks>
/// The one place a concrete audio library is named, matching
/// <see cref="Networking.NetworkTransports"/> and <see cref="PhysicsWorlds"/> so a host configuring an engine
/// finds every backend choice in the same place.
/// </remarks>
public static class AudioSystems
{
    /// <summary>BRUTAL's FMOD binding, which is the engine's intended backend.</summary>
    public static Func<IAudioSystem> Fmod { get; } = static () => FmodAudioSystem.Create();

    /// <summary>
    /// A system that mixes nothing and reports itself unavailable.
    /// </summary>
    /// <remarks>
    /// The right choice for a host that has no sound device and does not want the audio thread to spend its life
    /// failing to open one — a dedicated server, or a test harness.
    /// </remarks>
    public static Func<IAudioSystem> None { get; } = static () => new NullAudioSystem();
}

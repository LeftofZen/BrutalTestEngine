namespace BrutalTestEngine.Audio;

/// <summary>
/// An audio system that mixes nothing, and says so.
/// </summary>
/// <remarks>
/// Reports <see cref="IsAvailable"/> as false so a caller can tell "silent on purpose" from "silent because the
/// device failed" — a distinction that matters when someone is trying to work out why a sound did not play.
/// </remarks>
public sealed class NullAudioSystem : IAudioSystem
{
    public string Name => "none";

    public bool IsAvailable => false;

    public long UpdateCount => 0;

    public int PlayingChannels => 0;

    public int RealChannels => 0;

    public void Update() { }

    public void SetMasterVolume(float volume) { }

    public void Dispose() { }
}

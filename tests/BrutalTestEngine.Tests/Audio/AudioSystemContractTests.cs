using BrutalTestEngine.Audio;

namespace BrutalTestEngine.Tests.Audio;

/// <summary>
/// Pins the audio contract with an in-memory system, so the seam is proven usable without FMOD and without an
/// audio device.
/// </summary>
[TestFixture]
public class AudioSystemContractTests
{
    private sealed class ScriptedAudio : IAudioSystem
    {
        public string Name => "scripted";

        public bool IsAvailable { get; set; } = true;

        public long UpdateCount { get; private set; }

        public int PlayingChannels { get; private set; } = 3;

        public int RealChannels { get; private set; } = 2;

        public float Volume { get; private set; } = 1.0f;

        public void Update()
        {
            if (!IsAvailable)
                return;

            UpdateCount++;
        }

        public void SetMasterVolume(float volume)
        {
            if (!IsAvailable)
                return;

            Volume = Math.Clamp(volume, 0.0f, 1.0f);
        }

        public void Dispose() { }
    }

    [Test]
    public void Updating_advances_the_mixer_and_reports_its_channels()
    {
        using var audio = new ScriptedAudio();

        audio.Update();
        audio.Update();

        Assert.Multiple(() =>
        {
            Assert.That(audio.UpdateCount, Is.EqualTo(2));
            Assert.That(audio.PlayingChannels, Is.EqualTo(3));
            Assert.That(audio.RealChannels, Is.EqualTo(2));
        });
    }

    [TestCase(-1.0f, 0.0f)]
    [TestCase(0.25f, 0.25f)]
    [TestCase(2.0f, 1.0f)]
    public void Volume_is_clamped_rather_than_passed_through(float requested, float expected)
    {
        using var audio = new ScriptedAudio();

        audio.SetMasterVolume(requested);

        Assert.That(audio.Volume, Is.EqualTo(expected));
    }

    [Test]
    public void An_unavailable_system_updates_never_and_accepts_no_volume()
    {
        using var audio = new ScriptedAudio { IsAvailable = false };

        audio.Update();
        audio.SetMasterVolume(0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(audio.UpdateCount, Is.Zero);
            Assert.That(audio.Volume, Is.EqualTo(1.0f));
        });
    }

    [Test]
    public void The_null_system_reports_itself_unavailable_and_mixes_nothing()
    {
        using var audio = new NullAudioSystem();

        audio.Update();

        Assert.Multiple(() =>
        {
            Assert.That(audio.IsAvailable, Is.False);
            Assert.That(audio.Name, Is.EqualTo("none"));
            Assert.That(audio.UpdateCount, Is.Zero);
            Assert.That(audio.PlayingChannels, Is.Zero);
        });
    }
}

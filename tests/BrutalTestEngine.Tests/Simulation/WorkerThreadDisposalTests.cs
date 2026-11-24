using BrutalTestEngine.Audio;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Physics;
using BrutalTestEngine.Simulation;

namespace BrutalTestEngine.Tests.Simulation;

/// <summary>
/// The physics and audio threads own native resources — a Jolt world and an FMOD device — so disposing the
/// thread has to dispose what it owns. These tests pin that, because a thread that stops without releasing its
/// backend leaks the device for the life of the process and nothing else in the engine would notice.
/// </summary>
[TestFixture]
public class WorkerThreadDisposalTests
{
    private sealed class TrackedWorld : IPhysicsWorld
    {
        public string Name => "tracked";

        public bool IsAvailable => true;

        public int BodyCount => 0;

        public long StepCount => 0;

        public double SimulatedSeconds => 0;

        public bool IsDisposed { get; private set; }

        public int AddSphere(float radius, (float X, float Y, float Z) position, bool isStatic) => 0;

        public (float X, float Y, float Z) GetPosition(int handle) => default;

        public void Step(float deltaSeconds, int collisionSteps) { }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class TrackedAudio : IAudioSystem
    {
        public string Name => "tracked";

        public bool IsAvailable => true;

        public long UpdateCount => 0;

        public int PlayingChannels => 0;

        public int RealChannels => 0;

        public bool IsDisposed { get; private set; }

        public void Update() { }

        public void SetMasterVolume(float volume) { }

        public void Dispose() => IsDisposed = true;
    }

    [Test]
    public void Disposing_the_physics_thread_disposes_the_world_it_owns()
    {
        using var bus = new CommandBus();
        var timers = new ThreadTimerRegistry();
        var world = new TrackedWorld();

        var thread = new PhysicsThread(bus, world, timers);
        thread.Dispose();

        Assert.That(world.IsDisposed, Is.True);
    }

    [Test]
    public void Disposing_the_audio_thread_disposes_the_system_it_owns()
    {
        using var bus = new CommandBus();
        var timers = new ThreadTimerRegistry();
        var audio = new TrackedAudio();

        var thread = new AudioThread(bus, audio, timers);
        thread.Dispose();

        Assert.That(audio.IsDisposed, Is.True);
    }

    [Test]
    public void A_faulting_world_raises_Faulted_and_stops_the_thread()
    {
        // A worker that faults stops its own loop. Without a subscriber the failure is silent, so this pins
        // both halves: the event fires with the exception, and the thread stops stepping afterwards.
        using var bus = new CommandBus();
        var timers = new ThreadTimerRegistry();
        var world = new FaultingWorld();

        using var faulted = new ManualResetEventSlim();
        Exception? reported = null;

        var thread = new PhysicsThread(bus, world, timers);
        thread.Faulted += exception =>
        {
            reported = exception;
            faulted.Set();
        };

        Assert.That(faulted.Wait(TimeSpan.FromSeconds(5)), Is.True, "Faulted was never raised");

        var stepsAtFault = world.StepCount;
        Thread.Sleep(100);

        Assert.Multiple(() =>
        {
            Assert.That(reported, Is.Not.Null);
            Assert.That(reported!.Message, Is.EqualTo("scripted failure"));
            Assert.That(world.StepCount, Is.EqualTo(stepsAtFault), "the thread kept stepping after faulting");
        });

        thread.Dispose();
    }

    private sealed class FaultingWorld : IPhysicsWorld
    {
        private long _steps;

        public string Name => "faulting";

        public bool IsAvailable => true;

        public int BodyCount => 0;

        public long StepCount => Interlocked.Read(ref _steps);

        public double SimulatedSeconds => 0;

        public int AddSphere(float radius, (float X, float Y, float Z) position, bool isStatic) => 0;

        public (float X, float Y, float Z) GetPosition(int handle) => default;

        public void Step(float deltaSeconds, int collisionSteps)
        {
            Interlocked.Increment(ref _steps);
            throw new InvalidOperationException("scripted failure");
        }

        public void Dispose() { }
    }

    [Test]
    public void Disposing_twice_is_harmless()
    {
        using var bus = new CommandBus();
        var timers = new ThreadTimerRegistry();
        var world = new TrackedWorld();
        var audio = new TrackedAudio();

        var physics = new PhysicsThread(bus, world, timers);
        var audioThread = new AudioThread(bus, audio, timers);

        physics.Dispose();
        physics.Dispose();
        audioThread.Dispose();
        audioThread.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(world.IsDisposed, Is.True);
            Assert.That(audio.IsDisposed, Is.True);
        });
    }
}

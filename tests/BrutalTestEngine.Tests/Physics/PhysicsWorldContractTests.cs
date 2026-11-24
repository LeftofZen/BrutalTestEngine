using BrutalTestEngine.Physics;

namespace BrutalTestEngine.Tests.Physics;

/// <summary>
/// Pins the physics contract with an in-memory world, so the seam is proven usable without a native physics
/// library and without the engine running.
/// </summary>
/// <remarks>
/// This is the point of the abstraction rather than a formality: these tests could not run at all if the engine
/// depended on Jolt directly, and they are the tests that will still compile when the backend changes.
/// </remarks>
[TestFixture]
public class PhysicsWorldContractTests
{
    private sealed class ScriptedWorld : IPhysicsWorld
    {
        private readonly Dictionary<int, (float X, float Y, float Z)> _bodies = [];
        private int _next;

        public string Name => "scripted";

        public bool IsAvailable { get; set; } = true;

        public int BodyCount => _bodies.Count;

        public long StepCount { get; private set; }

        public double SimulatedSeconds { get; private set; }

        public int Rejected { get; private set; }

        public int AddSphere(float radius, (float X, float Y, float Z) position, bool isStatic)
        {
            if (!IsAvailable || radius <= 0.0f)
            {
                Rejected++;
                return 0;
            }

            var handle = ++_next;
            _bodies[handle] = position;
            return handle;
        }

        public (float X, float Y, float Z) GetPosition(int handle) =>
            _bodies.TryGetValue(handle, out var position) ? position : default;

        public void Step(float deltaSeconds, int collisionSteps)
        {
            if (!IsAvailable || deltaSeconds <= 0.0f)
                return;

            StepCount++;
            SimulatedSeconds += deltaSeconds;

            // A crude integration, enough to prove a position changes through the seam.
            foreach (var handle in _bodies.Keys.ToArray())
            {
                var (x, y, z) = _bodies[handle];
                _bodies[handle] = (x, y - (9.81f * deltaSeconds), z);
            }
        }

        public void Dispose() => _bodies.Clear();
    }

    [Test]
    public void A_body_can_be_added_read_back_and_moved_by_stepping()
    {
        using var world = new ScriptedWorld();

        var handle = world.AddSphere(0.5f, (0.0f, 10.0f, 0.0f), isStatic: false);

        Assert.That(handle, Is.GreaterThan(0));
        Assert.That(world.BodyCount, Is.EqualTo(1));
        Assert.That(world.GetPosition(handle).Y, Is.EqualTo(10.0f));

        world.Step(0.5f, 1);

        Assert.Multiple(() =>
        {
            Assert.That(world.StepCount, Is.EqualTo(1));
            Assert.That(world.SimulatedSeconds, Is.EqualTo(0.5).Within(1e-6));
            Assert.That(world.GetPosition(handle).Y, Is.LessThan(10.0f));
        });
    }

    [TestCase(0.0f)]
    [TestCase(-1.0f)]
    public void A_body_without_volume_is_refused_rather_than_created(float radius)
    {
        using var world = new ScriptedWorld();

        Assert.Multiple(() =>
        {
            Assert.That(world.AddSphere(radius, default, isStatic: false), Is.Zero);
            Assert.That(world.BodyCount, Is.Zero);
            Assert.That(world.Rejected, Is.EqualTo(1));
        });
    }

    [Test]
    public void An_unavailable_world_accepts_nothing_and_steps_never()
    {
        using var world = new ScriptedWorld { IsAvailable = false };

        world.Step(1.0f / 60.0f, 1);

        Assert.Multiple(() =>
        {
            Assert.That(world.AddSphere(1.0f, default, isStatic: false), Is.Zero);
            Assert.That(world.StepCount, Is.Zero);
            Assert.That(world.BodyCount, Is.Zero);
        });
    }

    [Test]
    public void An_unknown_handle_reads_back_as_the_default_position()
    {
        using var world = new ScriptedWorld();

        Assert.That(world.GetPosition(4321), Is.EqualTo(default((float, float, float))));
    }

    [Test]
    public void The_null_world_reports_itself_unavailable_and_does_nothing()
    {
        using var world = new NullPhysicsWorld();

        Assert.Multiple(() =>
        {
            Assert.That(world.IsAvailable, Is.False);
            Assert.That(world.Name, Is.EqualTo("none"));
            Assert.That(world.AddSphere(1.0f, (1.0f, 2.0f, 3.0f), isStatic: false), Is.Zero);
        });

        world.Step(1.0f / 60.0f, 1);

        Assert.That(world.StepCount, Is.Zero);
    }

    [Test]
    public void The_jolt_backend_never_throws_when_it_cannot_initialize()
    {
        // The native library may or may not be present on the machine running this, and both outcomes are
        // correct: what is pinned is that a backend that cannot start degrades to "unavailable" rather than
        // throwing out of the composition root.
        using var world = JoltPhysicsWorld.Create();

        Assert.Multiple(() =>
        {
            Assert.That(world.Name, Is.EqualTo("jolt"));
            Assert.That(world.AddSphere(0.0f, default, isStatic: false), Is.Zero);
        });
    }
}

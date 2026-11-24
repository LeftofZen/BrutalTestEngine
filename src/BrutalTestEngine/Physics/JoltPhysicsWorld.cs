using Brutal.JoltApi;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalTestEngine.Logging;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Physics;

/// <summary>
/// The engine's Jolt-backed physics world, built on BRUTAL's <see cref="PhysicsSystem"/> binding.
/// </summary>
/// <remarks>
/// <para>
/// Jolt is what BRUTAL ships and what the engine is meant to use, so this is the intended backend rather than
/// a placeholder. It is written against the real binding throughout: the object layers, broad-phase mapping
/// and temp allocator are all set up the way Jolt requires, because getting those wrong is not a details
/// problem — it is the difference between a world that steps and one that does not.
/// </para>
/// <para>
/// <b>Failure is expected and handled.</b> Initialization can fail in ways this class cannot fix: the native
/// <c>jolt.dll</c> may be missing, the package and the native binary may be different versions, or the binding
/// itself may fault. All of those are treated as "physics is unavailable" and logged, because a game that
/// refuses to start because its physics backend is broken is worse than a game that starts without physics.
/// </para>
/// <para>
/// <b>Two object layers, not one.</b> Static geometry and dynamic bodies are separated so the broad phase can
/// skip static-static pairs entirely. That is the standard Jolt setup and it costs nothing to do properly the
/// first time.
/// </para>
/// </remarks>
public sealed class JoltPhysicsWorld : IPhysicsWorld
{
    private const ushort StaticLayer = 0;
    private const ushort DynamicLayer = 1;
    private const uint MaxBodies = 4096;
    private const uint MaxBodyPairs = 8192;
    private const uint MaxContactConstraints = 8192;

    /// <summary>Jolt's temp allocator workspace. Generous, because running out of it faults rather than throws.</summary>
    private const int TempAllocatorBytes = 16 * 1024 * 1024;

    private static readonly LogCategory Log = EngineLog.Categories.Engine;

    private readonly Dictionary<int, uint> _bodies = [];
    private readonly object _gate = new();

    private PhysicsSystem _system;
    private TempAllocatorImplWithMallocFallback _tempAllocator;
    private BroadPhaseLayerInterfaceTable _broadPhaseLayers;
    private ObjectLayerPairFilterTable _objectLayerPairs;
    private ObjectVsBroadPhaseLayerFilterTable _objectVsBroadPhase;

    private long _steps;
    private double _simulatedSeconds;
    private int _nextHandle = 1;
    private bool _disposed;

    private JoltPhysicsWorld(bool available, string? unavailableReason)
    {
        IsAvailable = available;

        if (unavailableReason is not null && Log.IsEnabled(LogLevel.Warning))
            Log.Warning($"physics unavailable: {unavailableReason}");
    }

    public string Name => "jolt";

    public bool IsAvailable { get; private set; }

    public int BodyCount
    {
        get
        {
            lock (_gate)
                return _bodies.Count;
        }
    }

    public long StepCount => Interlocked.Read(ref _steps);

    public double SimulatedSeconds => Volatile.Read(ref _simulatedSeconds);

    /// <summary>
    /// Creates a world, or an unavailable one when Jolt cannot be initialized.
    /// </summary>
    /// <remarks>
    /// Nothing here throws. A native library that is absent, or a binding whose entry points do not match the
    /// binary on disk, fails at different moments — load time, allocation time, first save of the struct — and
    /// all of them have to land on the same answer.
    /// </remarks>
    public static JoltPhysicsWorld Create()
    {
        var world = new JoltPhysicsWorld(false, null);

        try
        {
            Jolt.RegisterDefaultAllocator();

            if (!Jolt.VerifyJoltVersionID())
                return new JoltPhysicsWorld(false, "the jolt native library reports a different version");

            world.Initialize();
            return world;
        }
        catch (Exception exception)
        {
            // DllNotFoundException, EntryPointNotFoundException and BadImageFormatException all mean the same
            // thing here: this build cannot talk to that binary.
            return new JoltPhysicsWorld(false, exception.Message);
        }
    }

    private void Initialize()
    {
        _broadPhaseLayers = BroadPhaseLayerInterfaceTable.Create(2, 2);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(StaticLayer, 0);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(DynamicLayer, 1);

        _objectLayerPairs = ObjectLayerPairFilterTable.Create(2);
        _objectLayerPairs.EnableCollision(StaticLayer, StaticLayer);
        _objectLayerPairs.EnableCollision(StaticLayer, DynamicLayer);
        _objectLayerPairs.EnableCollision(DynamicLayer, DynamicLayer);

        _objectVsBroadPhase = ObjectVsBroadPhaseLayerFilterTable.Create(
            _broadPhaseLayers,
            2,
            _objectLayerPairs,
            2
        );

        _system = PhysicsSystem.Create();
        _system.Init(
            MaxBodies,
            0,
            MaxBodyPairs,
            MaxContactConstraints,
            _broadPhaseLayers,
            _objectVsBroadPhase,
            _objectLayerPairs
        );

        // The allocator is created after the system so a failure here leaves nothing half-built to clean up.
        _tempAllocator = TempAllocatorImplWithMallocFallback.Create(TempAllocatorBytes);

        Gravity = (0.0f, -9.81f, 0.0f);
        _system.SetGravity(new float3(0.0f, -9.81f, 0.0f));
        IsAvailable = true;

        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.Info(
                $"physics world ready: jolt, {MaxBodies} body slots, gravity "
                    + $"{Gravity.X}, {Gravity.Y}, {Gravity.Z}"
            );
        }
    }

    /// <summary>Gravity applied to dynamic bodies, in metres per second squared.</summary>
    public (float X, float Y, float Z) Gravity { get; private set; }

    public void SetGravity(float x, float y, float z)
    {
        if (!IsAvailable)
            return;

        Gravity = (x, y, z);
        _system.SetGravity(new float3(x, y, z));
    }

    public int AddSphere(float radius, (float X, float Y, float Z) position, bool isStatic)
    {
        if (!IsAvailable || radius <= 0.0f)
            return 0;

        try
        {
            var shape = SphereShapeSettings.Create(radius, PhysicsMaterial.GetDefault()).CreateShape();
            if (shape.IsNull())
                return 0;

            var settings = BodyCreationSettings.Create();
            settings.SetShape(shape);
            settings.SetPosition(new float3(position.X, position.Y, position.Z));
            settings.SetRotation(new floatQuat(0.0f, 0.0f, 0.0f, 1.0f));
            settings.SetMotionType(isStatic ? MotionType.Static : MotionType.Dynamic);
            settings.SetObjectLayer(isStatic ? StaticLayer : DynamicLayer);

            var id = _system
                .GetBodyInterface()
                .CreateAndAddBody(settings, isStatic ? Activation.DontActivate : Activation.Activate);

            if (id == 0)
                return 0;

            lock (_gate)
            {
                var handle = _nextHandle++;
                _bodies[handle] = id;
                return handle;
            }
        }
        catch (Exception exception)
        {
            if (Log.IsEnabled(LogLevel.Error))
                Log.Error(exception, $"physics refused a body: {exception.Message}");

            return 0;
        }
    }

    public (float X, float Y, float Z) GetPosition(int handle)
    {
        if (!IsAvailable)
            return default;

        uint id;
        lock (_gate)
        {
            if (!_bodies.TryGetValue(handle, out id))
                return default;
        }

        var position = _system.GetBodyInterface().GetPosition(id);
        return (position.X, position.Y, position.Z);
    }

    public void Step(float deltaSeconds, int collisionSteps)
    {
        if (!IsAvailable || deltaSeconds <= 0.0f)
            return;

        try
        {
            // A default JobSystem means "run on the calling thread". That is the right default here: the
            // simulation already owns this thread and a physics job system would add a second pool competing
            // with the engine's own threads for the same cores.
            _system.Update(deltaSeconds, Math.Max(collisionSteps, 1), _tempAllocator, default);

            Interlocked.Increment(ref _steps);
            lock (_gate)
                _simulatedSeconds += deltaSeconds;
        }
        catch (Exception exception)
        {
            // A stepping failure is not recoverable per frame: the world's state is unknown afterwards, so it is
            // reported once and stepping stops rather than producing nonsense positions for the rest of the run.
            if (Log.IsEnabled(LogLevel.Error))
                Log.Error(exception, $"physics step failed: {exception.Message}");

            MarkBroken();
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
            _tempAllocator.Dispose();
            _broadPhaseLayers.Dispose();
            _objectLayerPairs.Dispose();
            _objectVsBroadPhase.Dispose();
        }
        catch (Exception exception)
        {
            if (Log.IsEnabled(LogLevel.Warning))
                Log.Warning($"physics teardown raised {exception.GetType().Name}: {exception.Message}");
        }

        lock (_gate)
            _bodies.Clear();
    }

    private void MarkBroken()
    {
        IsAvailable = false;

        lock (_gate)
            _bodies.Clear();
    }
}

namespace BrutalTestEngine.Physics;

/// <summary>
/// The seam between the engine and whatever simulates rigid bodies.
/// </summary>
/// <remarks>
/// <para>
/// Physics is a separate service from the simulation for one reason: it has to be stepped at the simulation's
/// fixed timestep, on the simulation thread, and a mesh-sharded world will eventually step several regions
/// independently. Keeping it behind this interface means the sharding decision is a policy on top of a stable
/// contract rather than a rewrite of every call site.
/// </para>
/// <para>
/// The surface is deliberately the smallest thing that is still useful: create a body, read its transform, step
/// the world, and ask how much work it is doing. Constraints, characters, vehicles and queries are all real
/// backends features, but none of them are needed to prove the seam, and every one of them would be a guess
/// about a game that has not been specified yet.
/// </para>
/// <para>
/// <b>Threading.</b> A world belongs to one thread — the simulation thread — and needs no synchronisation.
/// The counters are safe to read from any thread, which is what lets the debug panel report them.
/// </para>
/// </remarks>
public interface IPhysicsWorld : IDisposable
{
    /// <summary>A name for the backend, used in logs and in the debug panel.</summary>
    string Name { get; }

    /// <summary>True when the backend initialized and can be stepped.</summary>
    bool IsAvailable { get; }

    /// <summary>Bodies currently in the world.</summary>
    int BodyCount { get; }

    /// <summary>Fixed steps performed since the world was created.</summary>
    long StepCount { get; }

    /// <summary>Simulated seconds advanced since the world was created.</summary>
    double SimulatedSeconds { get; }

    /// <summary>
    /// Adds a sphere and returns its handle.
    /// </summary>
    /// <param name="radius">The sphere radius, in metres.</param>
    /// <param name="position">Where to place it.</param>
    /// <param name="isStatic">True for immovable geometry, false for a body the solver moves.</param>
    /// <returns>A positive handle, or zero when the backend is unavailable or refused the body.</returns>
    int AddSphere(float radius, (float X, float Y, float Z) position, bool isStatic);

    /// <summary>
    /// Reads a body's position. Returns <c>default</c> for an unknown handle.
    /// </summary>
    (float X, float Y, float Z) GetPosition(int handle);

    /// <summary>
    /// Advances the world by <paramref name="deltaSeconds"/> using the given number of collision substeps.
    /// </summary>
    void Step(float deltaSeconds, int collisionSteps);
}

using BrutalTestEngine.Physics;

namespace BrutalTestEngine;

/// <summary>
/// Chooses which physics world the engine runs on.
/// </summary>
/// <remarks>
/// The one place a concrete physics library is named. Kept beside
/// <see cref="Networking.NetworkTransports"/> rather than inside the physics folder so a host configuring an
/// engine can find every backend choice in the same place.
/// </remarks>
public static class PhysicsWorlds
{
    /// <summary>
    /// BRUTAL's Jolt binding, which is the engine's intended backend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the default yet, and the reason is a defect outside this engine: BRUTAL's
    /// <c>PhysicsSystem.Update</c> raises an access violation as soon as the world contains a body, in every
    /// configuration tried — with and without a job system, with table and mask filters, with mutexes at zero
    /// and non-zero, on package versions 2026.10.4 and 2026.10.2. Creation, <c>Init</c> and body creation all
    /// succeed; it is specifically the step that faults.
    /// </para>
    /// <para>
    /// A managed <c>try</c>/<c>catch</c> cannot contain that, because it terminates the process rather than
    /// raising. Defaulting to this backend would therefore make the engine crash the moment anything added a
    /// body, so it is opt-in until the binding is fixed. Everything above the seam is already written against
    /// it, so flipping the default is a one-line change once a working version ships.
    /// </para>
    /// </remarks>
    public static Func<IPhysicsWorld> Jolt { get; } = static () => JoltPhysicsWorld.Create();

    /// <summary>
    /// A world that does nothing and reports itself unavailable.
    /// </summary>
    /// <remarks>
    /// Not a stub for tests — tests get their own fake — but the honest option for a host that wants a headless
    /// server: a dedicated server simulating thousands of bodies it never renders is a real configuration, and
    /// "physics off" should be a supported one rather than a build-time omission.
    /// </remarks>
    public static Func<IPhysicsWorld> None { get; } = static () => new NullPhysicsWorld();
}

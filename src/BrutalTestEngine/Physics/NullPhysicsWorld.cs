namespace BrutalTestEngine.Physics;

/// <summary>
/// A physics world that does nothing, and says so.
/// </summary>
/// <remarks>
/// Useful for a headless server that has no bodies worth simulating, and for a client whose graphics stack is
/// present but whose physics backend is not. It reports <see cref="IsAvailable"/> as false so every caller can
/// tell the difference between "nothing happened" and "nothing is configured" — a distinction that is invisible
/// until someone is staring at a body that will not fall.
/// </remarks>
public sealed class NullPhysicsWorld : IPhysicsWorld
{
    public string Name => "none";

    public bool IsAvailable => false;

    public int BodyCount => 0;

    public long StepCount => 0;

    public double SimulatedSeconds => 0.0;

    public int AddSphere(float radius, (float X, float Y, float Z) position, bool isStatic) => 0;

    public (float X, float Y, float Z) GetPosition(int handle) => default;

    public void Step(float deltaSeconds, int collisionSteps) { }

    public void Dispose() { }
}

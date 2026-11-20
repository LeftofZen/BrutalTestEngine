using Brutal.Numerics;
using Brutal.VulkanApi;

namespace BrutalTestEngine.Engine;

/// <summary>
/// A single vertex: a 2D position in clip space and an RGB colour.
/// </summary>
public struct Vertex(float2 position, float3 color)
{
    public float2 Position = position;
    public float3 Color = color;
}
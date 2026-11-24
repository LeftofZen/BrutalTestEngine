using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace BrutalTestEngine.Compute;

/// <summary>
/// A compiled compute shader: GLSL source, compiled to SPIR-V at startup and wrapped in a shader module.
/// </summary>
/// <remarks>
/// Compilation happens once, at construction, rather than per dispatch. ShaderC is a full compiler and
/// compiling a shader every frame would dominate the frame time; the module is immutable once built, so
/// there is nothing to gain from deferring it.
/// </remarks>
public sealed unsafe class ComputeShader : IDisposable
{
    private readonly DeviceEx _device;
    private bool _disposed;

    /// <summary>
    /// Compiles <paramref name="glsl"/> and creates a shader module for it.
    /// </summary>
    /// <param name="device">The device to create the module on.</param>
    /// <param name="glsl">UTF-8 GLSL source.</param>
    /// <param name="name">The shader's name, used in compiler diagnostics.</param>
    /// <param name="entryPoint">The entry point function name. Defaults to <c>main</c>.</param>
    public ComputeShader(DeviceEx device, ReadOnlySpan<byte> glsl, string name, string entryPoint = "main")
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        Name = name;
        EntryPoint = entryPoint;
        _device = device;

        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var entryBytes = System.Text.Encoding.UTF8.GetBytes(entryPoint);

        using var spirv = compiler.Compile(
            glsl,
            ShaderKind.ComputeShader,
            nameBytes,
            entryBytes,
            options
        );

        Module = ShaderModuleExtensions.FromSpirv(
            device,
            new ReadOnlySpan<byte>(spirv.GetBytes(), (int)spirv.GetLength()),
            null
        );
    }

    /// <summary>The compiled shader module.</summary>
    public VkShaderModule Module { get; }

    /// <summary>The shader's name.</summary>
    public string Name { get; }

    /// <summary>The entry point function name.</summary>
    public string EntryPoint { get; }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _device.DestroyShaderModule(Module, null);
    }
}

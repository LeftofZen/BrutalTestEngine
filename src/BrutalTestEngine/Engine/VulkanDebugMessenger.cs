using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Logging;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalTestEngine.Logging;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Routes Vulkan validation and debug-utils messages into the engine's log, and owns the messenger's
/// lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The callback is unmanaged: Vulkan calls it from whichever thread triggered the message, which may be the
/// render thread, a driver worker, or the loader itself. It therefore does the one safe thing available to it
/// — formats the message into a <see cref="LogCategory"/> call, which hands it to the asynchronous sink — and
/// returns immediately. It never allocates shared state, never blocks and never touches engine objects,
/// because it has no safe way to know what is alive at that moment.
/// </para>
/// <para>
/// The category reference is held in a static field rather than reached through <c>userData</c>. A static is
/// simpler and cannot dangle; the process has exactly one log pipeline, so there is nothing to disambiguate.
/// </para>
/// </remarks>
internal static unsafe class VulkanDebugMessenger
{
    private static readonly LogCategory Log = EngineLog.Categories.Vulkan;

    /// <summary>
    /// Installs the messenger on <paramref name="instance"/>. Returns a null handle when the instance was not
    /// created with <c>VK_EXT_debug_utils</c>, which is the case when tracing is off.
    /// </summary>
    public static VkDebugUtilsMessengerEXT Install(Instance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!instance.IsExtensionEnabled("VK_EXT_debug_utils"))
            return default;

        var callback = new PfnVkDebugUtilsMessengerCallbackEXT(&OnMessage);
        var createInfo = VulkanHelpers.BuildDefaultDebugMessengerCreateInfo(callback);

        // BuildDefaultDebugMessengerCreateInfo subscribes to every severity including verbose, which turns a
        // validation run into thousands of lines of loader chatter. Errors and warnings are what a developer
        // acts on, so the verbose and info severities are dropped here.
        createInfo.MessageSeverity =
            VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT
            | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT;

        var messenger = instance.CreateDebugUtilsMessengerEXT(createInfo, default);

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info("Vulkan debug messenger installed");

        return messenger;
    }

    /// <summary>Destroys a messenger installed by <see cref="Install"/>.</summary>
    public static void Uninstall(Instance instance, VkDebugUtilsMessengerEXT messenger)
    {
        if (instance is null || messenger.IsNull())
            return;

        instance.DestroyDebugUtilsMessengerEXT(messenger, default);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 OnMessage(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT types,
        VkDebugUtilsMessengerCallbackDataEXT* data,
        void* userData
    )
    {
        try
        {
            var message = data is null ? "<null>" : Marshal.PtrToStringUTF8((nint)data->Message) ?? "<null>";

            // The type flags separate a genuine validation error from a performance hint, and the two need
            // different levels: a validation error is a bug, a performance warning is advice.
            var isPerformance = (types & VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT) != 0;

            if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0)
            {
                if (Log.IsEnabled(LogLevel.Error))
                    Log.Error($"[vulkan] {message}");
            }
            else if (isPerformance ? Log.IsEnabled(LogLevel.Information) : Log.IsEnabled(LogLevel.Warning))
            {
                if (isPerformance)
                    Log.Info($"[vulkan] {message}");
                else
                    Log.Warning($"[vulkan] {message}");
            }
        }
        catch
        {
            // A callback that threw would unwind into the Vulkan loader, which is not a place where an
            // exception can be handled. Losing one diagnostic line is the only safe failure here.
        }

        return false;
    }
}

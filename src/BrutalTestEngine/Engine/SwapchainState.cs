using Brutal.VulkanApi;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Everything that depends on the swapchain's size or format, rebuilt whenever the window resizes.
/// </summary>
internal sealed unsafe class SwapchainState
{
    public required VkSwapchainKHR Swapchain { get; init; }
    public required VkImageView[] ImageViews { get; init; }
    public required VkFramebuffer[] Framebuffers { get; init; }
    public required VkExtent2D Extent { get; init; }
    public required VkFormat Format { get; init; }
}

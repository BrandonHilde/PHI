# Vulkan 1.4

The GPU as its drivers give it: drawing and computing with Vulkan.
<https://docs.vulkan.org> has the specification and a tutorial; the functions are the ones
here.

```phi
use vendor.vulkan;

phi.Cards:Program
{
	VkApplicationInfo app;
	app.sType is Vk.STRUCTURE_TYPE_APPLICATION_INFO;
	app.apiVersion is Vk.API_VERSION_1_0;
	VkInstanceCreateInfo make;
	make.sType is Vk.STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
	make.pApplicationInfo is addr app;
	ptr<VkInstance_T> instance: 0;
	if (Vk.CreateInstance: addr make 0 addr instance) is not Vk.SUCCESS
		log 'no Vulkan here\n';
		exit 1;
	;;
	ptr<VkPhysicalDevice_T> cards[8];
	u32 count: 8;
	int r: (Vk.EnumeratePhysicalDevices: instance addr count addr cards);
	while int i: 0; i < count as int; i++;
		VkPhysicalDeviceProperties about;
		call Vk.GetPhysicalDeviceProperties: cards:i addr about;
		log (addr about.deviceName) '\n';
	;;
	call Vk.DestroyInstance: instance 0;
	exit 0;
}
```

- A function is `Vk.Name` for C's `vkName` (`Vk.CreateInstance`, `Vk.CreateDevice`), and a
  constant `Vk.NAME` for `VK_NAME` (`Vk.SUCCESS`, `Vk.STRUCTURE_TYPE_APPLICATION_INFO`,
  `Vk.FORMAT_B8G8R8A8_SRGB`). Structs keep C's whole names (`VkInstanceCreateInfo`).
- **Handles.** The ones Vulkan gives as addresses are pointers: `ptr<VkInstance_T>`,
  `ptr<VkPhysicalDevice_T>`, `ptr<VkDevice_T>`, `ptr<VkQueue_T>`, `ptr<VkCommandBuffer_T>`.
  Every other (a `VkBuffer`, a `VkImage`, a `VkSurfaceKHR`, a `VkSwapchainKHR`) is a `u64`,
  on every system.
  `Vk.NULL_HANDLE` (0) is no handle, of either kind.
- **A union** is a block of its size, read and written through a pointer to the form it is.
  The first one every program meets is the colour a frame is cleared to
  (`clearColor.color.float32` in C):

  ```phi
  VkClearValue colour;
  ptr<f32> rgba: addr colour as ptr<f32>;
  rgba:0 is 0.25;
  rgba:1 is 0.5;
  rgba:2 is 0.75;
  rgba:3 is 1.0;
  ```
- **A struct starts as zeros,** which Vulkan relies on: `pNext` and every field a program
  doesn't set are 0 without being told.
- **A window** comes from SDL3, which is in the vendor folder too:
  `SDL.Vulkan_GetInstanceExtensions` says which extensions its window needs, and
  `SDL.Vulkan_CreateSurface` makes the surface.
  [samples/vulkan/surface.phi](../../samples/vulkan/surface.phi) does both.
- A name Vulkan has as text (`VK_KHR_SWAPCHAIN_EXTENSION_NAME`) is written as its text:
  `'VK_KHR_swapchain'`.
- **On a Mac,** Vulkan is MoltenVK 1.4.2, which is Vulkan over Apple's Metal. It comes with
  PHI and a build puts `libMoltenVK.dylib` beside the program, so a Mac needs nothing
  installed. MoltenVK writes a page about itself whenever an instance is made unless it's
  told how much to say: the module tells it errors only, and `MVK_CONFIG_LOG_LEVEL=3` in the
  environment has it say everything.
- Left out ([left-out.txt](left-out.txt)): a few ray-tracing structs C packs into bits, the
  constants past 32 bits (`VK_WHOLE_SIZE`, and `UINT64_MAX` for a wait with no end: put
  the value in a `u64` of your own, `u64 forever: 0xFFFFFFFFFFFFFFFF;`), and the 64-bit flags of
  the newer calls whose names end in `2` (`VK_PIPELINE_STAGE_2_...`), which C has as
  variables: give their numbers. The headers for one window system alone
  (`vulkan_win32.h` and the like) aren't read; SDL3 does that part.

On Windows and Linux the library is the system's, and nothing is shipped for it: it comes
with the graphics driver (`vulkan-1.dll`, `libvulkan.so.1`). On a Mac it's MoltenVK's own
build, unchanged (Apache-2.0: [LICENSE-MoltenVK](LICENSE-MoltenVK)). The declarations are
written from Khronos's headers (Apache-2.0 or MIT: [LICENSE](LICENSE)).

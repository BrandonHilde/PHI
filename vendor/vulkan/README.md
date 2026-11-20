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
- **A window** comes from SDL3, which is in the vendor folder too:
  `SDL.Vulkan_GetInstanceExtensions` says which extensions its window needs, and
  `SDL.Vulkan_CreateSurface` makes the surface.
  [samples/vulkan/surface.phi](../../samples/vulkan/surface.phi) does both.
- A name Vulkan has as text (`VK_KHR_SWAPCHAIN_EXTENSION_NAME`) is written as its text:
  `'VK_KHR_swapchain'`.
- **On a Mac,** Vulkan is MoltenVK over Metal: an instance asks for
  `'VK_KHR_portability_enumeration'` and sets
  `Vk.INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR`
  ([tests/vendor/vulkan_program.phi](../../tests/vendor/vulkan_program.phi) shows how).
- Left out ([left-out.txt](left-out.txt)): a few ray-tracing structs C packs into bits, the
  constants past 32 bits (`VK_WHOLE_SIZE`: put its value in a `u64` of your own), and the 64-bit flags of
  the newer calls whose names end in `2` (`VK_PIPELINE_STAGE_2_...`), which C has as
  variables: give their numbers. The headers for one window system alone
  (`vulkan_win32.h` and the like) aren't read; SDL3 does that part.

Nothing is shipped for it: the library is the system's. It comes with the graphics driver
on Windows (`vulkan-1.dll`) and Linux (`libvulkan.so.1`), and on a Mac with MoltenVK
(`libvulkan.1.dylib`: the Vulkan SDK, or `brew install molten-vk vulkan-loader`). The
declarations are written from Khronos's headers (Apache-2.0 or MIT: [LICENSE](LICENSE)).

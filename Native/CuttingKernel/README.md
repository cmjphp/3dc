# CuttingKernel

Native C/C++ cutting core used by Unity through `Assets/Scripts/Cutting/SdfNativePlugin.cs`.

- `src/cutting_kernel.cpp` owns the workpiece SDF, initial blank SDF shapes, selected cutter mesh upload, swept cutter subtraction, SDF readback, and connectivity entry points.
- `build_macos.sh` builds `Assets/Plugins/cutting_kernel_v43.dylib`.
- Unity C# is only the UI/rendering/interaction bridge. It calls `sdf_cut_selected_cutter()` and reads back native SDF data; it does not perform concrete cutting.
- GPU acceleration must be added behind this native ABI as a C/C++ compute backend. The current implementation reports the CPU backend as active; Unity-side ComputeShaders are limited to display-buffer synchronization and rendering.

Initial blank shapes currently include box, cylinder, tube, half-tube, and imported triangle-mesh stocks inside the user-defined envelope. Imported mesh blanks are rejected if any local axis exceeds 1000 mm or if the mesh exceeds the current stock envelope.

The cutter mesh path keeps the uploaded triangle mesh as the authoritative fallback. OpenVDB level sets are used when available, but cutting, cut-history sampling, and measurement sampling still run against the triangle mesh if OpenVDB is unavailable or mesh conversion fails.

Surface and thickness measurement entry points live in the native ABI (`sdf_measure_surface_ray` and `sdf_measure_thickness_ray`). Unity passes probe rays and display/UI parameters; the native SDF resolves the machined surface to a tolerance no worse than 0.001 mm.

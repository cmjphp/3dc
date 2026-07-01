# Project Goals

This project is a real-time CNC machining simulation and measurement tool.

- Cutting must stay smooth enough for interactive machine-operation workflows.
- Users must be able to cut smoothly with many simulated CNC cutter/tool geometries.
- Cutting must use the selected tool geometry and pose, including tool depth chosen by the user through interaction.
- The native C/C++ core owns cutting and high-precision SDF/material state.
- Detached/island material detection and removal are native C/C++ material-state responsibilities; Unity C# may only request/observe native cleanup and refresh rendering, and must not implement alternate island cleanup or visual-only detached-material removal.
- Future GPU acceleration must be implemented as a native C/C++ compute backend that preserves native ownership of material/SDF truth; Unity C# ComputeShaders may be used for rendering/display buffer synchronization only, not as an alternate cutting or material-state implementation.
- Unity C# owns UI, scene interaction, rendering synchronization, measurement entry points, and parameter transfer.
- C# must not provide alternate cutting implementations that hide missing native behavior.
- The stock/workpiece blank is user-defined, with each axis allowed up to 1000 mm, so the maximum stock envelope is 1000 x 1000 x 1000 mm.
- Initial workpiece shapes must include at least box, cylinder, tube, half-tube, and third-party imported 3D model blanks, without exceeding the user-defined stock envelope.
- Third-party imported initial workpiece models must be rejected if any local axis exceeds 1000 mm; do not silently scale, crop, clamp, or replace them with a simpler fallback.
- Performance work must not substitute a forced fixed cutting window for the user's stock dimensions.
- The machined workpiece must remain measurable after cutting.
- Measurement precision must not be worse than 0.001 mm.

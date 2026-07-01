# Repository Instructions

Before starting any task in this repository, read `PROJECT_GOALS.md` first and keep its constraints active for the whole task.

The non-negotiable project purpose is:

- Build a real-time CNC machining simulation that can smoothly cut across many machine-processing scenarios.
- Cutting depth is decided by user interaction and must not be hidden, fixed, capped, or bypassed by implementation shortcuts.
- The selected tool geometry, pose, and direction must drive the actual cutting result.
- C/C++ native code owns the cutting core and high-precision material/SDF state.
- Unity C# owns UI, rendering/display synchronization, interaction, measurement entry points, and parameter transfer only.
- C# must not contain fallback or alternate concrete cutting logic that masks missing native behavior.
- The machined workpiece must remain measurable after cutting.
- Measurement precision must not be worse than 0.001 mm.
- The stock/workpiece blank is user-defined, with each axis allowed up to 1000 mm, so the maximum stock envelope is 1000 x 1000 x 1000 mm. Do not replace the user-defined stock with a forced fixed cutting window.

When performance work is needed, optimize native data structures, synchronization, and rendering flow without reducing machining truthfulness or measurement precision.

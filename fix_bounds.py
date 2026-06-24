import sys

code = open('/Users/chenmeijun/Documents/3dc/3dc/Assets/Plugins/sdf_island_remover.zig').read()

new_code = code.replace("""                // +x neighbor
                if (x + 1 < w and cellIsSolid(x + 1, y, z)) {
                    if (g_state.sdf[sampleIdx(x + 2, y + 1, z + 1)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 2, y + 2, z + 1)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 2, y + 1, z + 2)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0) {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x + 1, y, z)));
                    }
                }
                // +y neighbor
                if (y + 1 < h and cellIsSolid(x, y + 1, z)) {
                    if (g_state.sdf[sampleIdx(x + 1, y + 2, z + 1)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 2, y + 2, z + 1)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 1, y + 2, z + 2)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0) {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x, y + 1, z)));
                    }
                }
                // +z neighbor
                if (z + 1 < d and cellIsSolid(x, y, z + 1)) {
                    if (g_state.sdf[sampleIdx(x + 1, y + 1, z + 2)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 2, y + 1, z + 2)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 1, y + 2, z + 2)] <= 0.0 or
                        g_state.sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0) {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x, y, z + 1)));
                    }
                }""", """                // +x neighbor
                if (x + 1 < w and cellIsSolid(x + 1, y, z)) {
                    if (x + 2 < w and y + 2 < h and z + 2 < d) {
                        if (g_state.sdf[sampleIdx(x + 2, y + 1, z + 1)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 2, y + 2, z + 1)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 2, y + 1, z + 2)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0) {
                            ufUnion(@intCast(idx), @intCast(cellIdx(x + 1, y, z)));
                        }
                    } else {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x + 1, y, z)));
                    }
                }
                // +y neighbor
                if (y + 1 < h and cellIsSolid(x, y + 1, z)) {
                    if (x + 2 < w and y + 2 < h and z + 2 < d) {
                        if (g_state.sdf[sampleIdx(x + 1, y + 2, z + 1)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 2, y + 2, z + 1)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 1, y + 2, z + 2)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0) {
                            ufUnion(@intCast(idx), @intCast(cellIdx(x, y + 1, z)));
                        }
                    } else {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x, y + 1, z)));
                    }
                }
                // +z neighbor
                if (z + 1 < d and cellIsSolid(x, y, z + 1)) {
                    if (x + 2 < w and y + 2 < h and z + 2 < d) {
                        if (g_state.sdf[sampleIdx(x + 1, y + 1, z + 2)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 2, y + 1, z + 2)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 1, y + 2, z + 2)] <= 0.0 or
                            g_state.sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0) {
                            ufUnion(@intCast(idx), @intCast(cellIdx(x, y, z + 1)));
                        }
                    } else {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x, y, z + 1)));
                    }
                }""")

open('/Users/chenmeijun/Documents/3dc/3dc/Assets/Plugins/sdf_island_remover.zig', 'w').write(new_code)

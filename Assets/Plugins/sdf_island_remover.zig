// sdf_island_remover.zig
// Native plugin for Unity CNC cutting simulation.
// Maintains a CPU-side SDF grid, applies capsule/profile-cutter cuts,
// and runs connected-component (island) analysis on a background thread.
//
// Build:
//   zig build-lib sdf_island_remover.zig -dynamic -OReleaseFast \
//       -target aarch64-macos -lc \
//       -femit-bin=sdf_island_remover.dylib

const std = @import("std");
const atomic = std.atomic;
const assert = std.debug.assert;
const c = @cImport({
    @cInclude("unistd.h");
    @cInclude("stdio.h");
});

fn log(msg: [*c]const u8) void {
    _ = c.printf("%s", msg);
}

// ============================================================================
// Vec3 helper
// ============================================================================

const Vec3 = struct {
    x: f32,
    y: f32,
    z: f32,

    fn init(x: f32, y: f32, z: f32) Vec3 {
        return .{ .x = x, .y = y, .z = z };
    }

    fn sub(a: Vec3, b: Vec3) Vec3 {
        return .{ .x = a.x - b.x, .y = a.y - b.y, .z = a.z - b.z };
    }

    fn add(a: Vec3, b: Vec3) Vec3 {
        return .{ .x = a.x + b.x, .y = a.y + b.y, .z = a.z + b.z };
    }

    fn scale(a: Vec3, s: f32) Vec3 {
        return .{ .x = a.x * s, .y = a.y * s, .z = a.z * s };
    }

    fn dot(a: Vec3, b: Vec3) f32 {
        return a.x * b.x + a.y * b.y + a.z * b.z;
    }

    fn lengthSqr(a: Vec3) f32 {
        return a.dot(a);
    }

    fn length(a: Vec3) f32 {
        return @sqrt(a.lengthSqr());
    }

    fn abs(a: Vec3) Vec3 {
        return .{ .x = @abs(a.x), .y = @abs(a.y), .z = @abs(a.z) };
    }

    fn maxComponent(a: Vec3, b: Vec3) Vec3 {
        return .{
            .x = @max(a.x, b.x),
            .y = @max(a.y, b.y),
            .z = @max(a.z, b.z),
        };
    }

    fn minComponent(a: Vec3, b: Vec3) Vec3 {
        return .{
            .x = @min(a.x, b.x),
            .y = @min(a.y, b.y),
            .z = @min(a.z, b.z),
        };
    }
};

fn clampF(v: f32, lo: f32, hi: f32) f32 {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

fn clamp01(v: f32) f32 {
    return clampF(v, 0.0, 1.0);
}

fn clampI(v: i32, lo: i32, hi: i32) i32 {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

// ============================================================================
// Global plugin state
// ============================================================================

const State = struct {
    initialized: bool = false,

    // Grid dimensions
    width: i32 = 0, // cell count (voxels)
    height: i32 = 0,
    depth: i32 = 0,
    sample_w: i32 = 0, // sample count = cell + 3
    sample_h: i32 = 0,
    sample_d: i32 = 0,
    voxel_size: f32 = 0,
    grid_min: Vec3 = Vec3.init(0, 0, 0),
    local_size: Vec3 = Vec3.init(0, 0, 0),
    total_samples: i32 = 0,
    cell_count: i32 = 0,

    // Live SDF is written by Unity's main thread. Connectivity runs against a
    // snapshot so cutting never races the background worker.
    sdf: []f32 = &[_]f32{},
    analysis_sdf: []f32 = &[_]f32{},

    // Union-find arrays (only written during analysis, read during removal)
    parent: []i32 = &[_]i32{},
    rank_: []u8 = &[_]u8{},
    removal_mask: []bool = &[_]bool{},
    solid_cells: []bool = &[_]bool{},
    component_counts: []i32 = &[_]i32{},

    // Background thread
    thread: ?std.Thread = null,
    should_exit: atomic.Value(bool) = atomic.Value(bool).init(false),
    work_requested: atomic.Value(bool) = atomic.Value(bool).init(false),
    work_done: atomic.Value(bool) = atomic.Value(bool).init(false),
    islands_found: atomic.Value(bool) = atomic.Value(bool).init(false),
    analysis_in_flight: atomic.Value(bool) = atomic.Value(bool).init(false),

    profile_segment_count: i32 = 24,

    allocator: std.mem.Allocator = undefined,
};

var g_state: State = .{};

// ============================================================================
// SDF helpers
// ============================================================================

fn sampleIdx(x: i32, y: i32, z: i32) usize {
    const sw = g_state.sample_w;
    const sh = g_state.sample_h;
    return @intCast(@as(i32, @as(i32, x) + sw * (y + sh * z)));
}

fn samplePoint(x: i32, y: i32, z: i32) Vec3 {
    return Vec3.init(
        g_state.grid_min.x + @as(f32, @floatFromInt(x - 1)) * g_state.voxel_size,
        g_state.grid_min.y + @as(f32, @floatFromInt(y - 1)) * g_state.voxel_size,
        g_state.grid_min.z + @as(f32, @floatFromInt(z - 1)) * g_state.voxel_size,
    );
}

fn cellIdx(x: i32, y: i32, z: i32) usize {
    return @intCast(@as(i32, x + g_state.width * (y + g_state.height * z)));
}

fn decodeCell(index: i32, out_x: *i32, out_y: *i32, out_z: *i32) void {
    const layer = g_state.width * g_state.height;
    out_z.* = @divTrunc(index, layer);
    const rem = index - out_z.* * layer;
    out_y.* = @divTrunc(rem, g_state.width);
    out_x.* = rem - out_y.* * g_state.width;
}

fn boxSdf(p: Vec3) f32 {
    const half = g_state.local_size.scale(0.5);
    const half_voxel = g_state.voxel_size * 0.5;
    const safe_half = Vec3.init(
        @max(half.x - half_voxel, half_voxel),
        @max(half.y - half_voxel, half_voxel),
        @max(half.z - half_voxel, half_voxel),
    );
    const q = p.abs().sub(safe_half);
    const zero = Vec3.init(0, 0, 0);
    const outside = q.maxComponent(zero);
    const inside = @min(@max(q.x, @max(q.y, q.z)), 0.0);
    return outside.length() + inside;
}

fn closestOnSegment(a: Vec3, b: Vec3, p: Vec3) Vec3 {
    const seg = b.sub(a);
    const len2 = seg.lengthSqr();
    const epsilon = g_state.voxel_size * 0.000001;
    if (len2 < epsilon * epsilon) return a;
    const t = clamp01(p.sub(a).dot(seg) / len2);
    return a.add(seg.scale(t));
}

fn profileRadius(axial: f32, radius: f32, height: f32) f32 {
    const h = @max(height, g_state.voxel_size * 0.000001);
    const norm = clamp01(axial / h);
    const segs: i32 = @max(g_state.profile_segment_count, 2);
    const pos = norm * @as(f32, @floatFromInt(segs));
    const seg = @min(@as(i32, @intFromFloat(@floor(pos))), segs - 1);
    const t: f32 = clamp01(pos - @as(f32, @floatFromInt(seg)));
    const inner = radius * 0.6535898;
    const even = (@mod(seg, 2) == 0);
    const next_even = (@mod(seg + 1, 2) == 0);
    const rA: f32 = if (even) radius else inner;
    const rB: f32 = if (next_even) radius else inner;
    const smoothT = t * t * (3.0 - 2.0 * t);
    return rA + (rB - rA) * smoothT;
}

fn profileCutterDifferenceAtRoot(
    sp: Vec3,
    cutter_root: Vec3,
    axis: Vec3,
    radius: f32,
    height: f32,
) f32 {
    const to_sample = sp.sub(cutter_root);
    const axial = to_sample.dot(axis);
    const radial_vec = to_sample.sub(axis.scale(axial));
    const radial_diff = profileRadius(axial, radius, height) - radial_vec.length();
    const axial_diff = @min(axial, height - axial);
    return @min(radial_diff, axial_diff);
}

fn profileMaxRadius(min_axial: f32, max_axial: f32, radius: f32, height: f32) f32 {
    const safe_height = @max(height, g_state.voxel_size * 0.000001);
    const clamped_min = @max(0.0, @min(min_axial, safe_height));
    const clamped_max = @max(0.0, @min(max_axial, safe_height));
    var max_radius = @max(
        profileRadius(clamped_min, radius, height),
        profileRadius(clamped_max, radius, height),
    );
    const segs: i32 = @max(g_state.profile_segment_count, 2);
    const min_profile_position = clamped_min / safe_height * @as(f32, @floatFromInt(segs));
    const max_profile_position = clamped_max / safe_height * @as(f32, @floatFromInt(segs));
    var first_even_boundary: i32 = @intFromFloat(@ceil(min_profile_position));
    if (@mod(first_even_boundary, 2) != 0) first_even_boundary += 1;
    if (@as(f32, @floatFromInt(first_even_boundary)) <= max_profile_position) {
        max_radius = radius;
    }
    return max_radius;
}

fn axialSweptProfileCutterDifference(
    sp: Vec3,
    local_start: Vec3,
    local_end: Vec3,
    axis: Vec3,
    radius: f32,
    height: f32,
) f32 {
    const motion = local_end.sub(local_start);
    const travel = motion.dot(axis);
    const from_start = sp.sub(local_start);
    const sample_axial = from_start.dot(axis);
    const axial_at_end = sample_axial - travel;
    const profile_min = @max(0.0, @min(sample_axial, axial_at_end));
    const profile_max = @min(height, @max(sample_axial, axial_at_end));

    if (profile_min > profile_max) {
        return @max(
            profileCutterDifferenceAtRoot(sp, local_start, axis, radius, height),
            profileCutterDifferenceAtRoot(sp, local_end, axis, radius, height),
        );
    }

    const radial = from_start.sub(axis.scale(sample_axial));
    const radial_diff = profileMaxRadius(profile_min, profile_max, radius, height) - radial.length();
    const sweep_min = @min(0.0, travel);
    const sweep_max = @max(height, height + travel);
    const axial_diff = @min(sample_axial - sweep_min, sweep_max - sample_axial);
    return @min(radial_diff, axial_diff);
}

fn sweptProfileCutterDifference(
    sp: Vec3,
    local_start: Vec3,
    local_end: Vec3,
    axis: Vec3,
    radius: f32,
    height: f32,
) f32 {
    const motion = local_end.sub(local_start);
    const axial_motion_dot = motion.dot(axis);
    const radial_motion = motion.sub(axis.scale(axial_motion_dot));
    const to_sample = sp.sub(local_start);
    const radial_from_start = to_sample.sub(axis.scale(to_sample.dot(axis)));

    var t: f32 = 1.0;
    const rm_len2 = radial_motion.lengthSqr();
    const geometry_scale = @max(@max(radius, height), g_state.voxel_size);
    const radial_motion_epsilon = geometry_scale * 0.000001;
    
    if (rm_len2 <= radial_motion_epsilon * radial_motion_epsilon) {
        return axialSweptProfileCutterDifference(
            sp,
            local_start,
            local_end,
            axis,
            radius,
            height,
        );
    }
    t = clamp01(radial_from_start.dot(radial_motion) / rm_len2);

    // Include the current cutter for mixed radial/axial motion.
    const projected_root = local_start.add(motion.scale(t));
    const projected_diff = profileCutterDifferenceAtRoot(
        sp,
        projected_root,
        axis,
        radius,
        height,
    );
    const end_diff = profileCutterDifferenceAtRoot(
        sp,
        local_end,
        axis,
        radius,
        height,
    );
    return @max(projected_diff, end_diff);
}

// ============================================================================
// Union-Find
// ============================================================================

fn ufFind(x: i32) i32 {
    var r = x;
    while (g_state.parent[@intCast(r)] != r) {
        r = g_state.parent[@intCast(r)];
    }
    // Path compression
    var cur = x;
    while (cur != r) {
        const next = g_state.parent[@intCast(cur)];
        g_state.parent[@intCast(cur)] = r;
        cur = next;
    }
    return r;
}

fn ufUnion(a: i32, b: i32) void {
    const ra = ufFind(a);
    const rb = ufFind(b);
    if (ra == rb) return;

    const rra: usize = @intCast(ra);
    const rrb: usize = @intCast(rb);
    if (g_state.rank_[rra] < g_state.rank_[rrb]) {
        g_state.parent[rra] = rb;
    } else if (g_state.rank_[rra] > g_state.rank_[rrb]) {
        g_state.parent[rrb] = ra;
    } else {
        g_state.parent[rrb] = ra;
        g_state.rank_[rra] += 1;
    }
}

// ============================================================================
// Cell solid check (matches C# CellContainsSdfMaterial)
// ============================================================================

const cube_corners = [8][3]i32{
    .{ 0, 0, 0 }, .{ 1, 0, 0 }, .{ 1, 0, 1 }, .{ 0, 0, 1 },
    .{ 0, 1, 0 }, .{ 1, 1, 0 }, .{ 1, 1, 1 }, .{ 0, 1, 1 },
};

fn cellIsSolid(cx: i32, cy: i32, cz: i32) bool {
    // Check center sample: sample at (cx+1, cy+1, cz+1) + half-voxel offset
    // Actually CellContainsSdfMaterial checks center and 8 corners
    // Center = voxel center = gridMin + (cx+0.5, cy+0.5, cz+0.5) * voxelSize
    // But in the SDF grid, sample index for center = (cx+1, cy+1, cz+1) approximately
    // Let me match exactly: the C# code does:
    //   center = GetVoxelCenterLocal(x, y, z, GetLocalMin())
    //          = gridMin + (x+0.5, y+0.5, z+0.5) * voxelSize
    //   SampleSdf(center) → trilinear interpolation
    //
    // For simplicity (and speed), we use the 9 corner samples directly.
    // A cell is solid if ANY of the 9 samples (center + 8 corners) <= 0.
    // The center sample index = (cx+1, cy+1, cz+1) is actually a corner,
    // so let's just check all 8 corners + the center.

    // 8 corner samples at indices (cx+1+corner_x, cy+1+corner_y, cz+1+corner_z)
    for (cube_corners) |corner| {
        const sx = cx + 1 + corner[0];
        const sy = cy + 1 + corner[1];
        const sz = cz + 1 + corner[2];
        if (sx >= 0 and sx < g_state.sample_w and
            sy >= 0 and sy < g_state.sample_h and
            sz >= 0 and sz < g_state.sample_d)
        {
            if (g_state.analysis_sdf[sampleIdx(sx, sy, sz)] <= 0.0) {
                return true;
            }
        }
    }
    return false;
}

// ============================================================================
// Connectivity analysis (runs on background thread, reads SDF only)
// ============================================================================

fn runConnectivityAnalysis() void {
    const w = g_state.width;
    const h = g_state.height;
    const d = g_state.depth;
    const n = g_state.cell_count;

    // Classify each cell once. The previous implementation recomputed the
    // eight SDF corner tests for every neighbor and every counting pass.
    for (0..@intCast(n)) |i| {
        g_state.parent[i] = @intCast(i);
        g_state.rank_[i] = 0;
        g_state.removal_mask[i] = false;
        g_state.component_counts[i] = 0;
    }

    for (0..@intCast(d)) |zi| {
        const z: i32 = @intCast(zi);
        for (0..@intCast(h)) |yi| {
            const y: i32 = @intCast(yi);
            for (0..@intCast(w)) |xi| {
                const x: i32 = @intCast(xi);
                g_state.solid_cells[cellIdx(x, y, z)] = cellIsSolid(x, y, z);
            }
        }
    }

    // Determine solid cells and union with +x, +y, +z neighbors (6-connectivity)
    for (0..@intCast(d)) |zi| {
        const z: i32 = @intCast(zi);
        for (0..@intCast(h)) |yi| {
            const y: i32 = @intCast(yi);
            for (0..@intCast(w)) |xi| {
                const x: i32 = @intCast(xi);
                const idx = cellIdx(x, y, z);
                if (!g_state.solid_cells[idx]) continue;

                // +x neighbor
                if (x + 1 < w and g_state.solid_cells[cellIdx(x + 1, y, z)]) {
                    if (g_state.analysis_sdf[sampleIdx(x + 2, y + 1, z + 1)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 2, y + 2, z + 1)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 2, y + 1, z + 2)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0)
                    {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x + 1, y, z)));
                    }
                }
                // +y neighbor
                if (y + 1 < h and g_state.solid_cells[cellIdx(x, y + 1, z)]) {
                    if (g_state.analysis_sdf[sampleIdx(x + 1, y + 2, z + 1)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 2, y + 2, z + 1)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 1, y + 2, z + 2)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0)
                    {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x, y + 1, z)));
                    }
                }
                // +z neighbor
                if (z + 1 < d and g_state.solid_cells[cellIdx(x, y, z + 1)]) {
                    if (g_state.analysis_sdf[sampleIdx(x + 1, y + 1, z + 2)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 2, y + 1, z + 2)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 1, y + 2, z + 2)] <= 0.0 or
                        g_state.analysis_sdf[sampleIdx(x + 2, y + 2, z + 2)] <= 0.0)
                    {
                        ufUnion(@intCast(idx), @intCast(cellIdx(x, y, z + 1)));
                    }
                }
            }
        }
    }

    // Flatten all parents
    for (0..@intCast(n)) |i| {
        _ = ufFind(@intCast(i));
    }

    var largest_root: i32 = -1;
    var largest_size: i32 = 0;
    var total_components: i32 = 0;

    for (0..@intCast(d)) |zi| {
        const z: i32 = @intCast(zi);
        for (0..@intCast(h)) |yi| {
            const y: i32 = @intCast(yi);
            for (0..@intCast(w)) |xi| {
                const x: i32 = @intCast(xi);
                if (!g_state.solid_cells[cellIdx(x, y, z)]) continue;
                const idx: usize = cellIdx(x, y, z);
                const root = g_state.parent[idx]; // already flattened
                const ri: usize = @intCast(root);
                g_state.component_counts[ri] += 1;
            }
        }
    }

    for (0..@intCast(n)) |i| {
        if (g_state.component_counts[i] > 0) {
            total_components += 1;
            if (g_state.component_counts[i] > largest_size) {
                largest_size = g_state.component_counts[i];
                largest_root = @intCast(i);
            }
        }
    }

    if (total_components <= 1 or largest_root < 0) {
        g_state.islands_found.store(false, .release);
        return;
    }

    // Mark non-largest solid cells for removal
    var found_any = false;
    var marked_count: i32 = 0;
    if (total_components > 1 and largest_root != -1) {
        for (0..@intCast(d)) |zi| {
            const z: i32 = @intCast(zi);
            for (0..@intCast(h)) |yi| {
                const y: i32 = @intCast(yi);
                for (0..@intCast(w)) |xi| {
                    const x: i32 = @intCast(xi);
                    const idx: usize = cellIdx(x, y, z);
                    if (!g_state.solid_cells[idx]) continue;
                    const root = g_state.parent[idx];
                    if (root != largest_root) {
                        g_state.removal_mask[idx] = true;
                        found_any = true;
                        marked_count += 1;
                    }
                }
            }
        }
    }
    
    g_state.islands_found.store(found_any, .release);
}

// ============================================================================
// Background thread entry
// ============================================================================

fn threadMain() void {
    while (!g_state.should_exit.load(.acquire)) {
        if (g_state.work_requested.load(.acquire)) {
            g_state.work_requested.store(false, .release);
            runConnectivityAnalysis();
            g_state.work_done.store(true, .release);
        } else {
            _ = c.usleep(1000);
        }
    }
}

// ============================================================================
// Carve cells (main thread, after analysis)
// ============================================================================

fn carveCellToAir(cx: i32, cy: i32, cz: i32) void {
    const air_value = g_state.voxel_size * 1.5;
    const sw = g_state.sample_w;
    const sh = g_state.sample_h;
    const sd = g_state.sample_d;

    var sx = cx;
    while (sx <= cx + 2) : (sx += 1) {
        if (sx < 0 or sx >= sw) continue;
        var sy = cy;
        while (sy <= cy + 2) : (sy += 1) {
            if (sy < 0 or sy >= sh) continue;
            var sz = cz;
            while (sz <= cz + 2) : (sz += 1) {
                if (sz < 0 or sz >= sd) continue;
                const idx = sampleIdx(sx, sy, sz);
                g_state.sdf[idx] = @max(g_state.sdf[idx], air_value);
            }
        }
    }
}

// ============================================================================
// Exported C API
// ============================================================================

export fn sdf_plugin_init(
    w: i32,
    h: i32,
    d: i32,
    sample_w: i32,
    sample_h: i32,
    sample_d: i32,
    voxel_size: f32,
    grid_min_x: f32,
    grid_min_y: f32,
    grid_min_z: f32,
    local_size_x: f32,
    local_size_y: f32,
    local_size_z: f32,
) void {
    if (g_state.initialized) {
        sdf_plugin_shutdown();
    }

    g_state.allocator = std.heap.c_allocator;
    g_state.width = w;
    g_state.height = h;
    g_state.depth = d;
    g_state.sample_w = sample_w;
    g_state.sample_h = sample_h;
    g_state.sample_d = sample_d;
    g_state.voxel_size = voxel_size;
    g_state.grid_min = Vec3.init(grid_min_x, grid_min_y, grid_min_z);
    g_state.local_size = Vec3.init(local_size_x, local_size_y, local_size_z);
    g_state.total_samples = sample_w * sample_h * sample_d;
    g_state.cell_count = w * h * d;

    // Allocate SDF
    g_state.sdf = g_state.allocator.alloc(f32, @intCast(g_state.total_samples)) catch return;
    g_state.analysis_sdf = g_state.allocator.alloc(f32, @intCast(g_state.total_samples)) catch return;
    g_state.parent = g_state.allocator.alloc(i32, @intCast(g_state.cell_count)) catch return;
    g_state.rank_ = g_state.allocator.alloc(u8, @intCast(g_state.cell_count)) catch return;
    g_state.removal_mask = g_state.allocator.alloc(bool, @intCast(g_state.cell_count)) catch return;
    g_state.solid_cells = g_state.allocator.alloc(bool, @intCast(g_state.cell_count)) catch return;
    g_state.component_counts = g_state.allocator.alloc(i32, @intCast(g_state.cell_count)) catch return;

    // Initialize box SDF
    for (0..@intCast(sample_d)) |zi| {
        const z: i32 = @intCast(zi);
        for (0..@intCast(sample_h)) |yi| {
            const y: i32 = @intCast(yi);
            for (0..@intCast(sample_w)) |xi| {
                const x: i32 = @intCast(xi);
                const sp = samplePoint(x, y, z);
                g_state.sdf[sampleIdx(x, y, z)] = boxSdf(sp);
            }
        }
    }
    @memcpy(g_state.analysis_sdf, g_state.sdf);

    // Start background thread
    g_state.should_exit.store(false, .release);
    log("Allocated arrays\n");
    g_state.should_exit.store(false, .release);
    g_state.work_requested.store(false, .release);
    g_state.work_done.store(false, .release);
    g_state.islands_found.store(false, .release);
    g_state.analysis_in_flight.store(false, .release);
    g_state.thread = std.Thread.spawn(.{}, threadMain, .{}) catch {
        log("Thread spawn failed\n");
        return;
    };
    log("Thread spawned successfully\n");
    g_state.initialized = true;
}

export fn sdf_plugin_shutdown() void {
    if (!g_state.initialized) return;

    g_state.should_exit.store(true, .release);
    
    if (g_state.thread) |t| {
        t.join();
        g_state.thread = null;
    }

    const alloc = g_state.allocator;
    if (g_state.sdf.len > 0) alloc.free(g_state.sdf);
    if (g_state.analysis_sdf.len > 0) alloc.free(g_state.analysis_sdf);
    if (g_state.parent.len > 0) alloc.free(g_state.parent);
    if (g_state.rank_.len > 0) alloc.free(g_state.rank_);
    if (g_state.removal_mask.len > 0) alloc.free(g_state.removal_mask);
    if (g_state.solid_cells.len > 0) alloc.free(g_state.solid_cells);
    if (g_state.component_counts.len > 0) alloc.free(g_state.component_counts);

    g_state.sdf = &[_]f32{};
    g_state.analysis_sdf = &[_]f32{};
    g_state.parent = &[_]i32{};
    g_state.rank_ = &[_]u8{};
    g_state.removal_mask = &[_]bool{};
    g_state.solid_cells = &[_]bool{};
    g_state.component_counts = &[_]i32{};
    g_state.initialized = false;
}

export fn sdf_plugin_reset() void {
    if (!g_state.initialized) return;
    const sw = g_state.sample_w;
    const sh = g_state.sample_h;
    const sd = g_state.sample_d;
    for (0..@intCast(sd)) |zi| {
        const z: i32 = @intCast(zi);
        for (0..@intCast(sh)) |yi| {
            const y: i32 = @intCast(yi);
            for (0..@intCast(sw)) |xi| {
                const x: i32 = @intCast(xi);
                const sp = samplePoint(x, y, z);
                g_state.sdf[sampleIdx(x, y, z)] = boxSdf(sp);
            }
        }
    }
    // Cancel any pending analysis
    g_state.work_requested.store(false, .release);
    g_state.work_done.store(false, .release);
    g_state.islands_found.store(false, .release);
    g_state.analysis_in_flight.store(false, .release);
}

/// Apply capsule/sphere cut. Matches CutCapsuleSdfCpu logic.
/// Returns number of changed samples (1 = something changed, 0 = nothing).
export fn sdf_cut_capsule(
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    radius: f32,
    min_x: i32,
    max_x: i32,
    min_y: i32,
    max_y: i32,
    min_z: i32,
    max_z: i32,
) i32 {
    if (!g_state.initialized) return 0;

    const local_start = Vec3.init(start_x, start_y, start_z);
    const local_end = Vec3.init(end_x, end_y, end_z);
    const affected_radius_sqr = (radius + g_state.voxel_size * 2.0);
    const ar2 = affected_radius_sqr * affected_radius_sqr;
    const change_epsilon = g_state.voxel_size * 0.0001;
    var changed: i32 = 0;

    var x: i32 = min_x;
    while (x <= max_x) : (x += 1) {
        var y: i32 = min_y;
        while (y <= max_y) : (y += 1) {
            var z: i32 = min_z;
            while (z <= max_z) : (z += 1) {
                const sp = samplePoint(x, y, z);
                const closest = closestOnSegment(local_start, local_end, sp);
                const to_sample = sp.sub(closest);
                const dist2 = to_sample.lengthSqr();
                if (dist2 > ar2) continue;

                const cutter_diff = radius - @sqrt(dist2);
                const idx = sampleIdx(x, y, z);
                const old_val = g_state.sdf[idx];
                const new_val = @max(old_val, cutter_diff);

                if (new_val > old_val + change_epsilon) {
                    g_state.sdf[idx] = new_val;
                    changed += 1;
                }
            }
        }
    }

    return changed;
}

/// Apply profile cutter cut. Matches SweptProfileCutterDifference from compute shader.
export fn sdf_cut_profile_cutter(
    start_x: f32,
    start_y: f32,
    start_z: f32,
    end_x: f32,
    end_y: f32,
    end_z: f32,
    axis_x: f32,
    axis_y: f32,
    axis_z: f32,
    radius: f32,
    height: f32,
    update_band: f32,
    min_x: i32,
    max_x: i32,
    min_y: i32,
    max_y: i32,
    min_z: i32,
    max_z: i32,
) i32 {
    if (!g_state.initialized) return 0;

    const local_start = Vec3.init(start_x, start_y, start_z);
    const local_end = Vec3.init(end_x, end_y, end_z);
    // Normalize axis
    const raw_axis = Vec3.init(axis_x, axis_y, axis_z);
    const axis_len = raw_axis.length();
    const axis = if (axis_len > 0.000001) raw_axis.scale(1.0 / axis_len) else raw_axis;
    const change_epsilon = g_state.voxel_size * 0.0001;

    var changed: i32 = 0;

    var x: i32 = min_x;
    while (x <= max_x) : (x += 1) {
        var y: i32 = min_y;
        while (y <= max_y) : (y += 1) {
            var z: i32 = min_z;
            while (z <= max_z) : (z += 1) {
                const sp = samplePoint(x, y, z);
                const cutter_diff = sweptProfileCutterDifference(
                    sp,
                    local_start,
                    local_end,
                    axis,
                    radius,
                    height,
                );

                if (cutter_diff < -update_band) continue;

                const idx = sampleIdx(x, y, z);
                const old_val = g_state.sdf[idx];
                const new_val = @max(old_val, cutter_diff);

                if (new_val > old_val + change_epsilon) {
                    g_state.sdf[idx] = new_val;
                    changed += 1;
                }
            }
        }
    }

    return changed;
}

/// Request background connectivity analysis. Returns immediately.
export fn sdf_check_connectivity() void {
    if (!g_state.initialized) return;
    if (g_state.analysis_in_flight.load(.acquire)) return;

    // Claim the whole request/result lifetime before copying. The worker only
    // reads analysis_sdf, while cuts continue to update the live SDF.
    g_state.analysis_in_flight.store(true, .release);
    @memcpy(g_state.analysis_sdf, g_state.sdf);
    g_state.islands_found.store(false, .release);
    g_state.work_done.store(false, .release);
    g_state.work_requested.store(true, .release);
}

/// Check if background analysis finished. Returns 1 if done, 0 if still running.
export fn sdf_is_connectivity_ready() i32 {
    if (!g_state.initialized) return 0;
    return if (g_state.work_done.load(.acquire)) @as(i32, 1) else @as(i32, 0);
}

/// Get result of connectivity analysis. Returns 1 if islands found, 0 otherwise.
/// Only meaningful after sdf_is_connectivity_ready() returns 1.
export fn sdf_get_connectivity_result() i32 {
    if (!g_state.initialized) return 0;
    return if (g_state.islands_found.load(.acquire)) @as(i32, 1) else @as(i32, 0);
}

/// Acknowledge/consume the connectivity result, resetting flags so the next
/// check can proceed. Must be called after reading the result, regardless of
/// whether islands were found.
export fn sdf_consume_connectivity_result() void {
    if (!g_state.initialized) return;
    g_state.work_done.store(false, .release);
    g_state.islands_found.store(false, .release);
    g_state.analysis_in_flight.store(false, .release);
}

/// Apply the removal of island cells to the SDF grid.
/// Must be called after connectivity analysis is done and islands are found.
/// Returns the number of cells removed.
export fn sdf_apply_removal() i32 {
    if (!g_state.initialized) return 0;
    if (!g_state.work_done.load(.acquire)) return 0;

    // Reset work_done flag so next check can proceed
    g_state.work_done.store(false, .release);
    g_state.islands_found.store(false, .release);

    var removed: i32 = 0;
    var scanned: i32 = 0;
    const w = g_state.width;
    const h = g_state.height;
    const d = g_state.depth;

    for (0..@intCast(d)) |zi| {
        const z: i32 = @intCast(zi);
        for (0..@intCast(h)) |yi| {
            const y: i32 = @intCast(yi);
            for (0..@intCast(w)) |xi| {
                const x: i32 = @intCast(xi);
                const idx: usize = cellIdx(x, y, z);
                scanned += 1;
                
                if (g_state.removal_mask[idx]) {
                    carveCellToAir(x, y, z);
                    g_state.removal_mask[idx] = false;
                    removed += 1;
                }
            }
        }
    }

    g_state.work_done.store(false, .release);
    g_state.analysis_in_flight.store(false, .release);
    return removed;
}

/// Copy the full SDF grid to the caller-provided buffer.
/// Buffer must have room for total_samples floats (sample_w * sample_h * sample_d).
export fn sdf_get_data(buffer: ?[*]f32, length: i32) void {
    if (!g_state.initialized) return;
    const buf = buffer orelse return;
    const count: usize = @intCast(@min(@as(i32, @intCast(length)), g_state.total_samples));
    for (0..count) |i| {
        buf[i] = g_state.sdf[i];
    }
}

/// Returns the total number of SDF samples (for buffer allocation on C# side).
export fn sdf_get_sample_count() i32 {
    return g_state.total_samples;
}

/// Returns 1 if the plugin is initialized and ready.
export fn sdf_is_ready() i32 {
    return if (g_state.initialized) @as(i32, 1) else @as(i32, 0);
}

/// Set the profile cutter segment count (must match GPU/C# side).
export fn sdf_set_profile_segment_count(count: i32) void {
    g_state.profile_segment_count = @max(count, 2);
}

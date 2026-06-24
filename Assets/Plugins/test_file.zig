const std = @import("std");

pub fn writeLog(msg: []const u8) void {
    const file = std.fs.cwd().createFile("/tmp/zig_plugin.log", .{ .append = true }) catch return;
    defer file.close();
    file.writer().writeAll(msg) catch return;
}

const std = @import("std");
pub fn main() void {
    const ts = std.posix.timespec { .sec = 0, .nsec = 1_000_000 };
    _ = std.posix.nanosleep(&ts, null);
}

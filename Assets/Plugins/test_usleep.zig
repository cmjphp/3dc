const std = @import("std");
const c = @cImport({
    @cInclude("unistd.h");
});
pub fn main() void {
    _ = c.usleep(1000);
}

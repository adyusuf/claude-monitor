// Keeps a release build from opening a console window on Windows.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    claude_monitor_desktop::run()
}

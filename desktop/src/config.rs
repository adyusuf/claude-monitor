//! The shell's only configuration (global rule #2). No other file reads the environment, and no
//! file holds the board's URL or port: the board's own Python config owns them and the shell
//! asks for the address (`board_open.py --url`).

use std::ffi::OsString;
use std::path::PathBuf;

pub const PYTHON: &str = "python3";
pub const SCRIPT: &str = "board_open.py";
pub const SCRIPT_FLAG: &str = "--url";
pub const WINDOW_LABEL: &str = "main";
pub const WINDOW_TITLE: &str = "Claude Monitor";
pub const WINDOW_SIZE: (f64, f64) = (1280.0, 860.0);
pub const SPLASH_PAGE: &str = "index.html";
/// The scheme Tauri serves the splash page on (macOS and Linux).
pub const APP_SCHEME: &str = "tauri";

const SCRIPTS_DIR_ENV: &str = "BOARD_SCRIPTS_DIR";
const DEFAULT_SCRIPTS_SUBDIR: [&str; 3] = [".claude", "scripts", "board"];

/// Where the board's scripts are: `BOARD_SCRIPTS_DIR`, else `~/.claude/scripts/board` (the stable
/// path SETUP.md links the clone to). `None` when neither is known.
pub fn scripts_dir_from(env_value: Option<OsString>, home: Option<PathBuf>) -> Option<PathBuf> {
    match env_value.filter(|v| !v.is_empty()) {
        Some(dir) => Some(PathBuf::from(dir)),
        None => home.map(|h| DEFAULT_SCRIPTS_SUBDIR.iter().fold(h, |p, part| p.join(part))),
    }
}

pub fn scripts_dir() -> Option<PathBuf> {
    scripts_dir_from(std::env::var_os(SCRIPTS_DIR_ENV), std::env::var_os("HOME").map(PathBuf::from))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_environment_wins_over_the_default() {
        let got = scripts_dir_from(Some("/x/board".into()), Some("/home/u".into()));
        assert_eq!(got, Some(PathBuf::from("/x/board")));
    }

    #[test]
    fn without_the_environment_it_is_under_the_home_directory() {
        let got = scripts_dir_from(None, Some("/home/u".into()));
        assert_eq!(got, Some(PathBuf::from("/home/u/.claude/scripts/board")));
    }

    #[test]
    fn an_empty_variable_counts_as_unset() {
        let got = scripts_dir_from(Some("".into()), Some("/home/u".into()));
        assert_eq!(got, Some(PathBuf::from("/home/u/.claude/scripts/board")));
    }

    #[test]
    fn with_nothing_known_there_is_no_directory() {
        assert_eq!(scripts_dir_from(None, None), None);
    }
}

//! Finding the board. The shell never knows the address: it runs `board_open.py --url`, which
//! makes sure the server is up and prints it, and it only follows an address on this machine.

use std::fmt;
use std::io;
use std::net::IpAddr;
use std::path::{Path, PathBuf};
use std::process::Command;

use tauri::Url;

use crate::config::{APP_SCHEME, PYTHON, SCRIPT, SCRIPT_FLAG};

const LOOPBACK_NAME: &str = "localhost";
const HTTP: &str = "http";

/// What running the script produced, without tying the logic to `std::process`.
pub struct Ran {
    pub ok: bool,
    pub stdout: String,
    pub stderr: String,
}

#[derive(Debug, PartialEq)]
pub enum BoardError {
    NoScriptsDir,
    Launch(String),
    Failed(String),
    BadAddress(String),
}

impl fmt::Display for BoardError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            BoardError::NoScriptsDir => write!(
                f,
                "Cannot find the board scripts. Set BOARD_SCRIPTS_DIR, or link the clone to ~/.claude/scripts/board (SETUP.md)."
            ),
            BoardError::Launch(why) => write!(f, "Cannot run {PYTHON}: {why}"),
            BoardError::Failed(why) => write!(f, "The board server did not start. {why}"),
            BoardError::BadAddress(got) => {
                write!(f, "Refusing to open an address that is not on this machine: {got}")
            }
        }
    }
}

/// True for an http address on this machine: 127.0.0.0/8, ::1 or localhost.
pub fn is_loopback(url: &Url) -> bool {
    if url.scheme() != HTTP {
        return false;
    }
    match url.host_str() {
        Some(LOOPBACK_NAME) => true,
        Some(host) => host
            .trim_start_matches('[')
            .trim_end_matches(']')
            .parse::<IpAddr>()
            .map(|ip| ip.is_loopback())
            .unwrap_or(false),
        None => false,
    }
}

/// May the window navigate here? Its own splash page, or the board on this machine - nothing else.
pub fn allow_navigation(url: &Url) -> bool {
    url.scheme() == APP_SCHEME || is_loopback(url)
}

/// The first line the script printed, as an address on this machine.
pub fn parse_address(stdout: &str) -> Result<Url, BoardError> {
    let line = stdout.lines().next().unwrap_or("").trim();
    match Url::parse(line) {
        Ok(url) if is_loopback(&url) => Ok(url),
        _ => Err(BoardError::BadAddress(line.to_string())),
    }
}

/// Runs the script through `run` (a closure so the logic can be tested without Python).
pub fn board_url(
    scripts_dir: Option<PathBuf>,
    run: impl FnOnce(&Path) -> io::Result<Ran>,
) -> Result<Url, BoardError> {
    let dir = scripts_dir.ok_or(BoardError::NoScriptsDir)?;
    let ran = run(&dir).map_err(|e| BoardError::Launch(e.to_string()))?;
    if !ran.ok {
        return Err(BoardError::Failed(ran.stderr.trim().to_string()));
    }
    parse_address(&ran.stdout)
}

/// The splash page with the reason shown on it (set as the fragment, which the page reads).
pub fn error_page(splash: &Url, error: &BoardError) -> Url {
    let mut page = splash.clone();
    page.set_fragment(Some(&error.to_string()));
    page
}

/// The real run: `python3 <dir>/board_open.py --url`.
pub fn run_script(dir: &Path) -> io::Result<Ran> {
    let out = Command::new(PYTHON).arg(dir.join(SCRIPT)).arg(SCRIPT_FLAG).output()?;
    Ok(Ran {
        ok: out.status.success(),
        stdout: String::from_utf8_lossy(&out.stdout).into_owned(),
        stderr: String::from_utf8_lossy(&out.stderr).into_owned(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn url(s: &str) -> Url {
        Url::parse(s).unwrap()
    }

    fn ran(ok: bool, stdout: &str, stderr: &str) -> io::Result<Ran> {
        Ok(Ran { ok, stdout: stdout.into(), stderr: stderr.into() })
    }

    #[test]
    fn only_addresses_on_this_machine_are_loopback() {
        for good in ["http://127.0.0.1:8765", "http://127.0.0.2/", "http://[::1]:1/", "http://localhost:9"] {
            assert!(is_loopback(&url(good)), "{good}");
        }
        for bad in ["https://127.0.0.1/", "http://example.com/", "http://10.0.0.5:8765", "http://127.0.0.1.evil.com/", "file:///etc/passwd"] {
            assert!(!is_loopback(&url(bad)), "{bad}");
        }
    }

    #[test]
    fn navigation_is_the_splash_or_the_local_board() {
        assert!(allow_navigation(&url("tauri://localhost/index.html")));
        assert!(allow_navigation(&url("http://127.0.0.1:8765/")));
        assert!(!allow_navigation(&url("https://example.com/")));
        assert!(!allow_navigation(&url("http://192.168.1.2:8765/")));
    }

    #[test]
    fn the_first_line_is_the_address() {
        assert_eq!(parse_address("http://127.0.0.1:8765\nnoise\n").unwrap(), url("http://127.0.0.1:8765"));
    }

    #[test]
    fn an_address_that_is_not_local_or_not_an_address_is_refused() {
        assert_eq!(parse_address("http://evil.example/"), Err(BoardError::BadAddress("http://evil.example/".into())));
        assert_eq!(parse_address(""), Err(BoardError::BadAddress(String::new())));
        assert_eq!(parse_address("not a url"), Err(BoardError::BadAddress("not a url".into())));
    }

    #[test]
    fn a_working_script_gives_the_url() {
        let got = board_url(Some("/s".into()), |dir| {
            assert_eq!(dir, Path::new("/s"));
            ran(true, "http://127.0.0.1:8765\n", "")
        });
        assert_eq!(got.unwrap(), url("http://127.0.0.1:8765"));
    }

    #[test]
    fn every_way_to_fail_is_a_distinct_error() {
        assert_eq!(board_url(None, |_| ran(true, "", "")), Err(BoardError::NoScriptsDir));
        let launch = board_url(Some("/s".into()), |_| Err(io::Error::new(io::ErrorKind::NotFound, "no python")));
        assert_eq!(launch, Err(BoardError::Launch("no python".into())));
        let failed = board_url(Some("/s".into()), |_| ran(false, "", " no server \n"));
        assert_eq!(failed, Err(BoardError::Failed("no server".into())));
        let bad = board_url(Some("/s".into()), |_| ran(true, "http://evil.example/\n", ""));
        assert!(matches!(bad, Err(BoardError::BadAddress(_))));
    }

    #[test]
    fn each_error_says_what_to_do_or_what_happened() {
        assert!(BoardError::NoScriptsDir.to_string().contains("BOARD_SCRIPTS_DIR"));
        assert!(BoardError::Launch("x".into()).to_string().contains("python3"));
        assert!(BoardError::Failed("why".into()).to_string().contains("why"));
        assert!(BoardError::BadAddress("a".into()).to_string().contains("not on this machine"));
    }

    #[test]
    fn the_error_rides_on_the_splash_page_as_its_fragment() {
        let page = error_page(&url("tauri://localhost/index.html"), &BoardError::NoScriptsDir);
        assert_eq!(page.path(), "/index.html");
        assert!(page.fragment().unwrap().contains("BOARD_SCRIPTS_DIR"));
    }

    #[test]
    fn running_a_missing_script_reports_a_failure_not_a_panic() {
        let ran = run_script(Path::new("/definitely/not/here")).unwrap();
        assert!(!ran.ok);
        assert!(!ran.stderr.is_empty());
    }
}

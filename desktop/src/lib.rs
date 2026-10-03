//! A window around the Claude Monitor live board. All behaviour lives in `scripts/board/`; this
//! crate opens a window, asks the board where it is, and shows it (docs/live-board.md §2g).

pub mod board;
pub mod config;

use tauri::{Builder, Runtime, Url, WebviewUrl, WebviewWindow, WebviewWindowBuilder};

const FALLBACK_SPLASH: &str = "tauri://localhost/";

/// Where the window goes once the board has answered (or not): the board, or the splash page with
/// the reason on it. Returns the address it navigated to.
pub fn show<R: Runtime>(window: &WebviewWindow<R>, found: Result<Url, board::BoardError>) -> Url {
    let target = found.unwrap_or_else(|error| {
        let splash = window.url().unwrap_or_else(|_| Url::parse(FALLBACK_SPLASH).expect("a constant address"));
        board::error_page(&splash, &error)
    });
    let _ = window.navigate(target.clone());
    target
}

/// The app on `builder`: one window on the splash page that follows only the splash and the local
/// board, and, in the background, `locate` (the board's address) then `show`.
pub fn build<R: Runtime>(
    builder: Builder<R>,
    locate: impl FnOnce() -> Result<Url, board::BoardError> + Send + 'static,
) -> Builder<R> {
    builder.setup(move |app| {
        let window = WebviewWindowBuilder::new(app, config::WINDOW_LABEL, WebviewUrl::App(config::SPLASH_PAGE.into()))
            .title(config::WINDOW_TITLE)
            .inner_size(config::WINDOW_SIZE.0, config::WINDOW_SIZE.1)
            .on_navigation(board::allow_navigation)
            .build()?;
        std::thread::spawn(move || show(&window, locate()));
        Ok(())
    })
}

pub fn run() {
    build(Builder::default(), || board::board_url(config::scripts_dir(), board::run_script))
        .run(tauri::generate_context!())
        .expect("the desktop window failed to run");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;
    use tauri::test::{mock_builder, mock_context, noop_assets};
    use tauri::Manager;

    /// The mock app after one turn of its event loop, which is when Tauri runs the setup hook.
    fn app_with(locate: impl FnOnce() -> Result<Url, board::BoardError> + Send + 'static) -> tauri::App<tauri::test::MockRuntime> {
        let mut app = build(mock_builder(), locate).build(mock_context(noop_assets())).expect("the mock app builds");
        app.run_iteration(|_, _| {});
        app
    }

    #[test]
    fn the_app_opens_one_window_it_may_stay_on() {
        let app = app_with(|| Err(board::BoardError::NoScriptsDir));
        let window = app.get_webview_window(config::WINDOW_LABEL).expect("the window exists");
        assert_eq!(app.webview_windows().len(), 1);
        assert!(board::allow_navigation(&window.url().unwrap()));  // the page it opens on is one it may stay on
    }

    #[test]
    fn the_background_thread_asks_for_the_board_once() {
        let (tx, rx) = mpsc::channel();
        let _app = app_with(move || {
            tx.send(()).unwrap();
            Err(board::BoardError::NoScriptsDir)
        });
        rx.recv_timeout(std::time::Duration::from_secs(10)).expect("locate ran");
    }

    #[test]
    fn show_goes_to_the_board_when_it_was_found() {
        let app = app_with(|| Err(board::BoardError::NoScriptsDir));
        let window = app.get_webview_window(config::WINDOW_LABEL).unwrap();
        let board_url = Url::parse("http://127.0.0.1:8765").unwrap();
        assert_eq!(show(&window, Ok(board_url.clone())), board_url);
    }

    #[test]
    fn show_puts_the_reason_on_the_splash_page_when_it_was_not() {
        let app = app_with(|| Err(board::BoardError::NoScriptsDir));
        let window = app.get_webview_window(config::WINDOW_LABEL).unwrap();
        let page = show(&window, Err(board::BoardError::Failed("no server".into())));
        assert!(page.fragment().unwrap().contains("no%20server"));  // the reason, percent-encoded in the fragment
    }
}

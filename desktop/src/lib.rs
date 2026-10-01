//! A window around the Claude Monitor live board. All behaviour lives in `scripts/board/`; this
//! crate opens a window, asks the board where it is, and shows it (docs/live-board.md §2g).

pub mod board;
pub mod config;

use tauri::{WebviewUrl, WebviewWindowBuilder};

pub fn run() {
    tauri::Builder::default()
        .setup(|app| {
            let window = WebviewWindowBuilder::new(app, config::WINDOW_LABEL, WebviewUrl::App(config::SPLASH_PAGE.into()))
                .title(config::WINDOW_TITLE)
                .inner_size(config::WINDOW_SIZE.0, config::WINDOW_SIZE.1)
                .on_navigation(board::allow_navigation)
                .build()?;
            std::thread::spawn(move || {
                let target = board::board_url(config::scripts_dir(), board::run_script)
                    .unwrap_or_else(|e| board::error_page(&window.url().unwrap_or_else(|_| tauri::Url::parse("tauri://localhost/").unwrap()), &e));
                let _ = window.navigate(target);
            });
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("the desktop window failed to run");
}

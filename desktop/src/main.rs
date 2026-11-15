// Kelvin desktop shell: native window, tray icon, close-to-tray, start with Windows.
// Hardware control lives in the .NET service (Kelvin.Service). Installed, that service is a Windows
// service that runs with the rights fan control needs and keeps curves running while this app is
// closed. In development, or a portable copy, this app starts the service itself and stops it
// cleanly on quit so every fan goes back to BIOS control.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Manager, RunEvent, WindowEvent};
use tauri_plugin_autostart::MacosLauncher;

const SERVICE_ADDR: &str = "127.0.0.1:5577";
const MINIMIZED_FLAG: &str = "--minimized";
const WINDOWS_SERVICE: &str = "KelvinService";

#[cfg(windows)]
fn hide_console(cmd: &mut Command) {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    cmd.creation_flags(CREATE_NO_WINDOW);
}

#[cfg(not(windows))]
fn hide_console(_cmd: &mut Command) {}

/// True when the installer registered Kelvin's background service. The app then never starts a
/// second copy: if the service isn't up yet (e.g. right after sign-in), the UI waits for it.
fn windows_service_installed() -> bool {
    if !cfg!(windows) {
        return false;
    }
    let mut cmd = Command::new("sc.exe");
    cmd.args(["query", WINDOWS_SERVICE]).stdout(Stdio::null()).stderr(Stdio::null());
    hide_console(&mut cmd);
    cmd.status().map(|s| s.success()).unwrap_or(false)
}

/// The service process, if this app started it. A service started some other way (an installed
/// Windows service, or a developer's terminal) is left running when the app quits.
struct Service(Mutex<Option<Child>>);

fn addr() -> SocketAddr {
    SERVICE_ADDR.parse().expect("valid socket address")
}

fn service_running() -> bool {
    TcpStream::connect_timeout(&addr(), Duration::from_millis(300)).is_ok()
}

fn spawn_service() -> std::io::Result<Child> {
    let mut cmd = if cfg!(debug_assertions) {
        // Development: run the service from source, on simulated hardware.
        let project = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../src/Kelvin.Service");
        let mut c = Command::new("dotnet");
        c.arg("run").arg("--project").arg(project).arg("--").arg("--simulate");
        c
    } else {
        // Portable copy without the Windows service: run the bundled service directly. It uses
        // real hardware only when run as administrator, and simulated hardware otherwise.
        let dir = std::env::current_exe()?
            .parent()
            .map(PathBuf::from)
            .unwrap_or_default();
        Command::new(dir.join("service").join("Kelvin.Service.exe"))
    };
    hide_console(&mut cmd);
    cmd.spawn()
}

/// Asks the service to shut down gracefully. Killing it outright would skip the code that hands
/// fans back to the BIOS, so this goes through its API first.
fn request_shutdown() {
    let Ok(mut stream) = TcpStream::connect_timeout(&addr(), Duration::from_millis(500)) else {
        return;
    };
    let _ = stream.set_read_timeout(Some(Duration::from_secs(2)));
    let request = format!(
        "POST /api/shutdown HTTP/1.1\r\nHost: {SERVICE_ADDR}\r\nX-Kelvin: 1\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
    );
    if stream.write_all(request.as_bytes()).is_ok() {
        let mut sink = [0u8; 256];
        let _ = stream.read(&mut sink);
    }
}

fn stop_service(app: &AppHandle) {
    let Some(mut child) = app.state::<Service>().0.lock().unwrap().take() else {
        return;
    };
    request_shutdown();
    let deadline = Instant::now() + Duration::from_secs(5);
    while Instant::now() < deadline {
        if !service_running() {
            break;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    // In development `child` is `dotnet run`; the service it launched has exited by now.
    let _ = child.kill();
    let _ = child.wait();
}

fn show_main(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.unminimize();
        let _ = w.show();
        let _ = w.set_focus();
    }
}

fn toggle_main(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        if w.is_visible().unwrap_or(false) && !w.is_minimized().unwrap_or(false) {
            let _ = w.hide();
        } else {
            show_main(app);
        }
    }
}

fn main() {
    let app = tauri::Builder::default()
        // A second launch (e.g. clicking the shortcut again) brings the existing window forward.
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| show_main(app)))
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            Some(vec![MINIMIZED_FLAG]),
        ))
        .manage(Service(Mutex::new(None)))
        .setup(|app| {
            let installed = !cfg!(debug_assertions) && windows_service_installed();
            if !installed && !service_running() {
                match spawn_service() {
                    Ok(child) => *app.state::<Service>().0.lock().unwrap() = Some(child),
                    Err(e) => eprintln!("Couldn't start the Kelvin service: {e}"),
                }
            }

            let open = MenuItem::with_id(app, "open", "Open Kelvin", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Quit Kelvin", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&open, &PredefinedMenuItem::separator(app)?, &quit])?;

            TrayIconBuilder::with_id("main")
                .icon(app.default_window_icon().cloned().expect("bundle icon"))
                .tooltip("Kelvin")
                .menu(&menu)
                .show_menu_on_left_click(false)
                .on_menu_event(|app, event| match event.id().as_ref() {
                    "open" => show_main(app),
                    "quit" => app.exit(0),
                    _ => {}
                })
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        toggle_main(tray.app_handle());
                    }
                })
                .build(app)?;

            // Started with Windows: stay in the tray until the user opens the window.
            if std::env::args().any(|a| a == MINIMIZED_FLAG) {
                if let Some(w) = app.get_webview_window("main") {
                    let _ = w.hide();
                }
            }
            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing the window keeps Kelvin running in the tray, so fan curves keep working.
            if let WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = window.hide();
            }
        })
        .build(tauri::generate_context!())
        .expect("error while building Kelvin");

    app.run(|app, event| {
        if let RunEvent::Exit = event {
            stop_service(app);
        }
    });
}

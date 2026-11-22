// Screen sync: lights follow what's on screen.
//
// The service drives the lights but runs where it can't see the screen, so this app does the
// looking. While the Screen effect is on, it samples the main display about 10 times a second,
// shrinks it to a 16 x 9 grid of colors, and sends that grid to the service on this PC. Nothing
// else leaves this process, nothing is saved, and when the effect is off no capture happens.

use std::io::{Read, Write};
use std::net::TcpStream;
use std::time::Duration;

use crate::{addr, SERVICE_ADDR, SERVICE_PORT};

const GRID_W: usize = 16;
const GRID_H: usize = 9;
/// Each grid cell averages a block of this many sampled pixels per side.
const BLOCK: usize = 4;
const FRAME_INTERVAL: Duration = Duration::from_millis(80);
const IDLE_INTERVAL: Duration = Duration::from_secs(2);

pub fn start() {
    let _ = std::thread::Builder::new().name("screen-sync".into()).spawn(run);
}

fn run() {
    loop {
        if !wanted() {
            std::thread::sleep(IDLE_INTERVAL);
            continue;
        }
        // Capture until the service says the effect is off, or stops answering.
        loop {
            let Some(grid) = capture() else { break };
            match send(&grid) {
                Some(true) => std::thread::sleep(FRAME_INTERVAL),
                _ => break,
            }
        }
        std::thread::sleep(IDLE_INTERVAL);
    }
}

/// Asks the service whether the Screen effect is on.
fn wanted() -> bool {
    request("GET", "").is_some_and(|body| is_active(&body))
}

/// Sends one grid. Returns whether the service still wants frames, or None if it didn't answer.
fn send(grid: &[u8]) -> Option<bool> {
    let body = format!(r#"{{"width":{GRID_W},"height":{GRID_H},"data":"{}"}}"#, base64(grid));
    request("PUT", &body).map(|response| is_active(&response))
}

fn is_active(response: &str) -> bool {
    response.contains(r#""active":true"#)
}

fn request(method: &str, body: &str) -> Option<String> {
    // The screen's colors only ever go to Kelvin's own service, never to whatever else might be
    // listening on its port while the service is down.
    if !crate::peer::is_kelvin(SERVICE_PORT, crate::WINDOWS_SERVICE) {
        return None;
    }
    let mut stream = TcpStream::connect_timeout(&addr(), Duration::from_millis(300)).ok()?;
    stream.set_read_timeout(Some(Duration::from_secs(1))).ok()?;
    stream.set_write_timeout(Some(Duration::from_secs(1))).ok()?;
    let request = format!(
        "{method} /api/lighting/screen HTTP/1.1\r\nHost: {SERVICE_ADDR}\r\nX-Kelvin: 1\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
        body.len()
    );
    stream.write_all(request.as_bytes()).ok()?;
    // The reply is a few dozen bytes; never read more than a few kilobytes of it.
    let mut response = String::new();
    (&mut stream).take(4096).read_to_string(&mut response).ok()?;
    response.starts_with("HTTP/1.1 200").then_some(response)
}

fn base64(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let n = (chunk[0] as u32) << 16
            | (*chunk.get(1).unwrap_or(&0) as u32) << 8
            | *chunk.get(2).unwrap_or(&0) as u32;
        out.push(TABLE[(n >> 18) as usize & 63] as char);
        out.push(TABLE[(n >> 12) as usize & 63] as char);
        out.push(if chunk.len() > 1 { TABLE[(n >> 6) as usize & 63] as char } else { '=' });
        out.push(if chunk.len() > 2 { TABLE[n as usize & 63] as char } else { '=' });
    }
    out
}

/// Averages BLOCK x BLOCK squares of BGRA pixels down to a GRID_W x GRID_H grid of RGB.
fn shrink(bgra: &[u8]) -> Vec<u8> {
    let width = GRID_W * BLOCK;
    let mut grid = Vec::with_capacity(GRID_W * GRID_H * 3);
    for gy in 0..GRID_H {
        for gx in 0..GRID_W {
            let (mut r, mut g, mut b) = (0u32, 0u32, 0u32);
            for y in 0..BLOCK {
                for x in 0..BLOCK {
                    let i = ((gy * BLOCK + y) * width + gx * BLOCK + x) * 4;
                    b += bgra[i] as u32;
                    g += bgra[i + 1] as u32;
                    r += bgra[i + 2] as u32;
                }
            }
            let n = (BLOCK * BLOCK) as u32;
            grid.extend([(r / n) as u8, (g / n) as u8, (b / n) as u8]);
        }
    }
    grid
}

/// Samples the main display into a small grid of RGB colors.
#[cfg(windows)]
fn capture() -> Option<Vec<u8>> {
    use windows_sys::Win32::Graphics::Gdi::{
        BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, GetDC, GetDIBits,
        ReleaseDC, SelectObject, SetStretchBltMode, StretchBlt, BITMAPINFO, BITMAPINFOHEADER,
        COLORONCOLOR, DIB_RGB_COLORS, SRCCOPY,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{GetSystemMetrics, SM_CXSCREEN, SM_CYSCREEN};

    let (w, h) = ((GRID_W * BLOCK) as i32, (GRID_H * BLOCK) as i32);
    let mut pixels = vec![0u8; (w * h * 4) as usize];
    // SAFETY: plain GDI calls on handles created and released here; the buffer is w*h*4 bytes,
    // matching the 32-bit top-down bitmap described in `info`.
    let ok = unsafe {
        let screen = GetDC(std::ptr::null_mut());
        if screen.is_null() {
            return None;
        }
        let memory = CreateCompatibleDC(screen);
        let bitmap = CreateCompatibleBitmap(screen, w, h);
        let previous = SelectObject(memory, bitmap);
        // Point sampling is far cheaper than a smooth shrink, and the averaging in `shrink`
        // plus the fade in the service hide the noise.
        SetStretchBltMode(memory, COLORONCOLOR);
        let (sw, sh) = (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        let copied = if sw == w && sh == h {
            BitBlt(memory, 0, 0, w, h, screen, 0, 0, SRCCOPY)
        } else {
            StretchBlt(memory, 0, 0, w, h, screen, 0, 0, sw, sh, SRCCOPY)
        };
        SelectObject(memory, previous);

        let mut info: BITMAPINFO = std::mem::zeroed();
        info.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        info.bmiHeader.biWidth = w;
        info.bmiHeader.biHeight = -h; // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        let rows = GetDIBits(memory, bitmap, 0, h as u32, pixels.as_mut_ptr().cast(), &mut info, DIB_RGB_COLORS);

        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(std::ptr::null_mut(), screen);
        copied != 0 && rows == h
    };
    ok.then(|| shrink(&pixels))
}

#[cfg(not(windows))]
fn capture() -> Option<Vec<u8>> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64_matches_the_standard() {
        assert_eq!(base64(b""), "");
        assert_eq!(base64(b"f"), "Zg==");
        assert_eq!(base64(b"fo"), "Zm8=");
        assert_eq!(base64(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn shrink_averages_blocks_and_swaps_to_rgb() {
        let mut bgra = vec![0u8; GRID_W * BLOCK * GRID_H * BLOCK * 4];
        // Top-left block: half pure blue, half black.
        for y in 0..BLOCK {
            for x in 0..BLOCK / 2 {
                bgra[(y * GRID_W * BLOCK + x) * 4] = 200;
            }
        }
        let grid = shrink(&bgra);
        assert_eq!(grid.len(), GRID_W * GRID_H * 3);
        assert_eq!(&grid[..3], &[0, 0, 100]);
        assert_eq!(&grid[3..6], &[0, 0, 0]);
    }
}

#[cfg(all(test, windows))]
mod live {
    /// Run by hand: `cargo test -- --ignored --nocapture`. Needs a real desktop.
    #[test]
    #[ignore]
    fn captures_the_real_screen() {
        let started = std::time::Instant::now();
        let grid = super::capture().expect("capture failed");
        println!("capture took {:?}", started.elapsed());
        let started = std::time::Instant::now();
        for _ in 0..20 {
            super::capture();
        }
        println!("then {:?} each", started.elapsed() / 20);
        for row in grid.chunks(super::GRID_W * 3) {
            let line: Vec<String> = row.chunks(3).map(|p| format!("{:02x}{:02x}{:02x}", p[0], p[1], p[2])).collect();
            println!("{}", line.join(" "));
        }
        assert!(grid.iter().any(|&b| b != 0), "screen came back all black");
    }
}

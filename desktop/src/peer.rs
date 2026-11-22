// Is the program listening on Kelvin's port really Kelvin's service?
//
// Any program on the PC, under any account, can listen on a loopback port that happens to be
// free. If the real service is stopped, an impostor could sit on 127.0.0.1:5577, tell this app
// "screen sync is on" and be sent the colors of the screen, and it would look like a healthy
// service to the watchdog. So before this app trusts the port, it asks Windows which process
// owns it and accepts only:
//   - the installed KelvinService process,
//   - the service this app started itself (a portable copy, or development), or
//   - the service program that ships beside this app, however it was started.

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// Process id of the service this app spawned, or 0.
pub static CHILD_PID: AtomicU32 = AtomicU32::new(0);

static CACHE: Mutex<Option<(Instant, bool)>> = Mutex::new(None);
const CACHE_FOR: Duration = Duration::from_secs(2);

/// True when the listener on `port` is Kelvin's own service. The answer is reused for two
/// seconds, so callers can ask before every request without the lookups adding up.
pub fn is_kelvin(port: u16, service_name: &str) -> bool {
    if cfg!(debug_assertions) {
        return true; // development runs the service from source, under `dotnet`
    }
    let mut cache = CACHE.lock().unwrap();
    if let Some((at, answer)) = *cache {
        if at.elapsed() < CACHE_FOR {
            return answer;
        }
    }
    let answer = check(port, service_name);
    *cache = Some((Instant::now(), answer));
    answer
}

#[cfg(not(windows))]
fn check(_port: u16, _service_name: &str) -> bool {
    true
}

#[cfg(windows)]
fn check(port: u16, service_name: &str) -> bool {
    let Some(listener) = win::listener_pid(port) else { return false };
    if listener == 0 {
        return false;
    }
    if win::service_pid(service_name) == Some(listener) {
        return true;
    }
    let child = CHILD_PID.load(Ordering::Relaxed);
    if child != 0 && child == listener {
        return true;
    }
    // Not started by Windows or by us: accept it only if it is the program shipped beside this app.
    match (win::image_path(listener), bundled_service()) {
        (Some(running), Some(ours)) => same_file(&running, &ours),
        _ => false,
    }
}

#[cfg(windows)]
fn bundled_service() -> Option<std::path::PathBuf> {
    Some(std::env::current_exe().ok()?.parent()?.join("service").join("Kelvin.Service.exe"))
}

#[cfg(windows)]
fn same_file(a: &std::path::Path, b: &std::path::Path) -> bool {
    match (std::fs::canonicalize(a), std::fs::canonicalize(b)) {
        (Ok(a), Ok(b)) => a.to_string_lossy().eq_ignore_ascii_case(&b.to_string_lossy()),
        _ => false,
    }
}

#[cfg(windows)]
mod win {
    use std::os::windows::ffi::OsStringExt;
    use windows_sys::Win32::Foundation::{CloseHandle, ERROR_INSUFFICIENT_BUFFER};
    use windows_sys::Win32::NetworkManagement::IpHelper::{
        GetExtendedTcpTable, MIB_TCPROW_OWNER_PID, MIB_TCPTABLE_OWNER_PID, TCP_TABLE_OWNER_PID_LISTENER,
    };
    use windows_sys::Win32::System::Services::{
        CloseServiceHandle, OpenSCManagerW, OpenServiceW, QueryServiceStatusEx, SC_MANAGER_CONNECT,
        SC_STATUS_PROCESS_INFO, SERVICE_QUERY_STATUS, SERVICE_RUNNING, SERVICE_STATUS_PROCESS,
    };
    use windows_sys::Win32::System::Threading::{
        OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION,
    };

    const AF_INET: u32 = 2;
    const LOOPBACK: u32 = u32::from_ne_bytes([127, 0, 0, 1]);

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    /// The process listening on 127.0.0.1:`port` (or on every address), from Windows' own table.
    pub fn listener_pid(port: u16) -> Option<u32> {
        let mut size: u32 = 0;
        unsafe { GetExtendedTcpTable(std::ptr::null_mut(), &mut size, 0, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) };
        for _ in 0..3 {
            // u32 storage keeps the table aligned for its row type.
            let mut buffer = vec![0u32; (size as usize).div_ceil(4) + 16];
            let mut len = (buffer.len() * 4) as u32;
            let result = unsafe {
                GetExtendedTcpTable(buffer.as_mut_ptr().cast(), &mut len, 0, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0)
            };
            if result == ERROR_INSUFFICIENT_BUFFER {
                size = len;
                continue; // the table grew between the two calls
            }
            if result != 0 {
                return None;
            }
            let table = buffer.as_ptr().cast::<MIB_TCPTABLE_OWNER_PID>();
            let count = unsafe { (*table).dwNumEntries } as usize;
            let first = unsafe { std::ptr::addr_of!((*table).table).cast::<MIB_TCPROW_OWNER_PID>() };
            let room = (buffer.len() * 4 - 4) / std::mem::size_of::<MIB_TCPROW_OWNER_PID>();
            for i in 0..count.min(room) {
                let row = unsafe { &*first.add(i) };
                // Ports are stored in network byte order in the low 16 bits.
                let row_port = u16::from_be((row.dwLocalPort & 0xFFFF) as u16);
                if row_port == port && (row.dwLocalAddr == LOOPBACK || row.dwLocalAddr == 0) {
                    return Some(row.dwOwningPid);
                }
            }
            return Some(0);
        }
        None
    }

    /// The process id of the installed Windows service, when it is running.
    pub fn service_pid(name: &str) -> Option<u32> {
        unsafe {
            let manager = OpenSCManagerW(std::ptr::null(), std::ptr::null(), SC_MANAGER_CONNECT);
            if manager.is_null() {
                return None;
            }
            let service = OpenServiceW(manager, wide(name).as_ptr(), SERVICE_QUERY_STATUS);
            if service.is_null() {
                CloseServiceHandle(manager);
                return None;
            }
            let mut status: SERVICE_STATUS_PROCESS = std::mem::zeroed();
            let mut needed = 0u32;
            let ok = QueryServiceStatusEx(
                service,
                SC_STATUS_PROCESS_INFO,
                (&mut status as *mut SERVICE_STATUS_PROCESS).cast(),
                std::mem::size_of::<SERVICE_STATUS_PROCESS>() as u32,
                &mut needed,
            );
            CloseServiceHandle(service);
            CloseServiceHandle(manager);
            (ok != 0 && status.dwCurrentState == SERVICE_RUNNING && status.dwProcessId != 0).then_some(status.dwProcessId)
        }
    }

    /// Where a running process's program file is.
    pub fn image_path(pid: u32) -> Option<std::path::PathBuf> {
        unsafe {
            let process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if process.is_null() {
                return None;
            }
            let mut buffer = vec![0u16; 1024];
            let mut len = buffer.len() as u32;
            let ok = QueryFullProcessImageNameW(process, 0, buffer.as_mut_ptr(), &mut len);
            CloseHandle(process);
            (ok != 0).then(|| std::ffi::OsString::from_wide(&buffer[..len as usize]).into())
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn finds_the_process_that_owns_a_listening_port() {
            let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
            let port = listener.local_addr().unwrap().port();
            assert_eq!(listener_pid(port), Some(std::process::id()));
            drop(listener);
            assert_eq!(listener_pid(port), Some(0)); // nothing listens there any more
        }

        #[test]
        fn knows_where_this_process_runs_from() {
            let here = image_path(std::process::id()).unwrap();
            assert!(super::super::same_file(&here, &std::env::current_exe().unwrap()));
        }

        #[test]
        fn a_service_that_does_not_exist_has_no_process() {
            assert_eq!(service_pid("KelvinNoSuchService"), None);
        }
    }
}

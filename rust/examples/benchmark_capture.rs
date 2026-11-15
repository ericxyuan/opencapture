#[cfg(windows)]
fn main() -> std::io::Result<()> {
    use opencapture_core::capture::capture_virtual_desktop;
    use std::time::Instant;
    use windows_sys::Win32::UI::HiDpi::{
        SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    };

    if unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) } == 0 {
        return Err(std::io::Error::last_os_error());
    }

    let bounds = capture_virtual_desktop()?.bounds();
    for _ in 0..5 {
        std::hint::black_box(capture_virtual_desktop()?);
    }
    let mut samples = Vec::with_capacity(30);
    for _ in 0..30 {
        let start = Instant::now();
        std::hint::black_box(capture_virtual_desktop()?);
        samples.push(start.elapsed().as_secs_f64() * 1000.0);
    }
    samples.sort_by(f64::total_cmp);
    println!("SDR GDI virtual-desktop capture: 5 warmups, 30 samples");
    println!(
        "Bounds: {},{} {}x{}",
        bounds.x, bounds.y, bounds.width, bounds.height
    );
    println!(
        "Median: {:.2} ms; p95: {:.2} ms",
        (samples[14] + samples[15]) / 2.0,
        samples[28]
    );
    Ok(())
}

#[cfg(not(windows))]
fn main() {
    eprintln!("Desktop capture requires Windows.");
}

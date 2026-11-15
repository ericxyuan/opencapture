use std::io;

/// Physical virtual-desktop coordinates; origins can be negative.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl Rect {
    fn byte_len(self) -> io::Result<usize> {
        if self.width <= 0 || self.height <= 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "empty capture bounds",
            ));
        }
        (self.width as usize)
            .checked_mul(self.height as usize)
            .and_then(|v| v.checked_mul(4))
            .filter(|&v| v <= isize::MAX as usize)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "capture bounds too large"))
    }

    fn intersection(self, other: Rect) -> Option<Rect> {
        if self.width <= 0 || self.height <= 0 || other.width <= 0 || other.height <= 0 {
            return None;
        }
        let x = i64::from(self.x).max(i64::from(other.x));
        let y = i64::from(self.y).max(i64::from(other.y));
        let right = (i64::from(self.x) + i64::from(self.width))
            .min(i64::from(other.x) + i64::from(other.width));
        let bottom = (i64::from(self.y) + i64::from(self.height))
            .min(i64::from(other.y) + i64::from(other.height));
        if right <= x || bottom <= y {
            return None;
        }
        Some(Rect {
            x: x as i32,
            y: y as i32,
            width: (right - x) as i32,
            height: (bottom - y) as i32,
        })
    }
}

/// Top-down BGRX pixels. The unused fourth byte must not be treated as alpha.
#[derive(Debug)]
pub struct CapturedFrame {
    bounds: Rect,
    pixels: Vec<u8>,
}

impl CapturedFrame {
    pub fn bounds(&self) -> Rect {
        self.bounds
    }
    pub fn pixels(&self) -> &[u8] {
        &self.pixels
    }
    pub fn new(bounds: Rect, pixels: Vec<u8>) -> io::Result<Self> {
        if pixels.len() != bounds.byte_len()? {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "pixel buffer does not match capture bounds",
            ));
        }
        Ok(Self { bounds, pixels })
    }

    pub fn pixel(&self, x: i32, y: i32) -> Option<[u8; 3]> {
        let x = i64::from(x) - i64::from(self.bounds.x);
        let y = i64::from(y) - i64::from(self.bounds.y);
        if x < 0 || y < 0 || x >= i64::from(self.bounds.width) || y >= i64::from(self.bounds.height)
        {
            return None;
        }
        let offset = ((y as usize * self.bounds.width as usize) + x as usize) * 4;
        Some([
            self.pixels[offset + 2],
            self.pixels[offset + 1],
            self.pixels[offset],
        ])
    }

    pub fn crop(&self, selection: Rect) -> io::Result<CapturedFrame> {
        let clipped = self.bounds.intersection(selection).ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "selection does not intersect capture",
            )
        })?;
        let mut pixels = Vec::with_capacity(clipped.byte_len()?);
        let left = (i64::from(clipped.x) - i64::from(self.bounds.x)) as usize;
        let top = (i64::from(clipped.y) - i64::from(self.bounds.y)) as usize;
        let source_stride = self.bounds.width as usize * 4;
        let row_size = clipped.width as usize * 4;
        for row in 0..clipped.height as usize {
            let start = (top + row) * source_stride + left * 4;
            pixels.extend_from_slice(&self.pixels[start..start + row_size]);
        }
        Ok(Self {
            bounds: clipped,
            pixels,
        })
    }
}

#[cfg(windows)]
pub fn capture_virtual_desktop() -> io::Result<CapturedFrame> {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN,
        SM_YVIRTUALSCREEN,
    };
    // The application must set per-monitor-v2 DPI awareness before calling this.
    let bounds = unsafe {
        Rect {
            x: GetSystemMetrics(SM_XVIRTUALSCREEN),
            y: GetSystemMetrics(SM_YVIRTUALSCREEN),
            width: GetSystemMetrics(SM_CXVIRTUALSCREEN),
            height: GetSystemMetrics(SM_CYVIRTUALSCREEN),
        }
    };
    capture_rectangle(bounds)
}

/// Captures the SDR GDI desktop path; HDR desktop duplication is not implemented yet.
#[cfg(windows)]
pub fn capture_rectangle(bounds: Rect) -> io::Result<CapturedFrame> {
    use std::ptr::null_mut;
    use windows_sys::Win32::Graphics::Gdi::*;

    struct Resources {
        screen: HDC,
        memory: HDC,
        bitmap: HBITMAP,
        previous: HGDIOBJ,
    }
    impl Drop for Resources {
        fn drop(&mut self) {
            unsafe {
                if self.previous != 0 {
                    SelectObject(self.memory, self.previous);
                }
                if self.bitmap != 0 {
                    DeleteObject(self.bitmap);
                }
                if self.memory != 0 {
                    DeleteDC(self.memory);
                }
                if self.screen != 0 {
                    ReleaseDC(0, self.screen);
                }
            }
        }
    }

    let length = bounds.byte_len()?;
    let mut r = Resources {
        screen: 0,
        memory: 0,
        bitmap: 0,
        previous: 0,
    };
    let mut data = null_mut();
    unsafe {
        r.screen = GetDC(0);
        if r.screen == 0 {
            return Err(io::Error::last_os_error());
        }
        r.memory = CreateCompatibleDC(r.screen);
        if r.memory == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut info: BITMAPINFO = std::mem::zeroed();
        info.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        info.bmiHeader.biWidth = bounds.width;
        info.bmiHeader.biHeight = -bounds.height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        r.bitmap = CreateDIBSection(r.screen, &info, DIB_RGB_COLORS, &mut data, 0, 0);
        if r.bitmap == 0 || data.is_null() {
            return Err(io::Error::last_os_error());
        }
        r.previous = SelectObject(r.memory, r.bitmap);
        if r.previous == 0 || r.previous == -1 {
            r.previous = 0;
            return Err(io::Error::last_os_error());
        }
        if BitBlt(
            r.memory,
            0,
            0,
            bounds.width,
            bounds.height,
            r.screen,
            bounds.x,
            bounds.y,
            SRCCOPY | CAPTUREBLT,
        ) == 0
            || GdiFlush() == 0
        {
            return Err(io::Error::last_os_error());
        }
        let pixels = std::slice::from_raw_parts(data.cast::<u8>(), length).to_vec();
        CapturedFrame::new(bounds, pixels)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pixels_and_crop_use_physical_coordinates() {
        let bounds = Rect {
            x: -2,
            y: -1,
            width: 3,
            height: 2,
        };
        let frame = CapturedFrame::new(bounds, (0..24).collect()).unwrap();
        assert_eq!(frame.pixel(-1, 0), Some([18, 17, 16]));
        assert_eq!(frame.pixel(1, 0), None);
        let crop = frame
            .crop(Rect {
                x: -1,
                y: -2,
                width: 3,
                height: 2,
            })
            .unwrap();
        assert_eq!(
            crop.bounds,
            Rect {
                x: -1,
                y: -1,
                width: 2,
                height: 1
            }
        );
        assert_eq!(crop.pixels, (4..12).collect::<Vec<_>>());
        assert!(frame
            .crop(Rect {
                x: 20,
                y: 20,
                width: 1,
                height: 1
            })
            .is_err());
    }

    #[test]
    fn validates_bounds_and_buffer_length() {
        assert!(CapturedFrame::new(
            Rect {
                x: 0,
                y: 0,
                width: 0,
                height: 2
            },
            vec![]
        )
        .is_err());
        assert!(CapturedFrame::new(
            Rect {
                x: 0,
                y: 0,
                width: 2,
                height: 2
            },
            vec![0; 15]
        )
        .is_err());
    }

    #[cfg(windows)]
    #[test]
    fn live_virtual_desktop_capture_smoke() {
        let frame = capture_virtual_desktop().expect("live desktop capture");
        assert_eq!(frame.pixels.len(), frame.bounds.byte_len().unwrap());
        assert!(frame.pixel(frame.bounds.x, frame.bounds.y).is_some());
    }
}

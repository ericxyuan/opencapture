use crate::capture::{CapturedFrame, Rect};
use crate::mjpeg_avi::{MjpegAviReader, MjpegAviWriter};
use jpeg_encoder::{ColorType, Encoder};
use std::io;
use std::path::Path;
#[cfg(windows)]
use std::sync::mpsc::{self, Sender};
#[cfg(windows)]
use std::thread::{self, JoinHandle};
#[cfg(windows)]
use std::time::{Duration, Instant};

/// Encodes captured frames as quality-86 MJPEG, matching the existing recorder's settings.
pub struct RecordingWriter {
    avi: MjpegAviWriter,
    width: u16,
    height: u16,
    jpeg: Vec<u8>,
}

impl RecordingWriter {
    pub fn create(path: impl AsRef<Path>, width: u16, height: u16, fps: u32) -> io::Result<Self> {
        Ok(Self {
            avi: MjpegAviWriter::create(path, width, height, fps)?,
            width,
            height,
            jpeg: Vec::new(),
        })
    }

    pub fn write_frame(&mut self, frame: &CapturedFrame) -> io::Result<()> {
        let bounds = frame.bounds();
        if bounds.width != i32::from(self.width) || bounds.height != i32::from(self.height) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "recording frame dimensions changed",
            ));
        }
        self.jpeg.clear();
        Encoder::new(&mut self.jpeg, 86)
            .encode(frame.pixels(), self.width, self.height, ColorType::Bgra)
            .map_err(io::Error::other)?;
        self.avi.write_frame(&self.jpeg)
    }

    pub fn frame_count(&self) -> usize {
        self.avi.frame_count()
    }
    pub fn current_file_size_bytes(&mut self) -> io::Result<u64> {
        self.avi.current_file_size_bytes()
    }
    pub fn finish(self) -> io::Result<()> {
        self.avi.finish()
    }
}

/// Crops one frame at a time and writes a new recording without replacing a file.
pub fn crop_video(
    source: impl AsRef<Path>,
    destination: impl AsRef<Path>,
    crop: Rect,
) -> io::Result<()> {
    let mut input = MjpegAviReader::open(source)?;
    let left = i64::from(crop.x).max(0);
    let top = i64::from(crop.y).max(0);
    let right = (i64::from(crop.x) + i64::from(crop.width)).min(i64::from(input.width));
    let bottom = (i64::from(crop.y) + i64::from(crop.height)).min(i64::from(input.height));
    if crop.width <= 0 || crop.height <= 0 || right <= left || bottom <= top {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "video crop does not intersect the recording",
        ));
    }
    let width = u16::try_from(right - left).map_err(io::Error::other)?;
    let height = u16::try_from(bottom - top).map_err(io::Error::other)?;
    let mut output = RecordingWriter::create(destination, width, height, input.frames_per_second)?;
    while let Some(jpeg) = input.read_frame()? {
        use image::ImageDecoder;
        let mut decoder = image::codecs::jpeg::JpegDecoder::new(std::io::Cursor::new(&jpeg))
            .map_err(io::Error::other)?;
        if decoder.dimensions() != (input.width, input.height) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "recording frame dimensions do not match its header",
            ));
        }
        decoder
            .set_limits(image::Limits::default())
            .map_err(io::Error::other)?;
        let image = image::DynamicImage::from_decoder(decoder).map_err(io::Error::other)?;
        let mut pixels = image
            .crop_imm(left as u32, top as u32, width as u32, height as u32)
            .into_rgba8()
            .into_raw();
        for pixel in pixels.chunks_exact_mut(4) {
            pixel.swap(0, 2);
        }
        output.write_frame(&CapturedFrame::new(
            Rect {
                x: 0,
                y: 0,
                width: i32::from(width),
                height: i32::from(height),
            },
            pixels,
        )?)?;
    }
    output.finish()
}

/// Worker for the GDI recording path. The caller owns the output file and must
/// keep it private until the review has accepted the recording.
#[cfg(windows)]
pub struct Recorder {
    stop: Sender<()>,
    worker: Option<JoinHandle<io::Result<usize>>>,
}

#[cfg(windows)]
impl Recorder {
    pub fn start(
        path: impl AsRef<Path>,
        bounds: crate::capture::Rect,
        fps: u32,
    ) -> io::Result<Self> {
        let width = u16::try_from(bounds.width)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid recording width"))?;
        let height = u16::try_from(bounds.height)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "invalid recording height"))?;
        let recording = RecordingWriter::create(path, width, height, fps)?;
        let (stop, receiver) = mpsc::channel();
        let worker = thread::Builder::new()
            .name("OpenCapture recorder".into())
            .spawn(move || {
                let mut recording = recording;
                let period = Duration::from_nanos(1_000_000_000 / u64::from(fps));
                let mut deadline = Instant::now();
                let outcome = loop {
                    if receiver.try_recv().is_ok() {
                        break Ok(());
                    }
                    let result = crate::capture::capture_rectangle(bounds)
                        .and_then(|frame| recording.write_frame(&frame));
                    if let Err(error) = result {
                        break Err(error);
                    }
                    deadline += period;
                    let now = Instant::now();
                    if deadline > now {
                        match receiver.recv_timeout(deadline - now) {
                            Ok(()) | Err(mpsc::RecvTimeoutError::Disconnected) => break Ok(()),
                            Err(mpsc::RecvTimeoutError::Timeout) => {}
                        }
                    } else if now.duration_since(deadline) > period * 3 {
                        deadline = now;
                    }
                };
                let frames = recording.frame_count();
                let finish = recording.finish();
                outcome.and(finish).map(|_| frames)
            })?;
        Ok(Self {
            stop,
            worker: Some(worker),
        })
    }

    pub fn stop(mut self) -> io::Result<usize> {
        self.finish()
    }

    fn finish(&mut self) -> io::Result<usize> {
        let Some(worker) = self.worker.take() else {
            return Ok(0);
        };
        let _ = self.stop.send(());
        worker
            .join()
            .map_err(|_| io::Error::other("recording worker panicked"))?
    }
}

#[cfg(windows)]
impl Drop for Recorder {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}

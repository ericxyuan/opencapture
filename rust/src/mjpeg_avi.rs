use std::fs::{File, OpenOptions};
use std::io::{self, Read, Seek, SeekFrom, Write};
use std::path::Path;

const MAX_FILE_BYTES: u64 = 1900 * 1024 * 1024;

/// AVI 1.0 MJPEG container. Frames passed to this writer must already be JPEGs.
pub struct MjpegAviWriter {
    file: File,
    index: Vec<(u32, u32)>,
    total_frames: u64,
    stream_length: u64,
    suggested_buffer: u64,
    stream_suggested_buffer: u64,
    movi_size: u64,
    movi_type: u64,
    largest_frame: u32,
}

impl MjpegAviWriter {
    pub fn create(path: impl AsRef<Path>, width: u16, height: u16, fps: u32) -> io::Result<Self> {
        if width == 0
            || height == 0
            || width > i16::MAX as u16
            || height > i16::MAX as u16
            || !(1..=120).contains(&fps)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid AVI dimensions or frame rate",
            ));
        }
        let mut file = OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(path)?;
        file.write_all(b"RIFF\0\0\0\0AVI LIST")?;
        let header_size = position(&mut file)?;
        write_u32(&mut file, 0)?;
        file.write_all(b"hdrlavih")?;
        for value in [
            56,
            (1_000_000.0_f64 / f64::from(fps)).round_ties_even() as u32,
            0,
            0,
            0x10,
        ] {
            write_u32(&mut file, value)?;
        }
        let total_frames = position(&mut file)?;
        for value in [0, 0, 1] {
            write_u32(&mut file, value)?;
        }
        let suggested_buffer = position(&mut file)?;
        for value in [0, u32::from(width), u32::from(height), 0, 0, 0, 0] {
            write_u32(&mut file, value)?;
        }
        file.write_all(b"LIST")?;
        let stream_list_size = position(&mut file)?;
        write_u32(&mut file, 0)?;
        file.write_all(b"strlstrh")?;
        write_u32(&mut file, 56)?;
        file.write_all(b"vidsMJPG")?;
        for value in [0, 0, 0, 1, fps, 0] {
            write_u32(&mut file, value)?;
        }
        let stream_length = position(&mut file)?;
        write_u32(&mut file, 0)?;
        let stream_suggested_buffer = position(&mut file)?;
        for value in [0, u32::MAX, 0, 0] {
            write_u32(&mut file, value)?;
        }
        file.write_all(&width.to_le_bytes())?;
        file.write_all(&height.to_le_bytes())?;
        file.write_all(b"strf")?;
        for value in [40, 40, u32::from(width), u32::from(height), 0x0018_0001] {
            write_u32(&mut file, value)?;
        }
        file.write_all(b"MJPG")?;
        for value in [u32::from(width) * u32::from(height) * 3, 0, 0, 0, 0] {
            write_u32(&mut file, value)?;
        }
        patch_list_size(&mut file, stream_list_size)?;
        patch_list_size(&mut file, header_size)?;
        file.write_all(b"LIST")?;
        let movi_size = position(&mut file)?;
        write_u32(&mut file, 0)?;
        let movi_type = position(&mut file)?;
        file.write_all(b"movi")?;
        Ok(Self {
            file,
            index: Vec::new(),
            total_frames,
            stream_length,
            suggested_buffer,
            stream_suggested_buffer,
            movi_size,
            movi_type,
            largest_frame: 0,
        })
    }

    pub fn frame_count(&self) -> usize {
        self.index.len()
    }

    pub fn current_file_size_bytes(&mut self) -> io::Result<u64> {
        Ok(position(&mut self.file)? + 8 + self.index.len() as u64 * 16)
    }

    pub fn write_frame(&mut self, jpeg: &[u8]) -> io::Result<()> {
        let position = position(&mut self.file)?;
        if jpeg.is_empty()
            || position + jpeg.len() as u64 + 32 + (self.index.len() as u64 + 1) * 16
                >= MAX_FILE_BYTES
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "empty frame or AVI size limit reached",
            ));
        }
        self.file.write_all(b"00dc")?;
        write_u32(&mut self.file, jpeg.len() as u32)?;
        self.file.write_all(jpeg)?;
        if jpeg.len() % 2 != 0 {
            self.file.write_all(&[0])?;
        }
        self.index
            .push(((position - self.movi_type) as u32, jpeg.len() as u32));
        self.largest_frame = self.largest_frame.max(jpeg.len() as u32);
        Ok(())
    }

    /// Finalizes the index and flushes the completed file to disk.
    pub fn finish(mut self) -> io::Result<()> {
        let movi_end = position(&mut self.file)?;
        patch(
            &mut self.file,
            self.movi_size,
            (movi_end - self.movi_size - 4) as u32,
        )?;
        self.file.write_all(b"idx1")?;
        write_u32(&mut self.file, (self.index.len() * 16) as u32)?;
        for (offset, size) in &self.index {
            self.file.write_all(b"00dc")?;
            for value in [0x10, *offset, *size] {
                write_u32(&mut self.file, value)?;
            }
        }
        let end = position(&mut self.file)?;
        for (offset, value) in [
            (self.total_frames, self.index.len() as u32),
            (self.stream_length, self.index.len() as u32),
            (self.suggested_buffer, self.largest_frame),
            (self.stream_suggested_buffer, self.largest_frame),
            (4, (end - 8) as u32),
        ] {
            patch(&mut self.file, offset, value)?;
        }
        self.file.flush()?;
        self.file.sync_all()
    }
}

fn position(file: &mut File) -> io::Result<u64> {
    file.stream_position()
}
fn write_u32(file: &mut File, value: u32) -> io::Result<()> {
    file.write_all(&value.to_le_bytes())
}
fn patch(file: &mut File, offset: u64, value: u32) -> io::Result<()> {
    let end = position(file)?;
    file.seek(SeekFrom::Start(offset))?;
    write_u32(file, value)?;
    file.seek(SeekFrom::Start(end))?;
    Ok(())
}
fn patch_list_size(file: &mut File, offset: u64) -> io::Result<()> {
    let end = position(file)?;
    patch(file, offset, (end - offset - 4) as u32)
}

/// Reads the JPEG frames from an OpenCapture AVI without loading the whole video.
pub struct MjpegAviReader {
    file: File,
    frames_end: u64,
    pub width: u32,
    pub height: u32,
    pub frame_count: u32,
    pub frames_per_second: u32,
}

impl MjpegAviReader {
    pub fn open(path: impl AsRef<Path>) -> io::Result<Self> {
        let mut file = File::open(path)?;
        let end = file.metadata()?.len();
        if read_fourcc(&mut file)? != *b"RIFF" {
            return Err(invalid_recording());
        }
        let _riff_size = read_u32(&mut file)?;
        if read_fourcc(&mut file)? != *b"AVI " {
            return Err(invalid_recording());
        }
        let mut metadata = AviMetadata::default();
        read_chunks(&mut file, end, &mut metadata, 0)?;
        if metadata.width == 0 || metadata.height == 0 || metadata.frames_start == 0 {
            return Err(invalid_recording());
        }
        file.seek(SeekFrom::Start(metadata.frames_start))?;
        Ok(Self {
            file,
            frames_end: metadata.frames_end,
            width: metadata.width,
            height: metadata.height,
            frame_count: metadata.frame_count,
            frames_per_second: metadata.fps,
        })
    }

    pub fn read_frame(&mut self) -> io::Result<Option<Vec<u8>>> {
        while position(&mut self.file)? + 8 <= self.frames_end {
            let id = read_fourcc(&mut self.file)?;
            let length = read_u32(&mut self.file)? as u64;
            let data = position(&mut self.file)?;
            let next = data + length + (length & 1);
            if next > self.frames_end || length > 64 * 1024 * 1024 {
                return Err(invalid_recording());
            }
            if id == *b"00dc" {
                let mut bytes = vec![0; length as usize];
                self.file.read_exact(&mut bytes)?;
                self.file.seek(SeekFrom::Start(next))?;
                return Ok(Some(bytes));
            }
            self.file.seek(SeekFrom::Start(next))?;
        }
        Ok(None)
    }
}

#[derive(Default)]
struct AviMetadata {
    width: u32,
    height: u32,
    frame_count: u32,
    fps: u32,
    frames_start: u64,
    frames_end: u64,
}

fn read_chunks(file: &mut File, end: u64, info: &mut AviMetadata, depth: u8) -> io::Result<()> {
    if depth > 2 {
        return Err(invalid_recording());
    }
    while position(file)? + 8 <= end {
        let id = read_fourcc(file)?;
        let length = read_u32(file)? as u64;
        let data = position(file)?;
        let next = data + length + (length & 1);
        if next > end {
            return Err(invalid_recording());
        }
        if id == *b"LIST" && length >= 4 {
            let kind = read_fourcc(file)?;
            if kind == *b"movi" {
                info.frames_start = position(file)?;
                info.frames_end = data + length;
            } else if kind == *b"hdrl" || kind == *b"strl" {
                read_chunks(file, data + length, info, depth + 1)?;
            }
        } else if id == *b"avih" && length >= 40 {
            let microseconds = read_u32(file)?;
            info.fps = if microseconds == 0 {
                60
            } else {
                (1_000_000.0 / f64::from(microseconds)).round_ties_even() as u32
            };
            file.seek(SeekFrom::Start(data + 16))?;
            info.frame_count = read_u32(file)?;
            file.seek(SeekFrom::Start(data + 32))?;
            info.width = read_u32(file)?;
            info.height = read_u32(file)?;
        } else if id == *b"strh" && length >= 28 && read_fourcc(file)? == *b"vids" {
            file.seek(SeekFrom::Start(data + 20))?;
            let scale = read_u32(file)?;
            let rate = read_u32(file)?;
            if scale > 0 {
                info.fps = (f64::from(rate) / f64::from(scale)).round_ties_even() as u32;
            }
        }
        file.seek(SeekFrom::Start(next))?;
    }
    Ok(())
}

fn read_fourcc(file: &mut File) -> io::Result<[u8; 4]> {
    let mut bytes = [0; 4];
    file.read_exact(&mut bytes)?;
    Ok(bytes)
}
fn read_u32(file: &mut File) -> io::Result<u32> {
    Ok(u32::from_le_bytes(read_fourcc(file)?))
}
fn invalid_recording() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "invalid or incomplete AVI recording",
    )
}

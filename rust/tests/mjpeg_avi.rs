use opencapture_core::mjpeg_avi::{MjpegAviReader, MjpegAviWriter};
use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

fn temp_path() -> PathBuf {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    std::env::temp_dir().join(format!(
        "opencapture-rust-{}-{}.avi",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ))
}

#[test]
fn matches_the_existing_writer_byte_for_byte() {
    let path = temp_path();
    let mut writer = MjpegAviWriter::create(&path, 16, 12, 60).unwrap();
    writer.write_frame(&[0xff, 0xd8, 0x01, 0xff, 0xd9]).unwrap();
    writer.write_frame(&[0xff, 0xd8, 0xff, 0xd9]).unwrap();
    assert_eq!(writer.frame_count(), 2);
    let expected_length = writer.current_file_size_bytes().unwrap();
    writer.finish().unwrap();
    let bytes = fs::read(&path).unwrap();
    fs::remove_file(path).unwrap();
    assert_eq!(bytes.len() as u64, expected_length);
    assert_eq!(
        bytes.as_slice(),
        include_bytes!("fixtures/csharp-mjpeg-16x12.bin")
    );
}

#[test]
fn rejects_invalid_parameters_and_preserves_existing_files() {
    let path = temp_path();
    for (width, height, fps) in [
        (0, 12, 60),
        (16, 0, 60),
        (32768, 12, 60),
        (16, 12, 0),
        (16, 12, 121),
    ] {
        assert!(MjpegAviWriter::create(&path, width, height, fps).is_err());
        assert!(!path.exists());
    }
    fs::write(&path, b"existing data").unwrap();
    assert!(MjpegAviWriter::create(&path, 16, 12, 60).is_err());
    assert_eq!(fs::read(&path).unwrap(), b"existing data");
    fs::remove_file(path).unwrap();
}

#[test]
fn rejects_empty_frames_without_changing_the_recording() {
    let path = temp_path();
    let mut writer = MjpegAviWriter::create(&path, 16, 12, 60).unwrap();
    let size = writer.current_file_size_bytes().unwrap();
    assert!(writer.write_frame(&[]).is_err());
    assert_eq!(writer.frame_count(), 0);
    assert_eq!(writer.current_file_size_bytes().unwrap(), size);
    writer.finish().unwrap();
    fs::remove_file(path).unwrap();
}

#[test]
fn reads_the_existing_writers_recording_frame_by_frame() {
    let path = temp_path();
    fs::write(&path, include_bytes!("fixtures/csharp-mjpeg-16x12.bin")).unwrap();
    let mut reader = MjpegAviReader::open(&path).unwrap();
    assert_eq!(
        (
            reader.width,
            reader.height,
            reader.frame_count,
            reader.frames_per_second
        ),
        (16, 12, 2, 60)
    );
    assert_eq!(
        reader.read_frame().unwrap().unwrap(),
        [0xff, 0xd8, 0x01, 0xff, 0xd9]
    );
    assert_eq!(
        reader.read_frame().unwrap().unwrap(),
        [0xff, 0xd8, 0xff, 0xd9]
    );
    assert!(reader.read_frame().unwrap().is_none());
    drop(reader);
    fs::remove_file(path).unwrap();
}

#[test]
fn rejects_truncated_and_invalid_recordings() {
    let path = temp_path();
    let reference = include_bytes!("fixtures/csharp-mjpeg-16x12.bin");
    fs::write(&path, &reference[..reference.len() - 10]).unwrap();
    assert!(MjpegAviReader::open(&path).is_err());
    fs::write(&path, b"not an AVI file").unwrap();
    assert!(MjpegAviReader::open(&path).is_err());
    fs::remove_file(path).unwrap();
}

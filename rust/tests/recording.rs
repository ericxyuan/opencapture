use opencapture_core::capture::{CapturedFrame, Rect};
use opencapture_core::mjpeg_avi::MjpegAviReader;
use opencapture_core::recording::RecordingWriter;

#[test]
fn writes_encoded_frames_and_rejects_a_changed_size() {
    let path = std::env::temp_dir().join(format!(
        "opencapture-rust-recording-{}.avi",
        std::process::id()
    ));
    let mut recording = RecordingWriter::create(&path, 16, 12, 60).unwrap();
    let frame = CapturedFrame::new(
        Rect {
            x: -10,
            y: 2,
            width: 16,
            height: 12,
        },
        vec![255; 16 * 12 * 4],
    )
    .unwrap();
    let wrong = CapturedFrame::new(
        Rect {
            x: 0,
            y: 0,
            width: 2,
            height: 2,
        },
        vec![255; 16],
    )
    .unwrap();
    assert!(recording.write_frame(&wrong).is_err());
    recording.write_frame(&frame).unwrap();
    recording.write_frame(&frame).unwrap();
    assert_eq!(recording.frame_count(), 2);
    let size = recording.current_file_size_bytes().unwrap();
    recording.finish().unwrap();
    assert_eq!(std::fs::metadata(&path).unwrap().len(), size);
    let mut reader = MjpegAviReader::open(&path).unwrap();
    assert_eq!(
        (
            reader.width,
            reader.height,
            reader.frames_per_second,
            reader.frame_count
        ),
        (16, 12, 60, 2)
    );
    for _ in 0..2 {
        let jpeg = reader.read_frame().unwrap().unwrap();
        assert!(jpeg.starts_with(&[0xff, 0xd8]));
        assert!(jpeg.ends_with(&[0xff, 0xd9]));
    }
    assert!(reader.read_frame().unwrap().is_none());
    drop(reader);
    std::fs::remove_file(path).unwrap();
}

#[cfg(windows)]
#[test]
fn recorder_captures_a_small_region_and_stops_with_a_valid_index() {
    use opencapture_core::recording::Recorder;
    let path = std::env::temp_dir().join(format!(
        "opencapture-rust-worker-{}.avi",
        std::process::id()
    ));
    let bounds = opencapture_core::capture::capture_virtual_desktop()
        .unwrap()
        .bounds();
    let recorder = Recorder::start(
        &path,
        Rect {
            x: bounds.x,
            y: bounds.y,
            width: 8,
            height: 8,
        },
        20,
    )
    .unwrap();
    std::thread::sleep(std::time::Duration::from_millis(300));
    let frames = recorder.stop().unwrap();
    assert!(frames > 0);
    let mut reader = MjpegAviReader::open(&path).unwrap();
    assert_eq!(reader.frame_count as usize, frames);
    assert!(reader.read_frame().unwrap().is_some());
    drop(reader);
    std::fs::remove_file(path).unwrap();
}

use opencapture_core::capture::{CapturedFrame, Rect};
use opencapture_core::mjpeg_avi::MjpegAviReader;
use opencapture_core::recording::{crop_video, RecordingWriter};

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

#[test]
fn crops_a_recording_without_overwriting_existing_files() {
    let path = std::env::temp_dir().join(format!(
        "opencapture-rust-crop-source-{}.avi",
        std::process::id()
    ));
    let dest = std::env::temp_dir().join(format!(
        "opencapture-rust-crop-output-{}.avi",
        std::process::id()
    ));
    let frame = CapturedFrame::new(
        Rect {
            x: 0,
            y: 0,
            width: 16,
            height: 12,
        },
        [10, 100, 220, 255].repeat(16 * 12),
    )
    .unwrap();
    let mut source = RecordingWriter::create(&path, 16, 12, 60).unwrap();
    source.write_frame(&frame).unwrap();
    source.write_frame(&frame).unwrap();
    source.finish().unwrap();
    let crop = Rect {
        x: -2,
        y: 2,
        width: 8,
        height: 8,
    };
    crop_video(&path, &dest, crop).unwrap();
    let mut reader = MjpegAviReader::open(&dest).unwrap();
    assert_eq!(
        (
            reader.width,
            reader.height,
            reader.frame_count,
            reader.frames_per_second
        ),
        (6, 8, 2, 60)
    );
    let jpeg = reader.read_frame().unwrap().unwrap();
    let decoded = image::load_from_memory_with_format(&jpeg, image::ImageFormat::Jpeg)
        .unwrap()
        .to_rgb8();
    let pixel = decoded.get_pixel(2, 2).0;
    assert!(
        (i32::from(pixel[0]) - 220).abs() < 15
            && (i32::from(pixel[1]) - 100).abs() < 15
            && (i32::from(pixel[2]) - 10).abs() < 15,
        "{pixel:?}"
    );
    assert!(reader.read_frame().unwrap().is_some());
    assert!(reader.read_frame().unwrap().is_none());
    drop(reader);
    let before = std::fs::read(&dest).unwrap();
    assert!(crop_video(&path, &dest, crop).is_err());
    assert_eq!(std::fs::read(&dest).unwrap(), before);
    std::fs::remove_file(path).unwrap();
    std::fs::remove_file(dest).unwrap();
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

use opencapture_core::capture::{CapturedFrame, Rect};
use opencapture_core::review::{Decision, Review};
use opencapture_core::screenshot::save_accepted;
use std::time::Duration;

#[test]
fn screenshot_is_saved_only_on_acceptance_and_never_overwrites() {
    let path = std::env::temp_dir().join(format!(
        "opencapture-rust-screenshot-{}.png",
        std::process::id()
    ));
    let frame = CapturedFrame::new(
        Rect {
            x: -4,
            y: 6,
            width: 2,
            height: 1,
        },
        vec![1, 2, 3, 0, 4, 5, 6, 0],
    )
    .unwrap();
    let mut review = Review::new(Duration::ZERO);
    assert!(save_accepted(&frame, &review, &path).is_err());
    assert!(!path.exists());
    review.decide(Decision::Accept);
    save_accepted(&frame, &review, &path).unwrap();
    let bytes = std::fs::read(&path).unwrap();
    let image = image::load_from_memory_with_format(&bytes, image::ImageFormat::Png)
        .unwrap()
        .to_rgb8();
    assert_eq!(image.get_pixel(0, 0).0, [3, 2, 1]);
    assert_eq!(image.get_pixel(1, 0).0, [6, 5, 4]);
    assert!(save_accepted(&frame, &review, &path).is_err());
    assert_eq!(std::fs::read(&path).unwrap(), bytes);
    std::fs::remove_file(path).unwrap();
}

#[test]
fn discarded_screenshot_cannot_be_saved() {
    let path = std::env::temp_dir().join(format!(
        "opencapture-rust-discard-{}.png",
        std::process::id()
    ));
    let frame = CapturedFrame::new(
        Rect {
            x: 0,
            y: 0,
            width: 1,
            height: 1,
        },
        vec![0; 4],
    )
    .unwrap();
    let mut review = Review::new(Duration::ZERO);
    review.decide(Decision::Discard);
    assert!(save_accepted(&frame, &review, &path).is_err());
    assert!(!path.exists());
}

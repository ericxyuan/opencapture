use crate::capture::CapturedFrame;
use crate::review::{Decision, Review};
use image::ImageEncoder;
use std::fs::OpenOptions;
use std::io::{self, BufWriter, Write};
use std::path::Path;

/// Writes a screenshot only after the review accepts it; never replaces an existing file.
pub fn save_accepted(
    frame: &CapturedFrame,
    review: &Review,
    path: impl AsRef<Path>,
) -> io::Result<()> {
    if review.decision() != Some(Decision::Accept) {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "screenshot has not been accepted",
        ));
    }
    let mut rgb = Vec::with_capacity(frame.pixels().len() / 4 * 3);
    for pixel in frame.pixels().chunks_exact(4) {
        rgb.extend_from_slice(&[pixel[2], pixel[1], pixel[0]]);
    }
    let file = OpenOptions::new().write(true).create_new(true).open(path)?;
    let mut output = BufWriter::new(file);
    let bounds = frame.bounds();
    image::codecs::png::PngEncoder::new(&mut output)
        .write_image(
            &rgb,
            bounds.width as u32,
            bounds.height as u32,
            image::ExtendedColorType::Rgb8,
        )
        .map_err(io::Error::other)?;
    output.flush()?;
    output.get_ref().sync_all()
}

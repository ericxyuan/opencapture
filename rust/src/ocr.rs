use crate::capture::CapturedFrame;
use windows::Graphics::Imaging::{BitmapAlphaMode, BitmapPixelFormat, SoftwareBitmap};
use windows::Media::Ocr::OcrEngine;
use windows::Storage::Streams::DataWriter;
use windows::Win32::System::WinRT::{RoInitialize, RoUninitialize, RO_INIT_MULTITHREADED};

#[derive(Debug)]
pub struct Word {
    pub text: String,
    pub bounds: windows::Foundation::Rect,
    pub line: u32,
}

#[derive(Debug)]
pub struct Recognition {
    pub text: String,
    pub words: Vec<Word>,
}

/// Runs local Windows OCR on the caller's worker thread using the user's OCR language.
/// The thread must not already be initialized as a single-threaded COM apartment.
pub fn recognize(frame: &CapturedFrame) -> windows::core::Result<Recognition> {
    unsafe {
        RoInitialize(RO_INIT_MULTITHREADED)?;
    }
    struct Apartment;
    impl Drop for Apartment {
        fn drop(&mut self) {
            unsafe { RoUninitialize() }
        }
    }
    let _apartment = Apartment;
    let engine = OcrEngine::TryCreateFromUserProfileLanguages()?;
    let writer = DataWriter::new()?;
    writer.WriteBytes(frame.pixels())?;
    let buffer = writer.DetachBuffer()?;
    let bounds = frame.bounds();
    let bitmap = SoftwareBitmap::CreateCopyWithAlphaFromBuffer(
        &buffer,
        BitmapPixelFormat::Bgra8,
        bounds.width,
        bounds.height,
        BitmapAlphaMode::Ignore,
    )?;
    let result = engine.RecognizeAsync(&bitmap)?.get()?;
    let lines = result.Lines()?;
    let mut words = Vec::new();
    for index in 0..lines.Size()? {
        let line = lines.GetAt(index)?;
        let entries = line.Words()?;
        for word_index in 0..entries.Size()? {
            let word = entries.GetAt(word_index)?;
            words.push(Word {
                text: word.Text()?.to_string(),
                bounds: word.BoundingRect()?,
                line: index,
            });
        }
    }
    Ok(Recognition {
        text: result.Text()?.to_string(),
        words,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::capture::Rect;
    #[test]
    fn recognizes_a_blank_synthetic_frame() {
        let frame = CapturedFrame::new(
            Rect {
                x: 0,
                y: 0,
                width: 128,
                height: 64,
            },
            vec![255; 128 * 64 * 4],
        )
        .unwrap();
        let result = recognize(&frame).expect("local Windows OCR");
        assert!(result.text.trim().is_empty());
        assert!(result.words.is_empty());
    }

    #[test]
    fn recognizes_synthetic_text_and_word_bounds() {
        let packed = include_bytes!("../tests/fixtures/synthetic-text-320x80.bin");
        let mut pixels = Vec::with_capacity(320 * 80 * 4);
        for i in 0..320 * 80 {
            let value = if packed[i / 8] & (1 << (i % 8)) == 0 {
                255
            } else {
                0
            };
            pixels.extend_from_slice(&[value, value, value, 255]);
        }
        let frame = CapturedFrame::new(
            Rect {
                x: 0,
                y: 0,
                width: 320,
                height: 80,
            },
            pixels,
        )
        .unwrap();
        let result = recognize(&frame).expect("local Windows OCR");
        assert!(
            result.text.to_uppercase().contains("CAPTURE"),
            "{}",
            result.text
        );
        assert!(result
            .words
            .iter()
            .any(|word| word.text.to_uppercase() == "CAPTURE"
                && word.bounds.Width > 0.0
                && word.bounds.Height > 0.0));
    }
}

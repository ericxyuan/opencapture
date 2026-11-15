use crate::capture::CapturedFrame;
use crate::ocr_preprocess::{prepare, threshold};
use std::collections::HashSet;
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

/// Runs local Windows OCR on the caller's worker thread using installed OCR languages.
/// The thread must not already be initialized as a single-threaded COM apartment.
pub fn recognize(frame: &CapturedFrame) -> windows::core::Result<Recognition> {
    recognize_while(frame, || true)
}

/// Stops between OCR calls when the selection is no longer current.
pub fn recognize_while(
    frame: &CapturedFrame,
    is_current: impl Fn() -> bool,
) -> windows::core::Result<Recognition> {
    if !is_current() {
        return Ok(empty());
    }
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
    let mut engines = Vec::new();
    let mut tags = HashSet::new();
    let mut add = |engine: OcrEngine| {
        if let Ok(language) = engine.RecognizerLanguage() {
            if let Ok(tag) = language.LanguageTag() {
                if tags.insert(tag.to_string().to_lowercase()) {
                    engines.push(engine);
                }
            }
        }
    };
    if let Ok(engine) = OcrEngine::TryCreateFromUserProfileLanguages() {
        add(engine);
    }
    let languages = OcrEngine::AvailableRecognizerLanguages()?;
    for i in 0..languages.Size()? {
        if let Ok(engine) = OcrEngine::TryCreateFromLanguage(&languages.GetAt(i)?) {
            add(engine);
        }
    }
    if engines.is_empty() {
        return Err(windows::core::Error::new(
            windows::core::HRESULT(0x80004005u32 as i32),
            "No Windows OCR language pack is available".into(),
        ));
    }
    let prepared = prepare(frame, OcrEngine::MaxImageDimension()?);
    let bounds = frame.bounds();
    let mut best: Option<(i32, Recognition)> = None;
    let mut last_error = None;
    for variant in 0..4 {
        if !is_current() {
            return Ok(empty());
        }
        let pixels = match variant {
            0 => prepared.colour.clone(),
            1 => prepared.grey.clone(),
            2 => threshold(&prepared.grey, prepared.threshold, false),
            _ => threshold(&prepared.grey, prepared.threshold, true),
        };
        let writer = DataWriter::new()?;
        writer.WriteBytes(&pixels)?;
        let bitmap = SoftwareBitmap::CreateCopyWithAlphaFromBuffer(
            &writer.DetachBuffer()?,
            BitmapPixelFormat::Bgra8,
            prepared.width as i32,
            prepared.height as i32,
            BitmapAlphaMode::Ignore,
        )?;
        for engine in &engines {
            if !is_current() {
                return Ok(empty());
            }
            let result = engine
                .RecognizeAsync(&bitmap)
                .and_then(|operation| operation.get())
                .and_then(|result| {
                    recognition(
                        &result,
                        bounds.width as f64 / f64::from(prepared.width),
                        bounds.height as f64 / f64::from(prepared.height),
                    )
                });
            match result {
                Ok(candidate) => {
                    let score = score(&candidate.text);
                    if score > 0 && best.as_ref().is_none_or(|(previous, _)| score > *previous) {
                        best = Some((score, candidate));
                    }
                }
                Err(error) => last_error = Some(error),
            }
        }
    }
    if let Some((_, result)) = best {
        return Ok(result);
    }
    if let Some(error) = last_error {
        return Err(error);
    }
    Ok(empty())
}

fn empty() -> Recognition {
    Recognition {
        text: String::new(),
        words: Vec::new(),
    }
}

fn recognition(
    result: &windows::Media::Ocr::OcrResult,
    scale_x: f64,
    scale_y: f64,
) -> windows::core::Result<Recognition> {
    let lines = result.Lines()?;
    let mut words = Vec::new();
    for index in 0..lines.Size()? {
        let line = lines.GetAt(index)?;
        let entries = line.Words()?;
        for word_index in 0..entries.Size()? {
            let word = entries.GetAt(word_index)?;
            let mut bounds = word.BoundingRect()?;
            bounds.X *= scale_x as f32;
            bounds.Y *= scale_y as f32;
            bounds.Width *= scale_x as f32;
            bounds.Height *= scale_y as f32;
            words.push(Word {
                text: simplify(&normalize(&word.Text()?.to_string())),
                bounds,
                line: index,
            });
        }
    }
    Ok(Recognition {
        text: simplify(&normalize(&result.Text()?.to_string())),
        words,
    })
}

fn score(text: &str) -> i32 {
    let (mut score, mut count, mut inside) = (0, 0, false);
    for c in text.chars() {
        if c.is_alphanumeric() {
            score += 5;
            count += 1;
            if !inside {
                score += 3;
                inside = true;
            }
        } else if c.is_whitespace() {
            score += 1;
            inside = false;
        } else if c.is_control() {
            score -= 8;
        } else {
            score += 1;
        }
    }
    if count == 0 {
        0
    } else {
        score.max(0)
    }
}

fn compact(c: char) -> bool {
    matches!(c as u32, 0x3400..=0x4dbf | 0x4e00..=0x9fff | 0xf900..=0xfaff | 0x3040..=0x30ff | 0x1100..=0x11ff | 0xac00..=0xd7af)
}

fn normalize(text: &str) -> String {
    let chars: Vec<char> = text.trim().chars().collect();
    let mut result = String::new();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] != ' ' && chars[i] != '\t' {
            result.push(chars[i]);
            i += 1;
            continue;
        }
        while i < chars.len() && (chars[i] == ' ' || chars[i] == '\t') {
            i += 1;
        }
        if !(result.chars().last().is_some_and(compact)
            && chars.get(i).copied().is_some_and(compact))
        {
            result.push(' ');
        }
    }
    result
}

fn simplify(text: &str) -> String {
    use windows_sys::Win32::Globalization::{LCMapStringEx, LCMAP_SIMPLIFIED_CHINESE};
    let source: Vec<u16> = text.encode_utf16().collect();
    if source.is_empty() || source.len() > i32::MAX as usize {
        return text.to_owned();
    }
    let locale: Vec<u16> = "zh-CN\0".encode_utf16().collect();
    let required = unsafe {
        LCMapStringEx(
            locale.as_ptr(),
            LCMAP_SIMPLIFIED_CHINESE,
            source.as_ptr(),
            source.len() as i32,
            std::ptr::null_mut(),
            0,
            std::ptr::null(),
            std::ptr::null(),
            0,
        )
    };
    if required <= 0 {
        return text.to_owned();
    }
    let mut output = vec![0; required as usize];
    let written = unsafe {
        LCMapStringEx(
            locale.as_ptr(),
            LCMAP_SIMPLIFIED_CHINESE,
            source.as_ptr(),
            source.len() as i32,
            output.as_mut_ptr(),
            required,
            std::ptr::null(),
            std::ptr::null(),
            0,
        )
    };
    if written <= 0 {
        text.to_owned()
    } else {
        String::from_utf16_lossy(&output[..written as usize])
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::capture::Rect;
    #[test]
    fn normalizes_cjk_spaces_and_simplifies_traditional_chinese() {
        assert_eq!(
            normalize("  中 \t 文   Open   Capture  "),
            "中文 Open Capture"
        );
        assert_eq!(simplify("繁體中文"), "繁体中文");
        assert!(score("Open Capture 123") > score("123"));
        assert_eq!(score("..."), 0);
    }

    #[test]
    fn cancelled_requests_return_without_recognition() {
        let frame = CapturedFrame::new(
            Rect {
                x: 0,
                y: 0,
                width: 2,
                height: 2,
            },
            vec![0; 16],
        )
        .unwrap();
        let result = recognize_while(&frame, || false).unwrap();
        assert!(result.text.is_empty());
        assert!(result.words.is_empty());
    }
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
                && word.bounds.Height > 0.0
                && word.bounds.X + word.bounds.Width <= 320.0
                && word.bounds.Y + word.bounds.Height <= 80.0));
    }

    #[test]
    fn recognizes_light_text_on_dark_background() {
        let packed = include_bytes!("../tests/fixtures/synthetic-text-320x80.bin");
        let mut pixels = Vec::with_capacity(320 * 80 * 4);
        for i in 0..320 * 80 {
            let value = if packed[i / 8] & (1 << (i % 8)) == 0 {
                0
            } else {
                255
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
    }
}

use crate::capture::CapturedFrame;
use image::{imageops::FilterType, RgbaImage};

pub struct Prepared {
    pub width: u32,
    pub height: u32,
    pub colour: Vec<u8>,
    pub grey: Vec<u8>,
    pub threshold: u8,
}

pub fn prepare(frame: &CapturedFrame, max_dimension: u32) -> Prepared {
    let bounds = frame.bounds();
    let longest = bounds.width.max(bounds.height) as u32;
    let scale = (max_dimension as f64 / f64::from(longest)).min(3.0);
    let width = (f64::from(bounds.width) * scale).floor().max(1.0) as u32;
    let height = (f64::from(bounds.height) * scale).floor().max(1.0) as u32;
    let mut colour = frame.pixels().to_vec();
    for pixel in colour.chunks_exact_mut(4) {
        pixel[3] = 255;
    }
    let source = RgbaImage::from_raw(bounds.width as u32, bounds.height as u32, colour)
        .expect("valid capture pixels");
    let filter = if scale > 1.0 {
        FilterType::CatmullRom
    } else {
        FilterType::Triangle
    };
    let colour = image::imageops::resize(&source, width, height, filter).into_raw();
    let mut histogram = [0u64; 256];
    let mut grey = Vec::with_capacity(colour.len());
    for pixel in colour.chunks_exact(4) {
        let value = ((19 * u32::from(pixel[0])
            + 183 * u32::from(pixel[1])
            + 54 * u32::from(pixel[2])
            + 128)
            >> 8) as u8;
        histogram[value as usize] += 1;
        grey.extend_from_slice(&[value, value, value, 255]);
    }
    let tail = ((u64::from(width) * u64::from(height)) / 100).max(1);
    let mut sum = 0;
    let low = histogram
        .iter()
        .position(|count| {
            sum += count;
            sum >= tail
        })
        .unwrap_or(0);
    sum = 0;
    let high = histogram
        .iter()
        .rposition(|count| {
            sum += count;
            sum >= tail
        })
        .unwrap_or(255);
    if high.saturating_sub(low) >= 24 {
        histogram = [0; 256];
        for pixel in grey.chunks_exact_mut(4) {
            let value = ((i32::from(pixel[0]) - low as i32) * 255 / (high - low) as i32)
                .clamp(0, 255) as u8;
            pixel[..3].fill(value);
            histogram[value as usize] += 1;
        }
    }
    let threshold = otsu(&histogram);
    Prepared {
        width,
        height,
        colour,
        grey,
        threshold,
    }
}

fn otsu(histogram: &[u64; 256]) -> u8 {
    let total: u64 = histogram.iter().sum();
    let weighted: u64 = histogram
        .iter()
        .enumerate()
        .map(|(i, count)| i as u64 * count)
        .sum();
    let (mut background, mut background_weighted) = (0u64, 0u64);
    let (mut best, mut threshold) = (-1.0, 127);
    for (value, count) in histogram.iter().enumerate() {
        background += count;
        if background == 0 {
            continue;
        }
        let foreground = total - background;
        if foreground == 0 {
            break;
        }
        background_weighted += value as u64 * count;
        let delta = background_weighted as f64 / background as f64
            - (weighted - background_weighted) as f64 / foreground as f64;
        let variance = background as f64 * foreground as f64 * delta * delta;
        if variance > best {
            best = variance;
            threshold = value as u8;
        }
    }
    threshold
}

pub fn threshold(grey: &[u8], cutoff: u8, inverted: bool) -> Vec<u8> {
    let mut output = Vec::with_capacity(grey.len());
    for pixel in grey.chunks_exact(4) {
        let mut value = if pixel[0] <= cutoff { 0 } else { 255 };
        if inverted {
            value = 255 - value;
        }
        output.extend_from_slice(&[value, value, value, 255]);
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::capture::Rect;

    #[test]
    fn prepare_scales_and_thresholds_both_polarities() {
        let frame = CapturedFrame::new(
            Rect {
                x: 0,
                y: 0,
                width: 2,
                height: 1,
            },
            vec![0, 0, 0, 0, 255, 255, 255, 0],
        )
        .unwrap();
        let prepared = prepare(&frame, 2);
        assert_eq!((prepared.width, prepared.height), (2, 1));
        assert_eq!(prepared.threshold, 0);
        assert_eq!(
            threshold(&prepared.grey, prepared.threshold, false),
            vec![0, 0, 0, 255, 255, 255, 255, 255]
        );
        assert_eq!(
            threshold(&prepared.grey, prepared.threshold, true),
            vec![255, 255, 255, 255, 0, 0, 0, 255]
        );
    }
}

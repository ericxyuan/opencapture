//! Components being migrated from the existing Windows application.

pub mod capture;
pub mod mjpeg_avi;
#[cfg(windows)]
pub mod ocr;
#[cfg(windows)]
mod ocr_preprocess;
pub mod recording;
pub mod review;
pub mod screenshot;
pub mod shortcut;

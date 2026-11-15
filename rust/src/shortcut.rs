//! Shortcut values compatible with the existing Windows Forms registry format.

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Shortcut(Binding);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Binding {
    Keyboard { modifiers: i32, key: i32 },
    Mouse(i32),
}

impl Shortcut {
    pub fn keyboard(modifiers: i32, key: i32) -> Option<Self> {
        let key = key & 0xffff;
        if key == 0 || matches!(key, 0x10..=0x12 | 0xa0..=0xa5) {
            return None;
        }
        Some(Self(Binding::Keyboard {
            modifiers: modifiers & !0xffff,
            key,
        }))
    }

    pub fn parse(value: &str) -> Option<Self> {
        let mut parts = value.split(':');
        match (parts.next(), parts.next(), parts.next(), parts.next()) {
            (Some("M"), Some(button), None, None) => {
                let button: i32 = button.trim().parse().ok()?;
                match button {
                    1048576 | 2097152 | 4194304 | 8388608 | 16777216 => {
                        Some(Self(Binding::Mouse(button)))
                    }
                    _ => None,
                }
            }
            (Some("K"), Some(modifiers), Some(key), None) => {
                Self::keyboard(modifiers.trim().parse().ok()?, key.trim().parse().ok()?)
            }
            _ => None,
        }
    }

    pub fn serialize(self) -> String {
        match self.0 {
            Binding::Keyboard { modifiers, key } => format!("K:{modifiers}:{key}"),
            Binding::Mouse(button) => format!("M:{button}"),
        }
    }

    pub fn matches_key(self, key_data: i32) -> bool {
        match self.0 {
            Binding::Keyboard { modifiers, key } => {
                key_data & 0xffff == key && key_data & !0xffff == modifiers
            }
            Binding::Mouse(_) => false,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Shortcut;

    #[test]
    fn preserves_existing_default_shortcuts() {
        for value in ["K:262144:81", "K:131072:67", "M:1048576", "K:0:32"] {
            assert_eq!(Shortcut::parse(value).unwrap().serialize(), value);
        }
        assert!(Shortcut::parse("K:262144:81")
            .unwrap()
            .matches_key(262144 | 81));
        assert!(!Shortcut::parse("K:262144:81")
            .unwrap()
            .matches_key(131072 | 81));
    }

    #[test]
    fn rejects_invalid_bindings_and_normalizes_key_masks() {
        for value in [
            "", "K:0:0", "K:0:17", "K:0:160", "M:3", "K:0:81:0", "k:0:81",
        ] {
            assert!(Shortcut::parse(value).is_none(), "{value}");
        }
        assert_eq!(
            Shortcut::parse("K:262145:65617").unwrap().serialize(),
            "K:262144:81"
        );
    }
}

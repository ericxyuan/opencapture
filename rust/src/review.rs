use std::time::Duration;

const REVIEW_DURATION: Duration = Duration::from_secs(2);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Decision {
    Accept,
    Discard,
}

/// Timing and acceptance rules used by the capture review interface.
pub struct Review {
    started: Duration,
    editing: bool,
    decision: Option<Decision>,
}

impl Review {
    pub fn new(now: Duration) -> Self {
        Self {
            started: now,
            editing: false,
            decision: None,
        }
    }

    pub fn begin_crop(&mut self, can_crop: bool) -> bool {
        if self.editing || self.decision.is_some() || !can_crop {
            return false;
        }
        self.editing = true;
        true
    }

    pub fn end_crop(&mut self, now: Duration) {
        if self.editing {
            self.editing = false;
            self.started = now;
        }
    }

    pub fn click_away(&mut self) -> Option<Decision> {
        if !self.editing {
            self.decide(Decision::Accept);
        }
        self.decision
    }

    pub fn tick(
        &mut self,
        now: Duration,
        mouse_down: bool,
        action_pressed: bool,
    ) -> Option<Decision> {
        if !self.editing
            && !mouse_down
            && !action_pressed
            && now.saturating_sub(self.started) >= REVIEW_DURATION
        {
            self.decide(Decision::Accept);
        }
        self.decision
    }

    pub fn decide(&mut self, decision: Decision) {
        if self.decision.is_none() {
            self.editing = false;
            self.decision = Some(decision);
        }
    }

    pub fn remaining_fraction(&self, now: Duration) -> f64 {
        if self.editing {
            return 1.0;
        }
        (1.0 - now.saturating_sub(self.started).as_secs_f64() / REVIEW_DURATION.as_secs_f64())
            .clamp(0.0, 1.0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn ms(n: u64) -> Duration {
        Duration::from_millis(n)
    }

    #[test]
    fn expiry_waits_for_mouse_release_and_only_decides_once() {
        let mut review = Review::new(ms(100));
        assert_eq!(review.tick(ms(2099), false, false), None);
        assert_eq!(review.tick(ms(2100), true, false), None);
        assert_eq!(review.tick(ms(2101), false, true), None);
        assert_eq!(review.tick(ms(2102), false, false), Some(Decision::Accept));
        review.decide(Decision::Discard);
        assert_eq!(review.tick(ms(2200), false, false), Some(Decision::Accept));
    }

    #[test]
    fn cropping_pauses_acceptance_and_restarts_the_full_countdown() {
        let mut review = Review::new(ms(0));
        assert!(!review.begin_crop(false));
        assert!(review.begin_crop(true));
        assert_eq!(review.tick(ms(9000), false, false), None);
        assert_eq!(review.click_away(), None);
        assert_eq!(review.remaining_fraction(ms(9000)), 1.0);
        review.end_crop(ms(9000));
        assert_eq!(review.tick(ms(10999), false, false), None);
        assert_eq!(review.tick(ms(11000), false, false), Some(Decision::Accept));
    }

    #[test]
    fn discard_and_click_away_have_distinct_outcomes() {
        let mut discarded = Review::new(ms(0));
        discarded.decide(Decision::Discard);
        assert_eq!(discarded.click_away(), Some(Decision::Discard));
        let mut accepted = Review::new(ms(0));
        assert_eq!(accepted.click_away(), Some(Decision::Accept));
    }
}

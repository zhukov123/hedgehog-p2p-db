use hedgehog_types::labels::ALL_LABEL_GROUPS;
use std::collections::HashSet;

#[test]
fn wire_labels_are_lowercase_path_safe() {
    for group in ALL_LABEL_GROUPS {
        for label in *group {
            assert!(
                label
                    .wire
                    .chars()
                    .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '_'),
                "bad wire label {:?}",
                label
            );
        }
    }
}

#[test]
fn labels_are_unique_within_domain() {
    for group in ALL_LABEL_GROUPS {
        let mut seen = HashSet::new();
        for label in *group {
            assert!(seen.insert(label.wire), "duplicate label in {}: {}", label.domain, label.wire);
        }
    }
}


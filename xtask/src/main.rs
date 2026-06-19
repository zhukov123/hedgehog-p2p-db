use std::{env, fs, process};

const REQUIRED_DOCS: &[&str] = &[
    "README.md",
    "p2p-object-store-guide.md",
    "p2p-object-store-key-model.md",
    "p2p-object-store-sqlite-schema-plan.md",
    "p2p-nosql-implementation-contract.md",
    "p2p-nosql-scaffold-contract.md",
];

const REQUIRED_PHRASES: &[(&str, &str)] = &[
    ("p2p-object-store-key-model.md", "object_lookup_hash = HMAC-SHA256(dataset_lookup_key, normalized_object_name)"),
    ("p2p-object-store-sqlite-schema-plan.md", "object_lookup_hash blob not null"),
    ("p2p-nosql-implementation-contract.md", "Use `sqlx` with SQLite for v1-alpha."),
    ("p2p-nosql-scaffold-contract-part-01.md", "hedgehog-metadata-sql"),
];

const QUARANTINED_TOKENS: &[&str] = &[
    "write_intent",
    "COMMIT_PENDING",
    "AVAILABLE",
    "TRANSFER_ASSIGNED",
    "UPLOADING",
];

fn main() {
    let mut args = env::args().skip(1);
    match args.next().as_deref() {
        Some("validate-scaffold-contract") => validate(),
        _ => {
            eprintln!("usage: cargo run -p xtask -- validate-scaffold-contract");
            process::exit(2);
        }
    }
}

fn validate() {
    let mut failures = Vec::new();

    for path in REQUIRED_DOCS {
        if fs::metadata(path).is_err() {
            failures.push(format!("missing required doc: {path}"));
        }
    }

    for (path, phrase) in REQUIRED_PHRASES {
        match fs::read_to_string(path) {
            Ok(text) if text.contains(phrase) => {}
            Ok(_) => failures.push(format!("{path} missing required phrase: {phrase}")),
            Err(err) => failures.push(format!("failed to read {path}: {err}")),
        }
    }

    for path in [
        "p2p-object-store-sqlite-schema-plan.md",
        "p2p-nosql-replication-repair-state-machine.md",
    ] {
        if let Ok(text) = fs::read_to_string(path) {
            for token in QUARANTINED_TOKENS {
                if text.contains(token) {
                    failures.push(format!("{path} contains quarantined token: {token}"));
                }
            }
        }
    }

    if failures.is_empty() {
        println!("scaffold contract validation passed");
    } else {
        for failure in failures {
            eprintln!("validation failed: {failure}");
        }
        process::exit(1);
    }
}


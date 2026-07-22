PRAGMA foreign_keys = ON;

INSERT INTO workflow_definitions (name, display_order, description) VALUES
    ('reconcile_replica_failure', 135, 'Classify an unreadable healthy replica and enqueue repair when replica count falls below policy.')
ON CONFLICT (name) DO UPDATE SET
    display_order = excluded.display_order,
    description = excluded.description;

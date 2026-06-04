# Observability-First Plan

## Goal

Make observability a product feature, not an operator burden.

The system should ship with:
- an admin dashboard
- embedded metrics endpoints
- Grafana dashboards
- containerized observability services
- clear links from the admin UI into Grafana

## Core Requirements

### 1. Admin Dashboard

The database should expose a built-in admin dashboard that shows:
- nodes and peer identities
- node health
- replication state
- storage consumption
- memory usage
- write and read throughput
- repair progress
- index lag
- conflict counts
- recent errors and warnings

### 2. Grafana Integration

Grafana should be part of the standard deployment.

Requirements:
- run Grafana in containers
- ship default dashboards with the system
- make dashboard provisioning automatic
- link Grafana from the admin dashboard
- keep the observability stack easy to run locally

### 3. Metrics

Expose metrics for:
- peer connections
- active nodes
- replication lag
- message retry counts
- hinted handoff counts
- repair queue depth
- index freshness
- storage utilization
- compaction backlog
- request latency
- error rate

### 4. Logs and Traces

Keep:
- structured logs
- per-node logs
- replication logs
- repair logs
- audit logs for admin actions

If tracing is added early, keep it simple:
- request path tracing
- replication flow tracing
- repair worker tracing

## Containers

Use a container-based deployment for the observability stack.

Minimum stack:
- database node
- admin dashboard service
- Grafana
- metrics exporter

Recommended stack additions:
- Prometheus or compatible metrics backend
- log aggregation service
- optional trace backend

## Dashboard Views

The admin dashboard should have these views:
- Cluster Overview
- Node Detail
- Replication Detail
- Repair Status
- Index Health
- Storage Health
- Alerts and Events
- Grafana Shortcuts

## Default Dashboards

Ship at least these Grafana dashboards:
- cluster overview
- node health
- replication health
- repair health
- storage usage
- request performance

## Operational Philosophy

If a user cannot answer these questions quickly, observability is not good enough:
- Which nodes are alive?
- Is replication behind?
- Are conflicts increasing?
- Is repair healthy?
- Is storage running hot?
- What changed recently?

## Deployment Rule

If Grafana is part of the standard stack, the first-run experience should be boring:
- one command to start the containers
- one URL for the admin dashboard
- one URL for Grafana
- one place to see cluster health

## References

- [libp2p docs](https://docs.libp2p.io/)
- [Apache Cassandra monitoring](https://cassandra.apache.org/doc/latest/cassandra/managing/operating/metrics.html)
- [DynamoDB Streams](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Streams.html)
- [CouchDB documentation](https://docs.couchdb.org/_/downloads/en/stable/pdf/)

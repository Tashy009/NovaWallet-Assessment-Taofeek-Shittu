-- Transactional outbox: rows are written in the same transaction as the ledger change they describe.

CREATE TABLE outbox_messages (
    id              uuid         NOT NULL,
    event_type      varchar(100) NOT NULL,
    aggregate_id    uuid         NOT NULL,
    payload         jsonb        NOT NULL,
    occurred_at     timestamptz  NOT NULL DEFAULT now(),
    published_at    timestamptz  NULL,
    attempts        int          NOT NULL DEFAULT 0,
    next_attempt_at timestamptz  NOT NULL DEFAULT now(),
    last_error      varchar(500) NULL,
    CONSTRAINT pk_outbox_messages PRIMARY KEY (id),
    CONSTRAINT uq_outbox_messages_event_aggregate UNIQUE (event_type, aggregate_id),
    CONSTRAINT ck_outbox_messages_attempts_non_negative CHECK (attempts >= 0)
);

-- The relay only ever scans unpublished rows that are due.
CREATE INDEX ix_outbox_messages_pending
    ON outbox_messages (next_attempt_at, occurred_at)
    WHERE published_at IS NULL;

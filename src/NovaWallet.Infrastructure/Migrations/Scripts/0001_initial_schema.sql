-- All money columns are BIGINT kobo; all timestamps are timestamptz (UTC).

CREATE TABLE wallets (
    id                        uuid         NOT NULL,
    customer_id               varchar(128) NOT NULL,
    currency                  char(3)      NOT NULL DEFAULT 'NGN',
    balance_kobo              bigint       NOT NULL DEFAULT 0,
    daily_outbound_limit_kobo bigint       NOT NULL DEFAULT 50000000,
    status                    varchar(16)  NOT NULL DEFAULT 'ACTIVE',
    created_at                timestamptz  NOT NULL DEFAULT now(),
    updated_at                timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT pk_wallets PRIMARY KEY (id),
    CONSTRAINT ck_wallets_balance_non_negative CHECK (balance_kobo >= 0),
    CONSTRAINT ck_wallets_currency_ngn CHECK (currency = 'NGN'),
    CONSTRAINT ck_wallets_daily_limit_positive CHECK (daily_outbound_limit_kobo > 0),
    CONSTRAINT ck_wallets_status CHECK (status IN ('ACTIVE', 'FROZEN')),
    CONSTRAINT ck_wallets_customer_id_not_blank CHECK (length(btrim(customer_id)) > 0),
    CONSTRAINT uq_wallets_customer_currency UNIQUE (customer_id, currency)
);

CREATE TABLE ledger_transactions (
    id                    uuid         NOT NULL,
    type                  varchar(16)  NOT NULL,
    amount_kobo           bigint       NOT NULL,
    currency              char(3)      NOT NULL DEFAULT 'NGN',
    source_wallet_id      uuid         NULL,
    destination_wallet_id uuid         NOT NULL,
    business_date         date         NOT NULL,
    external_reference    varchar(64)  NULL,
    narration             varchar(140) NULL,
    initiated_by          varchar(128) NOT NULL,
    correlation_id        varchar(64)  NOT NULL,
    created_at            timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT pk_ledger_transactions PRIMARY KEY (id),
    CONSTRAINT fk_ledger_transactions_source FOREIGN KEY (source_wallet_id) REFERENCES wallets (id),
    CONSTRAINT fk_ledger_transactions_destination FOREIGN KEY (destination_wallet_id) REFERENCES wallets (id),
    CONSTRAINT ck_ledger_transactions_amount_positive CHECK (amount_kobo > 0),
    CONSTRAINT ck_ledger_transactions_currency_ngn CHECK (currency = 'NGN'),
    CONSTRAINT ck_ledger_transactions_type CHECK (type IN ('CREDIT', 'TRANSFER')),
    CONSTRAINT ck_ledger_transactions_shape CHECK (
        (type = 'CREDIT' AND source_wallet_id IS NULL)
        OR (type = 'TRANSFER' AND source_wallet_id IS NOT NULL AND source_wallet_id <> destination_wallet_id))
);

-- One inbound NIP credit per external reference, regardless of idempotency key.
CREATE UNIQUE INDEX ux_ledger_transactions_external_reference
    ON ledger_transactions (external_reference) WHERE external_reference IS NOT NULL;
CREATE INDEX ix_ledger_transactions_source ON ledger_transactions (source_wallet_id) WHERE source_wallet_id IS NOT NULL;
CREATE INDEX ix_ledger_transactions_destination ON ledger_transactions (destination_wallet_id);

CREATE TABLE ledger_entries (
    entry_no           bigint GENERATED ALWAYS AS IDENTITY,
    transaction_id     uuid        NOT NULL,
    wallet_id          uuid        NOT NULL,
    direction          varchar(6)  NOT NULL,
    amount_kobo        bigint      NOT NULL,
    balance_after_kobo bigint      NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_ledger_entries PRIMARY KEY (entry_no),
    CONSTRAINT fk_ledger_entries_transaction FOREIGN KEY (transaction_id) REFERENCES ledger_transactions (id),
    CONSTRAINT fk_ledger_entries_wallet FOREIGN KEY (wallet_id) REFERENCES wallets (id),
    CONSTRAINT ck_ledger_entries_direction CHECK (direction IN ('DEBIT', 'CREDIT')),
    CONSTRAINT ck_ledger_entries_amount_positive CHECK (amount_kobo > 0),
    CONSTRAINT ck_ledger_entries_balance_after_non_negative CHECK (balance_after_kobo >= 0),
    CONSTRAINT uq_ledger_entries_transaction_wallet_direction UNIQUE (transaction_id, wallet_id, direction)
);

-- Statement: newest first, keyset-paginated on entry_no.
CREATE INDEX ix_ledger_entries_wallet_newest ON ledger_entries (wallet_id, entry_no DESC);

CREATE TABLE daily_outbound_totals (
    wallet_id     uuid        NOT NULL,
    business_date date        NOT NULL,
    total_kobo    bigint      NOT NULL,
    updated_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_daily_outbound_totals PRIMARY KEY (wallet_id, business_date),
    CONSTRAINT fk_daily_outbound_totals_wallet FOREIGN KEY (wallet_id) REFERENCES wallets (id),
    CONSTRAINT ck_daily_outbound_totals_non_negative CHECK (total_kobo >= 0)
);

CREATE TABLE idempotency_keys (
    customer_id     varchar(128) NOT NULL,
    operation       varchar(32)  NOT NULL,
    idempotency_key varchar(100) NOT NULL,
    request_hash    char(64)     NOT NULL,
    response_status int          NOT NULL DEFAULT 0,
    response_body   jsonb        NOT NULL DEFAULT '{}'::jsonb,
    transaction_id  uuid         NULL,
    created_at      timestamptz  NOT NULL DEFAULT now(),
    completed_at    timestamptz  NULL,
    CONSTRAINT pk_idempotency_keys PRIMARY KEY (customer_id, operation, idempotency_key),
    CONSTRAINT fk_idempotency_keys_transaction FOREIGN KEY (transaction_id) REFERENCES ledger_transactions (id),
    CONSTRAINT ck_idempotency_keys_operation CHECK (operation IN ('TRANSFER', 'CREDIT')),
    CONSTRAINT ck_idempotency_keys_response_status CHECK (response_status = 0 OR response_status BETWEEN 200 AND 599)
);

CREATE TABLE audit_log (
    audit_id            bigint GENERATED ALWAYS AS IDENTITY,
    event_id            uuid         NOT NULL,
    transaction_id      uuid         NOT NULL,
    wallet_id           uuid         NOT NULL,
    action              varchar(32)  NOT NULL,
    amount_kobo         bigint       NOT NULL,
    balance_before_kobo bigint       NOT NULL,
    balance_after_kobo  bigint       NOT NULL,
    actor_id            varchar(128) NOT NULL,
    correlation_id      varchar(64)  NOT NULL,
    occurred_at         timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT pk_audit_log PRIMARY KEY (audit_id),
    CONSTRAINT uq_audit_log_event_id UNIQUE (event_id),
    CONSTRAINT fk_audit_log_transaction FOREIGN KEY (transaction_id) REFERENCES ledger_transactions (id),
    CONSTRAINT fk_audit_log_wallet FOREIGN KEY (wallet_id) REFERENCES wallets (id),
    CONSTRAINT ck_audit_log_action CHECK (action IN ('WALLET_DEBITED', 'WALLET_CREDITED')),
    CONSTRAINT ck_audit_log_amount_positive CHECK (amount_kobo > 0),
    CONSTRAINT ck_audit_log_balances_non_negative CHECK (balance_before_kobo >= 0 AND balance_after_kobo >= 0),
    CONSTRAINT ck_audit_log_arithmetic CHECK (
        (action = 'WALLET_DEBITED' AND balance_after_kobo = balance_before_kobo - amount_kobo)
        OR (action = 'WALLET_CREDITED' AND balance_after_kobo = balance_before_kobo + amount_kobo))
);

CREATE INDEX ix_audit_log_wallet ON audit_log (wallet_id, audit_id DESC);
CREATE INDEX ix_audit_log_transaction ON audit_log (transaction_id);

CREATE FUNCTION forbid_append_only_mutation() RETURNS trigger
LANGUAGE plpgsql AS
$$
BEGIN
    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER trg_audit_log_append_only
    BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION forbid_append_only_mutation();
CREATE TRIGGER trg_audit_log_no_truncate
    BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_append_only_mutation();

CREATE TRIGGER trg_ledger_entries_append_only
    BEFORE UPDATE OR DELETE ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION forbid_append_only_mutation();
CREATE TRIGGER trg_ledger_entries_no_truncate
    BEFORE TRUNCATE ON ledger_entries
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_append_only_mutation();

CREATE TRIGGER trg_ledger_transactions_append_only
    BEFORE UPDATE OR DELETE ON ledger_transactions
    FOR EACH ROW EXECUTE FUNCTION forbid_append_only_mutation();
CREATE TRIGGER trg_ledger_transactions_no_truncate
    BEFORE TRUNCATE ON ledger_transactions
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_append_only_mutation();

#!/usr/bin/env python3
"""Track AI outreach batches in a dedicated local SQLite database."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import sqlite3
import sys
from pathlib import Path
from typing import Any


DEFAULT_DB_PATH = Path(".local/ai-outreach/ai-outreach.db")
COMPANY_CONTACT_WINDOW_DAYS = 7
APPROVAL_TOKEN = "yes"
EMAIL_RE = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")


def utc_now_iso() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def normalize_email(value: str) -> str:
    return value.strip().lower()


def normalize_company_key(value: str) -> str:
    lowered = value.strip().lower()
    lowered = lowered.replace("&", " and ")
    lowered = re.sub(r"\b(incorporated|inc|corp|corporation|co|company|llc|ltd|limited|gmbh|ag|plc|bv|sas|sarl)\b", " ", lowered)
    lowered = re.sub(r"[^a-z0-9]+", " ", lowered)
    lowered = re.sub(r"\s+", " ", lowered).strip()
    return lowered


def parse_iso(value: str) -> dt.datetime:
    normalized = value[:-1] + "+00:00" if value.endswith("Z") else value
    parsed = dt.datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        return parsed.replace(tzinfo=dt.timezone.utc)
    return parsed.astimezone(dt.timezone.utc)


def json_dumps(payload: Any) -> str:
    return json.dumps(payload, ensure_ascii=True, indent=2, sort_keys=True)


def ensure_parent_directory(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)


def open_connection(db_path: Path) -> sqlite3.Connection:
    ensure_parent_directory(db_path)
    connection = sqlite3.connect(db_path)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA foreign_keys = ON;")
    initialize_schema(connection)
    return connection


def initialize_schema(connection: sqlite3.Connection) -> None:
    connection.executescript(
        """
        CREATE TABLE IF NOT EXISTS runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            batch_name TEXT NOT NULL,
            batch_generated_at_utc TEXT NOT NULL,
            mode TEXT NOT NULL,
            approval_token TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            total_candidates INTEGER NOT NULL,
            total_success INTEGER NOT NULL,
            total_failed INTEGER NOT NULL,
            total_skipped INTEGER NOT NULL,
            batch_json TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS contacts (
            email TEXT PRIMARY KEY,
            normalized_email TEXT NOT NULL UNIQUE,
            company_name TEXT NOT NULL,
            company_key TEXT NOT NULL,
            person_name TEXT NULL,
            role_title TEXT NULL,
            company_website TEXT NULL,
            company_evidence_url TEXT NOT NULL,
            email_source_url TEXT NOT NULL,
            why_selected TEXT NOT NULL,
            first_seen_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            last_contacted_at_utc TEXT NULL,
            status TEXT NOT NULL,
            last_subject TEXT NULL,
            last_body TEXT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS company_contact_window (
            company_key TEXT PRIMARY KEY,
            company_name TEXT NOT NULL,
            last_contacted_at_utc TEXT NOT NULL,
            last_contact_email TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id INTEGER NOT NULL,
            occurred_at_utc TEXT NOT NULL,
            event_type TEXT NOT NULL,
            email TEXT NOT NULL,
            company_key TEXT NOT NULL,
            status TEXT NOT NULL,
            error_message TEXT NULL,
            payload_json TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(id)
        );

        CREATE INDEX IF NOT EXISTS ix_contacts_company_key ON contacts(company_key);
        CREATE INDEX IF NOT EXISTS ix_contacts_last_contacted ON contacts(last_contacted_at_utc);
        CREATE INDEX IF NOT EXISTS ix_events_run_id ON events(run_id);
        CREATE INDEX IF NOT EXISTS ix_events_email ON events(email);

        CREATE TABLE IF NOT EXISTS lead_pool (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            email TEXT NOT NULL,
            normalized_email TEXT NOT NULL UNIQUE,
            company_name TEXT NOT NULL,
            company_key TEXT NOT NULL,
            person_name TEXT NULL,
            role_title TEXT NULL,
            company_website TEXT NULL,
            company_evidence_url TEXT NOT NULL,
            email_source_url TEXT NOT NULL,
            why_selected TEXT NOT NULL,
            active INTEGER NOT NULL DEFAULT 1,
            times_selected INTEGER NOT NULL DEFAULT 0,
            last_selected_at_utc TEXT NULL,
            added_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_lead_pool_company_key ON lead_pool(company_key);
        CREATE INDEX IF NOT EXISTS ix_lead_pool_active ON lead_pool(active);
        """
    )


def load_json_file(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require_string(item: dict[str, Any], key: str, errors: list[str]) -> str:
    raw = item.get(key)
    if not isinstance(raw, str) or not raw.strip():
        errors.append(f"Missing required non-empty string: {key}")
        return ""
    return raw.strip()


def optional_string(item: dict[str, Any], key: str) -> str:
    raw = item.get(key)
    return raw.strip() if isinstance(raw, str) else ""


def validate_batch_shape(batch: dict[str, Any]) -> tuple[list[str], list[dict[str, Any]]]:
    errors: list[str] = []
    batch_name = batch.get("batchName")
    generated_at = batch.get("generatedAtUtc")
    items = batch.get("items")

    if not isinstance(batch_name, str) or not batch_name.strip():
        errors.append("batchName must be a non-empty string.")
    if not isinstance(generated_at, str) or not generated_at.strip():
        errors.append("generatedAtUtc must be a non-empty ISO timestamp string.")
    else:
        try:
            parse_iso(generated_at)
        except ValueError:
            errors.append("generatedAtUtc must be a valid ISO timestamp.")

    if not isinstance(items, list):
        errors.append("items must be an array.")
        return errors, []

    if len(items) != 3:
        errors.append("items must contain exactly 3 candidates.")

    normalized_items: list[dict[str, Any]] = []
    for index, raw_item in enumerate(items, start=1):
        if not isinstance(raw_item, dict):
            errors.append(f"Item {index} must be an object.")
            continue

        item_errors: list[str] = []
        normalized = {
            "companyName": require_string(raw_item, "companyName", item_errors),
            "companyWebsite": require_string(raw_item, "companyWebsite", item_errors),
            "personName": optional_string(raw_item, "personName"),
            "roleTitle": optional_string(raw_item, "roleTitle"),
            "email": require_string(raw_item, "email", item_errors),
            "subject": require_string(raw_item, "subject", item_errors),
            "body": require_string(raw_item, "body", item_errors),
            "whySelected": require_string(raw_item, "whySelected", item_errors),
            "companyEvidenceUrl": require_string(raw_item, "companyEvidenceUrl", item_errors),
            "emailSourceUrl": require_string(raw_item, "emailSourceUrl", item_errors),
        }

        email = normalized["email"]
        if email and not EMAIL_RE.match(email):
            item_errors.append(f"Item {index} email is not valid: {email}")

        company_key = normalize_company_key(normalized["companyName"])
        if not company_key:
            item_errors.append(f"Item {index} companyName does not yield a usable company key.")

        normalized["normalizedEmail"] = normalize_email(email) if email else ""
        normalized["companyKey"] = company_key
        normalized["itemIndex"] = index

        if item_errors:
            errors.extend(item_errors)
        normalized_items.append(normalized)

    return errors, normalized_items


def validate_lead_shape(payload: dict[str, Any]) -> tuple[list[str], list[dict[str, Any]]]:
    errors: list[str] = []
    items = payload.get("items")
    if not isinstance(items, list):
        return ["items must be an array."], []
    if len(items) == 0:
        return ["items must contain at least 1 lead."], []

    normalized_items: list[dict[str, Any]] = []
    for index, raw_item in enumerate(items, start=1):
        if not isinstance(raw_item, dict):
            errors.append(f"Item {index} must be an object.")
            continue

        item_errors: list[str] = []
        company_name = require_string(raw_item, "companyName", item_errors)
        company_website = require_string(raw_item, "companyWebsite", item_errors)
        person_name = optional_string(raw_item, "personName")
        role_title = optional_string(raw_item, "roleTitle")
        email = require_string(raw_item, "email", item_errors)
        why_selected = require_string(raw_item, "whySelected", item_errors)
        company_evidence_url = require_string(raw_item, "companyEvidenceUrl", item_errors)
        email_source_url = require_string(raw_item, "emailSourceUrl", item_errors)

        if email and not EMAIL_RE.match(email):
            item_errors.append(f"Item {index} email is not valid: {email}")

        company_key = normalize_company_key(company_name)
        if not company_key:
            item_errors.append(f"Item {index} companyName does not yield a usable company key.")

        if item_errors:
            errors.extend(item_errors)
            continue

        normalized_items.append(
            {
                "itemIndex": index,
                "companyName": company_name,
                "companyWebsite": company_website,
                "personName": person_name,
                "roleTitle": role_title,
                "email": email,
                "normalizedEmail": normalize_email(email),
                "whySelected": why_selected,
                "companyEvidenceUrl": company_evidence_url,
                "emailSourceUrl": email_source_url,
                "companyKey": company_key,
            }
        )

    return errors, normalized_items


def role_priority(role_title: str) -> int:
    role = role_title.lower().strip()
    if "founder" in role:
        return 0
    if "cto" in role:
        return 1
    if "head of product" in role or "product leader" in role:
        return 2
    return 3


def upsert_lead_pool_row(connection: sqlite3.Connection, item: dict[str, Any]) -> str:
    existing = connection.execute(
        """
        SELECT normalized_email
        FROM lead_pool
        WHERE normalized_email = ?
        LIMIT 1
        """,
        (item["normalizedEmail"],),
    ).fetchone()

    now_text = utc_now_iso()
    connection.execute(
        """
        INSERT INTO lead_pool(
            email,
            normalized_email,
            company_name,
            company_key,
            person_name,
            role_title,
            company_website,
            company_evidence_url,
            email_source_url,
            why_selected,
            active,
            times_selected,
            last_selected_at_utc,
            added_at_utc,
            updated_at_utc
        )
        VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, 0, NULL, ?, ?)
        ON CONFLICT(normalized_email) DO UPDATE SET
            email = excluded.email,
            company_name = excluded.company_name,
            company_key = excluded.company_key,
            person_name = excluded.person_name,
            role_title = excluded.role_title,
            company_website = excluded.company_website,
            company_evidence_url = excluded.company_evidence_url,
            email_source_url = excluded.email_source_url,
            why_selected = excluded.why_selected,
            active = 1,
            updated_at_utc = excluded.updated_at_utc
        """,
        (
            item["email"],
            item["normalizedEmail"],
            item["companyName"],
            item["companyKey"],
            item["personName"],
            item["roleTitle"],
            item["companyWebsite"],
            item["companyEvidenceUrl"],
            item["emailSourceUrl"],
            item["whySelected"],
            now_text,
            now_text,
        ),
    )
    return "updated" if existing else "inserted"


def add_leads_command(args: argparse.Namespace) -> int:
    leads_path = Path(args.leads_file)
    db_path = Path(args.db_path)
    payload = load_json_file(leads_path)
    shape_errors, items = validate_lead_shape(payload)
    if shape_errors:
        raise ValueError("; ".join(shape_errors))

    inserted = 0
    updated = 0
    with open_connection(db_path) as connection:
        with connection:
            for item in items:
                outcome = upsert_lead_pool_row(connection, item)
                if outcome == "inserted":
                    inserted += 1
                else:
                    updated += 1

    result = {
        "dbPath": str(db_path),
        "leadsFile": str(leads_path),
        "summary": {
            "providedCount": len(items),
            "insertedCount": inserted,
            "updatedCount": updated,
            "totalUpserted": inserted + updated,
        },
    }
    print(json_dumps(result))
    return 0


def build_batch_from_pool_command(args: argparse.Namespace) -> int:
    db_path = Path(args.db_path)
    output_batch_path = Path(args.output_batch_file)
    generated_at = utc_now_iso()
    cutoff = dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=COMPANY_CONTACT_WINDOW_DAYS)

    with open_connection(db_path) as connection:
        rows = connection.execute(
            """
            SELECT
                id,
                email,
                normalized_email,
                company_name,
                company_key,
                person_name,
                role_title,
                company_website,
                company_evidence_url,
                email_source_url,
                why_selected,
                times_selected,
                updated_at_utc
            FROM lead_pool
            WHERE active = 1
            ORDER BY times_selected ASC, updated_at_utc DESC
            """
        ).fetchall()

        sorted_rows = sorted(rows, key=lambda row: role_priority(str(row["role_title"] or "")))
        selected: list[sqlite3.Row] = []
        skipped: list[dict[str, Any]] = []
        seen_companies: set[str] = set()
        seen_emails: set[str] = set()

        for row in sorted_rows:
            reasons: list[str] = []
            normalized_email = str(row["normalized_email"])
            company_key = str(row["company_key"])

            if normalized_email in seen_emails:
                reasons.append("Duplicate email in selection pass.")
            if company_key in seen_companies:
                reasons.append("Duplicate company in selection pass.")

            contact_row = get_contact_row(connection, normalized_email)
            if contact_row and contact_row["last_contacted_at_utc"]:
                reasons.append(f"Email already contacted on {contact_row['last_contacted_at_utc']}.")

            company_window = get_company_window_row(connection, company_key)
            if company_window and company_window["last_contacted_at_utc"]:
                last_contacted = parse_iso(str(company_window["last_contacted_at_utc"]))
                if last_contacted >= cutoff:
                    reasons.append(
                        f"Company contacted within the last {COMPANY_CONTACT_WINDOW_DAYS} days via "
                        f"{company_window['last_contact_email']} on {company_window['last_contacted_at_utc']}."
                    )

            if reasons:
                skipped.append(
                    {
                        "email": row["email"],
                        "companyName": row["company_name"],
                        "roleTitle": row["role_title"],
                        "skipReasons": reasons,
                    }
                )
                continue

            selected.append(row)
            seen_emails.add(normalized_email)
            seen_companies.add(company_key)

            if len(selected) == 3:
                break

        if selected:
            with connection:
                for row in selected:
                    connection.execute(
                        """
                        UPDATE lead_pool
                        SET times_selected = times_selected + 1,
                            last_selected_at_utc = ?,
                            updated_at_utc = ?
                        WHERE id = ?
                        """,
                        (generated_at, generated_at, row["id"]),
                    )

    batch_items: list[dict[str, Any]] = []
    for row in selected:
        batch_items.append(
            {
                "companyName": row["company_name"],
                "companyWebsite": row["company_website"],
                "personName": row["person_name"] or "",
                "roleTitle": row["role_title"] or "",
                "email": row["email"],
                # Runner script will replace with strict template text before preview/send.
                "subject": "TEMPLATE_PENDING",
                "body": "TEMPLATE_PENDING",
                "whySelected": row["why_selected"],
                "companyEvidenceUrl": row["company_evidence_url"],
                "emailSourceUrl": row["email_source_url"],
            }
        )

    batch_payload = {
        "batchName": args.batch_name.strip() or "ai-outreach-daily",
        "generatedAtUtc": generated_at,
        "items": batch_items,
    }

    ensure_parent_directory(output_batch_path)
    output_batch_path.write_text(json.dumps(batch_payload, ensure_ascii=True, indent=2), encoding="utf-8")

    result = {
        "dbPath": str(db_path),
        "outputBatchFile": str(output_batch_path),
        "generatedAtUtc": generated_at,
        "eligible": len(batch_items) == 3,
        "needsResearch": len(batch_items) < 3,
        "summary": {
            "selectedCount": len(batch_items),
            "requiredCount": 3,
            "availablePoolCount": len(rows) if "rows" in locals() else 0,
            "skippedCount": len(skipped),
        },
        "selected": batch_items,
        "skipped": skipped[:25],
    }
    print(json_dumps(result))
    return 0 if len(batch_items) == 3 else 1


def get_contact_row(connection: sqlite3.Connection, normalized_email: str) -> sqlite3.Row | None:
    return connection.execute(
        """
        SELECT email, normalized_email, company_name, company_key, last_contacted_at_utc, status
        FROM contacts
        WHERE normalized_email = ?
        LIMIT 1
        """,
        (normalized_email,),
    ).fetchone()


def get_company_window_row(connection: sqlite3.Connection, company_key: str) -> sqlite3.Row | None:
    return connection.execute(
        """
        SELECT company_key, company_name, last_contacted_at_utc, last_contact_email
        FROM company_contact_window
        WHERE company_key = ?
        LIMIT 1
        """,
        (company_key,),
    ).fetchone()


def build_preview_payload(batch: dict[str, Any], items: list[dict[str, Any]], connection: sqlite3.Connection) -> dict[str, Any]:
    batch_errors: list[str] = []
    seen_emails: set[str] = set()
    seen_companies: set[str] = set()
    cutoff = dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=COMPANY_CONTACT_WINDOW_DAYS)

    preview_items: list[dict[str, Any]] = []
    eligible_count = 0
    blocked_count = 0

    for item in items:
        skip_reasons: list[str] = []
        normalized_email = item["normalizedEmail"]
        company_key = item["companyKey"]

        if normalized_email in seen_emails:
            skip_reasons.append("Duplicate email within batch.")
        else:
            seen_emails.add(normalized_email)

        if company_key in seen_companies:
            skip_reasons.append("Duplicate company within batch.")
        else:
            seen_companies.add(company_key)

        existing_contact = get_contact_row(connection, normalized_email)
        if existing_contact and existing_contact["last_contacted_at_utc"]:
            skip_reasons.append(
                f"Email already contacted on {existing_contact['last_contacted_at_utc']}."
            )

        company_window = get_company_window_row(connection, company_key)
        if company_window and company_window["last_contacted_at_utc"]:
            last_contacted = parse_iso(company_window["last_contacted_at_utc"])
            if last_contacted >= cutoff:
                skip_reasons.append(
                    f"Company contacted within the last {COMPANY_CONTACT_WINDOW_DAYS} days via {company_window['last_contact_email']} on {company_window['last_contacted_at_utc']}."
                )

        eligible = len(skip_reasons) == 0
        if eligible:
            eligible_count += 1
        else:
            blocked_count += 1

        preview_items.append(
            {
                "itemIndex": item["itemIndex"],
                "companyName": item["companyName"],
                "companyKey": company_key,
                "companyWebsite": item["companyWebsite"],
                "personName": item["personName"],
                "roleTitle": item["roleTitle"],
                "email": item["email"],
                "normalizedEmail": normalized_email,
                "subject": item["subject"],
                "body": item["body"],
                "whySelected": item["whySelected"],
                "companyEvidenceUrl": item["companyEvidenceUrl"],
                "emailSourceUrl": item["emailSourceUrl"],
                "eligible": eligible,
                "skipReasons": skip_reasons,
            }
        )

    if eligible_count != 3:
        batch_errors.append(f"Preview must end with exactly 3 eligible candidates; got {eligible_count}.")

    return {
        "batchName": batch.get("batchName"),
        "generatedAtUtc": batch.get("generatedAtUtc"),
        "eligible": len(batch_errors) == 0,
        "batchErrors": batch_errors,
        "summary": {
            "providedCount": len(items),
            "eligibleCount": eligible_count,
            "blockedCount": blocked_count,
            "requiredCount": 3,
            "companyCooldownDays": COMPANY_CONTACT_WINDOW_DAYS,
        },
        "items": preview_items,
    }


def preview_command(args: argparse.Namespace) -> int:
    batch_path = Path(args.batch_file)
    db_path = Path(args.db_path)
    batch = load_json_file(batch_path)
    shape_errors, items = validate_batch_shape(batch)

    with open_connection(db_path) as connection:
        payload = build_preview_payload(batch, items, connection) if not shape_errors else {
            "batchName": batch.get("batchName"),
            "generatedAtUtc": batch.get("generatedAtUtc"),
            "eligible": False,
            "batchErrors": shape_errors,
            "summary": {
                "providedCount": len(items),
                "eligibleCount": 0,
                "blockedCount": len(items),
                "requiredCount": 3,
                "companyCooldownDays": COMPANY_CONTACT_WINDOW_DAYS,
            },
            "items": [],
        }

    payload["dbPath"] = str(db_path)
    payload["batchFile"] = str(batch_path)
    print(json_dumps(payload))
    return 0 if payload["eligible"] else 1


def create_run(
    connection: sqlite3.Connection,
    batch: dict[str, Any],
    mode: str,
    approval_token: str,
    success_count: int,
    failed_count: int,
    skipped_count: int,
) -> int:
    cursor = connection.execute(
        """
        INSERT INTO runs(
            batch_name,
            batch_generated_at_utc,
            mode,
            approval_token,
            created_at_utc,
            total_candidates,
            total_success,
            total_failed,
            total_skipped,
            batch_json
        )
        VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
        (
            batch["batchName"],
            batch["generatedAtUtc"],
            mode,
            approval_token,
            utc_now_iso(),
            len(batch["items"]),
            success_count,
            failed_count,
            skipped_count,
            json.dumps(batch, ensure_ascii=True, sort_keys=True),
        ),
    )
    return int(cursor.lastrowid)


def upsert_contact(
    connection: sqlite3.Connection,
    item: dict[str, Any],
    status: str,
    contacted_at_utc: str | None,
) -> None:
    existing = get_contact_row(connection, item["normalizedEmail"])
    now_text = utc_now_iso()
    first_seen = existing["last_contacted_at_utc"] if existing and existing["last_contacted_at_utc"] else now_text
    current_first_seen_row = connection.execute(
        "SELECT first_seen_at_utc FROM contacts WHERE normalized_email = ? LIMIT 1",
        (item["normalizedEmail"],),
    ).fetchone()
    if current_first_seen_row:
        first_seen = current_first_seen_row["first_seen_at_utc"]

    connection.execute(
        """
        INSERT INTO contacts(
            email,
            normalized_email,
            company_name,
            company_key,
            person_name,
            role_title,
            company_website,
            company_evidence_url,
            email_source_url,
            why_selected,
            first_seen_at_utc,
            last_seen_at_utc,
            last_contacted_at_utc,
            status,
            last_subject,
            last_body,
            updated_at_utc
        )
        VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(email) DO UPDATE SET
            normalized_email = excluded.normalized_email,
            company_name = excluded.company_name,
            company_key = excluded.company_key,
            person_name = excluded.person_name,
            role_title = excluded.role_title,
            company_website = excluded.company_website,
            company_evidence_url = excluded.company_evidence_url,
            email_source_url = excluded.email_source_url,
            why_selected = excluded.why_selected,
            last_seen_at_utc = excluded.last_seen_at_utc,
            last_contacted_at_utc = COALESCE(excluded.last_contacted_at_utc, contacts.last_contacted_at_utc),
            status = excluded.status,
            last_subject = CASE
                WHEN excluded.last_subject IS NULL OR excluded.last_subject = '' THEN contacts.last_subject
                ELSE excluded.last_subject
            END,
            last_body = CASE
                WHEN excluded.last_body IS NULL OR excluded.last_body = '' THEN contacts.last_body
                ELSE excluded.last_body
            END,
            updated_at_utc = excluded.updated_at_utc
        """,
        (
            item["normalizedEmail"],
            item["normalizedEmail"],
            item["companyName"],
            item["companyKey"],
            item["personName"],
            item["roleTitle"],
            item["companyWebsite"],
            item["companyEvidenceUrl"],
            item["emailSourceUrl"],
            item["whySelected"],
            first_seen,
            now_text,
            contacted_at_utc,
            status,
            item["subject"] if contacted_at_utc else None,
            item["body"] if contacted_at_utc else None,
            now_text,
        ),
    )


def update_company_window(connection: sqlite3.Connection, item: dict[str, Any], contacted_at_utc: str) -> None:
    connection.execute(
        """
        INSERT INTO company_contact_window(
            company_key,
            company_name,
            last_contacted_at_utc,
            last_contact_email,
            updated_at_utc
        )
        VALUES(?, ?, ?, ?, ?)
        ON CONFLICT(company_key) DO UPDATE SET
            company_name = excluded.company_name,
            last_contacted_at_utc = excluded.last_contacted_at_utc,
            last_contact_email = excluded.last_contact_email,
            updated_at_utc = excluded.updated_at_utc
        """,
        (
            item["companyKey"],
            item["companyName"],
            contacted_at_utc,
            item["email"],
            utc_now_iso(),
        ),
    )


def insert_event(
    connection: sqlite3.Connection,
    run_id: int,
    event_type: str,
    email: str,
    company_key: str,
    status: str,
    payload: dict[str, Any],
    error_message: str | None = None,
) -> None:
    connection.execute(
        """
        INSERT INTO events(
            run_id,
            occurred_at_utc,
            event_type,
            email,
            company_key,
            status,
            error_message,
            payload_json
        )
        VALUES(?, ?, ?, ?, ?, ?, ?, ?)
        """,
        (
            run_id,
            utc_now_iso(),
            event_type,
            email,
            company_key,
            status,
            error_message,
            json.dumps(payload, ensure_ascii=True, sort_keys=True),
        ),
    )


def validate_commit_inputs(
    batch: dict[str, Any],
    results_payload: dict[str, Any],
    db_path: Path,
) -> tuple[dict[str, Any], list[dict[str, Any]], list[dict[str, Any]]]:
    if not isinstance(results_payload.get("items"), list):
        raise ValueError("results file must contain an items array.")

    shape_errors, items = validate_batch_shape(batch)
    if shape_errors:
        raise ValueError("; ".join(shape_errors))

    with open_connection(db_path) as connection:
        preview = build_preview_payload(batch, items, connection)
    if not preview["eligible"]:
        raise ValueError("; ".join(preview["batchErrors"]))

    raw_results: list[dict[str, Any]] = []
    for raw_item in results_payload["items"]:
        if not isinstance(raw_item, dict):
            raise ValueError("Each result item must be an object.")
        email = raw_item.get("email")
        status = raw_item.get("status")
        if not isinstance(email, str) or not email.strip():
            raise ValueError("Each result item must include email.")
        if status not in {"sent", "failed", "skipped"}:
            raise ValueError("Each result item status must be one of: sent, failed, skipped.")
        raw_results.append(raw_item)

    return preview, items, raw_results


def commit_send_results_command(args: argparse.Namespace) -> int:
    if args.approval_token.strip().lower() != APPROVAL_TOKEN:
        raise ValueError("Approval token must be explicit yes.")

    batch_path = Path(args.batch_file)
    db_path = Path(args.db_path)
    results_path = Path(args.results_file)

    batch = load_json_file(batch_path)
    results_payload = load_json_file(results_path)
    preview, items, result_items = validate_commit_inputs(batch, results_payload, db_path)

    results_by_email = {
        normalize_email(str(result["email"])): result
        for result in result_items
    }
    if set(results_by_email.keys()) != {item["normalizedEmail"] for item in items}:
        raise ValueError("Results file emails must match the batch exactly.")

    success_count = sum(1 for result in result_items if result["status"] == "sent")
    failed_count = sum(1 for result in result_items if result["status"] == "failed")
    skipped_count = sum(1 for result in result_items if result["status"] == "skipped")

    with open_connection(db_path) as connection:
        with connection:
            run_id = create_run(
                connection,
                batch,
                mode="send-confirmed",
                approval_token=args.approval_token.strip(),
                success_count=success_count,
                failed_count=failed_count,
                skipped_count=skipped_count,
            )

            report_items: list[dict[str, Any]] = []
            for item in preview["items"]:
                result = results_by_email[item["normalizedEmail"]]
                status = str(result["status"])
                gmail_result = result.get("gmailResult")
                error_message = result.get("error")
                contacted_at_utc = utc_now_iso() if status == "sent" else None

                upsert_contact(
                    connection,
                    item,
                    status="Sent" if status == "sent" else "New",
                    contacted_at_utc=contacted_at_utc,
                )

                insert_event(
                    connection,
                    run_id,
                    event_type="candidate_selected",
                    email=item["email"],
                    company_key=item["companyKey"],
                    status="selected",
                    payload=item,
                )

                if status != "skipped":
                    insert_event(
                        connection,
                        run_id,
                        event_type="send_attempted",
                        email=item["email"],
                        company_key=item["companyKey"],
                        status=status,
                        payload={"gmailResult": gmail_result},
                        error_message=error_message,
                    )

                if status == "sent":
                    update_company_window(connection, item, contacted_at_utc or utc_now_iso())
                    insert_event(
                        connection,
                        run_id,
                        event_type="send_succeeded",
                        email=item["email"],
                        company_key=item["companyKey"],
                        status="sent",
                        payload={"gmailResult": gmail_result},
                    )
                elif status == "failed":
                    insert_event(
                        connection,
                        run_id,
                        event_type="send_failed",
                        email=item["email"],
                        company_key=item["companyKey"],
                        status="failed",
                        payload={"gmailResult": gmail_result},
                        error_message=str(error_message or "Unknown send failure."),
                    )

                report_items.append(
                    {
                        "itemIndex": item["itemIndex"],
                        "companyName": item["companyName"],
                        "personName": item["personName"],
                        "roleTitle": item["roleTitle"],
                        "email": item["email"],
                        "whySelected": item["whySelected"],
                        "subject": item["subject"],
                        "body": item["body"],
                        "status": status,
                        "gmailResult": gmail_result,
                        "error": error_message,
                        "tracker": {
                            "companyKey": item["companyKey"],
                            "dbPath": str(db_path),
                            "recordedContactedAtUtc": contacted_at_utc,
                        },
                    }
                )

    payload = {
        "runId": run_id,
        "dbPath": str(db_path),
        "batchFile": str(batch_path),
        "resultsFile": str(results_path),
        "summary": {
            "totalCandidates": len(items),
            "successCount": success_count,
            "failedCount": failed_count,
            "skippedCount": skipped_count,
        },
        "items": report_items,
    }
    print(json_dumps(payload))
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="AI outreach tracker utility.")
    parser.add_argument(
        "--db-path",
        default=str(DEFAULT_DB_PATH),
        help="SQLite database path. Defaults to .local/ai-outreach/ai-outreach.db",
    )

    subparsers = parser.add_subparsers(dest="command", required=True)

    preview_parser = subparsers.add_parser("preview", help="Validate a 3-item batch without mutating state.")
    preview_parser.add_argument("--batch-file", required=True, help="Path to JSON batch file.")
    preview_parser.set_defaults(func=preview_command)

    commit_parser = subparsers.add_parser(
        "commit-send-results",
        help="Persist approved send results after the Gmail sends have completed.",
    )
    commit_parser.add_argument("--batch-file", required=True, help="Path to JSON batch file.")
    commit_parser.add_argument("--results-file", required=True, help="Path to JSON results file.")
    commit_parser.add_argument("--approval-token", required=True, help='Must be "yes".')
    commit_parser.set_defaults(func=commit_send_results_command)

    add_leads_parser = subparsers.add_parser(
        "add-leads",
        help="Add or update candidate leads in the persistent lead pool.",
    )
    add_leads_parser.add_argument(
        "--leads-file",
        required=True,
        help="Path to JSON file with an items[] array of lead objects.",
    )
    add_leads_parser.set_defaults(func=add_leads_command)

    build_batch_parser = subparsers.add_parser(
        "build-batch-from-pool",
        help="Build a 3-item outreach batch from stored leads without doing new web research.",
    )
    build_batch_parser.add_argument(
        "--output-batch-file",
        required=True,
        help="Path where the generated batch JSON should be written.",
    )
    build_batch_parser.add_argument(
        "--batch-name",
        default="ai-outreach-daily",
        help="Batch name for generated output. Defaults to ai-outreach-daily.",
    )
    build_batch_parser.set_defaults(func=build_batch_from_pool_command)

    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    try:
        return int(args.func(args))
    except Exception as exc:  # noqa: BLE001
        error_payload = {
            "error": str(exc),
            "command": args.command,
        }
        print(json_dumps(error_payload))
        return 1


if __name__ == "__main__":
    sys.exit(main())

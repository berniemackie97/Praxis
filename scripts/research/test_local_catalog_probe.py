import sqlite3
import tempfile
import unittest
from pathlib import Path

from local_catalog_probe import SCHEMA, catalog_probe, connect, create_catalog, sparse_size_probe, streaming_probe

NOW = "2026-11-08T00:00:00Z"
DIGEST = "a" * 64


class CatalogContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="praxis-catalog-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.path = self.root / "catalog.sqlite"
        self.connection = create_catalog(self.path)
        self.addCleanup(self.connection.close)

    def source(self, source_id, locator):
        self.connection.execute("INSERT INTO sources VALUES (?, 'file', ?, ?, 'active')",
                                (source_id, locator, NOW))
        self.connection.execute("INSERT INTO items VALUES (?, ?)", (source_id + "-item", source_id))

    def object(self, digest=DIGEST):
        self.connection.execute("INSERT INTO original_objects VALUES (?, ?, ?)", (digest, 1024, NOW))

    def test_schema_version_and_local_transaction_settings(self):
        self.assertEqual(1, self.connection.execute("PRAGMA user_version").fetchone()[0])
        self.assertEqual(1, self.connection.execute("PRAGMA foreign_keys").fetchone()[0])
        self.assertEqual(2, self.connection.execute("PRAGMA synchronous").fetchone()[0])
        self.assertEqual("wal", self.connection.execute("PRAGMA journal_mode").fetchone()[0])
        self.assertIn("STRICT", SCHEMA.read_text())

    def test_same_bytes_keep_separate_source_and_item_identity(self):
        with self.connection:
            self.object()
            self.source("a", "folder/one.md")
            self.source("b", "other/two.md")
            for key in ("a", "b"):
                self.connection.execute(
                    "INSERT INTO item_versions VALUES (?, 1, ?, ?)", (key + "-item", DIGEST, NOW))
        self.assertEqual(1, self.connection.execute("SELECT count(*) FROM original_objects").fetchone()[0])
        self.assertEqual(2, self.connection.execute("SELECT count(*) FROM sources").fetchone()[0])
        self.assertEqual(2, self.connection.execute("SELECT count(*) FROM item_versions").fetchone()[0])

        with self.connection:
            self.connection.execute("UPDATE sources SET status = 'revoked' WHERE source_id = 'a'")
        self.assertEqual([("b",)], self.connection.execute(
            "SELECT source_id FROM sources WHERE status = 'active'").fetchall())
        self.assertEqual(2, self.connection.execute("SELECT count(*) FROM item_versions").fetchone()[0])

    def test_source_locator_and_request_receipt_are_unique(self):
        with self.connection:
            self.source("first", "folder/one.md")
            self.connection.execute(
                "INSERT INTO ingest_requests VALUES ('request-a', 'first', 'queued', 0, NULL)")
        with self.assertRaises(sqlite3.IntegrityError):
            with self.connection:
                self.source("second", "folder/one.md")
        with self.assertRaises(sqlite3.IntegrityError):
            with self.connection:
                self.connection.execute(
                    "INSERT INTO ingest_requests VALUES ('request-a', 'first', 'queued', 0, NULL)")
        self.assertEqual(1, self.connection.execute("SELECT count(*) FROM sources").fetchone()[0])

    def test_invalid_object_digest_or_foreign_reference_is_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            with self.connection:
                self.object("g" * 64)
        with self.assertRaises(sqlite3.IntegrityError):
            with self.connection:
                self.connection.execute(
                    "INSERT INTO item_versions VALUES ('missing', 1, ?, ?)", (DIGEST, NOW))
        with self.assertRaises(sqlite3.IntegrityError):
            with self.connection:
                self.connection.execute(
                    "INSERT INTO original_objects VALUES (?, -1, ?)", (DIGEST, NOW))

    def test_committed_records_survive_reopen_but_rolled_back_records_do_not(self):
        with self.connection:
            self.source("committed", "file.md")
        try:
            self.connection.execute("BEGIN")
            self.source("inflight", "interrupted.md")
            raise RuntimeError("simulated failure before commit")
        except RuntimeError:
            self.connection.rollback()
        self.connection.close()
        self.connection = connect(self.path)
        self.addCleanup(self.connection.close)
        self.assertEqual([("committed",)], self.connection.execute(
            "SELECT source_id FROM sources").fetchall())

    def test_metadata_backup_reopens_with_versions_and_requests(self):
        with self.connection:
            self.object()
            self.source("one", "file.md")
            self.connection.execute("INSERT INTO item_versions VALUES ('one-item', 1, ?, ?)",
                                    (DIGEST, NOW))
        destination = sqlite3.connect(self.root / "recovered.sqlite")
        try:
            self.connection.backup(destination)
            self.assertEqual(1, destination.execute("SELECT count(*) FROM item_versions").fetchone()[0])
            self.assertEqual(1, destination.execute("PRAGMA user_version").fetchone()[0])
        finally:
            destination.close()

    def test_corrupt_catalog_fails_integrity_inspection(self):
        damaged = self.root / "damaged.sqlite"
        damaged.write_bytes(b"not a SQLite database")
        with self.assertRaises(sqlite3.DatabaseError):
            connection = sqlite3.connect(damaged)
            try:
                connection.execute("PRAGMA integrity_check").fetchone()
            finally:
                connection.close()

    def test_probe_replays_updates_and_streams_original_bytes(self):
        benchmark = self.root / "benchmark"
        benchmark.mkdir()
        result = catalog_probe(benchmark, 24, 6)
        self.assertEqual(24, result["items"])
        self.assertEqual(6, result["new_revisions"])
        self.assertGreater(result["sqlite_disk_bytes"], 0)
        self.assertGreater(result["jsonl_disk_bytes"], 0)
        streamed = streaming_probe(self.root, 2)
        self.assertEqual(2 * 1024 * 1024, streamed["original_bytes"])
        self.assertEqual(64 * 1024, streamed["chunk_bytes"])
        sparse = sparse_size_probe(self.root, 3)
        self.assertEqual(3 * 1024 * 1024, sparse["sparse_input_bytes_streamed"])
        self.assertEqual(64, len(sparse["sparse_sha256"]))


if __name__ == "__main__":
    unittest.main()

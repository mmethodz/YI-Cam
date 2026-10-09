"""A local recording catalogue with explicit, bounded retention policies."""
from __future__ import annotations

from dataclasses import asdict, dataclass
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import time

GIB = 1024**3
MANAGED_NAME = re.compile(r'YI_\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d_\d+x\d+_[a-f0-9]{6}\.mp4\Z')


@dataclass
class StoragePolicy:
    quota_gib: float = 20
    minimum_free_gib: float = 2
    segment_minutes: float = 10
    recycle: bool = False
    keep_days: int = 0

    def validate(self):
        if not 0.1 <= self.quota_gib <= 100000:
            raise ValueError('Recording budget must be at least 0.1 GiB.')
        if not 0.1 <= self.minimum_free_gib <= 100000:
            raise ValueError('Keep at least 0.1 GiB of free disk space.')
        if not 0.1 <= self.segment_minutes <= 120:
            raise ValueError('Clip length must be between 0.1 and 120 minutes.')
        if not 0 <= self.keep_days <= 36500:
            raise ValueError('Maximum age must be zero (unlimited) or a positive number of days.')
        return self


class Library:
    """Only files created and registered by this app can be recycled.

    Keep separate instances on separate threads. Never follow file symlinks,
    directory junctions inside the library, or paths imported from a database.
    """
    def __init__(self, folder):
        self.root = Path(folder).expanduser().resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.connection = sqlite3.connect(self.root / '.yi-library.sqlite', timeout=10)
        self.connection.row_factory = sqlite3.Row
        self.connection.execute('PRAGMA journal_mode=WAL')
        self.connection.execute('''CREATE TABLE IF NOT EXISTS clips (
            name TEXT PRIMARY KEY, started REAL NOT NULL, duration REAL DEFAULT 0,
            width INTEGER, height INTEGER, bytes INTEGER DEFAULT 0,
            complete INTEGER DEFAULT 0, protected INTEGER DEFAULT 0)''')
        self.connection.commit()

    def path(self, name):
        if not MANAGED_NAME.fullmatch(name):
            raise ValueError('Not an app-managed recording filename.')
        path = self.root / name
        if path.is_symlink() or path.resolve().parent != self.root:
            raise ValueError('Recording points outside the library.')
        return path

    def register(self, path, width, height):
        path = Path(path)
        if path.parent.resolve() != self.root:
            raise ValueError('Recording is outside its library.')
        self.path(path.name)
        self.connection.execute('INSERT INTO clips(name,started,width,height) VALUES(?,?,?,?)',
                                (path.name, time.time(), width, height))
        self.connection.commit()

    def finish(self, path, duration):
        path = self.path(Path(path).name)
        self.connection.execute('UPDATE clips SET complete=1, duration=?, bytes=? WHERE name=?',
                                (duration, path.stat().st_size if path.exists() else 0, path.name))
        self.connection.commit()

    def clips(self):
        result = []
        for row in self.connection.execute('SELECT * FROM clips ORDER BY started DESC'):
            item = dict(row)
            try:
                path = self.path(item['name'])
                item['exists'] = path.is_file()
                item['bytes'] = path.stat().st_size if item['exists'] else 0
            except (OSError, ValueError):
                item['exists'] = False
                item['bytes'] = 0
            result.append(item)
        return result

    def protect(self, name, protected):
        self.path(name)
        self.connection.execute('UPDATE clips SET protected=? WHERE name=?', (int(protected), name))
        self.connection.commit()

    def delete(self, name):
        self.connection.execute('BEGIN IMMEDIATE')
        try:
            row = self.connection.execute('SELECT * FROM clips WHERE name=?', (name,)).fetchone()
            if not row or row['protected'] or not row['complete']:
                raise ValueError('Only finished, unprotected recordings can be deleted.')
            path = self.path(name)
            path.unlink(missing_ok=True)
            self.connection.execute('DELETE FROM clips WHERE name=?', (name,))
            self.connection.commit()
        except BaseException:
            self.connection.rollback()
            raise

    def enforce(self, policy: StoragePolicy, active=None, reserve=1024**2):
        policy.validate()
        clips = self.clips()
        total = sum(item['bytes'] for item in clips)
        free = shutil.disk_usage(self.root).free
        limit, floor = policy.quota_gib * GIB, policy.minimum_free_gib * GIB
        deleted = []
        cutoff = time.time() - policy.keep_days * 86400 if policy.keep_days else None
        for item in reversed(clips):
            over = total + reserve > limit or free - reserve < floor
            old = cutoff is not None and item['started'] < cutoff
            if not policy.recycle or not (over or old):
                continue
            if item['name'] == active or item['protected'] or not item['complete']:
                continue
            self.delete(item['name'])
            deleted.append(item['name'])
            total -= item['bytes']
            free = shutil.disk_usage(self.root).free
        if total + reserve > limit:
            raise OSError('Recording budget reached. Free space, raise the budget, or enable recycling.')
        if free - reserve < floor:
            raise OSError('Recording stopped to preserve the configured free disk space.')
        return deleted

    def close(self):
        self.connection.close()


def save_settings(path: Path, settings: dict):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(settings, indent=2), encoding='utf-8')
    os.replace(temporary, path)

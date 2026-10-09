from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from yicam.storage import GIB, Library, StoragePolicy


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.library = Library(self.temp.name)

    def tearDown(self):
        self.library.close()
        self.temp.cleanup()

    def clip(self, identifier, size=1, complete=True):
        path = self.library.root / f'YI_2026-01-01_00-00-00_1280x720_{identifier:06x}.mp4'
        with path.open('wb') as file:
            file.truncate(size)
        self.library.register(path, 1280, 720)
        if complete:
            self.library.finish(path, 5)
        return path

    def test_default_limit_stops_without_deleting(self):
        clip = self.clip(1, 120*1024**2)
        with self.assertRaises(OSError):
            self.library.enforce(StoragePolicy(quota_gib=.1))
        self.assertTrue(clip.exists())

    def test_recycle_protects_active_and_unmanaged_files(self):
        protected = self.clip(1, 30*1024**2)
        old = self.clip(2, 70*1024**2)
        active = self.clip(3, 30*1024**2, complete=False)
        external = self.library.root / 'family-video.mp4'
        external.write_bytes(b'not owned by this app')
        self.library.protect(protected.name, True)
        self.assertEqual(self.library.enforce(StoragePolicy(quota_gib=.1, recycle=True), active.name), [old.name])
        self.assertTrue(all(path.exists() for path in (protected, active, external)))

    def test_age_policy_and_protected_clip(self):
        old = self.clip(1)
        protected = self.clip(2)
        self.library.protect(protected.name, True)
        self.library.connection.execute('UPDATE clips SET started=0')
        self.library.connection.commit()
        self.library.enforce(StoragePolicy(recycle=True, keep_days=1))
        self.assertFalse(old.exists())
        self.assertTrue(protected.exists())

    def test_reject_paths_outside_library(self):
        for name in ('../video.mp4', 'C:\\video.mp4', 'other.mp4'):
            with self.assertRaises(ValueError):
                self.library.path(name)
        clip = self.clip(1)
        self.library.protect(clip.name, True)
        with self.assertRaises(ValueError):
            self.library.delete(clip.name)
        self.assertTrue(clip.exists())

    def test_symlink_is_never_deleted(self):
        clip = self.clip(1)
        with patch.object(Path, 'is_symlink', return_value=True):
            with self.assertRaises(ValueError):
                self.library.delete(clip.name)
        self.assertTrue(clip.exists())

    def test_native_playback_lease_prevents_python_recycling(self):
        clip = self.clip(1)
        self.library.connection.execute("INSERT INTO clip_leases VALUES(?, 'native-player', unixepoch()+120)", (clip.name,))
        self.library.connection.execute('UPDATE clips SET started=0')
        self.library.connection.commit()
        self.assertTrue(self.library.clips()[0]['in_use'])
        self.assertEqual(self.library.enforce(StoragePolicy(recycle=True, keep_days=1)), [])
        with self.assertRaises(ValueError):
            self.library.delete(clip.name)
        self.library.connection.execute('UPDATE clip_leases SET expires=0')
        self.library.connection.commit()
        self.assertEqual(self.library.enforce(StoragePolicy(recycle=True, keep_days=1)), [clip.name])


if __name__ == '__main__':
    unittest.main()

#!/usr/bin/env python3
"""Tests for vita/tools/make-kit-zip.py (the one-file kit). Run: python3 -I vita/tests/kitzip/test_make_kit_zip.py
Each refusal test is a negative control: a planted problem must stop the tool with nothing written."""
import hashlib
import io
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import zipfile

TOOL = os.environ.get("KITZIP_TOOL") or os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "tools", "make-kit-zip.py"))
BSDTAR = shutil.which("bsdtar") or ("/usr/bin/bsdtar" if os.path.exists("/usr/bin/bsdtar") else None)


def sha(b):
    return hashlib.sha256(b).hexdigest()


class Kit:
    """a small fake kit #7 laid out like the real ones"""

    def __init__(self, root):
        self.k = os.path.join(root, "kit")
        self.files = {}
        env = {"kit7-env/step1/env_play.txt": b"SHEPHERD_A=1\n", "kit7-env/x1/env_gl_measure.txt": b"SHEPHERD_B=0\n"}
        self.add("data/shepherd/vpk/k7-1-app.vpk", b"VPK1" * 1000)
        self.add("data/shepherd/vpk/probes/1-probe.vpk", b"PROBE" * 10)
        self.add("data/shepherd/managed/Shepherd.Engine.Derived.dll", b"NEWDLL" * 100)
        self.add("data/shepherd/pack/shepherd.pk2", os.urandom(300000))
        for name, data in env.items():
            self.add("extras/card-env/" + name, data)
        bio = io.BytesIO()
        with zipfile.ZipFile(bio, "w") as z:
            z.writestr("kit7-env/", b"")
            for name, data in env.items():
                z.writestr(name, data)
        self.add("data/shepherd/kit7-env-files.zip", bio.getvalue())
        self.add("alternates/alt-1-app.vpk", b"ALT" * 50)
        self.write_readme()
        self.write_sums()

    def add(self, rel, data, listed=True):
        p = os.path.join(self.k, rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)
        if listed:
            self.files[rel] = data

    def write_readme(self):
        with open(os.path.join(self.k, "README-KIT7.txt"), "w") as f:
            f.write("KIT 7 README\n")

    def write_sums(self):
        with open(os.path.join(self.k, "SHA256SUMS.txt"), "w") as f:
            for rel in sorted(self.files):
                f.write(f"{sha(self.files[rel])}  {rel}\n")


def run(*args):
    return subprocess.run([sys.executable, "-I", TOOL, *args], capture_output=True, text=True)


class MakeKitZip(unittest.TestCase):
    def setUp(self):
        self.td = tempfile.mkdtemp(prefix="kitzip-test-")
        self.kit = Kit(self.td)
        self.out = os.path.join(self.td, "shepherd-kit7.zip")

    def tearDown(self):
        shutil.rmtree(self.td)

    def refused(self, needle):
        r = run(self.kit.k)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("STOP", r.stdout)
        self.assertIn(needle, r.stdout)
        self.assertFalse(os.path.exists(self.out), "a zip was written despite STOP")
        self.assertFalse(os.path.exists(self.out + ".partial"))

    def test_layout_and_bytes(self):
        r = run(self.kit.k)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertIn("DONE", r.stdout)
        with zipfile.ZipFile(self.out) as z:
            self.assertIsNone(z.testzip())
            files = {zi.filename: z.read(zi) for zi in z.infolist() if not zi.is_dir()}
            dirs = {zi.filename for zi in z.infolist() if zi.is_dir()}
            self.assertTrue(all(zi.compress_type == zipfile.ZIP_STORED for zi in z.infolist()))
        want = {
            "shepherd/vpk/k7-1-app.vpk", "shepherd/vpk/probes/1-probe.vpk", "shepherd/managed/Shepherd.Engine.Derived.dll",
            "shepherd/pack/shepherd.pk2", "shepherd/kit7-env-files.zip", "shepherd/kit7-env/step1/env_play.txt",
            "shepherd/kit7-env/x1/env_gl_measure.txt", "shepherd/vpk/alternates/alt-1-app.vpk", "README-KIT7.txt",
        }
        self.assertEqual(set(files), want)
        self.assertIn("shepherd/", dirs)
        self.assertIn("shepherd/kit7-env/step1/", dirs)
        self.assertEqual(files["shepherd/pack/shepherd.pk2"], self.kit.files["data/shepherd/pack/shepherd.pk2"])
        self.assertEqual(files["shepherd/kit7-env/step1/env_play.txt"], b"SHEPHERD_A=1\n")
        self.assertEqual(files["shepherd/vpk/alternates/alt-1-app.vpk"], b"ALT" * 50)
        with open(self.out + ".sha256") as f:
            self.assertTrue(f.read().startswith(hashlib.sha256(open(self.out, "rb").read()).hexdigest()))

    def test_one_folder(self):
        """kit #14: --one-folder = the same members, the README inside shepherd/: every name under one top folder"""
        r = run(self.kit.k, "--one-folder")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        with zipfile.ZipFile(self.out) as z:
            files = {zi.filename for zi in z.infolist() if not zi.is_dir()}
            self.assertEqual({n.split("/")[0] for n in z.namelist()}, {"shepherd"})
            self.assertIn("shepherd/README-KIT7.txt", files)
            self.assertNotIn("README-KIT7.txt", files)
            self.assertIn("shepherd/kit7-env/step1/env_play.txt", files)

    def test_one_folder_refuses_live_env_at_top(self):
        self.kit.add("data/shepherd/env_gl_measure.txt", b"SHEPHERD_B=0\n")
        self.kit.write_sums()
        r = run(self.kit.k, "--one-folder")
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertFalse(os.path.exists(self.out))

    def test_one_folder_not_with_update(self):
        old = self.update_kit()
        out = os.path.join(self.td, "update.zip")
        r = run(self.kit.k, "--one-folder", "--update-from", old, "--out", out)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertFalse(os.path.exists(out))

    def test_mtime_kept_not_fixed(self):
        p = os.path.join(self.kit.k, "data/shepherd/managed/Shepherd.Engine.Derived.dll")
        t = time.mktime((2026, 10, 2, 13, 14, 16, 0, 0, -1))
        os.utime(p, (t, t))
        self.assertEqual(run(self.kit.k).returncode, 0)
        with zipfile.ZipFile(self.out) as z:
            self.assertEqual(z.getinfo("shepherd/managed/Shepherd.Engine.Derived.dll").date_time, (2026, 10, 2, 13, 14, 16))

    @unittest.skipUnless(BSDTAR, "needs bsdtar (libarchive)")
    def test_paste_merges_like_vitashell(self):
        """libarchive unpack of shepherd/ into a card's data/: kit files replace, every other card file stays"""
        self.assertEqual(run(self.kit.k).returncode, 0)
        card = os.path.join(self.td, "card")
        for rel, data in {"VitaShell/x": b"", "app/x": b"", "data/shepherd/persistent/slot_0.json": b"SAVE",
                          "data/shepherd/managed/Assembly-CSharp.dll": b"GAME", "data/shepherd/managed/Shepherd.Engine.Derived.dll": b"OLD",
                          "data/shepherd/shepherd.pack": b"PACK"}.items():
            p = os.path.join(card, rel)
            os.makedirs(os.path.dirname(p), exist_ok=True)
            open(p, "wb").write(data)
        r = subprocess.run([BSDTAR, "-xf", self.out, "-C", os.path.join(card, "data"), "shepherd"], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        rd = lambda rel: open(os.path.join(card, rel), "rb").read()
        self.assertEqual(rd("data/shepherd/persistent/slot_0.json"), b"SAVE")
        self.assertEqual(rd("data/shepherd/managed/Assembly-CSharp.dll"), b"GAME")
        self.assertEqual(rd("data/shepherd/shepherd.pack"), b"PACK")
        self.assertEqual(rd("data/shepherd/managed/Shepherd.Engine.Derived.dll"), b"NEWDLL" * 100)
        self.assertFalse(os.path.exists(os.path.join(card, "data/README-KIT7.txt")))
        self.assertFalse(os.path.exists(os.path.join(card, "data/shepherd/env_play.txt")))
        r = run("--check-card", self.kit.k, "--card", card)
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("PASS", r.stdout)
        self.assertIn("live env / args files at the top of ux0:data/shepherd/: none", r.stdout)

    def test_check_card_problem_and_optional(self):
        card = os.path.join(self.td, "card")
        for d in ("VitaShell", "app"):
            os.makedirs(os.path.join(card, d))
        for rel, data in self.kit.files.items():
            if rel.startswith("data/"):
                dst = rel
            elif rel.startswith("extras/card-env/"):
                dst = "data/shepherd/" + rel[len("extras/card-env/"):]
            else:
                continue  # alternates left off the card: optional
            os.makedirs(os.path.dirname(os.path.join(card, dst)), exist_ok=True)
            open(os.path.join(card, dst), "wb").write(data)
        open(os.path.join(card, "data/shepherd/env_play.txt"), "w").write("X=1\n")
        r = run("--check-card", self.kit.k, "--card", card)
        self.assertEqual(r.returncode, 0, r.stdout)
        self.assertIn("optional, fine", r.stdout)
        self.assertIn("top of ux0:data/shepherd/: env_play.txt", r.stdout)
        open(os.path.join(card, "data/shepherd/pack/shepherd.pk2"), "r+b").write(b"\0")
        r = run("--check-card", self.kit.k, "--card", card)
        self.assertEqual(r.returncode, 1)
        self.assertIn("DIFFERS  ux0:data/shepherd/pack/shepherd.pk2", r.stdout)
        self.assertEqual(run("--check-card", self.kit.k, "--card", os.path.join(self.td, "kit")).returncode, 1)  # not a card

    # ---- negative controls: each planted problem must stop it, nothing written
    def test_refuses_damaged_file(self):
        open(os.path.join(self.kit.k, "data/shepherd/vpk/k7-1-app.vpk"), "r+b").write(b"X")
        self.refused("damaged: data/shepherd/vpk/k7-1-app.vpk")

    def test_refuses_missing_file(self):
        os.remove(os.path.join(self.kit.k, "alternates/alt-1-app.vpk"))
        self.refused("missing: alternates/alt-1-app.vpk")

    def test_refuses_unlisted_file(self):
        self.kit.add("data/shepherd/stray.bin", b"?", listed=False)
        self.refused("data/shepherd/stray.bin")

    def test_ignores_mac_junk(self):
        self.kit.add("data/shepherd/vpk/._k7-1-app.vpk", b"\x00\x05\x16\x07", listed=False)
        self.kit.add("data/shepherd/.DS_Store", b"junk", listed=False)
        self.assertEqual(run(self.kit.k).returncode, 0)
        with zipfile.ZipFile(self.out) as z:
            self.assertFalse([n for n in z.namelist() if "._" in n or "DS_Store" in n])

    def test_refuses_env_zip_mismatch(self):
        self.kit.add("extras/card-env/kit7-env/step1/env_play.txt", b"SHEPHERD_A=2\n")
        self.kit.write_sums()
        self.refused("differs from extras/card-env/kit7-env/")

    def test_refuses_env_zip_missing_member(self):
        self.kit.add("extras/card-env/kit7-env/step9/env_play.txt", b"Z=1\n")
        self.kit.write_sums()
        self.refused("only in the folder")

    def test_refuses_live_env_at_top(self):
        self.kit.add("data/shepherd/env_play.txt", b"SHEPHERD_A=1\n")
        self.kit.write_sums()
        self.refused("TOP of ux0:data/shepherd/")

    def test_refuses_case_collision(self):
        self.kit.add("data/shepherd/vpk/alternates/ALT-1-APP.vpk", b"other")
        self.kit.write_sums()
        self.refused("exFAT ignores case")

    def test_refuses_non_ascii(self):
        self.kit.add("data/shepherd/data/café.txt", b"x")
        self.kit.write_sums()
        self.refused("not a plain ASCII name")

    def update_kit(self):
        """the older kit = a copy of this one; this one then changes a VPK, gains a file and a README-KIT7-1.txt"""
        old = os.path.join(self.td, "old")
        shutil.copytree(self.kit.k, old)
        self.kit.add("data/shepherd/vpk/k7-1-app.vpk", b"VPK2" * 1000)
        self.kit.add("data/shepherd/data/video/new.shv", b"NEW" * 100)
        with open(os.path.join(self.kit.k, "README-KIT7-1.txt"), "w") as f:
            f.write("KIT 7.1 README\n")
        with open(os.path.join(self.kit.k, "README-KIT7-1.txt"), "rb") as f:
            self.kit.files["README-KIT7-1.txt"] = f.read()
        with open(os.path.join(self.kit.k, "README-KIT7.txt"), "rb") as f:
            self.kit.files["README-KIT7.txt"] = f.read()
        self.kit.write_sums()
        return old

    def test_update_zip_only_what_changed(self):
        old = self.update_kit()
        out = os.path.join(self.td, "update.zip")
        r = run(self.kit.k, "--update-from", old, "--out", out)
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        with zipfile.ZipFile(out) as z:
            files = sorted(zi.filename for zi in z.infolist() if not zi.is_dir())
            self.assertEqual(files, ["shepherd/README-KIT7-1.txt", "shepherd/README-KIT7.txt", "shepherd/data/video/new.shv", "shepherd/vpk/k7-1-app.vpk"])
            self.assertEqual({n.split("/")[0] for n in z.namelist()}, {"shepherd"})
            self.assertEqual(z.read("shepherd/vpk/k7-1-app.vpk"), b"VPK2" * 1000)

    def test_update_refuses_a_lost_file(self):
        old = self.update_kit()
        del self.kit.files["data/shepherd/pack/shepherd.pk2"]
        os.remove(os.path.join(self.kit.k, "data/shepherd/pack/shepherd.pk2"))
        self.kit.write_sums()
        out = os.path.join(self.td, "update.zip")
        r = run(self.kit.k, "--update-from", old, "--out", out)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertIn("cannot delete", r.stdout)
        self.assertFalse(os.path.exists(out))

    def test_update_refuses_live_env_at_top(self):
        old = self.update_kit()
        self.kit.add("data/shepherd/env_play.txt", b"SHEPHERD_A=1\n")
        self.kit.write_sums()
        out = os.path.join(self.td, "update.zip")
        r = run(self.kit.k, "--update-from", old, "--out", out)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertFalse(os.path.exists(out))

    def test_update_needs_out(self):
        old = self.update_kit()
        r = run(self.kit.k, "--update-from", old)
        self.assertEqual(r.returncode, 1, r.stdout + r.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.td, "shepherd-kit7.zip")))

    def test_refuses_two_readmes(self):
        open(os.path.join(self.kit.k, "README-KIT8.txt"), "w").write("x")
        self.refused("exactly one README-KIT")


if __name__ == "__main__":
    unittest.main(verbosity=2)

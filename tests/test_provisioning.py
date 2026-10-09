import unittest

from yicam.provisioning import decode_password, decode_text, encode_password, encode_text, parse, serialize, token_cases


class ProvisioningTests(unittest.TestCase):
    def test_java_password_fixtures(self):
        self.assertEqual(encode_password("89"), "ODk=")
        self.assertEqual(encode_password("password123"), "SFg5NSQFHVx5Z1E=")
        for value in ("", "89", "password123", "salasana-äö-🔑", "a" * 150):
            self.assertEqual(decode_password(encode_password(value)), value)

    def test_raw_base64_not_url_encoding(self):
        payload = "b=EU_TEST&s=" + encode_text("WiFi--࠾") + "&p="
        self.assertIn("+", payload)
        self.assertEqual(decode_text(parse(payload)["s"]), "WiFi--࠾")
        self.assertEqual(serialize(parse(payload)), payload)

    def test_variants_preserve_network_and_region(self):
        original = "b=EU_synthetic1234&s=VGVzdA==&p=ODk="
        cases = token_cases(original)
        self.assertEqual(cases["issued-reference"], original)
        for name, payload in cases.items():
            fields = parse(payload)
            self.assertEqual(fields["s"], "VGVzdA==")
            self.assertEqual(fields["p"], "ODk=")
            if name in ("random-same-region", "altered-same-region"):
                self.assertTrue(fields["b"].startswith("EU"))
                self.assertEqual(len(fields["b"]), len("EU_synthetic1234"))
                self.assertNotEqual(fields["b"], "EU_synthetic1234")
        self.assertNotIn("b", parse(cases["missing-field"]))
        self.assertEqual(parse(cases["empty-field"])["b"], "")

    def test_reject_ambiguous_or_unsupported_input(self):
        for value in ("b=X&s=QQ==&p=&s=Qg==", "s=*&p=", "b=X&p=", "b=X&s=QQ==&p=&evil=1"):
            with self.assertRaises(ValueError):
                parse(value)
        with self.assertRaises(ValueError):
            token_cases("t=1&s=QQ==&p=&d=TEST")
        with self.assertRaises(ValueError):
            encode_password("\0")

"""Pure reference-conversion controls; require no installed CAN packages."""
from types import SimpleNamespace
import unittest

from make_independent_dbc_reference import exact_raw, safe_label


class IndependentDbcReferenceTests(unittest.TestCase):
    def test_unsigned_64bit_code_does_not_round_before_raw_export(self):
        signal = SimpleNamespace(is_float=False, length=64, choices={})
        bits, coded, exact, meaning = exact_raw(signal, (1 << 64) - 1)
        self.assertEqual((1 << 64) - 1, bits)
        self.assertEqual("18446744073709551615", coded)
        self.assertEqual(0, exact)
        self.assertEqual("", meaning)

    def test_signed_minus_one_keeps_two_complement_bits(self):
        signal = SimpleNamespace(is_float=False, length=12, choices={-1: "Invalid"})
        bits, coded, exact, meaning = exact_raw(signal, -1)
        self.assertEqual(0xFFF, bits)
        self.assertEqual("-1", coded)
        self.assertEqual(1, exact)
        self.assertEqual("Invalid", meaning)

    def test_float32_keeps_exact_encoded_ieee_bits(self):
        signal = SimpleNamespace(is_float=True, length=32, choices={})
        bits, coded, exact, meaning = exact_raw(signal, 1.25)
        self.assertEqual(0x3FA00000, bits)
        self.assertEqual("IEEE-754 bit pattern", coded)
        self.assertEqual(0, exact)
        self.assertEqual("", meaning)

    def test_no_automatic_spreadsheet_formula_execution_from_dbc_names(self):
        self.assertEqual("'=SUM(1,2)", safe_label("=SUM(1,2)"))
        self.assertEqual("'  @macro", safe_label("  @macro"))
        self.assertEqual("Safe_Name", safe_label("Safe_Name"))

    def test_cannot_mislabel_wrong_size_float(self):
        signal = SimpleNamespace(is_float=True, length=16, choices={})
        with self.assertRaises(ValueError):
            exact_raw(signal, 3.5)


if __name__ == "__main__":
    unittest.main()

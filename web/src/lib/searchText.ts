/**
 * The client twin of the API's `SearchText.Normalize` (global #13): case- and accent-insensitive, with the Turkish dotted and dotless i
 * folded first, so "İstanbul", "Istanbul" and "istanbul" are one word. Used where the client must predict the API's comparison.
 */
export function normalizeSearch(value: string): string {
  return value.trim().replace(/[ıİI]/g, "i").normalize("NFD").replace(/\p{Mn}/gu, "").toLowerCase().normalize("NFC");
}

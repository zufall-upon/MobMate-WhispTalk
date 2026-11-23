namespace MobMate.Core.Asr;

// Recognition language declarations, not UI locales or installed-model proof.
internal static class WhisperLanguageCatalog
{
    internal static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["af"] = "Afrikaans (af)", ["am"] = "Amharic (am)", ["ar"] = "Arabic (ar)", ["as"] = "Assamese (as)", ["az"] = "Azerbaijani (az)",
        ["ba"] = "Bashkir (ba)", ["be"] = "Belarusian (be)", ["bg"] = "Bulgarian (bg)", ["bn"] = "Bengali (bn)", ["bo"] = "Tibetan (bo)", ["br"] = "Breton (br)", ["bs"] = "Bosnian (bs)",
        ["ca"] = "Catalan (ca)", ["cs"] = "Czech (cs)", ["cy"] = "Welsh (cy)", ["da"] = "Danish (da)", ["de"] = "German (de)", ["el"] = "Greek (el)", ["en"] = "English (en)", ["es"] = "Spanish (es)", ["et"] = "Estonian (et)", ["eu"] = "Basque (eu)",
        ["fa"] = "Persian (fa)", ["fi"] = "Finnish (fi)", ["fo"] = "Faroese (fo)", ["fr"] = "French (fr)", ["gl"] = "Galician (gl)", ["gu"] = "Gujarati (gu)",
        ["ha"] = "Hausa (ha)", ["haw"] = "Hawaiian (haw)", ["he"] = "Hebrew (he)", ["hi"] = "Hindi (hi)", ["hr"] = "Croatian (hr)", ["ht"] = "Haitian Creole (ht)", ["hu"] = "Hungarian (hu)", ["hy"] = "Armenian (hy)",
        ["id"] = "Indonesian (id)", ["is"] = "Icelandic (is)", ["it"] = "Italian (it)", ["ja"] = "Japanese (ja)", ["jw"] = "Javanese (jw)",
        ["ka"] = "Georgian (ka)", ["kk"] = "Kazakh (kk)", ["km"] = "Khmer (km)", ["kn"] = "Kannada (kn)", ["ko"] = "Korean (ko)",
        ["la"] = "Latin (la)", ["lb"] = "Luxembourgish (lb)", ["ln"] = "Lingala (ln)", ["lo"] = "Lao (lo)", ["lt"] = "Lithuanian (lt)", ["lv"] = "Latvian (lv)",
        ["mg"] = "Malagasy (mg)", ["mi"] = "Maori (mi)", ["mk"] = "Macedonian (mk)", ["ml"] = "Malayalam (ml)", ["mn"] = "Mongolian (mn)", ["mr"] = "Marathi (mr)", ["ms"] = "Malay (ms)", ["mt"] = "Maltese (mt)", ["my"] = "Myanmar (my)",
        ["ne"] = "Nepali (ne)", ["nl"] = "Dutch (nl)", ["nn"] = "Norwegian Nynorsk (nn)", ["no"] = "Norwegian (no)", ["oc"] = "Occitan (oc)",
        ["pa"] = "Punjabi (pa)", ["pl"] = "Polish (pl)", ["ps"] = "Pashto (ps)", ["pt"] = "Portuguese (pt)",
        ["ro"] = "Romanian (ro)", ["ru"] = "Russian (ru)", ["sa"] = "Sanskrit (sa)", ["sd"] = "Sindhi (sd)", ["si"] = "Sinhala (si)", ["sk"] = "Slovak (sk)", ["sl"] = "Slovenian (sl)", ["sn"] = "Shona (sn)", ["so"] = "Somali (so)", ["sq"] = "Albanian (sq)", ["sr"] = "Serbian (sr)", ["su"] = "Sundanese (su)", ["sv"] = "Swedish (sv)", ["sw"] = "Swahili (sw)",
        ["ta"] = "Tamil (ta)", ["te"] = "Telugu (te)", ["tg"] = "Tajik (tg)", ["th"] = "Thai (th)", ["tk"] = "Turkmen (tk)", ["tl"] = "Tagalog (tl)", ["tr"] = "Turkish (tr)", ["tt"] = "Tatar (tt)",
        ["uk"] = "Ukrainian (uk)", ["ur"] = "Urdu (ur)", ["uz"] = "Uzbek (uz)", ["vi"] = "Vietnamese (vi)", ["yi"] = "Yiddish (yi)", ["yo"] = "Yoruba (yo)", ["yue"] = "Cantonese (yue)", ["zh"] = "Chinese (zh)",
    };
    internal static int PreferredOrder(string code) => code switch { "ja" => 0, "en" => 1, "zh" => 2, "ko" => 3, _ => 10 };
    internal static string[] OrderedCodes() => Labels.Keys.OrderBy(PreferredOrder).ThenBy(x => x).ToArray();
}

using System.Text;
using System.Xml.Linq;

namespace WarbandStudio.Pack;

internal static class DuplicateUpgradeUiRecovery
{
    public static bool TryRebase(byte[] original, byte[] compiled, byte[] current, out byte[] logical)
    {
        logical = current;
        try
        {
            var o = XDocument.Parse(Encoding.UTF8.GetString(original), LoadOptions.PreserveWhitespace);
            var c = XDocument.Parse(Encoding.UTF8.GetString(compiled), LoadOptions.PreserveWhitespace);
            var e = XDocument.Parse(Encoding.UTF8.GetString(current), LoadOptions.PreserveWhitespace);
            string Key(XElement cb)
            {
                var owner = cb.AncestorsAndSelf().FirstOrDefault(n => n.Attribute("this") is not null || n.Attribute("id") is not null);
                return $"{owner?.Attribute("this")?.Value ?? owner?.Attribute("id")?.Value}|{cb.Attribute("callback_id")?.Value}|{cb.Attribute("context_object_id")?.Value}";
            }
            Dictionary<string, XElement> Callbacks(XDocument d) => d.Descendants().Where(n => n.Name.LocalName == "callback_with_context")
                .GroupBy(Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
            var authored = Callbacks(o); var before = Callbacks(c); var external = Callbacks(e);
            var changes = 0;
            foreach (var (key, old) in before)
            {
                if (!authored.TryGetValue(key, out var source) || !external.TryGetValue(key, out var now)) continue;
                var generated = (string?)old.Attribute("context_function_id");
                var plain = (string?)source.Attribute("context_function_id");
                if (generated != plain && (string?)now.Attribute("context_function_id") == generated)
                {
                    now.SetAttributeValue("context_function_id", plain);
                    changes++;
                }
            }
            if (changes == 0) return false;
            logical = Encoding.UTF8.GetBytes(e.ToString(SaveOptions.DisableFormatting));
            return true;
        }
        catch (System.Xml.XmlException) { return false; }
    }
}

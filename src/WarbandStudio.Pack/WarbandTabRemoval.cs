using System.Text;
using System.Xml.Linq;

namespace WarbandStudio.Pack;

public static class WarbandTabRemoval
{
    private const string TemplateMarker = "WarbandStudio.EmptyTabTemplate.v1:";

    public static string RemoveXml(string xml, string category, bool keepLastTemplate = true)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var holderName = "holder_tab_" + category;
        var buttonName = "button_toggle_tab_" + category;
        bool BelongsToTab(XElement e) => IsNamed(e, holderName) || IsNamed(e, buttonName)
            || e.Parent?.Name.LocalName == "states" && IsNamed(e, category)
                && e.Ancestors().Any(parent => IsNamed(parent, "warband_upgrades"));
        var parts = document.Descendants().Where(BelongsToTab).ToHashSet();
        if (parts.Count == 0) return xml;

        var cleared = TabFactionVisibility.ClearCategory(xml, document, category);
        if (cleared != xml)
        {
            document = XDocument.Parse(cleared, LoadOptions.PreserveWhitespace);
            parts = document.Descendants().Where(BelongsToTab).ToHashSet();
        }
        // Preserve the last donor at its original XML positions so a zero-page pack can create a page again.
        // The game and existing-tab parser see only comments, without the holder or its background state.
        var park = keepLastTemplate && WarbandNewTab.ExistingTabs(xml) is [var last]
            && last.Equals(category, StringComparison.OrdinalIgnoreCase);
        foreach (var part in parts.Where(e => !e.Ancestors().Any(parts.Contains)).ToArray())
        {
            if (park)
                part.ReplaceWith(new XComment(TemplateMarker + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(part.ToString(SaveOptions.DisableFormatting)))));
            else part.Remove();
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    internal static string RestoreTemplate(string xml)
    {
        if (!xml.Contains(TemplateMarker, StringComparison.Ordinal)) return xml;
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        foreach (var comment in document.DescendantNodes().OfType<XComment>()
                     .Where(c => c.Value.StartsWith(TemplateMarker, StringComparison.Ordinal)).ToArray())
        {
            var part = Encoding.UTF8.GetString(Convert.FromBase64String(comment.Value[TemplateMarker.Length..]));
            comment.ReplaceWith(XElement.Parse(part, LoadOptions.PreserveWhitespace));
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static bool IsNamed(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
        || string.Equals((string?)element.Attribute("id"), name, StringComparison.OrdinalIgnoreCase)
        || string.Equals((string?)element.Attribute("name"), name, StringComparison.OrdinalIgnoreCase);
}

using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using Confuser.Renamer.BAML;
using dnlib.DotNet;

namespace FModelCnPatcher;

public static partial class BamlLocalizer
{
    private static readonly HashSet<short> UiKnownPropIds =
    [
        -1,   // AccessText.Text
        -14,  // ContentControl.Content
        -19,  // ContentPresenter.Content
        -65,  // GridViewColumn.Header
        -67,  // HeaderedContentControl.Header
        -71,  // HeaderedItemsControl.Header
        -85,  // Page.Content
        -110, // TextBlock.Text
        -114, // TextBox.Text
        -147, // Button.Content
        -148, // ButtonBase.Content
        -152, // CheckBox.Content
        -155, // ComboBoxItem.Content
        -166, // Expander.Content
        -177, // GridViewColumnHeader.Content
        -178, // GroupBox.Content
        -179, // GroupItem.Content
        -180, // HeaderedContentControl.Content
        -194, // Label.Content
        -198, // ListBoxItem.Content
        -201, // ListViewItem.Content
        -221, // RadioButton.Content
        -223, // RepeatButton.Content
        -226, // Run.Text
        -235, // StatusBarItem.Content
        -240, // TabItem.Content
        -248, // ToggleButton.Content
        -253, // ToolTip.Content
        -259, // UserControl.Content
        -266  // Window.Content
    ];

    private static readonly HashSet<string> UiPropNames = new(StringComparer.Ordinal)
    {
        "Header", "Content", "Title", "ToolTip", "Text", "Watermark", "Placeholder",
        "Description", "Label", "Caption", "SubHeader", "Message", "EmptyText",
        "HeaderStringFormat", "StringFormat"
    };

    // KnownElements negative IDs for WPF UI text-bearing elements (excluding Binding -20, MultiBinding -416, RelativeSource -516, etc.)
    private static readonly HashSet<short> UiTextKnownElementIds =
    [
        -2,   // AccessText
        -49,  // Bold
        -55,  // Button
        -84,  // CheckBox
        -109, // ContentControl
        -202, // Expander
        -263, // GroupBox
        -277, // Hyperlink
        -311, // Italic
        -326, // Label
        -403, // MenuItem
        -441, // Paragraph
        -493, // RadioButton
        -504, // RepeatButton
        -542, // Run
        -571, // Span
        -606, // StatusBarItem
        -624, // TabItem
        -638, // TextBlock
        -668, // ToggleButton
        -671, // ToolTip
        -694  // Underline
    ];

    private const short KnownSeparatorId = -554;
    private const short KnownSetterId = -556;

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespaceRegex();

    public static int LocalizeFModelResources(ModuleDefMD fmodelModule, LangPack lang, out int totalEntries)
    {
        totalEntries = 0;
        var gRes = fmodelModule.Resources.OfType<EmbeddedResource>()
            .FirstOrDefault(r => r.Name.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
        if (gRes == null)
            throw new InvalidOperationException("在 FModel.dll 中找不到 *.g.resources 嵌入资源。");

        using var inputStream = gRes.CreateReader().AsStream();
        using var rr = new ResourceReader(inputStream);
        using var outResStream = new MemoryStream();
        using (var rw = new ResourceWriter(outResStream))
        {
            var en = rr.GetEnumerator();
            while (en.MoveNext())
            {
                var key = en.Key?.ToString() ?? string.Empty;
                if (key.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
                {
                    rr.GetResourceData(key, out string typeName, out byte[] rawWithPrefix);
                    byte[] bamlBytes;
                    if (typeName == "ResourceTypeCode.Stream" && rawWithPrefix.Length >= 4)
                    {
                        int len = BitConverter.ToInt32(rawWithPrefix, 0);
                        bamlBytes = new byte[len];
                        Buffer.BlockCopy(rawWithPrefix, 4, bamlBytes, 0, len);
                    }
                    else
                    {
                        bamlBytes = (en.Value as Stream) is Stream stm ? ReadStreamAll(stm) : Array.Empty<byte>();
                    }

                    byte[] patchedBytes = LocalizeSingleBaml(bamlBytes, lang, out int count);
                    totalEntries += count;

                    byte[] wrapped = new byte[4 + patchedBytes.Length];
                    BitConverter.GetBytes(patchedBytes.Length).CopyTo(wrapped, 0);
                    Buffer.BlockCopy(patchedBytes, 0, wrapped, 4, patchedBytes.Length);
                    rw.AddResourceData(key, "ResourceTypeCode.Stream", wrapped);
                }
                else
                {
                    rr.GetResourceData(key, out string typeName, out byte[] rawData);
                    rw.AddResourceData(key, typeName, rawData);
                }
            }
            rw.Generate();
        }

        byte[] newResourceBytes = outResStream.ToArray();
        var attrs = gRes.Attributes;
        var name = gRes.Name;
        fmodelModule.Resources.Remove(gRes);
        fmodelModule.Resources.Add(new EmbeddedResource(name, newResourceBytes, attrs));
        return totalEntries;
    }

    private static byte[] LocalizeSingleBaml(byte[] bamlBytes, LangPack lang, out int replacedCount)
    {
        replacedCount = 0;
        BamlDocument doc;
        using (var ms = new MemoryStream(bamlBytes))
        {
            doc = BamlReader.ReadDocument(ms);
        }

        var typeMap = new Dictionary<short, string>();
        var attrMap = new Dictionary<short, string>();
        foreach (var rec in doc)
        {
            if (rec is TypeInfoRecord ti)
                typeMap[unchecked((short)ti.TypeId)] = ti.TypeFullName;
            else if (rec is AttributeInfoRecord ai)
                attrMap[unchecked((short)ai.AttributeId)] = ai.Name;
        }

        var elemStack = new Stack<short>();
        bool anyModified = false;

        foreach (var rec in doc)
        {
            switch (rec)
            {
                case ElementStartRecord es:
                    elemStack.Push(unchecked((short)es.TypeId));
                    break;
                case ElementEndRecord:
                    if (elemStack.Count > 0) elemStack.Pop();
                    break;
                case TextRecord tr:
                {
                    if (elemStack.Count == 0) break;
                    short topElem = elemStack.Peek();
                    bool isUiElement = UiTextKnownElementIds.Contains(topElem)
                        || (topElem >= 0 && typeMap.TryGetValue(topElem, out var tn) && IsUiCustomElementType(tn));
                    if (!isUiElement) break;

                    if (TryTranslateBamlText(tr.Value, lang.BamlStrings, out var translated))
                    {
                        tr.Value = translated;
                        replacedCount++;
                        anyModified = true;
                    }
                    break;
                }
                case PropertyRecord pr:
                {
                    if (string.IsNullOrEmpty(pr.Value)) break;
                    short aid = unchecked((short)pr.AttributeId);
                    short topElem = elemStack.Count > 0 ? elemStack.Peek() : (short)0;

                    if (aid < 0)
                    {
                        if (!UiKnownPropIds.Contains(aid)) break;
                        if (TryTranslateBamlPropertyValue(pr.Value, lang.BamlStrings, out var translated))
                        {
                            pr.Value = translated;
                            replacedCount++;
                            anyModified = true;
                        }
                    }
                    else if (attrMap.TryGetValue(aid, out var propName))
                    {
                        var shortProp = propName.Contains('.') ? propName[(propName.LastIndexOf('.') + 1)..] : propName;
                        if (topElem == KnownSetterId && string.Equals(shortProp, "Value", StringComparison.Ordinal))
                        {
                            if (TryTranslateBamlPropertyValue(pr.Value, lang.BamlSetterStrings, out var setterTranslated)
                                || TryTranslateBamlPropertyValue(pr.Value, lang.BamlStrings, out setterTranslated))
                            {
                                pr.Value = setterTranslated;
                                replacedCount++;
                                anyModified = true;
                            }
                        }
                        else if (topElem == KnownSeparatorId && string.Equals(shortProp, "Tag", StringComparison.Ordinal))
                        {
                            if (TryTranslateBamlPropertyValue(pr.Value, lang.BamlTagStrings, out var tagTranslated))
                            {
                                pr.Value = tagTranslated;
                                replacedCount++;
                                anyModified = true;
                            }
                        }
                        else if (UiPropNames.Contains(shortProp))
                        {
                            if (TryTranslateBamlPropertyValue(pr.Value, lang.BamlStrings, out var propTranslated))
                            {
                                pr.Value = propTranslated;
                                replacedCount++;
                                anyModified = true;
                            }
                        }
                    }
                    break;
                }
            }
        }

        if (!anyModified)
            return bamlBytes;

        using var outMs = new MemoryStream();
        BamlWriter.WriteDocument(doc, outMs);
        return outMs.ToArray();
    }

    private static bool IsUiCustomElementType(string typeFullName)
    {
        return typeFullName.EndsWith("SplitButton", StringComparison.Ordinal)
               || typeFullName.EndsWith("TextBlock", StringComparison.Ordinal)
               || typeFullName.EndsWith("MenuItem", StringComparison.Ordinal);
    }

    private static bool TryTranslateBamlText(string original, Dictionary<string, string> dict, out string translated)
    {
        if (TryTranslateBamlPropertyValue(original, dict, out translated))
            return true;

        var normalized = MultiWhitespaceRegex().Replace(original.Trim(), " ");
        if (!string.IsNullOrEmpty(normalized) && normalized != original && TryTranslateBamlPropertyValue(normalized, dict, out translated))
            return true;

        return false;
    }

    private static bool TryTranslateBamlPropertyValue(string original, Dictionary<string, string> dict, out string translated)
    {
        translated = original;
        if (string.IsNullOrWhiteSpace(original))
            return false;

        if (original.StartsWith('{'))
        {
            if (original.StartsWith("{}"))
            {
                var body = original[2..];
                if (dict.TryGetValue(body, out var bodyTr))
                {
                    translated = "{}" + bodyTr;
                    return true;
                }
            }
            return false;
        }

        if (dict.TryGetValue(original, out var exact))
        {
            translated = exact;
            return true;
        }

        var trimmed = original.Trim();
        if (trimmed != original && dict.TryGetValue(trimmed, out var trimmedTr))
        {
            int lead = original.Length - original.TrimStart().Length;
            int trail = original.Length - original.TrimEnd().Length;
            translated = string.Concat(original.AsSpan(0, lead), trimmedTr, original.AsSpan(original.Length - trail));
            return true;
        }

        return false;
    }

    private static byte[] ReadStreamAll(Stream stm)
    {
        using var ms = new MemoryStream();
        stm.CopyTo(ms);
        return ms.ToArray();
    }
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaVarAnalyzer.Openness
{
    // Porting C# di Convert-AccessToText + Get-MatchesFromBlockXml del bridge PowerShell.
    // Ricostruisce gli operandi dal SimaticML e applica le stesse regex del parser PDF.
    public static class SimaticMlParser
    {
        static readonly string[] PreferredLangs = { "it-IT", "it", "en-US", "en" };

        static readonly Regex VsRe =
            new Regex(@"\[([^\]]+)\]\s*\.\s*vs_?pos\[(\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex AppRe =
            new Regex(@"additionalPiecePresence([1-8])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Ws = new Regex(@"\s+", RegexOptions.Compiled);

        public static void Parse(XmlDocument doc, string blockName, List<VsRow> vsOut, List<AppRow> appOut)
        {
            var accesses = doc.SelectNodes("//*[local-name()='Access']");
            if (accesses == null) return;

            int line = 0;
            foreach (XmlNode node in accesses)
            {
                var acc = node as XmlElement;
                if (acc == null) continue;

                // salta gli Access che sono indici annidati di un Component
                if (acc.ParentNode is XmlElement pe && pe.LocalName == "Component") continue;

                string text = AccessToText(acc);
                if (string.IsNullOrEmpty(text)) continue;
                line++;

                string seg = SegmentTitle(acc);
                string op = OperationFromContext(acc);

                foreach (Match m in VsRe.Matches(text))
                {
                    vsOut.Add(new VsRow
                    {
                        Asse = m.Groups[1].Value.Trim().Trim('"'),
                        Indice = int.Parse(m.Groups[2].Value),
                        Blocco = blockName,
                        Segmento = seg,
                        Operazione = op,
                        Commento = "",
                        Testo = text,
                        Linea = line
                    });
                }

                foreach (Match m in AppRe.Matches(text))
                {
                    appOut.Add(new AppRow
                    {
                        Numero = int.Parse(m.Groups[1].Value),
                        Blocco = blockName,
                        Segmento = seg,
                        Operazione = op,
                        Commento = "",
                        Testo = text,
                        Linea = line
                    });
                }
            }
        }

        // Ricostruisce il testo TIA di un nodo <Access>.
        static string AccessToText(XmlElement access)
        {
            if (access == null) return "";
            string scope = access.GetAttribute("Scope");

            if (scope.IndexOf("Constant", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var cv = access.SelectSingleNode(".//*[local-name()='ConstantValue']");
                return (cv != null ? cv.InnerText : access.InnerText).Trim();
            }

            var symbol = FirstChild(access, "Symbol");
            if (symbol != null)
            {
                var parts = new List<string>();
                foreach (var comp in Children(symbol, "Component"))
                {
                    string cname = comp.GetAttribute("Name");
                    var idx = new List<string>();
                    foreach (var ia in Children(comp, "Access")) idx.Add(AccessToText(ia));
                    if (idx.Count > 0) cname += "[" + string.Join(",", idx) + "]";
                    parts.Add(cname);
                }
                return string.Join(".", parts);
            }

            return access.InnerText.Trim();
        }

        static string OperationFromContext(XmlElement access)
        {
            XmlNode n = access.ParentNode;
            int d = 0;
            while (n != null && d < 6)
            {
                if (n.LocalName == "Part")
                {
                    string pn = (n as XmlElement)?.GetAttribute("Name") ?? "";
                    switch (pn)
                    {
                        case "Coil": return "=";
                        case "SCoil":
                        case "SetCoil": return "S";
                        case "RCoil":
                        case "ResetCoil": return "R";
                        case "Contact": return "";
                        default: return pn;
                    }
                }
                n = n.ParentNode; d++;
            }
            return "";
        }

        static string SegmentTitle(XmlElement access)
        {
            XmlNode n = access.ParentNode;
            int d = 0;
            while (n != null && d < 30)
            {
                if (n.LocalName.IndexOf("CompileUnit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var title = (n as XmlElement)?.SelectSingleNode(
                        ".//*[local-name()='MultilingualText'][@CompositionName='Title']") as XmlElement;
                    if (title != null)
                    {
                        string t = MultilingualText(title);
                        if (!string.IsNullOrEmpty(t)) return t;
                    }
                    return "";
                }
                n = n.ParentNode; d++;
            }
            return "";
        }

        static string MultilingualText(XmlElement node)
        {
            if (node == null) return "";
            var items = node.SelectNodes(".//*[local-name()='MultilingualTextItem']");
            if (items == null || items.Count == 0)
                return Ws.Replace(node.InnerText, " ").Trim();

            foreach (var lang in PreferredLangs)
            {
                foreach (XmlNode itNode in items)
                {
                    var it = itNode as XmlElement;
                    if (it == null) continue;
                    var culture = it.SelectSingleNode(".//*[local-name()='Culture']");
                    var txt = it.SelectSingleNode(".//*[local-name()='Text']");
                    if (culture != null && culture.InnerText == lang && txt != null)
                    {
                        string t = Ws.Replace(txt.InnerText, " ").Trim();
                        if (t.Length > 0) return t;
                    }
                }
            }
            foreach (XmlNode itNode in items)
            {
                var txt = (itNode as XmlElement)?.SelectSingleNode(".//*[local-name()='Text']");
                if (txt != null)
                {
                    string t = Ws.Replace(txt.InnerText, " ").Trim();
                    if (t.Length > 0) return t;
                }
            }
            return "";
        }

        static IEnumerable<XmlElement> Children(XmlElement node, string localName)
        {
            foreach (XmlNode c in node.ChildNodes)
                if (c is XmlElement el && el.LocalName == localName)
                    yield return el;
        }

        static XmlElement FirstChild(XmlElement node, string localName)
        {
            foreach (var el in Children(node, localName)) return el;
            return null;
        }
    }
}

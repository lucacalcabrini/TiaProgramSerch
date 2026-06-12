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

            // Raggruppa accessi per CompileUnit (reference equality — XmlNode non sovrascrive Equals)
            var groups = new Dictionary<XmlNode, List<XmlElement>>();
            var groupOrder = new List<XmlNode>();

            foreach (XmlNode node in accesses)
            {
                var acc = node as XmlElement;
                if (acc == null) continue;
                if (acc.ParentNode is XmlElement pe && pe.LocalName == "Component") continue;

                XmlNode key = (XmlNode)FindCompileUnit(acc) ?? doc;
                if (!groups.ContainsKey(key)) { groups[key] = new List<XmlElement>(); groupOrder.Add(key); }
                groups[key].Add(acc);
            }

            int line = 0;
            foreach (var cuKey in groupOrder)
            {
                var accList = groups[cuKey];
                var cuEl = cuKey as XmlElement;

                string seg = cuEl != null ? SegmentTitleFromCu(cuEl) : "";

                // Contesto: tutti gli operandi distinti del CompileUnit (pseudo-listato del segmento)
                var ctxLines = new List<string>();
                foreach (var a in accList)
                {
                    string t = AccessToText(a);
                    if (!string.IsNullOrEmpty(t) && !ctxLines.Contains(t)) ctxLines.Add(t);
                }
                string contesto = string.Join("\n", ctxLines);

                foreach (var acc in accList)
                {
                    string text = AccessToText(acc);
                    if (string.IsNullOrEmpty(text)) continue;
                    line++;

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
                            Linea = line,
                            Contesto = contesto
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
                            Linea = line,
                            Contesto = contesto
                        });
                    }
                }
            }
        }

        static XmlElement FindCompileUnit(XmlElement acc)
        {
            XmlNode n = acc.ParentNode;
            int d = 0;
            while (n != null && d < 30)
            {
                if (n.LocalName.IndexOf("CompileUnit", StringComparison.OrdinalIgnoreCase) >= 0)
                    return n as XmlElement;
                n = n.ParentNode; d++;
            }
            return null;
        }

        // Ricostruisce il testo TIA di un nodo <Access>.
        static string AccessToText(XmlElement access)
        {
            if (access == null) return "";
            string scope = access.GetAttribute("Scope");

            if (scope.IndexOf("Constant", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var cv = access.SelectSingleNode(".//*[local-name()='ConstantValue']");
                if (cv != null) return cv.InnerText.Trim();
                // GlobalConstant: <Constant Name="SomeName"/> — il valore è nell'attributo Name, non nel testo
                var cn = access.SelectSingleNode(".//*[local-name()='Constant']") as XmlElement;
                if (cn != null) { string n = cn.GetAttribute("Name"); if (!string.IsNullOrEmpty(n)) return n; }
                return access.InnerText.Trim();
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

        static string SegmentTitleFromCu(XmlElement cu)
        {
            if (cu == null) return "";
            var title = cu.SelectSingleNode(
                ".//*[local-name()='MultilingualText'][@CompositionName='Title']") as XmlElement;
            if (title != null)
            {
                string t = MultilingualText(title);
                if (!string.IsNullOrEmpty(t)) return t;
            }
            return "";
        }

        static string SegmentTitle(XmlElement access)
        {
            var cu = FindCompileUnit(access);
            return SegmentTitleFromCu(cu);
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

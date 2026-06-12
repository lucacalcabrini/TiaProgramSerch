using System;
using System.Collections.Generic;

namespace TiaVarAnalyzer.Openness
{
    // Il progetto è protetto (UMAC): servono nome utente e password di progetto.
    // Il chiamante può chiederli all'utente e ritentare l'export con le credenziali.
    public class ProtectedProjectException : Exception
    {
        public ProtectedProjectException(string message) : base(message) { }
    }

    // Tutti i nomi vengono serializzati in camelCase (vedi MainForm.JsonSettings),
    // quindi le chiavi JSON coincidono con quelle attese dalla UI:
    // pid, name, path, tiaVersion, asse, indice, blocco, segmento, operazione,
    // commento, testo, linea, numero, blocks, skipped, vs, app.

    public class ProjectInfo
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string TiaVersion { get; set; }
    }

    public class ProjectMeta
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string TiaVersion { get; set; }
    }

    public class Stats
    {
        public int Blocks { get; set; }
        public int Skipped { get; set; }
        public int Vs { get; set; }
        public int App { get; set; }
    }

    public class VsRow
    {
        public string Asse { get; set; }
        public int Indice { get; set; }
        public string Blocco { get; set; }
        public string Segmento { get; set; }
        public string Operazione { get; set; }
        public string Commento { get; set; }
        public string Testo { get; set; }
        public int Linea { get; set; }
        public string Contesto { get; set; }
    }

    public class AppRow
    {
        public int Numero { get; set; }
        public string Blocco { get; set; }
        public string Segmento { get; set; }
        public string Operazione { get; set; }
        public string Commento { get; set; }
        public string Testo { get; set; }
        public int Linea { get; set; }
        public string Contesto { get; set; }
    }

    // Esito dell'export completo del SW del progetto in XML (SimaticML).
    public class XmlExportResult
    {
        public string OutDir { get; set; }
        public string Project { get; set; }
        public int Plcs { get; set; }
        public int Blocks { get; set; }
        public int TagTables { get; set; }
        public int Types { get; set; }
        public int Skipped { get; set; }
    }

    public class Bundle
    {
        public string Tool { get; set; } = "tia-app";
        public int BundleVersion { get; set; } = 1;
        public string ExportedAt { get; set; }
        public ProjectMeta Project { get; set; }
        public Stats Stats { get; set; }
        public List<VsRow> Vs { get; set; } = new List<VsRow>();
        public List<AppRow> App { get; set; } = new List<AppRow>();
    }
}

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace WukongBench.Core;

public sealed record CrashSummary(int ProcessId, string CrashType, string ErrorMessage, bool? OutOfMemory,
    long? AvailablePhysicalBytes, string? Adapter, string? DriverVersion)
{
    public static CrashSummary? Parse(string xml, int expectedPid)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var document = XDocument.Load(reader);
        var runtime = document.Root?.Element("RuntimeProperties");
        string? Read(string name) => runtime?.Element(name)?.Value;
        if (!int.TryParse(Read("ProcessId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) || pid != expectedPid) return null;
        var engine = document.Root?.Element("EngineData");
        return new CrashSummary(pid, Read("CrashType") ?? "Unknown", Read("ErrorMessage") ?? "Unknown",
            Read("MemoryStats.bIsOOM") switch { "0" => false, "1" => true, _ => null },
            long.TryParse(Read("MemoryStats.AvailablePhysical"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long memory) ? memory : null,
            engine?.Element("RHI.AdapterName")?.Value, engine?.Element("RHI.InternalDriverVersion")?.Value);
    }
}


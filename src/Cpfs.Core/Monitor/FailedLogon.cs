using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Cpfs.Core.Monitor;

// One Security log event 4625 ("An account failed to log on"), read from the event's XML by Data name, so neither
// the field order nor the localized message text matters.
public sealed class FailedLogon
{
    public const int EventId = 4625;

    public const int NetworkLogonType = 3;

    // The last 24 hours, network logons only (logon type 3), as the 0.2.3 Monitor showed them.
    public const string Last24HoursQuery =
        "*[System[(EventID=4625) and TimeCreated[timediff(@SystemTime) <= 86400000]]] and *[EventData[Data[@Name='LogonType']='3']]";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    // Status and substatus codes with a known meaning (MS-ERREF NTSTATUS values; the 0.2.3 README's table plus the
    // ones a sign-in to a local account can also end in). The resource key is FailReason_<code>.
    private static readonly HashSet<string> KnownCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "0xC000006A", "0xC0000064", "0xC0000234", "0xC0000072", "0xC000015B", "0xC0000071", "0xC0000133",
        "0xC000006D", "0xC000006F", "0xC0000070", "0xC0000193", "0xC0000224",
    };

    public long? RecordId { get; private set; }

    public DateTimeOffset? Time { get; private set; }

    public string? User { get; private set; }

    public string? Domain { get; private set; }

    public string? Source { get; private set; }

    public string? Workstation { get; private set; }

    public string? Status { get; private set; }

    public string? SubStatus { get; private set; }

    public int? LogonType { get; private set; }

    // The substatus says more when it is set (wrong password under the generic "bad user name or password").
    public string? ReasonCode => SubStatus is { } sub && sub != "0x00000000" ? sub : Status;

    // FailReason_<code> for a known code, otherwise null: the UI then shows the code itself.
    public string? ReasonKey => ReasonCode is { } code && KnownCodes.Contains(code) ? "FailReason_" + code.Substring(2).ToUpperInvariant() : null;

    public static IReadOnlyCollection<string> KnownReasonKeys
    {
        get
        {
            var keys = new List<string>();
            foreach (var code in KnownCodes)
            {
                keys.Add("FailReason_" + code.Substring(2).ToUpperInvariant());
            }

            return keys;
        }
    }

    // Identifies one event across readings of the log.
    public string Key => (RecordId ?? 0).ToString(CultureInfo.InvariantCulture) + "|" + Time?.UtcTicks.ToString(CultureInfo.InvariantCulture);

    // For the app's log: when, which account name was tried and why it failed. A 4625 event holds no password.
    public string LogText => "record " + (RecordId?.ToString(CultureInfo.InvariantCulture) ?? "?")
        + ", time " + (Time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "?")
        + ", user " + (User ?? "?")
        + ", reason " + (ReasonCode ?? "?") + (Status is { } st && !string.Equals(st, ReasonCode, StringComparison.OrdinalIgnoreCase) ? " (status " + st + ")" : "");

    public static FailedLogon Parse(string eventXml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(eventXml);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Event XML could not be parsed: " + ex.Message, ex);
        }

        var root = doc.Root ?? throw new FormatException("Event XML is empty.");
        var system = root.Element(Ns + "System");
        var result = new FailedLogon();
        if (system is not null)
        {
            if (long.TryParse((string?)system.Element(Ns + "EventRecordID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                result.RecordId = id;
            }

            var stamp = (string?)system.Element(Ns + "TimeCreated")?.Attribute("SystemTime");
            if (stamp is not null && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                result.Time = time;
            }
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in root.Element(Ns + "EventData")?.Elements(Ns + "Data") ?? Array.Empty<XElement>())
        {
            var name = (string?)d.Attribute("Name");
            if (name is not null)
            {
                data[name] = d.Value.Trim();
            }
        }

        result.User = Text(data, "TargetUserName");
        result.Domain = Text(data, "TargetDomainName");
        result.Source = Text(data, "IpAddress");
        result.Workstation = Text(data, "WorkstationName");
        result.Status = Code(Text(data, "Status"));
        result.SubStatus = Code(Text(data, "SubStatus"));
        if (int.TryParse(Text(data, "LogonType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
        {
            result.LogonType = type;
        }

        return result;
    }

    // "-" is what the log writes for an empty field.
    private static string? Text(Dictionary<string, string> data, string name) =>
        data.TryGetValue(name, out var v) && v.Length > 0 && v != "-" ? v : null;

    // Normalizes "0xc000006a" or "3221225578" to "0xC000006A".
    internal static string? Code(string? value)
    {
        if (value is null)
        {
            return null;
        }

        uint n;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
            {
                return value;
            }
        }
        else if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
        {
            return value;
        }

        return "0x" + n.ToString("X8", CultureInfo.InvariantCulture);
    }
}

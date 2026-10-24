using System;

namespace Cpfs.Core.Connect;

// Every address, path and command the connect guide shows or copies, for one chosen host. None of them holds a
// password: the commands that need one prompt for it (net use with *, mount.cifs without password=).
public sealed class ConnectValues
{
    public const string MountPoint = "/mnt/cpfs";

    public ConnectValues(string host, string share, string user, string domain, bool strict)
    {
        Host = Require(host, nameof(host));
        Share = Require(share, nameof(share));
        User = Require(user, nameof(user));
        Domain = Require(domain, nameof(domain));
        Strict = strict;
    }

    public string Host { get; }

    public string Share { get; }

    public string User { get; }

    // The PC's NetBIOS name, which is the domain of a local account.
    public string Domain { get; }

    public bool Strict { get; }

    public bool IsLocalName => Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    public string QualifiedUser => Domain + "\\" + User;

    public string Unc => "\\\\" + Host + "\\" + Share;

    public string SmbUrl => "smb://" + Host + "/" + Uri.EscapeDataString(Share);

    // The 0.2.3 guide's iPhone address: the user name is in it, the password never is.
    public string IosUrl => "smb://" + Uri.EscapeDataString(User) + "@" + Host + "/" + Uri.EscapeDataString(Share);

    public string NetUse => "net use Z: " + Unc + " * /user:" + QualifiedUser + " /persistent:yes";

    public const string MountDir = "sudo mkdir -p " + MountPoint;

    // seal asks for SMB 3 encryption, which a share in strict mode requires.
    public string Mount => "sudo mount -t cifs //" + Host + "/" + Share + " " + MountPoint
        + " -o username=" + User + ",domain=" + Domain + ",vers=3.0" + (Strict ? ",seal" : "") + ",uid=$(id -u),gid=$(id -g)";

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Empty value.", name) : value.Trim();
}

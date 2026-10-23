using System.Linq;
using Cpfs.Core.Drives;
using Xunit;

namespace Cpfs.Tests;

public class LetterScanTests
{
    [Fact]
    public void ReceivedLettersCanOnlyAddTakenLetters()
    {
        var scan = new LetterScan();
        scan.Add('C', LetterSource.SystemDrive);
        scan.Add('c', LetterSource.ThisProcess);
        scan.Add('D', LetterSource.MountedDevices);
        scan.AddAll("XD", LetterSource.UserSessionReceived);

        Assert.DoesNotContain('X', scan.Free());
        Assert.DoesNotContain('D', scan.Free());
        Assert.Equal(new[] { LetterSource.SystemDrive, LetterSource.ThisProcess }, scan.Taken['C'].Select(u => u.Source));
        Assert.Equal(new[] { LetterSource.UserSessionReceived, LetterSource.MountedDevices }, scan.Taken['D'].Select(u => u.Source));
        Assert.Equal('Z', scan.DefaultLetter());
    }

    [Fact]
    public void DefaultIsThePreviousLetterThenXThenTheLastFree()
    {
        var scan = new LetterScan();
        scan.Add('C', LetterSource.SystemDrive);
        Assert.Equal('X', scan.DefaultLetter());
        Assert.Equal('M', scan.DefaultLetter('m'));

        scan.Add('X', LetterSource.UserNetwork);
        Assert.Equal('Z', scan.DefaultLetter());

        // CPFS's own previous letter counts as free for CPFS, but never the system drive.
        scan.Add('M', LetterSource.DosDevices);
        Assert.Equal('M', scan.DefaultLetter('M'));
        Assert.DoesNotContain('C', scan.Free('C'));
    }

    [Fact]
    public void NoFreeLetterGivesNoDefault()
    {
        var scan = new LetterScan();
        scan.AddAll("CDEFGHIJKLMNOPQRSTUVWXYZ", LetterSource.ThisProcess);

        Assert.Empty(scan.Free());
        Assert.Null(scan.DefaultLetter());
    }

    [Fact]
    public void AAndBAreNeverOffered()
    {
        var free = new LetterScan().Free();

        Assert.DoesNotContain('A', free);
        Assert.DoesNotContain('B', free);
        Assert.Equal('C', free[0]);
    }
}

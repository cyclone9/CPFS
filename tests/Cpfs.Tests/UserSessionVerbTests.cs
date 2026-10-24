using System;
using Cpfs.Core.Apply;
using Cpfs.Core.Config;
using Cpfs.Core.Elevation;
using Xunit;

namespace Cpfs.Tests;

public class UserSessionVerbTests
{
    private static ConfigDocument Config(string? letter, string? drivePath = null)
    {
        var book = ConfigBook.ForSetup(ConfigReader.Parse(TestFiles.ConfigBytes("v0.2.3-anonymised.json")));
        book.SetField("DriveLetter", letter);
        if (drivePath is not null)
        {
            book.TrackCreated("DrivePath", drivePath);
        }

        return ConfigReader.Parse(book.Serialize("0.6.0-test", new DateTime(2026, 10, 24, 12, 0, 0)));
    }

    [Theory]
    [InlineData("map-drive", "x:", @"D:\Shared", UserSessionAction.MapDrive, 'X')]
    [InlineData("unmap-drive", "X:", @"D:\Server\Shared Folder", UserSessionAction.UnmapDrive, 'X')]
    public void ParsesExactlyTheVerbsShape(string verb, string device, string path, UserSessionAction action, char letter)
    {
        var request = UserSessionVerb.Parse(new[] { "--user-session", verb, device, path });

        Assert.NotNull(request);
        Assert.Equal(action, request!.Action);
        Assert.Equal(letter, request.Letter);
        Assert.Equal(path, request.Path);
    }

    [Theory]
    [InlineData("--user-session", "map-drive", "X:")]
    [InlineData("--user-session", "map-drive", "X:", @"D:\Shared", "extra")]
    [InlineData("--user-session", "delete", "X:", @"D:\Shared")]
    [InlineData("--user-session", "map-drive", "XY:", @"D:\Shared")]
    [InlineData("--user-session", "map-drive", "X", @"D:\Shared")]
    [InlineData("--user-session", "map-drive", "1:", @"D:\Shared")]
    [InlineData("--user-session", "map-drive", "A:", @"D:\Shared")]
    [InlineData("--user-session", "map-drive", "X:", @"\\server\share")]
    [InlineData("--user-session", "map-drive", "X:", @"D:\")]
    [InlineData("--user-session", "map-drive", "X:", @"D:\Shared\")]
    [InlineData("--user-session", "map-drive", "X:", @"Shared")]
    [InlineData("--user-session", "map-drive", "X:", "D:\\Sha\"red")]
    [InlineData("--user-session", "map-drive", "X:", "D:\\Sha\nred")]
    [InlineData("--user-session", "map-drive", "X:", @"D:\a:b")]
    [InlineData("--elevated", "map-drive", "X:", @"D:\Shared")]
    public void RefusesAnythingElse(params string[] args)
    {
        Assert.Null(UserSessionVerb.Parse(args));
    }

    [Fact]
    public void OnlyTheRecordedLetterAndFolderAreAccepted()
    {
        var map = UserSessionVerb.Parse(new[] { "--user-session", "map-drive", "X:", @"d:\shared" })!;
        var moved = UserSessionVerb.Parse(new[] { "--user-session", "unmap-drive", "X:", @"E:\Old" })!;

        Assert.Equal(UserSessionResult.Done, UserSessionVerb.Check(map, Config("X")));
        Assert.Equal(UserSessionResult.NotRecorded, UserSessionVerb.Check(map, Config("Y")));
        Assert.Equal(UserSessionResult.NotRecorded, UserSessionVerb.Check(map, Config(null)));
        Assert.Equal(UserSessionResult.NoConfig, UserSessionVerb.Check(map, null));

        // The folder an earlier drive points to, recorded before the share moved.
        Assert.Equal(UserSessionResult.NotRecorded, UserSessionVerb.Check(moved, Config("X")));
        Assert.Equal(UserSessionResult.Done, UserSessionVerb.Check(moved, Config("X", @"E:\Old")));
    }

    [Fact]
    public void TheTaskArgumentsParseBackToTheSameRequest()
    {
        var text = UserSessionVerb.Arguments(UserSessionAction.MapDrive, 'x', @"D:\Server\Shared Folder");

        Assert.Equal("--user-session map-drive X: \"D:\\Server\\Shared Folder\"", text);
        Assert.Throws<ArgumentException>(() => UserSessionVerb.Arguments(UserSessionAction.UnmapDrive, 'X', @"D:\Shared\"));
    }

    [Theory]
    [InlineData(UserSessionResult.Done, UserSessionAction.MapDrive, true)]
    [InlineData(UserSessionResult.AlreadyMapped, UserSessionAction.MapDrive, true)]
    [InlineData(UserSessionResult.AlreadyMapped, UserSessionAction.UnmapDrive, false)]
    [InlineData(UserSessionResult.NothingToRemove, UserSessionAction.UnmapDrive, true)]
    [InlineData(UserSessionResult.BootMapping, UserSessionAction.UnmapDrive, true)]
    [InlineData(UserSessionResult.PointsElsewhere, UserSessionAction.MapDrive, false)]
    [InlineData(UserSessionResult.NotRecorded, UserSessionAction.UnmapDrive, false)]
    public void SuccessDependsOnTheAction(UserSessionResult result, UserSessionAction action, bool success)
    {
        Assert.Equal(success, UserSessionVerb.IsSuccess((int)result, action));
    }
}

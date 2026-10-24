using System;
using System.Globalization;
using Cpfs.Core.Text;
using Xunit;

namespace Cpfs.Tests;

public class FormatsTests
{
    [Fact]
    public void FormattingFollowsTheUiCultureNotTheRegionalFormat()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // The owner's setup: English app, Persian regional format.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fa-IR");
            var en = CultureInfo.GetCultureInfo("en");
            var when = new DateTime(2026, 10, 23, 18, 37, 16);

            Assert.Equal("9,312,768", Formats.Count(9312768, en));
            Assert.Equal(when.ToString("g", en), Formats.DateTime(when, en));
            Assert.Contains("2026", Formats.DateTime(when, en), StringComparison.Ordinal);
            Assert.Equal("2026-10-23 18:37:16", Formats.Stamp(when));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}

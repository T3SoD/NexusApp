using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Pins the shared sanitizer that the .nexusgrid / .nexuslib log sinks route untrusted fields through
// before they reach nexus.log, plus the general Clean form that GridShareService still uses for its
// stored fields. The wired log sinks (inline WPF code-behind) are enumerated in the change report.
public class TextSanitizerTests
{
    [Fact]
    public void ForLog_ScrubsControlAndBidiChars()
    {
        var bidi = ((char)0x202E).ToString();   // right-to-left override (Format category)
        var cleaned = TextSanitizer.ForLog("pilot\r\n[SCAN] forged" + bidi + "evil");

        Assert.False(cleaned.Contains('\r'));
        Assert.False(cleaned.Contains('\n'));
        Assert.False(cleaned.Contains(bidi, StringComparison.Ordinal));
        Assert.Contains("pilot", cleaned);
    }

    [Fact]
    public void ForLog_IsSingleLine_DropsEvenBareNewlines()
    {
        var cleaned = TextSanitizer.ForLog("a\nb");   // ForLog never allows newlines (single log line)
        Assert.False(cleaned.Contains('\n'));
        Assert.Equal("ab", cleaned);
    }

    [Fact]
    public void ForLog_TruncatesToCap()
    {
        var cleaned = TextSanitizer.ForLog(new string('z', 5000));
        Assert.True(cleaned.Length <= 512);
    }

    [Fact]
    public void ForLog_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", TextSanitizer.ForLog(null));
        Assert.Equal("", TextSanitizer.ForLog(""));
    }

    [Fact]
    public void Clean_AllowNewlines_KeepsLineBreaksButDropsControl()
    {
        // The shared 3-arg form GridShareService uses for stored Notes/FlagNote: newlines kept, other
        // control/format chars still stripped.
        var cleaned = TextSanitizer.Clean("line1\r\nline2\tend", 4000, allowNewlines: true);
        Assert.Contains("\n", cleaned);
        Assert.False(cleaned.Contains('\r'));
        Assert.False(cleaned.Contains('\t'));
        Assert.Contains("line1", cleaned);
        Assert.Contains("line2", cleaned);
    }
}

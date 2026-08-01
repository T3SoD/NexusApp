using System;
using System.Linq;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class TourStepTests
{
    // 16 as of 2026-08-01: the Starmap and Trade steps were added after the app review found the
    // tour introduced only nine of the eleven default dock tiles. Every_anchored_target_is_used_
    // exactly_once below is the real guard - it fails automatically when an enum target has no
    // step - so this count exists to catch an accidental duplicate or deletion, not to gate growth.
    [Fact]
    public void Tour_has_exactly_16_steps()
        => Assert.Equal(16, TourController.Steps.Length);

    [Fact]
    public void First_and_last_steps_are_centered()
    {
        Assert.Equal(TutorialTarget.None, TourController.Steps[0].Target);
        Assert.Equal(TutorialTarget.None, TourController.Steps[^1].Target);
    }

    [Fact]
    public void Every_anchored_target_is_used_exactly_once()
    {
        var anchored = TourController.Steps.Select(s => s.Target).Where(t => t != TutorialTarget.None).ToList();
        var expected = Enum.GetValues<TutorialTarget>().Where(t => t != TutorialTarget.None);
        Assert.Equal(anchored.Count, anchored.Distinct().Count());
        Assert.True(expected.All(anchored.Contains), "an enum target has no step");
    }

    [Fact]
    public void Copy_has_no_em_dashes_and_no_emoji()
    {
        foreach (var s in TourController.Steps)
        {
            var text = s.Title + s.Caption;
            Assert.DoesNotContain('—', text);          // em-dash
            Assert.DoesNotContain('–', text);          // en-dash
            Assert.DoesNotContain(text, c => char.IsSurrogate(c));  // emoji live above the BMP
        }
    }

    [Fact]
    public void Captions_fit_the_bubble()
    {
        foreach (var s in TourController.Steps)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.InRange(s.Caption.Length, 40, 300);
        }
    }
}

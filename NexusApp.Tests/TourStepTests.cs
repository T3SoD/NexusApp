using System;
using System.Linq;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class TourStepTests
{
    [Fact]
    public void Tour_has_exactly_14_steps()
        => Assert.Equal(14, TourController.Steps.Length);

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

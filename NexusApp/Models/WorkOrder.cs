namespace NexusApp.Models;

public class WorkOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Label { get; set; } = "";
    public string Resources { get; set; } = "";
    public string Location { get; set; } = "";
    public string Refinery { get; set; } = "";
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Mining;
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? TimerStart { get; set; }
    public DateTime? TimerEnd { get; set; }

    public string StatusLabel => Status switch
    {
        WorkOrderStatus.Mining         => "Mining",
        WorkOrderStatus.Refining       => "Refining",
        WorkOrderStatus.ReadyToCollect => "Ready to Collect",
        WorkOrderStatus.Complete       => "Complete",
        _                              => "",
    };

    public string TimerRemainingShort
    {
        get
        {
            if (!TimerEnd.HasValue) return "";
            var rem = TimerEnd.Value - DateTime.UtcNow;
            if (rem <= TimeSpan.Zero) return "";
            var h = (int)rem.TotalHours;
            var m = rem.Minutes;
            return h > 0 ? $"{h}h {m:D2}m" : $"{m}m";
        }
    }

    public string CreatedAtDisplay => CreatedAt.ToLocalTime().ToString("MMM d, h:mm tt");

    public bool HasActiveTimer => TimerEnd.HasValue && TimerEnd.Value > DateTime.UtcNow;

    // True when this order's refine timer has run out but the model still sits in a pre-ready state, so a
    // gallery/overlay ticker should flip it to ReadyToCollect. Pure and time-injectable so the elapsed and
    // idempotence rules can be asserted without WPF. Already-ready or complete orders return false (no
    // double flip); a timerless order never auto-flips.
    public bool ShouldAutoFlipToReady(DateTime utcNow)
        => TimerEnd.HasValue
           && TimerEnd.Value <= utcNow
           && (Status == WorkOrderStatus.Refining || Status == WorkOrderStatus.Mining);

    public string SubtitleText       => HasActiveTimer ? TimerRemainingShort : CreatedAtDisplay;
    public string SubtitleForeground => HasActiveTimer ? "#E67E22" : "#8B949E";

    public string StatusColorHex => Status switch
    {
        WorkOrderStatus.Mining         => "#3B82F6",
        WorkOrderStatus.Refining       => "#E67E22",
        WorkOrderStatus.ReadyToCollect => "#2ECC71",
        WorkOrderStatus.Complete       => "#7F8C8D",
        _                              => "#7F8C8D",
    };

    public double TimerFraction
    {
        get
        {
            if (!TimerEnd.HasValue || !TimerStart.HasValue) return 0;
            var total = (TimerEnd.Value - TimerStart.Value).TotalSeconds;
            if (total <= 0) return 0;
            return Math.Clamp((DateTime.UtcNow - TimerStart.Value).TotalSeconds / total, 0, 1);
        }
    }
}

public enum WorkOrderStatus
{
    Mining,
    Refining,
    ReadyToCollect,
    Complete
}

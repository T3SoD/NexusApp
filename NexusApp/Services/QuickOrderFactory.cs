namespace NexusApp.Services;

using NexusApp.Models;

/// <summary>
/// Builds the minimal overlay quick-add work order: a Refining order with a running timer.
/// Everything richer (label, location, notes, status pills) stays in the main-window
/// WorkOrderEditorPanel. Callers pass DateTime.UtcNow because the model's timer math
/// (WorkOrder.TimerRemainingShort / HasActiveTimer / TimerFraction) is UtcNow-based.
/// </summary>
public static class QuickOrderFactory
{
    public static WorkOrder Create(string resource, string refinery, int minutes, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(resource)) throw new ArgumentException("resource required");
        if (minutes <= 0) throw new ArgumentException("timer must be positive");

        var name = resource.Trim();
        return new WorkOrder
        {
            Label     = name,
            Resources = name,
            Refinery  = refinery?.Trim() ?? "",
            Status    = WorkOrderStatus.Refining,
            TimerStart = now,
            TimerEnd   = now.AddMinutes(minutes),
        };
    }
}

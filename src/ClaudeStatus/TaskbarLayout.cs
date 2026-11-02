using System.Windows.Automation;

namespace ClaudeStatus;

/// <summary>
/// Finds where the taskbar's own buttons are (Start, search, pinned/running apps) via UI Automation,
/// so the widget is only shown in the free gap between the last button and the tray. Windows 11
/// centres the app buttons and reserves no room for third-party content.
/// </summary>
static class TaskbarLayout
{
    /// <summary>Horizontal pixel ranges occupied by taskbar buttons left of <paramref name="limit"/>.</summary>
    public static List<(int Left, int Right)> OccupiedRanges(Native.RECT taskbar, int limit)
    {
        var ranges = new List<(int, int)>();
        try
        {
            var shell = Native.ShellTrayWindow();
            if (shell == IntPtr.Zero) return ranges;

            var root = AutomationElement.FromHandle(shell);
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, condition))
            {
                var r = e.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Width > taskbar.Width / 2) continue;
                if (r.Bottom < taskbar.Top || r.Top > taskbar.Bottom) continue;
                if (r.Left >= limit) continue; // tray and clock
                ranges.Add(((int)r.Left, (int)Math.Min(r.Right, limit)));
            }
        }
        catch
        {
            // UI Automation can fail while Explorer restarts; treat as "nothing known".
        }
        return ranges;
    }
}

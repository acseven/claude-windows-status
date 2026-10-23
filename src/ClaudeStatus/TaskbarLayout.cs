using System.Windows.Automation;

namespace ClaudeStatus;

/// <summary>
/// Finds where the taskbar's own buttons are (Start, search, pinned/running apps) via UI Automation,
/// so the widget can sit in free space. Windows 11 centres the app buttons and reserves no room for
/// third-party content, so on narrow screens "next to the tray" can land on top of them.
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

    /// <summary>
    /// Right edge for a widget of width <paramref name="width"/>: the preferred spot if it is free,
    /// otherwise the nearest free gap to its left; the preferred spot if nothing fits.
    /// </summary>
    public static int FitRight(int preferredRight, int width, int minLeft, IReadOnlyList<(int Left, int Right)> occupied, int margin)
    {
        var right = preferredRight;
        for (var guard = 0; guard < 64; guard++)
        {
            var left = right - width;
            if (left < minLeft) return preferredRight;
            var hit = occupied.Where(o => o.Left < right + margin && o.Right > left - margin).ToList();
            if (hit.Count == 0) return right;
            right = hit.Min(o => o.Left) - margin;
        }
        return preferredRight;
    }
}

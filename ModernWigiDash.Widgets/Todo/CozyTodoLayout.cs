namespace ModernWigiDash.Widgets.Todo;


/// <summary>
/// Pure layout rules and hit geometry calculations for the Cozy To-Do Companion widget.
/// Adapts seamlessly across 5x4 full editorial, 4x2 split banner, and 2x3 compact poster presets.
/// </summary>
internal static class CozyTodoLayout
{
    /// <summary>Baseline design width (1016px framebuffer width).</summary>
    public const float DesignWidth = 1016f;

    /// <summary>Baseline design height (592px framebuffer height).</summary>
    public const float DesignHeight = 592f;

    /// <summary>
    /// Computes the complete layout geometry for rendering and hit-testing.
    /// </summary>
    public static CozyTodoGeometry Compute(
        SKRect bounds,
        bool isMinimalist,
        IReadOnlyList<TodoItem> tasks,
        float scrollY)
    {
        float scale = Math.Min(bounds.Width / DesignWidth, bounds.Height / DesignHeight);
        if (scale <= 0.01f) scale = 1.0f;

        float pad = Math.Max(8f, 16f * scale);
        float gap = Math.Max(6f, 14f * scale);

        if (isMinimalist)
        {
            return ComputeMinimalist(bounds, pad, gap, scale, tasks, scrollY);
        }

        bool isWide = bounds.Width / Math.Max(1f, bounds.Height) >= 1.25f;
        if (isWide)
        {
            return ComputeWideCompanion(bounds, pad, gap, scale, tasks, scrollY);
        }
        else
        {
            return ComputeTallCompanion(bounds, pad, gap, scale, tasks, scrollY);
        }
    }

    private static CozyTodoGeometry ComputeMinimalist(
        SKRect bounds,
        float pad,
        float gap,
        float scale,
        IReadOnlyList<TodoItem> tasks,
        float scrollY)
    {
        float headerHeight = Math.Max(40f, 64f * scale);
        SKRect headerRect = new(bounds.Left + pad, bounds.Top + pad, bounds.Right - pad, bounds.Top + pad + headerHeight);

        float pomodoroWidth = Math.Max(120f, 180f * scale);
        SKRect pomodoroCard = new(headerRect.Right - pomodoroWidth, headerRect.Top, headerRect.Right, headerRect.Bottom);
        SKRect pomodoroBtn = new(pomodoroCard.Right - Math.Max(36f, 44f * scale), pomodoroCard.Top + 4f * scale, pomodoroCard.Right - 4f * scale, pomodoroCard.Bottom - 4f * scale);

        float panelTop = headerRect.Bottom + gap;
        SKRect tasksPanel = new(bounds.Left + pad, panelTop, bounds.Right - pad, bounds.Bottom - pad);

        var (rows, maxScroll) = ComputeTaskRows(tasksPanel, tasks, scrollY, scale);

        return new CozyTodoGeometry(
            Bounds: bounds,
            IsMinimalist: true,
            CompanionCardRect: SKRect.Empty,
            PetSpriteRect: SKRect.Empty,
            FeedButtonRect: SKRect.Empty,
            PomodoroCardRect: pomodoroCard,
            PomodoroButtonRect: pomodoroBtn,
            TasksPanelRect: tasksPanel,
            TaskRows: rows,
            ScrollOffset: scrollY,
            MaxScrollOffset: maxScroll);
    }

    private static CozyTodoGeometry ComputeWideCompanion(
        SKRect bounds,
        float pad,
        float gap,
        float scale,
        IReadOnlyList<TodoItem> tasks,
        float scrollY)
    {
        // Lock left pillar width tightly to companion avatar boundary (~276px),
        // moving the entire pillar to the left to maximize space for daily tasks (~700px).
        float leftWidth = Math.Clamp(276f * scale, 180f * scale, bounds.Width * 0.38f);
        SKRect leftCard = new(bounds.Left + pad, bounds.Top + pad, bounds.Left + pad + leftWidth, bounds.Bottom - pad);
        SKRect tasksPanel = new(leftCard.Right + gap, bounds.Top + pad, bounds.Right - pad, bounds.Bottom - pad);

        float columnPad = 12f * scale;
        float innerLeft = leftCard.Left + columnPad;
        float innerRight = leftCard.Right - columnPad;

        // Avatar boundary box: Everything under it aligns strictly within [innerLeft, innerRight]
        float petHeight = Math.Min(232f * scale, leftCard.Height * 0.42f);
        float petTop = leftCard.Top + columnPad;
        SKRect petRect = new(innerLeft, petTop, innerRight, petTop + petHeight);

        // Feed Pet button: sits directly under pet stats, spanning strictly within the avatar boundary
        float feedBtnHeight = Math.Max(36f, 44f * scale);
        float feedBtnTop = petRect.Bottom + 80f * scale;
        SKRect feedBtn = new(innerLeft, feedBtnTop, innerRight, feedBtnTop + feedBtnHeight);

        // Pomodoro Focus Card: docked at bottom of left pillar, spanning strictly within the avatar boundary
        float pomoHeight = Math.Max(58f, 70f * scale);
        float pomoTop = leftCard.Bottom - pomoHeight - columnPad;
        SKRect pomoCard = new(innerLeft, pomoTop, innerRight, pomoTop + pomoHeight);
        float pomoBtnSide = pomoHeight - 20f * scale;
        SKRect pomoBtn = new(pomoCard.Right - pomoBtnSide - 10f * scale, pomoCard.Top + 10f * scale, pomoCard.Right - 10f * scale, pomoCard.Bottom - 10f * scale);

        var (rows, maxScroll) = ComputeTaskRows(tasksPanel, tasks, scrollY, scale);

        return new CozyTodoGeometry(
            Bounds: bounds,
            IsMinimalist: false,
            CompanionCardRect: leftCard,
            PetSpriteRect: petRect,
            FeedButtonRect: feedBtn,
            PomodoroCardRect: pomoCard,
            PomodoroButtonRect: pomoBtn,
            TasksPanelRect: tasksPanel,
            TaskRows: rows,
            ScrollOffset: scrollY,
            MaxScrollOffset: maxScroll);
    }

    private static CozyTodoGeometry ComputeTallCompanion(
        SKRect bounds,
        float pad,
        float gap,
        float scale,
        IReadOnlyList<TodoItem> tasks,
        float scrollY)
    {
        float topHeight = bounds.Height * 0.42f;
        SKRect topCard = new(bounds.Left + pad, bounds.Top + pad, bounds.Right - pad, bounds.Top + pad + topHeight);
        SKRect tasksPanel = new(bounds.Left + pad, topCard.Bottom + gap, bounds.Right - pad, bounds.Bottom - pad);

        float petSide = Math.Min(topCard.Height - 20f * scale, topCard.Width * 0.38f);
        SKRect petRect = new(topCard.Left + 12f * scale, topCard.Top + 10f * scale, topCard.Left + 12f * scale + petSide, topCard.Top + 10f * scale + petSide);

        float feedBtnWidth = Math.Max(100f, 130f * scale);
        float feedBtnHeight = Math.Max(28f, 36f * scale);
        SKRect feedBtn = new(topCard.Right - feedBtnWidth - 12f * scale, topCard.Top + 12f * scale, topCard.Right - 12f * scale, topCard.Top + 12f * scale + feedBtnHeight);

        float pomoHeight = Math.Max(36f, 48f * scale);
        SKRect pomoCard = new(petRect.Right + 12f * scale, topCard.Bottom - pomoHeight - 10f * scale, topCard.Right - 12f * scale, topCard.Bottom - 10f * scale);
        float pomoBtnSide = pomoHeight - 12f * scale;
        SKRect pomoBtn = new(pomoCard.Right - pomoBtnSide - 6f * scale, pomoCard.Top + 6f * scale, pomoCard.Right - 6f * scale, pomoCard.Bottom - 6f * scale);

        var (rows, maxScroll) = ComputeTaskRows(tasksPanel, tasks, scrollY, scale);

        return new CozyTodoGeometry(
            Bounds: bounds,
            IsMinimalist: false,
            CompanionCardRect: topCard,
            PetSpriteRect: petRect,
            FeedButtonRect: feedBtn,
            PomodoroCardRect: pomoCard,
            PomodoroButtonRect: pomoBtn,
            TasksPanelRect: tasksPanel,
            TaskRows: rows,
            ScrollOffset: scrollY,
            MaxScrollOffset: maxScroll);
    }

    private static (List<TaskRowGeometry> Rows, float MaxScroll) ComputeTaskRows(
        SKRect panel,
        IReadOnlyList<TodoItem> tasks,
        float scrollY,
        float scale)
    {
        // Increased row height (66px) for larger typography and multi-line word wrapping
        float rowHeight = Math.Max(46f, 66f * scale);
        float rowGap = Math.Max(5f, 9f * scale);
        float checkSide = Math.Max(22f, 28f * scale);
        float rowPad = Math.Max(10f, 14f * scale);

        float headerHeight = Math.Max(36f, 48f * scale);
        float currentY = panel.Top + headerHeight - scrollY;

        var list = new List<TaskRowGeometry>();
        float totalHeight = tasks.Count * (rowHeight + rowGap);
        float viewableHeight = Math.Max(1f, panel.Height - headerHeight);
        float maxScroll = Math.Max(0f, totalHeight - viewableHeight);

        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            SKRect rowRect = new(panel.Left + 10f * scale, currentY, panel.Right - 10f * scale, currentY + rowHeight);
            float checkTop = rowRect.Top + (rowRect.Height - checkSide) * 0.5f;
            SKRect checkRect = new(rowRect.Left + rowPad, checkTop, rowRect.Left + rowPad + checkSide, checkTop + checkSide);

            list.Add(new TaskRowGeometry(
                TaskId: task.Id,
                RowRect: rowRect,
                CheckboxRect: checkRect,
                IsDone: task.IsDone,
                Title: task.Title,
                Category: task.Category));

            currentY += rowHeight + rowGap;
        }

        return (list, maxScroll);
    }

    /// <summary>
    /// Pure hit-testing logic for touch points.
    /// </summary>
    public static CozyTodoHitAction HitTest(CozyTodoGeometry geom, SKPoint point)
    {
        if (geom.FeedButtonRect.Contains(point))
        {
            return CozyTodoHitAction.Feed;
        }

        if (geom.PomodoroButtonRect.Contains(point))
        {
            return CozyTodoHitAction.Focus;
        }

        if (geom.TasksPanelRect.Contains(point))
        {
            for (int i = 0; i < geom.TaskRows.Count; i++)
            {
                var row = geom.TaskRows[i];
                if (row.RowRect.Contains(point))
                {
                    return CozyTodoHitAction.Toggle(row.TaskId);
                }
            }
        }

        return CozyTodoHitAction.None;
    }
}

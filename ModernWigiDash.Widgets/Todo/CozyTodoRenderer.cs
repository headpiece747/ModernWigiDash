namespace ModernWigiDash.Widgets.Todo;

using ModernWigiDash.Core.Rendering;

/// <summary>
/// Allocation-free 30 FPS SkiaSharp renderer for the Cozy Companion task widget.
/// Manages hoisted SKPaints, procedural companion sprite animation, Pomodoro timer arcs,
/// and Clean Minimalist presentation.
/// </summary>
internal sealed class CozyTodoRenderer : IDisposable
{
    private static readonly SKBitmap? DragonHappyBitmap = LoadDragonBitmap("dragon_companion.png");
    private static readonly SKBitmap? DragonFocusBitmap = LoadDragonBitmap("dragon_focus.png");
    private static readonly SKBitmap? DragonOverjoyedBitmap = LoadDragonBitmap("dragon_overjoyed.png");
    private static readonly SKBitmap? DragonOverjoyedDownBitmap = LoadDragonBitmap("dragon_overjoyed_down.png");
    private static readonly SKBitmap? DragonSleepyBitmap = LoadDragonBitmap("dragon_sleepy.png");

    private static SKBitmap? LoadDragonBitmap(string resourceName)
    {
        try
        {
            using var stream = typeof(CozyTodoRenderer).Assembly.GetManifestResourceStream($"ModernWigiDash.Widgets.Resources.{resourceName}");
            if (stream is not null)
            {
                return SKBitmap.Decode(stream);
            }
        }
        catch
        {
            // Graceful fallback to procedural sprite
        }
        return null;
    }

    /// <summary>
    /// Precomputes the flame glow's radial gradient (the 3-stop orange fade,
    /// its alpha baked in) into a 128px bitmap once per mood, so the render
    /// tick draws it scaled instead of creating and disposing a native shader
    /// every frame.
    /// </summary>
    private static SKBitmap BuildGlowBitmap(byte alpha)
    {
        var bitmap = new SKBitmap(128, 128, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(64, 64),
                64f,
                [new SKColor(0xFF, 0x8C, 0x00, alpha), new SKColor(0xFF, 0x3D, 0x00, (byte)(alpha / 2)), SKColors.Transparent],
                [0f, 0.45f, 1f],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawCircle(64, 64, 64f, paint);
        return bitmap;
    }

    private SKRect _cachedPomoBtnRect;
    private SKPath? _playTriangle;
    private readonly SKPaint _bgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _panelPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _cardPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _borderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _accentPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _accentStrokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _highlightPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _greenPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _greenStrokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _azurePaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _azureStrokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _redPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _textPrimaryPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _textSecondaryPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strikePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _petBodyPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _petAccentPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _petBlushPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _petEyePaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _bitmapPaint = new() { IsAntialias = true };
    private readonly SKPaint _glowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    /// <summary>The one sampling options for scaled bitmap draws (linear filtering).</summary>
    private static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // The flame glow's radial gradient is precomputed once per mood (the alpha
    // is the only mood-varying stop) into a small bitmap, then drawn scaled:
    // the per-tick shader create/dispose is gone, and a steady-state tick
    // allocates nothing on the glow path.
    private static readonly SKBitmap GlowAttentiveBitmap = BuildGlowBitmap(150);
    private static readonly SKBitmap GlowOverjoyedBitmap = BuildGlowBitmap(120);
    private static readonly SKBitmap GlowCalmBitmap = BuildGlowBitmap(95);

    // The task-title wrap memo: the house's bounded wrap cache (one wrap rule
    // in the repo, the WrapCache model), so a steady-state tick reuses the
    // wrapped lines instead of re-splitting and re-measuring every row.
    private readonly WrapCache _wrappedTitles = new();

    // Single-slot string memos (the StalenessLine pattern, one owner per
    // string): each label is reformatted at most once per change of its
    // inputs, so the steady-state tick allocates no strings.
    private int _statsDone = -1;
    private int _statsTotal = -1;
    private string _statsText = "";
    private int _badgeDone = -1;
    private int _badgeTotal = -1;
    private string _badgeText = "";
    private string _nameLevelName = "";
    private int _nameLevelLevel = -1;
    private string _nameLevelText = "";
    private int _xpTotalXp = -1;
    private string _xpLabelText = "";
    private int _btnTreats = -1;
    private string _btnText = "";
    private int _timeMinutes = -1;
    private int _timeSeconds = -1;
    private string _timeText = "";

    /// <summary>
    /// Renders the complete widget canvas.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        CozyTodoGeometry layout,
        IReadOnlyList<TodoItem> tasks,
        CozyCompanionState companion,
        FocusState focus,
        SKColor customAccent,
        long animationTickMs,
        DateTime nowUtc)
    {
        // 1. Base dark background
        _bgPaint.Color = CozyTodoPalette.BgDark;
        canvas.DrawRect(bounds, _bgPaint);

        float scale = Math.Min(bounds.Width / CozyTodoLayout.DesignWidth, bounds.Height / CozyTodoLayout.DesignHeight);
        if (scale <= 0.01f) scale = 1.0f;

        if (layout.IsMinimalist)
        {
            RenderMinimalist(canvas, layout, tasks, focus, customAccent, scale);
        }
        else
        {
            RenderCompanion(canvas, layout, companion, focus, customAccent, scale, animationTickMs, nowUtc);
        }
    }

    private void RenderMinimalist(
        SKCanvas canvas,
        CozyTodoGeometry layout,
        IReadOnlyList<TodoItem> tasks,
        FocusState focus,
        SKColor customAccent,
        float scale)
    {
        // Header summary
        int completedCount = 0;
        for (int i = 0; i < tasks.Count; i++)
        {
            if (tasks[i].IsDone) completedCount++;
        }
        float progress = tasks.Count > 0 ? (float)completedCount / tasks.Count : 0f;

        // Circular progress ring
        float ringRadius = Math.Max(12f, 18f * scale);
        float ringCx = layout.Bounds.Left + 16f * scale + ringRadius;
        float ringCy = layout.Bounds.Top + 32f * scale;
        SKRect ringRect = new(ringCx - ringRadius, ringCy - ringRadius, ringCx + ringRadius, ringCy + ringRadius);

        _accentStrokePaint.Color = CozyTodoPalette.Border;
        _accentStrokePaint.StrokeWidth = 3f * scale;
        canvas.DrawOval(ringRect, _accentStrokePaint);

        if (progress > 0.01f)
        {
            _accentStrokePaint.Color = customAccent;
            canvas.DrawArc(ringRect, -90f, progress * 360f, false, _accentStrokePaint);
        }

        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(14f, 20f * scale));
        var subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(11f, 14f * scale));

        float textStartX = ringCx + ringRadius + 14f * scale;
        _textPrimaryPaint.Color = CozyTodoPalette.TextPrimary;
        canvas.DrawTextWithFallback("Today's Tasks", textStartX, ringCy - 2f * scale, titleFont, _textPrimaryPaint);

        if (_statsDone != completedCount || _statsTotal != tasks.Count)
        {
            _statsDone = completedCount;
            _statsTotal = tasks.Count;
            _statsText = $"{completedCount} of {tasks.Count} completed ({progress * 100:0}%)";
        }
        string statsText = _statsText;
        _textSecondaryPaint.Color = CozyTodoPalette.TextSecondary;
        canvas.DrawTextWithFallback(statsText, textStartX, ringCy + 16f * scale, subFont, _textSecondaryPaint);

        // Pomodoro Card in header
        if (!layout.PomodoroCardRect.IsEmpty)
        {
            RenderPomodoroCard(canvas, layout.PomodoroCardRect, layout.PomodoroButtonRect, focus, scale);
        }

        // Tasks panel
        RenderTasksPanel(canvas, layout, scale);
    }

    private void RenderCompanion(
        SKCanvas canvas,
        CozyTodoGeometry layout,
        CozyCompanionState companion,
        FocusState focus,
        SKColor customAccent,
        float scale,
        long animationTickMs,
        DateTime nowUtc)
    {
        // 1. Companion Sanctuary Card
        SKRect cardRect = layout.CompanionCardRect;
        _panelPaint.Color = CozyTodoPalette.BgPanel;
        canvas.DrawRoundRect(cardRect, 14f * scale, 14f * scale, _panelPaint);

        _borderPaint.Color = CozyTodoPalette.Border;
        _borderPaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(cardRect, 14f * scale, 14f * scale, _borderPaint);

        // Determine mood
        CozyMood mood = CozyCompanionEngine.ResolveMood(
            focus.IsRunning,
            companion.LastTaskCompletedUtc,
            companion.LastFedTimeUtc,
            nowUtc);

        // Pet Sanctuary Box
        SKRect petRect = layout.PetSpriteRect;
        float innerLeft = petRect.IsEmpty ? cardRect.Left + 12f * scale : petRect.Left;
        float innerRight = petRect.IsEmpty ? cardRect.Right - 12f * scale : petRect.Right;
        float innerWidth = innerRight - innerLeft;

        if (!petRect.IsEmpty)
        {
            _bgPaint.Color = CozyTodoPalette.BgDark;
            canvas.DrawRoundRect(petRect, 12f * scale, 12f * scale, _bgPaint);
            _borderPaint.Color = CozyTodoPalette.Border;
            canvas.DrawRoundRect(petRect, 12f * scale, 12f * scale, _borderPaint);

            // Draw animated dragon (or procedural fallback)
            DrawPetSprite(canvas, petRect, mood, customAccent, scale, animationTickMs);
        }

        // Pet Stats: Name & Level (Larger Desk Typography: 19px bold)
        var nameFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(14f, 19f * scale));
        var statFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(11f, 13f * scale));

        int level = CozyCompanionEngine.CalculateLevel(companion.TotalXp);
        if (!string.Equals(_nameLevelName, companion.Name, StringComparison.Ordinal) || _nameLevelLevel != level)
        {
            _nameLevelName = companion.Name;
            _nameLevelLevel = level;
            _nameLevelText = $"{companion.Name}  •  Lvl {level}";
        }
        string nameLevel = _nameLevelText;
        _textPrimaryPaint.Color = CozyTodoPalette.TextPrimary;

        float textX = innerLeft;
        float textY = (petRect.IsEmpty ? cardRect.Top + 24f * scale : petRect.Bottom + 22f * scale);
        if (textY < cardRect.Bottom - 80f * scale)
        {
            canvas.DrawTextWithFallback(nameLevel, textX, textY, nameFont, _textPrimaryPaint);

            // Mood pill docked at innerRight
            string moodText = mood switch
            {
                CozyMood.Sleepy => "💤 Sleepy",
                CozyMood.Attentive => "🎯 Focus",
                CozyMood.Happy => "💖 Happy",
                CozyMood.Overjoyed => "✨ Overjoyed",
                _ => "💤 Sleepy"
            };
            float moodPillW = FontHelper.MeasureTextWithFallback(moodText, statFont) + 14f * scale;
            float moodPillH = Math.Max(18f, 22f * scale);
            SKRect moodPillRect = new(innerRight - moodPillW, textY - 15f * scale, innerRight, textY - 15f * scale + moodPillH);
            _azurePaint.Color = customAccent.WithAlpha(35);
            canvas.DrawRoundRect(moodPillRect, moodPillH * 0.5f, moodPillH * 0.5f, _azurePaint);
            _accentPaint.Color = customAccent;
            canvas.DrawTextWithFallback(moodText, moodPillRect.Left + 7f * scale, moodPillRect.MidY + 4f * scale, statFont, _accentPaint);

            // XP bar spanning strictly from innerLeft to innerRight
            float xpFrac = CozyCompanionEngine.LevelProgressFraction(companion.TotalXp);
            float barW = innerWidth;
            float barH = Math.Max(5f, 7f * scale);
            float barY = textY + 9f * scale;
            SKRect xpTrack = new(textX, barY, textX + barW, barY + barH);
            _cardPaint.Color = CozyTodoPalette.BgCard;
            canvas.DrawRoundRect(xpTrack, barH / 2f, barH / 2f, _cardPaint);

            if (xpFrac > 0.01f)
            {
                SKRect xpFill = new(textX, barY, textX + barW * xpFrac, barY + barH);
                _accentPaint.Color = customAccent;
                canvas.DrawRoundRect(xpFill, barH / 2f, barH / 2f, _accentPaint);
            }

            if (_xpTotalXp != companion.TotalXp)
            {
                _xpTotalXp = companion.TotalXp;
                int toNext = CozyCompanionEngine.XpToNextLevel(companion.TotalXp);
                _xpLabelText = $"XP: {companion.TotalXp}  ({toNext} to Lvl {level + 1})";
            }
            string xpLabel = _xpLabelText;
            _textSecondaryPaint.Color = CozyTodoPalette.TextSecondary;
            var xpFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(10f, 12f * scale));
            canvas.DrawTextWithFallback(xpLabel, textX, barY + barH + 13f * scale, xpFont, _textSecondaryPaint);
        }

        // Feed button (Enlarged font: 15px bold, perfectly bounded)
        if (!layout.FeedButtonRect.IsEmpty)
        {
            RenderFeedButton(canvas, layout.FeedButtonRect, companion.SnackTreats, scale);
        }

        // Pomodoro Card (Enlarged digital font: 25px bold, perfectly bounded)
        if (!layout.PomodoroCardRect.IsEmpty)
        {
            RenderPomodoroCard(canvas, layout.PomodoroCardRect, layout.PomodoroButtonRect, focus, scale);
        }

        // 2. Tasks Panel
        RenderTasksPanel(canvas, layout, scale);
    }

    private void DrawPetSprite(
        SKCanvas canvas,
        SKRect rect,
        CozyMood mood,
        SKColor accentColor,
        float scale,
        long animationTickMs)
    {
        SKBitmap? bitmap;
        if (mood == CozyMood.Overjoyed)
        {
            // Rapid cartoon wing flapping alternation (~3.1 Hz)
            bool isFlapDown = ((animationTickMs / 160) % 2) == 1;
            bitmap = (isFlapDown && DragonOverjoyedDownBitmap is not null)
                ? DragonOverjoyedDownBitmap
                : (DragonOverjoyedBitmap ?? DragonHappyBitmap);
        }
        else
        {
            bitmap = mood switch
            {
                CozyMood.Sleepy => DragonSleepyBitmap ?? DragonHappyBitmap,
                CozyMood.Attentive => DragonFocusBitmap ?? DragonHappyBitmap,
                _ => DragonHappyBitmap
            };
        }

        if (bitmap is not null)
        {
            DrawDragonSprite(canvas, rect, mood, bitmap, scale, animationTickMs);
            return;
        }

        // Procedural pet fallback (in case resource is unavailable)
        DrawProceduralPetFallback(canvas, rect, mood, accentColor, scale, animationTickMs);
    }

    private void DrawDragonSprite(
        SKCanvas canvas,
        SKRect rect,
        CozyMood mood,
        SKBitmap bitmap,
        float scale,
        long animationTickMs)
    {
        // 1. Calculate fitted destination rect
        float pad = 6f * scale;
        float availW = rect.Width - pad * 2f;
        float availH = rect.Height - pad * 2f;
        float imgScale = Math.Min(availW / bitmap.Width, availH / bitmap.Height);
        float drawW = bitmap.Width * imgScale;
        float drawH = bitmap.Height * imgScale;
        float drawX = rect.Left + (rect.Width - drawW) * 0.5f;
        float drawY = rect.Top + (rect.Height - drawH) * 0.5f;
        SKRect dstRect = new(drawX, drawY, drawX + drawW, drawY + drawH);

        // 2. Soft floor contact shadow under dragon
        _cardPaint.Color = new SKColor(0, 0, 0, 95);
        float shadowW = drawW * (mood == CozyMood.Sleepy ? 0.72f : 0.60f);
        float shadowH = 10f * scale;
        SKRect shadowRect = new(dstRect.MidX - shadowW * 0.5f, dstRect.Bottom - shadowH * 0.3f, dstRect.MidX + shadowW * 0.5f, dstRect.Bottom + shadowH * 0.7f);
        canvas.DrawOval(shadowRect, _cardPaint);

        // 3. Dynamic Emotion & Wing Flap Physics
        float breathPhase;
        float breathOffset;
        float breathScaleY;
        float breathScaleX;
        float hopOffset = 0f;
        float swayAngle = 0f;
        float wingFlap = 0f;

        switch (mood)
        {
            case CozyMood.Overjoyed:
                // Joyful celebratory feeding animation: rapid wing flap alternation & energetic bouncing hop
                float hopCycle = (animationTickMs % 650) / 650f * MathF.PI;
                hopOffset = -MathF.Abs(MathF.Sin(hopCycle)) * (12f * scale);

                wingFlap = MathF.Sin(animationTickMs * 0.024f);
                breathScaleX = 1f + wingFlap * 0.035f;
                breathScaleY = 1f - wingFlap * 0.022f;
                breathOffset = 0f;
                swayAngle = MathF.Sin(animationTickMs * 0.012f) * 2.5f;
                break;

            case CozyMood.Attentive:
                // Determined focus posture: alert, calm rhythmic breathing, gentle headband ribbon sway
                breathPhase = (animationTickMs % 2000) / 2000f * MathF.Tau;
                breathOffset = MathF.Sin(breathPhase) * (1.8f * scale);
                breathScaleY = 1f + MathF.Sin(breathPhase) * 0.012f;
                breathScaleX = 1f - MathF.Sin(breathPhase) * 0.008f;
                swayAngle = MathF.Sin(animationTickMs * 0.006f) * 1.2f;
                break;

            case CozyMood.Sleepy:
                // Peaceful slumber: slow deep chest rise, curled snug posture
                breathPhase = (animationTickMs % 3400) / 3400f * MathF.Tau;
                breathOffset = MathF.Sin(breathPhase) * (1.5f * scale);
                breathScaleY = 1f + MathF.Sin(breathPhase) * 0.016f;
                breathScaleX = 1f - MathF.Sin(breathPhase) * 0.010f;
                break;

            default:
                // Cheerful steady breathing with visible idle wing rustle (Happy & fallback)
                breathPhase = (animationTickMs % 2200) / 2200f * MathF.Tau;
                breathOffset = MathF.Sin(breathPhase) * (3.6f * scale);
                breathScaleY = 1f + MathF.Sin(breathPhase) * 0.024f;
                breathScaleX = 1f - MathF.Sin(breathPhase) * 0.015f;
                // Visible cheerful wing rustle on left flank wing
                float wingRustle = MathF.Sin(animationTickMs * 0.016f) * 0.030f;
                breathScaleX += wingRustle;
                swayAngle = MathF.Sin(animationTickMs * 0.007f) * 2.2f;
                break;
        }

        // 4. Draw Animated Dragon
        canvas.Save();
        canvas.Translate(dstRect.MidX, dstRect.Bottom);
        if (swayAngle != 0f)
        {
            canvas.RotateDegrees(swayAngle);
        }
        canvas.Scale(breathScaleX, breathScaleY);
        canvas.Translate(-dstRect.MidX, -dstRect.Bottom + breathOffset + hopOffset);
        canvas.DrawBitmap(bitmap, dstRect, LinearSampling, _bitmapPaint);
        canvas.Restore();

        // 5. Wing Flap Motion Arcs (during Overjoyed celebration / feeding)
        if (mood == CozyMood.Overjoyed)
        {
            float flapAlpha = MathF.Max(0f, wingFlap) * 190f;
            if (flapAlpha > 25f)
            {
                _accentStrokePaint.Color = new SKColor(0xFF, 0xD7, 0x00, (byte)flapAlpha);
                _accentStrokePaint.StrokeWidth = 2f * scale;

                // Left wing flutter breeze arc
                float leftX = dstRect.Left + 2f * scale;
                float leftY = dstRect.Top + dstRect.Height * 0.32f + hopOffset;
                canvas.DrawArc(new SKRect(leftX - 10f * scale, leftY - 10f * scale, leftX + 10f * scale, leftY + 10f * scale), 190f, 75f, false, _accentStrokePaint);

                // Right wing flutter breeze arc
                float rightX = dstRect.Right - 2f * scale;
                float rightY = dstRect.Top + dstRect.Height * 0.32f + hopOffset;
                canvas.DrawArc(new SKRect(rightX - 10f * scale, rightY - 10f * scale, rightX + 10f * scale, rightY + 10f * scale), 275f, 75f, false, _accentStrokePaint);
            }
        }

        // 6. Dynamic Floating Flame Sparkle (positioned per mood)
        (float normX, float normY, bool hasFlame) flameConfig = mood switch
        {
            CozyMood.Attentive => (0.83f, 0.38f, true),
            CozyMood.Overjoyed => (0.81f, 0.29f, true),
            CozyMood.Sleepy => (0f, 0f, false),
            _ => (0.78f, 0.53f, true)
        };

        if (flameConfig.hasFlame)
        {
            float flameX = dstRect.Left + dstRect.Width * flameConfig.normX;
            float flameY = dstRect.Top + dstRect.Height * flameConfig.normY + breathOffset + hopOffset;

            // Radial glowing heat aura (the gradient is the precomputed mood
            // bitmap, drawn scaled: no per-tick shader allocation).
            float glowPulse = 0.82f + 0.18f * MathF.Sin(animationTickMs * 0.008f);
            float glowRadius = (mood == CozyMood.Attentive ? 28f : 24f) * scale * glowPulse;
            SKBitmap glowBitmap = mood switch
            {
                CozyMood.Attentive => GlowAttentiveBitmap,
                CozyMood.Overjoyed => GlowOverjoyedBitmap,
                _ => GlowCalmBitmap
            };
            SKRect glowDst = new(flameX - glowRadius, flameY - glowRadius, flameX + glowRadius, flameY + glowRadius);
            canvas.DrawBitmap(glowBitmap, glowDst, LinearSampling, _glowPaint);

            // Rising embers / floating flame sparks
            for (int s = 0; s < 3; s++)
            {
                float sparkCycle = ((animationTickMs + s * 420) % 1260) / 1260f;
                float sparkY = flameY - sparkCycle * (26f * scale);
                float sparkX = flameX + MathF.Sin(sparkCycle * MathF.Tau + s * 2f) * (4.5f * scale);
                byte sparkAlpha = (byte)((1f - sparkCycle) * 210);
                _highlightPaint.Color = new SKColor(0xFF, 0xD5, 0x4F, sparkAlpha);
                float sparkSize = (2.2f - sparkCycle * 1.2f) * scale;
                canvas.DrawCircle(sparkX, sparkY, Math.Max(0.8f, sparkSize), _highlightPaint);
            }
        }

        // 7. Mood Overlay Effects
        if (mood == CozyMood.Sleepy)
        {
            // Floating 'z Z Z' drifting from sleepy dragon
            for (int z = 0; z < 3; z++)
            {
                float zProgress = ((animationTickMs + z * 850) % 2550) / 2550f;
                float zY = dstRect.Top + 42f * scale - (zProgress * 30f * scale);
                float zX = dstRect.MidX + (16f + z * 10f) * scale + MathF.Sin(zProgress * MathF.PI) * (6f * scale);
                byte zAlpha = (byte)(MathF.Sin(zProgress * MathF.PI) * 190);
                var zFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, (10f + z * 3f) * scale);
                _textSecondaryPaint.Color = new SKColor(0xA5, 0xB4, 0xFC, zAlpha); // soft lavender
                canvas.DrawTextWithFallback("z", zX, zY, zFont, _textSecondaryPaint);
            }
        }
        else if (mood == CozyMood.Overjoyed)
        {
            // Subtle ambient celebration sparkles in sanctuary corners (never crossing the dragon's face)
            float pulse1 = 0.5f + 0.5f * MathF.Sin(animationTickMs * 0.006f);
            float pulse2 = 0.5f + 0.5f * MathF.Cos(animationTickMs * 0.007f);
            _highlightPaint.Color = new SKColor(0xFF, 0xD7, 0x00, (byte)(pulse1 * 210));
            canvas.DrawCircle(rect.Left + 20f * scale, rect.Top + 20f * scale, 2.5f * scale, _highlightPaint);
            _highlightPaint.Color = new SKColor(0xFF, 0xEB, 0x3B, (byte)(pulse2 * 190));
            canvas.DrawCircle(rect.Right - 20f * scale, rect.Top + 22f * scale, 2.2f * scale, _highlightPaint);
        }
    }

    private void DrawProceduralPetFallback(
        SKCanvas canvas,
        SKRect rect,
        CozyMood mood,
        SKColor accentColor,
        float scale,
        long animationTickMs)
    {
        float cx = rect.MidX;
        float cy = rect.MidY + 4f * scale;

        int frameIndex = (int)((animationTickMs / 125) % 8);
        float breath = (frameIndex < 4 ? frameIndex : (7 - frameIndex)) * (1.2f * scale);

        float bodyW = rect.Width * 0.46f;
        float bodyH = rect.Height * 0.44f;
        float bodyY = cy + breath;

        _cardPaint.Color = new SKColor(0, 0, 0, 80);
        SKRect shadowRect = new(cx - bodyW * 0.45f, cy + bodyH * 0.48f, cx + bodyW * 0.45f, cy + bodyH * 0.58f);
        canvas.DrawOval(shadowRect, _cardPaint);

        _petAccentPaint.Color = accentColor;
        float earW = bodyW * 0.28f;
        float earH = bodyH * 0.38f;
        SKRect leftEar = new(cx - bodyW * 0.42f, bodyY - bodyH * 0.48f, cx - bodyW * 0.42f + earW, bodyY - bodyH * 0.48f + earH);
        SKRect rightEar = new(cx + bodyW * 0.42f - earW, bodyY - bodyH * 0.48f, cx + bodyW * 0.42f, bodyY - bodyH * 0.48f + earH);
        canvas.DrawRoundRect(leftEar, 6f * scale, 6f * scale, _petAccentPaint);
        canvas.DrawRoundRect(rightEar, 6f * scale, 6f * scale, _petAccentPaint);

        _petBodyPaint.Color = new SKColor(0xFE, 0xF3, 0xC7);
        SKRect bodyRect = new(cx - bodyW * 0.5f, bodyY - bodyH * 0.5f, cx + bodyW * 0.5f, bodyY + bodyH * 0.5f);
        canvas.DrawRoundRect(bodyRect, 18f * scale, 18f * scale, _petBodyPaint);

        _petEyePaint.Color = new SKColor(0x1F, 0x29, 0x37);
        float eyeY = bodyY - bodyH * 0.08f;
        float eyeOffset = bodyW * 0.22f;

        switch (mood)
        {
            case CozyMood.Sleepy:
                _petEyePaint.Style = SKPaintStyle.Stroke;
                _petEyePaint.StrokeWidth = 2f * scale;
                _petEyePaint.StrokeCap = SKStrokeCap.Round;
                canvas.DrawArc(new SKRect(cx - eyeOffset - 6f * scale, eyeY - 4f * scale, cx - eyeOffset + 6f * scale, eyeY + 4f * scale), 20f, 140f, false, _petEyePaint);
                canvas.DrawArc(new SKRect(cx + eyeOffset - 6f * scale, eyeY - 4f * scale, cx + eyeOffset + 6f * scale, eyeY + 4f * scale), 20f, 140f, false, _petEyePaint);
                _petEyePaint.Style = SKPaintStyle.Fill;
                break;

            case CozyMood.Attentive:
                canvas.DrawCircle(cx - eyeOffset, eyeY, 4f * scale, _petEyePaint);
                canvas.DrawCircle(cx + eyeOffset, eyeY, 4f * scale, _petEyePaint);
                _textPrimaryPaint.Color = SKColors.White;
                canvas.DrawCircle(cx - eyeOffset + 1.2f * scale, eyeY - 1.2f * scale, 1.5f * scale, _textPrimaryPaint);
                canvas.DrawCircle(cx + eyeOffset + 1.2f * scale, eyeY - 1.2f * scale, 1.5f * scale, _textPrimaryPaint);
                break;

            case CozyMood.Happy:
            case CozyMood.Overjoyed:
                _petEyePaint.Style = SKPaintStyle.Stroke;
                _petEyePaint.StrokeWidth = 2f * scale;
                _petEyePaint.StrokeCap = SKStrokeCap.Round;
                canvas.DrawArc(new SKRect(cx - eyeOffset - 6f * scale, eyeY - 4f * scale, cx - eyeOffset + 6f * scale, eyeY + 4f * scale), 200f, 140f, false, _petEyePaint);
                canvas.DrawArc(new SKRect(cx + eyeOffset - 6f * scale, eyeY - 4f * scale, cx + eyeOffset + 6f * scale, eyeY + 4f * scale), 200f, 140f, false, _petEyePaint);
                _petEyePaint.Style = SKPaintStyle.Fill;
                break;
        }

        _petBlushPaint.Color = new SKColor(0xFB, 0x71, 0x85, 120);
        canvas.DrawCircle(cx - eyeOffset - 4f * scale, eyeY + 7f * scale, 4f * scale, _petBlushPaint);
        canvas.DrawCircle(cx + eyeOffset + 4f * scale, eyeY + 7f * scale, 4f * scale, _petBlushPaint);

        _petEyePaint.Style = SKPaintStyle.Stroke;
        _petEyePaint.StrokeWidth = 1.5f * scale;
        canvas.DrawArc(new SKRect(cx - 3.5f * scale, eyeY + 4f * scale, cx + 3.5f * scale, eyeY + 9f * scale), 10f, 160f, false, _petEyePaint);
        _petEyePaint.Style = SKPaintStyle.Fill;

        if (mood == CozyMood.Overjoyed)
        {
            _redPaint.Color = CozyTodoPalette.CalendarLiveRed;
            float heartY = bodyY - bodyH * 0.65f - breath * 1.5f;
            canvas.DrawCircle(cx + bodyW * 0.35f, heartY, 3.5f * scale, _redPaint);
            canvas.DrawCircle(cx + bodyW * 0.42f, heartY, 3.5f * scale, _redPaint);
        }
    }

    private void RenderFeedButton(SKCanvas canvas, SKRect rect, int snackTreats, float scale)
    {
        _cardPaint.Color = CozyTodoPalette.BgCard;
        canvas.DrawRoundRect(rect, 8f * scale, 8f * scale, _cardPaint);

        bool canFeed = CozyCompanionEngine.CanFeed(snackTreats);
        _borderPaint.Color = canFeed ? CozyTodoPalette.HighlightAmber : CozyTodoPalette.Border;
        _borderPaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(rect, 8f * scale, 8f * scale, _borderPaint);

        var btnFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(12f, 15f * scale));
        if (_btnTreats != snackTreats)
        {
            _btnTreats = snackTreats;
            _btnText = $"Feed Pet  🍖 {snackTreats}";
        }
        string btnText = _btnText;
        _textPrimaryPaint.Color = canFeed ? CozyTodoPalette.HighlightAmber : CozyTodoPalette.TextSecondary;
        float textW = FontHelper.MeasureTextWithFallback(btnText, btnFont);
        canvas.DrawTextWithFallback(btnText, rect.MidX - textW * 0.5f, rect.MidY + 5f * scale, btnFont, _textPrimaryPaint);
    }

    private void RenderPomodoroCard(
        SKCanvas canvas,
        SKRect cardRect,
        SKRect btnRect,
        FocusState focus,
        float scale)
    {
        _cardPaint.Color = CozyTodoPalette.BgDark;
        canvas.DrawRoundRect(cardRect, 8f * scale, 8f * scale, _cardPaint);

        _borderPaint.Color = focus.IsRunning ? CozyTodoPalette.CalendarAzure : CozyTodoPalette.Border;
        _borderPaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(cardRect, 8f * scale, 8f * scale, _borderPaint);

        // Header label above countdown
        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(9f, 11f * scale));
        _textSecondaryPaint.Color = focus.IsRunning ? CozyTodoPalette.CalendarAzure : CozyTodoPalette.TextSecondary;
        canvas.DrawTextWithFallback("FOCUS TIMER", cardRect.Left + 12f * scale, cardRect.Top + 18f * scale, labelFont, _textSecondaryPaint);

        // Time countdown string with larger, highly visible 25px bold digital font
        TimeSpan remaining = focus.Duration > focus.Elapsed ? (focus.Duration - focus.Elapsed) : TimeSpan.Zero;
        int remMinutes = (int)remaining.TotalMinutes;
        int remSeconds = remaining.Seconds;
        if (_timeMinutes != remMinutes || _timeSeconds != remSeconds)
        {
            _timeMinutes = remMinutes;
            _timeSeconds = remSeconds;
            _timeText = $"{remMinutes:00}:{remSeconds:00}";
        }
        string timeStr = _timeText;

        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(18f, 25f * scale));
        _textPrimaryPaint.Color = focus.IsRunning ? CozyTodoPalette.CalendarAzure : CozyTodoPalette.TextPrimary;
        canvas.DrawTextWithFallback(timeStr, cardRect.Left + 12f * scale, cardRect.Top + 47f * scale, timeFont, _textPrimaryPaint);

        // Play/Pause button
        _azurePaint.Color = focus.IsRunning ? CozyTodoPalette.CalendarAzure : CozyTodoPalette.BgCard;
        canvas.DrawRoundRect(btnRect, 6f * scale, 6f * scale, _azurePaint);

        _borderPaint.Color = CozyTodoPalette.CalendarAzure;
        canvas.DrawRoundRect(btnRect, 6f * scale, 6f * scale, _borderPaint);

        // Draw icon in button
        float iconCx = btnRect.MidX;
        float iconCy = btnRect.MidY;
        _textPrimaryPaint.Color = focus.IsRunning ? SKColors.White : CozyTodoPalette.CalendarAzure;

        if (focus.IsRunning)
        {
            // Pause bars ||
            float barW = Math.Max(2.5f, 3f * scale);
            float barH = Math.Max(8f, 12f * scale);
            canvas.DrawRect(iconCx - barW * 1.6f, iconCy - barH * 0.5f, barW, barH, _textPrimaryPaint);
            canvas.DrawRect(iconCx + barW * 0.6f, iconCy - barH * 0.5f, barW, barH, _textPrimaryPaint);
        }
        else
        {
            // Play triangle ▶
            EnsurePlayTriangle(btnRect, scale);
            if (_playTriangle is not null)
            {
                canvas.DrawPath(_playTriangle, _textPrimaryPaint);
            }
        }
    }

    private void EnsurePlayTriangle(SKRect btnRect, float scale)
    {
        if (_playTriangle is not null && _cachedPomoBtnRect == btnRect) return;
        _cachedPomoBtnRect = btnRect;
        _playTriangle?.Dispose();

        float iconCx = btnRect.MidX;
        float iconCy = btnRect.MidY;
        float triSize = Math.Max(5f, 7f * scale);

        using var builder = new SKPathBuilder();
        builder.MoveTo(iconCx - triSize * 0.6f, iconCy - triSize);
        builder.LineTo(iconCx + triSize * 0.8f, iconCy);
        builder.LineTo(iconCx - triSize * 0.6f, iconCy + triSize);
        builder.Close();
        _playTriangle = builder.Detach();
    }

    private void RenderTasksPanel(
        SKCanvas canvas,
        CozyTodoGeometry layout,
        float scale)
    {
        SKRect panel = layout.TasksPanelRect;
        _panelPaint.Color = CozyTodoPalette.BgPanel;
        canvas.DrawRoundRect(panel, 14f * scale, 14f * scale, _panelPaint);

        _borderPaint.Color = CozyTodoPalette.Border;
        _borderPaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(panel, 14f * scale, 14f * scale, _borderPaint);

        // Panel Header (Enlarged 17px bold uppercase)
        var headerFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(13f, 17f * scale));
        var badgeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(11f, 13f * scale));

        float headerY = panel.Top + 28f * scale;
        _textPrimaryPaint.Color = CozyTodoPalette.TextPrimary;
        canvas.DrawTextWithFallback("DAILY TASKS", panel.Left + 18f * scale, headerY, headerFont, _textPrimaryPaint);

        int totalCount = layout.TaskRows.Count;
        int doneCount = 0;
        for (int t = 0; t < layout.TaskRows.Count; t++)
        {
            if (layout.TaskRows[t].IsDone) doneCount++;
        }
        if (_badgeDone != doneCount || _badgeTotal != totalCount)
        {
            _badgeDone = doneCount;
            _badgeTotal = totalCount;
            _badgeText = $"{doneCount} / {totalCount}";
        }
        string countBadge = _badgeText;
        float countW = FontHelper.MeasureTextWithFallback(countBadge, badgeFont) + 18f * scale;
        float countH = 22f * scale;
        SKRect countRect = new(panel.Right - 18f * scale - countW, headerY - 15f * scale, panel.Right - 18f * scale, headerY - 15f * scale + countH);
        _cardPaint.Color = CozyTodoPalette.BgCard;
        canvas.DrawRoundRect(countRect, countH / 2f, countH / 2f, _cardPaint);
        _borderPaint.Color = CozyTodoPalette.Border;
        canvas.DrawRoundRect(countRect, countH / 2f, countH / 2f, _borderPaint);
        _textSecondaryPaint.Color = CozyTodoPalette.TextSecondary;
        canvas.DrawTextWithFallback(countBadge, countRect.Left + 9f * scale, countRect.MidY + 4.5f * scale, badgeFont, _textSecondaryPaint);

        if (totalCount == 0)
        {
            var emptyFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(14f, 18f * scale));
            var emptySubFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(12f, 14f * scale));
            string emptyMsg = "All caught up! No active tasks.";
            string emptySub = "Add tasks in ModernWigiDash Inspector or enable Super Productivity.";
            float emW = FontHelper.MeasureTextWithFallback(emptyMsg, emptyFont);
            float emSubW = FontHelper.MeasureTextWithFallback(emptySub, emptySubFont);
            _textPrimaryPaint.Color = CozyTodoPalette.TextPrimary;
            canvas.DrawTextWithFallback(emptyMsg, panel.MidX - emW * 0.5f, panel.MidY - 10f * scale, emptyFont, _textPrimaryPaint);
            _textSecondaryPaint.Color = CozyTodoPalette.TextSecondary;
            canvas.DrawTextWithFallback(emptySub, panel.MidX - emSubW * 0.5f, panel.MidY + 18f * scale, emptySubFont, _textSecondaryPaint);
            return;
        }

        // Clip tasks to inner area (below header)
        SKRect scrollArea = new(panel.Left, panel.Top + 42f * scale, panel.Right, panel.Bottom - 6f * scale);
        canvas.Save();
        canvas.ClipRect(scrollArea);

        // Enlarged typography for task rows
        float rowTitleFontSize = Math.Max(14f, 18f * scale);
        var rowTitleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, rowTitleFontSize);
        var catFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(10f, 13f * scale));

        for (int i = 0; i < layout.TaskRows.Count; i++)
        {
            var row = layout.TaskRows[i];
            if (row.RowRect.Bottom < panel.Top || row.RowRect.Top > panel.Bottom)
            {
                continue; // culled
            }

            // Row card background
            _cardPaint.Color = CozyTodoPalette.BgCard;
            canvas.DrawRoundRect(row.RowRect, 10f * scale, 10f * scale, _cardPaint);

            _borderPaint.Color = row.IsDone ? CozyTodoPalette.AccentGreen.WithAlpha(80) : CozyTodoPalette.Border;
            canvas.DrawRoundRect(row.RowRect, 10f * scale, 10f * scale, _borderPaint);

            // Checkbox (enlarged)
            SKRect check = row.CheckboxRect;
            if (row.IsDone)
            {
                _greenPaint.Color = CozyTodoPalette.AccentGreen;
                canvas.DrawRoundRect(check, 6f * scale, 6f * scale, _greenPaint);

                // Checkmark icon ✓
                _greenStrokePaint.Color = SKColors.White;
                _greenStrokePaint.StrokeWidth = 2.2f * scale;
                float p1x = check.Left + check.Width * 0.25f;
                float p1y = check.Top + check.Height * 0.52f;
                float p2x = check.Left + check.Width * 0.44f;
                float p2y = check.Top + check.Height * 0.72f;
                float p3x = check.Left + check.Width * 0.76f;
                float p3y = check.Top + check.Height * 0.30f;
                canvas.DrawLine(p1x, p1y, p2x, p2y, _greenStrokePaint);
                canvas.DrawLine(p2x, p2y, p3x, p3y, _greenStrokePaint);
            }
            else
            {
                _borderPaint.Color = CozyTodoPalette.Border;
                _borderPaint.StrokeWidth = 1.5f * scale;
                canvas.DrawRoundRect(check, 6f * scale, 6f * scale, _borderPaint);
            }

            // Category pill if present (docked on right edge of row card)
            float categoryPillWidth = 0f;
            if (!string.IsNullOrWhiteSpace(row.Category))
            {
                float pillW = FontHelper.MeasureTextWithFallback(row.Category, catFont) + 14f * scale;
                float pillH = Math.Max(18f, 22f * scale);
                SKRect pillRect = new(row.RowRect.Right - pillW - 14f * scale, row.RowRect.MidY - pillH * 0.5f, row.RowRect.Right - 14f * scale, row.RowRect.MidY + pillH * 0.5f);

                _azurePaint.Color = CozyTodoPalette.CalendarAzure.WithAlpha(40);
                canvas.DrawRoundRect(pillRect, pillH * 0.5f, pillH * 0.5f, _azurePaint);

                _textPrimaryPaint.Color = CozyTodoPalette.CalendarAzure;
                canvas.DrawTextWithFallback(row.Category, pillRect.Left + 7f * scale, pillRect.MidY + 4f * scale, catFont, _textPrimaryPaint);

                categoryPillWidth = pillW + 18f * scale;
            }

            // Task title with multi-line word wrapping and boundary containment
            float titleStartX = check.Right + 14f * scale;
            float maxTitleWidth = Math.Max(60f, row.RowRect.Right - titleStartX - categoryPillWidth - 14f * scale);
            _textPrimaryPaint.Color = row.IsDone ? CozyTodoPalette.TextSecondary : CozyTodoPalette.TextPrimary;

            var wrappedLines = _wrappedTitles.GetOrWrap(row.Title, rowTitleFont, rowTitleFontSize, maxTitleWidth);
            if (wrappedLines.Count == 1)
            {
                float lineY = row.RowRect.MidY + 6f * scale;
                canvas.DrawTextWithFallback(wrappedLines[0], titleStartX, lineY, rowTitleFont, _textPrimaryPaint);
                if (row.IsDone)
                {
                    float lineW = FontHelper.MeasureTextWithFallback(wrappedLines[0], rowTitleFont);
                    _strikePaint.Color = CozyTodoPalette.TextSecondary;
                    _strikePaint.StrokeWidth = 1.5f * scale;
                    canvas.DrawLine(titleStartX, lineY - 5f * scale, titleStartX + lineW, lineY - 5f * scale, _strikePaint);
                }
            }
            else if (wrappedLines.Count >= 2)
            {
                float line1Y = row.RowRect.MidY - 6.5f * scale;
                float line2Y = row.RowRect.MidY + 13f * scale;

                canvas.DrawTextWithFallback(wrappedLines[0], titleStartX, line1Y, rowTitleFont, _textPrimaryPaint);
                canvas.DrawTextWithFallback(wrappedLines[1], titleStartX, line2Y, rowTitleFont, _textPrimaryPaint);

                if (row.IsDone)
                {
                    _strikePaint.Color = CozyTodoPalette.TextSecondary;
                    _strikePaint.StrokeWidth = 1.5f * scale;
                    float line1W = FontHelper.MeasureTextWithFallback(wrappedLines[0], rowTitleFont);
                    float line2W = FontHelper.MeasureTextWithFallback(wrappedLines[1], rowTitleFont);
                    canvas.DrawLine(titleStartX, line1Y - 5f * scale, titleStartX + line1W, line1Y - 5f * scale, _strikePaint);
                    canvas.DrawLine(titleStartX, line2Y - 5f * scale, titleStartX + line2W, line2Y - 5f * scale, _strikePaint);
                }
            }
        }

        canvas.Restore();
    }

    /// <summary>
    /// Releases hoisted native SkiaSharp paints and resources.
    /// </summary>
    public void Dispose()
    {
        _playTriangle?.Dispose();
        _playTriangle = null;
        _bgPaint.Dispose();
        _panelPaint.Dispose();
        _cardPaint.Dispose();
        _borderPaint.Dispose();
        _accentPaint.Dispose();
        _accentStrokePaint.Dispose();
        _highlightPaint.Dispose();
        _greenPaint.Dispose();
        _greenStrokePaint.Dispose();
        _azurePaint.Dispose();
        _azureStrokePaint.Dispose();
        _redPaint.Dispose();
        _textPrimaryPaint.Dispose();
        _textSecondaryPaint.Dispose();
        _strikePaint.Dispose();
        _petBodyPaint.Dispose();
        _petAccentPaint.Dispose();
        _petBlushPaint.Dispose();
        _petEyePaint.Dispose();
        _bitmapPaint.Dispose();
        _glowPaint.Dispose();
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace FModel.Views;

public partial class FontPreviewWindow
{
    private static FontPreviewWindow _instance;

    public static FontPreviewWindow GetOrCreate()
    {
        if (_instance is null || !_instance.IsLoaded)
        {
            _instance = new FontPreviewWindow();
        }
        return _instance;
    }

    private FontPreviewWindow()
    {
        InitializeComponent();
    }

    public void AddOrActivateFont(string displayName, byte[] fontBytes, string sourceType)
    {
        foreach (TabItem existing in FontTabControl.Items)
        {
            if (existing.Tag is string tag && tag == displayName)
            {
                FontTabControl.SelectedItem = existing;
                StatusText.Text = string.Empty;
                return;
            }
        }

        SKTypeface typeface;
        try
        {
            typeface = SKTypeface.FromData(SKData.CreateCopy(fontBytes));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не вдалося завантажити '{displayName}': {ex.Message}";
            return;
        }

        if (typeface is null)
        {
            StatusText.Text = $"'{displayName}': SKTypeface.FromData повернув null.";
            return;
        }

        var tab = BuildFontTab(displayName, sourceType, typeface);
        FontTabControl.Items.Add(tab);
        FontTabControl.SelectedItem = tab;
        StatusText.Text = string.Empty;
    }

    // ── Побудова вкладки ──────────────────────────────────────────────────────

    private static TabItem BuildFontTab(string displayName, string sourceType, SKTypeface typeface)
    {
        // Стан вкладки
        int glyphSize = 36;
        int rangeStart = 0x0400;
        int rangeEnd = 0x04FF;

        // ── Діапазони
        var ranges = new (string Label, int Start, int End)[]
        {
            ("Cyrillic (U+0400–04FF)",            0x0400, 0x04FF),
            ("Basic Latin (U+0020–007F)",     0x0020, 0x007F),
            ("Latin Extended A (U+0080–00FF)",     0x0080, 0x00FF),
            ("Greek (U+0370–03FF)",              0x0370, 0x03FF),
            ("Digits and Punctuation (U+0020–0040)",  0x0020, 0x0040),
        };

        // ── Корінь вкладки
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Рядок 0: ім'я + badge
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var nameText = new TextBlock
        {
            Text = displayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border
        {
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(4),
            Background = SystemColors.HighlightBrush,
            Child = new TextBlock { Text = sourceType, Foreground = Brushes.White, FontSize = 11 },
        };
        header.Children.Add(badge);
        header.Children.Add(nameText);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var controls = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sizeLabel0 = new TextBlock { Text = "Size:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(sizeLabel0, 0);

        var slider = new Slider
        {
            Minimum = 12,
            Maximum = 72,
            Value = 36,
            TickFrequency = 4,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(slider, 1);

        var sizeLabel = new TextBlock { Text = "36px", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0), Width = 36 };
        Grid.SetColumn(sizeLabel, 2);

        var rangeLabel0 = new TextBlock { Text = "Range:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(rangeLabel0, 3);

        var rangeCombo = new ComboBox { Width = 250 };
        foreach (var (lbl, _, _) in ranges)
            rangeCombo.Items.Add(lbl);
        rangeCombo.SelectedIndex = 0;
        Grid.SetColumn(rangeCombo, 4);

        controls.Children.Add(sizeLabel0);
        controls.Children.Add(slider);
        controls.Children.Add(sizeLabel);
        controls.Children.Add(rangeLabel0);
        controls.Children.Add(rangeCombo);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);

        var glyphPanel = new WrapPanel { Margin = new Thickness(6) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = glyphPanel,
        };
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = SystemColors.ControlDarkBrush,
            Child = scroll,
        };
        Grid.SetRow(border, 2);
        root.Children.Add(border);

        var tabStatus = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontStyle = FontStyles.Italic,
            Foreground = Brushes.Gray,
        };
        Grid.SetRow(tabStatus, 3);
        root.Children.Add(tabStatus);

        void Render()
        {
            RenderGlyphs(glyphPanel, tabStatus, typeface, glyphSize, rangeStart, rangeEnd);
        }

        slider.ValueChanged += (_, e) =>
        {
            glyphSize = (int)e.NewValue;
            if (sizeLabel is null || typeface is null) return;
            sizeLabel.Text = $"{glyphSize}px";
            Render();
        };

        rangeCombo.SelectionChanged += (_, _) =>
        {
            int idx = rangeCombo.SelectedIndex;
            if (idx < 0 || idx >= ranges.Length) return;
            (rangeStart, rangeEnd) = (ranges[idx].Start, ranges[idx].End);
            Render();
        };

        root.Loaded += (_, _) => Render();

        var tabHeaderPanel = new StackPanel { Orientation = Orientation.Horizontal };
        tabHeaderPanel.Children.Add(new TextBlock { Text = displayName, VerticalAlignment = VerticalAlignment.Center });
        var closeBtn = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(3, 0, 3, 0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
        };
        tabHeaderPanel.Children.Add(closeBtn);

        var tabItem = new TabItem
        {
            Header = tabHeaderPanel,
            Content = root,
            Tag = displayName,
        };

        closeBtn.Click += (_, _) =>
        {
            if (tabItem.Parent is TabControl tc)
            {
                tc.Items.Remove(tabItem);
                typeface.Dispose();
            }
        };

        return tabItem;
    }

    private static void RenderGlyphs(WrapPanel panel, TextBlock statusText, SKTypeface typeface, int glyphSize, int rangeStart, int rangeEnd)
    {
        panel.Children.Clear();

        if (typeface is null) return;

        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = glyphSize,
            IsAntialias = true,
            Color = SKColors.White,
        };

        int cellSize = (int)(glyphSize * 1.4f);
        int rendered = 0;
        int missing = 0;

        for (int cp = rangeStart; cp <= rangeEnd; cp++)
        {
            var ch = char.ConvertFromUtf32(cp);
            ushort[] glyphIds = typeface.GetGlyphs(ch);
            bool hasGlyph = glyphIds.Length > 0 && glyphIds[0] != 0;

            UIElement cell;
            if (hasGlyph)
            {
                var bitmap = RenderSingleGlyph(ch, paint, cellSize);
                if (bitmap is null) { missing++; continue; }
                var img = BitmapToImageSource(bitmap);
                bitmap.Dispose();
                cell = BuildGlyphCell(img, cp, ch, cellSize, present: true);
                rendered++;
            }
            else
            {
                cell = BuildGlyphCell(null, cp, ch, cellSize, present: false);
                missing++;
            }

            panel.Children.Add(cell);
        }

        statusText.Text = $"Shown: {rendered} Missing: {missing}";
    }

    private static SKBitmap RenderSingleGlyph(string ch, SKPaint paint, int cellSize)
    {
        try
        {
            var bitmap = new SKBitmap(cellSize, cellSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(0x28, 0x28, 0x28));

            var metrics = paint.FontMetrics;
            float fontHeight = metrics.Descent - metrics.Ascent;
            float topPadding = (cellSize - fontHeight) / 2f + 3f;
            float baseline = topPadding + (-metrics.Ascent);

            var bounds = new SKRect();
            paint.MeasureText(ch, ref bounds);
            float x = (cellSize - bounds.Width) / 2f - bounds.Left;

            canvas.DrawText(ch, x, baseline, paint);
            return bitmap;
        }
        catch { return null; }
    }

    private static UIElement BuildGlyphCell(
        ImageSource img, int codepoint, string ch, int cellSize, bool present)
    {
        UIElement visual;
        if (present && img is not null)
        {
            visual = new Image
            {
                Source = img,
                Width = cellSize,
                Height = cellSize,
                Stretch = Stretch.None,
            };
        }
        else
        {
            // Червоний квадрат для відсутніх гліфів
            visual = new Border
            {
                Width = cellSize,
                Height = cellSize,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "?",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x60)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = cellSize * 0.35,
                },
            };
        }

        string charDisplay = codepoint >= 0x20 ? ch : "·";
        var label = new TextBlock
        {
            Text = $"{charDisplay}",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = present ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0xC0, 0x30, 0x30)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(2),
            Width = cellSize,
            ToolTip = $"U+{codepoint:X4} '{charDisplay}' {(present ? "present" : "missing")}",
        };
        stack.Children.Add(visual);
        stack.Children.Add(label);
        return stack;
    }

    private static ImageSource BitmapToImageSource(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var ms = new System.IO.MemoryStream(data.ToArray());
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (TabItem tab in FontTabControl.Items)
        {
            if (tab.Content is Grid g && g.Tag is SKTypeface tf)
                tf.Dispose();
        }
        _instance = null;
        base.OnClosed(e);
    }

    public void FocusWindow()
    {
        WindowState = WindowState.Normal;
        Activate();
    }
}
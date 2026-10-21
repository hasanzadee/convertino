using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Convertino.Native;

namespace Convertino.Ui;

/// <summary>The panel's "Recipes" card (your chains of petals and commands, a small builder) and "Save results".</summary>
public sealed partial class Flyout
{
    const string GlyphDown = "", GlyphUp = "", GlyphAdd = "", GlyphCode = "", GlyphWarn = "", GlyphX = "";

    bool _recipesOpen;    // the card stays folded until it's clicked, so the panel keeps its height
    RecipeDraft? _draft;  // the builder, while it's open

    /// <summary>A recipe being put together in the panel.</summary>
    sealed class RecipeDraft
    {
        public Kind Kind = Kind.Image;
        public List<string> For = [];
        public List<string> Steps = [];
        public string Name = "";
        public string Icon = "recipe";
        public string? Out;
        public string? Was;   // the name of the recipe being changed
        public bool Picking;  // the list of possible next steps is open
        public string? Error;

        public static RecipeDraft Of(Recipe r) => new()
        {
            Kind = Recipes.KindOf(r), For = [.. r.For], Steps = [.. r.Steps], Name = r.Name, Icon = r.Icon ?? "recipe", Out = r.Out, Was = r.Name,
        };
    }

    // ---------- recipes ----------

    UIElement RecipesCard()
    {
        var book = Recipes.Load();
        var names = book.Recipes.Select(r => r.Name).Concat(book.Commands.Select(c => c.Name)).ToList();
        var body = new StackPanel();

        // the header says what's there; a click folds the card open or shut
        var chevron = new TextBlock { Text = _recipesOpen ? GlyphUp : GlyphDown, FontFamily = Icons, FontSize = 11, Foreground = B(T.SubText), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = names.Count == 0 ? L.T("Chain petals into one") : Counted(book), FontFamily = TextF, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = B(T.Text) });
        text.Children.Add(new TextBlock
        {
            Text = names.Count == 0 ? L.T("e.g. {0}", $"Web-ready = {L.T("Full HD")} → WEBP → {L.T("Clean")}") : string.Join(" · ", names),
            FontFamily = TextF, FontSize = 11, Foreground = B(T.SubText), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        head.Children.Add(Beside(IconBox("recipe", accent: true), text));
        head.MouseLeftButtonUp += (_, e) =>
        {
            _recipesOpen = !_recipesOpen;
            if (!_recipesOpen) _draft = null;
            Build();
            e.Handled = true;
        };
        body.Children.Add(head);
        if (!_recipesOpen) return Card(body, new Thickness(0));

        if (Recipes.Broken) body.Children.Add(Note(GlyphWarn, L.T("recipes.json can't be read: fix it, or the next change starts it afresh (the old one is kept)"), T.Danger));
        if (_draft != null)
        {
            body.Children.Add(Builder(_draft));
            return Card(body, new Thickness(0));
        }

        foreach (var r in book.Recipes) body.Children.Add(RecipeRow(r));
        foreach (var c in book.Commands) body.Children.Add(CommandRow(c));

        // one-click templates (only those that work here and aren't added yet)
        var offered = Recipes.Offered();
        if (offered.Count > 0)
        {
            body.Children.Add(Caption(names.Count == 0 ? L.T("Start with one:") : L.T("Add one:"), new Thickness(2, 8, 0, 2)));
            var wrap = new WrapPanel { Margin = new Thickness(-2, 0, -2, 2) };
            foreach (var t in offered)
            {
                var pill = Pill("+ " + t.Name, false, () =>
                {
                    try { Recipes.Save(t); } catch (Exception ex) { Log.Error("recipe template", ex); }
                    Build();
                });
                pill.ToolTip = string.Join(" → ", Recipes.Describe(t).Steps);
                wrap.Children.Add(pill);
            }
            body.Children.Add(wrap);
        }
        var buttons = new WrapPanel { Margin = new Thickness(-8, 2, 0, 0) };
        buttons.Children.Add(FooterButton(GlyphAdd, L.T("New recipe…"), () => { _draft = new() { Picking = true }; Build(); }));
        var json = (FrameworkElement)FooterButton(GlyphCode, "recipes.json", () => { Shell.Open(Recipes.EnsureFile()); HideAnimated(); });
        json.ToolTip = L.T("Custom commands and hand edits (it can run programs: only you write it)");
        buttons.Children.Add(json);
        body.Children.Add(buttons);
        return Card(body, new Thickness(0));
    }

    static string Counted(RecipeFile book)
    {
        int r = book.Recipes.Count, c = book.Commands.Count;
        var parts = new List<string>();
        if (r > 0) parts.Add(L.N(r, "{0} recipe", "{0} recipes"));
        if (c > 0) parts.Add(L.N(c, "{0} command", "{0} commands"));
        return string.Join(" · ", parts);
    }

    UIElement RecipeRow(Recipe r)
    {
        var (steps, problem) = Recipes.Describe(r);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Name, FontFamily = TextF, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = B(T.Text), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Chain(steps));
        text.Children.Add(new TextBlock
        {
            Text = problem ?? L.T("for {0}", Recipes.ForText(r.For)) + (r.Out != null ? " · " + L.T("results {0}", Output.Describe(r.Out)) : ""),
            FontFamily = TextF, FontSize = 11, Foreground = B(problem != null ? T.Danger : T.SubText), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });
        var main = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L.T("Change it"), Child = Beside(IconBox(r.Icon ?? "recipe"), text) };
        main.MouseLeftButtonUp += (_, e) => { _draft = RecipeDraft.Of(r); Build(); e.Handled = true; };
        return Removable(main, L.T("Delete the recipe"), () => Recipes.Remove(r.Name));
    }

    UIElement CommandRow(CustomCommand c)
    {
        bool found = Services.Commands.Resolve(c.Program, out var why) != null;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = c.Name, FontFamily = TextF, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = B(T.Text), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = L.T("runs {0}", $"{c.Program} {string.Join(' ', c.Args)}"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10.5,
            Foreground = B(T.SubText), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = found ? L.T("for {0}", Recipes.ForText(c.For)) : why, FontFamily = TextF, FontSize = 11,
            Foreground = B(found ? T.SubText : T.Danger), Margin = new Thickness(0, 1, 0, 0),
        });
        var main = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L.T("Edit in recipes.json"), Child = Beside(IconBox(c.Icon ?? "terminal"), text) };
        main.MouseLeftButtonUp += (_, _) => { Shell.Open(Recipes.EnsureFile()); HideAnimated(); };
        return Removable(main, L.T("Delete the command"), () => Recipes.RemoveCommand(c.Name));
    }

    /// <summary>A row with a ✕ on the right.</summary>
    UIElement Removable(FrameworkElement main, string tip, Func<bool> remove)
    {
        var x = (FrameworkElement)FooterButton(GlyphX, "", () =>
        {
            try { remove(); } catch (Exception ex) { Log.Error("recipe remove", ex); }
            Build();
        });
        x.ToolTip = tip;
        x.VerticalAlignment = VerticalAlignment.Top;
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
        DockPanel.SetDock(x, Dock.Right);
        row.Children.Add(x);
        row.Children.Add(main);
        return row;
    }

    /// <summary>"→ Full HD → WEBP → Clean" as chips, wrapping; <paramref name="remove"/> adds a ✕ that cuts the chain from that step on.</summary>
    WrapPanel Chain(IReadOnlyList<string> steps, Action<int>? remove = null)
    {
        var chips = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        for (int i = 0; i < steps.Count; i++)
        {
            int at = i;
            chips.Children.Add(new TextBlock { Text = "→", FontFamily = TextF, FontSize = 11.5, Foreground = B(T.SubText), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = steps[i], FontFamily = TextF, FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = B(T.Text) });
            var chip = new Border
            {
                Child = label, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 1, 5, 2),
                Background = B(A(T.AccentA, T.IsDark ? .16 : .13)), BorderBrush = B(A(T.AccentA, T.IsDark ? .28 : .22)), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (remove != null)
            {
                label.Children.Add(new TextBlock { Text = GlyphX, FontFamily = Icons, FontSize = 8, Foreground = B(T.SubText), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 1, 0, 0) });
                chip.Cursor = Cursors.Hand;
                chip.ToolTip = at == steps.Count - 1 ? L.T("Remove this step") : L.T("Remove this step and the ones after it");
                chip.MouseLeftButtonUp += (_, e) => { remove(at); e.Handled = true; };
            }
            chips.Children.Add(chip);
        }
        return chips;
    }

    UIElement Builder(RecipeDraft d)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(Caption(d.Was == null ? L.T("New recipe, for") : L.T("Change {0}, for", d.Was), new Thickness(2, 6, 0, 2)));
        var kinds = new WrapPanel { Margin = new Thickness(-2, 0, -2, 2) };
        foreach (var (kind, name) in Recipes.BuilderKinds())
            kinds.Children.Add(Pill(char.ToUpper(name[0], L.Culture) + name[1..], kind == d.Kind, () =>
            {
                if (d.Kind != kind) { d.Kind = kind; d.For = []; d.Steps.Clear(); d.Error = null; d.Picking = d.Steps.Count == 0; }
                Build();
            }));
        sp.Children.Add(kinds);

        // the chain so far, then what can come next
        sp.Children.Add(Caption(L.T("Steps"), new Thickness(2, 6, 0, 0)));
        var next = Recipes.NextSteps(d.Kind, d.Steps);
        var chain = Chain(Recipes.Labels(d.Steps), at => { d.Steps.RemoveRange(at, d.Steps.Count - at); d.Error = null; Build(); });
        if (next.Count > 0 && d.Steps.Count < Recipes.MaxSteps)
            chain.Children.Add(Pill(d.Picking ? L.T("Close") : d.Steps.Count == 0 ? L.T("+ First step") : L.T("+ Add step"), d.Picking, () => { d.Picking = !d.Picking; Build(); }));
        else if (d.Steps.Count > 0)
            chain.Children.Add(Caption(L.T("nothing can follow {0}", Recipes.Labels(d.Steps)[^1]), new Thickness(2, 3, 0, 0)));
        sp.Children.Add(chain);
        if (d.Picking && next.Count > 0)
        {
            var grid = new WrapPanel { Margin = new Thickness(-2, 4, -2, 2) };
            foreach (var a in next)
            {
                var pill = Pill(a.Label, false, () => { d.Steps.Add(a.Id); d.Error = null; Build(); });
                pill.ToolTip = a.Verb;
                grid.Children.Add(pill);
            }
            sp.Children.Add(grid);
        }

        // name and icon
        sp.Children.Add(Caption(L.T("Name"), new Thickness(2, 8, 0, 2)));
        var (field, box) = Field(d.Name, L.T("e.g. {0}", "Web-ready"));
        box.TextChanged += (_, _) => d.Name = box.Text;
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveDraft(d); e.Handled = true; } };
        sp.Children.Add(field);
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var key in Recipes.IconChoices)
        {
            bool on = key == d.Icon;
            var b = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand, ToolTip = key,
                Background = on ? new LinearGradientBrush(T.AccentA, T.AccentB, 45) : B(A(T.SubText, .12)),
                Child = new TextBlock { Text = Ui.Icons.Glyph(key) ?? "", FontFamily = Icons, FontSize = 13, Foreground = B(on ? T.AccentText : T.Text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            b.MouseLeftButtonUp += (_, e) => { d.Icon = key; Build(); e.Handled = true; };
            icons.Children.Add(b);
        }
        sp.Children.Add(icons);
        if (d.Error != null) sp.Children.Add(Note(GlyphWarn, d.Error, T.Danger));

        var save = Pill(d.Was == null ? L.T("Save recipe") : L.T("Save changes"), true, () => SaveDraft(d));
        save.Padding = new Thickness(14, 5, 14, 6);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 10, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(FooterButton("", L.T("Cancel"), () => { _draft = null; Build(); }));
        sp.Children.Add(buttons);
        return sp;
    }

    void SaveDraft(RecipeDraft d)
    {
        var r = new Recipe
        {
            Name = d.Name.Trim(), Icon = d.Icon, Steps = [.. d.Steps], Out = d.Out,
            For = d.For.Count > 0 ? [.. d.For] : [d.Kind.ToString().ToLowerInvariant()],
        };
        try
        {
            // saving replaces a recipe of the same name: from here that has to be the one being changed, not another
            if (Recipes.Find(r.Name) is { } other && (d.Was == null || Recipes.IdOf(other) != Recipes.IdOf(d.Was)))
                throw new ArgumentException(L.T("There's already a recipe called {0}", other.Name));
            Recipes.Save(r);
            if (d.Was != null && Recipes.IdOf(d.Was) != Recipes.IdOf(r)) Recipes.Remove(d.Was); // renamed
            _draft = null;
        }
        catch (ArgumentException ex) { d.Error = ex.Message; }
        catch (Exception ex) { Log.Error("recipe save", ex); d.Error = Jobs.Friendly(ex); }
        Build();
    }

    // ---------- save results ----------

    UIElement SaveCard()
    {
        var s = Settings.Current;
        var place = Output.Global;
        string mode = place == Output.Beside ? "beside" : Path.IsPathFullyQualified(place) ? "folder" : "subfolder";
        var pills = new UniformGrid { Columns = 3, Margin = new Thickness(-2, 4, -2, 2) };
        foreach (var (id, label) in new[] { ("beside", L.T("Beside it")), ("subfolder", L.T("Subfolder")), ("folder", L.T("Fixed folder")) })
            pills.Children.Add(Pill(label, id == mode, () =>
            {
                // a fixed folder needs picking first (or again, when it's already the choice)
                if (id == "folder" && (mode == "folder" || Output.Valid(s.SaveFolder) is not { } dir || !Path.IsPathFullyQualified(dir))) { PickSaveFolder(); return; }
                s.SaveTo = id;
                s.Save();
                Build();
            }));
        var body = new StackPanel { Children = { pills } };
        if (mode == "subfolder")
        {
            var (field, box) = Field(s.SaveSubfolder, "Converted");
            box.TextChanged += (_, _) =>
            {
                bool ok = Watches.ValidOut(box.Text) != null;
                box.BorderBrush = B(ok ? A(T.SubText, .3) : T.Danger);
                if (ok) { s.SaveSubfolder = box.Text.Trim(); s.Save(); }
            };
            field.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(field);
        }
        body.Children.Add(Caption(mode switch
        {
            "folder" => place,
            "subfolder" => L.T("made inside the original's folder; a recipe can pick its own"),
            _ => L.T("results land next to the original; a recipe can pick its own place"),
        }, new Thickness(2, 4, 0, 4)));
        if (mode == "folder") body.Children.Add(FooterButton(Ui.Icons.Glyph("folder") ?? "", L.T("Change folder…"), PickSaveFolder));
        return Card(body, new Thickness(0));
    }

    void PickSaveFolder()
    {
        // like the watch folders: step aside for the dialog, come back with the choice made
        HideAnimated();
        string? path = null;
        try
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("Results of every petal go to this folder"), UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) path = dlg.SelectedPath;
        }
        catch (Exception ex) { Log.Error("folder picker", ex); }
        if (path != null)
        {
            Settings.Current.SaveFolder = path;
            Settings.Current.SaveTo = "folder";
            Settings.Current.Save();
        }
        _hiddenAt = DateTime.MinValue;
        Dispatcher.BeginInvoke(() => { if (!IsVisible || _hiding) { _hideTimer.Stop(); FinishHide(); Toggle(); } }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------- bits ----------

    /// <summary>An icon tile beside a block of text; the text gets the rest of the width (so chips wrap).</summary>
    static DockPanel Beside(UIElement icon, UIElement text)
    {
        var dp = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dp.Children.Add(icon);
        dp.Children.Add(text);
        return dp;
    }

    Border IconBox(string key, bool accent = false) => new()
    {
        Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
        Background = accent ? new LinearGradientBrush(A(T.AccentA, .25), A(T.AccentB, .2), 45) : B(A(T.SubText, .14)),
        Child = new TextBlock
        {
            Text = Ui.Icons.Glyph(key) ?? Ui.Icons.Glyph("recipe") ?? "", FontFamily = Icons, FontSize = 13, Foreground = B(accent ? T.AccentA : T.SubText),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    TextBlock Caption(string text, Thickness margin) => new()
    {
        Text = text, FontFamily = TextF, FontSize = 11, Foreground = B(T.SubText), TextWrapping = TextWrapping.Wrap, Margin = margin,
    };

    UIElement Note(string glyph, string text, Color color)
    {
        var sp = new DockPanel { Margin = new Thickness(2, 6, 0, 2) };
        var g = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = B(color), Margin = new Thickness(0, 1, 7, 0) };
        DockPanel.SetDock(g, Dock.Left);
        sp.Children.Add(g);
        sp.Children.Add(new TextBlock { Text = text, FontFamily = TextF, FontSize = 11.5, Foreground = B(color), TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    /// <summary>A pill button: the gradient when it's the current choice.</summary>
    Border Pill(string text, bool on, Action click)
    {
        Brush rest = on ? new LinearGradientBrush(T.AccentA, T.AccentB, 0) : B(A(T.SubText, .12));
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 5), Margin = new Thickness(2), Cursor = Cursors.Hand, Background = rest,
            Child = new TextBlock
            {
                Text = text, FontFamily = TextF, FontSize = 11.5, Foreground = B(on ? T.AccentText : T.Text), FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        if (!on)
        {
            pill.MouseEnter += (_, _) => pill.Background = B(A(T.SubText, .22));
            pill.MouseLeave += (_, _) => pill.Background = rest;
        }
        pill.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return pill;
    }

    /// <summary>A themed text box with a hint while it's empty.</summary>
    (Grid Field, TextBox Box) Field(string text, string hint)
    {
        var box = new TextBox
        {
            Text = text, FontFamily = TextF, FontSize = 12.5, Foreground = B(T.Text), CaretBrush = B(T.Text), SelectionBrush = B(T.AccentA),
            Background = B(A(T.SubText, .10)), BorderBrush = B(A(T.SubText, .3)), BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 4, 7, 5), MaxLength = 60, Template = FieldTemplate(),
        };
        var ghost = new TextBlock
        {
            Text = hint, FontFamily = TextF, FontSize = 12.5, Foreground = B(A(T.SubText, .7)), IsHitTestVisible = false,
            Margin = new Thickness(10, 5, 0, 0), Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) => ghost.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return (new Grid { Children = { box, ghost } }, box);
    }

    static ControlTemplate? _fieldTemplate;

    /// <summary>The box without Windows' own chrome (its blue hover border would clash with every theme).</summary>
    static ControlTemplate FieldTemplate()
    {
        if (_fieldTemplate != null) return _fieldTemplate;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost"));
        return _fieldTemplate = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
    }
}

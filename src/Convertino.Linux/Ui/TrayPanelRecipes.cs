using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Convertino.Ui;

/// <summary>The panel's "Recipes" card (your chains of petals and commands, a small builder) and "Save results", like the Windows one.</summary>
public sealed partial class TrayPanel
{
    // the card stays folded until it's clicked, so the panel keeps its height; the builder is open while there's a draft.
    // Screenshots: CONVERTINO_PANEL_RECIPES=open unfolds it, =builder also opens the builder on a sample chain.
    bool _recipesOpen = Environment.GetEnvironmentVariable("CONVERTINO_PANEL_RECIPES") is "open" or "builder";
    RecipeDraft? _draft = Environment.GetEnvironmentVariable("CONVERTINO_PANEL_RECIPES") == "builder"
        ? new RecipeDraft { Steps = ["fithd", "to-webp"], Name = "Web-ready", Icon = "globe", Picking = true } : null;

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

    static readonly Cursor Hand = new(StandardCursorType.Hand);

    /// <summary>Give this window the keyboard when a text box in it is clicked (X11: the panel is override-redirect, so nothing else will).</summary>
    public Action<Window>? TakeKeyboard { get; set; }

    // ---------- recipes ----------

    Control RecipesCard()
    {
        var book = Recipes.Load();
        var names = book.Recipes.Select(r => r.Name).Concat(book.Commands.Select(c => c.Name)).ToList();
        var body = new StackPanel();

        // the header says what's there; a click folds the card open or shut
        var chevron = new IconView { Key = _recipesOpen ? "up" : "down", Width = 12, Height = 12, Brush = B(T.SubText), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = names.Count == 0 ? L.T("Chain petals into one") : Counted(book), FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = B(T.Text) });
        text.Children.Add(new TextBlock
        {
            Text = names.Count == 0 ? L.T("e.g. {0}", $"Web-ready = {L.T("Full HD")} → WEBP → {L.T("Clean")}") : string.Join(" · ", names),
            FontSize = 11, Foreground = B(T.SubText), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Hand, Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        head.Children.Add(Beside(IconBox("recipe", accent: true), text));
        head.PointerReleased += (_, _) =>
        {
            _recipesOpen = !_recipesOpen;
            if (!_recipesOpen) _draft = null;
            Rebuild();
        };
        body.Children.Add(head);
        if (!_recipesOpen) return Card(body, new Thickness(0));

        if (Recipes.Broken) body.Children.Add(Note("alert", L.T("recipes.json can't be read: fix it, or the next change starts it afresh (the old one is kept)"), T.Danger));
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
                    Rebuild();
                });
                ToolTip.SetTip(pill, string.Join(" → ", Recipes.Describe(t).Steps));
                wrap.Children.Add(pill);
            }
            body.Children.Add(wrap);
        }
        var buttons = new WrapPanel { Margin = new Thickness(-8, 2, 0, 0) };
        buttons.Children.Add(FooterButton("plus", L.T("New recipe…"), () => { _draft = new() { Picking = true }; Rebuild(); }));
        var json = FooterButton("code", "recipes.json", () => { Platform.Current.Open(Recipes.EnsureFile()); HideAnimated(); });
        ToolTip.SetTip(json, L.T("Custom commands and hand edits (it can run programs: only you write it)"));
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

    Control RecipeRow(Recipe r)
    {
        var (steps, problem) = Recipes.Describe(r);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = B(T.Text), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(Chain(steps));
        text.Children.Add(new TextBlock
        {
            Text = problem ?? L.T("for {0}", Recipes.ForText(r.For)) + (r.Out != null ? " · " + L.T("results {0}", Output.Describe(r.Out)) : ""),
            FontSize = 11, Foreground = B(problem != null ? T.Danger : T.SubText), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });
        var main = new Border { Background = Brushes.Transparent, Cursor = Hand, Child = Beside(IconBox(r.Icon ?? "recipe"), text) };
        ToolTip.SetTip(main, L.T("Change it"));
        main.PointerReleased += (_, _) => { _draft = RecipeDraft.Of(r); Rebuild(); };
        return Removable(main, L.T("Delete the recipe"), () => Recipes.Remove(r.Name));
    }

    Control CommandRow(CustomCommand c)
    {
        bool found = Services.Commands.Resolve(c.Program, out var why) != null;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold, FontSize = 12.5, Foreground = B(T.Text), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = L.T("runs {0}", $"{c.Program} {string.Join(' ', c.Args)}"), FontFamily = new FontFamily("Menlo, DejaVu Sans Mono, monospace"), FontSize = 10.5,
            Foreground = B(T.SubText), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = found ? L.T("for {0}", Recipes.ForText(c.For)) : why, FontSize = 11, Foreground = B(found ? T.SubText : T.Danger), Margin = new Thickness(0, 1, 0, 0),
        });
        var main = new Border { Background = Brushes.Transparent, Cursor = Hand, Child = Beside(IconBox(c.Icon ?? "terminal"), text) };
        ToolTip.SetTip(main, L.T("Edit in recipes.json"));
        main.PointerReleased += (_, _) => { Platform.Current.Open(Recipes.EnsureFile()); HideAnimated(); };
        return Removable(main, L.T("Delete the command"), () => Recipes.RemoveCommand(c.Name));
    }

    /// <summary>A row with a ✕ on the right.</summary>
    Control Removable(Control main, string tip, Func<bool> remove)
    {
        var x = IconButton("x", () =>
        {
            try { remove(); } catch (Exception ex) { Log.Error("recipe remove", ex); }
            Rebuild();
        });
        ToolTip.SetTip(x, tip);
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
            chips.Children.Add(new TextBlock { Text = "→", FontSize = 11.5, Foreground = B(T.SubText), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = steps[i], FontWeight = FontWeight.SemiBold, FontSize = 11, Foreground = B(T.Text) });
            var chip = new Border
            {
                Child = label, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 1, 5, 2),
                Background = B(A(T.AccentA, T.IsDark ? .16 : .13)), BorderBrush = B(A(T.AccentA, T.IsDark ? .28 : .22)), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (remove != null)
            {
                label.Children.Add(new IconView { Key = "x", Width = 8, Height = 8, Brush = B(T.SubText), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 1, 0, 0) });
                chip.Cursor = Hand;
                ToolTip.SetTip(chip, at == steps.Count - 1 ? L.T("Remove this step") : L.T("Remove this step and the ones after it"));
                chip.PointerReleased += (_, _) => remove(at);
            }
            chips.Children.Add(chip);
        }
        return chips;
    }

    Control Builder(RecipeDraft d)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(Caption(d.Was == null ? L.T("New recipe, for") : L.T("Change {0}, for", d.Was), new Thickness(2, 6, 0, 2)));
        var kinds = new WrapPanel { Margin = new Thickness(-2, 0, -2, 2) };
        foreach (var (kind, name) in Recipes.BuilderKinds())
            kinds.Children.Add(Pill(char.ToUpperInvariant(name[0]) + name[1..], kind == d.Kind, () =>
            {
                if (d.Kind != kind) { d.Kind = kind; d.For = []; d.Steps.Clear(); d.Error = null; d.Picking = true; }
                Rebuild();
            }));
        sp.Children.Add(kinds);

        // the chain so far, then what can come next
        sp.Children.Add(Caption(L.T("Steps"), new Thickness(2, 6, 0, 0)));
        var next = Recipes.NextSteps(d.Kind, d.Steps);
        var chain = Chain(Recipes.Labels(d.Steps), at => { d.Steps.RemoveRange(at, d.Steps.Count - at); d.Error = null; Rebuild(); });
        if (next.Count > 0 && d.Steps.Count < Recipes.MaxSteps)
            chain.Children.Add(Pill(d.Picking ? L.T("Close") : d.Steps.Count == 0 ? L.T("+ First step") : L.T("+ Add step"), d.Picking, () => { d.Picking = !d.Picking; Rebuild(); }));
        else if (d.Steps.Count > 0)
            chain.Children.Add(Caption(L.T("nothing can follow {0}", Recipes.Labels(d.Steps)[^1]), new Thickness(2, 3, 0, 0)));
        sp.Children.Add(chain);
        if (d.Picking && next.Count > 0)
        {
            var list = new WrapPanel { Margin = new Thickness(-2, 4, -2, 2) };
            foreach (var a in next)
            {
                var pill = Pill(a.Label, false, () => { d.Steps.Add(a.Id); d.Error = null; Rebuild(); });
                ToolTip.SetTip(pill, a.Verb);
                list.Children.Add(pill);
            }
            sp.Children.Add(list);
        }

        // name and icon
        sp.Children.Add(Caption(L.T("Name"), new Thickness(2, 8, 0, 2)));
        var box = Field(d.Name, L.T("e.g. {0}", "Web-ready"));
        box.TextChanged += (_, _) => d.Name = box.Text ?? "";
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveDraft(d); e.Handled = true; } };
        sp.Children.Add(box);
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var key in Recipes.IconChoices)
        {
            bool on = key == d.Icon;
            var b = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 5, 0), Cursor = Hand,
                Background = on ? Diagonal(T.AccentA, T.AccentB) : B(A(T.SubText, .12)),
                Child = new IconView { Key = key, Width = 14, Height = 14, Brush = B(on ? T.AccentText : T.Text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            ToolTip.SetTip(b, key);
            b.PointerReleased += (_, _) => { d.Icon = key; Rebuild(); };
            icons.Children.Add(b);
        }
        sp.Children.Add(icons);
        if (d.Error != null) sp.Children.Add(Note("alert", d.Error, T.Danger));

        var save = Pill(d.Was == null ? L.T("Save recipe") : L.T("Save changes"), true, () => SaveDraft(d));
        save.Padding = new Thickness(14, 5, 14, 6);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 10, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(FooterButton("x", L.T("Cancel"), () => { _draft = null; Rebuild(); }));
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
        Rebuild();
    }

    // ---------- save results ----------

    Control SaveCard()
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
                Rebuild();
            }));
        var body = new StackPanel { Children = { pills } };
        if (mode == "subfolder")
        {
            var box = Field(s.SaveSubfolder, "Converted");
            box.Margin = new Thickness(0, 4, 0, 0);
            box.TextChanged += (_, _) =>
            {
                bool ok = Watches.ValidOut(box.Text) != null;
                box.BorderBrush = B(ok ? A(T.SubText, .3) : T.Danger);
                if (ok) { s.SaveSubfolder = box.Text!.Trim(); s.Save(); }
            };
            body.Children.Add(box);
        }
        body.Children.Add(Caption(mode switch
        {
            "folder" => place,
            "subfolder" => L.T("made inside the original's folder; a recipe can pick its own"),
            _ => L.T("results land next to the original; a recipe can pick its own place"),
        }, new Thickness(2, 4, 0, 4)));
        if (mode == "folder") body.Children.Add(FooterButton("folder", L.T("Change folder…"), PickSaveFolder));
        return Card(body, new Thickness(0));
    }

    async void PickSaveFolder()
    {
        _picking = true;
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Results of every petal go to this folder"), AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                Settings.Current.SaveFolder = path;
                Settings.Current.SaveTo = "folder";
                Settings.Current.Save();
                SettingsChanged?.Invoke();
            }
        }
        catch (Exception ex) { Log.Error("folder picker", ex); }
        finally { _picking = false; }
        if (IsVisible && !_hiding) { Rebuild(); Activate(); }
        else { _hiddenAt = -1e9; Open(); }
    }

    // ---------- bits ----------

    /// <summary>An icon tile beside a block of text; the text gets the rest of the width (so chips wrap).</summary>
    static DockPanel Beside(Control icon, Control text)
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
        Background = accent ? Diagonal(A(T.AccentA, .25), A(T.AccentB, .2)) : B(A(T.SubText, .14)),
        Child = new IconView
        {
            Key = Icons.Get(key) != null ? key : "recipe", Width = 14, Height = 14, Brush = B(accent ? T.AccentA : T.SubText),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    TextBlock Caption(string text, Thickness margin) => new()
    {
        Text = text, FontSize = 11, Foreground = B(T.SubText), TextWrapping = TextWrapping.Wrap, Margin = margin,
    };

    Control Note(string icon, string text, Color color)
    {
        var dp = new DockPanel { Margin = new Thickness(2, 6, 0, 2) };
        var g = new IconView { Key = icon, Width = 12, Height = 12, Brush = B(color), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(g, Dock.Left);
        dp.Children.Add(g);
        dp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = B(color), TextWrapping = TextWrapping.Wrap });
        return dp;
    }

    /// <summary>A pill button: the gradient when it's the current choice.</summary>
    Border Pill(string text, bool on, Action click)
    {
        IBrush rest = on ? Gradient(T.AccentA, T.AccentB, false) : B(A(T.SubText, .12));
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 5), Margin = new Thickness(2), Cursor = Hand, Background = rest,
            Child = new TextBlock
            {
                Text = text, FontSize = 11.5, Foreground = B(on ? T.AccentText : T.Text), FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal,
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        if (!on)
        {
            pill.PointerEntered += (_, _) => pill.Background = B(A(T.SubText, .22));
            pill.PointerExited += (_, _) => pill.Background = rest;
        }
        pill.PointerReleased += (_, e) => { click(); e.Handled = true; };
        return pill;
    }

    /// <summary>A text box in the panel's colours, also while hovered or focused (Fluent would turn it white).</summary>
    TextBox Field(string text, string hint)
    {
        var bg = B(A(T.SubText, .10));
        var border = B(A(T.SubText, .3));
        var box = new TextBox
        {
            Text = text, PlaceholderText = hint, PlaceholderForeground = B(A(T.SubText, .7)), FontSize = 12.5, Foreground = B(T.Text),
            CaretBrush = B(T.Text), SelectionBrush = B(A(T.AccentA, .45)), Background = bg, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 5, 7, 5), MinHeight = 30, MaxLength = 60,
        };
        box.GotFocus += (_, _) => TakeKeyboard?.Invoke(this);
        foreach (var state in new[] { "PointerOver", "Focused" })
        {
            box.Resources[$"TextControlBackground{state}"] = bg;
            box.Resources[$"TextControlForeground{state}"] = B(T.Text);
            box.Resources[$"TextControlBorderBrush{state}"] = state == "Focused" ? B(A(T.AccentA, .8)) : border;
            box.Resources[$"TextControlPlaceholderForeground{state}"] = B(A(T.SubText, .7));
        }
        return box;
    }
}

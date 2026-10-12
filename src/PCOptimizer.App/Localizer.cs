using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>
/// Traduz os textos fixos do XAML quando cada elemento é carregado (inclui os modelos das listas
/// e as janelas secundárias). Só toca em textos que não vêm de bindings; um texto que já está
/// em inglês não tem tradução e fica como está.
/// </summary>
public static class Localizer
{
    public static void Register()
    {
        if (!Loc.IsEnglish) return;
        // o WPF só dispara Loaded nos elementos com handlers próprios: por isso, quando cada janela abre,
        // percorre-se a árvore toda; o handler por elemento apanha o que é criado depois (itens das listas)
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => TranslateTree((DependencyObject)s)), true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
    }

    /// <summary>Traduz um elemento e tudo o que tem dentro (árvore lógica e visual).</summary>
    public static void TranslateTree(DependencyObject root)
    {
        if (!Loc.IsEnglish) return;
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (!seen.Add(d)) continue;
            OnLoaded(d, null!);
            if (d is ItemsControl ic) WatchItems(ic);
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) stack.Push(child);
            if (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }
    }

    /// <summary>Os separadores escondidos podem nunca chegar a ser "carregados": traduz já o título e o subtítulo.</summary>
    public static void TranslateTabs(TabControl tabs)
    {
        if (!Loc.IsEnglish) return;
        foreach (var ti in tabs.Items.OfType<TabItem>())
        {
            if (ti.Header is string h && Loc.TryTranslate(h) is string ht) ti.Header = ht;
            if (ti.Tag is string t && Loc.TryTranslate(t) is string tt) ti.Tag = tt;
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemsControl, object> Watched = new();

    /// <summary>Os itens das listas são criados depois (e de novo quando a lista muda): traduz quando aparecem.</summary>
    static void WatchItems(ItemsControl ic)
    {
        if (Watched.TryGetValue(ic, out _)) return;
        Watched.Add(ic, new object());
        ic.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ic.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
            // espera que os modelos dos itens estejam desenhados
            ic.Dispatcher.BeginInvoke(() => TranslateTree(ic), System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        // traduzir nunca pode rebentar a app: no pior caso o texto fica em português
        try { Translate(sender); } catch (Exception ex) { App.Log(ex); }
    }

    static void Translate(object sender)
    {
        switch (sender)
        {
            case TextBlock tb:
                if (tb.GetBindingExpression(TextBlock.TextProperty) != null) break;
                if (tb.Inlines.Count > 1)
                {
                    foreach (var run in tb.Inlines.OfType<Run>().ToList()) // ToList: mudar o texto altera a coleção
                        if (run.GetBindingExpression(Run.TextProperty) is null && Loc.TryTranslate(run.Text) is string r) run.Text = r;
                }
                else if (Loc.TryTranslate(tb.Text) is string t) tb.Text = t;
                break;
            case HeaderedContentControl hc:
                if (hc.Header is string h && Loc.TryTranslate(h) is string ht) hc.Header = ht;
                if (hc is TabItem { Tag: string tag } ti && Loc.TryTranslate(tag) is string tt) ti.Tag = tt;
                if (hc.Content is string hcs && hc.GetBindingExpression(ContentControl.ContentProperty) is null && Loc.TryTranslate(hcs) is string hct) hc.Content = hct;
                break;
            case ContentControl cc:
                if (cc.Content is string c && cc.GetBindingExpression(ContentControl.ContentProperty) is null && Loc.TryTranslate(c) is string ct) cc.Content = ct;
                if (cc is Window w && Loc.TryTranslate(w.Title) is string wt) w.Title = wt;
                break;
        }
        if (sender is FrameworkElement fe && fe.ToolTip is string tip && Loc.TryTranslate(tip) is string tipt) fe.ToolTip = tipt;
    }
}

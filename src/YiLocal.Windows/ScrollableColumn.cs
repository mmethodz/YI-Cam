namespace YiLocal.Windows;

/// <summary>The scroll extent belongs to one autosized child, including its bottom padding.</summary>
internal sealed class ScrollableColumn : Panel
{
    readonly FlowLayoutPanel content;
    bool arranging;
    public ScrollableColumn(FlowLayoutPanel content)
    {
        this.content = content;
        Dock = DockStyle.Fill; AutoScroll = true; Margin = Padding.Empty;
        content.Dock = DockStyle.Top;
        content.AutoSize = true; content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.WrapContents = false; content.AutoScroll = false;
        content.Padding = new Padding(8, 6, 8, 20);
        Controls.Add(content);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (arranging) return;
        arranging = true;
        try
        {
            // Reserve scrollbar width before it appears, preventing a width/height feedback loop.
            if (content is not null) content.MaximumSize = new Size(Math.Max(1, ClientSize.Width - SystemInformation.VerticalScrollBarWidth), 0);
            base.OnLayout(e);
        }
        finally { arranging = false; }
    }
}

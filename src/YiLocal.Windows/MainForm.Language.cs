namespace YiLocal.Windows;

public sealed partial class MainForm
{
    void BuildLanguage()
    {
        var body = Column(); Page(L.Get("Language.Settings")).Controls.Add(new ScrollableColumn(body));
        body.Controls.Add(Label(L.Get("Language.Title")));
        var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, AccessibleName = L.Get("Language.Title") };
        foreach (var language in L.Languages) choice.Items.Add(language);
        choice.SelectedItem = L.Languages.Single(l => l.Code == L.ResolveLanguage(preferences.Language));
        body.Controls.Add(choice);
        var message = Label(L.Format("Language.Active", L.Languages.Single(l => l.Code == L.Culture.Name).Name));
        body.Controls.Add(message);
        body.Controls.Add(Label(L.Get("Language.Restart")));
        body.Controls.Add(Label(L.Get("Language.Help")));
        body.Controls.Add(Label(L.Format("Language.Location", Preferences.FilePath)));
        choice.SelectionChangeCommitted += (_, _) => _ = Guard(() =>
        {
            var next = preferences.Copy(); next.Language = ((AppLanguage)choice.SelectedItem!).Code;
            try { next.Save(); }
            catch { choice.SelectedItem = L.Languages.Single(l => l.Code == L.ResolveLanguage(preferences.Language)); throw; }
            preferences = next; // Preserve other saved sections and any pending storage/capture edits.
            message.Text = next.Language == L.Culture.Name
                ? L.Format("Language.Active", ((AppLanguage)choice.SelectedItem!).Name) : L.Get("Language.Saved");
            return Task.CompletedTask;
        });
    }
}

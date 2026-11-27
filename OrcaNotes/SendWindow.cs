using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrcaNotes.Resources;

namespace OrcaNotes
{
    internal sealed class SendWindow : Window
    {
        private readonly OrcaClient client;
        private readonly ReviewNote singleNote;
        private readonly NoteStore store = NoteStore.Shared;
        private readonly StackPanel rows = new StackPanel();
        private readonly ComboBox targets = new ComboBox { MinHeight = 28, Margin = new Thickness(0, 5, 0, 10) };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
        private readonly Button send = new Button { Content = Strings.Send, IsEnabled = false, Padding = new Thickness(18,4,18,4) };
        private readonly List<CheckBox> checks = new List<CheckBox>();
        private readonly TextBlock singleNoteHeading;
        private bool notesReady, sending, deliveryStarted, closed;

        internal SendWindow(OrcaClient client, ReviewNote singleNote)
        {
            this.singleNote = singleNote;
            this.client = client;
            Title = singleNote == null ? Strings.SendWorkspace : Strings.SendNoteWindow;
            Width = 620; Height = 475; MinWidth = 410; MinHeight = 290;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var pane = new DockPanel { Margin = new Thickness(16) };
            Content = pane;
            var header = new StackPanel();
            DockPanel.SetDock(header, Dock.Top);
            pane.Children.Add(header);
            header.Children.Add(new TextBlock { Text = Strings.Recipient, FontWeight = FontWeights.SemiBold });
            header.Children.Add(targets);
            header.Children.Add(new TextBlock { Text = Strings.NotesToSend, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,2,0,5) });
            var footer = new StackPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            pane.Children.Add(footer);
            footer.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footer.Children.Add(buttons);
            var close = new Button { Content = Strings.Close, Padding = new Thickness(12,4,12,4), Margin = new Thickness(0,0,8,0), IsCancel = true };
            buttons.Children.Add(close); buttons.Children.Add(send);
            pane.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (singleNote != null)
            {
                singleNoteHeading = AddRow(singleNote);
                notesReady = true;
            }
            status.Text = Strings.Loading;
            targets.SelectionChanged += (s,e) => UpdateButton();
            send.Click += (s, e) => _ = SendClickedAsync();
            Closing += (s, e) => e.Cancel = sending;
            Closed += (s, e) => closed = true;
            Loaded += (s, e) => _ = WindowLoadedAsync();
        }

        private async Task SendClickedAsync()
        {
            try { await SendAsync(); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { sending = false; targets.IsEnabled = true; rows.IsEnabled = true; UpdateButton(); }
        }

        private async Task WindowLoadedAsync()
        {
            try
            {
                await Task.WhenAll(LoadNotesAsync(), LoadDestinationsAsync());
                if (closed || deliveryStarted) return;
                status.Text = checks.Count == 0 ? Strings.NoNotes :
                    targets.Items.Count == 0 ? Strings.NoTargets :
                    targets.SelectedItem == null ? Strings.SelectTarget : string.Format(Strings.WorkspacePath, client.Root);
            }
            catch (Exception ex) { if (!closed && !deliveryStarted) status.Text = ex.Message; }
        }

        private async Task LoadNotesAsync()
        {
            await client.LocateAsync();
            if (closed) return;
            if (singleNote != null) singleNoteHeading.Text = RowHeading(singleNote);
            else await LoadWorkspaceNotesAsync();
            notesReady = true;
            UpdateButton();
        }

        private async Task LoadDestinationsAsync()
        {
            var existing = await client.PreviewTargetsAsync();
            if (closed) return;
            foreach (var target in existing) targets.Items.Add(target);
            if (existing.Count != 0) status.Text = string.Format(Strings.WorkspacePath, client.Root);
            await Task.WhenAll(SelectPreferredAsync(existing), AppendNewSessionsAsync(existing.Count == 0));
        }

        private async Task SelectPreferredAsync(List<Destination> existing)
        {
            if (existing.Count == 0) return;
            var preferred = await client.PreferredAsync(existing);
            if (!closed && targets.SelectedItem == null) targets.SelectedItem = preferred;
        }

        private async Task AppendNewSessionsAsync(bool selectFirst)
        {
            var available = await client.NewSessionTargetsAsync();
            if (closed) return;
            foreach (var target in available) targets.Items.Add(target);
            if (selectFirst && targets.SelectedItem == null) targets.SelectedItem = available.FirstOrDefault();
        }

        private async Task LoadWorkspaceNotesAsync()
        {
            var notes = store.All().Where(n => n.State != NoteStore.Sent && OrcaClient.ContainsPath(client.Root, n.File));
            foreach (var group in notes.GroupBy(n => Path.GetDirectoryName(n.File), StringComparer.OrdinalIgnoreCase))
            {
                if (closed) return;
                // Nested Orca workspaces can share a path prefix. Verify the owning workspace before including a file.
                if (!string.Equals(group.Key, client.DirectoryPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(group.Key)) continue;
                    var owner = new OrcaClient(group.Key);
                    await owner.LocateAsync();
                    if (closed || owner.Id != client.Id) continue;
                }
                foreach (var note in group) AddRow(note);
            }
        }

        private string RowHeading(ReviewNote note)
        {
            string range = note.Line == note.LastLine ? note.Line.ToString() : note.Line + "–" + note.LastLine;
            string path = client.Root == null ? Path.GetFileName(note.File) : client.Relative(note.File);
            return string.Format(note.Line == note.LastLine ? Strings.RowHeading : Strings.RowRangeHeading, path, range);
        }

        private TextBlock AddRow(ReviewNote note)
        {
            var body = new StackPanel();
            var heading = new TextBlock { Text = RowHeading(note), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = note.Comment, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = note.Code, FontFamily = new FontFamily("Consolas"), MaxHeight = 50,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = note.Code, Opacity = .72 });
            if (note.State == NoteStore.Checking)
                body.Children.Add(new TextBlock { Text = Strings.CheckBeforeResending, TextWrapping = TextWrapping.Wrap });
            var box = new CheckBox { Content = body, Tag = note, IsChecked = note.State == NoteStore.Pending,
                Margin = new Thickness(2,5,8,6), VerticalContentAlignment = VerticalAlignment.Top };
            box.Checked += (s,e) => UpdateButton();
            box.Unchecked += (s,e) => UpdateButton();
            rows.Children.Add(box); checks.Add(box);
            return heading;
        }

        private void UpdateButton()
        {
            int count = checks.Count(c => c.IsChecked == true);
            send.Content = count == 1 ? Strings.SendOne : count > 1 ? string.Format(Strings.SendCount, count) : Strings.Send;
            send.IsEnabled = notesReady && !sending && count > 0 && targets.SelectedItem != null;
        }

        private async Task SendAsync()
        {
            var target = (Destination)targets.SelectedItem;
            var selected = checks.Where(c => c.IsChecked == true).ToList();
            var notes = selected.Select(c => (ReviewNote)c.Tag).ToList();
            string prompt = client.Format(notes);
            deliveryStarted = true;
            sending = true; send.IsEnabled = false; targets.IsEnabled = false; rows.IsEnabled = false;
            status.Text = Strings.Sending;
            var sender = await client.PrepareAsync(target, prompt);
            var pending = notes.Where(n => n.State == NoteStore.Pending).ToList();
            store.SetState(notes, NoteStore.Checking);
            string receipt;
            try
            {
                receipt = await client.SendAsync(target, sender, prompt);
            }
            catch (TerminalInputRefusedException)
            {
                if (pending.Count != 0) store.SetState(pending, NoteStore.Pending);
                throw;
            }
            catch (Exception ex)
            {
                foreach (var box in selected) { checks.Remove(box); rows.Children.Remove(box); AddRow((ReviewNote)box.Tag); }
                throw new IOException(Strings.CheckDelivery + Environment.NewLine + ex.Message, ex);
            }

            foreach (var box in selected) { checks.Remove(box); rows.Children.Remove(box); }
            try { store.RemoveDelivered(notes); }
            catch (Exception ex) { throw new IOException(string.Format(Strings.CleanupError, receipt, ex.Message), ex); }
            status.Text = receipt;
            if (singleNote != null) { sending = false; DialogResult = true; }
        }
    }
}

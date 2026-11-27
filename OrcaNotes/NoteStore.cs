using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using OrcaNotes.Resources;

namespace OrcaNotes
{
    public sealed class ReviewNote
    {
        public string Id { get; set; }
        public string Revision { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int LastLine { get; set; }
        public int Column { get; set; }
        public string Code { get; set; }
        public string Comment { get; set; }
        public string State { get; set; }
        public ReviewNote Copy() => (ReviewNote)MemberwiseClone();
    }

    internal sealed class NoteStore
    {
        internal const string Pending = "pending", Checking = "checking", Sent = "sent";
        internal static readonly NoteStore Shared = new NoteStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsOrcaNotes", "notes"));
        private readonly string directory;
        internal event Action Changed;

        internal NoteStore(string directory) { this.directory = directory; }

        private static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private string FileFor(string id) => Path.Combine(directory, Guid.Parse(id).ToString("N") + ".json");
        private ReviewNote Read(string path) => Serializer().Deserialize<ReviewNote>(System.IO.File.ReadAllText(path));

        internal List<ReviewNote> All()
        {
            var notes = new List<ReviewNote>();
            Locked(() =>
            {
                if (Directory.Exists(directory)) notes.AddRange(Directory.GetFiles(directory, "*.json").Select(Read));
            });
            return notes.OrderBy(n => n.File, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Line).ToList();
        }

        internal void Save(ReviewNote draft, string expectedRevision)
        {
            Locked(() => { RequireRevision(draft, expectedRevision); Write(draft); });
            Changed?.Invoke();
        }

        internal void Delete(ReviewNote note)
        {
            Locked(() => { RequireRevision(note, note.Revision); System.IO.File.Delete(FileFor(note.Id)); });
            Changed?.Invoke();
        }

        internal void RemoveDelivered(IList<ReviewNote> notes)
        {
            var failures = new List<Exception>();
            Locked(() =>
            {
                foreach (var note in notes)
                {
                    try
                    {
                        var path = FileFor(note.Id);
                        // An edit saved while Orca was sending belongs to the next review.
                        if (!System.IO.File.Exists(path) || Read(path).Revision != note.Revision) continue;
                        note.State = Sent;
                        Write(note);
                        System.IO.File.Delete(path);
                    }
                    catch (IOException ex) { failures.Add(ex); }
                    catch (UnauthorizedAccessException ex) { failures.Add(ex); }
                }
            });
            Changed?.Invoke();
            if (failures.Count != 0) throw new IOException(failures[0].Message, failures[0]);
        }

        // Claim the exact reviewed versions before starting Orca. An interrupted send stays visible for review.
        internal void SetState(IList<ReviewNote> notes, string state)
        {
            Locked(() =>
            {
                foreach (var note in notes) RequireRevision(note, note.Revision);
                foreach (var note in notes) { note.State = state; Write(note); }
            });
            Changed?.Invoke();
        }

        private void RequireRevision(ReviewNote note, string expected)
        {
            var path = FileFor(note.Id);
            if ((System.IO.File.Exists(path) ? Read(path).Revision : null) != expected)
                throw new InvalidOperationException(Strings.NoteChanged);
        }

        private void Write(ReviewNote note)
        {
            Directory.CreateDirectory(directory);
            string path = FileFor(note.Id);
            string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            note.Revision = Guid.NewGuid().ToString("N");
            try
            {
                System.IO.File.WriteAllText(temp, Serializer().Serialize(note), new UTF8Encoding(false));
                if (System.IO.File.Exists(path)) System.IO.File.Replace(temp, path, null);
                else System.IO.File.Move(temp, path);
            }
            finally { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); }
        }

        private static void Locked(Action action)
        {
            using (var mutex = new Mutex(false, @"Local\VsOrcaNotes.NoteStore.36312d9d"))
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException(Strings.StoreBusy);
                }
                catch (AbandonedMutexException) { }
                try { action(); } finally { mutex.ReleaseMutex(); }
            }
        }
    }
}

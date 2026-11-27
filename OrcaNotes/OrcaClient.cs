using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using JsonObject = System.Collections.Generic.Dictionary<string, object>;
using OrcaNotes.Resources;

namespace OrcaNotes
{
    internal static class JsonFields
    {
        public static object Field(this JsonObject obj, string key) =>
            obj != null && obj.TryGetValue(key, out var value) ? value : null;
        public static string Text(this JsonObject obj, string key) => obj.Field(key) as string;
        public static JsonObject Child(this JsonObject obj, string key) => obj.Field(key) as JsonObject;
        public static IEnumerable<JsonObject> Rows(this JsonObject obj, string key) =>
            (obj.Field(key) as object[] ?? Array.Empty<object>()).OfType<JsonObject>();
    }

    internal enum DeliveryRoute { Terminal, ChatMailbox, NewSession }

    internal sealed class TerminalInputRefusedException : IOException
    {
        internal TerminalInputRefusedException(string reason)
            : base(reason == "permission" || reason == "approval-unseen"
                ? Strings.TerminalNeedsApproval
                : Strings.TerminalRefused) { }
    }

    internal sealed class Destination
    {
        internal string Address, Display, Session, Incarnation;
        internal DeliveryRoute Route;
        public override string ToString() => Display;
    }

    internal sealed class OrcaClient
    {
        // Native Chat mail needs a sender owned by this VS instance.
        private static readonly Dictionary<string, string> SenderHandles = new Dictionary<string, string>();
        private readonly string cwd;
        private Task location;
        private Task<List<Destination>> preview;
        private Stopwatch previewAge;
        internal string DirectoryPath => cwd;
        internal string Root { get; private set; }
        internal string Id { get; private set; }
        private string Selector => "id:" + Id;
        internal OrcaClient(string directory) { cwd = directory; }

        // The note editor can start this on save and hand the in-flight read to the next dialog.
        internal bool HasRecentPreview(string directory) => preview != null && !preview.IsFaulted &&
            previewAge.Elapsed < TimeSpan.FromSeconds(15) && string.Equals(cwd, directory, StringComparison.OrdinalIgnoreCase);

        internal Task<List<Destination>> PreviewTargetsAsync() => preview ?? ExistingTargetsAsync();

        internal async Task PreloadAsync()
        {
            previewAge = Stopwatch.StartNew();
            preview = ExistingTargetsAsync();
            try { await preview; }
            catch (Exception) { /* Opening the dialog retries a failed speculative read. */ }
        }

        internal Task LocateAsync() => location ?? (location = ReadLocationAsync());

        private async Task ReadLocationAsync()
        {
            var tree = (await RunAsync(null, "worktree", "current")).Child("worktree");
            Root = tree.Text("path");
            Id = tree.Text("id");
            if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(Root) || !ContainsPath(Root, cwd))
                throw new InvalidOperationException(Strings.OpenWorkspaceInOrca);
        }

        internal static bool ContainsPath(string root, string path)
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/');
            var full = Path.GetFullPath(path);
            return full.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal string Relative(string path) => Path.GetFullPath(path)
            .Substring(Path.GetFullPath(Root).TrimEnd('\\', '/').Length).TrimStart('\\','/').Replace('\\','/');

        internal async Task<List<Destination>> ExistingTargetsAsync()
        {
            // "current" resolves from the CLI process's working directory. Check every row against
            // the independently resolved worktree ID before exposing a destination.
            var selector = Id == null ? "current" : Selector;
            var workspace = LocateAsync();
            var terminalTask = RunAsync(null, "terminal", "list", "--worktree", selector);
            var agentTask = RunAsync(null, "agent", "list", "--worktree", selector);
            await Task.WhenAll(workspace, terminalTask, agentTask);
            var terminals = (await terminalTask).Rows("terminals")
                .Where(t => t.Text("worktreeId") == Id && Equals(t.Field("connected"), true)
                    && Equals(t.Field("writable"), true) && !string.IsNullOrEmpty(t.Text("agentIdentity")))
                .ToLookup(t => t.Text("tabId") + ":" + t.Text("leafId"));
            var targets = new List<Destination>();
            foreach (var a in (await agentTask).Rows("agents").Where(a => a.Text("worktreeId") == Id))
            {
                string session = a.Text("orcaSessionId");
                string title = a.Text("title") ?? a.Text("agent");
                if (a.Text("kind") == "chat" && !string.IsNullOrEmpty(session))
                    targets.Add(new Destination { Route = DeliveryRoute.ChatMailbox,
                        Address = session.StartsWith("orca_session_id:", StringComparison.Ordinal) ? session : "orca_session_id:" + session,
                        Session = session, Display = string.Format(Strings.ChatTarget, title) });
                if (a.Text("kind") != "terminal") continue;
                foreach (var t in terminals[a.Text("paneKey")].Where(t =>
                    t.Text("agentIdentity") == a.Text("agent") && !string.IsNullOrEmpty(t.Text("handle"))))
                    targets.Add(new Destination { Route = DeliveryRoute.Terminal, Address = t.Text("handle"),
                        Incarnation = t.Text("incarnationId"), Session = session, Display = string.Format(Strings.TerminalTarget, a.Text("agent"), title) });
            }
            return targets;
        }

        internal async Task<List<Destination>> NewSessionTargetsAsync()
        {
            await LocateAsync();
            var installed = await RunAsync(null, "agent", "installed", "--worktree", Selector);
            return installed.Rows("agents").Where(a => !string.IsNullOrEmpty(a.Text("id")))
                .Select(a => new Destination { Address = a.Text("id"), Route = DeliveryRoute.NewSession,
                    Display = string.Format(Strings.NewSessionTarget, a.Text("name") ?? a.Text("id")) }).ToList();
        }

        internal async Task<Destination> PreferredAsync(List<Destination> destinations)
        {
            var existing = destinations.Where(d => d.Route != DeliveryRoute.NewSession).ToList();
            if (existing.Count == 0) return destinations.FirstOrDefault();
            if (existing.Count == 1) return existing[0];
            try
            {
                // Orca resolves the active terminal from cwd. A window from another workspace is never preselected.
                var active = (await RunAsync(null, "terminal", "show")).Child("terminal");
                if (active.Text("worktreeId") == Id)
                    return existing.FirstOrDefault(d => d.Route == DeliveryRoute.Terminal && d.Address == active.Text("handle") &&
                        d.Incarnation == active.Text("incarnationId"));
            }
            catch (InvalidOperationException) { /* No active terminal. Leave the selector for the user. */ }
            return null;
        }

        internal async Task<string> PrepareAsync(Destination target, string prompt)
        {
            if (target.Route != DeliveryRoute.ChatMailbox)
            {
                // Validate the actual command before marking the notes as in flight.
                BuildStartInfo(PromptArguments(target, prompt));
            }
            var candidates = target.Route == DeliveryRoute.NewSession ? await NewSessionTargetsAsync() : await ExistingTargetsAsync();
            if (!candidates.Any(t => t.Route == target.Route && t.Address == target.Address &&
                t.Session == target.Session && t.Incarnation == target.Incarnation))
                throw new InvalidOperationException(Strings.SessionChanged);
            if (target.Route != DeliveryRoute.ChatMailbox) return null;
            if (SenderHandles.TryGetValue(Id, out var handle))
            {
                var list = await RunAsync(null, "terminal", "list", "--worktree", Selector);
                if (list.Rows("terminals").Any(t => t.Text("handle") == handle && t.Text("worktreeId") == Id &&
                    Equals(t.Field("connected"), true) && string.IsNullOrEmpty(t.Text("agentIdentity")))) return handle;
            }
            var created = await RunAsync(null, "terminal", "create", "--worktree", Selector, "--title", "VS Orca Notes");
            handle = created.Child("terminal").Text("handle");
            if (string.IsNullOrEmpty(handle)) throw new InvalidOperationException(Strings.NoSender);
            SenderHandles[Id] = handle;
            return handle;
        }

        internal async Task<string> SendAsync(Destination target, string sender, string prompt)
        {
            if (target.Route == DeliveryRoute.Terminal)
            {
                var send = (await RunAsync(null, PromptArguments(target, prompt))).Child("send");
                return TerminalReceipt(send, target.Address);
            }
            if (target.Route == DeliveryRoute.NewSession)
            {
                var launch = (await RunAsync(null, PromptArguments(target, prompt))).Child("launch");
                var outcome = launch.Child("prompt").Text("outcome");
                if (outcome != "journaled" && outcome != "handed-to-terminal")
                    throw new IOException(Strings.CheckNewSession);
                return Strings.SentToNewSession;
            }
            var result = await RunAsync(prompt, "orchestration", "send", "--from", sender, "--to", target.Address,
                "--subject", "Visual Studio 代码备注", "--body-file", "-", "--type", "handoff", "--full");
            var id = result.Child("message").Text("id") ?? result.Text("messageId");
            if (string.IsNullOrEmpty(id)) throw new IOException(Strings.NoMessageId);
            return Strings.SentToInbox;
        }

        private string[] PromptArguments(Destination target, string prompt)
        {
            if (target.Route == DeliveryRoute.Terminal)
                return new[] { "terminal", "send", "--terminal", target.Address, "--text", prompt, "--enter", "--wait-submit", "3" };
            return new[] { "agent", "launch", "--worktree", Selector, "--agent", target.Address, "--prompt", prompt };
        }

        internal static string TerminalReceipt(JsonObject send, string handle)
        {
            if (send.Text("handle") == handle && Equals(send.Field("accepted"), false) && Equals(send.Field("bytesWritten"), 0))
                throw new TerminalInputRefusedException(send.Text("refusedReason"));
            if (send.Text("handle") != handle || !Equals(send.Field("accepted"), true))
                throw new IOException(Strings.NoTerminalReceipt);
            var prompt = send.Child("prompt");
            var submit = prompt.Child("submit");
            if (submit.Text("withheldFor") != null)
                throw new IOException(Strings.SubmitWithheld);
            if (prompt.Text("observation") == "incarnation_replaced")
                throw new IOException(Strings.TerminalReplaced);
            if ((prompt.Field("stages") as object[] ?? Array.Empty<object>()).Contains("turn_started"))
                return Strings.SentTurnStarted;
            if (submit.Field("enteredAt") != null)
                return Strings.SentAwaitingTurn;
            throw new IOException(Strings.EnterUnconfirmed);
        }

        internal string Format(IEnumerable<ReviewNote> notes)
        {
            var text = new StringBuilder();
            foreach (var note in notes)
            {
                if (text.Length > 0) text.Append("\n\n");
                text.Append("File: ").Append(Relative(note.File));
                text.Append("\nLine: ").Append(note.Line);
                if (note.LastLine > note.Line) text.Append('-').Append(note.LastLine);
                var comment = (note.Comment ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");
                text.Append("\nUser comment: \"").Append(comment).Append('"');
            }
            return text.ToString();
        }

        // Quote one argv value for Windows' CommandLineToArgvW rules, including a final backslash.
        internal static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static string Executable()
        {
            string install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "orca", "resources", "bin");
            foreach (var dir in new[] { install }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')))
            {
                string folder = dir.Trim().Trim('"');
                if (!Path.IsPathRooted(folder)) continue;
                string file = Path.Combine(folder, "orca.exe");
                if (File.Exists(file)) return file;
            }
            throw new FileNotFoundException(Strings.CliMissing);
        }

        private ProcessStartInfo BuildStartInfo(string[] args)
        {
            if (args.Any(arg => arg.IndexOf('\0') >= 0))
                throw new ArgumentException(Strings.NullCharacter);
            var info = new ProcessStartInfo(Executable(), string.Join(" ", args.Concat(new[] { "--environment", "local", "--json" }).Select(Quote)))
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            if (info.Arguments.Length + info.FileName.Length + 4 > 32767)
                throw new ArgumentException(Strings.PromptTooLong);
            // VS may have inherited the identity of the terminal that launched it.
            foreach (var key in info.EnvironmentVariables.Keys.Cast<string>().Where(k =>
                k.StartsWith("ORCA_", StringComparison.OrdinalIgnoreCase) || k == "CODEX_THREAD_ID").ToList())
                info.EnvironmentVariables.Remove(key);
            return info;
        }

        private Task<JsonObject> RunAsync(string input, params string[] args) => Task.Run(() =>
        {
            var info = BuildStartInfo(args);
            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var stdin = process.StandardInput.BaseStream;
                var write = Task.Run(() =>
                {
                    using (stdin)
                    {
                        if (input == null) return;
                        var bytes = Encoding.UTF8.GetBytes(input);
                        stdin.Write(bytes, 0, bytes.Length);
                    }
                });
                if (!process.WaitForExit(45000))
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException(Strings.CliTimeout);
                }
                var stdout = output.GetAwaiter().GetResult();
                var stderr = error.GetAwaiter().GetResult();
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var envelope = string.IsNullOrWhiteSpace(stdout) ? null : serializer.DeserializeObject(stdout) as JsonObject;
                var result = envelope.Child("result");
                // Orca exits with 1 on an explicit input refusal while returning a valid terminal receipt.
                bool refused = args[0] == "terminal" && args[1] == "send" && Equals(result.Child("send").Field("accepted"), false);
                if ((process.ExitCode != 0 && !refused) || !Equals(envelope.Field("ok"), true))
                    throw new InvalidOperationException(envelope.Child("error").Text("message") ??
                        (!string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : Strings.CliFailed));
                write.GetAwaiter().GetResult();
                return result ?? throw new IOException(Strings.InvalidResponse);
            }
        });
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ArniLite.Core;
using Newtonsoft.Json;

namespace ArniLite.Storage
{
    public sealed class JsonlJournal : ISideEffectJournal
    {
        private readonly string _path;
        private readonly object _gate = new object();

        public JsonlJournal(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public static JsonlJournal InDataDirectory()
        {
            return new JsonlJournal(Path.Combine(AppPaths.DataDirectory, "side-effects.jsonl"));
        }

        public SideEffectEntry Latest(string key)
        {
            SideEffectEntry latest = null;
            foreach (var entry in Read())
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    latest = entry;
                }
            }

            return latest;
        }

        public void Append(SideEffectEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonConvert.SerializeObject(entry) + "\n";
            lock (_gate)
            {
                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(line);
                    writer.Flush();
                    stream.Flush(true);
                }
            }
        }

        public void MarkExecutingAsUnknown()
        {
            var open = new Dictionary<string, SideEffectEntry>(StringComparer.Ordinal);
            foreach (var entry in Read())
            {
                if (string.IsNullOrEmpty(entry.Key))
                {
                    continue;
                }

                open[entry.Key] = entry;
            }

            foreach (var pair in open)
            {
                if (pair.Value.State != "prepared" && pair.Value.State != "executing")
                {
                    continue;
                }

                Append(new SideEffectEntry
                {
                    Key = pair.Key,
                    ArgumentsHash = pair.Value.ArgumentsHash,
                    State = "outcome_unknown",
                    ResultJson = SideEffectGuard.Result("outcome_unknown", "The app restarted while this action was unfinished. It was not run again."),
                });
            }
        }

        private IList<SideEffectEntry> Read()
        {
            var entries = new List<SideEffectEntry>();
            if (!File.Exists(_path))
            {
                return entries;
            }

            string[] lines;
            lock (_gate)
            {
                lines = File.ReadAllLines(_path);
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                try
                {
                    var entry = JsonConvert.DeserializeObject<SideEffectEntry>(lines[i]);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return entries;
        }
    }

    public sealed class ChatHistory
    {
        private readonly string _directory;

        public ChatHistory(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Directory.CreateDirectory(_directory);
        }

        public static ChatHistory InDataDirectory()
        {
            return new ChatHistory(Path.Combine(AppPaths.DataDirectory, "chats"));
        }

        public string CurrentChatId()
        {
            var pointer = Path.Combine(_directory, "current.txt");
            if (File.Exists(pointer))
            {
                var id = File.ReadAllText(pointer).Trim();
                if (id.Length > 0 && File.Exists(Path.Combine(_directory, id + ".jsonl")))
                {
                    return id;
                }
            }

            return NewChat();
        }

        public string NewChat()
        {
            var id = Guid.NewGuid().ToString("D");
            File.WriteAllText(Path.Combine(_directory, "current.txt"), id);
            return id;
        }

        public string ChatPath(string chatId)
        {
            return Path.Combine(_directory, chatId + ".jsonl");
        }

        public IList<ChatSummary> List()
        {
            var summaries = new List<ChatSummary>();
            if (!Directory.Exists(_directory))
            {
                return summaries;
            }

            foreach (var file in Directory.GetFiles(_directory, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var title = "Новый диалог";
                foreach (var message in Load(id))
                {
                    if (message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content))
                    {
                        title = message.Content.Replace("\r", " ").Replace("\n", " ").Trim();
                        if (title.Length > 48)
                        {
                            title = title.Substring(0, 48) + "…";
                        }

                        break;
                    }
                }

                summaries.Add(new ChatSummary { Id = id, Title = title, Updated = File.GetLastWriteTime(file) });
            }

            summaries.Sort((left, right) => right.Updated.CompareTo(left.Updated));
            return summaries;
        }

        public IList<ChatMessage> Load(string chatId)
        {
            var messages = new List<ChatMessage>();
            var path = ChatPath(chatId);
            if (!File.Exists(path))
            {
                return messages;
            }

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                try
                {
                    var message = JsonConvert.DeserializeObject<ChatMessage>(lines[i]);
                    if (message != null && !string.IsNullOrEmpty(message.Role))
                    {
                        messages.Add(message);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return messages;
        }

        public void Append(string chatId, ChatMessage message)
        {
            var path = ChatPath(chatId);
            var line = JsonConvert.SerializeObject(message) + "\n";
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(line);
                writer.Flush();
                stream.Flush(true);
            }
        }

        public void Rewrite(string chatId, IList<ChatMessage> messages)
        {
            var path = ChatPath(chatId);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                for (var i = 0; i < messages.Count; i++)
                {
                    writer.Write(JsonConvert.SerializeObject(messages[i]));
                    writer.Write('\n');
                }

                writer.Flush();
                stream.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temporary, path);
        }
    }

    public sealed class ChatSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime Updated { get; set; }
    }
}

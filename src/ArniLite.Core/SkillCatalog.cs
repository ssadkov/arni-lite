using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArniLite.Core
{
    public sealed class SkillSummary
    {
        public SkillSummary(string name, string description, bool fromProject)
        {
            Name = name ?? string.Empty;
            Description = description ?? string.Empty;
            FromProject = fromProject;
        }

        public string Name { get; }
        public string Description { get; }
        public bool FromProject { get; }
    }

    public static class SkillText
    {
        public static bool IsName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 40 || name[0] == '-')
            {
                return false;
            }

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok || (c == '-' && (i == 0 || name[i - 1] == '-')))
                {
                    return false;
                }
            }

            return name[name.Length - 1] != '-';
        }

        public static bool TryParse(string text, out string name, out string description, out string body)
        {
            name = string.Empty;
            description = string.Empty;
            body = string.Empty;
            text = (text ?? string.Empty).Replace("\r\n", "\n");
            if (!text.StartsWith("---\n", StringComparison.Ordinal))
            {
                return false;
            }

            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            var header = text.Substring(4, end - 4);
            var rest = end + 4;
            if (rest < text.Length && text[rest] == '\n')
            {
                rest++;
            }

            foreach (var line in header.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("name:", StringComparison.Ordinal))
                {
                    name = Unquote(trimmed.Substring(5));
                }
                else if (trimmed.StartsWith("description:", StringComparison.Ordinal))
                {
                    description = Unquote(trimmed.Substring(12));
                }
            }

            if (!IsName(name) || description.Length == 0 || description.Length > 300)
            {
                return false;
            }

            body = text.Substring(rest).Trim();
            return true;
        }

        private static string Unquote(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return value.Trim();
        }
    }

    public static class SkillCatalog
    {
        public const int MaxBodyChars = 12000;

        private const string SourceResearch = "---\nname: source-research\ndescription: Сверить свежий факт по коротким источникам и не принимать сниппет за страницу.\n---\nИспользуй web_search. Сниппет — не страница. Адрес не открывай и не дочитывай. В ответе дай ссылки и отдельно напиши, чего в сниппетах нет. Если поиск недоступен, так и скажи.\n";
        private const string FileOrganizing = "---\nname: file-organizing\ndescription: Разобрать файлы проекта и предложить порядок, ничего не перемещая без прямой просьбы.\n---\nСначала list_files и search_text. Опиши группы и предлагаемые имена. Не перемещай, не переименовывай и не удаляй, пока пользователь прямо не попросил. Новый файл создавай через propose_file_change.\n";
        private const string DeckOutline = "---\nname: deck-outline\ndescription: Собрать текстовый план колоды: аудитория, цель и слайды с источниками.\n---\nЕсли аудитория, цель или число слайдов не заданы, коротко уточни их. Не выдумывай факты. Каждый слайд: заголовок, до шести коротких пунктов и источник. Это план, не файл PPTX. Картинку создавай через generate_image только когда пользователь просит файл в проекте.\n";

        public static IList<SkillSummary> List(string root)
        {
            var found = Load(root);
            var list = new List<SkillSummary>();
            foreach (var skill in found)
            {
                list.Add(skill.Summary);
            }

            return list;
        }

        public static string Read(string root, string name)
        {
            if (!SkillText.IsName(name))
            {
                return string.Empty;
            }

            foreach (var skill in Load(root))
            {
                if (!string.Equals(skill.Summary.Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                var text = skill.Body ?? string.Empty;
                if (text.Length > MaxBodyChars)
                {
                    text = text.Substring(0, MaxBodyChars);
                }

                return skill.Summary.Name + "\n" + skill.Summary.Description + "\n\n" + text;
            }

            return string.Empty;
        }

        public static string Prompt(string root)
        {
            var skills = List(root);
            if (skills.Count == 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            text.Append("Навыки. Полный текст читай через read_skill. Навык проекта в .github/skills заменяет встроенный с тем же именем.\n");
            for (var i = 0; i < skills.Count; i++)
            {
                text.Append("- ").Append(skills[i].Name).Append(": ").Append(skills[i].Description).Append('\n');
            }

            return text.ToString();
        }

        private static List<LoadedSkill> Load(string root)
        {
            var map = new Dictionary<string, LoadedSkill>(StringComparer.Ordinal);
            AddBuiltIn(map, SourceResearch);
            AddBuiltIn(map, FileOrganizing);
            AddBuiltIn(map, DeckOutline);
            AddProject(root, map);
            var list = new List<LoadedSkill>();
            foreach (var skill in map.Values)
            {
                list.Add(skill);
            }

            list.Sort((left, right) => string.CompareOrdinal(left.Summary.Name, right.Summary.Name));
            return list;
        }

        private static void AddBuiltIn(Dictionary<string, LoadedSkill> map, string markdown)
        {
            if (!SkillText.TryParse(markdown, out var name, out var description, out var body))
            {
                return;
            }

            map[name] = new LoadedSkill(new SkillSummary(name, description, false), body);
        }

        private static void AddProject(string root, Dictionary<string, LoadedSkill> map)
        {
            if (string.IsNullOrWhiteSpace(root) || !WorkspacePath.TryCombine(root, ".github/skills", out var folder, out _))
            {
                return;
            }

            string[] dirs;
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                dirs = Directory.GetDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }

            for (var i = 0; i < dirs.Length; i++)
            {
                var folderName = Path.GetFileName(dirs[i].TrimEnd('\\'));
                if (!SkillText.IsName(folderName))
                {
                    continue;
                }

                if (!WorkspacePath.TryCombine(root, ".github/skills/" + folderName + "/SKILL.md", out var file, out _) || !File.Exists(file))
                {
                    continue;
                }

                string text;
                try
                {
                    if (new FileInfo(file).Length > 65536)
                    {
                        continue;
                    }

                    text = File.ReadAllText(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }

                if (!SkillText.TryParse(text, out var name, out var description, out var body) || !string.Equals(name, folderName, StringComparison.Ordinal))
                {
                    continue;
                }

                map[name] = new LoadedSkill(new SkillSummary(name, description, true), body);
            }
        }

        private sealed class LoadedSkill
        {
            public LoadedSkill(SkillSummary summary, string body)
            {
                Summary = summary;
                Body = body ?? string.Empty;
            }

            public SkillSummary Summary { get; }
            public string Body { get; }
        }
    }
}
